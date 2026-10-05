using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicMachine.Core;

namespace MusicMachine.App;

/// <summary>Local drag preview; authoritative song changes only on release. Capture loss and Escape roll back.</summary>
public sealed class WaveformDisplay : Control
{
    private enum Handle { None, Samples, Amplitude, Peak, Width, High, Low }
    private Instrument? _view, _before;
    private short[] _samples = [], _beforeSamples = [];
    private Handle _drag, _selectedHandle = Handle.Amplitude;
    private IPointer? _pointer;
    private Point _press;
    private int _selected, _lastIndex;
    private short _lastValue;
    public event Action<short[]>? DrawCompleted;
    public event Action<Instrument>? ShapeCompleted, PreviewChanged;
    public bool IsDragging => _drag != Handle.None;
    public double PreviewAmplitude => _view?.OscillatorAmplitude ?? 0;

    public void Show(Instrument? instrument, int frame = 0)
    {
        CancelGesture();
        _view = instrument is null ? null : InstrumentFile.Clone(instrument);
        _samples = instrument is null ? [] : instrument.Waveform switch
        {
            Waveform.Custom => (short[])instrument.CustomWave.Clone(),
            Waveform.Wavetable when instrument.Wavetable.Count > 0 => (short[])instrument.Wavetable[Math.Clamp(frame, 0, instrument.Wavetable.Count - 1)].Clone(),
            _ => WaveformShape.Cycle(instrument)
        };
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _samples.Length - 1));
        Focusable = Editable;
        Cursor = new Cursor(Editable ? StandardCursorType.Cross : StandardCursorType.Arrow);
        InvalidateVisual();
    }
    private bool Editable => _view is { Drum: DrumKind.None };
    private bool PointMode => _view?.Waveform is Waveform.Custom or Waveform.Wavetable;
    // The last 22 px are a separate, unambiguous amplitude rail, available even for silent waves.
    private Rect Plot => new(8, 10, Math.Max(1, Bounds.Width - 38), Math.Max(1, Bounds.Height - 20));
    private Point At(double x, double y) => new(Plot.X + Plot.Width * x, Plot.Center.Y - y * Plot.Height * .47);
    private Point AmplitudePoint => new(Bounds.Width - 13, At(0, _view?.OscillatorAmplitude ?? 0).Y);
    private Point PeakPoint => At(_view!.TrianglePeak, _view.OscillatorAmplitude);
    private double ShapeWidth => _view!.Waveform == Waveform.Square ? _view.SquareWidth : _view.PulseWidth;
    private Point WidthPoint => At(ShapeWidth, 0);
    private Point HighPoint => At(ShapeWidth / 2, _view!.WaveHigh * _view.OscillatorAmplitude);
    private Point LowPoint => At((1 + ShapeWidth) / 2, _view!.WaveLow * _view.OscillatorAmplitude);
    private double X(Point p) => Math.Clamp((p.X - Plot.X) / Plot.Width, .01, .99);
    private double Y(Point p) => Math.Clamp((Plot.Center.Y - p.Y) / (Plot.Height * .47), -1, 1);

    public void CancelGesture()
    {
        if (!IsDragging) return;
        _view = _before; _samples = _beforeSamples; _before = null; _drag = Handle.None;
        var pointer = _pointer; _pointer = null; pointer?.Capture(null);
        if (_view is not null) PreviewChanged?.Invoke(_view);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Ui.Surface, new Pen(Ui.Line), new Rect(Bounds.Size), 5, 5);
        if (_view is null || Bounds.Width < 40 || Bounds.Height < 24) return;
        for (var x = 1; x < 8; x++) context.DrawLine(new Pen(Ui.Line, .5), At(x / 8d, 1), At(x / 8d, -1));
        context.DrawLine(new Pen(Ui.Line), At(0, 0), At(1, 0));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            if (_view.Waveform == Waveform.Triangle)
            {
                path.BeginFigure(At(0, -_view.OscillatorAmplitude), false);
                path.LineTo(PeakPoint); path.LineTo(At(1, -_view.OscillatorAmplitude));
            }
            else if (_view.Waveform is Waveform.Square or Waveform.Pulse)
            {
                var high = _view.WaveHigh * _view.OscillatorAmplitude;
                var low = _view.WaveLow * _view.OscillatorAmplitude;
                path.BeginFigure(At(0, high), false); path.LineTo(At(ShapeWidth, high));
                path.LineTo(At(ShapeWidth, low)); path.LineTo(At(1, low)); path.LineTo(At(1, high));
            }
            else
            {
                for (var n = 0; n < _samples.Length; n++)
                {
                    var point = At(n / (double)_samples.Length, _samples[n] / 32768d * _view.OscillatorAmplitude);
                    if (n == 0) path.BeginFigure(point, false); else path.LineTo(point);
                }
                // Close the periodic cycle visually without adding an extra stored sample.
                if (_samples.Length > 0) path.LineTo(At(1, _samples[0] / 32768d * _view.OscillatorAmplitude));
            }
            path.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Ui.Accent, 1.6), geometry);
        if (!Editable) return;
        context.DrawLine(new Pen(Ui.Muted), new(Bounds.Width - 13, At(0, 1).Y), new(Bounds.Width - 13, At(0, 0).Y));
        DrawHandle(context, AmplitudePoint, _selectedHandle == Handle.Amplitude);
        if (_view.Waveform == Waveform.Triangle) DrawHandle(context, PeakPoint, _selectedHandle == Handle.Peak);
        if (_view.Waveform is Waveform.Square or Waveform.Pulse)
        {
            context.DrawLine(new Pen(Ui.Muted, .5), At(ShapeWidth, 1), At(ShapeWidth, -1));
            DrawHandle(context, WidthPoint, _selectedHandle == Handle.Width);
            DrawHandle(context, HighPoint, _selectedHandle == Handle.High);
            DrawHandle(context, LowPoint, _selectedHandle == Handle.Low);
        }
        if (PointMode && IsFocused && _samples.Length > 0)
            DrawHandle(context, At(_selected / (double)_samples.Length, _samples[_selected] / 32768d * _view.OscillatorAmplitude), true);
    }
    private void DrawHandle(DrawingContext context, Point p, bool selected)
        => context.DrawEllipse(selected && IsFocused ? Ui.Accent : Ui.Surface, new Pen(Ui.Accent, 1.5), p, 4, 4);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Editable || IsDragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _drag = Hit(p);
        if (_drag == Handle.None) return;
        Focus(); _selectedHandle = _drag;
        _before = InstrumentFile.Clone(_view!); _beforeSamples = (short[])_samples.Clone(); _press = p;
        _pointer = e.Pointer; e.Pointer.Capture(this);
        if (_drag == Handle.Samples)
        {
            (_lastIndex, _lastValue) = SampleAt(p); _selected = _lastIndex; _samples[_selected] = _lastValue;
        }
        else UpdateShape(p);
        e.Handled = true; InvalidateVisual();
    }
    private Handle Hit(Point p)
    {
        if (p.X >= Plot.Right + 5) return Handle.Amplitude;
        if (PointMode) return Handle.Samples;
        if (_view!.Waveform == Waveform.Triangle && Near(p, PeakPoint)) return Handle.Peak;
        if (_view.Waveform is Waveform.Square or Waveform.Pulse)
        {
            if (Near(p, HighPoint)) return Handle.High;
            if (Near(p, LowPoint)) return Handle.Low;
            if (Math.Abs(p.X - WidthPoint.X) <= 9) return Handle.Width;
        }
        return Handle.None;
    }
    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= 10 && Math.Abs(a.Y - b.Y) <= 10;
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsDragging || e.Pointer != _pointer) return;
        Move(e.GetPosition(this)); e.Handled = true;
    }
    private void Move(Point p)
    {
        if (_drag == Handle.Samples)
        {
            var (index, value) = SampleAt(p);
            for (var n = Math.Min(index, _lastIndex); n <= Math.Max(index, _lastIndex); n++)
                _samples[n] = index == _lastIndex ? value : (short)Math.Round(_lastValue + (value - _lastValue) * (n - _lastIndex) / (double)(index - _lastIndex));
            _lastIndex = _selected = index; _lastValue = value;
        }
        else UpdateShape(p);
        InvalidateVisual();
    }
    private void UpdateShape(Point p)
    {
        if (_view is null) return;
        switch (_drag)
        {
            case Handle.Amplitude: _view.OscillatorAmplitude = Math.Clamp(Y(p), 0, 1); break;
            case Handle.Peak: _view.TrianglePeak = X(p); _view.OscillatorAmplitude = Math.Clamp(Y(p), 0, 1); break;
            case Handle.Width:
                if (_view.Waveform == Waveform.Square) _view.SquareWidth = X(p); else _view.PulseWidth = X(p);
                break;
            case Handle.High:
                _view.WaveHigh = Math.Clamp(_before!.WaveHigh + (Y(p) - Y(_press)) / (_view.OscillatorAmplitude == 0 ? 1 : _view.OscillatorAmplitude), -1, 1);
                break;
            case Handle.Low:
                _view.WaveLow = Math.Clamp(_before!.WaveLow + (Y(p) - Y(_press)) / (_view.OscillatorAmplitude == 0 ? 1 : _view.OscillatorAmplitude), -1, 1);
                break;
        }
        if (!PointMode) _samples = WaveformShape.Cycle(_view);
        PreviewChanged?.Invoke(_view);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsDragging || e.Pointer != _pointer) return;
        Move(e.GetPosition(this));
        var samples = _drag == Handle.Samples;
        var changed = samples ? !_samples.AsSpan().SequenceEqual(_beforeSamples) :
            !InstrumentFile.Write(_view!).AsSpan().SequenceEqual(InstrumentFile.Write(_before!));
        _drag = Handle.None; _before = null; _pointer = null; e.Pointer.Capture(null); e.Handled = true;
        if (changed)
        {
            if (samples) DrawCompleted?.Invoke((short[])_samples.Clone()); else ShapeCompleted?.Invoke(_view!);
        }
        InvalidateVisual();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); CancelGesture(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { CancelGesture(); base.OnDetachedFromVisualTree(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Editable) return;
        if (e.Key == Key.Escape) { CancelGesture(); e.Handled = true; return; }
        if (IsDragging) return;
        if (PointMode && e.Key is Key.Left or Key.Right)
        {
            _selected = Math.Clamp(_selected + (e.Key == Key.Left ? -1 : 1), 0, _samples.Length - 1);
            InvalidateVisual(); e.Handled = true;
        }
        if (e.Key is not (Key.Up or Key.Down)) return;
        var sign = e.Key == Key.Up ? 1 : -1;
        if (PointMode)
        {
            var old = _samples[_selected];
            var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 4096 : 512;
            _samples[_selected] = (short)Math.Clamp(old + sign * delta, short.MinValue, short.MaxValue);
            if (_samples[_selected] != old) DrawCompleted?.Invoke((short[])_samples.Clone());
        }
        else
        {
            var old = _view!.OscillatorAmplitude;
            _view.OscillatorAmplitude = Math.Clamp(old + sign * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? .1 : .01), 0, 1);
            if (old != _view.OscillatorAmplitude) ShapeCompleted?.Invoke(_view);
        }
        InvalidateVisual(); e.Handled = true;
    }
    private (int Index, short Value) SampleAt(Point p)
    {
        var index = (int)Math.Clamp(Math.Round((p.X - Plot.X) / Plot.Width * _samples.Length), 0, _samples.Length - 1);
        // At zero amplitude, retain an editable underlying shape while the output stays silent.
        var amplitude = _view!.OscillatorAmplitude == 0 ? 1 : _view.OscillatorAmplitude;
        var value = (short)Math.Clamp(Math.Round(Y(p) / amplitude * 32768), short.MinValue, short.MaxValue);
        return (index, value);
    }
}
