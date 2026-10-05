using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MusicMachine.Core;

namespace MusicMachine.App;

public sealed partial class MainView
{
    private FxReferencePanel? fxReferencePanel;
    private ComboBox? inspectorPicker;
    private MenuItem? fxReferenceMenu;
    private bool ShowingFxReference => viewSettings.InspectorContent == "fx";

    private Control BuildInspectorShell()
    {
        var body = new Grid { RowDefinitions = new("34,*") };
        inspectorPicker = new ComboBox
        {
            Name = "InspectorContentPicker", ItemsSource = new[] { "Instrument", "FX reference" },
            SelectedIndex = ShowingFxReference ? 1 : 0, FontSize = 12,
            Margin = new Thickness(8, 3), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(inspectorPicker, "Right pane content");
        inspectorPicker.SelectionChanged += (_, _) =>
        {
            if (refreshing || inspectorPicker.SelectedIndex < 0) return;
            var requested = inspectorPicker.SelectedIndex == 1 ? "fx" : "instrument";
            if (requested == viewSettings.InspectorContent) return;
            if (!tracker.CommitPending()) { UpdateReferenceContext(); RestoreInputFocus(); return; }
            viewSettings.InspectorContent = requested;
            SaveViewSettings(); Refresh();
            if (ShowingFxReference) Dispatcher.UIThread.Post(() => fxReferencePanel?.FocusSearch(), DispatcherPriority.Background);
        };
        body.Children.Add(inspectorPicker); Grid.SetRow(inspectorHost, 1); body.Children.Add(inspectorHost);
        tracker.SelectionChanged += (_, _) => UpdateReferenceContext();
        KeyDown += (_, e) =>
        {
            if (e.Handled || OverlayRoot.Children.Count > 1) return;
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (e.Key == Key.F2 && !ctrl) { ShowFxReference(); e.Handled = true; }
            else if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.D)
            { _ = LoadDemo(); e.Handled = true; }
        };
        return body;
    }

    private void ShowFxReference()
    {
        if (!tracker.CommitPending()) return;
        viewSettings.InspectorContent = "fx"; viewSettings.Inspector = true;
        SaveViewSettings(); Refresh();
        Dispatcher.UIThread.Post(() => fxReferencePanel?.FocusSearch(), DispatcherPriority.Background);
    }

    private void UpdateReferenceContext()
    {
        if (inspectorPicker is not null) inspectorPicker.SelectedIndex = ShowingFxReference ? 1 : 0;
        if (fxReferenceMenu is not null) fxReferenceMenu.IsChecked = viewSettings.Inspector && ShowingFxReference;
        if (fxReferencePanel is null) return;
        var track = editor.Song.Tracks.ElementAtOrDefault(tracker.SelectedTrack);
        var pattern = editor.Song.FindPattern(activePattern);
        bool enabled = mode == "Tracker" && track is not null && pattern is not null && tracker.SelectedRow < pattern.Length;
        fxReferencePanel.SetContext(enabled
            ? $"{track!.Name} · row {tracker.SelectedRow + 1:00} · {(tracker.SelectedColumn == 0 ? "same-row FX" : "FX " + tracker.SelectedColumn)}"
            : "Select a tracker cell to insert an effect", enabled);
    }

    private void InsertReferenceEffect(string code)
    {
        if (mode != "Tracker") { SetStatus("Choose a tracker cell before inserting an effect"); return; }
        if (!tracker.CommitPending()) { RestoreInputFocus(); return; }
        if (!FxParser.TryParse(code, out var effect, out var error) || effect.IsEmpty) { SetStatus(error.Length > 0 ? error : "Choose an effect first"); return; }
        int row = tracker.SelectedRow, track = tracker.SelectedTrack, selectedColumn = tracker.SelectedColumn;
        var trackId = editor.Song.Tracks.ElementAtOrDefault(track)?.Id;
        var note = trackId is null ? null : editor.Song.FindPattern(activePattern)?.Tracks.FirstOrDefault(t => t.TrackId == trackId)?.Rows.ElementAtOrDefault(row);
        if (note is null) return;
        int column = selectedColumn - 1;
        if (selectedColumn == 0)
        {
            // A note-cell selection never overwrites the pitch or consumes another row.
            // Reusing this command updates its existing slot; a new command uses a free slot.
            column = note.Effects.FindIndex(value => FxParser.TryParse(value, out var existing, out _) && existing.Code == effect.Code);
            if (column < 0) column = note.Effects.FindIndex(value => FxParser.TryParse(value, out var existing, out _) && existing.IsEmpty);
            if (column < 0) column = note.Effects.Count;
        }
        if (column >= FxParser.MaxColumns) { SetStatus("This row has no free FX column. Select an effect cell to replace it."); return; }
        var previousColumns = tracker.EffectColumns;
        tracker.EffectColumns = Math.Max(previousColumns, column + 1);
        tracker.SetSong(editor.Song, activePattern); tracker.Select(row, track, column + 1);
        if (tracker.CommitText(effect.ToString()))
        {
            // Keep the insertion point; Enter is the user's explicit move to the next row.
            tracker.Select(row, track, column + 1); UpdateSelection();
            if (tracker.EffectColumns != previousColumns)
            {
                viewSettings.EffectColumns = tracker.EffectColumns;
                SaveViewSettings();
            }
            SetStatus($"Inserted {effect} at row {row + 1:00}, FX {column + 1} · same time slot · Ctrl+Z to undo");
        }
        else if (tracker.EffectColumns != previousColumns)
        {
            // A rejected command must not leave an unpersisted expansion or move to another logical track.
            tracker.EffectColumns = previousColumns;
            tracker.SetSong(editor.Song, activePattern); tracker.Select(row, track, selectedColumn);
        }
        UpdateReferenceContext(); RestoreInputFocus();
    }
}
