using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MusicMachine.Audio;
using IconPacks.Avalonia.Material;

namespace MusicMachine.App;

public enum SamplingApplyMode { NewInstrument, ReplaceWave, AppendFrame }

/// <summary>Transient clip and non-destructive single-cycle design. Only Apply changes a song.</summary>
public sealed class SamplingPanel : UserControl
{
    private readonly Func<CancellationToken, Task<SampleClip?>> _import;
    private readonly Func<short[], string, SamplingApplyMode, string> _apply;
    private readonly Action<short[]> _audition;
    private readonly Action _stop;
    private readonly TextBlock _source = Text("Import a clip to find its characteristic tone", 15);
    private readonly TextBlock _analysis = Text("WAV and QOA work on every platform. Source audio stays here until cleared or the app closes.");
    private readonly TextBlock _outputInfo = Text("No extracted waveform yet");
    private readonly TextBlock _target = Text("Creates an independent instrument in this song");
    private readonly SampleWaveCanvas _sourceWave = new() { Name = "SamplingSourceWave", Height = 120, Selectable = true };
    private readonly SampleWaveCanvas _outputWave = new() { Name = "SamplingOutputWave", Height = 96 };
    private readonly NumericUpDown _regionStart, _regionEnd, _cycleStart, _period;
    private readonly Slider _smoothing, _drive, _blend;
    private readonly ComboBox _applyMode;
    private readonly Button _importButton, _detectButton, _extractButton, _applyButton, _auditionButton, _clearButton, _cancelButton, _resetButton;
    private readonly Grid _fields;
    private readonly Expander _manual;
    private SampleClip? _clip;
    private short[] _baseWave = [], _shapedWave = [];
    private CancellationTokenSource? _work;
    private int _generation, _start, _end;
    private bool _updating;
    private string _targetName = "selected instrument";

    public SamplingPanel(Func<CancellationToken, Task<SampleClip?>> import,
        Func<short[], string, SamplingApplyMode, string> apply, Action<short[]> audition, Action stop)
    {
        _import = import; _apply = apply; _audition = audition; _stop = stop;
        Name = "SamplingPanel"; Background = Ui.Background;
        AutomationProperties.SetName(this, "Sampling waveform instrument designer");
        AutomationProperties.SetName(_outputWave, "Extracted periodic waveform preview");
        var body = new StackPanel { Spacing = 8, Margin = new(12), MaxWidth = 1120, HorizontalAlignment = HorizontalAlignment.Stretch };
        var title = new Grid { ColumnDefinitions = new("*,Auto") };
        title.Children.Add(Text("Sampling", 18));
        _importButton = Icon(PackIconMaterialKind.FolderOpen, () => _ = ImportAsync(), "Import audio for waveform extraction", "SamplingImport");
        _clearButton = Icon(PackIconMaterialKind.Close, Clear, "Clear transient source clip", "SamplingClear");
        var tools = Ui.Row(_importButton, _clearButton); Grid.SetColumn(tools, 1); title.Children.Add(tools); body.Children.Add(title);
        body.Children.Add(Text("Turn a representative cycle into a new synth voice. This does not preserve or play back the full recording."));
        body.Children.Add(_source); body.Children.Add(_sourceWave);
        _sourceWave.RegionChanged += (start, end) => SetRegion(start, end);
        _regionStart = Number("SamplingRegionStart", "Selection start in milliseconds", 0, 0, 30000, .1m, "0.0");
        _regionEnd = Number("SamplingRegionEnd", "Selection end in milliseconds", 0, 0, 30000, .1m, "0.0");
        _cycleStart = Number("SamplingCycleStart", "Cycle start in source samples", 0, 0, 1440000, 1, "0");
        _period = Number("SamplingPeriod", "Manual period in source samples", 128, 2, 9600, .01m, "0.00");
        _fields = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 8 };
        AddField("Selection start · ms", _regionStart, 0, 0); AddField("Selection end · ms", _regionEnd, 1, 0);
        AddField("Cycle start · samples", _cycleStart, 0, 1); AddField("Period · samples", _period, 1, 1);
        _regionStart.ValueChanged += (_, _) => RegionFieldsChanged(); _regionEnd.ValueChanged += (_, _) => RegionFieldsChanged();
        _cycleStart.ValueChanged += (_, _) => ManualFieldsChanged(); _period.ValueChanged += (_, _) => ManualFieldsChanged();
        _detectButton = Icon(PackIconMaterialKind.AutoFix, () => _ = AnalyzeAsync(), "Find a stable repeating cycle in the selected region", "SamplingDetect");
        _extractButton = Icon(PackIconMaterialKind.ContentCut, ExtractManual, "Extract the cycle at the manual start and period", "SamplingExtract");
        _cancelButton = Icon(PackIconMaterialKind.Stop, CancelWork, "Cancel audio import or analysis", "SamplingCancel");
        tools.Children.Insert(1, _detectButton); tools.Children.Insert(2, _cancelButton);
        var manualBody = new StackPanel { Spacing = 8, Margin = new(0, 6, 0, 0) };
        manualBody.Children.Add(_fields); manualBody.Children.Add(Ui.Row(_extractButton, Text("Extract manual cycle")));
        _manual = new Expander { Name = "SamplingManualControls", Header = "Selection and manual period", Content = manualBody, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(0), BorderThickness = new(0) };
        AutomationProperties.SetName(_manual, "Selection bounds and manual cycle controls"); body.Children.Add(_manual);
        body.Children.Add(_analysis);
        ToolTip.SetTip(_sourceWave, "Drag to select a region. Use Find stable cycle, or expand Selection and manual period for precise bounds and unpitched sounds.");
        body.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new(0, 4) });
        var outputHeader = new Grid { ColumnDefinitions = new("*,Auto") }; outputHeader.Children.Add(Text("Extracted waveform", 14));
        _auditionButton = Icon(PackIconMaterialKind.Play, Audition, "Audition extracted instrument at C4", "SamplingAudition");
        var outputTools = Ui.Row(_auditionButton, Icon(PackIconMaterialKind.Stop, _stop, "Stop audition", "SamplingStop")); Grid.SetColumn(outputTools, 1); outputHeader.Children.Add(outputTools); body.Children.Add(outputHeader);
        body.Children.Add(_outputWave); body.Children.Add(_outputInfo);
        var shaping = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 14 };
        _smoothing = ShapeControl(shaping, "Smooth", "SamplingSmoothing", 0);
        _drive = ShapeControl(shaping, "Drive", "SamplingDrive", 1);
        _blend = ShapeControl(shaping, "Sine blend", "SamplingBlend", 2);
        body.Children.Add(shaping);
        _resetButton = Icon(PackIconMaterialKind.Eraser, ResetShape, "Reset smoothing, drive and sine blend", "SamplingReset"); outputTools.Children.Add(_resetButton);
        _applyMode = new ComboBox { Name = "SamplingApplyMode", ItemsSource = new[] { "New instrument", "Replace selected waveform", "Append selected wavetable frame" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 220 };
        AutomationProperties.SetName(_applyMode, "Where to apply extracted waveform");
        _applyMode.SelectionChanged += (_, _) => UpdateActions();
        _applyButton = Icon(PackIconMaterialKind.Plus, Apply, "Create an instrument from the extracted waveform", "SamplingApply"); _applyButton.Classes.Add("accent");
        body.Children.Add(Wrap(_applyMode, _applyButton)); body.Children.Add(_target);
        body.Children.Add(Text("Apply is undoable. The song stores only the 128-sample waveform, with no file dependency. Shaping and audition leave your song unchanged."));
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        UpdateActions();
    }

    public bool IsBusy => _work is not null;
    public bool HasSource => _clip is not null;
    public short[] ExtractedWave => (short[])_shapedWave.Clone();
    public void SetTarget(string name) { _targetName = name; UpdateActions(); }

    public async Task ImportAsync()
    {
        if (IsBusy) return;
        var work = StartWork("Opening audio…"); var generation = _generation; bool loaded = false;
        try
        {
            var clip = await _import(work.Token);
            if (work.IsCancellationRequested || generation != _generation) return;
            if (clip is null) { _analysis.Text = "Import cancelled; your previous extraction is unchanged"; return; }
            LoadClip(clip); loaded = true;
        }
        catch (OperationCanceledException) { if (generation == _generation) _analysis.Text = "Import cancelled; your previous extraction is unchanged"; }
        catch (Exception e) { if (generation == _generation) _analysis.Text = "Could not import: " + e.Message; }
        finally { FinishWork(work); }
        if (loaded && generation == _generation && _clip is not null) await AnalyzeAsync();
    }

    /// <summary>Replace the transient clip, preserving neither raw audio nor selection in the song.</summary>
    public void LoadClip(SampleClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Samples.Length < 3 || clip.Samples.Length > SampleImporter.MaxSamples || clip.SampleRate <= 0)
            throw new ArgumentException("The clip needs at least three samples and must fit the sampling limits.");
        _clip = clip; _manual.IsExpanded = false; _sourceWave.SetSamples(clip.Samples);
        _source.Text = $"{clip.Name} · {clip.DurationSeconds:0.00} s · {clip.SampleRate:N0} Hz · {(clip.OriginalChannels > 0 ? clip.OriginalChannels + " ch → mono" : "converted to mono")}";
        _baseWave = []; _shapedWave = []; _outputWave.SetSamples([]);
        _updating = true;
        try
        {
            _regionStart.Maximum = _regionEnd.Maximum = (decimal)(clip.DurationSeconds * 1000);
            _cycleStart.Maximum = clip.Samples.Length - 3;
            _period.Maximum = Math.Max(2, Math.Min(9600, clip.Samples.Length - 1));
            _period.Value = Math.Min(128, _period.Maximum); _smoothing.Value = _drive.Value = _blend.Value = 0;
        }
        finally { _updating = false; }
        SetRegion(0, clip.Samples.Length);
    }

    public void SetRegion(int start, int end)
    {
        if (_clip is null) return;
        _start = Math.Clamp(start, 0, _clip.Samples.Length - 3); _end = Math.Clamp(end, _start + 3, _clip.Samples.Length);
        _updating = true;
        try
        {
            _regionStart.Value = (decimal)(_start * 1000d / _clip.SampleRate); _regionEnd.Value = (decimal)(_end * 1000d / _clip.SampleRate);
            _cycleStart.Minimum = 0; _cycleStart.Maximum = _end - 3; _cycleStart.Minimum = _start; _cycleStart.Value = _start;
            _period.Maximum = Math.Max(2, Math.Min(9600, _end - _start - 1)); _period.Value = Math.Min(_period.Value ?? 128, _period.Maximum);
        }
        finally { _updating = false; }
        _sourceWave.SetSelection(_start, _end); ClearExtraction();
        _analysis.Text = "Region selected. Find a stable cycle, or set a manual start and period."; UpdateActions();
    }

    public async Task AnalyzeAsync()
    {
        if (_clip is null || IsBusy) return;
        var work = StartWork("Looking for a stable repeating cycle…"); var generation = _generation; var clip = _clip; var start = _start; var length = _end - _start;
        try
        {
            // Let the UI paint busy/cancel before bounded DSP. Native hosts use a worker thread.
            await Task.Delay(20, work.Token);
            var result = await Task.Run(() => SampleAnalysis.Detect(clip, start, length, work.Token), work.Token);
            if (generation != _generation || work.IsCancellationRequested) return;
            if (!result.HasPitch)
            {
                ClearExtraction(); _manual.IsExpanded = true; _analysis.Text = $"Low confidence ({result.Confidence:P0}). {result.Message} Select a pitched region or extract a manual cycle.";
                return;
            }
            _updating = true;
            try { _cycleStart.Value = result.Start; _period.Value = (decimal)result.Period; }
            finally { _updating = false; }
            ExtractCurrent();
            _analysis.Text = $"Estimated {result.Frequency:0.0} Hz · confidence {result.Confidence:P0} · period {result.Period:0.00} samples. Listen before applying.";
        }
        catch (OperationCanceledException) { if (generation == _generation) _analysis.Text = "Analysis cancelled"; }
        catch (Exception e) { if (generation == _generation) _analysis.Text = "Analysis: " + e.Message; }
        finally { FinishWork(work); }
    }

    public void CancelWork() { _work?.Cancel(); }
    public void Clear()
    {
        _generation++; _work?.Cancel(); _stop(); _clip = null; _sourceWave.SetSamples([]); ClearExtraction(); _source.Text = "Import a clip to find its characteristic tone";
        _analysis.Text = "Source cleared. Instruments already applied to the song are preserved."; UpdateActions();
    }

    private CancellationTokenSource StartWork(string status)
    {
        var work = new CancellationTokenSource(); _work = work; _analysis.Text = status; UpdateActions(); return work;
    }
    private void FinishWork(CancellationTokenSource work) { if (ReferenceEquals(_work, work)) _work = null; work.Dispose(); UpdateActions(); }
    private void RegionFieldsChanged()
    {
        if (_updating || IsBusy || _clip is null) return;
        SetRegion((int)Math.Round((double)(_regionStart.Value ?? 0) * _clip.SampleRate / 1000), (int)Math.Round((double)(_regionEnd.Value ?? 0) * _clip.SampleRate / 1000));
    }
    private void ManualFieldsChanged()
    {
        if (_updating || IsBusy || _clip is null) return;
        ClearExtraction(); _analysis.Text = "Manual cycle changed. Choose Extract manual cycle to preview it."; UpdateActions();
    }
    private void ExtractManual()
    {
        if (IsBusy || _clip is null) return;
        try { ExtractCurrent(); _analysis.Text = $"Manual cycle · {_clip.SampleRate / (double)(_period.Value ?? 128):0.0} Hz assumed · pitch confidence is not measured. Listen before applying."; }
        catch (Exception e) { ClearExtraction(); _analysis.Text = "Manual extraction: " + e.Message; }
        UpdateActions();
    }
    private void ExtractCurrent()
    {
        if (_clip is null) return;
        int start = (int)(_cycleStart.Value ?? _start); double period = (double)(_period.Value ?? 128);
        if (start < _start || start + period >= _end) throw new ArgumentException("The complete cycle must fit inside the selected region. Shorten the period or move the cycle start.");
        _baseWave = SampleAnalysis.Extract(_clip, start, period);
        _sourceWave.SetCycle(start, period); Shape();
    }
    private void ClearExtraction() { _baseWave = []; _shapedWave = []; _outputWave.SetSamples([]); _sourceWave.SetCycle(0, 0); _outputInfo.Text = "No extracted waveform yet"; }
    private void Shape()
    {
        if (_updating || _baseWave.Length == 0) return;
        _shapedWave = SampleAnalysis.Shape(_baseWave, _smoothing.Value, _drive.Value, _blend.Value);
        _outputWave.SetSamples(_shapedWave.Select(s => s / 32768f).ToArray());
        _outputInfo.Text = _shapedWave.Any(s => Math.Abs((int)s) > 4) ? "128 samples · DC removed · periodic seam repaired · shaping is reversible" : "This extraction is silent. Choose another region or period.";
        UpdateActions();
    }
    private void ResetShape() { _updating = true; _smoothing.Value = _drive.Value = _blend.Value = 0; _updating = false; Shape(); }
    private void Audition() { if (_shapedWave.Length == 0 || IsBusy) return; try { _audition((short[])_shapedWave.Clone()); } catch (Exception e) { _analysis.Text = "Audition: " + e.Message; } }
    private void Apply()
    {
        if (!_shapedWave.Any(s => Math.Abs((int)s) > 4) || IsBusy || _clip is null) return;
        try { _analysis.Text = _apply((short[])_shapedWave.Clone(), _clip.Name, (SamplingApplyMode)Math.Max(0, _applyMode.SelectedIndex)); }
        catch (Exception e) { _analysis.Text = "Apply: " + e.Message; }
    }
    private void UpdateActions()
    {
        if (_applyButton is null) return;
        var ready = !IsBusy && _clip is not null; var hasWave = ready && _shapedWave.Any(s => Math.Abs((int)s) > 4);
        _importButton.IsEnabled = !IsBusy; _clearButton.IsEnabled = _clip is not null || IsBusy; _cancelButton.IsVisible = IsBusy;
        _detectButton.IsEnabled = _extractButton.IsEnabled = _fields.IsEnabled = _sourceWave.IsEnabled = ready;
        _smoothing.IsEnabled = _drive.IsEnabled = _blend.IsEnabled = _resetButton.IsEnabled = ready && _baseWave.Length > 0;
        _auditionButton.IsEnabled = _applyButton.IsEnabled = hasWave;
        if (_applyButton.Content is PackIconMaterial applyIcon) applyIcon.Kind = _applyMode.SelectedIndex switch { 1 => PackIconMaterialKind.ContentCopy, 2 => PackIconMaterialKind.LayersPlus, _ => PackIconMaterialKind.Plus };
        string applyLabel = _applyMode.SelectedIndex switch { 1 => "Replace the selected instrument waveform", 2 => "Append a frame to the selected wavetable", _ => "Create an instrument from the extracted waveform" };
        ToolTip.SetTip(_applyButton, applyLabel); AutomationProperties.SetName(_applyButton, applyLabel);
        _target.Text = _applyMode.SelectedIndex switch { 1 => $"Replaces {_targetName}'s oscillator and disables its drum engine. All notes using it will change. Undo restores it.", 2 => $"Adds a frame to {_targetName} and switches it to wavetable synthesis. Undo restores it.", _ => "Creates an independent instrument in this song. Select it in the library to assign it to a track." };
    }
    private void AddField(string label, Control control, int column, int row) { var panel = new StackPanel { Spacing = 3 }; panel.Children.Add(Text(label)); panel.Children.Add(control); Grid.SetColumn(panel, column); Grid.SetRow(panel, row); _fields.Children.Add(panel); }
    private Slider ShapeControl(Grid grid, string label, string name, int column)
    {
        var panel = new StackPanel { Spacing = 2 }; panel.Children.Add(Text(label));
        var slider = new Slider { Name = name, Minimum = 0, Maximum = 1, SmallChange = .01, LargeChange = .1, Value = 0 };
        AutomationProperties.SetName(slider, label + " extracted waveform"); ToolTip.SetTip(slider, "Non-destructive " + label.ToLowerInvariant() + " · 0 to 100%");
        slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Shape(); };
        panel.Children.Add(slider); Grid.SetColumn(panel, column); grid.Children.Add(panel); return slider;
    }
    private static NumericUpDown Number(string name, string label, decimal value, decimal min, decimal max, decimal increment, string format)
    {
        var result = new NumericUpDown { Name = name, Value = value, Minimum = min, Maximum = max, Increment = increment, FormatString = format, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(result, label); ToolTip.SetTip(result, label); return result;
    }
    private static TextBlock Text(string text, double size = 11) => new() { Text = text, FontSize = size, Foreground = size >= 14 ? Ui.Text : Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private static Button Icon(PackIconMaterialKind kind, Action action, string tip, string name) { var b = Ui.IconButton(kind, action, tip); b.Name = name; return b; }
    private static WrapPanel Wrap(params Control[] controls) { var panel = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 }; foreach (var c in controls) panel.Children.Add(c); return panel; }
}

/// <summary>Bounded peak overview. Pointer gestures are transactional, with Escape/capture-loss rollback.</summary>
public sealed class SampleWaveCanvas : Control
{
    private float[] _min = [], _max = [];
    private int _length, _start, _end, _anchor, _beforeStart, _beforeEnd;
    private double _cycleStart, _period;
    private IPointer? _pointer;
    public bool Selectable { get; set; }
    public event Action<int, int>? RegionChanged;
    public SampleWaveCanvas() { Focusable = true; AutomationProperties.SetName(this, "Source waveform selection; use selection start and end fields for keyboard access"); }
    public void SetSamples(float[] samples)
    {
        CancelGesture(); _length = samples.Length; var bins = Math.Min(1024, samples.Length); _min = new float[bins]; _max = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            int first = (int)((long)b * samples.Length / bins), end = (int)((long)(b + 1) * samples.Length / bins); float min = 1, max = -1;
            for (int i = first; i < end; i++) { min = Math.Min(min, samples[i]); max = Math.Max(max, samples[i]); }
            _min[b] = min; _max[b] = max;
        }
        _start = 0; _end = samples.Length; _period = 0; InvalidateVisual();
    }
    public void SetSelection(int start, int end) { _start = start; _end = end; InvalidateVisual(); }
    public void SetCycle(double start, double period) { _cycleStart = start; _period = period; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context); var box = new Rect(Bounds.Size); context.DrawRectangle(Ui.Workspace, new Pen(Ui.Line), box, 4, 4); var r = box.Deflate(8); if (r.Width <= 0 || r.Height <= 0) return;
        if (Selectable && _length > 0)
        {
            double left = r.X + r.Width * _start / _length, right = r.X + r.Width * _end / _length;
            context.DrawRectangle(Ui.Selection, null, new Rect(left, r.Y, Math.Max(0, right - left), r.Height));
            context.DrawLine(new Pen(Ui.Accent), new(left, r.Y), new(left, r.Bottom)); context.DrawLine(new Pen(Ui.Accent), new(right, r.Y), new(right, r.Bottom));
        }
        context.DrawLine(new Pen(Ui.Line, .5), new(r.X, r.Center.Y), new(r.Right, r.Center.Y));
        for (int b = 0; b < _min.Length; b++)
        {
            var x = r.X + r.Width * b / Math.Max(1, _min.Length - 1);
            context.DrawLine(new Pen(Ui.Accent, Math.Max(1, r.Width / Math.Max(1, _min.Length))), new(x, r.Center.Y - _max[b] * r.Height * .45), new(x, r.Center.Y - _min[b] * r.Height * .45));
            if (b > 0 && _min.Length <= 256) context.DrawLine(new Pen(Ui.Accent, 1.4), new(r.X + r.Width * (b - 1) / (_min.Length - 1), r.Center.Y - _max[b - 1] * r.Height * .45), new(x, r.Center.Y - _max[b] * r.Height * .45));
        }
        if (Selectable && _period > 0 && _length > 0) context.DrawRectangle(null, new Pen(Ui.Success, 2), new Rect(r.X + r.Width * _cycleStart / _length, r.Y + 3, Math.Max(2, r.Width * _period / _length), r.Height - 6));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!Selectable || _length < 3 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); _beforeStart = _start; _beforeEnd = _end; _anchor = SampleAt(e.GetPosition(this)); _pointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (_pointer is null) return; int sample = SampleAt(e.GetPosition(this)); SetSelection(Math.Min(_anchor, sample), Math.Max(_anchor, sample)); e.Handled = true; }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); if (_pointer is null) return; int sample = SampleAt(e.GetPosition(this)); _pointer = null; e.Pointer.Capture(null);
        int start = Math.Clamp(Math.Min(_anchor, sample), 0, _length - 3), end = Math.Clamp(Math.Max(_anchor, sample), start + 3, _length);
        SetSelection(start, end); RegionChanged?.Invoke(start, end); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); CancelGesture(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape && _pointer is not null) { CancelGesture(); e.Handled = true; } }
    private void CancelGesture() { if (_pointer is null) return; var pointer = _pointer; _pointer = null; SetSelection(_beforeStart, _beforeEnd); pointer.Capture(null); }
    private int SampleAt(Point p) { var r = new Rect(Bounds.Size).Deflate(8); return (int)Math.Clamp(Math.Round((p.X - r.X) / Math.Max(1, r.Width) * _length), 0, Math.Max(0, _length - 1)); }
}
