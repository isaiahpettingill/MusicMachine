using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
namespace MusicMachine.App;
internal sealed class EditorDialog : ContentControl
{
    public string Title { get; set; } = "MusicMachine";
    public bool CanResize { get; set; }
    public SizeToContent SizeToContent { get; set; }
    public WindowStartupLocation WindowStartupLocation { get; set; }
    public IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;
    public event EventHandler? Closing;
    private Window? native;
    private Border? backdrop;
    private MainView? owner;
    private TaskCompletionSource<object?>? completion;
    private bool closing;
    public EditorDialog() { Background = Ui.Surface; BorderBrush = Ui.Line; BorderThickness = new Thickness(1); CornerRadius = new(7); KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle); }
    public async Task<T> ShowDialog<T>(MainView view)
    {
        owner = view;
        if (!OperatingSystem.IsBrowser() && TopLevel.GetTopLevel(view) is Window window)
        {
            native = new Window { Title = Title, Width = Width, CanResize = CanResize, SizeToContent = SizeToContent, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = this };
            native.Closing += (_, _) => { if (!closing) { closing = true; Closing?.Invoke(this, EventArgs.Empty); } };
            var value = await native.ShowDialog<object?>(window); view.RestoreInputFocus(); return value is T result ? result : default!;
        }
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MaxHeight = Math.Max(200, view.Bounds.Height - 40); MaxWidth = Math.Max(260, view.Bounds.Width - 40); HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), Child = this }; backdrop.ZIndex = 1000; view.OverlayRoot.Children.Add(backdrop);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }; Focusable = true; Focus();
        var response = await completion.Task; view.RestoreInputFocus(); return response is T result2 ? result2 : default!;
    }
    public Task ShowDialog(MainView view) => ShowDialog<object?>(view);
    public void Close(object? result = null)
    {
        if (closing) return; closing = true; Closing?.Invoke(this, EventArgs.Empty);
        if (native is not null) { native.Close(result); return; }
        if (backdrop is not null) { backdrop.Child = null; owner?.OverlayRoot.Children.Remove(backdrop); }
        completion?.TrySetResult(result);
    }
}
