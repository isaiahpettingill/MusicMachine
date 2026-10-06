using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class TrackerEditRecoveryChecks
{
    internal static void Run()
    {
        var editor = new SongEditor(TestSong.CreateEmpty());
        var patternId = editor.Song.Patterns[0].Id;
        var tracker = new TrackerGrid();
        tracker.SetSong(editor.Song, patternId);
        tracker.Change += change => { editor.Change(change); tracker.SetSong(editor.Song, patternId); };
        string status = ""; tracker.Status += message => status = message;
        void Type(string text) => tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        void KeyPress(Key key, KeyModifiers modifiers = KeyModifiers.None) => tracker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        NoteEvent Note() => editor.Song.FindPattern(patternId)!.Tracks[0].Rows[2];

        // Empty/unmodified FX drafts must not materialize absent columns, dirty
        // startup, create undo entries, or erase the real effects around them.
        var clean = SongFile.Write(editor.Song);
        foreach (var column in new[] { 1, 2, 8 })
        {
            tracker.EffectColumns = 8; tracker.Select(2, 0, column);
            Assert.True(tracker.CommitText("  "));
            Assert.Equal(clean, SongFile.Write(editor.Song)); Assert.False(editor.IsDirty);
            Assert.Equal(0, editor.Revision); Assert.False(editor.CanUndo); Assert.Empty(Note().Effects);
        }
        tracker.EffectColumns = 2;

        tracker.Select(2, 0); Assert.True(tracker.CommitText("G4"));
        tracker.Select(2, 0, 1); Assert.True(tracker.CommitText("V80"));
        var before = SongFile.Write(editor.Song); var revision = editor.Revision;
        Assert.True(tracker.CommitText(" v80 "));
        tracker.Select(2, 0, 2); Assert.True(tracker.CommitText(""));
        Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);
        tracker.Select(2, 0, 1); Assert.True(tracker.CommitText(""));
        Assert.Equal("", Note().Effects[0]); Assert.Equal(revision + 1, editor.Revision);
        Assert.True(editor.Undo()); tracker.SetSong(editor.Song, patternId);
        Assert.Equal(before, SongFile.Write(editor.Song)); revision = editor.Revision;
        // Explicit commit rejects malformed FX, explains recovery and leaves the original intact.
        Type("8"); KeyPress(Key.Enter);
        Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedColumn);
        Assert.Equal("8", tracker.EditText); Assert.NotNull(tracker.EditError);
        Assert.Contains("Esc cancels", status); Assert.Contains("F2", status);
        Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);
        KeyPress(Key.Escape); KeyPress(Key.Escape);
        Assert.False(tracker.HasPendingEdit); Assert.Null(tracker.EditError); Assert.Equal("V80", tracker.CurrentText);
        Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);

        // All ordinary navigation keys remain escape routes; rejected drafts never erase saved cells.
        foreach (var (key, modifiers, row, track, column) in new[]
        {
            (Key.Up, KeyModifiers.None, 1, 0, 1), (Key.Down, KeyModifiers.None, 3, 0, 1),
            (Key.Left, KeyModifiers.None, 2, 0, 0), (Key.Right, KeyModifiers.None, 2, 0, 2),
            (Key.Tab, KeyModifiers.None, 2, 1, 1), (Key.Home, KeyModifiers.None, 0, 0, 1),
            (Key.End, KeyModifiers.None, 31, 0, 1), (Key.PageDown, KeyModifiers.None, 18, 0, 1),
            (Key.PageUp, KeyModifiers.None, 0, 0, 1), (Key.Right, KeyModifiers.Shift, 2, 0, 2)
        })
        {
            tracker.Select(2, 0, 1); Type("8"); KeyPress(key, modifiers);
            Assert.False(tracker.HasPendingEdit); Assert.Null(tracker.EditError);
            Assert.Equal(row, tracker.SelectedRow); Assert.Equal(track, tracker.SelectedTrack); Assert.Equal(column, tracker.SelectedColumn);
            Assert.Contains("Discarded invalid FX", status); Assert.Contains("original cell kept", status);
            Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);
        }
        tracker.Select(2, 1, 1); Type("8"); KeyPress(Key.Tab, KeyModifiers.Shift);
        Assert.Equal(0, tracker.SelectedTrack); Assert.Equal(1, tracker.SelectedColumn); Assert.False(tracker.HasPendingEdit);
        tracker.Select(2, 0); Type("bad-note"); KeyPress(Key.Down);
        Assert.Contains("Discarded invalid note", status); Assert.Equal(before, SongFile.Write(editor.Song));

        // Editing the rejected draft clears stale errors, then saves exactly once and advances normally.
        tracker.Select(2, 0, 1); Type("8"); KeyPress(Key.Enter); KeyPress(Key.Back);
        Assert.Null(tracker.EditError); Type("V40"); KeyPress(Key.Enter);
        Assert.False(tracker.HasPendingEdit); Assert.Equal(3, tracker.SelectedRow);
        Assert.Equal("V40", Note().Effects[0]); Assert.Equal(67, Note().Pitch); Assert.Equal(revision + 1, editor.Revision);
        tracker.Select(2, 0); Type("A4"); KeyPress(Key.Right);
        Assert.Equal(69, Note().Pitch); Assert.Equal(1, tracker.SelectedColumn); Assert.False(tracker.HasPendingEdit);
        Type("V60"); KeyPress(Key.Tab); Assert.Equal("V60", Note().Effects[0]); Assert.Equal(1, tracker.SelectedTrack);

        // Real pointer events: returning to a draft keeps it editable; another cell exits cleanly.
        var window = new Window { Width = 800, Height = 720, Content = tracker };
        window.Show(); window.UpdateLayout();
        try
        {
            void Click(double x, int row)
            {
                var point = tracker.TranslatePoint(new Point(x, TrackerGrid.HeaderHeight + row * TrackerGrid.RowHeight + 14), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            }
            tracker.Select(2, 0, 1); Type("8"); KeyPress(Key.Enter);
            Click(TrackerGrid.Gutter + TrackerGrid.NoteWidth + 11, 2);
            Assert.Equal("8", tracker.EditText); Assert.NotNull(tracker.EditError); Assert.True(tracker.IsFocused);
            Click(TrackerGrid.Gutter + 25, 4);
            Assert.False(tracker.HasPendingEdit); Assert.Equal(4, tracker.SelectedRow); Assert.Equal(0, tracker.SelectedColumn);
            Assert.Equal("V60", Note().Effects[0]); Assert.Equal(69, Note().Pitch); Assert.Contains("original cell kept", status);
            tracker.Select(2, 0, 1); Type("V20"); Click(TrackerGrid.Gutter + 25, 5);
            Assert.Equal("V20", Note().Effects[0]); Assert.Equal(5, tracker.SelectedRow); Assert.False(tracker.HasPendingEdit);
            // Explicit paste is also a replacement: the invalid draft cannot block its transaction.
            tracker.Select(2, 0, 1); Type("8"); Assert.True(tracker.PasteText("V30"));
            Assert.False(tracker.HasPendingEdit); Assert.Null(tracker.EditError); Assert.Equal("V30", Note().Effects[0]); Assert.Equal(69, Note().Pitch);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
