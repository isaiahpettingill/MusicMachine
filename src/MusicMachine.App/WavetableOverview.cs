using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MusicMachine.Core;

namespace MusicMachine.App;

public sealed class WavetableOverview : Control
{
    private Instrument? instrument;
    private int selected;
    public event Action<int>? FrameSelected;
    public WavetableOverview()
    {
        Name = "WavetableOverview"; Height = 260;
        Avalonia.Automation.AutomationProperties.SetName(this, "All wavetable frames and morphed output");
        ToolTip.SetTip(this, "All frames are stacked above. Click a frame to edit it. The lower waveform shows the output at the current morph position.");
    }
    public void Show(Instrument value, int frame) { instrument = value; selected = frame; InvalidateVisual(); }
    private Rect FramePlot => new(12, 30, Math.Max(1, Bounds.Width - 24), 122);
    private Point FramePoint(int frame, double phase)
    {
        var plot = FramePlot; var count = instrument!.Wavetable.Count;
        var offset = count <= 1 ? 0 : frame * 50d / (count - 1);
        return new(plot.X + phase * plot.Width, plot.Y + 36 + offset - WaveformShape.Sample(instrument.Wavetable[frame], phase) * 30);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Ui.Workspace, new Pen(Ui.Line, .6), new Rect(Bounds.Size), 4, 4);
        if (instrument is null || instrument.Wavetable.Count == 0) return;
        using var clip = context.PushClip(new Rect(Bounds.Size));
        context.DrawText(Ui.Fmt($"All {instrument.Wavetable.Count} frames · editing {selected + 1:00}", 12, Ui.Text), new Point(10, 8));
        void Curve(Func<double, Point> point, IBrush brush, double width)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(point(0), false);
                for (var n = 1; n <= 192; n++) path.LineTo(point(n / 192d));
                path.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(brush, width), geometry);
        }
        using (context.PushOpacity(.5))
            for (var f = 0; f < instrument.Wavetable.Count; f++)
                if (f != selected) { var index = f; Curve(p => FramePoint(index, p), Ui.Muted, 1); }
        Curve(p => FramePoint(selected, p), Ui.Accent, 2);
        context.DrawLine(new Pen(Ui.Line, .6), new Point(10, 157), new Point(Bounds.Width - 10, 157));
        context.DrawText(Ui.Fmt($"Morph output · {instrument.WavetablePosition:P0}", 12, Ui.Text), new Point(10, 165));
        var samples = WaveformShape.Cycle(instrument);
        var center = 222d;
        context.DrawLine(new Pen(Ui.Line, .5), new Point(12, center), new Point(Bounds.Width - 12, center));
        Curve(p => new Point(12 + p * FramePlot.Width, center - WaveformShape.Sample(samples, p) * 26), Ui.Success, 2);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (instrument is null || instrument.Wavetable.Count == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this); if (!FramePlot.Contains(p)) return;
        var phase = Math.Clamp((p.X - FramePlot.X) / FramePlot.Width, 0, 1);
        selected = Enumerable.Range(0, instrument.Wavetable.Count).MinBy(f => Math.Abs(FramePoint(f, phase).Y - p.Y));
        FrameSelected?.Invoke(selected); InvalidateVisual(); e.Handled = true;
    }
}
