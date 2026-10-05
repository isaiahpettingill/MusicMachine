using System.Buffers.Binary;
using MusicMachine.Audio;
using MusicMachine.Core;
using Xunit;

namespace MusicMachine.Tests;

public sealed class AudioTests
{
    private static Song Tone(int length = 8, Waveform waveform = Waveform.Sine)
    {
        var instrument = new Instrument { Id = "tone", Waveform = waveform, VolumeDb = -9, FilterCutoff = 18000,
            Amplitude = new Envelope { AttackMs = 1, DecayMs = 1, Sustain = 1, ReleaseMs = 40 } };
        var track = new Track { Id = "lead", InstrumentId = instrument.Id };
        var pattern = new Pattern { Id = "a", Length = length };
        pattern.GetTrack(track.Id).Rows[0] = new NoteEvent { Kind = NoteKind.Note, Pitch = 69 };
        return new Song { Bpm = 120, MasterVolumeDb = 0, Instruments = [instrument], Tracks = [track], Patterns = [pattern], Arrangement = [new SongSection { PatternId = pattern.Id }] };
    }
    private static double Rms(float[] pcm, int frame, int frames = 1000)
    {
        double sum = 0; for (int f = frame; f < frame + frames; f++) sum += pcm[f * 2] * pcm[f * 2]; return Math.Sqrt(sum / frames);
    }
    private static int Crossings(float[] pcm, int start, int frames)
    {
        int count = 0; for (int f = start + 1; f < start + frames; f++) if (pcm[(f - 1) * 2] <= 0 && pcm[f * 2] > 0) count++; return count;
    }
    [Fact] public void DeterministicAcrossCallbackSizesAndImmutableSnapshot()
    {
        var song = Tone(waveform: Waveform.Noise); var renderer = new SynthRenderer(song);
        var expected = OfflineExporter.Render(song); song.Instruments[0].Waveform = Waveform.Square;
        var actual = new float[expected.Length];
        for (int p = 0; p < actual.Length; p += 254) renderer.Render(actual.AsSpan(p, Math.Min(254, actual.Length - p)));
        Assert.Equal(expected, actual);
    }
    [Fact] public void EmptyRowsSustainAndOffReleasesWhileCutStopsImmediately()
    {
        var sustain = OfflineExporter.Render(Tone()); Assert.True(Rms(sustain, 20000) > .1);
        var offSong = Tone(); offSong.Patterns[0].Tracks[0].Rows[2].Kind = NoteKind.Off;
        var off = OfflineExporter.Render(offSong); Assert.True(Rms(off, 12000, 100) > .05); Assert.Equal(0, Rms(off, 15000));
        offSong.Patterns[0].Tracks[0].Rows[2].Kind = NoteKind.Cut;
        var cut = OfflineExporter.Render(offSong); Assert.Equal(0, Rms(cut, 12000));
    }
    [Fact] public void A440PitchAndPatternTransposeAreAccurate()
    {
        var song = Tone(); var pcm = OfflineExporter.Render(song);
        Assert.InRange(Crossings(pcm, 12000, 24000), 219, 221);
        song.Arrangement[0].Transpose = 12; pcm = OfflineExporter.Render(song);
        Assert.InRange(Crossings(pcm, 12000, 24000), 439, 441);
    }
    [Theory]
    [InlineData(Waveform.Sine)] [InlineData(Waveform.Triangle)] [InlineData(Waveform.Saw)]
    [InlineData(Waveform.Square)] [InlineData(Waveform.Pulse)] [InlineData(Waveform.Noise)]
    [InlineData(Waveform.Custom)] [InlineData(Waveform.Wavetable)]
    public void EveryWaveformIsFiniteBoundedAndAudible(Waveform waveform)
    {
        var song = Tone(waveform: waveform);
        song.Instruments[0].CustomWave = [0, 20000, 0, -20000];
        song.Instruments[0].Wavetable = [[0, 20000, 0, -20000], [32767, 32767, -32767, -32767]];
        song.Instruments[0].WavetablePosition = .4;
        var pcm = OfflineExporter.Render(song); Assert.All(pcm, f => Assert.True(float.IsFinite(f) && Math.Abs(f) <= 1)); Assert.True(Rms(pcm, 6000) > .01);
    }
    [Theory] [InlineData(DrumKind.Kick)] [InlineData(DrumKind.Snare)] [InlineData(DrumKind.ClosedHat)] [InlineData(DrumKind.OpenHat)] [InlineData(DrumKind.Tom)] [InlineData(DrumKind.Clap)]
    public void DrumVoicesAndVelocityAreDeterministic(DrumKind drum)
    {
        var song = Tone(); song.Tracks.Clear(); song.Instruments[0].Drum = drum;
        song.Patterns[0].Drums = [new DrumLane { InstrumentId = "tone", Steps = [255, 0, 0, 0, 0, 0, 0, 0] }];
        var loud = OfflineExporter.Render(song); Assert.True(Rms(loud, 100, 3000) > .003);
        Assert.Equal(loud, OfflineExporter.Render(song)); song.Patterns[0].Drums[0].Steps[0] = 64;
        var soft = OfflineExporter.Render(song); Assert.True(Rms(soft, 100, 3000) < Rms(loud, 100, 3000) * .4);
    }
    [Fact] public void SimultaneousEffectsGateAndVolumeAreApplied()
    {
        var song = Tone(); song.Patterns[0].Tracks[0].Rows[0].Effects = ["A37", "V80", "G80", "U01", "D01"];
        var pcm = OfflineExporter.Render(song); Assert.True(Rms(pcm, 500) > .02); Assert.Equal(0, Rms(pcm, 7000));
        song.Patterns[0].Tracks[0].Rows[0].Effects = ["V00"]; pcm = OfflineExporter.Render(song); Assert.True(Rms(pcm, 20000) < 1e-10);
    }
    [Fact] public void PersistentVolumeFxOnlyRowAndAutomationMultiply()
    {
        var song = Tone(); song.Patterns[0].Tracks[0].Rows[2].Effects = ["V40"];
        var pcm = OfflineExporter.Render(song); Assert.True(Rms(pcm, 24000) < Rms(pcm, 6000) * .35);
        song.Patterns[0].Tracks[0].Rows[2].Effects.Clear();
        song.Tracks[0].VolumeAutomation = [new() { Row = 0, Decibels = 0 }, new() { Row = 4, Decibels = -24 }];
        pcm = OfflineExporter.Render(song); Assert.True(Rms(pcm, 35000) < Rms(pcm, 2000) * .15);
    }
    [Fact] public void ArpeggioAndSlidesChangePitch()
    {
        var song = Tone(); song.Patterns[0].Tracks[0].Rows[0].Effects = ["A0C"];
        var pcm = OfflineExporter.Render(song);
        Assert.True(Crossings(pcm, 4200, 1500) > Crossings(pcm, 200, 1500));
        song.Patterns[0].Tracks[0].Rows[0].Effects = ["U60"];
        pcm = OfflineExporter.Render(song); Assert.True(Crossings(pcm, 4000, 1500) > Crossings(pcm, 200, 1500));
    }
    [Fact] public void TripletAndRetriggerRestartTheEnvelope()
    {
        var song = Tone(); song.Instruments[0].Amplitude = new Envelope { AttackMs = 1, DecayMs = 10, Sustain = 0, ReleaseMs = 1 };
        var straight = OfflineExporter.Render(song); Assert.Equal(0, Rms(straight, 8000, 500));
        song.Patterns[0].Tracks[0].Rows[0].Timing = NoteTiming.TripletEighth;
        var triplet = OfflineExporter.Render(song); Assert.True(Rms(triplet, 8050, 300) > .01);
        song.Patterns[0].Tracks[0].Rows[0].Timing = NoteTiming.TripletSixteenth;
        var sixteenth = OfflineExporter.Render(song); Assert.True(Rms(sixteenth, 4050, 300) > .01);
        song.Patterns[0].Tracks[0].Rows[0].Timing = NoteTiming.Straight; song.Patterns[0].Tracks[0].Rows[0].Effects = ["R04"];
        var retrigger = OfflineExporter.Render(song); Assert.True(Rms(retrigger, 1550, 300) > .01);
    }
    [Fact] public void SwingDelaysOddRowNotesOnly()
    {
        var song = Tone(); var rows = song.Patterns[0].Tracks[0].Rows;
        rows[0].Kind = NoteKind.Empty; rows[1] = new() { Kind = NoteKind.Note, Pitch = 69, Timing = NoteTiming.Swing }; song.Swing = .5;
        var pcm = OfflineExporter.Render(song); Assert.Equal(0, Rms(pcm, 6000, 2900)); Assert.True(Rms(pcm, 9200) > .1);
    }
    [Fact] public void InstrumentHeadersPersistAcrossPatternsButDoNotRetimbreHeldNote()
    {
        var song = Tone(4); song.Instruments.Add(new Instrument { Id = "silent", VolumeDb = -96 });
        song.Patterns[0].Tracks[0].Rows[1].InstrumentId = "silent";
        var b = new Pattern { Id = "b", Length = 4 }; b.GetTrack("lead").Rows[0] = new() { Kind = NoteKind.Note, Pitch = 69 };
        song.Patterns.Add(b); song.Arrangement.Add(new() { PatternId = "b" });
        var pcm = OfflineExporter.Render(song); Assert.True(Rms(pcm, 18000) > .1); Assert.True(Rms(pcm, 30000) < .001);
    }
    [Fact] public void MuteSoloPanAndRepeatAreRespected()
    {
        var song = Tone(); song.Tracks[0].Muted = true; Assert.All(OfflineExporter.Render(song), value => Assert.Equal(0, value));
        song.Tracks[0].Muted = false; song.Tracks[0].Pan = -1;
        var pcm = OfflineExporter.Render(song); Assert.True(pcm.Where((_, i) => (i & 1) == 1).All(v => Math.Abs(v) < 1e-10));
        song.Tracks.Add(new Track { Id = "solo-silent", InstrumentId = "tone", Solo = true });
        Assert.All(OfflineExporter.Render(song), value => Assert.Equal(0, value));
        song.Arrangement[0].Repeats = 3; Assert.Equal(144000, new SynthRenderer(song).MusicalFrames);
    }
    [Fact] public void SeekAndResetReconstructIdenticalNoiseAndEffectState()
    {
        var song = Tone(waveform: Waveform.Noise); song.Patterns[0].Tracks[0].Rows[0].Effects = ["A37", "U03", "V50"];
        var full = OfflineExporter.Render(song); var renderer = new SynthRenderer(song); renderer.Seek(9000);
        var part = new float[6000]; renderer.Render(part); Assert.Equal(full.AsSpan(18000, 6000).ToArray(), part);
        renderer.Reset(); var reset = new float[full.Length]; renderer.Render(reset); Assert.Equal(full, reset);
    }
    [Fact] public void RenderDoesNotAllocateAfterConstruction()
    {
        var renderer = new SynthRenderer(Tone(64)); var buffer = new float[512];
        renderer.Render(buffer); renderer.Render(buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) renderer.Render(buffer);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    [Fact] public void HostileFiniteControlsAndNaNsCannotEscapeAsInvalidPcm()
    {
        var song = Tone(waveform: Waveform.Saw); song.MasterVolumeDb = double.NaN; song.Instruments[0].FilterCutoff = double.PositiveInfinity;
        song.Instruments[0].FilterResonance = 1000; song.Instruments[0].VolumeDb = 2000; song.Tracks[0].Pan = double.NaN;
        var pcm = OfflineExporter.Render(song); Assert.All(pcm, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1));
    }
    [Fact] public void WavHeaderAndDurationMatchExactPcmAndOptionalTail()
    {
        string path = Path.GetTempFileName();
        try
        {
            var song = Tone(); var pcm = OfflineExporter.Render(song); OfflineExporter.WriteWav(path, song);
            var bytes = File.ReadAllBytes(path); Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24))); Assert.Equal(pcm.Length * 2 + 44, bytes.Length);
            Assert.True(OfflineExporter.Render(song, includeTail: true).Length > pcm.Length);
        }
        finally { File.Delete(path); }
    }
    [Theory] [InlineData(1)] [InlineData(19)] [InlineData(20)] [InlineData(21)] [InlineData(5119)] [InlineData(5120)] [InlineData(5121)] [InlineData(13007)]
    public void QoaDecodesEverySampleAcrossPartialSliceAndFrameBoundaries(int frames)
    {
        var input = new short[frames * 2]; for (int i = 0; i < frames; i++) { input[i * 2] = (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 22000); input[i * 2 + 1] = (short)(Math.Sin(i * 2 * Math.PI * 660 / 48000) * 18000); }
        var bytes = QoaCodec.Encode(input); Assert.Equal("qoaf", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(frames, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        var output = QoaCodec.Decode(bytes, out int rate, out int channels); Assert.Equal(48000, rate); Assert.Equal(2, channels); Assert.Equal(input.Length, output.Length);
        if (frames > 1000)
        {
            double signal = 0, error = 0; for (int i = 0; i < input.Length; i++) { signal += (double)input[i] * input[i]; double delta = input[i] - output[i]; error += delta * delta; }
            Assert.True(10 * Math.Log10(signal / error) > 35, $"SNR was {10 * Math.Log10(signal / error):F2} dB");
        }
        Assert.Throws<InvalidDataException>(() => QoaCodec.Decode(bytes.AsSpan(0, bytes.Length - 1), out _, out _));
    }
    [Fact] public void CancellationPreventsExportAndMalformedQoaIsRejected()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => OfflineExporter.Render(Tone(), cancellation.Token));
        Assert.Throws<InvalidDataException>(() => QoaCodec.Decode(new byte[20], out _, out _));
    }
    [Fact] public void CancelledExportsPreserveExistingDestination()
    {
        string path = Path.GetTempFileName(); File.WriteAllText(path, "existing-file");
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => OfflineExporter.WriteWav(path, new float[200], cancellationToken: cancellation.Token));
            Assert.Equal("existing-file", File.ReadAllText(path));
            Assert.Throws<OperationCanceledException>(() => OfflineExporter.WriteQoa(path, new float[200], cancellationToken: cancellation.Token));
            Assert.Equal("existing-file", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task MissingFfmpegFailsWithoutTouchingDestination()
    {
        string path = Path.GetTempFileName(); File.WriteAllText(path, "preserve");
        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => OfflineExporter.WriteFfmpegAsync("/definitely-missing/ffmpeg", path, Tone()));
            Assert.Equal("preserve", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void QoaOfficialReferenceGoldenFileDecodesIdentically()
    {
        // Generated by phoboslab/qoa qoa_write: 41 stereo frames, channel 0=i*500-10000, channel 1=-channel 0.
        const string reference = "cW9hZgAAACkCALuAACkAWAAAAAAAAAAAAAAAAOAAQAAAAAAAAAAAAAAAAADgAEAA34sEkENEggjNyCCCCASAQTXKAhgQSKIAT4EBokgEgggYAAAAAAAAAEIAAAAAAAAA";
        var pcm = QoaCodec.Decode(Convert.FromBase64String(reference), out int rate, out int channels);
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
        Assert.Equal(48000, rate); Assert.Equal(2, channels); Assert.Equal(82, pcm.Length);
        Assert.Equal("0c2df9e261b0158b1c3a1e3ace26cc9dfe8af499197d3f8ba25c7592c246fcb4", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

}
