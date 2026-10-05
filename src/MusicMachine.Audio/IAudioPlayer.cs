using MusicMachine.Core;
namespace MusicMachine.Audio;
public interface IAudioPlayer : IDisposable
{
    void Play(Song song, long startFrame = 0, bool loop = false);
    void Stop();
    bool IsPlaying { get; }
    long PositionFrames { get; }
    int SampleRate { get; }
    string? LastError { get; }
}
public static class AudioServices
{
    public static Func<IAudioPlayer> CreatePlayer { get; set; } = () => new AudioPlayer();
}
