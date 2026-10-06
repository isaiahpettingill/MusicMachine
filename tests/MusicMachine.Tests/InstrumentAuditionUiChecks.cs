using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.Audio;
using MusicMachine.Core;
namespace MusicMachine.Tests;
internal static class InstrumentAuditionUiChecks
{
    internal static void Run()
    {
        var oldPlayer = AudioServices.CreatePlayer; var oldStorage = EditorPlatform.ProjectStorage;
        var oldLoad = EditorPlatform.LoadPreferences; var oldSave = EditorPlatform.SavePreferences;
        var player = new RecordingPlayer(); Window? window = null;
        try
        {
            AudioServices.CreatePlayer = () => player; EditorPlatform.ProjectStorage = new MemoryProjectStorage(true);
            EditorPlatform.LoadPreferences = () => "inspector=true\ncheckForUpdates=false\n"; EditorPlatform.SavePreferences = _ => { };
            var view = new MainView(); window = new Window { Width = 1180, Height = 900, Content = view };
            window.Show(); Pump(window);
            typeof(MainView).GetMethod("AddInstrument", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null); Pump(window);
            var selector = view.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "WaveformSelector");
            var audition = view.GetLogicalDescendants().OfType<Button>().Single(c => c.Name == "AuditionInstrument");
            var editor = (SongEditor)typeof(MainView).GetField("editor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            // The first authored sound supplies every blank track, and playback mixes all four.
            var firstInstrument = Assert.Single(editor.Song.Instruments);
            Assert.All(editor.Song.Tracks, track => Assert.Equal(firstInstrument.Id, track.InstrumentId));
            editor.Change(song =>
            {
                var pattern = song.Patterns[0];
                for (var track = 0; track < song.Tracks.Count; track++)
                    pattern.GetTrack(song.Tracks[track].Id).Rows[0] = new NoteEvent { Kind = NoteKind.Note, Pitch = 60 + track * 7 };
            });
            // Repair tracks saved by the old UI with notes but no default sound.
            editor.Change(song => { song.Tracks[1].InstrumentId = ""; song.Tracks[3].InstrumentId = ""; });
            typeof(MainView).GetMethod("TogglePlay", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null); Pump(window);
            Assert.True(player.IsPlaying); Assert.Equal(4, player.Snapshot!.Tracks.Count);
            Assert.All(player.Snapshot.Tracks, track => Assert.Equal(firstInstrument.Id, track.InstrumentId));
            var mixed = OfflineExporter.Render(player.Snapshot);
            for (var track = 0; track < 4; track++)
            {
                var muted = SongFile.Clone(player.Snapshot); muted.Tracks[track].Muted = true;
                var withoutTrack = OfflineExporter.Render(muted);
                Assert.True(mixed.Zip(withoutTrack, (a, b) => Math.Abs(a - b)).Max() > .02, $"Track {track + 1} did not contribute to the mix");
            }
            var tracker = (TrackerGrid)typeof(MainView).GetField("tracker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            tracker.Select(0, 2);
            typeof(MainView).GetMethod("AddInstrument", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null); Pump(window);
            var secondInstrument = editor.Song.Instruments[1];
            Assert.Equal(secondInstrument.Id, editor.Song.Tracks[2].InstrumentId);
            Assert.Equal(firstInstrument.Id, editor.Song.Tracks[0].InstrumentId);
            var picker = view.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "TrackInstrumentPicker");
            picker.SelectedIndex = 1; Pump(window);
            Assert.Equal(firstInstrument.Id, editor.Song.Tracks[2].InstrumentId);
            typeof(MainView).GetMethod("ChooseInstrument", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [secondInstrument.Id]); Pump(window);
            Assert.Equal(secondInstrument.Id, editor.Song.Tracks[2].InstrumentId);
            foreach (var shape in Enum.GetValues<Waveform>())
            {
                selector.SelectedIndex = (int)shape; Pump(window);
                var before = SongFile.Write(editor.Song); var revision = editor.Revision;
                audition.BringIntoView(); Pump(window);
                var point = audition.TranslatePoint(new Point(audition.Bounds.Width / 2, audition.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
                Assert.True(player.IsPlaying); Assert.NotNull(player.Snapshot);
                Assert.Equal(shape, Assert.Single(player.Snapshot!.Instruments).Waveform);
                var samples = OfflineExporter.Render(player.Snapshot);
                Assert.True(samples.Max(v => Math.Abs(v)) > .05, $"{shape} audition was silent");
                Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);
            }
        }
        finally
        {
            if (window is not null) { window.Content = null; window.Close(); }
            AudioServices.CreatePlayer = oldPlayer; EditorPlatform.ProjectStorage = oldStorage;
            EditorPlatform.LoadPreferences = oldLoad; EditorPlatform.SavePreferences = oldSave;
        }
    }
    private static void Pump(Window window) { for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); } }
    private sealed class RecordingPlayer : IAudioPlayer
    {
        public Song? Snapshot { get; private set; }
        public bool IsPlaying { get; private set; }
        public long PositionFrames => 0;
        public int SampleRate => 48000;
        public string? LastError => null;
        public void Play(Song song, long startFrame = 0, bool loop = false) { Snapshot = SongFile.Clone(song); IsPlaying = true; }
        public void Stop() => IsPlaying = false;
        public void Dispose() => Stop();
    }
}
