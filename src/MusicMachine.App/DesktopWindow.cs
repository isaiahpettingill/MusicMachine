using Avalonia.Controls;
using Avalonia.Threading;
namespace MusicMachine.App;
public sealed class MainWindow : Window
{
    private readonly MainView view;
    private bool closingApproved, deciding;
    public MainWindow(string? path = null)
    {
        Width = 1400; Height = 890; MinWidth = 820; MinHeight = 600; Title = "MusicMachine";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MusicMachine.App/Assets/musicmachine.ico")));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = view = new MainView(path);
        Closing += async (_, e) =>
        {
            if (closingApproved) return;
            e.Cancel = true; if (deciding) return; deciding = true;
            try { if (await view.RequestCloseAsync()) { closingApproved = true; Dispatcher.UIThread.Post(Close); } }
            finally { deciding = false; }
        };
    }
}
