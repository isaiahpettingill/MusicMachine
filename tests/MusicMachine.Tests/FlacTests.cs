using System.Buffers.Binary;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Tests;

public sealed class FlacTests
{
    public static IEnumerable<object[]> BoundaryCases()
    {
        foreach (int frames in new[] { 1, 15, 16, 255, 256, 257, 4095, 4096, 4097, 12303 })
            foreach (int signal in Enumerable.Range(0, 7)) yield return [frames, signal];
    }

    [Theory, MemberData(nameof(BoundaryCases))]
    public void LosslessAcrossSignalsPartialBlocksAndFullPcm16Range(int frames, int signal)
    {
        short[] input = MakeSignal(frames, signal);
        byte[] encoded = FlacEncoder.Encode(input);
        var decoded = Decode(encoded);
        Assert.Equal(input, decoded.Pcm);
        Assert.InRange(encoded.Length, 1, 42 + input.Length * 2 + ((frames + 4095) / 4096) * 32);
        Assert.Equal(encoded, FlacEncoder.Encode(input));
    }

    [Theory]
    [InlineData(0, 256)] [InlineData(1, 256)] [InlineData(2, 128)] [InlineData(3, 32)] [InlineData(4, 16)]
    public void FixedPredictorOrdersAreActuallyUsed(int order, int frames)
    {
        var input = new short[frames * 2];
        var random = new Random(440);
        int[] values = Enumerable.Range(0, frames).Select(_ => random.Next(-2, 3)).ToArray();
        for (int pass = 0; pass < order; pass++)
            for (int i = 1; i < frames; i++) values[i] += values[i - 1];
        // A quartic makes the highest supported predictor worthwhile even in this tiny block.
        if (order == 4) for (int i = 0; i < frames; i++) values[i] = (int)Math.Pow(i - frames / 2, 4);
        for (int i = 0; i < frames; i++) input[i * 2] = input[i * 2 + 1] = checked((short)values[i]);
        var decoded = Decode(FlacEncoder.Encode(input));
        Assert.Equal(input, decoded.Pcm);
        Assert.Contains(8 + order, decoded.SubframeTypes);
    }

    [Fact] public void ConstantAndUncorrelatedAudioChooseSmallestSupportedRepresentation()
    {
        var silence = Decode(FlacEncoder.Encode(new short[8192]));
        Assert.All(silence.SubframeTypes, type => Assert.Equal(0, type));
        var noise = MakeSignal(4096, 4);
        var decoded = Decode(FlacEncoder.Encode(noise));
        Assert.Equal(noise, decoded.Pcm);
        Assert.All(decoded.SubframeTypes, type => Assert.Equal(1, type));
    }

    [Theory] [InlineData(130)] [InlineData(2050)]
    public void FrameNumbersCrossExtendedUtf8Boundaries(int blocks)
    {
        var input = new short[4096 * 2 * blocks + 2];
        input[^2] = 100;
        input[^1] = -100;
        Assert.Equal(input, Decode(FlacEncoder.Encode(input)).Pcm);
    }

    [Fact] public void StreamInfoDescribesPcmAndExplicitlyOmitsOptionalMd5()
    {
        byte[] data = FlacEncoder.Encode(MakeSignal(4097, 2));
        Assert.Equal("fLaC"u8.ToArray(), data[..4]);
        Assert.Equal(new byte[] { 0x80, 0, 0, 34 }, data[4..8]);
        Assert.Equal(4096, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8)));
        Assert.Equal(4096, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10)));
        Assert.All(data[12..18], b => Assert.Equal(0, b));
        Assert.All(data[26..42], b => Assert.Equal(0, b));
        ulong info = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(18));
        Assert.Equal(48000UL, info >> 44);
        Assert.Equal(1UL, (info >> 41) & 7);
        Assert.Equal(15UL, (info >> 36) & 31);
        Assert.Equal(4097UL, info & 0xfffffffff);
    }

    [Fact] public void FrameAndHeaderChecksumsDetectDamage()
    {
        var bytes = FlacEncoder.Encode(MakeSignal(4097, 2));
        var damagedHeader = bytes.ToArray(); damagedHeader[46] ^= 1;
        Assert.Throws<InvalidDataException>(() => Decode(damagedHeader));
        var damagedPayload = bytes.ToArray(); damagedPayload[51] ^= 1;
        Assert.Throws<InvalidDataException>(() => Decode(damagedPayload));
        var damagedFooter = bytes.ToArray(); damagedFooter[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => Decode(damagedFooter));
        Assert.Throws<InvalidDataException>(() => Decode(bytes[..^1]));
    }

    [Fact] public void FloatExportIsExactlyTheSameQuantizedPcmAsWav()
    {
        float[] values = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -2, 2, -1,
            1, 0, -.5f, .5f, 1f / 32767, -1f / 32767, .2f, -.2f];
        string directory = NewDirectory();
        try
        {
            string wav = Path.Combine(directory, "reference.wav"), flac = Path.Combine(directory, "actual.flac");
            OfflineExporter.WriteWav(wav, values);
            OfflineExporter.WriteFlac(flac, values);
            var reference = File.ReadAllBytes(wav).AsSpan(44).ToArray();
            Assert.Equal(reference, PcmBytes(Decode(File.ReadAllBytes(flac)).Pcm));
            Assert.Single(Directory.GetFiles(directory, "*.flac"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public async Task StreamingSongAndCooperativeExportsMatchSynchronousPcmIncludingTail()
    {
        var song = TestSong.CreateEmpty();
        song.Patterns[0].GetTrack(song.Tracks[0].Id).Rows[0] = new() { Kind = NoteKind.Note, Pitch = 69 };
        string directory = NewDirectory();
        try
        {
            foreach (bool tail in new[] { false, true })
            {
                string sync = Path.Combine(directory, "sync.flac"), asyncPath = Path.Combine(directory, "async.flac");
                OfflineExporter.WriteFlac(sync, song, includeTail: tail);
                await OfflineExporter.WriteFlacAsync(asyncPath, song, includeTail: tail);
                var encoded = File.ReadAllBytes(sync);
                Assert.Equal(encoded, File.ReadAllBytes(asyncPath));
                var expected = OfflineExporter.Render(song, includeTail: tail).Select(OfflineExporter.ToPcm16).ToArray();
                Assert.Equal(expected, Decode(encoded).Pcm);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void SineAndDemoAreMeaningfullySmallerThanPcm16Wav()
    {
        short[] sine = MakeSignal(48000, 2);
        Assert.True(FlacEncoder.Encode(sine).Length < sine.Length * 2 * .6);
        string directory = NewDirectory();
        try
        {
            var song = DemoSong.Create();
            string path = Path.Combine(directory, "demo.flac");
            OfflineExporter.WriteFlac(path, song);
            long wavSize = new SynthRenderer(song).MusicalFrames * 4 + 44;
            Assert.True(new FileInfo(path).Length < wavSize * .9);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void EncoderMemoryIsBoundedIndependentlyOfStreamLength()
    {
        var small = new short[8192]; var large = new short[8192 * 256];
        FlacEncoder.Write(Stream.Null, small); // JIT/static warm-up.
        long start = GC.GetAllocatedBytesForCurrentThread();
        FlacEncoder.Write(Stream.Null, small);
        long smallAllocated = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        FlacEncoder.Write(Stream.Null, large);
        long largeAllocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.InRange(smallAllocated, 1, 64000);
        Assert.Equal(smallAllocated, largeAllocated);
    }

    [Fact] public void CancellationBetweenFramesStopsWritingWithoutClosingCallerStream()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelAfterFrameStream(cancellation);
        Assert.Throws<OperationCanceledException>(() => FlacEncoder.Write(stream, MakeSignal(8193, 4), cancellation.Token));
        Assert.Equal(2, stream.Writes); // STREAMINFO and first frame; second frame is not written.
        Assert.True(stream.CanWrite);
    }

    [Fact] public async Task CancelledAndInvalidExportsPreserveDestinationAndRemoveTemporaryFiles()
    {
        string directory = NewDirectory(), path = Path.Combine(directory, "keep.flac");
        try
        {
            File.WriteAllText(path, "original");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => OfflineExporter.WriteFlac(path, new float[200], cancellationToken: cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => OfflineExporter.WriteFlac(path, DemoSong.Create(), cancellation.Token));
            Assert.Throws<ArgumentException>(() => OfflineExporter.WriteFlac(path, new float[200], sampleRate: 44100));
            Assert.Throws<ArgumentException>(() => OfflineExporter.WriteFlac(path, new float[3]));
            Assert.Throws<ArgumentException>(() => OfflineExporter.WriteFlac(path, ReadOnlySpan<float>.Empty));
            Assert.Equal("original", File.ReadAllText(path));
            using var laterCancellation = new CancellationTokenSource();
            Task export = OfflineExporter.WriteFlacAsync(path, DemoSong.Create(), laterCancellation.Token);
            Assert.False(export.IsCompleted); // Timer yield is real, not a synchronous Task.Run substitute.
            await Task.Delay(20);
            laterCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CancelAfterFrameStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public int Writes { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            if (++Writes == 2) cancellation.Cancel();
        }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "musicmachine-flac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    private static short[] MakeSignal(int frames, int signal)
    {
        var input = new short[frames * 2]; var random = new Random(9639);
        for (int i = 0; i < frames; i++)
        {
            input[i * 2] = signal switch
            {
                0 => 0,
                1 => -137,
                2 => (short)(Math.Sin(i * Math.Tau * 440 / 48000) * 28000),
                3 => i % 257 == 0 ? short.MinValue : i % 127 == 0 ? short.MaxValue : (short)0,
                4 => (short)random.Next(short.MinValue, short.MaxValue + 1),
                5 => (i & 1) == 0 ? short.MinValue : short.MaxValue,
                _ => (short)(i - 2048)
            };
            input[i * 2 + 1] = signal switch
            {
                0 => 0,
                1 => short.MaxValue,
                2 => (short)(Math.Sin(i * Math.Tau * 660 / 48000) * 17000),
                4 => (short)random.Next(short.MinValue, short.MaxValue + 1),
                _ => (short)(-input[i * 2] - 1)
            };
        }
        return input;
    }

    private static byte[] PcmBytes(short[] pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), pcm[i]);
        return bytes;
    }

    // Strict, test-only reader of the emitted subset. External verification additionally uses
    // FFmpeg and libFLAC, so round trips do not rely solely on an in-repository decoder.
    private static (short[] Pcm, List<int> SubframeTypes) Decode(byte[] bytes)
    {
        if (bytes.Length < 42 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 102, 76, 97, 67, 128, 0, 0, 34 })) throw new InvalidDataException();
        ulong info = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18));
        if (info >> 44 != 48000 || ((info >> 41) & 7) != 1 || ((info >> 36) & 31) != 15) throw new InvalidDataException();
        int total = checked((int)(info & 0xfffffffff));
        var pcm = new short[checked(total * 2)];
        var types = new List<int>();
        var reader = new Bits(bytes, 42 * 8);
        int position = 0, frameIndex = 0;
        while (reader.BytePosition < bytes.Length)
        {
            int start = reader.BytePosition;
            if (reader.Read(16) != 0xfff8) throw new InvalidDataException();
            int blockCode = (int)reader.Read(4);
            if (reader.Read(4) != 10 || reader.Read(8) != 0x18) throw new InvalidDataException();
            uint number = reader.Read(8);
            if ((number & 0x80) != 0)
            {
                int leading = 0;
                for (uint mask = 0x80; (number & mask) != 0; mask >>= 1) leading++;
                if (leading < 2 || leading > 6) throw new InvalidDataException();
                number &= (uint)(0x7f >> leading);
                for (int i = 1; i < leading; i++)
                {
                    uint continuation = reader.Read(8);
                    if ((continuation & 0xc0) != 0x80) throw new InvalidDataException();
                    number = (number << 6) | (continuation & 0x3f);
                }
            }
            if (number != frameIndex++) throw new InvalidDataException();
            int count = blockCode switch { 6 => (int)reader.Read(8) + 1, 7 => (int)reader.Read(16) + 1, 12 => 4096, _ => throw new InvalidDataException() };
            if (count > 4096 || position + count > total || (count != 4096 && position + count != total)) throw new InvalidDataException();
            int headerEnd = reader.BytePosition;
            if (reader.Read(8) != Checksum(bytes.AsSpan(start, headerEnd - start), 8, 0x07)) throw new InvalidDataException();
            for (int channel = 0; channel < 2; channel++)
            {
                if (reader.Read(1) != 0) throw new InvalidDataException();
                int type = (int)reader.Read(6); types.Add(type);
                if (reader.Read(1) != 0) throw new InvalidDataException();
                var samples = new int[count];
                if (type == 0) Array.Fill(samples, (short)reader.Read(16));
                else if (type == 1) for (int i = 0; i < count; i++) samples[i] = (short)reader.Read(16);
                else
                {
                    int order = type - 8;
                    if (order < 0 || order > 4 || order >= count) throw new InvalidDataException();
                    for (int i = 0; i < order; i++) samples[i] = (short)reader.Read(16);
                    if (reader.Read(6) != 0) throw new InvalidDataException();
                    int rice = (int)reader.Read(4);
                    if (rice == 15) throw new InvalidDataException();
                    int[][] coefficients = [[], [1], [2, -1], [3, -3, 1], [4, -6, 4, -1]];
                    for (int i = order; i < count; i++)
                    {
                        uint quotient = 0;
                        while (reader.Read(1) == 0) if (++quotient > 0x100000) throw new InvalidDataException();
                        uint folded = (quotient << rice) | reader.Read(rice);
                        int residual = (int)(folded >> 1) ^ -(int)(folded & 1);
                        // Binomial finite-difference reconstruction, separate from production predictor code.
                        int value = residual;
                        for (int j = 0; j < order; j++) value += coefficients[order][j] * samples[i - j - 1];
                        if (value < short.MinValue || value > short.MaxValue) throw new InvalidDataException();
                        samples[i] = value;
                    }
                }
                for (int i = 0; i < count; i++) pcm[(position + i) * 2 + channel] = (short)samples[i];
            }
            while ((reader.Position & 7) != 0) if (reader.Read(1) != 0) throw new InvalidDataException();
            int end = reader.BytePosition;
            if (reader.Read(16) != Checksum(bytes.AsSpan(start, end - start), 16, 0x8005)) throw new InvalidDataException();
            position += count;
        }
        if (position != total) throw new InvalidDataException();
        return (pcm, types);
    }

    private static uint Checksum(ReadOnlySpan<byte> data, int width, uint polynomial)
    {
        uint value = 0;
        foreach (byte b in data)
            for (int bit = 7; bit >= 0; bit--)
            {
                bool feedback = (((value >> (width - 1)) ^ ((uint)b >> bit)) & 1) != 0;
                value = (value << 1) ^ (feedback ? polynomial : 0);
            }
        return value & ((1u << width) - 1);
    }

    private sealed class Bits(byte[] bytes, int position)
    {
        public int Position { get; private set; } = position;
        public int BytePosition => Position / 8;
        public uint Read(int count)
        {
            if (count < 0 || count > 32 || Position + count > bytes.Length * 8) throw new InvalidDataException();
            uint value = 0;
            for (int i = 0; i < count; i++, Position++) value = (value << 1) | (uint)((bytes[Position / 8] >> (7 - Position % 8)) & 1);
            return value;
        }
    }
}
