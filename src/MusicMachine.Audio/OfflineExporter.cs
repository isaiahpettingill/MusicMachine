using System.Diagnostics;
using System.Buffers.Binary;
using MusicMachine.Core;

namespace MusicMachine.Audio;

public static class OfflineExporter
{
    /// <summary>Exact musical length by default for game loops. includeTail adds the natural OFF release.
    /// A seamless waveform seam is not promised: compose matching loop endpoints or crossfade in the game.</summary>
    public static float[] Render(Song song, CancellationToken cancellationToken = default, bool includeTail = false)
    {
        var renderer = new SynthRenderer(song);
        long frames = includeTail ? renderer.TotalFrames : renderer.MusicalFrames;
        if (frames > int.MaxValue / 2) throw new InvalidOperationException("Song is too long for in-memory export.");
        var data = new float[checked((int)frames * 2)];
        for (int offset = 0; offset < data.Length; offset += 8192)
        {
            cancellationToken.ThrowIfCancellationRequested();
            renderer.Render(data.AsSpan(offset, Math.Min(8192, data.Length - offset)));
        }
        return data;
    }
    /// <summary>Writes PCM16 WAV without retaining the whole song's rendered audio.</summary>
    public static void WriteWav(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
        => WriteSongAsync(path, song, qoa: false, cooperative: false, cancellationToken, includeTail).GetAwaiter().GetResult();

    /// <summary>Cooperatively yields between bounded blocks, even on single-threaded browser runtimes.</summary>
    public static Task WriteWavAsync(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
        => WriteSongAsync(path, song, qoa: false, cooperative: true, cancellationToken, includeTail);

    public static void WriteWav(string path, ReadOnlySpan<float> stereo, int sampleRate = 48000, CancellationToken cancellationToken = default)
    {
        if ((stereo.Length & 1) != 0) throw new ArgumentException("Invalid stereo PCM.");
        uint dataSize = ValidateWavFormat(stereo.Length / 2, sampleRate);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                WriteWavHeader(stream, dataSize, sampleRate);
                var bytes = new byte[4096 * 4];
                for (int offset = 0; offset < stereo.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(bytes.Length / 2, stereo.Length - offset);
                    WriteWavBlock(stream, stereo.Slice(offset, count), bytes);
                    offset += count;
                }
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static uint ValidateWavFormat(long frames, int sampleRate)
    {
        if (sampleRate is < 1 or > int.MaxValue / 4 || frames < 0 || frames > (uint.MaxValue - 36L) / 4)
            throw new ArgumentException("PCM16 stereo WAV needs a valid sample rate and a RIFF size below 4 GiB.");
        return (uint)(frames * 4);
    }

    private static void WriteWavHeader(Stream stream, uint dataSize, int sampleRate)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36 + dataSize);
        "WAVEfmt "u8.CopyTo(header[8..]); BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate); BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4); BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]); BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataSize);
        stream.Write(header);
    }

    private static void WriteWavBlock(Stream stream, ReadOnlySpan<float> samples, Span<byte> bytes)
    {
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes[(i * 2)..], ToPcm16(samples[i]));
        stream.Write(bytes[..(samples.Length * 2)]);
    }

    /// <summary>Built-in lossless PCM16 FLAC. Streams the song in bounded 4096-frame blocks.</summary>
    public static void WriteFlac(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var renderer = new SynthRenderer(song);
        long frames = includeTail ? renderer.TotalFrames : renderer.MusicalFrames;
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var writer = new FlacEncoder.Writer(stream, frames, cancellationToken);
                var samples = new float[FlacEncoder.BlockSize * 2];
                var pcm = new short[samples.Length];
                while (renderer.PositionFrames < frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(FlacEncoder.BlockSize, frames - renderer.PositionFrames) * 2;
                    renderer.Render(samples.AsSpan(0, count));
                    for (int i = 0; i < count; i++) pcm[i] = ToPcm16(samples[i]);
                    writer.WriteBlock(pcm.AsSpan(0, count), cancellationToken);
                }
                writer.Complete(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Cooperatively yields between blocks, including on single-threaded browser runtimes.
    /// The synchronous overload remains suitable for worker threads and headless export.</summary>
    public static async Task WriteFlacAsync(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var renderer = new SynthRenderer(song);
        long frames = includeTail ? renderer.TotalFrames : renderer.MusicalFrames;
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var writer = new FlacEncoder.Writer(stream, frames, cancellationToken);
                var samples = new float[FlacEncoder.BlockSize * 2];
                var pcm = new short[samples.Length];
                while (renderer.PositionFrames < frames)
                {
                    // Task.Run alone does not create a worker on single-threaded WebAssembly.
                    // A timer yield allows UI input/cancellation to run between bounded blocks.
                    await Task.Delay(1, cancellationToken);
                    int count = (int)Math.Min(FlacEncoder.BlockSize, frames - renderer.PositionFrames) * 2;
                    renderer.Render(samples.AsSpan(0, count));
                    for (int i = 0; i < count; i++) pcm[i] = ToPcm16(samples[i]);
                    writer.WriteBlock(pcm.AsSpan(0, count), cancellationToken);
                }
                writer.Complete(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void WriteFlac(string path, ReadOnlySpan<float> stereo, int sampleRate = 48000, CancellationToken cancellationToken = default)
    {
        if (stereo.IsEmpty || (stereo.Length & 1) != 0 || sampleRate != FlacEncoder.SampleRate)
            throw new ArgumentException("Built-in FLAC needs nonempty 48 kHz stereo PCM.");
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var writer = new FlacEncoder.Writer(stream, stereo.Length / 2, cancellationToken);
                var pcm = new short[FlacEncoder.BlockSize * 2];
                for (int offset = 0; offset < stereo.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(pcm.Length, stereo.Length - offset);
                    for (int i = 0; i < count; i++) pcm[i] = ToPcm16(stereo[offset + i]);
                    writer.WriteBlock(pcm.AsSpan(0, count), cancellationToken);
                    offset += count;
                }
                writer.Complete(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Writes QOA in bounded 5120-frame blocks, preserving predictor state across frames.</summary>
    public static void WriteQoa(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
        => WriteSongAsync(path, song, qoa: true, cooperative: false, cancellationToken, includeTail).GetAwaiter().GetResult();

    /// <summary>Cooperatively yields between bounded blocks, even on single-threaded browser runtimes.</summary>
    public static Task WriteQoaAsync(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
        => WriteSongAsync(path, song, qoa: true, cooperative: true, cancellationToken, includeTail);

    private static async Task WriteSongAsync(string path, Song song, bool qoa, bool cooperative, CancellationToken cancellationToken, bool includeTail)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var renderer = new SynthRenderer(song);
        long frames = includeTail ? renderer.TotalFrames : renderer.MusicalFrames;
        // Validate representable lengths before opening even the temporary output.
        uint wavDataSize = 0;
        if (qoa) QoaCodec.ValidateFormat(frames, SynthRenderer.OutputSampleRate, 2);
        else wavDataSize = ValidateWavFormat(frames, SynthRenderer.OutputSampleRate);
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                int blockFrames = qoa ? QoaCodec.FrameSamples : 4096;
                var samples = new float[blockFrames * 2];
                var pcm = qoa ? new short[samples.Length] : [];
                var bytes = qoa ? [] : new byte[samples.Length * 2];
                var writer = qoa ? new QoaCodec.Writer(stream, frames, cancellationToken: cancellationToken) : null;
                if (!qoa) WriteWavHeader(stream, wavDataSize, SynthRenderer.OutputSampleRate);
                while (renderer.PositionFrames < frames)
                {
                    // Task.Run cannot yield to UI input on single-threaded WebAssembly. A timer can.
                    if (cooperative) await Task.Delay(1, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(blockFrames, frames - renderer.PositionFrames) * 2;
                    renderer.Render(samples.AsSpan(0, count));
                    if (writer is not null)
                    {
                        for (int i = 0; i < count; i++) pcm[i] = ToPcm16(samples[i]);
                        writer.WriteFrame(pcm.AsSpan(0, count), cancellationToken);
                    }
                    else WriteWavBlock(stream, samples.AsSpan(0, count), bytes);
                }
                writer?.Complete(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void WriteQoa(string path, ReadOnlySpan<float> stereo, int sampleRate = 48000, CancellationToken cancellationToken = default)
    {
        if ((stereo.Length & 1) != 0) throw new ArgumentException("Invalid stereo PCM.");
        QoaCodec.ValidateFormat(stereo.Length / 2, sampleRate, 2);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var writer = new QoaCodec.Writer(stream, stereo.Length / 2, sampleRate, cancellationToken: cancellationToken);
                var pcm = new short[QoaCodec.FrameSamples * 2];
                for (int offset = 0; offset < stereo.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(pcm.Length, stereo.Length - offset);
                    for (int i = 0; i < count; i++) pcm[i] = ToPcm16(stereo[offset + i]);
                    writer.WriteFrame(pcm.AsSpan(0, count), cancellationToken);
                    offset += count;
                }
                writer.Complete(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string TemporaryPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
    public static short ToPcm16(float value) => !float.IsFinite(value) ? (short)0 : (short)Math.Clamp((int)Math.Round(Math.Clamp(value, -1, 1) * 32767), -32768, 32767);

    /// <summary>Optional FFmpeg executable configured explicitly by the caller; never searches PATH or invokes a shell.</summary>
    public static async Task WriteFfmpegAsync(string executablePath, string destination, Song song, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath)) throw new FileNotFoundException("Choose an existing FFmpeg executable with an absolute path.", executablePath);
        string extension = Path.GetExtension(destination).ToLowerInvariant();
        string codec = extension switch { ".flac" => "flac", ".mp3" => "libmp3lame", ".opus" => "libopus", ".ogg" => "libvorbis", ".m4a" => "aac", _ => throw new ArgumentException("Supported optional formats: FLAC, MP3, Opus, Ogg, M4A.") };
        string fullDestination = Path.GetFullPath(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(fullDestination)!, $".{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}{extension}");
        var start = new ProcessStartInfo(executablePath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", "pipe:0", "-c:a", codec, temporary }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("FFmpeg did not start.");
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var renderer = new SynthRenderer(song); var samples = new float[4096]; var bytes = new byte[4096 * sizeof(float)];
            while (renderer.PositionFrames < renderer.MusicalFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(samples.Length / 2, renderer.MusicalFrames - renderer.PositionFrames);
                renderer.Render(samples.AsSpan(0, count * 2));
                for (int i = 0; i < count * 2; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), samples[i]);
                await process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, count * 8), cancellationToken);
            }
            process.StandardInput.Close(); await process.WaitForExitAsync(cancellationToken);
            string error = await stderr; await stdout;
            if (process.ExitCode != 0) throw new IOException($"FFmpeg export failed ({process.ExitCode}): {error.Trim()}");
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
