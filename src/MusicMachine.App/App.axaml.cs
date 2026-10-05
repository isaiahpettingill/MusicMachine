using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace MusicMachine.App;
public partial class App : Application
{
    public override void Initialize() { AvaloniaXamlLoader.Load(this); EditorThemes.Apply(EditorThemes.Default); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault(p => p.EndsWith(".song", StringComparison.OrdinalIgnoreCase)));
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = new MainView();
        base.OnFrameworkInitializationCompleted();
    }
}
