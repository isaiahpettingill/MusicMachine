using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MusicMachine.Core;
using IconPacks.Avalonia.Material;

namespace MusicMachine.App;

/// <summary>Compact, reflection-free instrument editor. Every gesture is an undoable model edit.</summary>
public sealed class InstrumentPanel : UserControl
{
    private readonly Action<Action<Song>> _change;
    private readonly TextBox _name;
    private readonly ComboBox _waveform, _drum, _frames;
    private readonly TextBlock _scope, _waveHint, _frameCount;
    private readonly WaveformDisplay _wave;
    private readonly EnvelopeDisplay _envelope;
    private readonly StackPanel _body, _customTools, _tableTools;
    private readonly Grid _sectionLayout, _toneLayout, _filterFields, _pitchFields;
    private readonly StackPanel _soundColumn, _panel;
    private readonly Expander _oscillatorSection, _amplitudeSection, _filterSection, _pitchSection, _tuningSection;
    private bool _wideLayout, _twoColumns, _layoutApplied;
    private readonly Control _oscillatorFields;
    private readonly Button _addFrame, _removeFrame, _duplicateFrame;
    private readonly List<(NumericUpDown Control, Func<Instrument, double> Read)> _numbers = [];
    private readonly List<(Slider Control, TextBlock Readout, Func<Instrument, double> Read, Func<double, string> Format)> _sliders = [];
    private Instrument? _instrument;
    private string? _instrumentId;
    private bool _refreshing;
    private int _editingFrame;

    public InstrumentPanel(Action<Action<Song>> change, Action preview,
        Action exportInstrument, Action importInstrument)
    {
        _change = change;
        Name = "InstrumentPanel";
        MinWidth = 238;
        Background = Ui.Background;
        AutomationProperties.SetName(this, "Sound design instrument editor");

        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = "Sound design", FontSize = 12,
            Foreground = Ui.Muted, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _scope = new TextBlock { Text = "GLOBAL", FontSize = 9, LetterSpacing = .6, Foreground = Ui.Accent };
        var scopePill = new Border { Child = _scope, CornerRadius = new(3), Padding = new(6, 3),
            Background = Ui.Surface };
        ToolTip.SetTip(scopePill, "Global instruments are shared. Make a local copy from the instrument library for independent edits.");
        Grid.SetColumn(scopePill, 1); header.Children.Add(scopePill);

        _name = new TextBox { Name = "InstrumentName", PlaceholderText = "Instrument name", FontSize = 14,
            FontWeight = FontWeight.SemiBold, MaxLength = 128, MinHeight = 32, Margin = new(0) };
        Accessible(_name, "Instrument name", "Name this sound. Press Enter or leave the field to apply.");
        _name.LostFocus += (_, _) => CommitName();
        _name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitName(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _name.Text = _instrument?.Name; e.Handled = true; }
        };

        var audition = SmallIconButton(PackIconMaterialKind.Play, preview, "Audition the selected instrument", true);
        audition.Name = "AuditionInstrument";
        var nameAndActions = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        nameAndActions.Children.Add(_name); Grid.SetColumn(audition, 1); nameAndActions.Children.Add(audition);

        _drum = Choice("VoiceKindSelector", "Voice engine", ["Synthesizer", "Kick", "Snare", "Closed hi-hat", "Open hi-hat", "Tom", "Clap"]);
        _drum.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _drum.SelectedIndex >= 0)
            {
                var kind = (DrumKind)_drum.SelectedIndex;
                if (_instrument?.Drum != kind) Edit(i => i.Drum = kind);
            }
        };
        _waveform = Choice("WaveformSelector", "Oscillator waveform", ["Sine", "Triangle", "Saw", "Square", "Pulse", "Noise", "Custom", "Wavetable"]);
        _waveform.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _waveform.SelectedIndex < 0) return;
            var shape = (Waveform)_waveform.SelectedIndex;
            if (_instrument?.Waveform == shape) return;
            Edit(i =>
            {
                i.Waveform = shape;
                if (shape == Waveform.Custom && i.CustomWave.Length == 0) i.CustomWave = ShapeSamples(Waveform.Sine);
                if (shape == Waveform.Wavetable && i.Wavetable.Count == 0)
                    i.Wavetable = [ShapeSamples(Waveform.Sine), ShapeSamples(Waveform.Square)];
            });
        };
        _wave = new WaveformDisplay { Name = "WaveformEditor", Height = 84, Margin = new(0, 8, 0, 0) };
        Accessible(_wave, "Waveform preview and sample editor", "For custom waves and wavetable frames, drag to draw 128 signed PCM16 samples. Arrow keys select and adjust samples; Shift makes larger adjustments. Escape cancels a stroke.");
        _wave.DrawCompleted += ReplaceEditorSamples;
        _waveHint = Ui.Label("ONE CYCLE", 9, Ui.Muted);
        _waveHint.LetterSpacing = .8; _waveHint.Margin = new(0, 5, 0, 0);
        _customTools = new StackPanel { Spacing = 5, Margin = new(0, 8, 0, 0) };
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var presetShapes = new[] { Waveform.Sine, Waveform.Square, Waveform.Noise };
        for (var n = 0; n < presetShapes.Length; n++)
        {
            var shape = presetShapes[n];
            var icon = shape switch
            {
                Waveform.Sine => PackIconMaterialKind.SineWave,
                Waveform.Square => PackIconMaterialKind.SquareWave,
                _ => PackIconMaterialKind.ShuffleVariant
            };
            var button = SmallIconButton(icon,
                () => ReplaceEditorSamples(shape == Waveform.Noise ? RandomSamples() : ShapeSamples(shape)), $"Replace the edited waveform with a {shape.ToString().ToLowerInvariant()} shape");
            presets.Children.Add(button);
        }
        _customTools.Children.Add(presets);
        ToolTip.SetTip(presets, "Draw directly on the waveform, or replace it with a preset shape. Each stroke is one undo.");

        _tableTools = new StackPanel { Spacing = 6, Margin = new(0, 8, 0, 0) };
        var frameHeader = new Grid { ColumnDefinitions = new("*,Auto") };
        _frames = Choice("WavetableFrameSelector", "Wavetable frame to edit", []);
        _frames.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _frames.SelectedIndex < 0) return;
            _editingFrame = _frames.SelectedIndex; RefreshWave();
        };
        _frameCount = Ui.Label("0 FRAMES", 9, Ui.Muted);
        _frameCount.Margin = new(8, 0, 0, 0);
        frameHeader.Children.Add(_frames); Grid.SetColumn(_frameCount, 1); frameHeader.Children.Add(_frameCount);
        _tableTools.Children.Add(frameHeader);
        var frameButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        _addFrame = SmallIconButton(PackIconMaterialKind.Plus, () =>
        {
            if (_instrument is null || _instrument.Wavetable.Count >= SongLimits.MaxWaveFrames) return;
            _editingFrame = _instrument.Wavetable.Count;
            Edit(i => i.Wavetable.Add(ShapeSamples(Waveform.Sine)));
        }, "Add a sine-wave frame to the wavetable");
        _duplicateFrame = SmallIconButton(PackIconMaterialKind.ContentCopy, () =>
        {
            if (_instrument is null || _instrument.Wavetable.Count == 0 || _instrument.Wavetable.Count >= SongLimits.MaxWaveFrames) return;
            var source = EditorSamples(); _editingFrame = _instrument.Wavetable.Count;
            Edit(i => i.Wavetable.Add(source));
        }, "Duplicate the selected wavetable frame");
        _removeFrame = SmallIconButton(PackIconMaterialKind.DeleteOutline, () =>
        {
            if (_instrument is null || _instrument.Wavetable.Count <= 1) return;
            var index = _editingFrame; _editingFrame = Math.Max(0, index - 1);
            Edit(i => { if (index < i.Wavetable.Count) i.Wavetable.RemoveAt(index); });
        }, "Remove the selected wavetable frame; at least one frame is retained");
        frameButtons.Children.Add(_addFrame); frameButtons.Children.Add(_duplicateFrame); frameButtons.Children.Add(_removeFrame);
        _tableTools.Children.Add(frameButtons);
        _tableTools.Children.Add(SliderField("Morph position", "WavetablePosition", 0, 1, .01,
            i => i.WavetablePosition, (i, v) => i.WavetablePosition = v, v => $"{v:P0}"));

        var oscillator = new StackPanel { Spacing = 0 };
        oscillator.Children.Add(Ui.Label("Voice engine", 10, Ui.Muted));
        _drum.Margin = new(0, 4, 0, 8); oscillator.Children.Add(_drum);
        ToolTip.SetTip(_drum, "Choose an oscillator or a synthesized drum voice");
        oscillator.Children.Add(_waveform); oscillator.Children.Add(_wave); oscillator.Children.Add(_waveHint);
        oscillator.Children.Add(_tableTools); oscillator.Children.Add(_customTools);
        _oscillatorFields = NumberGrid(
            Number("Pulse width %", "PulseWidth", 1, 99, 1, i => i.PulseWidth * 100, (i, v) => i.PulseWidth = v / 100, "0"),
            Number("Detune · cents", "DetuneCents", -1200, 1200, 1, i => i.DetuneCents, (i, v) => i.DetuneCents = v, "0"),
            Number("Phase · degrees", "Phase", 0, 360, 1, i => i.Phase * 360, (i, v) => i.Phase = v / 360, "0.#"));
        _tuningSection = Section("Tuning", _oscillatorFields, false);
        _tuningSection.Name = "TuningSection";
        oscillator.Children.Add(_tuningSection);

        _envelope = new EnvelopeDisplay { Height = 55, Margin = new(0, 0, 0, 8) };
        Accessible(_envelope, "Amplitude envelope graph", "Attack, decay, sustain and release shape the volume of each note.");
        var envelopeSection = new StackPanel { Spacing = 0 };
        envelopeSection.Children.Add(_envelope);
        envelopeSection.Children.Add(NumberGrid(
            Number("Attack · ms", "AttackMs", 0, 60000, 1, i => i.Amplitude.AttackMs, (i, v) => i.Amplitude.AttackMs = v, "0.#"),
            Number("Decay · ms", "DecayMs", 0, 60000, 1, i => i.Amplitude.DecayMs, (i, v) => i.Amplitude.DecayMs = v, "0.#"),
            Number("Sustain · %", "Sustain", 0, 100, 1, i => i.Amplitude.Sustain * 100, (i, v) => i.Amplitude.Sustain = v / 100, "0.#"),
            Number("Release · ms", "ReleaseMs", 0, 60000, 1, i => i.Amplitude.ReleaseMs, (i, v) => i.Amplitude.ReleaseMs = v, "0.#")));

        _filterFields = NumberGrid(
            Number("Cutoff · Hz", "FilterCutoff", 20, 96000, 100, i => i.FilterCutoff, (i, v) => i.FilterCutoff = v, "0"),
            Number("Resonance · %", "FilterResonance", 0, 99, 1, i => i.FilterResonance * 100, (i, v) => i.FilterResonance = v / 100, "0.#"));
        _pitchFields = NumberGrid(
            Number("Amount · st", "PitchEnvelopeSemitones", -96, 96, 1, i => i.PitchEnvelopeSemitones, (i, v) => i.PitchEnvelopeSemitones = v, "0.#"),
            Number("Time · ms", "PitchEnvelopeMs", 0, 60000, 1, i => i.PitchEnvelopeMs, (i, v) => i.PitchEnvelopeMs = v, "0.#"));

        envelopeSection.Children.Add(SliderField("Output level", "InstrumentVolumeDb", -96, 12, .5,
            i => i.VolumeDb, (i, v) => i.VolumeDb = v, v => $"{v:0.#} dB"));
        _oscillatorSection = Section("Oscillator", oscillator, true);
        _oscillatorSection.Name = "OscillatorSection";
        _amplitudeSection = Section("Amplitude", envelopeSection, true);
        _amplitudeSection.Name = "AmplitudeSection";
        _filterSection = Section("Low-pass filter", _filterFields, false);
        _pitchSection = Section("Pitch envelope", _pitchFields, false);
        _filterSection.Name = "FilterSection"; _pitchSection.Name = "PitchSection";
        _toneLayout = new Grid { ColumnDefinitions = new("*"), RowDefinitions = new("Auto,Auto") };
        _toneLayout.Children.Add(_filterSection); Grid.SetRow(_pitchSection, 1); _toneLayout.Children.Add(_pitchSection);
        _soundColumn = new StackPanel();
        _soundColumn.Children.Add(_amplitudeSection); _soundColumn.Children.Add(_toneLayout);
        _sectionLayout = new Grid { ColumnDefinitions = new("*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 24 };
        _sectionLayout.Children.Add(_oscillatorSection); Grid.SetRow(_soundColumn, 1); _sectionLayout.Children.Add(_soundColumn);
        _body = new StackPanel { Spacing = 0 };
        _body.Children.Add(nameAndActions); _body.Children.Add(_sectionLayout);

        _panel = new StackPanel { Margin = new(14, 12, 14, 16) };
        _panel.Children.Add(header); _panel.Children.Add(_body);
        Content = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        SizeChanged += (_, _) => ApplyLayout();
        _body.IsEnabled = false;
    }

    /// <summary>Uses a two-column sound workspace when space permits, or a compact collapsible inspector.</summary>
    public void SetWideLayout(bool wide)
    {
        if (_wideLayout != wide)
        {
            _wideLayout = wide;
            _filterSection.IsExpanded = wide;
            _pitchSection.IsExpanded = wide;
            _tuningSection.IsExpanded = wide;
        }
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var twoColumns = _wideLayout && (Bounds.Width <= 0 || Bounds.Width >= 620);
        if (_layoutApplied && _twoColumns == twoColumns) return;
        _layoutApplied = true; _twoColumns = twoColumns;
        _sectionLayout.ColumnDefinitions = new(twoColumns ? "*,*" : "*");
        _sectionLayout.RowDefinitions = new(twoColumns ? "Auto" : "Auto,Auto");
        Grid.SetColumn(_soundColumn, twoColumns ? 1 : 0);
        Grid.SetRow(_soundColumn, twoColumns ? 0 : 1);
        _toneLayout.ColumnDefinitions = new(twoColumns ? "*,*" : "*");
        _toneLayout.RowDefinitions = new(twoColumns ? "Auto" : "Auto,Auto");
        _toneLayout.ColumnSpacing = twoColumns ? 16 : 0;
        Grid.SetColumn(_pitchSection, twoColumns ? 1 : 0);
        Grid.SetRow(_pitchSection, twoColumns ? 0 : 1);
        foreach (var fields in new[] { _filterFields, _pitchFields })
        {
            fields.ColumnDefinitions = new(twoColumns ? "*" : "*,*");
            fields.RowDefinitions = new(twoColumns ? "Auto,Auto" : "Auto");
            for (var n = 0; n < fields.Children.Count; n++)
            {
                Grid.SetColumn(fields.Children[n], twoColumns ? 0 : n);
                Grid.SetRow(fields.Children[n], twoColumns ? n : 0);
            }
        }
        _wave.Height = twoColumns ? 120 : 76;
        _envelope.Height = twoColumns ? 84 : 52;
        _panel.Margin = twoColumns ? new Thickness(20, 14, 20, 16) : new Thickness(14, 12, 14, 16);
    }

    /// <summary>Refreshes from the authoritative model without generating change callbacks.</summary>
    public void ShowInstrument(Song song, string? instrumentId)
    {
        var selected = instrumentId is null ? null : song.FindInstrument(instrumentId);
        if (_instrumentId != selected?.Id)
        {
            _editingFrame = 0;
            _wave.CancelGesture();
        }
        _instrument = selected; _instrumentId = selected?.Id;
        _refreshing = true;
        try
        {
            _body.IsEnabled = selected is not null;
            if (selected is null)
            {
                _name.Text = ""; _name.PlaceholderText = "Select an instrument";
                _scope.Text = "NO SOUND"; _wave.SetSamples(ShapeSamples(Waveform.Sine), false);
                return;
            }
            _name.PlaceholderText = "Instrument name";
            _name.Text = selected.Name;
            _scope.Text = selected.IsLocal ? "LOCAL" : "GLOBAL";
            _drum.SelectedIndex = (int)selected.Drum;
            _waveform.SelectedIndex = (int)selected.Waveform;
            _waveform.IsEnabled = selected.Drum == DrumKind.None;
            ToolTip.SetTip(_drum, selected.Drum == DrumKind.None ? "Oscillator voice · note-driven pitch" :
                "Drum voice · shape its tail, tone and pitch below");
            _tuningSection.IsVisible = selected.Drum == DrumKind.None;
            var custom = selected.Drum == DrumKind.None && selected.Waveform is Waveform.Custom or Waveform.Wavetable;
            _customTools.IsVisible = custom;
            _tableTools.IsVisible = selected.Drum == DrumKind.None && selected.Waveform == Waveform.Wavetable;
            _editingFrame = Math.Clamp(_editingFrame, 0, Math.Max(0, selected.Wavetable.Count - 1));
            _frames.ItemsSource = Enumerable.Range(1, selected.Wavetable.Count).Select(n => $"Frame {n:00}").ToArray();
            _frames.SelectedIndex = selected.Wavetable.Count > 0 ? _editingFrame : -1;
            _frameCount.Text = $"{selected.Wavetable.Count} FRAMES";
            _removeFrame.IsEnabled = selected.Wavetable.Count > 1;
            _addFrame.IsEnabled = selected.Wavetable.Count < SongLimits.MaxWaveFrames;
            _duplicateFrame.IsEnabled = selected.Wavetable.Count > 0 && selected.Wavetable.Count < SongLimits.MaxWaveFrames;
            foreach (var (control, read) in _numbers)
            {
                var value = read(selected);
                if (double.IsFinite(value)) control.Value = Math.Clamp((decimal)value, control.Minimum, control.Maximum);
            }
            foreach (var (control, readout, read, format) in _sliders)
            {
                control.Value = Math.Clamp(read(selected), control.Minimum, control.Maximum);
                readout.Text = format(control.Value);
            }
            RefreshWave(); _envelope.Show(selected.Amplitude);
        }
        finally { _refreshing = false; }
    }

    private void CommitName()
    {
        if (_refreshing || _instrument is null) return;
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) { _name.Text = _instrument.Name; return; }
        if (name != _instrument.Name) Edit(i => i.Name = name);
    }

    private void Edit(Action<Instrument> edit)
    {
        if (_refreshing || _instrumentId is not { } id) return;
        // Resolve in the supplied song, never mutate the UI's previous undo snapshot.
        _change(song => { if (song.FindInstrument(id) is { } instrument) edit(instrument); });
    }

    private void ReplaceEditorSamples(short[] samples)
    {
        if (_instrument is null || _refreshing) return;
        var frame = _editingFrame;
        var table = _instrument.Waveform == Waveform.Wavetable;
        var copy = (short[])samples.Clone();
        Edit(i =>
        {
            if (table)
            {
                if (i.Wavetable.Count == 0) i.Wavetable.Add(copy);
                else if (frame < i.Wavetable.Count) i.Wavetable[frame] = copy;
            }
            else { i.CustomWave = copy; i.Waveform = Waveform.Custom; }
        });
    }

    private short[] EditorSamples()
    {
        if (_instrument is null) return ShapeSamples(Waveform.Sine);
        if (_instrument.Waveform == Waveform.Wavetable && _instrument.Wavetable.Count > 0)
            return Resample(_instrument.Wavetable[Math.Clamp(_editingFrame, 0, _instrument.Wavetable.Count - 1)]);
        if (_instrument.Waveform == Waveform.Custom && _instrument.CustomWave.Length > 0)
            return Resample(_instrument.CustomWave);
        return ShapeSamples(_instrument.Waveform, _instrument.PulseWidth, _instrument.Phase);
    }

    private void RefreshWave()
    {
        if (_instrument is null) return;
        var custom = _instrument.Drum == DrumKind.None && _instrument.Waveform is Waveform.Custom or Waveform.Wavetable;
        _wave.SetSamples(EditorSamples(), custom);
        _waveHint.Text = _instrument.Drum != DrumKind.None ? "DRUM OSCILLATOR" : custom ? "128 SAMPLES · SIGNED PCM16" : "ONE CYCLE · OSCILLATOR PREVIEW";
    }

    private Control Number(string label, string name, double min, double max, double step,
        Func<Instrument, double> read, Action<Instrument, double> write, string format)
    {
        var input = new NumericUpDown { Name = name, Minimum = (decimal)min, Maximum = (decimal)max,
            Increment = (decimal)step, FormatString = format, FontSize = 11, MinHeight = 28,
            Padding = new(6, 3), ShowButtonSpinner = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        Accessible(input, label, $"{label}. Range {min} to {max}. Use up and down arrows to adjust.");
        _numbers.Add((input, read));
        input.ValueChanged += (_, _) =>
        {
            if (_refreshing || _instrument is null || input.Value is not { } value) return;
            var next = (double)value;
            if (Math.Abs(read(_instrument) - next) > .0000001) Edit(i => write(i, next));
        };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Ui.Label(label, 10, Ui.Muted)); stack.Children.Add(input); return stack;
    }

    private Control SliderField(string label, string name, double min, double max, double step,
        Func<Instrument, double> read, Action<Instrument, double> write, Func<double, string> format)
    {
        var slider = new Slider { Name = name, Minimum = min, Maximum = max, SmallChange = step,
            LargeChange = step * 10, TickFrequency = step, IsSnapToTickEnabled = true, MinHeight = 22 };
        Accessible(slider, label, $"{label}. Drag or use the arrow keys; one drag is one undoable edit.");
        var readout = Ui.Label(format(min), 10, Ui.Accent);
        _sliders.Add((slider, readout, read, format));
        slider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) readout.Text = format(slider.Value); };
        void Commit()
        {
            if (_refreshing || _instrument is null) return;
            var value = slider.Value;
            if (Math.Abs(read(_instrument) - value) > .0000001) Edit(i => write(i, value));
        }
        slider.AddHandler(PointerReleasedEvent, (_, _) => Commit(), RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) Commit();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        slider.LostFocus += (_, _) => Commit();
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(Ui.Label(label, 10, Ui.Muted)); Grid.SetColumn(readout, 1); header.Children.Add(readout);
        var panel = new StackPanel { Spacing = 0, Margin = new(0, 10, 0, 0) };
        panel.Children.Add(header); panel.Children.Add(slider); return panel;
    }

    private static Grid NumberGrid(params Control[] fields)
    {
        var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, RowSpacing = 8 };
        for (var r = 0; r < (fields.Length + 1) / 2; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var n = 0; n < fields.Length; n++)
        {
            Grid.SetColumn(fields[n], n % 2); Grid.SetRow(fields[n], n / 2); grid.Children.Add(fields[n]);
        }
        return grid;
    }

    private static Expander Section(string title, Control content, bool expanded)
    {
        content.Margin = new(0, 2, 0, 8);
        var section = new Expander
        {
            Header = new TextBlock { Text = title, FontSize = 11, Foreground = Ui.Text, FontWeight = FontWeight.SemiBold },
            Content = content, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(0),
            BorderBrush = Ui.Line, BorderThickness = new(0, 1, 0, 0), Margin = new(0, 10, 0, 0),
            Background = Brushes.Transparent
        };
        AutomationProperties.SetName(section, title + " sound controls");
        return section;
    }

    private static ComboBox Choice(string name, string accessibleName, string[] items)
    {
        var choice = new ComboBox { Name = name, ItemsSource = items, MinHeight = 29, FontSize = 11,
            Padding = new(8, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        Accessible(choice, accessibleName, accessibleName); return choice;
    }

    private static Button SmallIconButton(PackIconMaterialKind icon, Action action, string tip, bool accent = false)
    {
        var button = Ui.IconButton(icon, action, tip, accent);
        button.MinHeight = 28; button.MinWidth = 28; button.Padding = new(5);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.HorizontalContentAlignment = HorizontalAlignment.Center; return button;
    }

    private static void Accessible(Control control, string name, string tip)
    {
        AutomationProperties.SetName(control, name); ToolTip.SetTip(control, tip);
    }

    private static short[] Resample(short[] source)
    {
        if (source.Length == 0) return ShapeSamples(Waveform.Sine);
        if (source.Length == 128) return (short[])source.Clone();
        var result = new short[128];
        for (var n = 0; n < result.Length; n++)
        {
            var position = n * source.Length / 128d;
            var left = (int)position;
            result[n] = (short)Math.Clamp(Math.Round(source[left] + (source[(left + 1) % source.Length] - source[left]) * (position - left)), short.MinValue, short.MaxValue);
        }
        return result;
    }

    private static short[] RandomSamples()
    {
        var samples = new short[128];
        for (var n = 0; n < samples.Length; n++) samples[n] = (short)Random.Shared.Next(short.MinValue, short.MaxValue + 1);
        return samples;
    }

    private static short[] ShapeSamples(Waveform shape, double pulseWidth = .5, double phase = 0)
    {
        var result = new short[128]; uint random = 0x4D555349;
        for (var n = 0; n < result.Length; n++)
        {
            var p = (n / 128d + phase) % 1;
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            var value = shape switch
            {
                Waveform.Triangle => 1 - 4 * Math.Abs(p - .5),
                Waveform.Saw => 2 * p - 1,
                Waveform.Square => p < .5 ? 1 : -1,
                Waveform.Pulse => p < pulseWidth ? 1 : -1,
                Waveform.Noise => random / (double)uint.MaxValue * 2 - 1,
                _ => Math.Sin(p * 2 * Math.PI)
            };
            result[n] = (short)Math.Clamp(Math.Round(value * 32767), short.MinValue, short.MaxValue);
        }
        return result;
    }

    private sealed class WaveformDisplay : Control
    {
        private short[] _samples = ShapeSamples(Waveform.Sine);
        private short[]? _before;
        private bool _editable, _drawing;
        private IPointer? _pointer;
        private int _selected, _lastIndex;
        private short _lastValue;
        public event Action<short[]>? DrawCompleted;

        public void SetSamples(short[] samples, bool editable)
        {
            if (_drawing) return;
            _samples = samples; _editable = editable; Focusable = editable;
            Cursor = new Cursor(editable ? StandardCursorType.Cross : StandardCursorType.Arrow);
            InvalidateVisual();
        }
        public void CancelGesture()
        {
            if (_before is not null) _samples = _before;
            _before = null; _drawing = false;
            var pointer = _pointer; _pointer = null; pointer?.Capture(null);
            InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var box = new Rect(Bounds.Size);
            context.DrawRectangle(Ui.Surface, new Pen(Ui.Line), box, 5, 5);
            var inner = box.Deflate(8);
            if (inner.Width <= 0 || inner.Height <= 0) return;
            for (var x = 1; x < 8; x++) context.DrawLine(new Pen(Ui.Line, .5),
                new Point(inner.X + inner.Width * x / 8, inner.Y), new Point(inner.X + inner.Width * x / 8, inner.Bottom));
            context.DrawLine(new Pen(Ui.Line), new Point(inner.X, inner.Center.Y), new Point(inner.Right, inner.Center.Y));
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                for (var n = 0; n < _samples.Length; n++)
                {
                    var point = new Point(inner.X + inner.Width * n / (_samples.Length - 1), inner.Center.Y - _samples[n] / 32768d * inner.Height * .47);
                    if (n == 0) path.BeginFigure(point, false); else path.LineTo(point);
                }
                path.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(Ui.Accent, 1.6), geometry);
            if (_editable && IsFocused)
            {
                var x = inner.X + inner.Width * _selected / 127;
                var y = inner.Center.Y - _samples[_selected] / 32768d * inner.Height * .47;
                context.DrawLine(new Pen(Ui.Muted, .5), new(x, inner.Y), new(x, inner.Bottom));
                context.DrawEllipse(Ui.Accent, null, new Point(x, y), 3, 3);
            }
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!_editable || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus(); _before = (short[])_samples.Clone(); _drawing = true;
            (_lastIndex, _lastValue) = SampleAt(e.GetPosition(this));
            _samples[_lastIndex] = _lastValue; _selected = _lastIndex;
            _pointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true; InvalidateVisual();
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_drawing) return;
            var (index, value) = SampleAt(e.GetPosition(this));
            var start = Math.Min(index, _lastIndex); var end = Math.Max(index, _lastIndex);
            for (var n = start; n <= end; n++)
                _samples[n] = index == _lastIndex ? value : (short)Math.Round(_lastValue + (value - _lastValue) * (n - _lastIndex) / (double)(index - _lastIndex));
            _lastIndex = _selected = index; _lastValue = value;
            InvalidateVisual(); e.Handled = true;
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_drawing) return;
            var changed = _before is not null && !_samples.AsSpan().SequenceEqual(_before);
            _drawing = false; _before = null; _pointer = null; e.Pointer.Capture(null); e.Handled = true;
            if (changed) DrawCompleted?.Invoke((short[])_samples.Clone());
        }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _pointer = null;
            if (_drawing) CancelGesture();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!_editable) return;
            if (e.Key == Key.Escape) { CancelGesture(); e.Handled = true; return; }
            if (_drawing) return;
            if (e.Key is Key.Left or Key.Right)
            {
                _selected = Math.Clamp(_selected + (e.Key == Key.Left ? -1 : 1), 0, 127);
                InvalidateVisual(); e.Handled = true;
            }
            if (e.Key is Key.Up or Key.Down)
            {
                var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 4096 : 512;
                _samples[_selected] = (short)Math.Clamp(_samples[_selected] + (e.Key == Key.Up ? delta : -delta), short.MinValue, short.MaxValue);
                InvalidateVisual(); DrawCompleted?.Invoke((short[])_samples.Clone()); e.Handled = true;
            }
        }
        private (int Index, short Value) SampleAt(Point p)
        {
            var inner = new Rect(Bounds.Size).Deflate(8);
            var index = (int)Math.Clamp(Math.Round((p.X - inner.X) / Math.Max(1, inner.Width) * 127), 0, 127);
            var value = (short)Math.Clamp(Math.Round((inner.Center.Y - p.Y) / Math.Max(1, inner.Height * .47) * 32768), short.MinValue, short.MaxValue);
            return (index, value);
        }
    }

    private sealed class EnvelopeDisplay : Control
    {
        private double _attack = 5, _decay = 120, _sustain = .55, _release = 90;
        public void Show(Envelope envelope)
        {
            _attack = envelope.AttackMs; _decay = envelope.DecayMs;
            _sustain = envelope.Sustain; _release = envelope.ReleaseMs; InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var box = new Rect(Bounds.Size);
            context.DrawRectangle(Ui.Surface, new Pen(Ui.Line), box, 5, 5);
            var r = box.Deflate(8);
            if (r.Width <= 0 || r.Height <= 0) return;
            // A brief visual sustain plateau keeps the four stages legible at any time scale.
            var a = Math.Max(12, Math.Sqrt(Math.Max(0, _attack)));
            var d = Math.Max(12, Math.Sqrt(Math.Max(0, _decay)));
            var release = Math.Max(12, Math.Sqrt(Math.Max(0, _release)));
            var hold = (a + d + release) * .3; var total = a + d + hold + release;
            var sustain = r.Bottom - Math.Clamp(_sustain, 0, 1) * r.Height;
            Point[] points = [new(r.X, r.Bottom), new(r.X + r.Width * a / total, r.Y),
                new(r.X + r.Width * (a + d) / total, sustain),
                new(r.X + r.Width * (a + d + hold) / total, sustain), new(r.Right, r.Bottom)];
            var fill = new StreamGeometry();
            using (var path = fill.Open())
            {
                path.BeginFigure(points[0], true);
                for (var n = 1; n < points.Length; n++) path.LineTo(points[n]);
                path.EndFigure(true);
            }
            context.DrawGeometry(Ui.Selection, null, fill);
            for (var n = 1; n < points.Length; n++) context.DrawLine(new Pen(Ui.Accent, 1.4), points[n - 1], points[n]);
            for (var n = 1; n < points.Length - 1; n++) context.DrawEllipse(Ui.Accent, null, points[n], 2, 2);
        }
    }
}
