using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Browser;
using MusicMachine.App;
using MusicMachine.Audio;

namespace MusicMachine.Browser;

internal static partial class Program
{
    private static Task Main(string[] args)
    {
        // Register before App creates its shared MainView. SDL is never used in the browser.
        EditorPlatform.LoadPreferences = LoadPreferences;
        EditorPlatform.SavePreferences = SavePreferences;
        AudioServices.CreatePlayer = static () => new BrowserAudioPlayer();
        EditorPlatform.LoadRecoveryAsync = async () =>
        {
            string? saved = await LoadRecovery();
            return saved is null ? null : Convert.FromBase64String(saved);
        };
        EditorPlatform.SaveRecoveryAsync = bytes => SaveRecovery(Convert.ToBase64String(bytes));
        EditorPlatform.ClearRecoveryAsync = ClearRecovery;
        EditorPlatform.ConvertAudioToWaveAsync = BrowserAudioConverter.ConvertAsync;
        return AppBuilder.Configure<MusicMachine.App.App>()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }

    [JSImport("preferences.load", "musicmachine")]
    private static partial string? LoadPreferences();
    [JSImport("preferences.save", "musicmachine")]
    private static partial void SavePreferences(string text);

    [JSImport("recovery.load", "musicmachine")]
    private static partial Task<string?> LoadRecovery();
    [JSImport("recovery.save", "musicmachine")]
    private static partial Task SaveRecovery(string base64);
    [JSImport("recovery.clear", "musicmachine")]
    private static partial Task ClearRecovery();
}
