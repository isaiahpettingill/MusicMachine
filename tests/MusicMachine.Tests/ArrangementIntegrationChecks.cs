using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class ArrangementIntegrationChecks
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "musicmachine-arrangement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldData = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA");
        var oldLoad = EditorPlatform.LoadPreferences; var oldSave = EditorPlatform.SavePreferences;
        var oldStorage = EditorPlatform.ProjectStorage; var oldPlayer = AudioServices.CreatePlayer;
        MainView? view = null;
        var player = new RecordingPlayer();
        try
        {
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", directory);
            EditorPlatform.LoadPreferences = () => "workspace=Tracker\nlibrary=false\ninspector=false\ncheckForUpdates=false\n";
            EditorPlatform.SavePreferences = _ => { }; EditorPlatform.ProjectStorage = null; AudioServices.CreatePlayer = () => player;
            view = new MainView();
            var editor = Field<SongEditor>(view, "editor"); var tracker = Field<TrackerGrid>(view, "tracker");
            var seed = DemoSong.CreateEmpty(); var intro = seed.Patterns[0]; intro.Name = "Intro";
            var verse = new Pattern { Name = "Verse", Length = 16 };
            foreach (var track in seed.Tracks) verse.GetTrack(track.Id);
            verse.Tracks[0].Rows[0] = new() { Kind = NoteKind.Note, Pitch = 67 };
            seed.Patterns.Add(verse);
            Invoke(view, "SetCurrentSong", seed, null, false);
            Invoke(view, "OpenSongArrangement");
            Assert.Equal("Arrangement", Field<string>(view, "mode"));
            Assert.Same(editor.Song.Arrangement, Named<ListBox>(view, "ArrangementOrder").ItemsSource);
            foreach (var section in new[] { "ArrangementMixer", "ArrangementAutomation", "ArrangementOverview", "ArrangementPlaybackSettings" })
                Assert.False(Named<Expander>(view, section).IsExpanded);
            Assert.False(Named<Button>(view, "ArrangementRemove").IsEnabled);
            Assert.False(Named<Button>(view, "ArrangementMoveUp").IsEnabled);
            Assert.False(Named<Button>(view, "ArrangementMoveDown").IsEnabled);

            // The pattern picker works with the library closed and the sequence is the
            // same authoritative list that SongFile and the audio renderer consume.
            Named<ComboBox>(view, "ArrangementPatternPicker").SelectedIndex = 1;
            Click(view, "ArrangementAppend");
            AssertOrder(editor, intro.Id, verse.Id); Assert.Equal(1, Field<int>(view, "selectedSection"));
            Assert.Equal(2, editor.Song.LoopEndSection);
            Assert.Equal(1, Named<ComboBox>(view, "ArrangementPatternPicker").SelectedIndex);
            Named<ComboBox>(view, "ArrangementPatternPicker").SelectedIndex = 0;
            Named<ListBox>(view, "ArrangementOrder").SelectedIndex = 0;
            Click(view, "ArrangementInsert");
            AssertOrder(editor, intro.Id, intro.Id, verse.Id);
            Assert.Equal(1, Named<ListBox>(view, "ArrangementOrder").SelectedIndex);
            Named<NumericUpDown>(view, "ArrangementRepeats").Value = 3;
            Assert.Equal(3, editor.Song.Arrangement[1].Repeats);
            Click(view, "ArrangementMoveDown");
            AssertOrder(editor, intro.Id, verse.Id, intro.Id); Assert.Equal(3, editor.Song.Arrangement[2].Repeats);
            Assert.Equal(2, Named<ListBox>(view, "ArrangementOrder").SelectedIndex);
            Click(view, "ArrangementMoveUp"); AssertOrder(editor, intro.Id, intro.Id, verse.Id);
            var beforeRemove = SongFile.Write(editor.Song);
            Click(view, "ArrangementRemove"); AssertOrder(editor, intro.Id, verse.Id);
            Assert.Equal(2, editor.Song.Patterns.Count); Assert.Equal(2, editor.Song.LoopEndSection);
            Invoke(view, "Undo"); Assert.Equal(beforeRemove, SongFile.Write(editor.Song));
            Invoke(view, "Redo"); AssertOrder(editor, intro.Id, verse.Id);
            Named<ListBox>(view, "ArrangementOrder").SelectedIndex = 1;
            Click(view, "ArrangementEditPattern");
            Assert.Equal("Tracker", Field<string>(view, "mode")); Assert.Equal(verse.Id, Field<string>(view, "activePattern"));
            tracker.Select(0, 0); Assert.True(tracker.CommitText("C5"));
            Invoke(view, "AddSelectedPatternToSong");
            Assert.Equal("Arrangement", Field<string>(view, "mode"));
            AssertOrder(editor, intro.Id, verse.Id, verse.Id);
            Assert.Equal(72, editor.Song.FindPattern(editor.Song.Arrangement[1].PatternId)!.Tracks[0].Rows[0].Pitch);
            Assert.Equal(72, editor.Song.FindPattern(editor.Song.Arrangement[2].PatternId)!.Tracks[0].Rows[0].Pitch);
            Assert.Equal(NoteKind.Empty, editor.Song.FindPattern(intro.Id)!.Tracks[0].Rows[0].Kind);

            // Changes stop audio, retain one undo transaction, and keep collapsed
            // auxiliary controls from taking over the song-building workspace.
            Named<Expander>(view, "ArrangementMixer").IsExpanded = true;
            Named<NumericUpDown>(view, "ArrangementRepeats").Value = 2;
            Assert.True(Named<Expander>(view, "ArrangementMixer").IsExpanded);
            Assert.False(Named<Expander>(view, "ArrangementAutomation").IsExpanded);
            editor.Change(s => { s.LoopStartSection = 1; s.LoopEndSection = 2; }); Invoke(view, "Refresh", false);
            var expected = SongFile.Write(editor.Song);
            Click(view, "ArrangementPlaySong");
            Assert.True(player.IsPlaying); Assert.False(player.Loop); Assert.Equal(0, player.StartFrame);
            Assert.Equal(expected, SongFile.Write(player.Snapshot!)); Assert.NotSame(editor.Song, player.Snapshot);
            var renderer = new SynthRenderer(player.Snapshot!);
            var rows = editor.Song.Arrangement.Sum(s => editor.Song.FindPattern(s.PatternId)!.Length * s.Repeats);
            Assert.Equal((long)Math.Round(rows * renderer.FramesPerRow), renderer.MusicalFrames);
            var output = Path.Combine(directory, "arranged.wav");
            OfflineExporter.WriteWav(output, editor.Song);
            Assert.Equal(44 + renderer.MusicalFrames * 4, new FileInfo(output).Length);
            Click(view, "ArrangementStop"); Assert.False(player.IsPlaying);

            // Export goes through the same existing full-arrangement flow; cancel
            // leaves the arrangement and the editor's view state unchanged.
            Click(view, "ArrangementExport");
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("complete arrangement") == true);
            view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); Assert.Equal(expected, SongFile.Write(editor.Song));

            // Inserting before a bounded loop preserves the intended old sections;
            // inserting within it extends the end, and removal reverses both shifts.
            Invoke(view, "InsertSongPattern", intro.Id, 0);
            Assert.Equal(2, editor.Song.LoopStartSection); Assert.Equal(3, editor.Song.LoopEndSection);
            Invoke(view, "RemoveSection"); Assert.Equal(1, editor.Song.LoopStartSection); Assert.Equal(2, editor.Song.LoopEndSection);
            Invoke(view, "InsertSongPattern", intro.Id, 1);
            Assert.Equal(1, editor.Song.LoopStartSection); Assert.Equal(3, editor.Song.LoopEndSection);
            Invoke(view, "RemoveSection"); Assert.Equal(1, editor.Song.LoopStartSection); Assert.Equal(2, editor.Song.LoopEndSection);

            // Invalid buffered notes cannot be silently discarded by navigation,
            // append, move, or remove actions from another workspace.
            Invoke(view, "OpenSelectedSongPattern"); tracker.Select(0, 0);
            tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "invalid" });
            expected = SongFile.Write(editor.Song); var revision = editor.Revision;
            var selected = Field<int>(view, "selectedSection");
            Invoke(view, "AddSelectedPatternToSong"); Invoke(view, "OpenSongArrangement"); Invoke(view, "MoveSection", 1); Invoke(view, "RemoveSection");
            Assert.True(tracker.HasPendingEdit); Assert.Equal("Tracker", Field<string>(view, "mode"));
            Assert.Equal(expected, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision); Assert.Equal(selected, Field<int>(view, "selectedSection"));
            tracker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Invoke(view, "OpenSongArrangement");
            while (editor.Song.Arrangement.Count > 1) Invoke(view, "RemoveSection");
            expected = SongFile.Write(editor.Song); Invoke(view, "RemoveSection"); Assert.Equal(expected, SongFile.Write(editor.Song));
            Assert.Equal(0, editor.Song.LoopStartSection); Assert.Equal(1, editor.Song.LoopEndSection);
            Assert.False(Named<Button>(view, "ArrangementRemove").IsEnabled);

            // Model validation rejects an overlong arrangement transactionally and
            // neither claims an append nor jumps selection on failure.
            var large = DemoSong.CreateEmpty(); large.Patterns[0].Length = 1000;
            foreach (var track in large.Patterns[0].Tracks) while (track.Rows.Count < 1000) track.Rows.Add(new());
            foreach (var lane in large.Patterns[0].Drums) while (lane.Steps.Count < 1000) lane.Steps.Add(0);
            var repeatRows = 1000 * 125;
            large.Arrangement = Enumerable.Range(0, SongLimits.MaxExpandedRows / repeatRows).Select(_ => new SongSection { PatternId = large.Patterns[0].Id, Repeats = 125 }).ToList();
            large.LoopStartSection = 0; large.LoopEndSection = large.Arrangement.Count;
            Invoke(view, "SetCurrentSong", large, null, false); Invoke(view, "OpenSongArrangement");
            expected = SongFile.Write(editor.Song); selected = Field<int>(view, "selectedSection");
            Assert.False((bool)Invoke(view, "InsertSongPattern", large.Patterns[0].Id, large.Arrangement.Count)!);
            Assert.Equal(expected, SongFile.Write(editor.Song)); Assert.Equal(selected, Field<int>(view, "selectedSection"));
            Assert.Contains("playback row limit", Field<TextBlock>(view, "status").Text);
        }
        finally
        {
            if (view is not null)
            {
                Field<DispatcherTimer>(view, "timer").Stop(); Field<DispatcherTimer>(view, "recoveryTimer").Stop();
                Invoke(view, "StopUpdates"); player.Dispose();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (Field<SemaphoreSlim>(view, "recoveryWrites").CurrentCount != 1 && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
            }
            AudioServices.CreatePlayer = oldPlayer;
            EditorPlatform.LoadPreferences = oldLoad; EditorPlatform.SavePreferences = oldSave; EditorPlatform.ProjectStorage = oldStorage;
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", oldData);
            Directory.Delete(directory, true);
        }
    }

    private static void AssertOrder(SongEditor editor, params string[] ids) => Assert.Equal(ids, editor.Song.Arrangement.Select(s => s.PatternId));
    private static void Click(Control root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Named<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static T Field<T>(MainView view, string name) => (T)typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static object? Invoke(MainView view, string name, params object?[] args) => typeof(MainView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args);
    private sealed class RecordingPlayer : IAudioPlayer
    {
        public Song? Snapshot { get; private set; }
        public bool Loop { get; private set; }
        public long StartFrame { get; private set; }
        public bool IsPlaying { get; private set; }
        public long PositionFrames => 0;
        public int SampleRate => 48000;
        public string? LastError => null;
        public void Play(Song song, long startFrame = 0, bool loop = false) { Snapshot = song; StartFrame = startFrame; Loop = loop; IsPlaying = true; }
        public void Stop() => IsPlaying = false;
        public void Dispose() => Stop();
    }
}
