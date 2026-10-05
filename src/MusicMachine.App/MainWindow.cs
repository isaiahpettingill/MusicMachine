using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MusicMachine.Core;
using MusicMachine.Audio;
using IconPacks.Avalonia.Material;

namespace MusicMachine.App;
public sealed partial class MainView : UserControl
{
    private readonly SongEditor editor;
    private readonly IAudioPlayer player = AudioServices.CreatePlayer();
    private readonly TrackerGrid tracker = new();
    private readonly InstrumentPanel instrumentPanel;
    private readonly SamplingPanel samplingPanel;
    private readonly StackPanel library = new() { Spacing = 3 }, patternList = new() { Spacing = 3 };
    private readonly ContentControl workArea = new();
    private readonly Dictionary<string, Button> tabButtons = [];
    private readonly TextBlock status = Ui.Label("Ready · Space to play · Type a note to begin", 11, Ui.Muted);
    private readonly TextBlock transportPosition = Ui.Label("00:00.0", 18, Ui.Accent);
    private readonly TextBox titleBox = new() { Width = 205, Background = Brushes.Transparent, BorderThickness = new(0), FontWeight = FontWeight.SemiBold };
    private readonly TextBox cellEntry = new() { Width = 118, PlaceholderText = "F#4, OFF, CUT" };
    private readonly TextBlock selection = Ui.Label("", 11, Ui.Muted);
    private readonly Button playButton, undoButton, redoButton;
    private readonly NumericUpDown tempo, rows, beats, swing;
    private readonly ComboBox beatUnit;
    private readonly ComboBox gridResolution;
    private readonly CheckBox loopToggle = new() { Content = "Loop", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string activePattern, selectedInstrument;
    private string? filePath;
    private string mode = "Tracker";
    private string displayedMode = "Tracker";
    private readonly Dictionary<string, Vector> workspaceOffsets = [];
    private bool refreshing, exportBusy, previewing;
    private string? playingPattern;
    private int chosenTrack;
    private readonly string recoveryPath;
    private bool initialized;
    internal Grid OverlayRoot { get; } = new();
    private string title = "MusicMachine";
    private string Title { get => title; set { title = value; if (TopLevel.GetTopLevel(this) is Window window) window.Title = value; } }
    private readonly ProjectFileAccess? suppliedProjectFiles;
    private IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;
    private IFocusManager? FocusManager => TopLevel.GetTopLevel(this)?.FocusManager;
    public MainView(string? initialPath = null, ProjectFileAccess? projectFiles = null)
    {
        suppliedProjectFiles = projectFiles;
        MinWidth = 820; MinHeight = 560; Background = Ui.Background; Foreground = Ui.Text; FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"); FontSize = 13;
        editor = new SongEditor(DemoSong.CreateEmpty());
        activePattern = editor.Song.Patterns[0].Id; selectedInstrument = editor.Song.Tracks[0].InstrumentId;
        var data = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicMachine");
        recoveryPath = Path.Combine(data, "recovery.song");
        projectStorage = EditorPlatform.ProjectStorage ?? new DesktopProjectStorage(data);
        projectStartup = new ProjectStartupService(projectStorage);
        LoadViewSettings(); EditorThemes.Apply(viewSettings.Theme);
        instrumentPanel = new InstrumentPanel(Change, PreviewInstrument, () => _ = ExportInstrument(), () => _ = ImportInstrument());
        samplingPanel = new SamplingPanel(ImportSamplingAudio, ApplySampledWave, PreviewSampledWave, Stop);
        playButton = Ui.IconButton(PackIconMaterialKind.Play, TogglePlay, "Play / stop · Space");
        undoButton = Ui.Button("Undo", () => Undo(), "Undo · Ctrl+Z"); redoButton = Ui.Button("Redo", () => Redo(), "Redo · Ctrl+Shift+Z");
        tempo = Number(editor.Song.Bpm, 20, 400, 1, 76, v => Change(s => s.Bpm = v), "0");
        beats = Number(editor.Song.BeatsPerBar, 1, 16, 1, 45, v => Change(s => s.BeatsPerBar = (int)v), "0");
        rows = Number(32, 4, 256, 4, 70, v => ResizePattern((int)v), "0");
        swing = Number(editor.Song.Swing * 100, 0, 75, 1, 62, v => Change(s => s.Swing = v / 100), "0");
        beatUnit = new ComboBox { ItemsSource = new[] { "2", "4", "8", "16" }, SelectedIndex = 1, Width = 60 };
        beatUnit.SelectionChanged += (_, _) => { if (!refreshing && beatUnit.SelectedIndex >= 0) Change(s => s.BeatUnit = 1 << (beatUnit.SelectedIndex + 1)); };
        gridResolution = new() { ItemsSource = new[] { "1/4", "1/8", "1/16", "1/32", "1/64" }, SelectedIndex = 2, Width = 84 };
        gridResolution.SelectionChanged += (_, _) => { if (!refreshing && gridResolution.SelectedIndex >= 0) ChangeGrid(1 << gridResolution.SelectedIndex); };
        tracker.Change += Change; tracker.Status += SetStatus;
        tracker.SelectionChanged += (r, t) => { chosenTrack = t; UpdateSelection(); };
        tracker.InstrumentHeaderClicked += id => { selectedInstrument = id; Refresh(true); };
        tracker.TrackHeaderContextRequested += t => _ = TrackOptions(t);
        tracker.TrackHeaderClicked += t => { chosenTrack = t; selectedInstrument = editor.Song.Tracks[t].InstrumentId; Refresh(); SetStatus("Track selected · right-click its header to move or remove it"); };
        titleBox.LostFocus += (_, _) => { if (!refreshing && titleBox.Text is { } t && t != editor.Song.Title && !string.IsNullOrWhiteSpace(t)) Change(s => s.Title = t.Trim()); };
        titleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { tracker.Focus(); e.Handled = true; } };
        cellEntry.KeyDown += (_, e) => { if (e.Key == Key.Enter) { if (tracker.CommitText(cellEntry.Text ?? "")) { tracker.Select(tracker.SelectedRow + 1, tracker.SelectedTrack, tracker.SelectedColumn); UpdateSelection(); tracker.Focus(); } e.Handled = true; } };
        OverlayRoot.Children.Add(BuildShell()); Content = OverlayRoot;
        KeyDown += GlobalKeyDown;
        DetachedFromVisualTree += (_, _) => { timer.Stop(); recoveryTimer.Stop(); StopUpdates(); samplingPanel.CancelWork(); player.Dispose(); };
        timer.Tick += (_, _) => TickPlayback(); timer.Start();
        recoveryTimer.Tick += async (_, _) => await SaveRecoverySnapshotAsync();
        SynchronizeDrumSound(); Refresh();
        AttachedToVisualTree += async (_, _) =>
        {
            if (initialized) return; initialized = true; Title = title; RestoreInputFocus();
            await InitializeProjectAsync(initialPath);
            StartUpdates();
        };
    }
    private void Refresh(bool preserveTracker = false)
    {
        refreshing = true;
        try
        {
            var song = editor.Song;
            if (song.FindPattern(activePattern) is null) activePattern = song.Patterns[0].Id;
            if (song.FindInstrument(selectedInstrument) is null) selectedInstrument = song.Instruments.FirstOrDefault()?.Id ?? "";
            chosenTrack = Math.Clamp(chosenTrack, 0, Math.Max(0, song.Tracks.Count - 1));
            Title = $"{(editor.IsDirty ? "● " : "")}{song.Title} · MusicMachine"; titleBox.Text = song.Title;
            tempo.Value = (decimal)song.Bpm; beats.Value = song.BeatsPerBar; beatUnit.SelectedIndex = (int)Math.Log2(song.BeatUnit) - 1; swing.Value = (decimal)(song.Swing * 100); gridResolution.SelectedIndex = (int)Math.Log2(song.RowsPerBeat); rows.Value = song.FindPattern(activePattern)!.Length;
            undoButton.IsEnabled = editor.CanUndo; redoButton.IsEnabled = editor.CanRedo;
            library.Children.Clear(); foreach (var instrument in song.Instruments)
            {
                var button = Ui.Button(instrument.Name, () => { selectedInstrument = instrument.Id; Refresh(); }, $"{instrument.Name} · {instrument.Waveform} · {(instrument.IsLocal ? "independent local copy" : "song instrument")}");
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left; button.FontSize = 11; button.Padding = new(7, 6); if (instrument.Id == selectedInstrument) button.Classes.Add("selected"); library.Children.Add(button);
            }
            patternList.Children.Clear(); foreach (var pattern in song.Patterns)
            {
                var row = new Grid { ColumnDefinitions = new("*,Auto") }; var button = Ui.Button(pattern.Name, () => { if (tracker.CommitPending()) { activePattern = pattern.Id; SynchronizeDrumSound(); Refresh(); } }); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left; button.FontSize = 11;
                if (pattern.Id == activePattern) button.Classes.Add("selected"); row.Children.Add(button); var count = Ui.Label($"{pattern.Length}", 10, Ui.Muted); count.Margin = new(4); Grid.SetColumn(count, 1); row.Children.Add(count); patternList.Children.Add(row);
            }
            RefreshChrome(); samplingPanel.SetTarget(song.FindInstrument(selectedInstrument)?.Name ?? "selected instrument");
            tracker.SetSong(song, activePattern); instrumentPanel.ShowInstrument(song, selectedInstrument); if (!(preserveTracker && mode == "Tracker")) RefreshWorkspace(); UpdateSelection();
        }
        finally { refreshing = false; }
    }
    private void RefreshWorkspace()
    {
        foreach (var (name, button) in tabButtons) { if (name == mode) button.Classes.Add("selected"); else button.Classes.Remove("selected"); }
        if (WorkspaceScroll(workArea.Content) is { } previous) workspaceOffsets[displayedMode] = previous.Offset;
        workArea.Content = null;
        PrepareInspector();
        var content = mode switch { "Arrangement" => BuildArrangement(), "Drums" => BuildDrums(), "Instrument" => BuildInstrumentWorkspace(), "Automation" => BuildAutomationWorkspace(), "Sampling" => BuildSamplingWorkspace(), _ => BuildTracker() };
        displayedMode = mode; workArea.Content = content;
        if (WorkspaceScroll(content) is { } scroll)
        {
            var restore = workspaceOffsets.GetValueOrDefault(mode); var modeKey = mode;
            scroll.Loaded += (_, _) => scroll.Offset = restore;
            scroll.ScrollChanged += (_, _) => workspaceOffsets[modeKey] = scroll.Offset;
        }
        RestoreInputFocus();
    }
    private static ScrollViewer? WorkspaceScroll(object? control) => control as ScrollViewer ?? (control as Grid)?.Children.OfType<ScrollViewer>().FirstOrDefault();
    private void UpdateSelection()
    {
        var t = editor.Song.Tracks.ElementAtOrDefault(tracker.SelectedTrack); cellEntry.Text = tracker.CurrentText;
        selection.Text = $"{t?.Name ?? "Track"} · {tracker.SelectedRow + 1:00}";
        var n = t is null ? null : editor.Song.FindPattern(activePattern)?.Tracks.FirstOrDefault(pt => pt.TrackId == t.Id)?.Rows.ElementAtOrDefault(tracker.SelectedRow);
        if (!string.IsNullOrWhiteSpace(n?.InstrumentId)) selection.Text += $" · {editor.Song.FindInstrument(n.InstrumentId)?.Name}";
    }
    private void Change(Action<Song> action)
    {
        if (refreshing || !tracker.CommitPending()) return;
        try { var keepFocus = tracker.IsFocused; Stop(); editor.Change(action); Refresh(keepFocus); recoveryTimer.Stop(); recoveryTimer.Start(); }
        catch (Exception e) { Refresh(); SetStatus(e.Message); }
    }
    private void Undo() { Stop(); if (editor.Undo()) { Refresh(tracker.IsFocused); SetStatus("Undone"); recoveryTimer.Start(); } }
    private void Redo() { Stop(); if (editor.Redo()) { Refresh(tracker.IsFocused); SetStatus("Redone"); recoveryTimer.Start(); } }
    internal void RestoreInputFocus() => Dispatcher.UIThread.Post(() => { if (OverlayRoot.Children.Count > 1) return; if (mode == "Tracker") tracker.Focus(); else { Focusable = true; Focus(); } }, DispatcherPriority.Background);
    private void SetStatus(string message) { status.Text = message; ToolTip.SetTip(status, message); }
    private void TogglePlay()
    {
        if (player.IsPlaying) { Stop(); return; }
        if (!tracker.CommitPending()) return;
        try { previewing = false; playingPattern = null; player.Play(SongFile.Clone(editor.Song), loop: loopToggle.IsChecked == true); SetPlayingVisual(true); SetStatus("Playing song · edits stop playback safely"); }
        catch (Exception e) { SetStatus("Audio device: " + e.Message + " · offline WAV/QOA export remains available"); }
    }
    private void PlayPattern()
    {
        try { Stop(); var s = SongFile.Clone(editor.Song); s.Arrangement = [new() { PatternId = activePattern }]; s.LoopStartSection = 0; s.LoopEndSection = 1; player.Play(s, loop: loopToggle.IsChecked == true); SetPlayingVisual(true); playingPattern = activePattern; previewing = false; SetStatus("Playing current pattern · F6"); } catch (Exception e) { SetStatus("Pattern playback: " + e.Message); }
    }
    private void PlayFromCursor()
    {
        long startRow = tracker.SelectedRow; bool found = false;
        foreach (var section in editor.Song.Arrangement) { if (section.PatternId == activePattern) { found = true; break; } startRow += (long)editor.Song.FindPattern(section.PatternId)!.Length * section.Repeats; }
        if (!found) { SetStatus("Append this pattern to the arrangement, or use Play pattern"); return; }
        try { Stop(); var s = SongFile.Clone(editor.Song); long frame = (long)Math.Round(startRow * 48000 * 60.0 / s.Bpm / s.RowsPerBeat); player.Play(s, frame); SetPlayingVisual(true); SetStatus("Playing from cursor with preceding instrument and FX state restored"); } catch (Exception e) { SetStatus("Cursor playback: " + e.Message); }
    }
    private void Stop() { player.Stop(); SetPlayingVisual(false); tracker.PlaybackRow = -1; tracker.InvalidateVisual(); transportPosition.Text = "00:00.0"; previewing = false; playingPattern = null; }
    private void TickPlayback()
    {
        if (!player.IsPlaying) { if (playingVisual) { var error = player.LastError; Stop(); if (!string.IsNullOrEmpty(error)) SetStatus(error); } return; }
        var seconds = player.PositionFrames / (double)Math.Max(1, player.SampleRate); transportPosition.Text = $"{(int)seconds / 60:00}:{seconds % 60:00.0}";
        if (previewing) return;
        if (playingPattern is not null) { tracker.PlaybackRow = playingPattern == activePattern ? (int)(seconds * editor.Song.Bpm / 60 * editor.Song.RowsPerBeat) % editor.Song.FindPattern(playingPattern)!.Length : -1; tracker.InvalidateVisual(); return; }
        var absRow = (int)(seconds * editor.Song.Bpm / 60 * editor.Song.RowsPerBeat); var rem = absRow;
        foreach (var part in editor.Song.Arrangement)
        {
            var pattern = editor.Song.FindPattern(part.PatternId)!; var length = pattern.Length * part.Repeats;
            if (rem < length) { tracker.PlaybackRow = pattern.Id == activePattern ? rem % pattern.Length : -1; tracker.InvalidateVisual(); return; } rem -= length;
        }
    }
    private void PreviewInstrument()
    {
        try
        {
            Stop(); var s = DemoSong.CreateEmpty(); var ins = InstrumentFile.Clone(editor.Song.FindInstrument(selectedInstrument)!); s.Instruments.Clear(); s.Instruments.Add(ins); s.Tracks[0].InstrumentId = ins.Id; var p = s.Patterns[0]; p.Length = 8; s.Tracks.RemoveRange(1, s.Tracks.Count - 1); p.Tracks.RemoveAll(t => t.TrackId != s.Tracks[0].Id); p.Tracks[0].Rows = Enumerable.Range(0, 8).Select(_ => new NoteEvent()).ToList(); var notes = p.GetTrack(s.Tracks[0].Id).Rows; notes[0] = new() { Kind = NoteKind.Note, Pitch = 60 }; notes[4] = new() { Kind = NoteKind.Off }; p.Drums.Clear(); SongFile.Validate(s); player.Play(s); previewing = true; SetPlayingVisual(true); SetStatus($"Previewing {ins.Name} · C4");
        }
        catch (Exception e) { SetStatus("Preview: " + e.Message); }
    }
    private void GlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (OverlayRoot.Children.Count > 1) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl)
        {
            switch (e.Key) { case Key.S: _ = SaveSong(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); break; case Key.O: _ = OpenSong(); break; case Key.N: _ = NewSong(); break; case Key.Z: if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo(); else Undo(); break; case Key.Y: Redo(); break; case Key.L: TogglePane(true); break; case Key.I: TogglePane(false); break; default: return; } e.Handled = true;
        }
        else if (e.Key == Key.Space && e.Source is not TextBox && FocusManager?.GetFocusedElement() is not TextBox) { TogglePlay(); e.Handled = true; }
        else if (e.Key == Key.F5) { PlayFromCursor(); e.Handled = true; }
        else if (e.Key == Key.F6) { PlayPattern(); e.Handled = true; }
        else if (e.Key == Key.F1) { _ = ShowHelp(); e.Handled = true; }
    }
    private void ResizePattern(int length)
    {
        var p = editor.Song.FindPattern(activePattern)!; if (p.Length == length) return;
        if (length < p.Length && (p.Tracks.Any(t => t.Rows.Skip(length).Any(n => n.Kind != NoteKind.Empty || n.InstrumentId is not null || n.Effects.Any(x => x.Length > 0))) || p.Drums.Any(d => d.Steps.Skip(length).Any(v => v > 0)))) { SetStatus("Shortening would remove notes. Clear the later rows first, or duplicate this pattern."); refreshing = true; rows.Value = p.Length; refreshing = false; return; }
        Change(s => { var target = s.FindPattern(activePattern)!; target.Length = length; foreach (var t in target.Tracks) { while (t.Rows.Count < length) t.Rows.Add(new()); if (t.Rows.Count > length) t.Rows.RemoveRange(length, t.Rows.Count - length); } foreach (var d in target.Drums) { while (d.Steps.Count < length) d.Steps.Add(0); if (d.Steps.Count > length) d.Steps.RemoveRange(length, d.Steps.Count - length); } });
    }
    private void ChangeGrid(int newRowsPerBeat)
    {
        var old = editor.Song.RowsPerBeat; if (old == newRowsPerBeat) return;
        if (newRowsPerBeat < old)
        {
            var factor = old / newRowsPerBeat;
            if (editor.Song.Patterns.Any(p => p.Length % factor != 0 || p.Tracks.Any(t => t.Rows.Where((_, i) => i % factor != 0).Any(n => n.Kind != NoteKind.Empty || !string.IsNullOrEmpty(n.InstrumentId) || n.Effects.Any(f => f.Length > 0))) || p.Drums.Any(d => d.Steps.Where((_, i) => i % factor != 0).Any(v => v > 0)))) { SetStatus("Coarser grid would lose off-grid notes, FX or drum hits. Existing music was preserved."); refreshing = true; gridResolution.SelectedIndex = (int)Math.Log2(old); refreshing = false; return; }
            Change(s => { foreach (var p in s.Patterns) { p.Length /= factor; foreach (var t in p.Tracks) t.Rows = t.Rows.Where((_, i) => i % factor == 0).ToList(); foreach (var d in p.Drums) d.Steps = d.Steps.Where((_, i) => i % factor == 0).ToList(); } foreach (var t in s.Tracks) foreach (var a in t.VolumeAutomation) a.Row /= factor; s.RowsPerBeat = newRowsPerBeat; }); SetStatus("Grid coarsened; note onsets preserved. Row-relative FX follow the new row duration."); return;
        }
        var scale = newRowsPerBeat / old;
        if (editor.Song.Patterns.Any(p => p.Length * scale > 256)) { SetStatus("This finer grid would exceed 256 rows. Split long patterns first."); refreshing = true; gridResolution.SelectedIndex = (int)Math.Log2(old); refreshing = false; return; }
        Change(s => { foreach (var p in s.Patterns) { p.Length *= scale; foreach (var t in p.Tracks) { var prior = t.Rows.ToArray(); t.Rows = Enumerable.Range(0, p.Length).Select(i => i % scale == 0 && i / scale < prior.Length ? prior[i / scale] : new NoteEvent()).ToList(); } foreach (var d in p.Drums) { var prior = d.Steps.ToArray(); d.Steps = Enumerable.Range(0, p.Length).Select(i => i % scale == 0 && i / scale < prior.Length ? prior[i / scale] : (byte)0).ToList(); } } foreach (var t in s.Tracks) foreach (var a in t.VolumeAutomation) a.Row *= scale; s.RowsPerBeat = newRowsPerBeat; }); SetStatus("Grid refined; musical note positions preserved");
    }
    private void AddInstrument() { var ins = new Instrument { Name = "New pulse", VolumeDb = -14 }; Change(s => s.Instruments.Add(ins)); selectedInstrument = ins.Id; Refresh(); }
    private void MakeLocal() { var originalId = selectedInstrument; var ins = InstrumentFile.Clone(editor.Song.FindInstrument(originalId)!); ins.Id = Guid.NewGuid().ToString("N"); ins.Name += " local"; ins.IsLocal = true; Change(s => { s.Instruments.Add(ins); if (s.Tracks.Count > 0) { var t = s.Tracks[chosenTrack]; t.InstrumentId = ins.Id; foreach (var p in s.Patterns) foreach (var n in p.GetTrack(t.Id).Rows) if (n.InstrumentId == originalId) n.InstrumentId = ins.Id; } }); selectedInstrument = ins.Id; Refresh(); SetStatus("Independent local copy assigned to this track and its matching sound sections"); }
    private void AddTrack() { Change(s => { var t = new Track { Name = $"Track {s.Tracks.Count + 1:00}", InstrumentId = selectedInstrument }; s.Tracks.Add(t); foreach (var p in s.Patterns) p.GetTrack(t.Id); }); }
    private void SetInstrumentEvent() { if (editor.Song.Tracks.Count == 0 || editor.Song.FindInstrument(selectedInstrument) is null) return; var r = tracker.SelectedRow; var t = editor.Song.Tracks[tracker.SelectedTrack].Id; Change(s => s.FindPattern(activePattern)!.GetTrack(t).Rows[r].InstrumentId = selectedInstrument); SetStatus("Instrument section header inserted; time position unchanged"); }
    private void AddPattern() { var p = new Pattern { Name = $"Pattern {editor.Song.Patterns.Count + 1:00}", Length = 32 }; Change(s => { foreach (var t in s.Tracks) p.GetTrack(t.Id); s.Patterns.Add(p); }); activePattern = p.Id; Refresh(); }
    private void DuplicatePattern() { var copy = SongFile.Clone(editor.Song).FindPattern(activePattern)!; copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy"; Change(s => s.Patterns.Add(copy)); activePattern = copy.Id; Refresh(); }    private async Task TrackOptions(int index)
    {
        if (index < 0 || index >= editor.Song.Tracks.Count || !tracker.CommitPending()) return;
        var id = editor.Song.Tracks[index].Id;
        var choice = await Ask(editor.Song.Tracks[index].Name, "Move this track in the editor or remove it and its notes from every pattern. Changes can be undone.", "Move left", "Move right", "Remove track", "Cancel");
        if (choice is "Move left" or "Move right")
        {
            int target = index + (choice == "Move left" ? -1 : 1); if (target < 0 || target >= editor.Song.Tracks.Count) return;
            Change(s => { var item = s.Tracks[index]; s.Tracks.RemoveAt(index); s.Tracks.Insert(target, item); }); chosenTrack = target; tracker.Select(tracker.SelectedRow, target, tracker.SelectedColumn); selectedInstrument = editor.Song.Tracks[target].InstrumentId; Refresh();
        }
        else if (choice == "Remove track")
        {
            Change(s => { s.Tracks.RemoveAll(t => t.Id == id); foreach (var p in s.Patterns) p.Tracks.RemoveAll(t => t.TrackId == id); });
            chosenTrack = Math.Clamp(index, 0, Math.Max(0, editor.Song.Tracks.Count - 1)); tracker.Select(tracker.SelectedRow, chosenTrack, tracker.SelectedColumn);
            if (editor.Song.Tracks.Count > 0) selectedInstrument = editor.Song.Tracks[chosenTrack].InstrumentId; Refresh();
        }
    }

}
