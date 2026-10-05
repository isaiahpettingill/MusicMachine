using System.Diagnostics;
using System.Text;
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
    public static void WriteWav(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
        => WriteWav(path, Render(song, cancellationToken, includeTail), 48000, cancellationToken);
    public static void WriteWav(string path, ReadOnlySpan<float> stereo, int sampleRate = 48000, CancellationToken cancellationToken = default)
    {
        if ((stereo.Length & 1) != 0 || sampleRate <= 0) throw new ArgumentException("Invalid stereo PCM.");
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                uint dataSize = checked((uint)stereo.Length * 2);
                writer.Write("RIFF"u8); writer.Write(checked(36u + dataSize)); writer.Write("WAVEfmt "u8);
                writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write(sampleRate);
                writer.Write(checked(sampleRate * 4)); writer.Write((ushort)4); writer.Write((ushort)16);
                writer.Write("data"u8); writer.Write(dataSize);
                for (int i = 0; i < stereo.Length; i++)
                {
                    if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(ToPcm16(stereo[i]));
                }
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void WriteQoa(string path, Song song, CancellationToken cancellationToken = default, bool includeTail = false)
        => WriteQoa(path, Render(song, cancellationToken, includeTail), 48000, cancellationToken);
    public static void WriteQoa(string path, ReadOnlySpan<float> stereo, int sampleRate = 48000, CancellationToken cancellationToken = default)
    {
        var pcm = new short[stereo.Length]; for (int i = 0; i < pcm.Length; i++) pcm[i] = ToPcm16(stereo[i]);
        var bytes = QoaCodec.Encode(pcm, sampleRate, 2, cancellationToken);
        string fullPath = Path.GetFullPath(path), temporary = TemporaryPath(fullPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); File.WriteAllBytes(temporary, bytes);
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
