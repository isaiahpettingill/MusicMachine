namespace MusicMachine.App;
public static class EditorPlatform
{
    public static Func<string?>? LoadPreferences { get; set; }
    public static Action<string>? SavePreferences { get; set; }
    // Browser storage holds clean project snapshots separately from unsaved recovery copies.
    public static IProjectStorage? ProjectStorage { get; set; }
    // Optional host converter. Native WAV/QOA imports bypass this hook entirely.
    public static Func<string, byte[], CancellationToken, Task<byte[]>>? ConvertAudioToWaveAsync { get; set; }
}
