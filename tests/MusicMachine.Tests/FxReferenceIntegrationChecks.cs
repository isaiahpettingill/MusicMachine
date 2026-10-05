using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class FxReferenceIntegrationChecks
{
    internal static void Run()
    {
        TrackerEditRecoveryChecks.Run();
        var directory = Path.Combine(Path.GetTempPath(), "musicmachine-fx-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousData = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA");
        var previousLoad = EditorPlatform.LoadPreferences; var previousSave = EditorPlatform.SavePreferences;
        var previousStorage = EditorPlatform.ProjectStorage;
        MainView? view = null; MainView? reopened = null; Window? window = null;
        var preferenceViews = new List<MainView>();
        try
        {
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", directory);
            string preferences = "theme=catppuccin-mocha\nlibrary=false\ninspector=false\nworkspace=Tracker\n";
            EditorPlatform.LoadPreferences = () => preferences; EditorPlatform.SavePreferences = text => preferences = text;
            EditorPlatform.ProjectStorage = null;
            view = new MainView(); window = new Window { Width = 1180, Height = 812, Content = view }; window.Show(); window.UpdateLayout();
            Pump(() => !Field<bool>(view, "projectChangeBusy"));
            var editor = Field<SongEditor>(view, "editor"); var tracker = Field<TrackerGrid>(view, "tracker");
            Assert.Equal(2, tracker.EffectColumns); // Existing preferences without the new key retain the old default.
            string pattern = Field<string>(view, "activePattern");
            NoteEvent Note() => editor.Song.FindPattern(pattern)!.Tracks.First(t => t.TrackId == editor.Song.Tracks[1].Id).Rows[2];
            tracker.Select(2, 1); Assert.True(tracker.CommitText("F#4"));
            tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "G4" });
            Assert.True(tracker.HasPendingEdit);
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2 });
            Dispatcher.UIThread.RunJobs();
            Assert.False(tracker.HasPendingEdit); Assert.Equal(67, Note().Pitch);
            var pane = Field<FxReferencePanel>(view, "fxReferencePanel");
            var search = Named<TextBox>(pane, "FxSearch"); Assert.True(search.IsFocused);
            Assert.Contains("inspectorContent=fx", preferences); Assert.Contains("inspector=true", preferences);
            search.Text = "volume"; Dispatcher.UIThread.RunJobs(); Named<NumericUpDown>(pane, "FxParameter0").Value = 128;
            Assert.Equal("V80", pane.SelectedCode);
            var insert = Named<Button>(pane, "FxInsert"); Assert.True(insert.IsEnabled); insert.Focus();
            insert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "V80" }, Note().Effects); Assert.Equal(67, Note().Pitch);
            Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(1, tracker.SelectedColumn); Assert.True(tracker.IsFocused);
            Assert.Equal("volume", search.Text);
            Invoke(view, "Undo"); Assert.Empty(Note().Effects); Assert.Equal(67, Note().Pitch);
            Invoke(view, "Redo"); Assert.Equal(new[] { "V80" }, Note().Effects);
            tracker.Select(2, 1, 0); Invoke(view, "InsertReferenceEffect", "A37");
            Assert.Equal(new[] { "V80", "A37" }, Note().Effects); Assert.Equal(2, tracker.SelectedRow);
            Assert.Equal(NoteKind.Empty, editor.Song.FindPattern(pattern)!.Tracks[0].Rows[2].Kind);
            // Explicit FX cells replace that cell only. Duplicating a command fails transactionally.
            tracker.Select(2, 1, 1); var before = SongFile.Write(editor.Song); var revision = editor.Revision;
            Invoke(view, "InsertReferenceEffect", "A47"); Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);
            // From a note cell, reuse the same command's slot instead of making a duplicate.
            tracker.Select(2, 1, 0); Invoke(view, "InsertReferenceEffect", "A47"); Assert.Equal(new[] { "V80", "A47" }, Note().Effects);
            Invoke(view, "SetEffectColumns", 0); Assert.Equal(1, tracker.SelectedTrack);
            Assert.Contains("effectColumns=0\n", preferences);
            Invoke(view, "InsertReferenceEffect", "V40"); Assert.Equal(new[] { "V40", "A47" }, Note().Effects);
            Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.EffectColumns);
            Assert.Contains("effectColumns=1\n", preferences);
            // F2 stays available for malformed FX and keeps the draft until an explicit replacement.
            tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "8" });
            before = SongFile.Write(editor.Song); revision = editor.Revision;
            tracker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2 });
            Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsFocused); Assert.True(tracker.HasPendingEdit); Assert.Equal("8", tracker.EditText);
            Assert.NotNull(tracker.EditError); Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);
            Assert.Contains("replaces unfinished", Named<TextBlock>(pane, "FxInsertContext").Text);
            // A rejected replacement preserves both the draft and original music.
            Invoke(view, "InsertReferenceEffect", "A57");
            Assert.True(tracker.HasPendingEdit); Assert.Equal("8", tracker.EditText); Assert.Equal(before, SongFile.Write(editor.Song));
            Assert.Equal(FxCatalog.SyntaxHelp, tracker.EditError);
            var unchangedPreferences = preferences;
            Invoke(view, "SetEffectColumns", 6);
            Assert.Equal(1, tracker.EffectColumns); Assert.Equal(unchangedPreferences, preferences);
            Invoke(view, "InsertReferenceEffect", "G80"); Dispatcher.UIThread.RunJobs();
            Assert.False(tracker.HasPendingEdit); Assert.Null(tracker.EditError); Assert.True(tracker.IsFocused);
            Assert.Equal(new[] { "G80", "A47" }, Note().Effects); Assert.Equal(67, Note().Pitch);
            Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(1, tracker.SelectedColumn);
            Assert.Equal(revision + 1, editor.Revision);
            Invoke(view, "Undo"); Assert.Equal(before, SongFile.Write(editor.Song));
            // A note draft cannot be replaced by an FX. Escape also works from the reference search.
            tracker.Select(2, 1, 0);
            tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "invalid" });
            tracker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2 });
            Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsFocused); Assert.False(insert.IsEnabled); Assert.True(tracker.HasPendingEdit);
            Invoke(view, "InsertReferenceEffect", "G80"); Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal("invalid", tracker.EditText);
            Invoke(view, "ShowFxReference"); Dispatcher.UIThread.RunJobs();
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); Dispatcher.UIThread.RunJobs();
            Assert.False(tracker.HasPendingEdit); Assert.True(tracker.IsFocused); Assert.True(insert.IsEnabled);
            Assert.Equal(before, SongFile.Write(editor.Song));
            // Switching inspector content also leaves unfinished FX available for recovery.
            tracker.Select(2, 1, 1);
            tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "8" });
            Field<ComboBox>(view, "inspectorPicker").SelectedIndex = 0;
            Assert.True(tracker.HasPendingEdit); Assert.Equal(0, Field<ComboBox>(view, "inspectorPicker").SelectedIndex);
            Field<ComboBox>(view, "inspectorPicker").SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsFocused); Assert.Equal("8", tracker.EditText);
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); Dispatcher.UIThread.RunJobs();
            Assert.False(tracker.HasPendingEdit); Assert.Equal(before, SongFile.Write(editor.Song));
            // Paste replaces the draft before dispatching the host transaction, so the host's
            // pending-edit guard cannot reject it while reporting a successful paste.
            tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "8" });
            Assert.True(tracker.PasteText("V50")); Assert.False(tracker.HasPendingEdit);
            Assert.Equal(new[] { "V50", "A47" }, Note().Effects); Assert.Equal(67, Note().Pitch);
            Invoke(view, "Undo"); Assert.Equal(before, SongFile.Write(editor.Song));
            Invoke(view, "SelectWorkspace", "Drums"); Assert.False(insert.IsEnabled);
            Invoke(view, "InsertReferenceEffect", "G80"); Assert.Equal(before, SongFile.Write(editor.Song));
            Invoke(view, "SelectWorkspace", "Tracker"); Assert.True(insert.IsEnabled);
            // Inserting beyond the visible columns persists the expansion without moving logical selection.
            tracker.Select(2, 1, 0); Invoke(view, "InsertReferenceEffect", "G60");
            Assert.Equal(new[] { "V40", "A47", "G60" }, Note().Effects);
            Assert.Equal(3, tracker.EffectColumns); Assert.Contains("effectColumns=3\n", preferences);
            Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(3, tracker.SelectedColumn);
            Invoke(view, "TogglePane", false); Dispatcher.UIThread.RunJobs(); Assert.True(tracker.IsFocused);
            Assert.Contains("inspector=false", preferences); Assert.Contains("inspectorContent=fx", preferences);
            reopened = new MainView(); Assert.Equal(1, Field<ComboBox>(reopened, "inspectorPicker").SelectedIndex);
            var reopenedTracker = Field<TrackerGrid>(reopened, "tracker");
            Assert.Equal(3, reopenedTracker.EffectColumns);
            var persistedSong = SongFile.Clone(editor.Song);
            Invoke(reopened, "SetCurrentSong", SongFile.Clone(persistedSong), null!, false);
            Assert.Equal(3, reopenedTracker.EffectColumns);
            reopenedTracker.Select(2, 1, 3);
            Assert.Equal("G60", reopenedTracker.CurrentText);
            // Explicit Hide FX remains hidden even when the loaded song contains three populated columns.
            Invoke(reopened, "SetEffectColumns", 0);
            Assert.Equal(0, reopenedTracker.EffectColumns); Assert.Contains("effectColumns=0\n", preferences);
            Assert.Equal(2, reopenedTracker.SelectedRow); Assert.Equal(1, reopenedTracker.SelectedTrack); Assert.Equal(0, reopenedTracker.SelectedColumn);
            var hidden = new MainView(); preferenceViews.Add(hidden);
            Invoke(hidden, "SetCurrentSong", SongFile.Clone(persistedSong), null!, false);
            Assert.Equal(0, Field<TrackerGrid>(hidden, "tracker").EffectColumns);
            Assert.Equal(new[] { "V40", "A47", "G60" }, Field<SongEditor>(hidden, "editor").Song.FindPattern(pattern)!.Tracks[1].Rows[2].Effects);

            // A conflicting insertion rolls back any tentative expansion and keeps its previous preferences.
            tracker.Select(2, 1, 0); Invoke(view, "InsertReferenceEffect", "U02");
            Assert.Equal(4, tracker.EffectColumns); Assert.Contains("effectColumns=4\n", preferences);
            tracker.Select(2, 1, 0); before = SongFile.Write(editor.Song); unchangedPreferences = preferences;
            Invoke(view, "InsertReferenceEffect", "D02");
            Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(unchangedPreferences, preferences);
            Assert.Equal(4, tracker.EffectColumns); Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(0, tracker.SelectedColumn);

            tracker.Select(2, 1, 4); Invoke(view, "SetEffectColumns", 99);
            Assert.Equal(16, tracker.EffectColumns); Assert.Contains("effectColumns=16\n", preferences);
            Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(4, tracker.SelectedColumn);
            Invoke(view, "SetEffectColumns", -9);
            Assert.Equal(0, tracker.EffectColumns); Assert.Contains("effectColumns=0\n", preferences);
            Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(0, tracker.SelectedColumn);
            foreach (var (stored, expected) in new[] { ("", 2), ("not-a-number", 2), ("-9", 0), ("99", 16), ("16", 16), ("3", 3) })
            {
                preferences = $"checkForUpdates=false\neffectColumns={stored}\n";
                var restored = new MainView(); preferenceViews.Add(restored);
                Assert.Equal(expected, Field<TrackerGrid>(restored, "tracker").EffectColumns);
                Invoke(restored, "SaveViewSettings");
                Assert.Contains($"effectColumns={expected}\n", preferences);
            }
        }
        finally
        {
            window?.Close();
            foreach (var item in new[] { view, reopened }.OfType<MainView>().Concat(preferenceViews))
            {
                Field<DispatcherTimer>(item, "timer").Stop(); Field<DispatcherTimer>(item, "recoveryTimer").Stop();
                Invoke(item, "StopUpdates"); Field<IDisposable>(item, "player").Dispose();
                Pump(() => Field<SemaphoreSlim>(item, "recoveryWrites").CurrentCount == 1);
            }
            EditorPlatform.LoadPreferences = previousLoad; EditorPlatform.SavePreferences = previousSave; EditorPlatform.ProjectStorage = previousStorage;
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", previousData);
            Directory.Delete(directory, true);
        }
    }
    private static T Named<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static T Field<T>(MainView view, string name) => (T)typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static object? Invoke(MainView view, string name, params object?[] args) => typeof(MainView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args);
    private static void Pump(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!complete() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Dispatcher.UIThread.RunJobs(); Assert.True(complete(), "The reference integration did not settle.");
    }
}
