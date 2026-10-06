using MusicMachine.Audio;
using MusicMachine.Core;
namespace MusicMachine.Tests;
public sealed class InstrumentAuditionTests
{
    [Theory]
    [InlineData(Waveform.Sine)] [InlineData(Waveform.Triangle)] [InlineData(Waveform.Saw)]
    [InlineData(Waveform.Square)] [InlineData(Waveform.Pulse)] [InlineData(Waveform.Noise)]
    [InlineData(Waveform.Custom)] [InlineData(Waveform.Wavetable)]
    public void EveryShapeProducesAudibleFiniteSamples(Waveform waveform)
    {
        var instrument = new Instrument { Waveform = waveform, CustomWave = [0, 20000, 0, -20000], Wavetable = [[0, 20000, 0, -20000]] };
        var before = InstrumentFile.Write(instrument);
        var song = InstrumentAudition.CreateSong(instrument);
        var samples = OfflineExporter.Render(song);
        Assert.True(samples.Max(v => Math.Abs(v)) > .05);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));
        Assert.Equal(before, InstrumentFile.Write(instrument));
        Assert.Single(song.Tracks); Assert.Equal(instrument.Id, song.Tracks[0].InstrumentId);
    }
    [Theory]
    [InlineData(Waveform.Sine)] [InlineData(Waveform.Triangle)]
    public void SlowEnvelopeReachesFullAttackBeforeRelease(Waveform waveform)
    {
        var instrument = new Instrument { Waveform = waveform, Amplitude = new() { AttackMs = 2000, DecayMs = 1000, Sustain = .7 } };
        var song = InstrumentAudition.CreateSong(instrument);
        var offRow = song.Patterns[0].Tracks[0].Rows.FindIndex(n => n.Kind == NoteKind.Off);
        Assert.True(offRow * .125 >= 3.25);
        var samples = OfflineExporter.Render(song);
        Assert.True(samples.Skip(48000 * 2 * 2).Take(4800).Max(v => Math.Abs(v)) > .12);
    }
}
