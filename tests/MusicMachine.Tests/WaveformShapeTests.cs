using System.Formats.Cbor;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Tests;

public sealed class WaveformShapeTests
{
    private static Instrument Shaped(Waveform shape) => new()
    {
        Id = "shaped", Waveform = shape, OscillatorAmplitude = .37, TrianglePeak = .23, SquareWidth = .67,
        PulseWidth = .17, WaveHigh = .63, WaveLow = -.24, Phase = .13, VolumeDb = -18, IsLocal = true,
        CustomWave = [0, 8192, 0, -4096], Wavetable = [[-2000, 7000, -2000], [0, 8192, 0, -4096]],
        WavetablePosition = .31, Amplitude = new() { AttackMs = 1, DecayMs = 1, Sustain = 1 }
    };
    private static Song Tone(Instrument instrument)
    {
        var song = DemoSong.CreateEmpty(); song.Instruments = [instrument];
        song.Tracks[0].InstrumentId = instrument.Id;
        song.Tracks.RemoveRange(1, song.Tracks.Count - 1);
        song.Patterns[0].Tracks.RemoveAll(t => t.TrackId != song.Tracks[0].Id);
        song.Patterns[0].Drums.Clear();
        song.Patterns[0].Length = 2;
        song.Patterns[0].Tracks[0].Rows = [new() { Kind = NoteKind.Note, Pitch = 69 }, new()];
        return song;
    }

    [Theory]
    [InlineData(Waveform.Sine)] [InlineData(Waveform.Triangle)] [InlineData(Waveform.Saw)] [InlineData(Waveform.Square)]
    [InlineData(Waveform.Pulse)] [InlineData(Waveform.Noise)] [InlineData(Waveform.Custom)] [InlineData(Waveform.Wavetable)]
    public void ShapeAmplitudeSurvivesRoundtripAndMatchesCallbackRendering(Waveform shape)
    {
        var source = Shaped(shape); var song = Tone(source);
        var bytes = InstrumentFile.Write(source); var restored = InstrumentFile.Read(bytes);
        Assert.Equal(bytes, InstrumentFile.Write(restored));
        Assert.Equal(.37, restored.OscillatorAmplitude); Assert.Equal(.23, restored.TrianglePeak);
        Assert.Equal(.67, restored.SquareWidth); Assert.Equal(.63, restored.WaveHigh); Assert.Equal(-.24, restored.WaveLow);
        var expected = OfflineExporter.Render(song);
        var clone = SongFile.Clone(song); Assert.Equal(2, clone.Version); var actual = new float[expected.Length];
        var renderer = new SynthRenderer(clone);
        for (var offset = 0; offset < actual.Length; offset += 254)
            renderer.Render(actual.AsSpan(offset, Math.Min(254, actual.Length - offset)));
        Assert.Equal(expected, actual);
        Assert.All(actual, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1));
        // Before the common tanh output limiter the amplitude multiplier is exactly linear.
        clone.Instruments[0].OscillatorAmplitude *= .25;
        var quieter = OfflineExporter.Render(clone);
        for (var n = 0; n < expected.Length; n += 17)
            Assert.InRange(Math.Abs(Math.Atanh(quieter[n]) - Math.Atanh(expected[n]) * .25), 0, 1e-7);
        clone.Instruments[0].OscillatorAmplitude = 0;
        Assert.All(OfflineExporter.Render(clone), v => Assert.Equal(0, v));
        Assert.Equal(bytes, InstrumentFile.Write(source));
    }

    [Fact]
    public void LegacyFieldsDefaultWithoutReinterpretingDormantSquarePulseWidth()
    {
        var source = new Instrument { Id = "old", Waveform = Waveform.Square, PulseWidth = .17 };
        var reader = new CborReader(InstrumentFile.Write(source)); var writer = new CborWriter();
        Assert.Equal(3, reader.ReadStartMap()); writer.WriteStartMap(3);
        for (var root = 0; root < 3; root++)
        {
            var key = reader.ReadInt32(); writer.WriteInt32(key);
            if (key != 2) { writer.WriteEncodedValue(reader.ReadEncodedValue().Span); continue; }
            var count = reader.ReadStartMap()!.Value; writer.WriteStartMap(19);
            for (var n = 0; n < count; n++)
            {
                var field = reader.ReadInt32(); var value = reader.ReadEncodedValue();
                if (field < 19) { writer.WriteInt32(field); writer.WriteEncodedValue(value.Span); }
            }
            reader.ReadEndMap(); writer.WriteEndMap();
        }
        reader.ReadEndMap(); writer.WriteEndMap();
        var restored = InstrumentFile.Read(writer.Encode());
        Assert.Equal(1, restored.OscillatorAmplitude); Assert.Equal(.5, restored.TrianglePeak);
        Assert.Equal(.5, restored.SquareWidth); Assert.Equal(.17, restored.PulseWidth);
        Assert.Equal(1, restored.WaveHigh); Assert.Equal(-1, restored.WaveLow);
        Assert.Equal(OfflineExporter.Render(Tone(source)), OfflineExporter.Render(Tone(restored)));
    }

    [Fact]
    public void NewShapeFieldsRequireVersionTwoWhileUnchangedLegacyFilesStayVersionOne()
    {
        static int RootVersion(byte[] bytes)
        {
            var r = new CborReader(bytes); r.ReadStartMap();
            while (r.PeekState() != CborReaderState.EndMap)
            {
                var key = r.ReadInt32(); if (key == 1) return r.ReadInt32(); r.SkipValue();
            }
            throw new Exception("Missing version");
        }
        var old = new Instrument { Id = "old" };
        Assert.Equal(1, RootVersion(InstrumentFile.Write(old)));
        Assert.Equal(1, RootVersion(SongFile.Write(Tone(old))));
        var shaped = Shaped(Waveform.Triangle);
        Assert.Equal(2, RootVersion(InstrumentFile.Write(shaped)));
        Assert.Equal(2, RootVersion(SongFile.Write(Tone(shaped))));
        var restored = SongFile.Read(SongFile.Write(Tone(shaped)));
        restored.Instruments = [old]; restored.Tracks[0].InstrumentId = old.Id;
        Assert.Equal(2, RootVersion(SongFile.Write(restored)));
        // Old application's strict version check rejects both v2 roots instead of silently altering sound.
        Assert.NotEqual(1, RootVersion(InstrumentFile.Write(shaped)));
        Assert.NotEqual(1, RootVersion(SongFile.Write(Tone(shaped))));
    }

    [Fact]
    public void ShapeLimitsRejectNonfiniteAndOutOfRangeNumbers()
    {
        (Action<Instrument, double> Set, double Low, double High)[] fields =
        [ ((i, v) => i.OscillatorAmplitude = v, 0, 1), ((i, v) => i.TrianglePeak = v, .01, .99),
          ((i, v) => i.SquareWidth = v, .01, .99), ((i, v) => i.WaveHigh = v, -1, 1), ((i, v) => i.WaveLow = v, -1, 1) ];
        foreach (var (set, low, high) in fields)
        {
            foreach (var value in new[] { low - .001, high + .001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            { var i = Shaped(Waveform.Sine); set(i, value); Assert.Throws<SongFormatException>(() => InstrumentFile.Write(i)); }
            foreach (var value in new[] { low, high }) { var i = Shaped(Waveform.Sine); set(i, value); InstrumentFile.Validate(i); }
        }
    }

    [Fact]
    public void ConversionIsDeterministicPhaseZeroAndDoesNotNormalizeQuietSamples()
    {
        var instrument = Shaped(Waveform.Custom);
        Assert.Equal(instrument.CustomWave, WaveformShape.Cycle(instrument, length: 4));
        var cycle = WaveformShape.Cycle(instrument);
        instrument.Phase = .8; instrument.OscillatorAmplitude = .05;
        Assert.Equal(cycle, WaveformShape.Cycle(instrument));
        Assert.Equal(8192, cycle.Max()); Assert.Equal(-4096, cycle.Min());
        instrument.Waveform = Waveform.Noise;
        Assert.Equal(WaveformShape.Cycle(instrument), WaveformShape.Cycle(instrument));
        instrument.Waveform = Waveform.Triangle; instrument.TrianglePeak = .25;
        cycle = WaveformShape.Cycle(instrument);
        Assert.Equal(-32768, cycle[0]); Assert.Equal(32767, cycle[32]);
        Assert.Equal(0, WaveformShape.Triangle(.125, .25));
        instrument.Waveform = Waveform.Square; instrument.SquareWidth = .25; instrument.WaveHigh = .5; instrument.WaveLow = -.25;
        cycle = WaveformShape.Cycle(instrument);
        Assert.All(cycle.Take(32), v => Assert.Equal(16384, v));
        Assert.All(cycle.Skip(32), v => Assert.Equal(-8192, v));
    }

    [Fact]
    public void LocalCopyPreservesShapeAndDeepCopiesQuietWaveframes()
    {
        var instrument = Shaped(Waveform.Wavetable); instrument.IsLocal = false;
        var local = InstrumentLibrary.CreateLocalCopy(instrument);
        Assert.True(local.IsLocal); Assert.NotEqual(instrument.Id, local.Id);
        Assert.Equal(.37, local.OscillatorAmplitude); Assert.Equal(.23, local.TrianglePeak);
        Assert.Equal(.67, local.SquareWidth); Assert.Equal(.63, local.WaveHigh);
        local.Wavetable[0][0] = 0; local.CustomWave[1] = 0; local.OscillatorAmplitude = .1;
        Assert.Equal(-2000, instrument.Wavetable[0][0]); Assert.Equal(8192, instrument.CustomWave[1]);
        Assert.Equal(.37, instrument.OscillatorAmplitude);
    }
}
