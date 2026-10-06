namespace MusicMachine.Core;

public sealed class SongFormatException : IOException
{
    public SongFormatException(string message) : base(message) { }
    public SongFormatException(string message, Exception innerException) : base(message, innerException) { }
}

public static class SongLimits
{
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxTracks = 64;
    public const int MaxInstruments = 256;
    public const int MaxPatterns = 256;
    public const int MaxRows = 1024;
    public const int MaxSections = 1024;
    public const int MaxDrumLanes = 32;
    public const int MaxAutomationPoints = 8192;
    public const int MaxWaveSamples = 4096;
    public const int MaxWaveFrames = 64;
    public const int MaxTotalRows = 262144;
    public const int MaxExpandedRows = 1_000_000;
}

internal static class SongValidation
{
    public static void Validate(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        Require(song.Version is 1 or 2, $"Unsupported song version {song.Version}");
        Text(song.Title, 256, "Song title"); Text(song.Author, 256, "Author");
        Range(song.Bpm, 20, 400, "Tempo"); Range(song.RowsPerBeat, 1, 16, "Rows per beat");
        Require((song.RowsPerBeat & (song.RowsPerBeat - 1)) == 0, "Rows per beat must be 1, 2, 4, 8 or 16");
        Range(song.BeatsPerBar, 1, 16, "Beats per bar"); Range(song.Swing, 0, .75, "Swing");
        Require(song.BeatUnit is 2 or 4 or 8 or 16, "Beat unit must be 2, 4, 8 or 16");
        Range(song.SampleRate, 8000, 192000, "Sample rate"); Range(song.MasterVolumeDb, -96, 12, "Master volume");
        Count(song.Instruments, SongLimits.MaxInstruments, "Instruments");
        Count(song.Tracks, SongLimits.MaxTracks, "Tracks"); Count(song.Patterns, SongLimits.MaxPatterns, "Patterns");
        Count(song.Arrangement, SongLimits.MaxSections, "Arrangement");
        Require(song.Patterns.Count > 0 && song.Arrangement.Count > 0, "A song needs a pattern and an arrangement section");
        var instruments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instrument in song.Instruments) { Validate(instrument); Require(instruments.Add(instrument.Id), "Duplicate instrument ID"); }
        var tracks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in song.Tracks)
        {
            Require(track is not null, "Missing track");
            Id(track!.Id); Require(tracks.Add(track.Id), "Duplicate track ID"); Text(track.Name, 128, "Track name");
            Require(track.InstrumentId == "" || instruments.Contains(track.InstrumentId), $"Unknown track instrument {track.InstrumentId}");
            Range(track.VolumeDb, -96, 12, "Track volume"); Range(track.Pan, -1, 1, "Track pan");
            Text(track.Color, 16, "Track color");
            Require(track.Color.Length == 7 && track.Color[0] == '#' && track.Color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0, "Track color must be #RRGGBB");
            Count(track.VolumeAutomation, SongLimits.MaxAutomationPoints, "Automation");
            double previous = -1;
            foreach (var point in track.VolumeAutomation)
            {
                Require(point is not null, "Missing automation point");
                Range(point!.Row, 0, SongLimits.MaxExpandedRows, "Automation row"); Range(point.Decibels, -96, 12, "Automation volume");
                Require(point.Row > previous, "Automation rows must be unique and ascending"); previous = point.Row;
            }
        }
        var patterns = new HashSet<string>(StringComparer.Ordinal);
        long totalRows = 0;
        foreach (var pattern in song.Patterns)
        {
            Require(pattern is not null, "Missing pattern"); Id(pattern!.Id); Require(patterns.Add(pattern.Id), "Duplicate pattern ID");
            Text(pattern.Name, 128, "Pattern name"); Range(pattern.Length, 1, SongLimits.MaxRows, "Pattern length");
            Count(pattern.Tracks, SongLimits.MaxTracks, "Pattern tracks"); Count(pattern.Drums, SongLimits.MaxDrumLanes, "Drum lanes");
            var patternTracks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in pattern.Tracks)
            {
                Require(track is not null && tracks.Contains(track.TrackId), "Unknown pattern track");
                Require(patternTracks.Add(track!.TrackId), "Duplicate pattern track"); Count(track.Rows, SongLimits.MaxRows, "Rows");
                Require(track.Rows.Count == pattern.Length, "Pattern tracks must contain exactly the pattern's row count");
                totalRows += track.Rows.Count;
                foreach (var note in track.Rows)
                {
                    Require(note is not null, "Missing note event");
                    Range((int)note!.Kind, 0, 3, "Note kind"); Range((int)note.Timing, 0, 3, "Note timing"); Range(note.Pitch, 0, 127, "MIDI pitch");
                    Require(note.Kind == NoteKind.Note || note.Timing == NoteTiming.Straight, "Timing modifiers require a pitched note");
                    if (!string.IsNullOrEmpty(note.InstrumentId)) Require(instruments.Contains(note.InstrumentId), "Unknown note instrument");
                    Count(note.Effects, FxParser.MaxColumns, "FX columns"); FxParser.Validate(note.Effects);
                }
            }
            foreach (var drum in pattern.Drums)
            {
                Require(drum is not null, "Missing drum lane"); Text(drum!.Name, 128, "Drum lane name");
                Require(instruments.Contains(drum.InstrumentId), "Unknown drum instrument");
                Range(drum.VolumeDb, -96, 12, "Drum volume"); Range(drum.Pan, -1, 1, "Drum pan");
                Count(drum.Steps, SongLimits.MaxRows, "Drum steps"); Require(drum.Steps.Count == pattern.Length, "Drum steps must match pattern length");
                totalRows += drum.Steps.Count;
            }
        }
        Require(totalRows <= SongLimits.MaxTotalRows, "Song has too many note rows and drum steps");
        long expandedRows = 0;
        foreach (var section in song.Arrangement)
        {
            Require(section is not null && patterns.Contains(section.PatternId), "Unknown arrangement pattern");
            Range(section!.Repeats, 1, 128, "Pattern repeats"); Range(section.Transpose, -48, 48, "Section transpose");
            expandedRows += (long)song.FindPattern(section.PatternId)!.Length * section.Repeats;
        }
        Require(expandedRows <= SongLimits.MaxExpandedRows, "Arrangement exceeds the playback row limit");
        Require(song.LoopStartSection >= 0 && song.LoopStartSection < song.LoopEndSection && song.LoopEndSection <= song.Arrangement.Count, "Invalid loop section range");
    }

    public static void Validate(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Id(instrument.Id); Text(instrument.Name, 128, "Instrument name");
        Range((int)instrument.Waveform, 0, 7, "Waveform"); Range((int)instrument.Drum, 0, 6, "Drum kind");
        Range(instrument.PulseWidth, .01, .99, "Pulse width"); Range(instrument.DetuneCents, -1200, 1200, "Detune"); Range(instrument.Phase, 0, 1, "Phase");
        Range(instrument.VolumeDb, -96, 12, "Instrument volume");
        Range(instrument.OscillatorAmplitude, 0, 1, "Oscillator amplitude");
        Range(instrument.TrianglePeak, .01, .99, "Triangle peak position");
        Range(instrument.SquareWidth, .01, .99, "Square width");
        Range(instrument.WaveHigh, -1, 1, "Wave high level"); Range(instrument.WaveLow, -1, 1, "Wave low level");
        Require(instrument.Amplitude is not null, "Missing amplitude envelope");
        Range(instrument.Amplitude!.AttackMs, 0, 60000, "Attack"); Range(instrument.Amplitude.DecayMs, 0, 60000, "Decay");
        Range(instrument.Amplitude.Sustain, 0, 1, "Sustain"); Range(instrument.Amplitude.ReleaseMs, 0, 60000, "Release");
        Range(instrument.PitchEnvelopeSemitones, -96, 96, "Pitch envelope"); Range(instrument.PitchEnvelopeMs, 0, 60000, "Pitch envelope time");
        Range(instrument.FilterCutoff, 20, 96000, "Filter cutoff"); Range(instrument.FilterResonance, 0, .99, "Filter resonance");
        Range(instrument.WavetablePosition, 0, 1, "Wavetable position"); Count(instrument.CustomWave, SongLimits.MaxWaveSamples, "Custom wave samples");
        Count(instrument.Wavetable, SongLimits.MaxWaveFrames, "Wavetable frames");
        foreach (var frame in instrument.Wavetable) { Count(frame, SongLimits.MaxWaveSamples, "Wavetable samples"); Require(frame.Length >= 2, "A wavetable frame needs at least two samples"); }
        Require(instrument.Waveform != Waveform.Custom || instrument.CustomWave.Length >= 2, "Custom waveform needs at least two samples");
        Require(instrument.Waveform != Waveform.Wavetable || instrument.Wavetable.Count >= 1, "Wavetable needs at least one frame");
    }
    internal static void Require(bool condition, string message) { if (!condition) throw new SongFormatException(message); }
    private static void Id(string id) { Text(id, 128, "ID"); Require(!string.IsNullOrWhiteSpace(id), "IDs cannot be empty"); }
    private static void Text(string text, int max, string name) => Require(text is not null && text.Length <= max, $"{name} is missing or too long (max {max})");
    private static void Count<T>(IReadOnlyCollection<T> items, int max, string name) => Require(items is not null && items.Count <= max, $"{name} is missing or has too many entries (max {max})");
    private static void Range(double number, double min, double max, string name) => Require(double.IsFinite(number) && number >= min && number <= max, $"{name} must be between {min} and {max}");
}
