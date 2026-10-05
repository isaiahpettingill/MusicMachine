using System.Buffers.Binary;

namespace MusicMachine.Audio;

/// <summary>
/// Dependency-free RFC 9639 encoder for 48 kHz, stereo PCM16. Uses constant, verbatim,
/// and fixed-predictor subframes. The optional STREAMINFO MD5 is zero (unknown);
/// every header and frame carries its required CRC. No native or cryptography API is used.
/// </summary>
public static class FlacEncoder
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BlockSize = 4096;

    /// <summary>Returns a complete FLAC file. For bounded-memory output, use Write instead.</summary>
    public static byte[] Encode(ReadOnlySpan<short> stereo, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        Write(stream, stereo, cancellationToken);
        return stream.ToArray();
    }

    /// <summary>Writes a complete FLAC file without closing or seeking the supplied stream.</summary>
    public static void Write(Stream destination, ReadOnlySpan<short> stereo, CancellationToken cancellationToken = default)
    {
        if (stereo.IsEmpty || (stereo.Length & 1) != 0)
            throw new ArgumentException("FLAC needs at least one complete stereo PCM16 frame.", nameof(stereo));
        var writer = new Writer(destination, stereo.Length / Channels, cancellationToken);
        for (int offset = 0; offset < stereo.Length;)
        {
            int count = Math.Min(BlockSize * Channels, stereo.Length - offset);
            writer.WriteBlock(stereo.Slice(offset, count), cancellationToken);
            offset += count;
        }
        writer.Complete(cancellationToken);
    }

    // A Writer owns only fixed-size scratch buffers; neither sample data nor encoded frames accumulate.
    // All blocks except the final one must contain exactly BlockSize interchannel samples.
    internal sealed class Writer
    {
        private readonly Stream _destination;
        private readonly long _totalFrames;
        private readonly int[] _samples = new int[BlockSize];
        private readonly uint[] _residual = new uint[BlockSize];
        private readonly BitWriter _bits = new();
        private long _writtenFrames;
        private uint _frameNumber;

        internal Writer(Stream destination, long totalFrames, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite) throw new ArgumentException("The FLAC destination is not writable.", nameof(destination));
            if (totalFrames < 1 || totalFrames >= (1L << 36))
                throw new ArgumentOutOfRangeException(nameof(totalFrames), "FLAC sample count must fit in 36 bits and be positive.");
            cancellationToken.ThrowIfCancellationRequested();
            _destination = destination;
            _totalFrames = totalFrames;
            Span<byte> metadata = stackalloc byte[42];
            metadata.Clear();
            "fLaC"u8.CopyTo(metadata);
            metadata[4] = 0x80; // Last metadata block, type STREAMINFO.
            metadata[7] = 34;
            BinaryPrimitives.WriteUInt16BigEndian(metadata[8..], BlockSize);
            BinaryPrimitives.WriteUInt16BigEndian(metadata[10..], BlockSize);
            // Min/max encoded frame sizes and MD5 are zero, explicitly permitted as unknown.
            ulong properties = ((ulong)SampleRate << 44) | (1UL << 41) | (15UL << 36) | (ulong)totalFrames;
            BinaryPrimitives.WriteUInt64BigEndian(metadata[18..], properties);
            _destination.Write(metadata);
        }

        internal void WriteBlock(ReadOnlySpan<short> stereo, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frames = stereo.Length / Channels;
            if ((stereo.Length & 1) != 0 || frames < 1 || frames > BlockSize ||
                _writtenFrames + frames > _totalFrames || (frames != BlockSize && _writtenFrames + frames != _totalFrames))
                throw new ArgumentException("Invalid FLAC block size or total sample count.", nameof(stereo));

            _bits.Reset();
            _bits.Write(0xfff8, 16); // Sync and fixed-block strategy; coded number is a frame number.
            int blockCode = frames == BlockSize ? 12 : frames <= 256 ? 6 : 7;
            _bits.Write((uint)((blockCode << 4) | 10), 8); // 48 kHz.
            _bits.Write(0x18, 8); // Independent stereo, 16 bits/sample, reserved zero.
            WriteFrameNumber(_frameNumber);
            if (blockCode == 6) _bits.Write((uint)(frames - 1), 8);
            else if (blockCode == 7) _bits.Write((uint)(frames - 1), 16);
            _bits.Write(Crc8(_bits.Bytes), 8);

            for (int channel = 0; channel < Channels; channel++)
            {
                for (int i = 0; i < frames; i++) _samples[i] = stereo[i * Channels + channel];
                WriteSubframe(frames, cancellationToken);
            }
            _bits.Align();
            _bits.Write(Crc16(_bits.Bytes), 16);
            cancellationToken.ThrowIfCancellationRequested();
            _destination.Write(_bits.Bytes);
            _writtenFrames += frames;
            _frameNumber++;
        }

        internal void Complete(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_writtenFrames != _totalFrames) throw new InvalidOperationException("FLAC stream is incomplete.");
        }

        private void WriteSubframe(int frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool constant = true;
            for (int i = 1; i < frames; i++) constant &= _samples[i] == _samples[0];
            if (constant)
            {
                _bits.Write(0, 8);
                _bits.Write(unchecked((uint)_samples[0]), 16);
                return;
            }

            // Exact bit costs, including warm-up samples and the residual headers. Ties stay
            // verbatim. A compressed subframe can never exceed the raw PCM subframe budget.
            long bestCost = 8L + frames * 16L;
            int bestOrder = -1, bestRice = 0;
            for (int order = 0; order <= Math.Min(4, frames - 1); order++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = frames - order;
                for (int i = order; i < frames; i++) _residual[i - order] = Fold(_samples[i] - Predict(i, order));
                for (int rice = 0; rice <= 14; rice++)
                {
                    long cost = 18L + order * 16L + (long)count * (1 + rice);
                    for (int i = 0; i < count && cost < bestCost; i++) cost += _residual[i] >> rice;
                    if (cost < bestCost) { bestCost = cost; bestOrder = order; bestRice = rice; }
                }
            }
            if (bestOrder < 0)
            {
                _bits.Write(2, 8); // Verbatim, no wasted bits.
                for (int i = 0; i < frames; i++) _bits.Write(unchecked((uint)_samples[i]), 16);
                return;
            }

            _bits.Write((uint)((8 + bestOrder) << 1), 8);
            for (int i = 0; i < bestOrder; i++) _bits.Write(unchecked((uint)_samples[i]), 16);
            _bits.Write(0, 6); // Rice method 0, partition order 0 (one partition).
            _bits.Write((uint)bestRice, 4);
            for (int i = bestOrder; i < frames; i++)
            {
                uint folded = Fold(_samples[i] - Predict(i, bestOrder));
                _bits.WriteZeros((int)(folded >> bestRice));
                _bits.Write(1, 1);
                _bits.Write(folded, bestRice);
            }
        }

        // PCM16 with order <= 4 bounds every intermediate and residual to fewer than 21 bits.
        private int Predict(int i, int order) => order switch
        {
            0 => 0,
            1 => _samples[i - 1],
            2 => 2 * _samples[i - 1] - _samples[i - 2],
            3 => 3 * _samples[i - 1] - 3 * _samples[i - 2] + _samples[i - 3],
            4 => 4 * _samples[i - 1] - 6 * _samples[i - 2] + 4 * _samples[i - 3] - _samples[i - 4],
            _ => throw new InvalidOperationException("Invalid fixed predictor order.")
        };

        private static uint Fold(int residual) => (uint)((residual << 1) ^ (residual >> 31));

        private void WriteFrameNumber(uint value)
        {
            if (value < 0x80) { _bits.Write(value, 8); return; }
            int bytes = value < 0x800 ? 2 : value < 0x10000 ? 3 : value < 0x200000 ? 4 : value < 0x4000000 ? 5 : 6;
            _bits.Write((uint)((0xff << (8 - bytes)) & 0xff) | (value >> (6 * (bytes - 1))), 8);
            for (int i = bytes - 2; i >= 0; i--) _bits.Write(0x80 | ((value >> (6 * i)) & 0x3f), 8);
        }
    }

    private static byte Crc8(ReadOnlySpan<byte> bytes)
    {
        byte crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (byte)((crc << 1) ^ ((crc & 0x80) != 0 ? 0x07 : 0));
        }
        return crc;
    }

    private static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x8005 : 0));
        }
        return crc;
    }

    private sealed class BitWriter
    {
        // Verbatim stereo PCM plus subframe headers, largest frame header and CRC. Fixed
        // predictors are chosen only when smaller, so this bound does not depend on input.
        private readonly byte[] _buffer = new byte[BlockSize * Channels * sizeof(short) + 32];
        private int _byteCount, _pendingBits;
        private uint _pending;
        internal ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _byteCount);
        internal void Reset() { _byteCount = 0; _pendingBits = 0; _pending = 0; }
        internal void Write(uint value, int count)
        {
            while (count > 0)
            {
                int take = Math.Min(8 - _pendingBits, count);
                count -= take;
                _pending = (_pending << take) | ((value >> count) & ((1u << take) - 1));
                _pendingBits += take;
                if (_pendingBits == 8) { _buffer[_byteCount++] = (byte)_pending; _pending = 0; _pendingBits = 0; }
            }
        }
        internal void WriteZeros(int count)
        {
            if (_pendingBits != 0)
            {
                int take = Math.Min(8 - _pendingBits, count);
                Write(0, take);
                count -= take;
            }
            int bytes = count / 8;
            _buffer.AsSpan(_byteCount, bytes).Clear();
            _byteCount += bytes;
            Write(0, count & 7);
        }
        internal void Align() { if (_pendingBits != 0) Write(0, 8 - _pendingBits); }
    }
}
