namespace MusicMachine.Core;

// File enums have explicit, stable values. Notes use MIDI numbering (C4 = 60).
public enum Waveform { Sine = 0, Triangle = 1, Saw = 2, Square = 3, Pulse = 4, Noise = 5, Custom = 6, Wavetable = 7 }
public enum DrumKind { None = 0, Kick = 1, Snare = 2, ClosedHat = 3, OpenHat = 4, Tom = 5, Clap = 6 }
public enum NoteTiming { Straight = 0, TripletEighth = 1, TripletSixteenth = 2, Swing = 3 }
public enum NoteKind { Empty = 0, Note = 1, Off = 2, Cut = 3 }
public sealed class Envelope
{
    public double AttackMs { get; set; } = 5;
    public double DecayMs { get; set; } = 120;
    public double Sustain { get; set; } = .55;
    public double ReleaseMs { get; set; } = 90;
}
public sealed class Instrument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New instrument";
    public Waveform Waveform { get; set; } = Waveform.Pulse;
    public DrumKind Drum { get; set; }
    public double PulseWidth { get; set; } = .5;
    // Linear oscillator height, before filtering, the ADSR envelope and output dB.
    public double OscillatorAmplitude { get; set; } = 1;
    public double TrianglePeak { get; set; } = .5;
    // Separate from PulseWidth so old square presets with a dormant pulse width keep their sound.
    public double SquareWidth { get; set; } = .5;
    public double WaveHigh { get; set; } = 1;
    public double WaveLow { get; set; } = -1;
    public double DetuneCents { get; set; }
    public double Phase { get; set; }
    public double VolumeDb { get; set; } = -12;
    public Envelope Amplitude { get; set; } = new();
    public double PitchEnvelopeSemitones { get; set; }
    public double PitchEnvelopeMs { get; set; } = 80;
    public double FilterCutoff { get; set; } = 16000;
    public double FilterResonance { get; set; }
    public short[] CustomWave { get; set; } = [];
    public List<short[]> Wavetable { get; set; } = [];
    public double WavetablePosition { get; set; }
    public bool IsLocal { get; set; }
}
public sealed class NoteEvent
{
    public NoteKind Kind { get; set; }
    public int Pitch { get; set; } = 60;
    public NoteTiming Timing { get; set; }
    // Empty means the current instrument persists; a nonempty ID is a section header.
    public string? InstrumentId { get; set; }
    public List<string> Effects { get; set; } = [];
}
public sealed class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Track";
    public string InstrumentId { get; set; } = "";
    public double VolumeDb { get; set; }
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public string Color { get; set; } = "#97B8FA";
    public List<AutomationPoint> VolumeAutomation { get; set; } = [];
}
public sealed class AutomationPoint
{
    public double Row { get; set; }
    public double Decibels { get; set; }
}
public sealed class PatternTrack
{
    public string TrackId { get; set; } = "";
    public List<NoteEvent> Rows { get; set; } = [];
}
public sealed class DrumLane
{
    public string Name { get; set; } = "Kick";
    public string InstrumentId { get; set; } = "";
    public double VolumeDb { get; set; }
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public List<byte> Steps { get; set; } = [];
}
public sealed class Pattern
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Pattern 01";
    public int Length { get; set; } = 32;
    public List<PatternTrack> Tracks { get; set; } = [];
    public List<DrumLane> Drums { get; set; } = [];
    public PatternTrack GetTrack(string id)
    {
        var track = Tracks.FirstOrDefault(t => t.TrackId == id);
        if (track is null) { track = new() { TrackId = id }; Tracks.Add(track); }
        while (track.Rows.Count < Length) track.Rows.Add(new());
        return track;
    }
}
public sealed class SongSection
{
    public string PatternId { get; set; } = "";
    public int Repeats { get; set; } = 1;
    public int Transpose { get; set; }
}
public sealed class Song
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "Untitled song";
    public string Author { get; set; } = "";
    public double Bpm { get; set; } = 120;
    public int RowsPerBeat { get; set; } = 4;
    public int BeatsPerBar { get; set; } = 4;
    // Meter denominator. Tempo and row duration remain defined in quarter notes.
    public int BeatUnit { get; set; } = 4;
    public double Swing { get; set; }
    public int SampleRate { get; set; } = 48000;
    public uint Seed { get; set; } = 0x4D555349;
    public double MasterVolumeDb { get; set; } = -1;
    public int LoopStartSection { get; set; }
    public int LoopEndSection { get; set; } = 1; // exclusive
    public List<Instrument> Instruments { get; set; } = [];
    public List<Track> Tracks { get; set; } = [];
    public List<Pattern> Patterns { get; set; } = [];
    public List<SongSection> Arrangement { get; set; } = [];
    public Instrument? FindInstrument(string id) => Instruments.FirstOrDefault(i => i.Id == id);
    public Pattern? FindPattern(string id) => Patterns.FirstOrDefault(p => p.Id == id);
}
