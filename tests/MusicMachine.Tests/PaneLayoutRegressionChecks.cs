using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class PaneLayoutRegressionChecks
{
    internal static void Run()
    {
        var data = Path.Combine(Path.GetTempPath(), "musicmachine-panes-" + Guid.NewGuid().ToString("N"));
        var oldData = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA");
        var oldLoad = EditorPlatform.LoadPreferences; var oldSave = EditorPlatform.SavePreferences; var oldStorage = EditorPlatform.ProjectStorage;
        var preferences = "library=false\ninspector=true\ninspectorContent=fx\ninspectorWidth=400\nworkspace=Tracker\n";
        Window? window = null; MainView? view = null;
        try
        {
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", data);
            EditorPlatform.LoadPreferences = () => preferences; EditorPlatform.SavePreferences = value => preferences = value; EditorPlatform.ProjectStorage = null;
            view = new MainView(); window = new Window { Width = 1180, Height = 812, Content = view }; window.Show(); Pump(); window.UpdateLayout();
            var panes = Named<Grid>(view, "WorkspacePanes"); var splitter = Named<GridSplitter>(view, "InspectorSplitter");
            Assert.True(splitter.IsVisible); Assert.InRange(panes.ColumnDefinitions[3].ActualWidth, 399, 401);
            Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
            var tracker = Field<TrackerGrid>(view, "tracker"); var editor = Field<SongEditor>(view, "editor");
            // The editor stays at the top of a tall viewport and retains compact columns.
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, tracker.VerticalAlignment);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, tracker.HorizontalAlignment);
            Assert.Equal(TrackerGrid.Gutter + editor.Song.Tracks.Count * (TrackerGrid.NoteWidth + tracker.EffectColumns * TrackerGrid.EffectWidth), tracker.Width);
            Assert.InRange(tracker.Bounds.Y, 0, 1);
            Assert.Equal(SongLimits.MaxRows, Named<NumericUpDown>(view, "PatternRows").Maximum);
            tracker.Select(31, 0);
            tracker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }); Pump();
            Assert.Equal(64, editor.Song.Patterns[0].Length); Assert.Equal(32, tracker.SelectedRow);
            Assert.All(editor.Song.Patterns[0].Tracks, t => Assert.Equal(64, t.Rows.Count));
            Invoke(view, "Undo"); Pump(); Assert.Equal(32, editor.Song.Patterns[0].Length);
            Invoke(view, "ResizePattern", SongLimits.MaxRows); Pump();
            tracker.Select(SongLimits.MaxRows - 1, 0);
            tracker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }); Pump();
            Assert.Equal(SongLimits.MaxRows, editor.Song.Patterns[0].Length); Assert.Equal(SongLimits.MaxRows - 1, tracker.SelectedRow);
            Invoke(view, "Undo"); Pump();
            tracker.Select(2, 1); var before = SongFile.Write(editor.Song);
            splitter.Focus();
            splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left }); window.UpdateLayout();
            splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Left }); Pump();
            Assert.InRange(panes.ColumnDefinitions[3].ActualWidth, 415, 417);
            Assert.Contains("inspectorWidth=416", preferences);
            Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(before, SongFile.Write(editor.Song));
            Invoke(view, "TogglePane", false); window.UpdateLayout(); Assert.False(splitter.IsVisible); Assert.Equal(0, panes.ColumnDefinitions[3].ActualWidth);
            Invoke(view, "TogglePane", false); window.UpdateLayout(); Assert.InRange(panes.ColumnDefinitions[3].ActualWidth, 415, 417);
            // Shrinking keeps a usable center, without forgetting the requested width.
            Invoke(view, "TogglePane", true); window.Width = 820; Pump(); window.UpdateLayout();
            Assert.InRange(panes.ColumnDefinitions[3].ActualWidth, 238, 314.5); Assert.True(panes.ColumnDefinitions[1].ActualWidth >= 319);
            window.Width = 1180; Pump(); window.UpdateLayout(); Assert.InRange(panes.ColumnDefinitions[3].ActualWidth, 415, 417);
            Assert.Contains("inspectorWidth=416", preferences);
            var controls = view.GetLogicalDescendants().OfType<Button>().Select(x => x.Name).ToArray();
            Assert.Contains("TrackerAddTrack", controls); Assert.Contains("TrackerAddFx", controls);
            Assert.Contains("TrackerAddToSong", controls); Assert.Contains("TrackerOpenSong", controls);
            window.Content = null; window.Close(); view = new MainView(); window = new Window { Width = 1180, Height = 812, Content = view }; window.Show(); Pump(); window.UpdateLayout();
            Assert.InRange(Named<Grid>(view, "WorkspacePanes").ColumnDefinitions[3].ActualWidth, 415, 417);
        }
        finally
        {
            if (window is not null) { window.Content = null; window.Close(); }
            EditorPlatform.LoadPreferences = oldLoad; EditorPlatform.SavePreferences = oldSave; EditorPlatform.ProjectStorage = oldStorage;
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", oldData); if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
    private static T Named<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Pump() { for (var n = 0; n < 4; n++) Dispatcher.UIThread.RunJobs(); }
}
