using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace MusicMachine.Audio;

/// <summary>Bounded, UI-independent WAV/QOA import. Streams may be non-seekable and remain caller-owned.</summary>
public static class SampleImporter
{
    public const int MaxEncodedBytes = 32 * 1024 * 1024;
    public const int MaxDurationSeconds = 30;
    public const int MaxSamples = 1_440_000;
    public const int MaxChannels = 8;
    public const int MaxSampleRate = 192_000;
    public const int FfmpegSampleRate = 48_000;
    public const int FfmpegTimeoutSeconds = 45;
    private const int MaxErrorCharacters = 8192;
    private const int MaxWavChunks = 4096;
    private const int MaxQoaFrames = 1024;

    public static async Task<SampleClip> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanRead) throw new ArgumentException("The sample stream is not readable.", nameof(stream));
        var bytes = await ReadBoundedAsync(stream, MaxEncodedBytes, "Sample files are limited to 32 MiB.", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string name = Path.GetFileName(fileName);
        if (bytes.AsSpan().StartsWith("RIFF"u8)) return await ReadWavAsync(bytes, name, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith("qoaf"u8)) return await ReadQoaAsync(bytes, name, cancellationToken).ConfigureAwait(false);
        throw new InvalidDataException("Choose a WAV (PCM or IEEE float) or QOA file. Other audio formats need optional FFmpeg conversion.");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, string error, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stream.CanSeek && stream.Length - stream.Position > maximum) throw new InvalidDataException(error);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(81920, maximum + 1)];
        while (true)
        {
            // A single probe byte detects oversized streams without buffering beyond the limit.
            int remaining = maximum - (int)output.Length;
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > remaining) throw new InvalidDataException(error);
            output.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static void ValidateMetadata(long frames, int rate, int channels)
    {
        if (channels is < 1 or > MaxChannels || rate is < 1 or > MaxSampleRate)
            throw new InvalidDataException("Samples require 1–8 channels and a sample rate no higher than 192 kHz.");
        if (frames <= 0) throw new InvalidDataException("The sample contains no audio frames.");
        if (frames > MaxSamples || frames > (long)rate * MaxDurationSeconds)
            throw new InvalidDataException("Samples are limited to 30 seconds and 1,440,000 mono frames.");
    }

    private static (int Format, int Channels, int Rate, int Bits, int DataOffset, int Frames) ReadWavMetadata(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        if (bytes.Length < 12 || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != (long)bytes.Length - 8)
            throw new InvalidDataException("Invalid, truncated, or oversized RIFF/WAVE container.");
        int formatOffset = -1, formatLength = 0, dataOffset = -1, dataLength = 0;
        int chunks = 0;
        for (int offset = 12; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (++chunks > MaxWavChunks) throw new InvalidDataException("WAV sample import is limited to 4,096 container chunks.");
            if (bytes.Length - offset < 8) throw new InvalidDataException("Truncated WAV chunk header.");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            long next = (long)offset + 8 + size + (size & 1);
            if (next > bytes.Length) throw new InvalidDataException("Truncated WAV chunk or padding.");
            if (bytes.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (formatOffset >= 0) throw new InvalidDataException("Duplicate WAV format chunk.");
                formatOffset = offset + 8; formatLength = (int)size;
            }
            else if (bytes.Slice(offset, 4).SequenceEqual("data"u8))
            {
                if (dataOffset >= 0) throw new InvalidDataException("Multiple WAV data chunks are not supported.");
                dataOffset = offset + 8; dataLength = (int)size;
            }
            offset = (int)next;
        }
        if (formatOffset < 0 || dataOffset < 0 || formatLength < 16 || formatLength == 17)
            throw new InvalidDataException("WAV needs a complete format chunk and audio data chunk.");
        var fmt = bytes.Slice(formatOffset, formatLength);
        int format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        uint unsignedRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        int rate = unsignedRate <= MaxSampleRate ? (int)unsignedRate : 0;
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (fmt.Length >= 18 && BinaryPrimitives.ReadUInt16LittleEndian(fmt[16..]) != fmt.Length - 18)
            throw new InvalidDataException("Invalid WAV format extension size.");
        if (format == 0xfffe)
        {
            // WAVEFORMATEXTENSIBLE with full-width PCM/float samples and the standard subtype GUID.
            if (fmt.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]) != bits ||
                !fmt.Slice(26, 14).SequenceEqual(new byte[] { 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71 }))
                throw new InvalidDataException("Unsupported WAV extensible sample layout.");
            format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }
        if (!((format == 1 && bits is 8 or 16 or 24 or 32) || (format == 3 && bits is 32 or 64)))
            throw new InvalidDataException("WAV import supports PCM8/16/24/32 and IEEE float32/64.");
        if (channels is < 1 or > MaxChannels || rate == 0 || blockAlign != channels * (bits / 8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]) != (long)rate * blockAlign || dataLength % blockAlign != 0)
            throw new InvalidDataException("Invalid WAV sample rate, channels, block alignment, or data size.");
        int frames = dataLength / blockAlign;
        ValidateMetadata(frames, rate, channels); // Check all attacker-controlled counts before allocating PCM.
        return (format, channels, rate, bits, dataOffset, frames);
    }

    private static async Task<SampleClip> ReadWavAsync(byte[] bytes, string name, CancellationToken token)
    {
        var (format, channels, rate, bits, dataOffset, frames) = ReadWavMetadata(bytes, token);
        var mono = new float[frames];
        int p = dataOffset, width = bits / 8;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 4095) == 0) token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsBrowser() && (frame & 16383) == 0) await Task.Delay(1, token).ConfigureAwait(false);
            double sum = 0;
            for (int channel = 0; channel < channels; channel++, p += width)
            {
                double value;
                if (format == 3) value = bits == 32 ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(p)) : BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(p));
                else value = bits switch
                {
                    8 => (bytes[p] - 128) / 128d,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(p)) / 32768d,
                    24 => ((bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16) << 8 >> 8) / 8388608d,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p)) / 2147483648d
                };
                if (!double.IsFinite(value)) throw new InvalidDataException("WAV contains non-finite floating-point audio.");
                sum += Math.Clamp(value, -1, 1);
            }
            mono[frame] = (float)(sum / channels);
        }
        token.ThrowIfCancellationRequested();
        return new SampleClip(mono, rate, channels, name);
    }

    private static (int Total, int Rate, int Channels) ReadQoaMetadata(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        if (bytes.Length < 16) throw new InvalidDataException("Truncated QOA header.");
        uint total = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        ulong first = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        int channels = (int)(first >> 56), rate = (int)((first >> 32) & 0xffffff);
        ValidateMetadata(total, rate, channels);
        // Validate every frame before QoaCodec.Decode allocates its interleaved PCM buffer.
        int offset = 8, covered = 0, frames = 0;
        while (covered < total)
        {
            token.ThrowIfCancellationRequested();
            if (++frames > MaxQoaFrames) throw new InvalidDataException("QOA sample import is limited to 1,024 encoded frames.");
            if (bytes.Length - offset < 8) throw new InvalidDataException("Truncated QOA frame header.");
            ulong frame = BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]);
            int count = (int)((frame >> 16) & 0xffff), size = (int)(frame & 0xffff);
            int expected = 8 + channels * 16 + ((count + 19) / 20) * channels * 8;
            if ((int)(frame >> 56) != channels || (int)((frame >> 32) & 0xffffff) != rate ||
                count is < 1 or > QoaCodec.FrameSamples || count > total - covered || size != expected || size > bytes.Length - offset)
                throw new InvalidDataException("Invalid or truncated QOA frame.");
            covered += count; offset += size;
        }
        if (offset != bytes.Length) throw new InvalidDataException("Unexpected trailing QOA data.");
        return ((int)total, rate, channels);
    }

    private static async Task<SampleClip> ReadQoaAsync(byte[] bytes, string name, CancellationToken token)
    {
        var (total, rate, channels) = ReadQoaMetadata(bytes, token);
        var mono = new float[total];
        // QOA frames store their own complete LMS state. Decode independent one-frame files through the
        // existing codec, limiting non-yielding work to 5,120 frames and never allocating full interleaved PCM.
        var frameFile = new byte[8 + 8 + 16 * channels + ((QoaCodec.FrameSamples + 19) / 20) * 8 * channels];
        "qoaf"u8.CopyTo(frameFile);
        int offset = 8, written = 0, framesDecoded = 0;
        while (written < total)
        {
            token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsBrowser() && framesDecoded % 4 == 0) await Task.Delay(1, token).ConfigureAwait(false);
            ulong header = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset));
            int count = (int)((header >> 16) & 0xffff), size = (int)(header & 0xffff);
            BinaryPrimitives.WriteUInt32BigEndian(frameFile.AsSpan(4), (uint)count);
            bytes.AsSpan(offset, size).CopyTo(frameFile.AsSpan(8));
            var pcm = QoaCodec.Decode(frameFile.AsSpan(0, size + 8), out _, out _);
            for (int frame = 0; frame < count; frame++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++) sum += pcm[frame * channels + c];
                mono[written + frame] = (float)(sum / (32768d * channels));
            }
            written += count; offset += size; framesDecoded++;
        }
        token.ThrowIfCancellationRequested();
        return new SampleClip(mono, rate, channels, name);
    }

    /// <summary>Optional desktop conversion using an explicitly configured trusted executable, never PATH or a shell.
    /// Only local files/container demuxers are allowed; output is capped 48 kHz mono with unknown original channels.</summary>
    public static async Task<SampleClip> ReadFfmpegAsync(string executablePath, string localPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Optional FFmpeg import is desktop-only.");
        if (!IsLocalAbsolutePath(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("Choose an existing trusted FFmpeg executable with an absolute local path.", executablePath);
        if (!IsLocalAbsolutePath(localPath)) throw new ArgumentException("FFmpeg import requires an absolute local file path.", nameof(localPath));
        var file = new FileInfo(localPath);
        if (!file.Exists) throw new FileNotFoundException("The sample file was not found.", localPath);
        if (file.Length is <= 0 or > MaxEncodedBytes) throw new InvalidDataException("Choose a nonempty sample file no larger than 32 MiB.");

        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (string arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-max_alloc", MaxEncodedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-protocol_whitelist", "file", "-format_whitelist", "wav,qoa,flac,mp3,ogg,mov,aac,aiff,au,ape,asf,wv,caf", "-threads", "1",
            "-i", Path.GetFullPath(localPath), "-map", "0:a:0", "-vn", "-sn", "-dn", "-t", "30.001", "-threads", "1",
            "-ac", "1", "-ar", "48000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"
        }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(FfmpegTimeoutSeconds));
        Task<byte[]>? output = null;
        Task<string>? errors = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("FFmpeg did not start.");
            process.StandardInput.Close();
            using var registration = timeout.Token.Register(() => TryKill(process));
            errors = ReadCappedErrorsAsync(process.StandardError, timeout.Token);
            output = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxSamples * sizeof(float), "Decoded audio exceeds the 30-second sample limit.", timeout.Token);
            var bytes = await output.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string error = await errors.ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException($"FFmpeg import failed ({process.ExitCode}): {error.Trim()}");
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) throw new InvalidDataException("FFmpeg returned empty or incomplete audio.");
            var mono = new float[bytes.Length / sizeof(float)];
            for (int i = 0; i < mono.Length; i++)
            {
                if ((i & 4095) == 0) timeout.Token.ThrowIfCancellationRequested();
                float value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
                if (!float.IsFinite(value)) throw new InvalidDataException("FFmpeg returned non-finite audio.");
                mono[i] = Math.Clamp(value, -1, 1);
            }
            return new SampleClip(mono, FfmpegSampleRate, 0, file.Name);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"FFmpeg import exceeded {FfmpegTimeoutSeconds} seconds.");
        }
        finally
        {
            TryKill(process);
            await timeout.CancelAsync().ConfigureAwait(false);
            // Observe both pipe tasks even when a decoder fails, exceeds a limit, or is cancelled.
            if (output is not null) { try { await output.ConfigureAwait(false); } catch (Exception) { } }
            if (errors is not null) { try { await errors.ConfigureAwait(false); } catch (Exception) { } }
        }
    }

    private static bool IsLocalAbsolutePath(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith(@"\\", StringComparison.Ordinal);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<string> ReadCappedErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            int keep = Math.Min(read, MaxErrorCharacters - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            truncated |= keep != read;
        }
        if (truncated) text.Append(" [additional decoder output omitted]");
        return text.ToString();
    }
}
