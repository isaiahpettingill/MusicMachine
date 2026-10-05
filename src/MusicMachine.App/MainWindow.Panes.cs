using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using IconPacks.Avalonia.Material;
using MusicMachine.Core;

namespace MusicMachine.App;

public sealed partial class MainView
{
    private GridSplitter? inspectorSplitter;
    private GridSplitter BuildInspectorSplitter()
    {
        var splitter = new GridSplitter
        {
            Name = "InspectorSplitter", Width = 6, Background = Ui.Line,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ShowsPreview = false, KeyboardIncrement = 16, DragIncrement = 1, Focusable = true
        };
        AutomationProperties.SetName(splitter, "Resize right pane");
        ToolTip.SetTip(splitter, "Drag to resize the right pane · focus and use Left/Right for keyboard resizing");
        splitter.AddHandler(Thumb.DragCompletedEvent, (_, _) => PersistInspectorWidth(), RoutingStrategies.Bubble, true);
        splitter.KeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right) PersistInspectorWidth(); };
        return splitter;
    }
    private void PersistInspectorWidth()
    {
        if (panes is null || inspectorPane?.IsVisible != true) return;
        var width = panes.ColumnDefinitions[3].ActualWidth;
        if (!double.IsFinite(width) || width < 238) return;
        viewSettings.InspectorWidth = Math.Clamp(width, 238, 1600);
        SaveViewSettings();
    }
    private void UpdatePaneLayout()
    {
        if (panes is null) return;
        var inspect = viewSettings.Inspector && (mode != "Instrument" || ShowingFxReference);
        var available = panes.Bounds.Width > 0 ? panes.Bounds.Width : Math.Max(820, Bounds.Width);
        var libraryWidth = viewSettings.Library ? 180 : 0;
        var maximum = Math.Max(238, available - libraryWidth - 6 - 320);
        panes.ColumnDefinitions[0].Width = new GridLength(libraryWidth);
        panes.ColumnDefinitions[1].MinWidth = 320;
        panes.ColumnDefinitions[2].Width = new GridLength(inspect ? 6 : 0);
        var right = panes.ColumnDefinitions[3];
        right.MinWidth = inspect ? 238 : 0; right.MaxWidth = inspect ? maximum : double.PositiveInfinity;
        right.Width = new GridLength(inspect ? Math.Clamp(viewSettings.InspectorWidth, 238, maximum) : 0);
        if (libraryPane is not null) libraryPane.IsVisible = viewSettings.Library;
        if (inspectorPane is not null) inspectorPane.IsVisible = inspect;
        if (inspectorSplitter is not null) inspectorSplitter.IsVisible = inspect;
    }
    private static Button TrackerCommand(string label, PackIconMaterialKind icon, Action action, string help, string name)
    {
        var button = Ui.Button(label, action, help); button.Name = name; button.Margin = new(0, 0, 6, 3); button.Padding = new(8, 4);
        button.Content = Ui.Row(new PackIconMaterial { Kind = icon, Width = 14, Height = 14 }, Ui.Label(label, 11));
        return button;
    }
    private void AddEffectColumn()
    {
        if (!tracker.CommitPending()) return;
        SetEffectColumns(Math.Min(FxParser.MaxColumns, tracker.EffectColumns + 1));
        SetStatus($"{tracker.EffectColumns} visible FX columns per track · all run on the same row · F2 opens the effect helper");
        Refresh(true);
    }
}
