using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MusicMachine.Core;

namespace MusicMachine.Audio;

/// <summary>SDL2 float32 callback player, with no NuGet/native wrapper dependency. Install SDL2 for your OS.
/// Editing the original Song after Play does not change the immutable playback snapshot. Stop/Play to apply edits.</summary>
public sealed unsafe class AudioPlayer : IAudioPlayer
{
    private readonly object _control = new();
    private uint _device;
    private GCHandle _handle;
    private SynthRenderer? _renderer;
    private int _playing;
    private long _position;
    private bool _initialized, _disposed, _loop;
    private long _loopStart, _loopEnd;
    private Action? _restoreLoop;
    public bool IsPlaying => Volatile.Read(ref _playing) != 0;
    public long PositionFrames => Interlocked.Read(ref _position);
    public int SampleRate => SynthRenderer.OutputSampleRate;
    public string? LastError { get; private set; }

    /// <summary>startFrame reconstructs prior synth state. Loop repeats the selected arrangement sections.
    /// Loop restores deterministic precompiled voice/FX state captured before playback, without callback allocation.</summary>
    public void Play(Song song, long startFrame = 0, bool loop = false)
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore(); LastError = null;
            var renderer = new SynthRenderer(song);
            if (!Sdl.Load()) throw new InvalidOperationException("SDL2 audio library was not found. Install SDL2 (Linux: libsdl2-2.0-0; macOS: brew install sdl2; Windows: SDL2.dll beside the app). WAV and QOA exports work without an audio device.");
            try
            {
                if (Sdl.Init(0x10) < 0) throw new InvalidOperationException($"Could not initialize audio: {Sdl.Error()}");
                _initialized = true; _renderer = renderer; _loop = loop; _loopStart = 0; _loopEnd = renderer.MusicalFrames;
                if (loop && song.Arrangement.Count > 0)
                {
                    long rows = 0;
                    for (int i = 0; i < Math.Clamp(song.LoopEndSection, 1, song.Arrangement.Count); i++)
                    {
                        var section = song.Arrangement[i]; var pattern = song.FindPattern(section.PatternId);
                        if (i == Math.Clamp(song.LoopStartSection, 0, song.Arrangement.Count - 1)) _loopStart = (long)Math.Round(rows * renderer.FramesPerRow);
                        if (pattern is not null) rows += (long)Math.Clamp(pattern.Length, 1, 4096) * Math.Clamp(section.Repeats, 1, 256);
                    }
                    _loopEnd = Math.Max(1, (long)Math.Round(rows * renderer.FramesPerRow));
                }
                if (loop) { renderer.Seek(_loopStart); _restoreLoop = renderer.CaptureRestorePoint(); renderer.Reset(); }
                renderer.Seek(loop ? Math.Clamp(startFrame, _loopStart, Math.Max(_loopStart, _loopEnd - 1)) : startFrame);
                _handle = GCHandle.Alloc(this);
                var spec = new Sdl.AudioSpec
                {
                    Frequency = SampleRate, Format = BitConverter.IsLittleEndian ? (ushort)0x8120 : (ushort)0x9120,
                    Channels = 2, Samples = 512, Callback = &Callback, UserData = GCHandle.ToIntPtr(_handle)
                };
                _device = Sdl.Open(null, 0, &spec, null, 0);
                if (_device == 0) throw new InvalidOperationException($"No usable audio output device: {Sdl.Error()}. Check your output device and restart playback; exports remain available.");
                Interlocked.Exchange(ref _position, renderer.PositionFrames); Volatile.Write(ref _playing, 1);
                Sdl.Pause(_device, 0);
            }
            catch (Exception e) { LastError = e.Message; StopCore(); throw; }
        }
    }
    public void Stop() { lock (_control) StopCore(); }
    private void StopCore()
    {
        Volatile.Write(ref _playing, 0);
        if (_device != 0) { Sdl.Pause(_device, 1); Sdl.Close(_device); _device = 0; }
        // SDL_CloseAudioDevice waits for any in-flight callback before userdata is freed.
        if (_handle.IsAllocated) _handle.Free();
        _renderer = null; _restoreLoop = null;
        if (_initialized) { Sdl.Quit(0x10); _initialized = false; }
    }
    public void Dispose() { lock (_control) { if (_disposed) return; StopCore(); _disposed = true; } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(nint userdata, byte* stream, int length)
    {
        var samples = new Span<float>(stream, length / sizeof(float));
        samples.Clear();
        try
        {
            if (GCHandle.FromIntPtr(userdata).Target is not AudioPlayer player || !player.IsPlaying || player._renderer is not { } renderer) return;
            int offset = 0;
            while (offset < samples.Length)
            {
                if (player._loop && renderer.PositionFrames >= player._loopEnd) player._restoreLoop!();
                int frames = player._loop ? (int)Math.Min((samples.Length - offset) / 2, player._loopEnd - renderer.PositionFrames) : (samples.Length - offset) / 2;
                if (frames <= 0) break;
                int rendered = renderer.Render(samples.Slice(offset, frames * 2)); offset += frames * 2;
                if (rendered < frames || !player._loop && renderer.PositionFrames >= renderer.TotalFrames) { Volatile.Write(ref player._playing, 0); break; }
            }
            Interlocked.Exchange(ref player._position, renderer.PositionFrames);
        }
        catch { samples.Clear(); if (GCHandle.FromIntPtr(userdata).Target is AudioPlayer p) { p.LastError = "Audio rendering stopped unexpectedly."; Volatile.Write(ref p._playing, 0); } }
    }
    private static class Sdl
    {
        [StructLayout(LayoutKind.Sequential)] public struct AudioSpec
        {
            public int Frequency; public ushort Format; public byte Channels, Silence; public ushort Samples, Padding; public uint Size;
            public delegate* unmanaged[Cdecl]<nint, byte*, int, void> Callback; public nint UserData;
        }
        private static nint _library;
        public static delegate* unmanaged[Cdecl]<uint, int> Init;
        public static delegate* unmanaged[Cdecl]<uint, void> Quit;
        public static delegate* unmanaged[Cdecl]<byte*, int, AudioSpec*, AudioSpec*, int, uint> Open;
        public static delegate* unmanaged[Cdecl]<uint, int, void> Pause;
        public static delegate* unmanaged[Cdecl]<uint, void> Close;
        private static delegate* unmanaged[Cdecl]<nint> _getError;
        private static readonly object LoadLock = new();
        public static bool Load()
        {
            lock (LoadLock)
            {
                if (_library != 0) return true;
                string[] names = OperatingSystem.IsWindows() ? ["SDL2.dll"] : OperatingSystem.IsMacOS() ? ["libSDL2.dylib", "/opt/homebrew/lib/libSDL2.dylib", "/usr/local/lib/libSDL2.dylib"] : ["libSDL2-2.0.so.0", "libSDL2.so"];
                foreach (string name in names) if (NativeLibrary.TryLoad(name, out _library)) break;
                if (_library == 0) return false;
                Init = (delegate* unmanaged[Cdecl]<uint, int>)NativeLibrary.GetExport(_library, "SDL_InitSubSystem");
                Quit = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(_library, "SDL_QuitSubSystem");
                Open = (delegate* unmanaged[Cdecl]<byte*, int, AudioSpec*, AudioSpec*, int, uint>)NativeLibrary.GetExport(_library, "SDL_OpenAudioDevice");
                Pause = (delegate* unmanaged[Cdecl]<uint, int, void>)NativeLibrary.GetExport(_library, "SDL_PauseAudioDevice");
                Close = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(_library, "SDL_CloseAudioDevice");
                _getError = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(_library, "SDL_GetError");
                return true;
            }
        }
        public static string Error() => Marshal.PtrToStringUTF8(_getError()) ?? "Unknown SDL audio error";
    }
}
