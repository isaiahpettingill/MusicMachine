using System.Buffers.Binary;

namespace MusicMachine.Audio;

/// <summary>Managed QOA encoder/decoder, based on the official MIT-licensed reference codec.
/// Format and arithmetic: https://github.com/phoboslab/qoa/blob/master/qoa.h . See QOA-LICENSE.txt.</summary>
public static class QoaCodec
{
    public const int FrameSamples = 5120;
    private static readonly int[] Quant = [7, 7, 7, 5, 5, 3, 3, 1, 0, 0, 2, 2, 4, 4, 6, 6, 6];
    private static readonly int[] Reciprocal = [65536, 9363, 3121, 1457, 781, 475, 311, 216, 156, 117, 90, 71, 57, 47, 39, 32];
    private static readonly int[] Scales = [1, 7, 21, 45, 84, 138, 211, 304, 421, 562, 731, 928, 1157, 1419, 1715, 2048];
    private static readonly int[,] Dequant = MakeDequant();
    private static int[,] MakeDequant()
    {
        var result = new int[16, 8]; double[] weights = [.75, -.75, 2.5, -2.5, 4.5, -4.5, 7, -7];
        for (int s = 0; s < 16; s++) for (int q = 0; q < 8; q++) result[s, q] = (int)Math.Round(Scales[s] * weights[q], MidpointRounding.AwayFromZero);
        return result;
    }
    private struct Lms
    {
        public int H0, H1, H2, H3, W0, W1, W2, W3;
        public readonly int Predict() => unchecked(W0 * H0 + W1 * H1 + W2 * H2 + W3 * H3) >> 13;
        public void Update(int sample, int residual)
        {
            int d = residual >> 4;
            W0 += H0 < 0 ? -d : d; W1 += H1 < 0 ? -d : d; W2 += H2 < 0 ? -d : d; W3 += H3 < 0 ? -d : d;
            H0 = H1; H1 = H2; H2 = H3; H3 = sample;
        }
        public readonly long Penalty()
        {
            long p = (((long)W0 * W0 + (long)W1 * W1 + (long)W2 * W2 + (long)W3 * W3) >> 18) - 0x8ff;
            return Math.Max(0, p);
        }
        public readonly ulong History => Pack(H0, H1, H2, H3);
        public readonly ulong Weights => Pack(W0, W1, W2, W3);
        private static ulong Pack(int a, int b, int c, int d) => (ulong)(ushort)a << 48 | (ulong)(ushort)b << 32 | (ulong)(ushort)c << 16 | (ushort)d;
        public static Lms From(ulong h, ulong w) => new()
        {
            H0 = (short)(h >> 48), H1 = (short)(h >> 32), H2 = (short)(h >> 16), H3 = (short)h,
            W0 = (short)(w >> 48), W1 = (short)(w >> 32), W2 = (short)(w >> 16), W3 = (short)w
        };
    }
    public static byte[] Encode(ReadOnlySpan<short> interleaved, int sampleRate = 48000, int channels = 2, CancellationToken cancellationToken = default)
    {
        if (channels is < 1 or > 8 || sampleRate is < 1 or > 0xffffff || interleaved.Length == 0 || interleaved.Length % channels != 0) throw new ArgumentException("QOA requires 1–8 channels, a valid sample rate, and complete nonempty frames.");
        cancellationToken.ThrowIfCancellationRequested();
        int samples = interleaved.Length / channels;
        int frames = (samples - 1) / FrameSamples + 1;
        int slices = (samples - 1) / 20 + 1;
        var bytes = new byte[checked(8 + frames * (8 + 16 * channels) + slices * 8 * channels)];
        int p = 0; Write(bytes, ref p, 0x716f616600000000UL | (uint)samples);
        var states = new Lms[channels];
        for (int c = 0; c < channels; c++) states[c] = new Lms { W2 = -8192, W3 = 16384 };
        for (int start = 0; start < samples; start += FrameSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(FrameSamples, samples - start);
            p += EncodeFrame(bytes.AsSpan(p), interleaved.Slice(start * channels, count * channels), sampleRate, channels, states);
        }
        return bytes;
    }
    private static int EncodeFrame(Span<byte> bytes, ReadOnlySpan<short> interleaved, int sampleRate, int channels, Lms[] states)
    {
        int count = interleaved.Length / channels, p = 0;
        int frameSize = 8 + channels * 16 + ((count + 19) / 20) * channels * 8;
        Write(bytes, ref p, (ulong)channels << 56 | (ulong)sampleRate << 32 | (ulong)count << 16 | (uint)frameSize);
        for (int c = 0; c < channels; c++)
        {
            // Restart each frame from exactly the signed 16-bit state stored in its header.
            states[c] = Lms.From(states[c].History, states[c].Weights);
            Write(bytes, ref p, states[c].History); Write(bytes, ref p, states[c].Weights);
        }
        for (int index = 0; index < count; index += 20)
        for (int c = 0; c < channels; c++)
        {
            int length = Math.Min(20, count - index); long bestRank = long.MaxValue; ulong bestSlice = 0; Lms best = states[c];
            for (int scale = 0; scale < 16; scale++)
            {
                Lms lms = states[c]; ulong slice = (uint)scale; long rank = 0;
                for (int i = 0; i < length; i++)
                {
                    int sample = interleaved[(index + i) * channels + c];
                    int predicted = lms.Predict(), residual = sample - predicted;
                    int scaled = (int)(((long)residual * Reciprocal[scale] + (1 << 15)) >> 16);
                    scaled += Math.Sign(residual) - Math.Sign(scaled);
                    int quantized = Quant[Math.Clamp(scaled, -8, 8) + 8];
                    int dequantized = Dequant[scale, quantized];
                    int reconstructed = Math.Clamp(predicted + dequantized, -32768, 32767);
                    long error = sample - reconstructed, penalty = lms.Penalty();
                    rank += error * error + penalty * penalty;
                    if (rank > bestRank) break;
                    lms.Update(reconstructed, dequantized); slice = (slice << 3) | (uint)quantized;
                }
                if (rank < bestRank) { bestRank = rank; bestSlice = slice; best = lms; }
            }
            states[c] = best; Write(bytes, ref p, bestSlice << ((20 - length) * 3));
        }
        return p;
    }

    internal static void ValidateFormat(long frames, int sampleRate, int channels)
    {
        if (frames is < 1 or > uint.MaxValue || channels is < 1 or > 8 || sampleRate is < 1 or > 0xffffff)
            throw new ArgumentException("QOA requires 1–8 channels, a valid sample rate, and 1–4,294,967,295 frames.");
    }

    /// <summary>Bounded-memory QOA writer. The caller owns the stream; frames retain the reference codec's LMS history.</summary>
    public sealed class Writer
    {
        private readonly Stream stream;
        private readonly long total;
        private readonly int sampleRate, channels;
        private readonly Lms[] states, pendingStates;
        private readonly byte[] frame;
        private long written;
        public Writer(Stream stream, long frames, int sampleRate = 48000, int channels = 2, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanWrite) throw new ArgumentException("QOA needs a writable stream.", nameof(stream));
            ValidateFormat(frames, sampleRate, channels);
            cancellationToken.ThrowIfCancellationRequested();
            this.stream = stream; total = frames; this.sampleRate = sampleRate; this.channels = channels;
            states = new Lms[channels]; pendingStates = new Lms[channels];
            for (int c = 0; c < channels; c++) states[c] = new Lms { W2 = -8192, W3 = 16384 };
            frame = new byte[8 + channels * 16 + (FrameSamples / 20) * channels * 8];
            Span<byte> header = stackalloc byte[8]; int p = 0;
            Write(header, ref p, 0x716f616600000000UL | (uint)frames); stream.Write(header);
        }
        public void WriteFrame(ReadOnlySpan<short> interleaved, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int expected = (int)Math.Min(FrameSamples, total - written);
            if (expected == 0 || interleaved.Length != expected * channels)
                throw new ArgumentException("Write one complete QOA frame, or the final partial frame, in sequence.");
            // Do not advance the predictor if cancellation arrives while encoding the frame.
            states.CopyTo(pendingStates, 0);
            int length = EncodeFrame(frame, interleaved, sampleRate, channels, pendingStates);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Write(frame.AsSpan(0, length));
            pendingStates.CopyTo(states, 0); written += expected;
        }
        public void Complete(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (written != total) throw new InvalidOperationException("The QOA stream is incomplete.");
        }
    }
    public static short[] Decode(ReadOnlySpan<byte> data, out int sampleRate, out int channels)
    {
        sampleRate = channels = 0;
        if (data.Length < 16) throw new InvalidDataException("Truncated QOA header.");
        int p = 0; ulong header = Read(data, ref p);
        if ((header >> 32) != 0x716f6166) throw new InvalidDataException("Not a QOA file.");
        uint total = (uint)header;
        ulong first = BinaryPrimitives.ReadUInt64BigEndian(data[p..]);
        channels = (int)(first >> 56); sampleRate = (int)((first >> 32) & 0xffffff);
        if (total == 0 || total > int.MaxValue / Math.Max(1, channels) || channels is < 1 or > 8 || sampleRate == 0) throw new InvalidDataException("Invalid QOA stream metadata.");
        // Check attainable coverage before allocating for an untrusted header.
        if ((long)total * channels > (long)data.Length * 3) throw new InvalidDataException("QOA header exceeds available frame data.");
        var output = new short[checked((int)total * channels)]; var states = new Lms[channels]; int written = 0;
        while (written < total)
        {
            int frameStart = p; ulong fh = Read(data, ref p);
            int fc = (int)(fh >> 56), rate = (int)((fh >> 32) & 0xffffff), count = (int)((fh >> 16) & 0xffff), size = (int)(fh & 0xffff);
            int required = 8 + 16 * channels + ((count + 19) / 20) * 8 * channels;
            if (fc != channels || rate != sampleRate || count is < 1 or > FrameSamples || count > total - written || size != required || size > data.Length - frameStart) throw new InvalidDataException("Invalid or truncated QOA frame.");
            for (int c = 0; c < channels; c++) { ulong h = Read(data, ref p), w = Read(data, ref p); states[c] = Lms.From(h, w); }
            for (int index = 0; index < count; index += 20)
            for (int c = 0; c < channels; c++)
            {
                ulong slice = Read(data, ref p); int scale = (int)(slice >> 60); slice <<= 4;
                for (int i = 0; i < Math.Min(20, count - index); i++)
                {
                    int residual = Dequant[scale, (int)(slice >> 61)];
                    int sample = Math.Clamp(states[c].Predict() + residual, -32768, 32767);
                    output[(written + index + i) * channels + c] = (short)sample; states[c].Update(sample, residual); slice <<= 3;
                }
            }
            written += count;
        }
        if (p != data.Length) throw new InvalidDataException("Unexpected trailing QOA data.");
        return output;
    }
    private static void Write(Span<byte> bytes, ref int p, ulong value) { BinaryPrimitives.WriteUInt64BigEndian(bytes[p..], value); p += 8; }
    private static ulong Read(ReadOnlySpan<byte> bytes, ref int p)
    {
        if (bytes.Length - p < 8) throw new InvalidDataException("Truncated QOA data.");
        ulong result = BinaryPrimitives.ReadUInt64BigEndian(bytes[p..]); p += 8; return result;
    }
}
