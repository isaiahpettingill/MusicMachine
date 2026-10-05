using System.Buffers.Binary;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Tests;

public class SamplingDspTests
{
    [Theory]
    [InlineData(55, 48000)] [InlineData(110, 44100)] [InlineData(220, 22050)]
    [InlineData(440, 48000)] [InlineData(880, 96000)] [InlineData(1760, 192000)]
    public void DetectsKnownPitchesAcrossRates(double frequency, int rate)
    {
        var clip = Tone(frequency, rate, .4);
        var original = (float[])clip.Samples.Clone();
        var result = SampleAnalysis.Detect(clip, 0, clip.Samples.Length);
        Assert.True(result.HasPitch, result.Message);
        Assert.InRange(result.Frequency / frequency, .99, 1.01);
        Assert.InRange(result.Confidence, .95, 1);
        Assert.InRange(result.Start, 0, clip.Samples.Length - 1 - (int)Math.Ceiling(result.Period));
        Assert.Equal(original, clip.Samples);
    }

    [Fact]
    public void DetectsHarmonicFundamentalAndFindsSustainAfterSilence()
    {
        const int rate = 48000;
        var samples = new float[rate];
        for (int i = rate / 2; i < samples.Length; i++)
        {
            double phase = 2 * Math.PI * 220 * i / rate;
            samples[i] = (float)(.3 * Math.Sin(phase) + .5 * Math.Sin(phase * 2) + .1 * Math.Sin(phase * 3) + .05);
        }
        var clip = new SampleClip(samples, rate, 1, "harmonics.wav");
        var result = SampleAnalysis.Detect(clip, 0, samples.Length);
        Assert.True(result.HasPitch, result.Message);
        Assert.InRange(result.Frequency, 218, 222);
        Assert.True(result.Start >= rate / 2);
    }

    [Fact]
    public void SelectionBoundsExcludeOtherPitch()
    {
        var low = Tone(220, 48000, .4); var high = Tone(660, 48000, .4);
        var clip = new SampleClip([.. low.Samples, .. high.Samples], 48000, 1, "two tones");
        var result = SampleAnalysis.Detect(clip, low.Samples.Length, high.Samples.Length);
        Assert.InRange(result.Frequency, 655, 665);
        Assert.InRange(result.Start, low.Samples.Length, clip.Samples.Length - 1 - (int)Math.Ceiling(result.Period));
    }

    [Fact]
    public void BoundedEnergyScoutFindsBriefToneBetweenWidelySpacedWindows()
    {
        const int rate = 48000;
        var samples = new float[rate * 30];
        for (int i = rate / 2; i < rate; i++) samples[i] = (float)(.6 * Math.Sin(i * 2 * Math.PI * 440 / rate));
        var clip = new SampleClip(samples, rate, 1, "brief note in silence");
        var result = SampleAnalysis.Detect(clip, 0, samples.Length);
        Assert.True(result.HasPitch, result.Message); Assert.InRange(result.Frequency, 435, 445);
        Assert.InRange(result.Start, rate / 2, rate - 1 - (int)Math.Ceiling(result.Period));
    }

    [Fact]
    public void SilenceConstantDcNoiseAndTooShortRegionsHaveNoConfidentPitch()
    {
        foreach (float level in new[] { 0f, .5f })
        {
            var silence = new SampleClip(Enumerable.Repeat(level, 48000).ToArray(), 48000, 1, "silent");
            var result = SampleAnalysis.Detect(silence, 0, silence.Samples.Length);
            Assert.False(result.HasPitch); Assert.Equal(0, result.Period); Assert.Equal(0, result.Confidence);
            Assert.Contains("silent", result.Message);
        }
        var random = new Random(81729);
        var noise = new SampleClip(Enumerable.Range(0, 48000).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray(), 48000, 1, "noise");
        var detected = SampleAnalysis.Detect(noise, 0, noise.Samples.Length);
        Assert.False(detected.HasPitch); Assert.InRange(detected.Confidence, 0, .4);
        Assert.Equal(0, detected.Frequency);
        Assert.Contains("short", SampleAnalysis.Detect(noise, 0, 20).Message);
    }

    [Fact]
    public void ExtractionRemovesDcKeepsFiniteHeadroomAndPeriodicSeam()
    {
        var clip = Tone(375, 48000, .1, dc: .3);
        var original = (float[])clip.Samples.Clone();
        var wave = SampleAnalysis.Extract(clip, 35, 128);
        Assert.Equal(128, wave.Length);
        Assert.InRange(Math.Abs(wave.Average(v => (double)v)), 0, 1);
        Assert.InRange(wave.Max(v => Math.Abs((int)v)), 31000, 31128);
        Assert.InRange(Math.Abs(wave[^1] - wave[0]), 0, 1700);
        Assert.Equal(original, clip.Samples);
        var dc = new SampleClip(Enumerable.Repeat(.2f, 256).ToArray(), 48000, 1, "dc");
        Assert.All(SampleAnalysis.Extract(dc, 0, 128), value => Assert.Equal(0, value));
    }

    [Fact]
    public void EndpointCorrectionReducesBadlyAlignedSourceSeam()
    {
        var clip = Tone(375, 48000, .1);
        var wave = SampleAnalysis.Extract(clip, 0, 100);
        double uncorrected = Math.Abs(clip.Samples[99] - clip.Samples[0]);
        double corrected = Math.Abs(wave[^1] - wave[0]) / 32768d;
        Assert.True(corrected < uncorrected / 3, $"Seam: {corrected} versus {uncorrected}");
    }

    [Fact]
    public void ShapesAreDeterministicNonDestructiveAndReturnToOriginal()
    {
        short[] original = Enumerable.Range(0, 128).Select(i => (short)(i < 64 ? 28000 : -28000)).ToArray();
        var saved = (short[])original.Clone();
        var smooth = SampleAnalysis.Shape(original, 1, 0, 0);
        Assert.True(DifferenceEnergy(smooth) < DifferenceEnergy(original) * .25);
        var changed = SampleAnalysis.Shape(original, .35, .7, .2);
        Assert.Equal(changed, SampleAnalysis.Shape(original, .35, .7, .2));
        Assert.Equal(saved, original);
        var restored = SampleAnalysis.Shape(original, 0, 0, 0);
        Assert.Equal(original, restored); Assert.NotSame(original, restored);
        Assert.InRange(Math.Abs(changed.Average(v => (double)v)), 0, 1);
        Assert.All(changed, value => Assert.InRange(value, (short)-31128, (short)31128));
        var sine = SampleAnalysis.Shape(original, 0, 0, 1);
        Assert.True(DifferenceEnergy(sine) < DifferenceEnergy(original) * .1);
    }

    [Fact]
    public void GeneratedWaveIsSelfContainedInstrumentCbor()
    {
        var clip = Tone(440, 48000, .2);
        var detection = SampleAnalysis.Detect(clip, 0, clip.Samples.Length);
        var wave = SampleAnalysis.Shape(SampleAnalysis.Extract(clip, detection.Start, detection.Period), .2, .3, .1);
        var instrument = new Instrument { Name = "Extracted tone", Waveform = Waveform.Custom, CustomWave = wave, IsLocal = true };
        var bytes = InstrumentFile.Write(instrument);
        var restored = InstrumentFile.Read(bytes);
        Assert.Equal(wave, restored.CustomWave); Assert.Equal(bytes, InstrumentFile.Write(restored));
        instrument.Waveform = Waveform.Wavetable; instrument.Wavetable.Add(wave);
        restored = InstrumentFile.Read(InstrumentFile.Write(instrument));
        Assert.Equal(wave, restored.Wavetable[0]);
    }

    [Theory]
    [InlineData(1, 8)] [InlineData(1, 16)] [InlineData(1, 24)] [InlineData(1, 32)]
    [InlineData(3, 32)] [InlineData(3, 64)]
    public async Task ImportsPcmAndFloatWavAndAveragesChannels(int format, int bits)
    {
        byte[] data = Wav(format, bits, 2, 48000, [-1, .5, .5, .25, 0, 0]);
        using var stream = new MemoryStream(data);
        var clip = await SampleImporter.ReadAsync(stream, "stereo.wav");
        Assert.Equal(48000, clip.SampleRate); Assert.Equal(2, clip.OriginalChannels); Assert.Equal("stereo.wav", clip.Name);
        Assert.Equal(3, clip.Samples.Length); Assert.True(stream.CanRead);
        Assert.InRange(clip.Samples[0], -.255f, -.245f); Assert.InRange(clip.Samples[1], .37f, .38f);
        Assert.Equal(0, clip.Samples[2]);
    }

    [Fact]
    public async Task ImportsExtensiblePcmWithOddUnknownChunkAndDataBeforeFormat()
    {
        byte[] source = Wav(1, 24, 1, 48000, [-.5, 0, .5]);
        var fmt = source[20..36]; var pcm = source[44..53];
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVE"u8);
        writer.Write("JUNK"u8); writer.Write(1); writer.Write((byte)91); writer.Write((byte)0);
        writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm); writer.Write((byte)0);
        writer.Write("fmt "u8); writer.Write(40); fmt[0] = 0xfe; fmt[1] = 0xff; writer.Write(fmt);
        writer.Write((ushort)22); writer.Write((ushort)24); writer.Write(0u);
        writer.Write(new byte[] { 1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71 });
        var bytes = memory.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        var clip = await SampleImporter.ReadAsync(new MemoryStream(bytes), "extensible.wav");
        Assert.Equal(new[] { -.5f, 0, .5f }, clip.Samples);
    }

    [Fact]
    public async Task ImportSupportsNonSeekableStreamsCurrentPositionAndCallerOwnership()
    {
        byte[] wav = Wav(1, 16, 1, 48000, [.1, .2, .3]);
        using var stream = new FragmentedStream(wav);
        var clip = await SampleImporter.ReadAsync(stream, "fragmented.wav");
        Assert.Equal(3, clip.Samples.Length); Assert.False(stream.WasDisposed);
        using var positioned = new MemoryStream([1, 2, 3, .. wav]); positioned.Position = 3;
        Assert.Equal(clip.Samples, (await SampleImporter.ReadAsync(positioned, "positioned.wav")).Samples);
    }

    [Fact]
    public async Task NativeFormatIdentificationUsesBytesNotExtension()
    {
        var wav = Wav(1, 16, 1, 8000, [.1, -.1]);
        Assert.Equal(2, (await SampleImporter.ReadAsync(new MemoryStream(wav), "audio.bin")).Samples.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream("not audio"u8.ToArray()), "fake.wav"));
    }

    [Theory] [InlineData(0)] [InlineData(3)] [InlineData(11)] [InlineData(19)] [InlineData(35)] [InlineData(45)]
    public async Task RejectsTruncatedWav(int length)
    {
        byte[] bytes = Wav(1, 16, 1, 48000, [0, .25, -.25]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(bytes[..length]), "bad.wav"));
    }

    [Fact]
    public async Task RejectsMalformedWavMetadataNonfiniteFramesAndTrailingData()
    {
        byte[] original = Wav(1, 16, 1, 48000, [0, .25]);
        foreach (Action<byte[]> damage in new Action<byte[]>[]
        {
            b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), uint.MaxValue),
            b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), uint.MaxValue),
            b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 2),
            b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 0),
            b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 9),
            b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 192001),
            b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 12),
            b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32), 0),
            b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 12)
        })
        {
            var bytes = (byte[])original.Clone(); damage(bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(bytes), "bad.wav"));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream([.. original, 0]), "trailing.wav"));
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(Wav(3, 64, 1, 48000, [value])), "nonfinite.wav"));
    }

    [Fact]
    public async Task RejectsDuplicateWavChunksAndUnalignedData()
    {
        byte[] source = Wav(1, 16, 1, 48000, [0, .25]);
        byte[] duplicate = [.. source, .. source[12..36]];
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(4), (uint)duplicate.Length - 8);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(duplicate), "duplicate.wav"));
        var unaligned = source[..^2]; BinaryPrimitives.WriteUInt32LittleEndian(unaligned.AsSpan(4), (uint)unaligned.Length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(unaligned.AsSpan(40), 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(unaligned), "unaligned.wav"));
    }

    [Fact]
    public async Task RejectsDurationAndFrameLimits()
    {
        byte[] longWav = Wav(1, 8, 1, 8000, new double[8000 * 30 + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(longWav), "long.wav"));
        byte[] manyFrames = Wav(1, 8, 1, 192000, new double[SampleImporter.MaxSamples + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(manyFrames), "large.wav"));
        var exact = await SampleImporter.ReadAsync(new MemoryStream(Wav(1, 8, 1, 8000, new double[8000 * 30])), "limit.wav");
        Assert.Equal(30, exact.DurationSeconds);
    }

    [Fact]
    public async Task EncodedLimitsRejectSeekableBeforeReadAndBoundNonseekableProbe()
    {
        using var known = new LengthOnlyStream(SampleImporter.MaxEncodedBytes + 1, seekable: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(known, "large.wav"));
        Assert.Equal(0, known.ReadBytes);
        using var unknown = new LengthOnlyStream(SampleImporter.MaxEncodedBytes + 100, seekable: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(unknown, "large.wav"));
        Assert.Equal(SampleImporter.MaxEncodedBytes + 1, unknown.ReadBytes);
    }

    [Fact]
    public async Task RejectsPathologicalChunkCountsBeforeLengthyBrowserWork()
    {
        byte[] source = Wav(1, 16, 1, 48000, [.2]);
        byte[] wav = new byte[source.Length + 4100 * 8]; source.CopyTo(wav, 0);
        for (int offset = source.Length; offset < wav.Length; offset += 8) "JUNK"u8.CopyTo(wav.AsSpan(offset));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)wav.Length - 8);
        var wavError = await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(wav), "chunks.wav"));
        Assert.Contains("chunks", wavError.Message);

        byte[] one = QoaCodec.Encode([0], 48000, 1);
        byte[] qoa = new byte[8 + (one.Length - 8) * 1025]; one.AsSpan(0, 8).CopyTo(qoa);
        BinaryPrimitives.WriteUInt32BigEndian(qoa.AsSpan(4), 1025);
        for (int offset = 8; offset < qoa.Length; offset += one.Length - 8) one.AsSpan(8).CopyTo(qoa.AsSpan(offset));
        var qoaError = await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(qoa), "frames.qoa"));
        Assert.Contains("encoded frames", qoaError.Message);
    }

    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(8)]
    public async Task ImportsQoaFramesThroughExistingCodecWithExactMonoDownmix(int channels)
    {
        const int frames = 5121;
        var pcm = Enumerable.Range(0, frames * channels).Select(i => (short)(Math.Sin(i / channels * .07) * 16000)).ToArray();
        byte[] encoded = QoaCodec.Encode(pcm, 48000, channels);
        short[] decoded = QoaCodec.Decode(encoded, out _, out _);
        var clip = await SampleImporter.ReadAsync(new MemoryStream(encoded), "sample.qoa");
        Assert.Equal(channels, clip.OriginalChannels); Assert.Equal(frames, clip.Samples.Length);
        for (int i = 0; i < frames; i++)
        {
            double sum = 0; for (int c = 0; c < channels; c++) sum += decoded[i * channels + c];
            Assert.Equal((float)(sum / (32768d * channels)), clip.Samples[i]);
        }
    }

    [Fact]
    public async Task RejectsHostileTruncatedAndTrailingQoaBeforePcmAllocation()
    {
        byte[] original = QoaCodec.Encode(new short[41 * 2]);
        foreach (int length in new[] { 0, 4, 8, 15, original.Length - 1 })
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(original[..length]), "bad.qoa"));
        foreach (Action<byte[]> damage in new Action<byte[]>[]
        {
            b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), uint.MaxValue),
            b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0),
            b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), SampleImporter.MaxSamples + 1),
            b => b[8] = 0,
            b => b[8] = 9,
            b => { b[9] = 0xff; b[10] = 0xff; b[11] = 0xff; },
            b => { b[12] = 0; b[13] = 0; },
            b => { b[14] = 0; b[15] = 8; }
        })
        {
            var bad = (byte[])original.Clone(); damage(bad);
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream(bad), "bad.qoa"));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadAsync(new MemoryStream([.. original, 0]), "bad.qoa"));
    }

    [Fact]
    public async Task CancellationAndInvalidAnalysisBoundsAreExplicit()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SampleImporter.ReadAsync(new MemoryStream(), "cancel.wav", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SampleImporter.ReadFfmpegAsync("ffmpeg", "test.wav", cancellation.Token));
        var clip = Tone(440, 48000, .1);
        Assert.ThrowsAny<OperationCanceledException>(() => SampleAnalysis.Detect(clip, 0, clip.Samples.Length, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Detect(clip, -1, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Detect(clip, 0, clip.Samples.Length + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Extract(clip, 0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Extract(clip, clip.Samples.Length - 10, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Extract(clip, 0, 128, 4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Shape([0, 1], -.1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Shape([0, 1], 0, double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleAnalysis.Shape([0, 1], 0, 0, 1.1));
        clip.Samples[0] = float.NaN;
        Assert.Throws<ArgumentException>(() => SampleAnalysis.Detect(clip, 0, clip.Samples.Length));
        Assert.Throws<ArgumentException>(() => SampleAnalysis.Extract(clip, 0, 128));
    }

    [Fact]
    public async Task FfmpegRequiresExplicitExecutableAndLocalInputBeforeLaunching()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => SampleImporter.ReadFfmpegAsync("ffmpeg", "/input.wav"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => SampleImporter.ReadFfmpegAsync("/definitely-missing/ffmpeg", "/input.wav"));
        string exists = typeof(SampleImporter).Assembly.Location;
        foreach (string input in new[] { "https://example.com/audio.mp3", "pipe:0", "relative.mp3", "//server/share/audio.mp3", @"\\server\share\audio.mp3" })
            await Assert.ThrowsAsync<ArgumentException>(() => SampleImporter.ReadFfmpegAsync(exists, input));
        string path = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadFfmpegAsync(exists, path));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(SampleImporter.MaxEncodedBytes + 1);
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadFfmpegAsync(exists, path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExplicitInstalledFfmpegImportsSafelyAndRejectsOverlength()
    {
        // Optional integration test: no PATH search, download, shell, or guessed executable is performed.
        string? executable = Environment.GetEnvironmentVariable("MUSICMACHINE_TEST_FFMPEG");
        if (string.IsNullOrWhiteSpace(executable)) return;
        string directory = Path.Combine(Path.GetTempPath(), $"sample import {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "tone with spaces & quotes '.wav");
        try
        {
            var tone = Tone(440, 24000, .1);
            await File.WriteAllBytesAsync(path, Wav(1, 16, 1, 24000, tone.Samples.Select(v => (double)v).ToArray()));
            var clip = await SampleImporter.ReadFfmpegAsync(executable, path);
            Assert.Equal(48000, clip.SampleRate); Assert.Equal(0, clip.OriginalChannels);
            Assert.InRange(clip.Samples.Length, 4790, 4810);
            Assert.InRange(SampleAnalysis.Detect(clip, 0, clip.Samples.Length).Frequency, 435, 445);
            await File.WriteAllBytesAsync(path, Wav(1, 8, 1, 8000, new double[8000 * 31]));
            await Assert.ThrowsAsync<InvalidDataException>(() => SampleImporter.ReadFfmpegAsync(executable, path));
            await File.WriteAllTextAsync(path, "file is not audio");
            var error = await Assert.ThrowsAsync<IOException>(() => SampleImporter.ReadFfmpegAsync(executable, path));
            Assert.True(error.Message.Length < 8500);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SampleClip Tone(double frequency, int rate, double seconds, double dc = 0)
    {
        var samples = new float[(int)(rate * seconds)];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(.6 * Math.Sin(i * 2 * Math.PI * frequency / rate) + dc);
        return new SampleClip(samples, rate, 1, "generated.wav");
    }

    private static double DifferenceEnergy(short[] wave)
    {
        double sum = 0; for (int i = 0; i < wave.Length; i++) { double d = wave[i] - wave[(i + 1) % wave.Length]; sum += d * d; }
        return sum;
    }

    private static byte[] Wav(int format, int bits, int channels, int rate, double[] samples)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        int bytes = samples.Length * (bits / 8), padding = bytes & 1;
        writer.Write("RIFF"u8); writer.Write(36 + bytes + padding); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)format); writer.Write((ushort)channels); writer.Write(rate); writer.Write(rate * channels * bits / 8);
        writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits); writer.Write("data"u8); writer.Write(bytes);
        foreach (double sample in samples)
        {
            if (format == 3) { if (bits == 32) writer.Write((float)sample); else writer.Write(sample); }
            else if (bits == 8) writer.Write((byte)Math.Clamp((int)(sample * 128) + 128, 0, 255));
            else if (bits == 16) writer.Write((short)Math.Clamp((int)(sample * 32768), -32768, 32767));
            else if (bits == 24)
            {
                int value = (int)Math.Clamp((long)(sample * 8388608), -8388608, 8388607);
                writer.Write((byte)value); writer.Write((byte)(value >> 8)); writer.Write((byte)(value >> 16));
            }
            else writer.Write((int)Math.Clamp((long)(sample * 2147483648), int.MinValue, int.MaxValue));
        }
        if (padding != 0) writer.Write((byte)0);
        return memory.ToArray();
    }

    private sealed class FragmentedStream(byte[] bytes) : Stream
    {
        private int position;
        public bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int length = Math.Min(Math.Min(count, 7), bytes.Length - position);
            bytes.AsSpan(position, length).CopyTo(buffer.AsSpan(offset)); position += length; return length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(Math.Min(buffer.Length, 7), bytes.Length - position);
            bytes.AsMemory(position, length).CopyTo(buffer); position += length; return ValueTask.FromResult(length);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LengthOnlyStream(int length, bool seekable) : Stream
    {
        public int ReadBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();
        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Use async reads.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length, length - ReadBytes); buffer.Span[..count].Clear(); ReadBytes += count;
            return ValueTask.FromResult(count);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
