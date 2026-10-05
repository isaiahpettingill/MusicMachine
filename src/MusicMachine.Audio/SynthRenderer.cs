using System.Globalization;
using MusicMachine.Core;

namespace MusicMachine.Audio;

/// <summary>Deterministic, snapshot-based 48 kHz stereo synthesis. Render has no managed allocation or locks.
/// One renderer is owned by one audio thread. Empty rows sustain; OFF releases; CUT immediately silences.
/// Axy: row-local root/x/y semitone cycle, three equal parts of a row. Vxx: persistent xx/255 gain.
/// Gxx: note-local gate xx/255 of a row. Uxx/Dxx: row-local xx semitones/second (both add).
/// Rxx: row-local xx equal subdivisions (00 disables). T/TT retrigger held notes every beat/3 or beat/6.
/// Swing delays notes on odd absolute rows by Song.Swing * row duration; empty/OFF/CUT are on-grid.
/// TotalFrames includes a bounded envelope-release tail. MusicalFrames excludes that tail.</summary>
public sealed class SynthRenderer
{
    public const int OutputSampleRate = 48000;
    public int SampleRate => OutputSampleRate;
    public long PositionFrames { get; private set; }
    public long TotalFrames { get; }
    public long MusicalFrames { get; }
    public double FramesPerRow { get; }
    public int TotalRows { get; }
    private readonly ScheduledEvent[] _events;
    private readonly Channel[] _channels;
    private readonly double _masterGain;
    private int _eventIndex;
    private bool _ending;

    public SynthRenderer(Song snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Compile everything the callback needs. UI edits to the input song cannot race audio.
        var bpm = Safe(snapshot.Bpm, 120, 20, 400);
        var rowsPerBeat = Math.Clamp(snapshot.RowsPerBeat, 1, 32);
        FramesPerRow = OutputSampleRate * 60.0 / bpm / rowsPerBeat;
        var beatFrames = FramesPerRow * rowsPerBeat;
        _masterGain = Db(Safe(snapshot.MasterVolumeDb, -1, -96, 12));
        var instruments = snapshot.Instruments.ToDictionary(i => i.Id, i => new Sound(i));
        var fallback = new Sound(new Instrument());
        Sound Find(string? id) => id is not null && instruments.TryGetValue(id, out var sound) ? sound : fallback;
        var channels = new List<Channel>();
        var events = new List<ScheduledEvent>();
        bool anySolo = snapshot.Tracks.Any(t => t.Solo);
        foreach (var track in snapshot.Tracks)
            channels.Add(new Channel(Find(track.InstrumentId), snapshot.Seed + (uint)channels.Count * 7919u,
                track.VolumeDb, track.Pan, track.Muted || anySolo && !track.Solo, track.VolumeAutomation));
        // Drum lane identity is per pattern/lane; this prevents unrelated pattern lanes sharing voices.
        var drumChannels = new Dictionary<(string, int), int>();
        var patterns = snapshot.Patterns.ToDictionary(p => p.Id);
        var arrangement = snapshot.Arrangement.Count > 0 ? snapshot.Arrangement : snapshot.Patterns.Take(1).Select(p => new SongSection { PatternId = p.Id }).ToList();
        long plannedRows = 0;
        foreach (var section in arrangement)
            if (patterns.TryGetValue(section.PatternId, out var p)) plannedRows += (long)Math.Clamp(p.Length, 1, 4096) * Math.Clamp(section.Repeats, 1, 256);
        if (plannedRows * Math.Max(1, snapshot.Tracks.Count + snapshot.Patterns.Select(p => p.Drums.Count).DefaultIfEmpty(0).Max()) > 2_000_000)
            throw new ArgumentException("The expanded arrangement exceeds the safe two-million-event playback budget. Shorten the arrangement or use loop playback.");
        long row = 0;
        foreach (var section in arrangement)
        {
            if (!patterns.TryGetValue(section.PatternId, out var pattern)) continue;
            int length = Math.Clamp(pattern.Length, 1, 4096);
            int repeats = Math.Clamp(section.Repeats, 1, 256);
            if (row + (long)length * repeats > 1_000_000) throw new ArgumentException("Arrangement exceeds one million rows.");
            for (int repeat = 0; repeat < repeats; repeat++)
            for (int r = 0; r < length; r++, row++)
            {
                long frame = (long)Math.Round(row * FramesPerRow);
                for (int t = 0; t < snapshot.Tracks.Count; t++)
                {
                    var part = pattern.Tracks.FirstOrDefault(p => p.TrackId == snapshot.Tracks[t].Id);
                    var note = part is not null && r < part.Rows.Count ? part.Rows[r] : null;
                    var effects = Effects.Parse(note?.Effects);
                    long eventFrame = frame;
                    if (note?.Kind == NoteKind.Note && note.Timing == NoteTiming.Swing && (row & 1) != 0)
                        eventFrame += (long)Math.Round(Safe(snapshot.Swing, 0, 0, .75) * FramesPerRow);
                    events.Add(new ScheduledEvent(eventFrame, t, note?.Kind ?? NoteKind.Empty,
                        Math.Clamp((note?.Pitch ?? 60) + section.Transpose, 0, 127), note?.Timing ?? NoteTiming.Straight,
                        string.IsNullOrEmpty(note?.InstrumentId) ? null : Find(note.InstrumentId), effects, 1,
                        FramesPerRow, beatFrames));
                }
                for (int d = 0; d < pattern.Drums.Count; d++)
                {
                    var lane = pattern.Drums[d];
                    if (!drumChannels.TryGetValue((pattern.Id, d), out int index))
                    {
                        index = channels.Count;
                        drumChannels[(pattern.Id, d)] = index;
                        channels.Add(new Channel(Find(lane.InstrumentId), snapshot.Seed + (uint)index * 7919u,
                            lane.VolumeDb, lane.Pan, lane.Muted || anySolo, []));
                    }
                    if (r < lane.Steps.Count && lane.Steps[r] != 0)
                        events.Add(new ScheduledEvent(frame, index, NoteKind.Note, 48, NoteTiming.Straight,
                            Find(lane.InstrumentId), default, lane.Steps[r] / 255.0, FramesPerRow, beatFrames));
                }
            }
        }
        TotalRows = checked((int)row);
        MusicalFrames = (long)Math.Round(row * FramesPerRow);
        var maxRelease = Math.Max(.05, channels.Count == 0 ? 0 : instruments.Values.Select(i => i.Release / OutputSampleRate).DefaultIfEmpty(.1).Max());
        TotalFrames = MusicalFrames + (long)Math.Ceiling(Math.Clamp(maxRelease + .05, .1, 60.1) * OutputSampleRate);
        _channels = channels.ToArray();
        _events = events.OrderBy(e => e.Frame).ToArray();
    }

    /// <returns>Number of frames before the end; any remaining destination is cleared.</returns>
    public int Render(Span<float> stereo)
    {
        if ((stereo.Length & 1) != 0) throw new ArgumentException("Stereo destination must have an even length.", nameof(stereo));
        stereo.Clear();
        int count = (int)Math.Min(stereo.Length / 2L, Math.Max(0, TotalFrames - PositionFrames));
        for (int f = 0; f < count; f++, PositionFrames++)
        {
            while (_eventIndex < _events.Length && _events[_eventIndex].Frame <= PositionFrames)
            {
                ref readonly var e = ref _events[_eventIndex++];
                _channels[e.Channel].Apply(e);
            }
            if (!_ending && PositionFrames >= MusicalFrames)
            {
                _ending = true;
                foreach (var channel in _channels) channel.Voice.Off();
            }
            double left = 0, right = 0;
            foreach (var channel in _channels)
            {
                double value = channel.Next(PositionFrames, PositionFrames / FramesPerRow);
                left += value * channel.Left;
                right += value * channel.Right;
            }
            // Continuous soft saturation is a final safety net; it never wraps, clips or emits NaN.
            stereo[f * 2] = Limit(left * _masterGain);
            stereo[f * 2 + 1] = Limit(right * _masterGain);
        }
        return count;
    }
    /// <summary>Rewind without allocation. Call only from the owning render thread or while audio is stopped.</summary>
    public void Reset()
    {
        PositionFrames = 0; _eventIndex = 0; _ending = false;
        foreach (var channel in _channels) channel.Reset();
    }
    /// <summary>Reconstruct every prior event and oscillator state; seeking is not a realtime operation.</summary>
    public void Seek(long frame)
    {
        frame = Math.Clamp(frame, 0, TotalFrames);
        if (frame < PositionFrames) Reset();
        Span<float> scratch = stackalloc float[2048];
        while (PositionFrames < frame) Render(scratch[..(int)Math.Min(scratch.Length, (frame - PositionFrames) * 2)]);
    }
    internal Action CaptureRestorePoint()
    {
        var saved = _channels.Select(c => c.Clone()).ToArray();
        int eventIndex = _eventIndex; long position = PositionFrames; bool ending = _ending;
        return () =>
        {
            _eventIndex = eventIndex; PositionFrames = position; _ending = ending;
            for (int i = 0; i < _channels.Length; i++) _channels[i].CopyFrom(saved[i]);
        };
    }
    private static float Limit(double x) => double.IsFinite(x) ? (float)Math.Tanh(x) : 0;
    internal static double Safe(double x, double fallback, double min, double max) => double.IsFinite(x) ? Math.Clamp(x, min, max) : fallback;
    internal static double Db(double db) => Math.Pow(10, db / 20);

    private readonly record struct ScheduledEvent(long Frame, int Channel, NoteKind Kind, int Pitch, NoteTiming Timing,
        Sound? Sound, Effects Effects, double Velocity, double RowFrames, double BeatFrames);
    private readonly record struct Effects(bool VolumeSet, double Volume, bool GateSet, double Gate, int ArpX, int ArpY, double Slide, int Retrigger)
    {
        public static Effects Parse(List<string>? values)
        {
            bool vs = false, gs = false; double vol = 1, gate = 1, slide = 0; int x = 0, y = 0, retrig = 0;
            if (values is not null) foreach (string raw in values)
            {
                string? text = raw?.Trim();
                if (text is null || text.Length != 3 || !byte.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)) continue;
                switch (char.ToUpperInvariant(text[0]))
                {
                    case 'V': vs = true; vol = value / 255.0; break;
                    case 'G': gs = true; gate = value / 255.0; break;
                    case 'A': x = value >> 4; y = value & 15; break;
                    case 'U': slide += value; break;
                    case 'D': slide -= value; break;
                    case 'R': retrig = Math.Clamp((int)value, 0, 32); break;
                }
            }
            return new(vs, vol, gs, gate, x, y, slide, retrig);
        }
    }
    private sealed class Channel
    {
        public Voice Voice;
        public readonly double Left, Right;
        private readonly bool _muted;
        private readonly double _baseDb;
        private readonly AutomationPoint[] _automation;
        private int _automationIndex;
        private double _volume = 1, _gain;
        private Effects _effects;
        private long _rowStart;
        private double _rowFrames = 1, _retriggerFrames, _nextRetrigger = double.MaxValue;
        private int _pitch;
        private NoteTiming _timing;
        private double _beatFrames, _velocity;
        private Sound _sound;
        private readonly Sound _initialSound;
        public Channel(Sound sound, uint seed, double db, double pan, bool muted, List<AutomationPoint> automation)
        {
            _sound = _initialSound = sound; Voice = new Voice(seed); _muted = muted; _baseDb = Safe(db, 0, -96, 12);
            pan = Safe(pan, 0, -1, 1);
            Left = Math.Cos((pan + 1) * Math.PI / 4); Right = Math.Sin((pan + 1) * Math.PI / 4);
            _automation = automation.Where(p => double.IsFinite(p.Row) && double.IsFinite(p.Decibels))
                .OrderBy(p => p.Row).Select(p => new AutomationPoint { Row = Math.Max(0, p.Row), Decibels = Math.Clamp(p.Decibels, -96, 12) }).ToArray();
            _gain = Db(_baseDb + Automation(0));
        }
        public Channel Clone()
        {
            var clone = (Channel)MemberwiseClone(); clone.Voice = Voice.Clone(); return clone;
        }
        public void CopyFrom(Channel other)
        {
            Voice.CopyFrom(other.Voice);
            _automationIndex = other._automationIndex; _volume = other._volume; _gain = other._gain;
            _effects = other._effects; _rowStart = other._rowStart; _rowFrames = other._rowFrames;
            _retriggerFrames = other._retriggerFrames; _nextRetrigger = other._nextRetrigger;
            _pitch = other._pitch; _timing = other._timing; _beatFrames = other._beatFrames;
            _velocity = other._velocity; _sound = other._sound;
        }
        public void Reset()
        {
            _sound = _initialSound; Voice.Reset(); _automationIndex = 0; _volume = 1;
            _gain = Db(_baseDb + Automation(0)); _effects = default; _rowStart = 0;
            _rowFrames = 1; _retriggerFrames = 0; _nextRetrigger = double.MaxValue;
            _pitch = 0; _timing = NoteTiming.Straight; _beatFrames = _velocity = 0;
        }
        public void Apply(in ScheduledEvent e)
        {
            _rowStart = e.Frame; _rowFrames = e.RowFrames; _effects = e.Effects;
            if (e.Sound is not null) _sound = e.Sound;
            if (_effects.VolumeSet) _volume = _effects.Volume;
            if (e.Kind == NoteKind.Cut) { Voice.Cut(); _nextRetrigger = double.MaxValue; return; }
            if (e.Kind == NoteKind.Off) { Voice.Off(); _nextRetrigger = double.MaxValue; return; }
            if (e.Kind == NoteKind.Note)
            {
                _pitch = e.Pitch; _timing = e.Timing; _beatFrames = e.BeatFrames; _velocity = e.Velocity;
                Voice.Start(_sound, _pitch, _velocity);
                _retriggerFrames = _timing switch { NoteTiming.TripletEighth => _beatFrames / 3, NoteTiming.TripletSixteenth => _beatFrames / 6, _ => 0 };
                _nextRetrigger = _retriggerFrames > 0 ? e.Frame + _retriggerFrames : double.MaxValue;
                if (_effects.GateSet) Voice.GateFrame = e.Frame + (long)Math.Round(_rowFrames * _effects.Gate);
            }
            if (_effects.GateSet && Voice.Held) Voice.GateFrame = e.Frame + (long)Math.Round(_rowFrames * _effects.Gate);
            if (_effects.Retrigger > 1 && Voice.Held) _nextRetrigger = e.Frame + _rowFrames / _effects.Retrigger;
        }
        public double Next(long frame, double row)
        {
            if (Voice.Held && frame >= Voice.GateFrame) { Voice.Off(); _nextRetrigger = double.MaxValue; }
            if (Voice.Held && frame >= _nextRetrigger)
            {
                long gate = Voice.GateFrame;
                Voice.Start(_sound, _pitch, _velocity); Voice.GateFrame = gate;
                double interval = _effects.Retrigger > 1 ? _rowFrames / _effects.Retrigger : _retriggerFrames;
                _nextRetrigger = interval > 0 ? _nextRetrigger + interval : double.MaxValue;
                if (_effects.Retrigger > 1 && _nextRetrigger >= _rowStart + _rowFrames)
                    _nextRetrigger = _retriggerFrames > 0 ? frame + _retriggerFrames : double.MaxValue;
            }
            int part = (int)((frame - _rowStart) * 3 / _rowFrames) % 3;
            double arp = part == 1 ? _effects.ArpX : part == 2 ? _effects.ArpY : 0;
            double value = Voice.Next(arp, _effects.Slide);
            double target = Db(_baseDb + Automation(row)) * _volume;
            _gain += (target - _gain) / 240; // 5ms control smoothing; no row-boundary gain spikes
            return _muted ? 0 : value * _gain;
        }
        private double Automation(double row)
        {
            if (_automation.Length == 0) return 0;
            while (_automationIndex + 1 < _automation.Length && _automation[_automationIndex + 1].Row <= row) _automationIndex++;
            var a = _automation[_automationIndex];
            if (row <= a.Row || _automationIndex + 1 >= _automation.Length) return a.Decibels;
            var b = _automation[_automationIndex + 1];
            double t = Math.Clamp((row - a.Row) / Math.Max(.000001, b.Row - a.Row), 0, 1);
            return a.Decibels + (b.Decibels - a.Decibels) * t;
        }
    }

    private sealed class Sound
    {
        public readonly Waveform Waveform;
        public readonly DrumKind Drum;
        public readonly double PulseWidth, Detune, Phase, Gain, Attack, Decay, Sustain, Release, PitchEnv, PitchTime;
        public readonly double B0, B1, B2, A1, A2, WavetablePosition;
        public readonly double OscillatorAmplitude, TrianglePeak, SquareWidth, WaveHigh, WaveLow;
        public readonly short[] Custom;
        public readonly short[][] Tables;
        public Sound(Instrument i)
        {
            Waveform = i.Waveform; Drum = i.Drum;
            OscillatorAmplitude = Safe(i.OscillatorAmplitude, 1, 0, 1);
            TrianglePeak = Safe(i.TrianglePeak, .5, .01, .99); SquareWidth = Safe(i.SquareWidth, .5, .01, .99);
            WaveHigh = Safe(i.WaveHigh, 1, -1, 1); WaveLow = Safe(i.WaveLow, -1, -1, 1);
            PulseWidth = Safe(i.PulseWidth, .5, .01, .99); Detune = Safe(i.DetuneCents, 0, -2400, 2400) / 100;
            Phase = Safe(i.Phase, 0, 0, 1); Gain = Db(Safe(i.VolumeDb, -12, -96, 12));
            var a = i.Amplitude ?? new Envelope();
            Attack = Math.Max(48, Safe(a.AttackMs, 5, 0, 60000) * 48);
            Decay = Math.Max(1, Safe(a.DecayMs, 120, 0, 60000) * 48);
            Sustain = Safe(a.Sustain, .55, 0, 1); Release = Math.Max(1, Safe(a.ReleaseMs, 90, 0, 60000) * 48);
            PitchEnv = Safe(i.PitchEnvelopeSemitones, 0, -96, 96); PitchTime = Math.Max(1, Safe(i.PitchEnvelopeMs, 80, 0, 60000) * 48);
            Custom = i.CustomWave is null ? [] : (short[])i.CustomWave.Clone();
            Tables = i.Wavetable?.Where(t => t is not null && t.Length > 0).Select(t => (short[])t.Clone()).ToArray() ?? [];
            WavetablePosition = Safe(i.WavetablePosition, 0, 0, 1);
            double w = 2 * Math.PI * Safe(i.FilterCutoff, 16000, 20, 20000) / OutputSampleRate;
            double q = .707 + Safe(i.FilterResonance, 0, 0, 1) * 8;
            double alpha = Math.Sin(w) / (2 * q), norm = 1 / (1 + alpha);
            B0 = (1 - Math.Cos(w)) * .5 * norm; B1 = 2 * B0; B2 = B0; A1 = -2 * Math.Cos(w) * norm; A2 = (1 - alpha) * norm;
        }
    }
    private sealed class Voice
    {
        public bool Held { get; private set; }
        public long GateFrame = long.MaxValue;
        private Sound? _sound;
        private uint _random;
        private readonly uint _seed;
        private double _phase, _phase2, _level, _releaseLevel, _age, _releaseAge, _slide, _velocity, _basePitch;
        private double _z1, _z2, _last, _crossfade, _noiseLow;
        private int _crossfadeLeft;
        private bool _active;
        public Voice(uint seed) => _seed = _random = seed == 0 ? 1u : seed;
        public void Reset()
        {
            Cut(); _random = _seed; _phase = _phase2 = _releaseLevel = _age = _releaseAge = _slide = _velocity = _basePitch = _crossfade = _noiseLow = 0;
            _sound = null; GateFrame = long.MaxValue;
        }
        public Voice Clone() => (Voice)MemberwiseClone();
        public void CopyFrom(Voice other)
        {
            Held = other.Held; GateFrame = other.GateFrame; _sound = other._sound; _random = other._random;
            _phase = other._phase; _phase2 = other._phase2; _level = other._level; _releaseLevel = other._releaseLevel;
            _age = other._age; _releaseAge = other._releaseAge; _slide = other._slide; _velocity = other._velocity;
            _basePitch = other._basePitch; _z1 = other._z1; _z2 = other._z2; _last = other._last;
            _crossfade = other._crossfade; _noiseLow = other._noiseLow; _crossfadeLeft = other._crossfadeLeft; _active = other._active;
        }
        public void Start(Sound sound, int pitch, double velocity)
        {
            _crossfade = _last; _crossfadeLeft = 64;
            _sound = sound; _basePitch = pitch; _velocity = velocity; _phase = sound.Phase; _phase2 = 0;
            _level = _age = _releaseAge = _slide = 0; Held = _active = true; GateFrame = long.MaxValue;
        }
        public void Off() { if (!Held) return; Held = false; _releaseLevel = _level; _releaseAge = 0; }
        public void Cut() { Held = _active = false; _level = _last = _z1 = _z2 = 0; _crossfadeLeft = 0; }
        public double Next(double arpeggio, double slideRate)
        {
            if (!_active || _sound is null) return 0;
            var s = _sound;
            _slide = Math.Clamp(_slide + slideRate / OutputSampleRate, -120, 120);
            if (Held) _level = _age < s.Attack ? _age / s.Attack : _age < s.Attack + s.Decay ? 1 - (1 - s.Sustain) * (_age - s.Attack) / s.Decay : s.Sustain;
            else { _level = _releaseLevel * Math.Max(0, 1 - _releaseAge++ / s.Release); if (_level <= 0) { _active = false; return _last = 0; } }
            double pitch = _basePitch + s.Detune + arpeggio + _slide + s.PitchEnv * Math.Exp(-_age / s.PitchTime);
            double frequency = Math.Clamp(440 * Math.Pow(2, (pitch - 69) / 12), 5, 20000);
            double step = frequency / OutputSampleRate;
            double raw;
            if (s.Drum != DrumKind.None) raw = Drum(s.Drum, frequency);
            else raw = Oscillator(s, step) * s.OscillatorAmplitude;
            _phase += step; _phase -= Math.Floor(_phase);
            double filtered = s.B0 * raw + _z1;
            _z1 = s.B1 * raw - s.A1 * filtered + _z2; _z2 = s.B2 * raw - s.A2 * filtered;
            if (!double.IsFinite(filtered)) { _z1 = _z2 = 0; filtered = 0; }
            double output = filtered * _level * _velocity * s.Gain;
            if (_crossfadeLeft > 0) { double t = 1 - _crossfadeLeft-- / 64.0; output = _crossfade * (1 - t) + output * t; }
            _age++;
            return _last = output;
        }
        private double Oscillator(Sound s, double dt) => s.Waveform switch
        {
            Waveform.Sine => Math.Sin(2 * Math.PI * _phase),
            Waveform.Triangle => WaveformShape.Triangle(_phase, s.TrianglePeak),
            Waveform.Saw => 2 * _phase - 1 - PolyBlep(_phase, dt),
            Waveform.Square => (s.WaveHigh + s.WaveLow) * .5 + Pulse(s.SquareWidth, dt) * (s.WaveHigh - s.WaveLow) * .5,
            Waveform.Pulse => (s.WaveHigh + s.WaveLow) * .5 + Pulse(s.PulseWidth, dt) * (s.WaveHigh - s.WaveLow) * .5,
            Waveform.Noise => Noise(),
            Waveform.Custom => Wave(s.Custom, _phase),
            Waveform.Wavetable => Table(s, _phase),
            _ => 0
        };
        private double Pulse(double width, double dt) => (_phase < width ? 1 : -1) + PolyBlep(_phase, dt) - PolyBlep((_phase - width + 1) % 1, dt);
        private static double PolyBlep(double t, double dt)
        {
            if (t < dt) { t /= dt; return t + t - t * t - 1; }
            if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
            return 0;
        }
        private static double Wave(short[] wave, double phase)
        {
            if (wave.Length == 0) return 0;
            double x = phase * wave.Length; int a = (int)x % wave.Length; int b = (a + 1) % wave.Length;
            return (wave[a] + (wave[b] - wave[a]) * (x - Math.Floor(x))) / 32768.0;
        }
        private static double Table(Sound s, double phase)
        {
            if (s.Tables.Length == 0) return Wave(s.Custom, phase);
            double position = s.WavetablePosition * (s.Tables.Length - 1); int a = (int)position, b = Math.Min(a + 1, s.Tables.Length - 1);
            return Wave(s.Tables[a], phase) * (1 - (position - a)) + Wave(s.Tables[b], phase) * (position - a);
        }
        private double Noise() { _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5; return _random / 2147483648.0 - 1; }
        private double Drum(DrumKind kind, double frequency)
        {
            double t = _age / OutputSampleRate;
            double noise = Noise(); _noiseLow += .08 * (noise - _noiseLow); double high = noise - _noiseLow;
            double tuning = frequency / 130.8127826502993; // Drum lanes trigger C3; instrument detune/pitch envelope retune the body.
            double f = kind switch { DrumKind.Kick => tuning * (45 + 150 * Math.Exp(-t * 40)), DrumKind.Tom => frequency * .5 + 100 * Math.Exp(-t * 18), _ => 180 * tuning };
            _phase2 += f / OutputSampleRate; _phase2 -= Math.Floor(_phase2);
            double tone = Math.Sin(2 * Math.PI * _phase2);
            double raw = kind switch
            {
                DrumKind.Kick => tone * Math.Exp(-t * 9) + noise * .08 * Math.Exp(-t * 100),
                DrumKind.Snare => .3 * tone * Math.Exp(-t * 18) + .8 * high * Math.Exp(-t * 14),
                DrumKind.ClosedHat => high * Math.Exp(-t * 65),
                DrumKind.OpenHat => high * Math.Exp(-t * 9),
                DrumKind.Tom => tone * Math.Exp(-t * 8),
                DrumKind.Clap => high * (Math.Exp(-t * 22) + (t >= .012 ? .7 * Math.Exp(-(t - .012) * 80) : 0) + (t >= .025 ? .6 * Math.Exp(-(t - .025) * 70) : 0)),
                _ => 0
            };
            if (t > 2) { _active = Held = false; }
            return raw;
        }
    }
}
