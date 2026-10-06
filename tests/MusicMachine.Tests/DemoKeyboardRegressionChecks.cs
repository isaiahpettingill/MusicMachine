using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class DemoKeyboardRegressionChecks
{
    internal static void Run()
    {
        var previousLoad = EditorPlatform.LoadPreferences; var previousSave = EditorPlatform.SavePreferences;
        var previousStorage = EditorPlatform.ProjectStorage;
        Window? window = null; MainView? view = null;
        try
        {
            var store = new MemoryProjectStorage(true);
            EditorPlatform.ProjectStorage = store;
            EditorPlatform.LoadPreferences = () => "library=true\ninspector=false\ncheckForUpdates=false\n";
            EditorPlatform.SavePreferences = _ => { };
            view = new MainView(); window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var tracker = Field<TrackerGrid>(view, "tracker"); var editor = Field<SongEditor>(view, "editor");
            Assert.False(Field<bool>(view, "projectChangeBusy")); Assert.False(editor.IsDirty);
            // The browser smoke refocuses this tracker region twice quickly. Avalonia
            // derives its own double-click count, even when CDP sends clickCount: 1.
            var point = tracker.TranslatePoint(new Point(TrackerGrid.Gutter + TrackerGrid.NoteWidth + TrackerGrid.EffectWidth + 10,
                TrackerGrid.HeaderHeight + TrackerGrid.RowHeight + 14), window)!.Value;
            for (var click = 0; click < 2; click++) { window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); }
            Assert.Equal(2, tracker.SelectedColumn); Assert.True(tracker.HasPendingEdit);
            Assert.Equal("", tracker.EditText); Assert.False(editor.IsDirty);
            // Real raw input reaches the shared command. An unchanged FX draft must
            // not create a spurious save dialog or require a second user decision.
            window.KeyPress(Key.D, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.D, "D");
            window.KeyRelease(Key.D, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.D, "D");
            Dispatcher.UIThread.RunJobs();
            Assert.Single(((Grid)view.Content!).Children);
            Assert.False(Field<bool>(view, "projectChangeBusy"));
            Assert.Equal("Demo song", editor.Song.Title); Assert.True(editor.IsDirty);
            var recovery = Assert.Single(store.Data, item => item.Key.StartsWith("recovery-", StringComparison.Ordinal));
            var saved = SongFile.Read(recovery.Value);
            Assert.Equal("Demo song", saved.Title);
            Assert.Contains(saved.Patterns.SelectMany(p => p.Tracks).SelectMany(t => t.Rows), note => note.Kind == NoteKind.Note);
            Assert.NotEmpty(saved.Arrangement); Assert.False(store.Data.ContainsKey("last-project"));
            Assert.True(tracker.IsFocused);
        }
        finally
        {
            window?.Close();
            if (view is not null)
            {
                Field<DispatcherTimer>(view, "timer").Stop(); Field<DispatcherTimer>(view, "recoveryTimer").Stop();
                Field<IDisposable>(view, "player").Dispose();
            }
            EditorPlatform.LoadPreferences = previousLoad; EditorPlatform.SavePreferences = previousSave;
            EditorPlatform.ProjectStorage = previousStorage;
        }
    }
    private static T Field<T>(MainView view, string name) => (T)typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
}
