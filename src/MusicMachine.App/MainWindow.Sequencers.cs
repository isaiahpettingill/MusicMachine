using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MusicMachine.Core;
using IconPacks.Avalonia.Material;

namespace MusicMachine.App;

public sealed partial class MainView
{
    private int selectedSection;
    private int selectedDrumLane;
    private int selectedDrumStep;
    private DrumGrid? drumGridSurface;
    private Vector drumScrollOffset;

    private Control BuildArrangement()
    {
        var song = editor.Song;
        selectedSection = Math.Clamp(selectedSection, 0, song.Arrangement.Count - 1);
        var body = new StackPanel { Spacing = 12, Margin = new(14, 4, 14, 16) };
        var totalRows = song.Arrangement.Sum(s => song.FindPattern(s.PatternId)!.Length * s.Repeats);
        var totalBeats = totalRows / (double)song.RowsPerBeat;
        var summary = new Grid { ColumnDefinitions = new("*,Auto") };
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(Ui.Label("Arrangement", 14));
        heading.Children.Add(Ui.Label($"{song.Arrangement.Count} sections  ·  {totalBeats / (song.BeatsPerBar * 4.0 / song.BeatUnit):0.#} bars  ·  {totalBeats * 60 / song.Bpm:0.0} seconds", 11, Ui.Muted));
        summary.Children.Add(heading);
        var append = Ui.IconButton(PackIconMaterialKind.Plus, () =>
        {
            var id = activePattern;
            selectedSection = editor.Song.Arrangement.Count;
            Change(s => { var extendLoop = s.LoopEndSection == s.Arrangement.Count; s.Arrangement.Add(new() { PatternId = id }); if (extendLoop) s.LoopEndSection++; });
            SetStatus("Pattern appended · edits to a library pattern update every instance");
        }, "Append the selected library pattern to the song", true);
        append.IsEnabled = song.Arrangement.Count < SongLimits.MaxSections;
        Grid.SetColumn(append, 1); summary.Children.Add(append); body.Children.Add(summary);

        var timeline = new ArrangementCanvas(song, selectedSection, chosenTrack);
        timeline.SectionSelected += (section, track) =>
        {
            selectedSection = section; activePattern = editor.Song.Arrangement[section].PatternId;
            if (track >= 0) { chosenTrack = track; selectedInstrument = editor.Song.Tracks[track].InstrumentId; }
            Refresh();
        };
        body.Children.Add(timeline);

        var section = song.Arrangement[selectedSection];
        var pattern = song.FindPattern(section.PatternId)!;
        var sectionTop = new Grid { ColumnDefinitions = new("*,Auto") };
        sectionTop.Children.Add(Ui.Label($"SECTION {selectedSection + 1:00}  ·  {pattern.Name}", 11, Ui.Accent));
        var moveLeft = Ui.IconButton(PackIconMaterialKind.ArrowLeft, () => MoveSection(-1), "Move selected section earlier"); moveLeft.IsEnabled = selectedSection > 0;
        var moveRight = Ui.IconButton(PackIconMaterialKind.ArrowRight, () => MoveSection(1), "Move selected section later"); moveRight.IsEnabled = selectedSection < song.Arrangement.Count - 1;
        var remove = Ui.IconButton(PackIconMaterialKind.DeleteOutline, RemoveSection, "Remove this instance; keep the reusable library pattern"); remove.IsEnabled = song.Arrangement.Count > 1;
        var order = Ui.Row(moveLeft, moveRight, remove); Grid.SetColumn(order, 1); sectionTop.Children.Add(order);
        var sectionBox = new StackPanel { Spacing = 9 }; sectionBox.Children.Add(sectionTop);
        var sectionIndex = selectedSection;
        var repeat = Number(section.Repeats, 1, 128, 1, 74, v => Change(s => s.Arrangement[sectionIndex].Repeats = (int)v));
        var transpose = Number(section.Transpose, -48, 48, 1, 74, v => Change(s => s.Arrangement[sectionIndex].Transpose = (int)v));
        var loopLabels = song.Arrangement.Select((s, i) => $"{i + 1:00} · {song.FindPattern(s.PatternId)!.Name}").ToArray();
        var loopStart = new ComboBox { ItemsSource = loopLabels, SelectedIndex = song.LoopStartSection, Width = 125, FontSize = 11 };
        var loopEnd = new ComboBox { ItemsSource = loopLabels, SelectedIndex = song.LoopEndSection - 1, Width = 125, FontSize = 11 };
        loopStart.SelectionChanged += (_, _) => { if (loopStart.SelectedIndex >= 0 && loopStart.SelectedIndex != song.LoopStartSection) Change(s => { s.LoopStartSection = loopStart.SelectedIndex; s.LoopEndSection = Math.Max(s.LoopEndSection, s.LoopStartSection + 1); }); };
        loopEnd.SelectionChanged += (_, _) => { if (loopEnd.SelectedIndex >= 0 && loopEnd.SelectedIndex + 1 != song.LoopEndSection) Change(s => { s.LoopEndSection = loopEnd.SelectedIndex + 1; s.LoopStartSection = Math.Min(s.LoopStartSection, s.LoopEndSection - 1); }); };
        ToolTip.SetTip(loopStart, "Loop starts at the beginning of this section"); ToolTip.SetTip(loopEnd, "Loop ends after this section");
        var settings = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in new Control[] { Field("REPEAT", repeat), Field("TRANSPOSE", transpose), Field("LOOP START", loopStart), Field("LOOP THROUGH", loopEnd) }) { item.Margin = new(0, 0, 10, 0); settings.Children.Add(item); }
        sectionBox.Children.Add(settings); body.Children.Add(SequencerPanel(sectionBox));

        var mixer = new StackPanel { Spacing = 5 };
        var mixTop = new Grid { ColumnDefinitions = new("*,Auto") }; mixTop.Children.Add(Ui.Heading("TRACK MIXER"));
        var trim = Ui.Row(Ui.Label("MIX TRIM", 9, Ui.Muted), Ui.IconButton(PackIconMaterialKind.Minus, () => TrimMix(-1), "Lower all track and drum-lane gains equally; retain the mix balance"), Ui.IconButton(PackIconMaterialKind.Plus, () => TrimMix(1), "Raise all track and drum-lane gains equally; retain the mix balance"));
        Grid.SetColumn(trim, 1); mixTop.Children.Add(trim); mixer.Children.Add(mixTop);
        var labels = new Grid { ColumnDefinitions = new("28,*,144,79,79,34,34"), Margin = new(0, 2) };
        string[] captions = ["", "TRACK", "DEFAULT SOUND", "GAIN dB", "PAN", "M", "S"];
        for (var i = 0; i < captions.Length; i++) { var label = Ui.Label(captions[i], 9, Ui.Muted); label.Margin = new(3, 0); Grid.SetColumn(label, i); labels.Children.Add(label); }
        mixer.Children.Add(labels);
        for (var i = 0; i < song.Tracks.Count; i++)
        {
            var index = i; var track = song.Tracks[i]; var id = track.Id;
            var row = new Grid { ColumnDefinitions = new("28,*,144,79,79,34,34"), Height = 37 };
            var choose = Ui.Button($"{i + 1:00}", () => { chosenTrack = index; selectedInstrument = track.InstrumentId; Refresh(); }, $"Edit {track.Name} automation and instrument");
            choose.Padding = new(2); choose.FontSize = 10; choose.Foreground = Ui.ThemeBrush(track.Color);
            if (i == chosenTrack) choose.Classes.Add("selected"); row.Children.Add(choose);
            var name = new TextBox { Text = track.Name, FontSize = 11, Margin = new(3, 0), Padding = new(6, 5), MaxLength = 128 };
            name.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && name.Text != track.Name) Change(s => s.Tracks.First(t => t.Id == id).Name = name.Text.Trim()); };
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { choose.Focus(); e.Handled = true; } };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var sounds = new ComboBox { ItemsSource = song.Instruments.Select(x => x.Name).ToArray(), SelectedIndex = song.Instruments.FindIndex(x => x.Id == track.InstrumentId), FontSize = 11, Margin = new(3, 0), Padding = new(6, 5), HorizontalAlignment = HorizontalAlignment.Stretch };
            sounds.SelectionChanged += (_, _) => { if (sounds.SelectedIndex >= 0 && song.Instruments[sounds.SelectedIndex].Id != track.InstrumentId) { var soundId = song.Instruments[sounds.SelectedIndex].Id; selectedInstrument = soundId; chosenTrack = index; Change(s => s.Tracks.First(t => t.Id == id).InstrumentId = soundId); } };
            Grid.SetColumn(sounds, 2); row.Children.Add(sounds);
            var gain = Number(track.VolumeDb, -96, 12, 1, 73, v => Change(s => s.Tracks.First(t => t.Id == id).VolumeDb = v)); gain.Margin = new(3, 0); Grid.SetColumn(gain, 3); row.Children.Add(gain);
            var pan = Number(track.Pan, -1, 1, .1, 73, v => Change(s => s.Tracks.First(t => t.Id == id).Pan = v), "0.0"); pan.Margin = new(3, 0); Grid.SetColumn(pan, 4); row.Children.Add(pan); ToolTip.SetTip(pan, "Pan: −1 left · 0 center · +1 right");
            var mute = Ui.IconButton(track.Muted ? PackIconMaterialKind.VolumeOff : PackIconMaterialKind.VolumeHigh, () => Change(s => s.Tracks.First(t => t.Id == id).Muted = !track.Muted), $"{(track.Muted ? "Unmute" : "Mute")} {track.Name}"); mute.Padding = new(6); mute.Margin = new(2, 0); if (track.Muted) mute.Classes.Add("selected"); Grid.SetColumn(mute, 5); row.Children.Add(mute);
            var solo = Ui.IconButton(PackIconMaterialKind.Headphones, () => Change(s => s.Tracks.First(t => t.Id == id).Solo = !track.Solo), $"{(track.Solo ? "Unsolo" : "Solo")} {track.Name}"); solo.Padding = new(6); solo.Margin = new(2, 0); if (track.Solo) solo.Classes.Add("selected"); Grid.SetColumn(solo, 6); row.Children.Add(solo);
            mixer.Children.Add(row);
        }
        if (song.Tracks.Count == 0) mixer.Children.Add(Ui.Button("+ Add melodic track", AddTrack));
        body.Children.Add(SequencerPanel(mixer));

        if (song.Tracks.Count > 0)
        {
            var track = song.Tracks[chosenTrack]; var id = track.Id;
            var automation = new StackPanel { Spacing = 6 };
            var autoTop = new Grid { ColumnDefinitions = new("*,Auto") };
            autoTop.Children.Add(Ui.Label($"{track.Name}  /  Volume automation", 12, Ui.ThemeBrush(track.Color)));
            var reset = Ui.IconButton(PackIconMaterialKind.Eraser, () => Change(s => s.Tracks.First(t => t.Id == id).VolumeAutomation.Clear()), "Clear this track's song-wide volume automation"); reset.IsEnabled = track.VolumeAutomation.Count > 0;
            Grid.SetColumn(reset, 1); autoTop.Children.Add(reset); automation.Children.Add(autoTop);
            automation.Children.Add(Ui.Label("Click to add · drag to shape · right-click to remove · absolute song rows", 10, Ui.Muted));
            var canvas = new AutomationCanvas(song, track, totalRows);
            canvas.Commit += points => { Change(s => s.Tracks.First(t => t.Id == id).VolumeAutomation = points); SetStatus($"{track.Name} volume automation updated"); };
            canvas.Status += SetStatus; automation.Children.Add(canvas); body.Children.Add(SequencerPanel(automation));
        }
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void MoveSection(int delta)
    {
        var from = selectedSection; var to = from + delta;
        if (to < 0 || to >= editor.Song.Arrangement.Count) return;
        selectedSection = to;
        Change(s => (s.Arrangement[from], s.Arrangement[to]) = (s.Arrangement[to], s.Arrangement[from]));
    }

    private void RemoveSection()
    {
        if (editor.Song.Arrangement.Count <= 1) { SetStatus("Keep at least one section in the arrangement"); return; }
        var index = selectedSection;
        Change(s =>
        {
            s.Arrangement.RemoveAt(index);
            if (index < s.LoopStartSection) s.LoopStartSection--;
            if (index < s.LoopEndSection) s.LoopEndSection--;
            s.LoopStartSection = Math.Clamp(s.LoopStartSection, 0, s.Arrangement.Count - 1);
            s.LoopEndSection = Math.Clamp(s.LoopEndSection, s.LoopStartSection + 1, s.Arrangement.Count);
        });
    }

    private void TrimMix(double requested)
    {
        var song = editor.Song;
        var levels = song.Tracks.Select(t => t.VolumeDb).Concat(song.Patterns.SelectMany(p => p.Drums).Select(d => d.VolumeDb)).ToArray();
        if (levels.Length == 0) return;
        var delta = Math.Clamp(requested, -96 - levels.Min(), 12 - levels.Max());
        if (Math.Abs(delta) < .0001) { SetStatus("Mix trim reached a gain limit; relative levels are preserved"); return; }
        Change(s => { foreach (var t in s.Tracks) t.VolumeDb += delta; foreach (var lane in s.Patterns.SelectMany(p => p.Drums)) lane.VolumeDb += delta; });
        SetStatus($"All track and drum-lane levels {(delta > 0 ? "+" : "")}{delta:0.#} dB · mix balance preserved");
    }

    private Control BuildDrums()
    {
        var song = editor.Song; var pattern = song.FindPattern(activePattern)!; var pid = pattern.Id;
        selectedDrumLane = Math.Clamp(selectedDrumLane, 0, Math.Max(0, pattern.Drums.Count - 1));
        selectedDrumStep = Math.Clamp(selectedDrumStep, 0, pattern.Length - 1);
        var body = new StackPanel { Spacing = 12, Margin = new(14, 4, 14, 16) };
        var top = new Grid { ColumnDefinitions = new("*,Auto") };
        var title = new StackPanel { Spacing = 4 }; title.Children.Add(Ui.Label("Drums", 14)); title.Children.Add(Ui.Label($"{pattern.Name}  ·  {pattern.Length} steps  ·  {song.RowsPerBeat} steps / beat", 11, Ui.Muted)); top.Children.Add(title);
        var add = Ui.IconButton(PackIconMaterialKind.Plus, AddDrumLane, "Add a lane using the selected drum instrument", true); add.IsEnabled = pattern.Drums.Count < SongLimits.MaxDrumLanes; Grid.SetColumn(add, 1); top.Children.Add(add); body.Children.Add(top);
        if (pattern.Drums.Count > 0)
        {
            var kit = new Grid { ColumnDefinitions = new("152,*") };
            var laneHeaders = new StackPanel { Spacing = 0 }; laneHeaders.Children.Add(new Border { Height = DrumGrid.HeaderHeight, Child = Ui.Label("SOUND / LEVEL", 9, Ui.Muted) });
            for (var i = 0; i < pattern.Drums.Count; i++)
            {
                var index = i; var lane = pattern.Drums[i];
                var header = new StackPanel { Spacing = 5, Margin = new(0, 7, 8, 5) };
                var select = Ui.Button(lane.Name, () => { selectedDrumLane = index; selectedInstrument = lane.InstrumentId; Refresh(); }, $"Edit {lane.Name} sound in the instrument panel"); select.FontSize = 11; select.Padding = new(6, 3); select.HorizontalAlignment = HorizontalAlignment.Stretch; select.HorizontalContentAlignment = HorizontalAlignment.Left; if (index == selectedDrumLane) select.Classes.Add("selected"); header.Children.Add(select);
                var mute = Ui.IconButton(lane.Muted ? PackIconMaterialKind.VolumeOff : PackIconMaterialKind.VolumeHigh, () => Change(s => s.FindPattern(pid)!.Drums[index].Muted = !lane.Muted), $"{(lane.Muted ? "Unmute" : "Mute")} {lane.Name}"); mute.FontSize = 10; mute.Padding = new(7, 4); if (lane.Muted) mute.Classes.Add("selected");
                var gain = Number(lane.VolumeDb, -96, 12, 1, 77, v => Change(s => s.FindPattern(pid)!.Drums[index].VolumeDb = v)); gain.FontSize = 10; gain.MinHeight = 26; gain.Padding = new(4, 2);
                header.Children.Add(Ui.Row(mute, gain, Ui.Label("dB", 9, Ui.Muted)));
                laneHeaders.Children.Add(new Border { Height = DrumGrid.LaneHeight, Child = header, BorderBrush = Ui.Line, BorderThickness = new(0, 0, 0, 1) });
            }
            kit.Children.Add(laneHeaders);
            var steps = new DrumGrid(song, pattern, selectedDrumLane, selectedDrumStep);
            drumGridSurface = steps;
            steps.SelectHit += (lane, step) => { selectedDrumLane = lane; selectedDrumStep = step; selectedInstrument = pattern.Drums[lane].InstrumentId; Refresh(); FocusDrumStep(); };
            steps.ChangeHit += (lane, step, velocity) => { selectedDrumLane = lane; selectedDrumStep = step; selectedInstrument = pattern.Drums[lane].InstrumentId; Change(s => s.FindPattern(pid)!.Drums[lane].Steps[step] = velocity); SetStatus($"{pattern.Drums[lane].Name} · step {step + 1:00} · {(velocity == 0 ? "off" : $"velocity {velocity / 255.0:P0}")}"); FocusDrumStep(); };
            var scroll = new ScrollViewer { Content = steps, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }; var priorOffset = drumScrollOffset; scroll.Loaded += (_, _) => scroll.Offset = priorOffset; scroll.ScrollChanged += (_, _) => drumScrollOffset = scroll.Offset;
            Grid.SetColumn(scroll, 1); kit.Children.Add(scroll); body.Children.Add(kit);
            var selectedLane = pattern.Drums[selectedDrumLane]; var laneIndex = selectedDrumLane; var stepIndex = selectedDrumStep;
            var inspector = new WrapPanel { Orientation = Orientation.Horizontal };
            var info = Ui.Label($"{selectedLane.Name}  ·  STEP {selectedDrumStep + 1:00}", 11, Ui.Accent); info.Margin = new(0, 0, 12, 0); inspector.Children.Add(info);
            var velocity = Number(Math.Round(selectedLane.Steps[stepIndex] / 255.0 * 100), 0, 100, 1, 83, v => Change(s => s.FindPattern(pid)!.Drums[laneIndex].Steps[stepIndex] = (byte)Math.Round(v / 100 * 255)));
            inspector.Children.Add(Field("VELOCITY %", velocity));
            var clearLane = Ui.IconButton(PackIconMaterialKind.Eraser, () => Change(s => s.FindPattern(pid)!.Drums[laneIndex].Steps = Enumerable.Repeat((byte)0, pattern.Length).ToList()), "Clear this drum lane"); clearLane.Margin = new(12, 0, 6, 0); inspector.Children.Add(clearLane);
            inspector.Children.Add(Ui.IconButton(PackIconMaterialKind.DeleteOutline, () => Change(s => s.FindPattern(pid)!.Drums.RemoveAt(laneIndex)), "Remove this drum lane")); body.Children.Add(SequencerPanel(inspector));
        }
        else
        {
            var empty = new StackPanel { Spacing = 10, Margin = new(14, 28) }; empty.Children.Add(Ui.Label("Start a rhythm", 17, Ui.Accent)); empty.Children.Add(Ui.Label("Choose a drum sound in the library and add a lane,\nor start with one of the ready-to-play beats above.", 12, Ui.Muted)); body.Children.Add(SequencerPanel(empty));
        }

        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void FocusDrumStep()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            drumGridSurface?.Focus();
            if (drumGridSurface is { } surface) surface.BringIntoView(surface.CellRect(selectedDrumLane, selectedDrumStep));
        });
    }

    private void AddDrumLane()
    {
        var instrument = editor.Song.FindInstrument(selectedInstrument)!;
        if (instrument.Drum == DrumKind.None) { SetStatus("Select a drum sound (◈) in the instrument library, then add a lane"); return; }
        var pid = activePattern; selectedDrumLane = editor.Song.FindPattern(pid)!.Drums.Count;
        Change(s => { var p = s.FindPattern(pid)!; p.Drums.Add(new() { Name = instrument.Name, InstrumentId = instrument.Id, Steps = Enumerable.Repeat((byte)0, p.Length).ToList() }); });
    }

    private void ApplyDrumPreset(bool broken)
    {
        var pid = activePattern;
        Change(s =>
        {
            var p = s.FindPattern(pid)!;
            foreach (var kind in new[] { DrumKind.Kick, DrumKind.Snare, DrumKind.ClosedHat })
            {
                if (p.Drums.Any(d => s.FindInstrument(d.InstrumentId)?.Drum == kind)) continue;
                var sound = s.Instruments.FirstOrDefault(i => i.Drum == kind);
                if (sound is null) { sound = InstrumentLibrary.CreatePresets().First(i => i.Drum == kind); sound.Id = Guid.NewGuid().ToString("N"); sound.IsLocal = true; s.Instruments.Add(sound); }
                if (p.Drums.Count < SongLimits.MaxDrumLanes) p.Drums.Add(new() { Name = sound.Name, InstrumentId = sound.Id, Steps = Enumerable.Repeat((byte)0, p.Length).ToList() });
            }
            foreach (var lane in p.Drums)
            {
                var kind = s.FindInstrument(lane.InstrumentId)?.Drum ?? DrumKind.None;
                lane.Steps = Enumerable.Repeat((byte)0, p.Length).ToList();
                for (var row = 0; row < p.Length; row++)
                {
                    var metricRows = Math.Max(1, s.RowsPerBeat * 4 / s.BeatUnit); var beat = row / metricRows; var within = row % metricRows; var inBar = beat % s.BeatsPerBar;
                    switch (kind)
                    {
                        case DrumKind.Kick:
                            if (!broken && within == 0 || broken && (within == 0 && (inBar == 0 || inBar == 2) || inBar == s.BeatsPerBar - 1 && within == s.RowsPerBeat / 2)) lane.Steps[row] = 232;
                            break;
                        case DrumKind.Snare:
                        case DrumKind.Clap:
                            if (within == 0 && inBar % 2 == 1) lane.Steps[row] = 215;
                            if (broken && s.RowsPerBeat >= 4 && inBar == s.BeatsPerBar - 1 && within == s.RowsPerBeat - 1) lane.Steps[row] = 82;
                            break;
                        case DrumKind.ClosedHat:
                            if (within == 0 || within == s.RowsPerBeat / 2) lane.Steps[row] = (byte)(within == 0 ? 145 : 96);
                            break;
                        case DrumKind.OpenHat:
                            if (inBar == s.BeatsPerBar - 1 && within == s.RowsPerBeat / 2) lane.Steps[row] = 145;
                            break;
                    }
                }
            }
        });
        SetStatus($"{(broken ? "Broken beat" : "Four on floor")} applied to this pattern · Undo restores your previous steps");
    }

    private NumericUpDown Number(double value, double min, double max, double increment, double width, Action<double> changed, string format = "0.#")
    {
        var control = new NumericUpDown { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)increment, Value = (decimal)value, Width = width, FormatString = format, FontSize = 11, Padding = new(5, 4) };
        var committed = value;
        void CommitValue()
        {
            if (refreshing || control.Value is not { } v || Math.Abs((double)v - committed) < .00001) return;
            committed = (double)v; changed(committed);
        }
        // Preserve multi-digit typing. A spinner click, Enter, or focus loss commits one edit.
        control.ValueChanged += (_, _) => { if (refreshing && control.Value is { } v) committed = (double)v; else if (!control.IsKeyboardFocusWithin) CommitValue(); };
        control.LostFocus += (_, _) => { if (!control.IsKeyboardFocusWithin) CommitValue(); };
        control.PointerReleased += (_, _) => CommitValue();
        control.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitValue(); e.Handled = true; } };
        control.KeyUp += (_, e) => { if (e.Key is Key.Up or Key.Down) CommitValue(); };
        return control;
    }
    private static StackPanel Field(string label, Control editor) { var panel = Ui.Row(Ui.Label(label, 9, Ui.Muted), editor); return panel; }
    private static Border SequencerPanel(Control content) => new() { Child = content, Padding = new(10), Background = Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(6) };
}

internal sealed class ArrangementCanvas : Control
{
    private readonly Song song;
    private readonly int selected;
    private readonly int selectedTrack;
    private readonly double[] starts;
    private const double LabelWidth = 100, Header = 65, TrackHeight = 29;
    private double Total => starts[^1];
    private double PlotWidth => Math.Max(1, Bounds.Width - LabelWidth - 6);
    private double X(double row) => LabelWidth + row / Math.Max(1, Total) * PlotWidth;
    public event Action<int, int>? SectionSelected;
    public ArrangementCanvas(Song song, int selected, int selectedTrack)
    {
        this.song = song; this.selected = selected; this.selectedTrack = selectedTrack;
        starts = new double[song.Arrangement.Count + 1];
        for (var i = 0; i < song.Arrangement.Count; i++) starts[i + 1] = starts[i] + song.FindPattern(song.Arrangement[i].PatternId)!.Length * song.Arrangement[i].Repeats;
        Height = Header + Math.Max(1, song.Tracks.Count) * TrackHeight + 13; MinWidth = 420; ClipToBounds = true; Cursor = new(StandardCursorType.Hand);
        Avalonia.Automation.AutomationProperties.SetName(this, "Song arrangement overview. Click a section or track to select it.");
    }
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx); ctx.FillRectangle(Ui.Background, new Rect(Bounds.Size));
        var loopStart = X(starts[song.LoopStartSection]); var loopEnd = X(starts[song.LoopEndSection]);
        ctx.FillRectangle(Ui.ThemeBrush("#21463D"), new Rect(loopStart, 1, Math.Max(1, loopEnd - loopStart), 17));
        ctx.DrawLine(new Pen(Ui.Accent, 2), new Point(loopStart, 1), new Point(loopStart, 19)); ctx.DrawLine(new Pen(Ui.Accent, 2), new Point(loopEnd, 1), new Point(loopEnd, 19));
        ctx.DrawText(Ui.Fmt("LOOP", 8, Ui.Accent, true), new Point(loopStart + 5, 3));
        ctx.DrawText(Ui.Fmt("SECTIONS", 9, Ui.Muted), new Point(0, 34));
        for (var i = 0; i < song.Arrangement.Count; i++)
        {
            var section = song.Arrangement[i]; var p = song.FindPattern(section.PatternId)!;
            var left = X(starts[i]); var right = X(starts[i + 1]); var width = right - left;
            var fill = i == selected ? Ui.ThemeBrush("#2C544F") : Ui.ThemeBrush(i % 2 == 0 ? "#233347" : "#1C2A3C");
            ctx.DrawRectangle(fill, new Pen(i == selected ? Ui.Accent : Ui.Line, 1), new Rect(left + 1, 23, Math.Max(1, width - 3), 34), 4, 4);
            using (ctx.PushClip(new Rect(left + 3, 24, Math.Max(1, width - 6), 33)))
            {
                ctx.DrawText(Ui.Fmt($"{i + 1:00}  {p.Name}", 9, i == selected ? Ui.Accent : Ui.Text), new Point(left + 6, 27));
                ctx.DrawText(Ui.Fmt($"{p.Length * section.Repeats} rows" + (section.Transpose == 0 ? "" : $"  {section.Transpose:+0;-0}"), 8, Ui.Muted, true), new Point(left + 6, 42));
            }
        }
        var overviewNotes = new Dictionary<(string Pattern, string Track), List<(int Row, int Pitch)>>();
        var rowPixels = PlotWidth / Math.Max(1, Total);
        for (var t = 0; t < song.Tracks.Count; t++)
        {
            var track = song.Tracks[t]; var color = Ui.ThemeBrush(track.Color); var y = Header + t * TrackHeight;
            if (t == selectedTrack) ctx.FillRectangle(Ui.ThemeBrush("#1B2B3C"), new Rect(0, y, Bounds.Width, TrackHeight));
            using (ctx.PushClip(new Rect(0, y, LabelWidth - 8, TrackHeight))) ctx.DrawText(Ui.Fmt(track.Name, 10, track.Muted ? Ui.Muted : color), new Point(3, y + 8));
            for (var i = 0; i < song.Arrangement.Count; i++)
            {
                var section = song.Arrangement[i]; var p = song.FindPattern(section.PatternId)!; var notes = p.Tracks.FirstOrDefault(x => x.TrackId == track.Id)?.Rows;
                ctx.DrawRectangle(null, new Pen(Ui.Line, .7), new Rect(X(starts[i]), y, Math.Max(1, X(starts[i + 1]) - X(starts[i])), TrackHeight));
                if (notes is null) continue;
                // Rasterize at most one marker per pixel and reuse repeated patterns.
                var key = (p.Id, track.Id);
                if (!overviewNotes.TryGetValue(key, out var markers))
                {
                    markers = []; var lastPixel = -1;
                    for (var r = 0; r < notes.Count; r++)
                    {
                        if (notes[r].Kind != NoteKind.Note || (int)(r * rowPixels) == lastPixel) continue;
                        lastPixel = (int)(r * rowPixels); markers.Add((r, notes[r].Pitch));
                    }
                    overviewNotes[key] = markers;
                }
                var repeatStride = Math.Max(1, (int)Math.Ceiling(1 / Math.Max(.000001, p.Length * rowPixels)));
                for (var repeat = 0; repeat < section.Repeats; repeat += repeatStride)
                for (var r = 0; r < notes.Count; r++) if (!string.IsNullOrEmpty(notes[r].InstrumentId))
                {
                    var markerX = X(starts[i] + repeat * p.Length + r);
                    ctx.FillRectangle(Ui.Orange, new Rect(markerX, y + 2, 2, 8));
                }
                for (var repeat = 0; repeat < section.Repeats; repeat += repeatStride) foreach (var marker in markers)
                {
                    var x = X(starts[i] + repeat * p.Length + marker.Row);
                    var noteY = y + 5 + (127 - Math.Clamp(marker.Pitch + section.Transpose, 0, 127)) / 127.0 * 16;
                    ctx.FillRectangle(track.Muted ? Ui.Muted : color, new Rect(x, noteY, Math.Max(1, rowPixels - .5), 2.5));
                }
            }
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var position = e.GetPosition(this); if (position.X < LabelWidth) return;
        var absolute = Math.Clamp((position.X - LabelWidth) / PlotWidth * Total, 0, Total - .001); int section = 0;
        while (section + 1 < song.Arrangement.Count && starts[section + 1] <= absolute) section++;
        var pattern = song.FindPattern(song.Arrangement[section].PatternId)!;
        var trackIndex = (int)((position.Y - Header) / TrackHeight);
        var text = $"Section {section + 1}: {pattern.Name} · click to select";
        if (position.Y >= Header && trackIndex >= 0 && trackIndex < song.Tracks.Count)
        {
            var track = song.Tracks[trackIndex]; var row = (int)(absolute - starts[section]) % pattern.Length;
            var notes = pattern.Tracks.FirstOrDefault(t => t.TrackId == track.Id)?.Rows; var instrument = track.InstrumentId;
            if (notes is not null) for (int r = 0; r <= row; r++) if (!string.IsNullOrEmpty(notes[r].InstrumentId)) instrument = notes[r].InstrumentId!;
            text += $" · {track.Name} · row {row + 1} · {song.FindInstrument(instrument)?.Name}. Orange marks are instrument section changes.";
        }
        ToolTip.SetTip(this, text);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var p = e.GetPosition(this); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || p.X < LabelWidth) return;
        var row = Math.Clamp((p.X - LabelWidth) / PlotWidth * Total, 0, Total - .001); var section = 0;
        while (section + 1 < song.Arrangement.Count && starts[section + 1] <= row) section++;
        var track = p.Y >= Header ? Math.Clamp((int)((p.Y - Header) / TrackHeight), 0, Math.Max(0, song.Tracks.Count - 1)) : -1;
        SectionSelected?.Invoke(section, song.Tracks.Count == 0 ? -1 : track); e.Handled = true;
    }
}

internal sealed class AutomationCanvas : Control
{
    private readonly Song song;
    private readonly List<AutomationPoint> points;
    private readonly IBrush color;
    private readonly int totalRows;
    private int dragging = -1;
    private AutomationPoint? draft;
    private IPointer? capturedPointer;
    private const double Left = 37, Right = 12, Top = 14, Bottom = 25;
    private double PlotWidth => Math.Max(1, Bounds.Width - Left - Right);
    private double PlotHeight => Math.Max(1, Bounds.Height - Top - Bottom);
    private double LastRow => Math.Max(1, totalRows - 1);
    private Point Position(AutomationPoint p) => new(Left + p.Row / LastRow * PlotWidth, Top + (12 - Math.Clamp(p.Decibels, -60, 12)) / 72 * PlotHeight);
    public event Action<List<AutomationPoint>>? Commit;
    public event Action<string>? Status;
    public AutomationCanvas(Song song, MusicMachine.Core.Track track, int totalRows)
    {
        this.song = song; this.totalRows = totalRows; points = track.VolumeAutomation.Select(p => new AutomationPoint { Row = p.Row, Decibels = p.Decibels }).ToList(); color = Ui.ThemeBrush(track.Color);
        Height = 173; MinWidth = 350; ClipToBounds = true; Focusable = true; Cursor = new(StandardCursorType.Cross);
        Avalonia.Automation.AutomationProperties.SetName(this, $"{track.Name} volume automation. Click to add, drag to move, right click to remove. Range minus 60 to plus 12 decibels.");
    }
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx); ctx.FillRectangle(Ui.Background, new Rect(Bounds.Size));
        foreach (var db in new[] { 12, 0, -12, -36, -60 })
        {
            var y = Position(new() { Decibels = db }).Y;
            ctx.DrawLine(new Pen(db == 0 ? Ui.ThemeBrush("#466170") : Ui.Line, db == 0 ? 1 : .5), new Point(Left, y), new Point(Bounds.Width - Right, y));
            ctx.DrawText(Ui.Fmt(db > 0 ? $"+{db}" : db.ToString(), 9, Ui.Muted, true), new Point(3, y - 6));
        }
        var barRows = song.RowsPerBeat * song.BeatsPerBar;
        var barStride = Math.Max(1, (int)Math.Ceiling(totalRows / (double)barRows / Math.Max(1, PlotWidth / 65)));
        for (var row = 0; row < totalRows; row += barRows * barStride)
        {
            var x = Position(new() { Row = row }).X; ctx.DrawLine(new Pen(Ui.Line, .5), new Point(x, Top), new Point(x, Top + PlotHeight));
            ctx.DrawText(Ui.Fmt(row.ToString(), 9, Ui.Muted, true), new Point(x, Top + PlotHeight + 6));
        }
        ctx.DrawText(Ui.Fmt($"{totalRows - 1}", 9, Ui.Muted, true), new Point(Bounds.Width - 32, Top + PlotHeight + 6));
        var visible = points.Where((p, i) => i != dragging).Select(p => new AutomationPoint { Row = p.Row, Decibels = p.Decibels }).ToList(); if (draft is not null) visible.Add(draft); visible = visible.OrderBy(p => p.Row).ToList();
        using (ctx.PushClip(new Rect(Left - 5, Top - 6, PlotWidth + 10, PlotHeight + 12)))
        {
            var zero = Position(new() { Decibels = 0 });
            if (visible.Count == 0) ctx.DrawLine(new Pen(color, 1.5), new Point(Left, zero.Y), new Point(Left + PlotWidth, zero.Y));
            else
            {
                var previous = new Point(Left, Position(visible[0]).Y);
                foreach (var point in visible) { var next = Position(point); ctx.DrawLine(new Pen(color, 2), previous, next); previous = next; }
                if (previous.X < Left + PlotWidth) ctx.DrawLine(new Pen(color, 2), previous, new Point(Left + PlotWidth, previous.Y));
                foreach (var point in visible) { var pos = Position(point); ctx.DrawEllipse(Ui.Background, new Pen(color, point == draft ? 2.5 : 1.7), pos, 4, 4); }
            }
        }
        if (draft is not null) ctx.DrawText(Ui.Fmt($"Row {draft.Row:0}  ·  {draft.Decibels:+0.0;-0.0;0.0} dB", 10, color), new Point(Math.Clamp(Position(draft).X + 8, Left, Math.Max(Left, Bounds.Width - 160)), Math.Clamp(Position(draft).Y - 22, 0, Bounds.Height - 16)));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var pos = e.GetPosition(this); if (pos.X < Left - 6 || pos.X > Bounds.Width - Right + 6 || pos.Y < Top - 6 || pos.Y > Top + PlotHeight + 6) return;
        var closest = -1; var distance = 100.0;
        for (var i = 0; i < points.Count; i++) { var p = Position(points[i]); var d = (p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y); if (d < distance) { distance = d; closest = i; } }
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (closest >= 0) { var result = points.Where((_, i) => i != closest).ToList(); Commit?.Invoke(result); } e.Handled = true; return;
        }
        if (!properties.IsLeftButtonPressed) return;
        dragging = closest; draft = closest >= 0 ? new() { Row = Math.Clamp(points[closest].Row, 0, totalRows - 1), Decibels = Math.Clamp(points[closest].Decibels, -60, 12) } : FromPosition(pos);
        Focus(); capturedPointer = e.Pointer; e.Pointer.Capture(this); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (draft is null) return; draft = FromPosition(e.GetPosition(this)); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); if (draft is null) return;
        var point = draft; var index = dragging; draft = null; dragging = -1; capturedPointer = null; e.Pointer.Capture(null);
        var result = points.Where((p, i) => i != index && Math.Abs(p.Row - point.Row) > .0001).Select(p => new AutomationPoint { Row = p.Row, Decibels = p.Decibels }).ToList(); result.Add(point); result.Sort((a, b) => a.Row.CompareTo(b.Row));
        Commit?.Invoke(result); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); capturedPointer = null; draft = null; dragging = -1; InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape && draft is not null) { draft = null; dragging = -1; capturedPointer?.Capture(null); capturedPointer = null; InvalidateVisual(); Status?.Invoke("Automation edit cancelled"); e.Handled = true; } }
    private AutomationPoint FromPosition(Point point) => new() { Row = Math.Clamp(Math.Round((point.X - Left) / PlotWidth * LastRow), 0, totalRows - 1), Decibels = Math.Round(Math.Clamp(12 - (point.Y - Top) / PlotHeight * 72, -60, 12), 1) };
}

internal sealed class DrumGrid : Control
{
    public const double HeaderHeight = 30, LaneHeight = 70;
    private double StepWidth => Math.Max(12, Bounds.Width / Math.Max(1, pattern.Length));
    public Rect CellRect(int lane, int step) => new(step * StepWidth, HeaderHeight + lane * LaneHeight, StepWidth, LaneHeight);
    private readonly Song song;
    private readonly Pattern pattern;
    private readonly int selectedLane, selectedStep;
    public event Action<int, int, byte>? ChangeHit;
    public event Action<int, int>? SelectHit;
    public DrumGrid(Song song, Pattern pattern, int selectedLane, int selectedStep)
    {
        this.song = song; this.pattern = pattern; this.selectedLane = selectedLane; this.selectedStep = selectedStep;
        MinWidth = Math.Max(240, pattern.Length * 12); Width = double.NaN; Height = HeaderHeight + pattern.Drums.Count * LaneHeight; Focusable = true; ClipToBounds = true; Cursor = new(StandardCursorType.Hand);
        Avalonia.Automation.AutomationProperties.SetName(this, "Drum step editor. Click toggles hits, right click accents, shift click selects, arrows navigate.");
    }
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx); ctx.FillRectangle(Ui.Background, new Rect(Bounds.Size));
        var beatRows = Math.Max(1, song.RowsPerBeat * 4 / song.BeatUnit); var barRows = beatRows * song.BeatsPerBar;
        for (var step = 0; step < pattern.Length; step++)
        {
            var x = step * StepWidth; var isBeat = step % beatRows == 0; var isBar = step % barRows == 0;
            if (isBeat) ctx.DrawText(Ui.Fmt($"{step / barRows + 1}.{step / beatRows % song.BeatsPerBar + 1}", 9, isBar ? Ui.Text : Ui.Muted, true), new Point(x + 2, 8));
            for (var lane = 0; lane < pattern.Drums.Count; lane++)
            {
                var y = HeaderHeight + lane * LaneHeight; var drum = pattern.Drums[lane]; var velocity = drum.Steps[step];
                if ((step / beatRows) % 2 == 0) ctx.FillRectangle(Ui.ThemeBrush("#141F2C"), new Rect(x, y, StepWidth, LaneHeight));
                if (isBeat) ctx.DrawLine(new Pen(isBar ? Ui.ThemeBrush("#4A6177") : Ui.Line, isBar ? 1.5 : .7), new Point(x, y + 3), new Point(x, y + LaneHeight - 3));
                var rect = new Rect(x + 3, y + 12, StepWidth - 6, 34); var on = velocity > 0;
                var fill = drum.Muted ? Ui.ThemeBrush(on ? "#566270" : "#26313E") : !on ? Ui.ThemeBrush("#263648") : velocity >= 220 ? Ui.Orange : Ui.Accent;
                ctx.DrawRectangle(fill, null, rect, 3, 3);
                if (on)
                {
                    var bar = 18 * velocity / 255.0; ctx.FillRectangle(drum.Muted ? Ui.Muted : Ui.ThemeBrush("#557C79"), new Rect(x + 5, y + 55 - bar / 2, StepWidth - 10, Math.Max(1, bar / 2)));
                    if (velocity >= 220) ctx.DrawText(Ui.Fmt("•", 11, Ui.ThemeBrush("#8C542D")), new Point(x + 6, y + 20));
                }
                if (lane == selectedLane && step == selectedStep) ctx.DrawRectangle(null, new Pen(Ui.Text, 1.5), new Rect(x + 1, y + 9, StepWidth - 2, 40), 4, 4);
                ctx.DrawLine(new Pen(Ui.Line, .5), new Point(x, y + LaneHeight - 1), new Point(x + StepWidth, y + LaneHeight - 1));
            }
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var p = e.GetPosition(this); if (p.Y < HeaderHeight) return;
        var step = (int)(p.X / StepWidth); var lane = (int)((p.Y - HeaderHeight) / LaneHeight); if (step < 0 || step >= pattern.Length || lane < 0 || lane >= pattern.Drums.Count) return;
        Focus(); var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed) ChangeHit?.Invoke(lane, step, 255);
        else if (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) SelectHit?.Invoke(lane, step);
        else if (properties.IsLeftButtonPressed) ChangeHit?.Invoke(lane, step, pattern.Drums[lane].Steps[step] == 0 ? (byte)190 : (byte)0);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var p = e.GetPosition(this); var step = (int)(p.X / StepWidth); var lane = (int)((p.Y - HeaderHeight) / LaneHeight);
        if (p.Y >= HeaderHeight && step >= 0 && step < pattern.Length && lane >= 0 && lane < pattern.Drums.Count) ToolTip.SetTip(this, $"{pattern.Drums[lane].Name} · step {step + 1} · velocity {pattern.Drums[lane].Steps[step] / 255.0:P0}\nLeft: toggle · Right: accent · Shift-click: select");
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); var lane = selectedLane; var step = selectedStep;
        switch (e.Key)
        {
            case Key.Left: step = Math.Max(0, step - 1); break;
            case Key.Right: step = Math.Min(pattern.Length - 1, step + 1); break;
            case Key.Up: lane = Math.Max(0, lane - 1); break;
            case Key.Down: lane = Math.Min(pattern.Drums.Count - 1, lane + 1); break;
            case Key.Enter: ChangeHit?.Invoke(lane, step, pattern.Drums[lane].Steps[step] == 0 ? (byte)190 : (byte)0); e.Handled = true; return;
            case Key.Delete: ChangeHit?.Invoke(lane, step, 0); e.Handled = true; return;
            default: return;
        }
        SelectHit?.Invoke(lane, step); e.Handled = true;
    }
}
