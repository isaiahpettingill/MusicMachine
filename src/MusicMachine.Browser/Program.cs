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
        EditorPlatform.ProjectStorage = new BrowserProjectStorage();
        EditorPlatform.ConvertAudioToWaveAsync = BrowserAudioConverter.ConvertAsync;
        return AppBuilder.Configure<MusicMachine.App.App>()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }

    [JSImport("preferences.load", "musicmachine")]
    private static partial string? LoadPreferences();
    [JSImport("preferences.save", "musicmachine")]
    private static partial void SavePreferences(string text);

}
