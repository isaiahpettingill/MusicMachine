using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MusicMachine.Core;
using IconPacks.Avalonia.Material;

namespace MusicMachine.App;

public sealed partial class MainView
{
    // These are view preferences only. Song.Arrangement remains the one sequence used
    // by editing, undo, save/recovery, playback and export.
    private string? arrangementAddPattern;
    private bool arrangementOverviewExpanded, arrangementMixerExpanded, arrangementAutomationExpanded, arrangementPlaybackExpanded;
    private Vector arrangementOrderOffset;
    private ListBox? arrangementOrderList;

    private void OpenSongArrangement()
    {
        if (!tracker.CommitPending()) return;
        arrangementAddPattern = activePattern;
        SelectWorkspace("Arrangement");
    }

    private void AddSelectedPatternToSong()
    {
        if (!tracker.CommitPending()) return;
        arrangementAddPattern = activePattern;
        if (InsertSongPattern(activePattern, editor.Song.Arrangement.Count)) SelectWorkspace("Arrangement");
    }

    private Control BuildArrangement()
    {
        var song = editor.Song;
        selectedSection = Math.Clamp(selectedSection, 0, song.Arrangement.Count - 1);
        if (song.FindPattern(arrangementAddPattern ?? "") is null) arrangementAddPattern = activePattern;
        var totalRows = song.Arrangement.Sum(s => song.FindPattern(s.PatternId)!.Length * s.Repeats);
        var body = new StackPanel { Spacing = 10, Margin = new(12, 4, 12, 14) };
        body.Children.Add(Ui.Label("Song arrangement", 15));
        body.Children.Add(ArrangementText($"{song.Arrangement.Count} sections · {ArrangementLength(song, totalRows)} · played from top to bottom", 11));
        var play = Ui.Button("Play whole song", PlayWholeSong, "Play every section once, from the beginning, regardless of playback loop markers");
        play.Name = "ArrangementPlaySong";
        var stop = Ui.Button("Stop", Stop, "Stop song or pattern playback"); stop.Name = "ArrangementStop";
        var export = Ui.Button("Export audio…", () => _ = ExportAudio(), "Export every section in this song order, including repeats"); export.Name = "ArrangementExport";
        body.Children.Add(ArrangementWrap(play, stop, export));

        var addBox = new StackPanel { Spacing = 6 };
        addBox.Children.Add(Ui.Label("ADD A PATTERN TO THE SONG", 10, Ui.Muted));
        var patterns = song.Patterns.ToArray();
        var picker = new ComboBox
        {
            Name = "ArrangementPatternPicker", ItemsSource = patterns.Select(p => p.Name).ToArray(),
            SelectedIndex = Array.FindIndex(patterns, p => p.Id == arrangementAddPattern),
            HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, MinWidth = 0
        };
        AutomationProperties.SetName(picker, "Pattern to add to song");
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0 && picker.SelectedIndex < patterns.Length) arrangementAddPattern = patterns[picker.SelectedIndex].Id; };
        addBox.Children.Add(picker);
        var append = Ui.Button("Append to song", () => InsertSongPattern(arrangementAddPattern!, editor.Song.Arrangement.Count), "Add this reusable pattern at the end of the song", true);
        append.Name = "ArrangementAppend"; append.IsEnabled = song.Arrangement.Count < SongLimits.MaxSections;
        var insert = Ui.Button("Insert after selected", () => InsertSongPattern(arrangementAddPattern!, selectedSection + 1), "Insert this pattern immediately after the selected song section");
        insert.Name = "ArrangementInsert"; insert.IsEnabled = append.IsEnabled;
        addBox.Children.Add(ArrangementWrap(append, insert));
        body.Children.Add(SequencerPanel(addBox));

        var orderBox = new StackPanel { Spacing = 6 };
        orderBox.Children.Add(Ui.Label("SONG ORDER", 10, Ui.Muted));
        var order = new ListBox
        {
            Name = "ArrangementOrder", ItemsSource = song.Arrangement,
            SelectedIndex = selectedSection, Height = Math.Clamp(song.Arrangement.Count * 35 + 6, 76, 216),
            Background = Ui.Background,
            ItemTemplate = new FuncDataTemplate<SongSection>((section, _) =>
            {
                if (section is null) return new TextBlock();
                var index = song.Arrangement.IndexOf(section); var pattern = song.FindPattern(section.PatternId)!;
                var row = new Grid { ColumnDefinitions = new("30,*,48,66"), MinHeight = 25 };
                row.Children.Add(Ui.Label($"{index + 1:00}", 11, Ui.Muted));
                var name = Ui.Label(pattern.Name, 12); name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1); row.Children.Add(name);
                var repeats = Ui.Label($"×{section.Repeats}", 11, Ui.Muted); repeats.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(repeats, 2); row.Children.Add(repeats);
                var bars = Ui.Label($"{ArrangementBars(song, pattern.Length * section.Repeats):0.#} bars", 10, Ui.Muted); bars.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(bars, 3); row.Children.Add(bars);
                ToolTip.SetTip(row, $"Section {index + 1}: {pattern.Name}, repeated {section.Repeats} times. {ArrangementLength(song, pattern.Length * section.Repeats)}");
                return row;
            })
        };
        arrangementOrderList = order;
        order.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters =
        {
            new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(Control.HeightProperty, 35d), new Setter(Control.MinHeightProperty, 35d),
            new Setter(ContentControl.PaddingProperty, new Thickness(6, 3))
        } });
        ScrollViewer.SetHorizontalScrollBarVisibility(order, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(order, "Song order. Select a section, then edit its pattern, repeats or position.");
        order.SelectionChanged += (_, _) => { if (order.SelectedIndex >= 0 && order.SelectedIndex != selectedSection) SelectSongSection(order.SelectedIndex); };
        order.DoubleTapped += (_, _) => OpenSelectedSongPattern();
        order.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelectedSongPattern(); e.Handled = true; } };
        order.Loaded += (_, _) =>
        {
            if (order.Scroll is { } scroll) scroll.Offset = arrangementOrderOffset;
            order.ScrollIntoView(selectedSection);
        };
        order.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => { if (order.Scroll is { } scroll) arrangementOrderOffset = scroll.Offset; });
        orderBox.Children.Add(order);

        var sectionIndex = selectedSection; var selected = song.Arrangement[sectionIndex];
        var repeat = Number(selected.Repeats, 1, 128, 1, 74, v => Change(s => s.Arrangement[sectionIndex].Repeats = (int)v), "0");
        repeat.Name = "ArrangementRepeats"; AutomationProperties.SetName(repeat, "Selected section repeat count");
        var edit = Ui.Button("Edit pattern", OpenSelectedSongPattern, "Open the selected section's reusable pattern in the tracker"); edit.Name = "ArrangementEditPattern";
        var up = Ui.IconButton(PackIconMaterialKind.ArrowUp, () => MoveSection(-1), "Move selected section earlier in the song"); up.Name = "ArrangementMoveUp"; up.IsEnabled = selectedSection > 0;
        var down = Ui.IconButton(PackIconMaterialKind.ArrowDown, () => MoveSection(1), "Move selected section later in the song"); down.Name = "ArrangementMoveDown"; down.IsEnabled = selectedSection < song.Arrangement.Count - 1;
        var remove = Ui.Button("Remove", RemoveSection, "Remove this song section; keep its reusable pattern in the library"); remove.Name = "ArrangementRemove"; remove.IsEnabled = song.Arrangement.Count > 1;
        orderBox.Children.Add(ArrangementWrap(Field("REPEATS", repeat), edit, Ui.Row(up, down), remove));
        orderBox.Children.Add(ArrangementText("Patterns are reusable. Editing a pattern updates every section that uses it.", 10));
        body.Children.Add(SequencerPanel(orderBox));

        body.Children.Add(ArrangementSection("Playback loop & transpose", "ArrangementPlaybackSettings", BuildArrangementPlaybackSettings(), arrangementPlaybackExpanded, value => arrangementPlaybackExpanded = value));
        var overview = new ArrangementCanvas(song, selectedSection, chosenTrack);
        overview.SectionSelected += SelectSongSection;
        body.Children.Add(ArrangementSection("Song overview", "ArrangementOverview", overview, arrangementOverviewExpanded, value => arrangementOverviewExpanded = value));
        body.Children.Add(ArrangementSection("Track mixer", "ArrangementMixer", BuildArrangementMixer(), arrangementMixerExpanded, value => arrangementMixerExpanded = value));
        if (song.Tracks.Count > 0)
            body.Children.Add(ArrangementSection("Volume automation", "ArrangementAutomation", BuildArrangementAutomation(totalRows), arrangementAutomationExpanded, value => arrangementAutomationExpanded = value));
        return new ScrollViewer { Name = "ArrangementWorkspace", Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private bool InsertSongPattern(string patternId, int at)
    {
        if (refreshing || !tracker.CommitPending()) return false;
        var song = editor.Song;
        if (song.FindPattern(patternId) is null) { SetStatus("Choose a pattern to add to the song"); return false; }
        if (song.Arrangement.Count >= SongLimits.MaxSections) { SetStatus($"The song already has the maximum {SongLimits.MaxSections} sections"); return false; }
        at = Math.Clamp(at, 0, song.Arrangement.Count);
        return ChangeArrangement(s =>
        {
            var count = s.Arrangement.Count;
            s.Arrangement.Insert(at, new() { PatternId = patternId });
            // Keep existing loop content in range when inserting before it, and
            // extend a loop ending at the song end when appending a section.
            if (at < s.LoopStartSection) s.LoopStartSection++;
            if (at < s.LoopEndSection || at == count && s.LoopEndSection == count) s.LoopEndSection++;
        }, at, $"Added {song.FindPattern(patternId)!.Name} as section {at + 1:00} · song order is ready to play or export");
    }

    private bool ChangeArrangement(Action<Song> change, int nextSelection, string message)
    {
        if (refreshing || !tracker.CommitPending()) return false;
        var previousSelection = selectedSection; var revision = editor.Revision;
        selectedSection = nextSelection;
        Change(change);
        if (editor.Revision == revision)
        {
            selectedSection = previousSelection;
            Refresh();
            return false;
        }
        SetStatus(message); return true;
    }

    private void SelectSongSection(int index, int track = -1)
    {
        if (!tracker.CommitPending() || index < 0 || index >= editor.Song.Arrangement.Count) return;
        var keepOrderFocus = arrangementOrderList?.IsKeyboardFocusWithin == true;
        selectedSection = index;
        if (track >= 0 && track < editor.Song.Tracks.Count) { chosenTrack = track; selectedInstrument = editor.Song.Tracks[track].InstrumentId; }
        Refresh();
        if (keepOrderFocus) Dispatcher.UIThread.Post(() => { if (mode == "Arrangement") arrangementOrderList?.Focus(); }, DispatcherPriority.Background);
    }

    private void OpenSelectedSongPattern()
    {
        if (!tracker.CommitPending()) return;
        selectedSection = Math.Clamp(selectedSection, 0, editor.Song.Arrangement.Count - 1);
        activePattern = editor.Song.Arrangement[selectedSection].PatternId;
        SelectWorkspace("Tracker");
        SetStatus($"Editing {editor.Song.FindPattern(activePattern)!.Name} · changes update every occurrence in the song");
    }

    private void MoveSection(int delta)
    {
        var from = selectedSection; var to = from + delta;
        if (to < 0 || to >= editor.Song.Arrangement.Count) return;
        ChangeArrangement(s => (s.Arrangement[from], s.Arrangement[to]) = (s.Arrangement[to], s.Arrangement[from]), to, $"Moved section {from + 1:00} to {to + 1:00}");
    }

    private void RemoveSection()
    {
        if (editor.Song.Arrangement.Count <= 1) { SetStatus("Keep at least one section in the song; its pattern can be edited or cleared"); return; }
        var index = selectedSection;
        ChangeArrangement(s =>
        {
            s.Arrangement.RemoveAt(index);
            if (index < s.LoopStartSection) s.LoopStartSection--;
            if (index < s.LoopEndSection) s.LoopEndSection--;
            s.LoopStartSection = Math.Clamp(s.LoopStartSection, 0, s.Arrangement.Count - 1);
            s.LoopEndSection = Math.Clamp(s.LoopEndSection, s.LoopStartSection + 1, s.Arrangement.Count);
        }, Math.Min(index, editor.Song.Arrangement.Count - 2), "Section removed · its reusable pattern is still in the library");
    }

    private void PlayWholeSong()
    {
        if (!tracker.CommitPending()) return;
        try
        {
            Stop();
            player.Play(SongFile.Clone(editor.Song), loop: false);
            SetPlayingVisual(true);
            SetStatus("Playing the whole song once, from the beginning · all sections and repeats");
        }
        catch (Exception e) { SetStatus("Song playback: " + e.Message + " · offline export remains available"); }
    }

    private Control BuildArrangementPlaybackSettings()
    {
        var song = editor.Song; var index = selectedSection; var section = song.Arrangement[index];
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(ArrangementText("Loop markers apply to the main transport's Loop option. Play whole song and Export audio always use every section.", 10));
        var labels = song.Arrangement.Select((s, i) => $"{i + 1:00} · {song.FindPattern(s.PatternId)!.Name}").ToArray();
        var start = new ComboBox { ItemsSource = labels, SelectedIndex = song.LoopStartSection, Width = 154, FontSize = 11 };
        var end = new ComboBox { ItemsSource = labels, SelectedIndex = song.LoopEndSection - 1, Width = 154, FontSize = 11 };
        start.SelectionChanged += (_, _) => { if (start.SelectedIndex >= 0 && start.SelectedIndex != song.LoopStartSection) Change(s => { s.LoopStartSection = start.SelectedIndex; s.LoopEndSection = Math.Max(s.LoopEndSection, s.LoopStartSection + 1); }); };
        end.SelectionChanged += (_, _) => { if (end.SelectedIndex >= 0 && end.SelectedIndex + 1 != song.LoopEndSection) Change(s => { s.LoopEndSection = end.SelectedIndex + 1; s.LoopStartSection = Math.Min(s.LoopStartSection, s.LoopEndSection - 1); }); };
        AutomationProperties.SetName(start, "First playback loop section"); AutomationProperties.SetName(end, "Last playback loop section");
        var transpose = Number(section.Transpose, -48, 48, 1, 74, v => Change(s => s.Arrangement[index].Transpose = (int)v), "0");
        AutomationProperties.SetName(transpose, "Selected section transpose in semitones");
        panel.Children.Add(ArrangementWrap(Field("LOOP START", start), Field("LOOP THROUGH", end), Field("TRANSPOSE", transpose)));
        return panel;
    }

    private Control BuildArrangementMixer()
    {
        var song = editor.Song; var mixer = new StackPanel { Spacing = 9 };
        mixer.Children.Add(ArrangementWrap(Ui.Label("Mix trim", 10, Ui.Muted),
            Ui.IconButton(PackIconMaterialKind.Minus, () => TrimMix(-1), "Lower all track and drum-lane gains equally; retain the mix balance"),
            Ui.IconButton(PackIconMaterialKind.Plus, () => TrimMix(1), "Raise all track and drum-lane gains equally; retain the mix balance")));
        for (var i = 0; i < song.Tracks.Count; i++)
        {
            var index = i; var track = song.Tracks[i]; var id = track.Id;
            var trackBox = new StackPanel { Spacing = 5 };
            var heading = new Grid { ColumnDefinitions = new("32,*") };
            var choose = Ui.Button($"{i + 1:00}", () => { chosenTrack = index; selectedInstrument = track.InstrumentId; Refresh(); }, $"Select {track.Name} for automation");
            choose.Padding = new(2); choose.Foreground = Ui.ThemeBrush(track.Color); if (i == chosenTrack) choose.Classes.Add("selected"); heading.Children.Add(choose);
            var name = new TextBox { Text = track.Name, FontSize = 11, Margin = new(6, 0, 0, 0), Padding = new(6, 5), MaxLength = 128 };
            name.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && name.Text != track.Name) Change(s => s.Tracks.First(t => t.Id == id).Name = name.Text.Trim()); };
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { choose.Focus(); e.Handled = true; } };
            Grid.SetColumn(name, 1); heading.Children.Add(name); trackBox.Children.Add(heading);
            var sounds = new ComboBox { ItemsSource = song.Instruments.Select(x => x.Name).ToArray(), SelectedIndex = song.Instruments.FindIndex(x => x.Id == track.InstrumentId), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
            sounds.SelectionChanged += (_, _) => { if (sounds.SelectedIndex >= 0 && song.Instruments[sounds.SelectedIndex].Id != track.InstrumentId) { var soundId = song.Instruments[sounds.SelectedIndex].Id; selectedInstrument = soundId; chosenTrack = index; Change(s => s.Tracks.First(t => t.Id == id).InstrumentId = soundId); } };
            ToolTip.SetTip(sounds, "Default instrument for " + track.Name); trackBox.Children.Add(sounds);
            var gain = Number(track.VolumeDb, -96, 12, 1, 73, v => Change(s => s.Tracks.First(t => t.Id == id).VolumeDb = v));
            var pan = Number(track.Pan, -1, 1, .1, 73, v => Change(s => s.Tracks.First(t => t.Id == id).Pan = v), "0.0"); ToolTip.SetTip(pan, "Pan: −1 left · 0 center · +1 right");
            var mute = Ui.IconButton(track.Muted ? PackIconMaterialKind.VolumeOff : PackIconMaterialKind.VolumeHigh, () => Change(s => s.Tracks.First(t => t.Id == id).Muted = !track.Muted), $"{(track.Muted ? "Unmute" : "Mute")} {track.Name}"); if (track.Muted) mute.Classes.Add("selected");
            var solo = Ui.IconButton(PackIconMaterialKind.Headphones, () => Change(s => s.Tracks.First(t => t.Id == id).Solo = !track.Solo), $"{(track.Solo ? "Unsolo" : "Solo")} {track.Name}"); if (track.Solo) solo.Classes.Add("selected");
            trackBox.Children.Add(ArrangementWrap(Field("GAIN dB", gain), Field("PAN", pan), mute, solo));
            mixer.Children.Add(SequencerPanel(trackBox));
        }
        if (song.Tracks.Count == 0) mixer.Children.Add(Ui.Button("Add melodic track", AddTrack));
        return mixer;
    }

    private Control BuildArrangementAutomation(int totalRows)
    {
        var song = editor.Song; var track = song.Tracks[chosenTrack]; var id = track.Id;
        var panel = new StackPanel { Spacing = 6 };
        var picker = new ComboBox { ItemsSource = song.Tracks.Select(t => t.Name).ToArray(), SelectedIndex = chosenTrack, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(picker, "Track volume automation");
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0 && picker.SelectedIndex != chosenTrack) { chosenTrack = picker.SelectedIndex; Refresh(); } };
        panel.Children.Add(picker);
        var reset = Ui.Button("Clear automation", () => Change(s => s.Tracks.First(t => t.Id == id).VolumeAutomation.Clear()), "Clear this track's song-wide volume automation"); reset.IsEnabled = track.VolumeAutomation.Count > 0;
        panel.Children.Add(reset);
        panel.Children.Add(ArrangementText("Click to add · drag to shape · right-click to remove · absolute song rows", 10));
        var canvas = new AutomationCanvas(song, track, totalRows);
        canvas.Commit += points => { Change(s => s.Tracks.First(t => t.Id == id).VolumeAutomation = points); SetStatus($"{track.Name} volume automation updated"); };
        canvas.Status += SetStatus; panel.Children.Add(canvas);
        return panel;
    }

    private static double ArrangementBars(Song song, int rows) => rows / (song.RowsPerBeat * song.BeatsPerBar * 4.0 / song.BeatUnit);
    private static string ArrangementLength(Song song, int rows) => $"{ArrangementBars(song, rows):0.#} bars · {rows * 60.0 / song.RowsPerBeat / song.Bpm:0.0} seconds";
    private static TextBlock ArrangementText(string text, double size) => new() { Text = text, FontSize = size, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private static WrapPanel ArrangementWrap(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) { control.Margin = new(0, 0, 6, 4); panel.Children.Add(control); }
        return panel;
    }
    private static Expander ArrangementSection(string title, string name, Control content, bool expanded, Action<bool> changed)
    {
        content.Margin = new(0, 7, 0, 3);
        var section = new Expander { Name = name, Header = title, Content = content, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        section.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) changed(section.IsExpanded); };
        return section;
    }
}
