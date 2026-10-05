using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IconPacks.Avalonia.Material;
using MusicMachine.Core;
namespace MusicMachine.App;

public sealed partial class MainView
{
    private sealed class ViewSettings { public string Theme = "catppuccin-mocha"; public bool Library = true; public bool Inspector; public string InspectorContent = "instrument"; public bool CheckForUpdates = true; public string Workspace = "Tracker"; public int EffectColumns = 2; }
    private readonly ViewSettings viewSettings = new();
    private Grid? panes;
    private Border? libraryPane, inspectorPane;
    private readonly ContentControl inspectorHost = new();
    private readonly ScrollViewer libraryScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ComboBox workspacePicker = new() { ItemsSource = new[] { "Tracker", "Drums", "Instrument", "Arrangement", "Automation", "Sampling" }, Width = 150, FontSize = 12 };
    private readonly ComboBox centerItemPicker = new() { MinWidth = 140, MaxWidth = 280, FontSize = 12 };
    private readonly ComboBox libraryPicker = new() { ItemsSource = new[] { "Patterns", "Instruments" }, SelectedIndex = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<(MenuItem Item, string Theme)> themeItems = [];
    private MenuItem? libraryMenu, inspectorMenu, undoMenu, redoMenu;
    private bool playingVisual;
    private Button? loopButton;
    private TextBlock? projectLabel;
    private void LoadViewSettings()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(recoveryPath)!, "view-settings.txt");
            var text = EditorPlatform.LoadPreferences?.Invoke() ?? (File.Exists(path) ? File.ReadAllText(path) : "");
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split('=', 2); if (parts.Length != 2) continue;
                switch (parts[0]) { case "effectColumns": if (int.TryParse(parts[1].Trim(), out var count)) viewSettings.EffectColumns = Math.Clamp(count, 0, FxParser.MaxColumns); break; case "inspectorContent": viewSettings.InspectorContent = parts[1].Trim() == "fx" ? "fx" : "instrument"; break; case "checkForUpdates": viewSettings.CheckForUpdates = parts[1].Trim() != "false"; break; case "theme": viewSettings.Theme = parts[1].Trim(); break; case "library": viewSettings.Library = parts[1].Trim() != "false"; break; case "inspector": viewSettings.Inspector = parts[1].Trim() == "true"; break; case "workspace": if (new[] { "Tracker", "Drums", "Instrument", "Arrangement", "Automation", "Sampling" }.Contains(parts[1].Trim())) viewSettings.Workspace = parts[1].Trim(); break; }
            }
            mode = viewSettings.Workspace;
        }
        catch { }
        tracker.EffectColumns = viewSettings.EffectColumns;
    }
    private void SaveViewSettings()
    {
        var text = $"theme={viewSettings.Theme}\nlibrary={viewSettings.Library.ToString().ToLowerInvariant()}\ninspector={viewSettings.Inspector.ToString().ToLowerInvariant()}\nworkspace={mode}\ncheckForUpdates={viewSettings.CheckForUpdates.ToString().ToLowerInvariant()}\ninspectorContent={viewSettings.InspectorContent}\neffectColumns={viewSettings.EffectColumns}\n";
        try { if (EditorPlatform.SavePreferences is { } save) save(text); else AtomicWrite(Path.Combine(Path.GetDirectoryName(recoveryPath)!, "view-settings.txt"), System.Text.Encoding.UTF8.GetBytes(text)); } catch (Exception e) { SetStatus("View settings: " + e.Message); }
    }
    private Control BuildShell()
    {
        var root = new Grid { RowDefinitions = new("28,38,*,24") };
        var menuRow = new Grid { ColumnDefinitions = new("*,Auto"), Background = Ui.Background };
        menuRow.Children.Add(BuildMenus()); var project = Ui.Label(editor.Song.Title, 11, Ui.Muted); project.Name = "ProjectLabel"; projectLabel = project; project.Margin = new(12, 0); Grid.SetColumn(project, 1); menuRow.Children.Add(project); root.Children.Add(menuRow);
        var transport = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(8, 0) };
        var loop = Ui.IconButton(PackIconMaterialKind.Repeat, () => { loopToggle.IsChecked = loopToggle.IsChecked != true; RefreshChrome(); }, "Loop selected sections");
        loop.Name = "LoopIcon"; loopButton = loop;
        transport.Children.Add(Ui.Row(playButton, Ui.IconButton(PackIconMaterialKind.Stop, Stop, "Stop"), loop, transportPosition, Ui.Label("BPM", 9, Ui.Muted), tempo));
        var views = Ui.Row(Ui.IconButton(PackIconMaterialKind.DockLeft, () => TogglePane(true), "Show / hide library · Ctrl+L"), workspacePicker, centerItemPicker, Ui.IconButton(PackIconMaterialKind.DockRight, () => TogglePane(false), "Show / hide inspector · Ctrl+I")); Grid.SetColumn(views, 2); transport.Children.Add(views);
        workspacePicker.SelectionChanged += (_, _) => { if (!refreshing && workspacePicker.SelectedItem is string name && name != mode) SelectWorkspace(name); };
        centerItemPicker.SelectionChanged += (_, _) => { if (refreshing || centerItemPicker.SelectedIndex < 0) return; var n = centerItemPicker.SelectedIndex; if (mode is "Tracker" or "Drums") { if (n < editor.Song.Patterns.Count && tracker.CommitPending()) { activePattern = editor.Song.Patterns[n].Id; SynchronizeDrumSound(); Refresh(); } } else if (mode is "Instrument" or "Sampling") { if (n < editor.Song.Instruments.Count) { selectedInstrument = editor.Song.Instruments[n].Id; Refresh(); } } else if (mode == "Automation" && n < editor.Song.Tracks.Count) { chosenTrack = n; Refresh(); } };
        var transportBorder = Ui.Panel(transport, new Thickness(0)); transportBorder.BorderThickness = new(0, 1, 0, 1); Grid.SetRow(transportBorder, 1); root.Children.Add(transportBorder);
        panes = new Grid { ColumnDefinitions = new("180,*,248") }; Grid.SetRow(panes, 2);
        var libraryBody = new Grid { RowDefinitions = new("34,*"), Margin = new(8, 4) }; libraryBody.Children.Add(libraryPicker); Grid.SetRow(libraryScroll, 1); libraryBody.Children.Add(libraryScroll);
        libraryPicker.SelectionChanged += (_, _) => { libraryScroll.Content = null; libraryScroll.Content = libraryPicker.SelectedIndex == 0 ? patternList : library; };
        libraryPicker.SelectedIndex = mode is "Instrument" or "Sampling" ? 1 : 0;
        libraryScroll.Content = libraryPicker.SelectedIndex == 1 ? library : patternList; libraryPane = Ui.Panel(libraryBody, new Thickness(0)); panes.Children.Add(libraryPane);
        Grid.SetColumn(workArea, 1); panes.Children.Add(workArea);
        inspectorPane = Ui.Panel(BuildInspectorShell(), new Thickness(0)); inspectorPane.BorderThickness = new(1, 0, 0, 0); Grid.SetColumn(inspectorPane, 2); panes.Children.Add(inspectorPane); root.Children.Add(panes);
        status.Margin = new(10, 0); status.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetRow(status, 3); root.Children.Add(status);
        UpdatePaneLayout(); return root;
    }
    private Menu BuildMenus()
    {
        MenuItem Command(string label, Action action, string? shortcut = null)
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); if (shortcut is not null) item.InputGesture = KeyGesture.Parse(shortcut); return item;
        }
        MenuItem Group(string label, params Control[] items) => new() { Header = label, ItemsSource = items };
        var file = Group("_File", Command("New song", () => _ = NewSong(), "Ctrl+N"), Command("Open…", () => _ = OpenSong(), "Ctrl+O"), Command("Save", () => _ = SaveSong(), "Ctrl+S"), Command("Save as…", () => _ = SaveSong(true), "Ctrl+Shift+S"), new Separator(), Command("Export audio…", () => _ = ExportAudio()), Command("Import instrument…", () => _ = ImportInstrument()), Command("Import audio for sampling…", () => { SelectWorkspace("Sampling"); _ = samplingPanel.ImportAsync(); }), Command("Export instrument…", () => _ = ExportInstrument()), new Separator(), Command("Open demo song", () => _ = LoadDemo(), "Ctrl+Shift+D"), Command("Recover unsaved song…", () => _ = RecoverSong()));
        undoMenu = Command("Undo", Undo, "Ctrl+Z"); redoMenu = Command("Redo", Redo, "Ctrl+Shift+Z");
        var edit = Group("_Edit", undoMenu, redoMenu, Command("Copy tracker cells", () => _ = tracker.CopySelectionAsync(), "Ctrl+C"), Command("Paste tracker cells", () => _ = tracker.PasteSelectionAsync(), "Ctrl+V"), new Separator(), Command("Song settings…", () => _ = EditSongSettings()), Command("Rename song…", () => _ = EditText("Rename song", editor.Song.Title, t => Change(s => s.Title = t))));
        libraryMenu = Command("Library pane", () => TogglePane(true), "Ctrl+L"); inspectorMenu = Command("Inspector pane", () => TogglePane(false), "Ctrl+I");
        libraryMenu.ToggleType = MenuItemToggleType.CheckBox; inspectorMenu.ToggleType = MenuItemToggleType.CheckBox;
        var workspace = Group("Center workspace", new[] { "Tracker", "Drums", "Instrument", "Arrangement", "Automation", "Sampling" }.Select(name => Command(name, () => SelectWorkspace(name))).ToArray());
        var themes = Group("Theme", EditorThemes.All.Select(theme => { var item = Command(theme.Name, () => ApplyTheme(theme.Id)); item.ToggleType = MenuItemToggleType.Radio; themeItems.Add((item, theme.Id)); return item; }).ToArray());
        var effects = Group("FX columns", new[] { 0, 1, 2, 4, 6 }.Select(n => Command(n == 0 ? "Hide FX" : n.ToString(), () => SetEffectColumns(n))).ToArray());
        fxReferenceMenu = Command("FX reference", ShowFxReference, "F2"); fxReferenceMenu.ToggleType = MenuItemToggleType.CheckBox;
        var view = Group("_View", libraryMenu, inspectorMenu, fxReferenceMenu, workspace, themes, effects);
        var pattern = Group("_Pattern", Command("New pattern", AddPattern), Command("Duplicate pattern", DuplicatePattern), Command("Rename pattern…", () => _ = EditText("Rename pattern", editor.Song.FindPattern(activePattern)!.Name, t => Change(s => s.FindPattern(activePattern)!.Name = t))), Command("Pattern length…", () => _ = EditText("Pattern rows (4–256)", editor.Song.FindPattern(activePattern)!.Length.ToString(), t => { if (int.TryParse(t, out var n)) ResizePattern(Math.Clamp(n, 4, 256)); })), new Separator(), Command("Four-on-the-floor drums", () => ApplyDrumPreset(false)), Command("Broken beat drums", () => ApplyDrumPreset(true)), Command("Clear drum steps", () => Change(s => { foreach (var lane in s.FindPattern(activePattern)!.Drums) lane.Steps = Enumerable.Repeat((byte)0, s.FindPattern(activePattern)!.Length).ToList(); })));
        var track = Group("_Track", Command("Add track", AddTrack), Command("Move / remove selected track…", () => _ = TrackOptions(chosenTrack)), Command("Insert instrument change", SetInstrumentEvent));
        var instrument = Group("_Instrument", Command("New instrument", AddInstrument), Command("Make local copy", MakeLocal), Command("Edit in center", () => SelectWorkspace("Instrument")), Command("Sample a waveform", () => SelectWorkspace("Sampling")), Command("Audition", PreviewInstrument));
        if (!OperatingSystem.IsBrowser()) file.ItemsSource = ((Control[])file.ItemsSource!).Concat([new Separator(), Command("Configure FFmpeg…", () => _ = ConfigureSamplingFfmpeg())]).ToArray();
        var transport = Group("_Transport", Command("Play / stop", TogglePlay, "Space"), Command("Stop", Stop), Command("Play current pattern", PlayPattern, "F6"), Command("Play from cursor", PlayFromCursor, "F5"), Command("Toggle loop", () => { loopToggle.IsChecked = loopToggle.IsChecked != true; RefreshChrome(); }));
        return new Menu { ItemsSource = new Control[] { file, edit, view, pattern, track, instrument, transport, BuildHelpMenu(Command("Keyboard and workflow", () => _ = ShowHelp(), "F1")) }, FontSize = 12 };
    }
    private void RefreshChrome()
    {
        if (projectLabel is not null) projectLabel.Text = editor.Song.Title;
        UpdateReferenceContext();
        workspacePicker.SelectedItem = mode;
        if (mode is "Tracker" or "Drums") { centerItemPicker.IsVisible = true; centerItemPicker.ItemsSource = editor.Song.Patterns.Select(p => p.Name).ToArray(); centerItemPicker.SelectedIndex = editor.Song.Patterns.FindIndex(p => p.Id == activePattern); }
        else if (mode is "Instrument" or "Sampling") { centerItemPicker.IsVisible = true; centerItemPicker.ItemsSource = editor.Song.Instruments.Select(i => i.Name).ToArray(); centerItemPicker.SelectedIndex = editor.Song.Instruments.FindIndex(i => i.Id == selectedInstrument); }
        else if (mode == "Automation") { centerItemPicker.IsVisible = true; centerItemPicker.ItemsSource = editor.Song.Tracks.Select(t => t.Name).ToArray(); centerItemPicker.SelectedIndex = chosenTrack; }
        else centerItemPicker.IsVisible = false;
        if (undoMenu is not null) undoMenu.IsEnabled = editor.CanUndo; if (redoMenu is not null) redoMenu.IsEnabled = editor.CanRedo;
        if (libraryMenu is not null) libraryMenu.IsChecked = viewSettings.Library; if (inspectorMenu is not null) inspectorMenu.IsChecked = viewSettings.Inspector;
        foreach (var (item, id) in themeItems) item.IsChecked = id == viewSettings.Theme;
        if (loopButton is { } loop) { if (loopToggle.IsChecked == true) loop.Classes.Add("selected"); else loop.Classes.Remove("selected"); }
    }
    private void SetEffectColumns(int count)
    {
        if (!tracker.CommitPending()) return;
        var row = tracker.SelectedRow; var track = tracker.SelectedTrack; var column = tracker.SelectedColumn;
        count = Math.Clamp(count, 0, FxParser.MaxColumns);
        tracker.EffectColumns = viewSettings.EffectColumns = count;
        tracker.SetSong(editor.Song, activePattern); tracker.Select(row, track, Math.Min(column, count)); UpdateSelection(); SaveViewSettings(); RestoreInputFocus();
    }
    private void SelectWorkspace(string name)
    {
        if (!tracker.CommitPending()) return; mode = name; SynchronizeDrumSound(); if (name is "Instrument" or "Sampling") libraryPicker.SelectedIndex = 1; else if (name is "Tracker" or "Drums") libraryPicker.SelectedIndex = 0; SaveViewSettings(); Refresh();
    }
    private void SynchronizeDrumSound()
    {
        if (mode != "Drums") return; var lanes = editor.Song.FindPattern(activePattern)?.Drums;
        if (lanes is { Count: > 0 }) selectedInstrument = lanes[Math.Clamp(selectedDrumLane, 0, lanes.Count - 1)].InstrumentId;
    }
    private void TogglePane(bool left) { if (left) viewSettings.Library = !viewSettings.Library; else viewSettings.Inspector = !viewSettings.Inspector; UpdatePaneLayout(); SaveViewSettings(); RefreshChrome(); if (!left && !viewSettings.Inspector) RestoreInputFocus(); }
    private void UpdatePaneLayout()
    {
        if (panes is null) return; var inspect = viewSettings.Inspector && (mode != "Instrument" || ShowingFxReference);
        panes.ColumnDefinitions[0].Width = new GridLength(viewSettings.Library ? 180 : 0); panes.ColumnDefinitions[2].Width = new GridLength(inspect ? 248 : 0);
        if (libraryPane is not null) libraryPane.IsVisible = viewSettings.Library; if (inspectorPane is not null) inspectorPane.IsVisible = inspect;
    }
    private void PrepareInspector()
    {
        Ui.Detach(instrumentPanel); instrumentPanel.SetWideLayout(mode == "Instrument");
        if (ShowingFxReference)
        {
            fxReferencePanel ??= new FxReferencePanel(InsertReferenceEffect);
            if (!ReferenceEquals(inspectorHost.Content, fxReferencePanel)) { Ui.Detach(fxReferencePanel); inspectorHost.Content = fxReferencePanel; }
        }
        else inspectorHost.Content = mode != "Instrument" ? instrumentPanel : null;
        UpdateReferenceContext(); UpdatePaneLayout();
    }
    private Control BuildInstrumentWorkspace() => new ScrollViewer { Content = instrumentPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(12, 8) };
    private Control BuildTracker()
    {
        Ui.Detach(tracker); return new ScrollViewer { Content = tracker, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(8) };
    }
    private Control BuildAutomationWorkspace()
    {
        if (editor.Song.Tracks.Count == 0) return Ui.Label("Add a track from the Track menu to draw automation", 13, Ui.Muted);
        var song = editor.Song; var track = song.Tracks[chosenTrack]; var total = song.Arrangement.Sum(s => song.FindPattern(s.PatternId)!.Length * s.Repeats);
        var panel = new Grid { RowDefinitions = new("30,*,24"), Margin = new(16) }; panel.Children.Add(Ui.Label(track.Name + " / Volume", 13));
        var curve = new AutomationCanvas(song, track, total) { Height = double.NaN, MinHeight = 240 }; curve.Commit += points => Change(s => s.Tracks.First(t => t.Id == track.Id).VolumeAutomation = points); curve.Status += SetStatus; Grid.SetRow(curve, 1); panel.Children.Add(curve);
        var hint = Ui.Label("Click to add · drag to shape · right-click to remove", 10, Ui.Muted); Grid.SetRow(hint, 2); panel.Children.Add(hint); return panel;
    }
    private void ApplyTheme(string id) { viewSettings.Theme = id; EditorThemes.Apply(id); SaveViewSettings(); Refresh(); tracker.InvalidateVisual(); }
    private void SetPlayingVisual(bool playing) { playingVisual = playing; playButton.Content = new PackIconMaterial { Kind = playing ? PackIconMaterialKind.Pause : PackIconMaterialKind.Play, Width = 16, Height = 16 }; }
    private async Task EditText(string caption, string initial, Action<string> apply)
    {
        var box = new TextBox { Text = initial, MaxLength = 128 }; var dialog = new EditorDialog { Title = caption, Width = 400, SizeToContent = SizeToContent.Height };
        var panel = new StackPanel { Margin = new(20), Spacing = 12 }; panel.Children.Add(Ui.Label(caption, 16)); panel.Children.Add(box); panel.Children.Add(Ui.Row(Ui.Button("Cancel", () => dialog.Close()), Ui.Button("Apply", () => { if (!string.IsNullOrWhiteSpace(box.Text)) { apply(box.Text.Trim()); dialog.Close(); } }))); dialog.Content = panel; await dialog.ShowDialog(this);
    }
    private async Task EditSongSettings()
    {
        var s = editor.Song; var bpm = new NumericUpDown { Value = (decimal)s.Bpm, Minimum = 20, Maximum = 400 }; var numerator = new NumericUpDown { Value = s.BeatsPerBar, Minimum = 1, Maximum = 16 }; var denominator = new ComboBox { ItemsSource = new[] { "2", "4", "8", "16" }, SelectedIndex = (int)Math.Log2(s.BeatUnit) - 1 }; var amount = new NumericUpDown { Value = (decimal)(s.Swing * 100), Minimum = 0, Maximum = 75 }; var grid = new ComboBox { ItemsSource = new[] { "1/4", "1/8", "1/16", "1/32", "1/64" }, SelectedIndex = (int)Math.Log2(s.RowsPerBeat) };
        var dialog = new EditorDialog { Title = "Song settings", Width = 370, SizeToContent = SizeToContent.Height }; var panel = new StackPanel { Margin = new(20), Spacing = 10 };
        panel.Children.Add(Ui.Label("Song settings", 17)); panel.Children.Add(Field("BPM", bpm)); panel.Children.Add(Field("Meter", Ui.Row(numerator, Ui.Label("/"), denominator))); panel.Children.Add(Field("Swing %", amount)); panel.Children.Add(Field("Grid", grid));
        panel.Children.Add(Ui.Row(Ui.Button("Cancel", () => dialog.Close()), Ui.Button("Apply", () => { Change(song => { song.Bpm = (double)(bpm.Value ?? 120); song.BeatsPerBar = (int)(numerator.Value ?? 4); song.BeatUnit = 1 << (denominator.SelectedIndex + 1); song.Swing = (double)(amount.Value ?? 0) / 100; }); ChangeGrid(1 << grid.SelectedIndex); dialog.Close(); }))); dialog.Content = panel; await dialog.ShowDialog(this);
    }
}
