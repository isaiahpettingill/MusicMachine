using System.Runtime.InteropServices.JavaScript;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Browser;

/// <summary>
/// Pre-renders an immutable synth snapshot in bounded chunks, then gives Web Audio ownership of
/// the PCM. Playback, looping and device-rate conversion run in the browser's audio engine.
/// No managed callback, SDL library, or allocation runs on the audio rendering thread.
/// </summary>
public sealed partial class BrowserAudioPlayer : IAudioPlayer
{
    // A five-minute stereo float buffer uses ~110 MiB in Web Audio. The managed side only keeps
    // one 32 KiB chunk. Longer arrangements can be exported or played with the native application.
    public const int MaximumBufferedSeconds = 300;
    public const int MaximumPreparationSeconds = 900;
    private const int ChunkFrames = 4096;
    private int _buffer;
    private long _origin, _lastPosition;
    private bool _disposed;
    public int SampleRate => SynthRenderer.OutputSampleRate;
    public bool IsPlaying => !_disposed && _buffer != 0 && GetPlaying(_buffer);
    public long PositionFrames => !_disposed && _buffer != 0
        ? _origin + (long)GetPosition(_buffer) : _lastPosition;
    public string? LastError => !_disposed && _buffer != 0 ? GetError(_buffer) : null;

    public void Play(Song song, long startFrame = 0, bool loop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(song);
        Stop();
        var renderer = new SynthRenderer(song);
        long loopStart = 0, loopEnd = renderer.MusicalFrames;
        if (loop && song.Arrangement.Count > 0)
        {
            long rows = 0;
            int first = Math.Clamp(song.LoopStartSection, 0, song.Arrangement.Count - 1);
            int end = Math.Clamp(song.LoopEndSection, first + 1, song.Arrangement.Count);
            for (int sectionIndex = 0; sectionIndex < end; sectionIndex++)
            {
                if (sectionIndex == first) loopStart = (long)Math.Round(rows * renderer.FramesPerRow);
                var section = song.Arrangement[sectionIndex];
                if (song.FindPattern(section.PatternId) is { } pattern)
                    rows += (long)Math.Clamp(pattern.Length, 1, 4096) * Math.Clamp(section.Repeats, 1, 256);
            }
            loopEnd = Math.Min(renderer.MusicalFrames, (long)Math.Round(rows * renderer.FramesPerRow));
        }
        if (loop && loopEnd <= loopStart)
            throw new InvalidOperationException("The loop has no notes or rows. Add a pattern to the arrangement or turn Loop off.");
        long start = loop ? Math.Clamp(startFrame, loopStart, loopEnd - 1)
            : Math.Clamp(startFrame, 0, renderer.TotalFrames);
        long origin = loop ? loopStart : start;
        long length = (loop ? loopEnd : renderer.TotalFrames) - origin;
        if (length <= 0) { _lastPosition = start; return; }
        if (origin + length > (long)SampleRate * MaximumPreparationSeconds)
            throw new InvalidOperationException($"Browser playback can prepare the first {MaximumPreparationSeconds / 60} minutes of an arrangement. Use MusicMachine for Windows or Linux to play later positions, or export the song.");
        if (length > (long)SampleRate * MaximumBufferedSeconds)
            throw new InvalidOperationException($"Browser playback can buffer up to {MaximumBufferedSeconds / 60} minutes at once. Select a shorter loop, export the song, or use MusicMachine for Windows or Linux.");
        try
        {
            _buffer = CreateBuffer(checked((int)length), SampleRate);
            _origin = origin;
            renderer.Seek(origin);
            var samples = new float[ChunkFrames * 2];
            var bytes = new byte[samples.Length * sizeof(float)];
            int written = 0;
            while (written < length)
            {
                int count = (int)Math.Min(ChunkFrames, length - written);
                renderer.Render(samples.AsSpan(0, count * 2));
                Buffer.BlockCopy(samples, 0, bytes, 0, count * 2 * sizeof(float));
                WriteBuffer(_buffer, bytes, written, count);
                written += count;
            }
            StartBuffer(_buffer, checked((int)(start - origin)), loop);
            _lastPosition = start;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        if (_disposed || _buffer == 0) return;
        _lastPosition = PositionFrames;
        ReleaseBuffer(_buffer);
        _buffer = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    [JSImport("audio.createBuffer", "musicmachine")]
    private static partial int CreateBuffer(int frames, int sampleRate);
    [JSImport("audio.writeBuffer", "musicmachine")]
    private static partial void WriteBuffer(int id, byte[] samples, int offset, int frames);
    [JSImport("audio.start", "musicmachine")]
    private static partial void StartBuffer(int id, int startFrame, bool loop);
    [JSImport("audio.release", "musicmachine")]
    private static partial void ReleaseBuffer(int id);
    [JSImport("audio.isPlaying", "musicmachine")]
    private static partial bool GetPlaying(int id);
    [JSImport("audio.position", "musicmachine")]
    private static partial double GetPosition(int id);
    [JSImport("audio.error", "musicmachine")]
    private static partial string? GetError(int id);
}
