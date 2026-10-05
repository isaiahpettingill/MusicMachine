namespace MusicMachine.App;
public static class EditorPlatform
{
    public static Func<string?>? LoadPreferences { get; set; }
    public static Action<string>? SavePreferences { get; set; }
    // The browser host supplies durable IndexedDB storage; native uses atomic local files.
    public static Func<Task<byte[]?>>? LoadRecoveryAsync { get; set; }
    public static Func<byte[], Task>? SaveRecoveryAsync { get; set; }
    public static Func<Task>? ClearRecoveryAsync { get; set; }
    // Optional host converter. Native WAV/QOA imports bypass this hook entirely.
    public static Func<string, byte[], CancellationToken, Task<byte[]>>? ConvertAudioToWaveAsync { get; set; }
}
