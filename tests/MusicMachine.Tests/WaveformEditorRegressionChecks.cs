using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class WaveformEditorRegressionChecks
{
    internal static void Run()
    {
        var editor = new SongEditor(TestSong.CreateEmpty()); var id = editor.Song.Instruments[0].Id;
        Instrument Instrument() => editor.Song.FindInstrument(id)!;
        InstrumentPanel? panel = null;
        panel = new InstrumentPanel(change => { editor.Change(change); panel!.ShowInstrument(editor.Song, id); }, () => { }, () => { }, () => { });
        T Named<T>(string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        void Show() => panel.ShowInstrument(editor.Song, id);
        void SetShape(Waveform shape) { Named<ComboBox>("WaveformSelector").SelectedIndex = (int)shape; }
        Show();
        Named<NumericUpDown>("OscillatorAmplitude").Value = .31m;
        Assert.Equal(.31, Instrument().OscillatorAmplitude);
        var output = Instrument().VolumeDb; var envelope = Instrument().Amplitude.Sustain;
        foreach (var shape in Enum.GetValues<Waveform>())
        {
            SetShape(shape); Assert.Equal(.31, Instrument().OscillatorAmplitude);
            Assert.Equal(output, Instrument().VolumeDb); Assert.Equal(envelope, Instrument().Amplitude.Sustain);
            if (shape is not (Waveform.Custom or Waveform.Wavetable))
            {
                var before = SongFile.Write(editor.Song); var expected = WaveformShape.Cycle(Instrument());
                Named<Button>("EditWaveformPoints").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Waveform.Custom, Instrument().Waveform); Assert.Equal(expected, Instrument().CustomWave);
                Assert.Equal(.31, Instrument().OscillatorAmplitude);
                Assert.True(editor.Undo()); Assert.Equal(before, SongFile.Write(editor.Song)); Show();
            }
        }
        SetShape(Waveform.Triangle); Named<NumericUpDown>("TrianglePeak").Value = 23;
        Assert.Equal(.23, Instrument().TrianglePeak);
        SetShape(Waveform.Square); Named<NumericUpDown>("SquareWidth").Value = 37;
        Named<NumericUpDown>("WaveHigh").Value = .4m; Named<NumericUpDown>("WaveLow").Value = -.2m;
        Assert.Equal(.37, Instrument().SquareWidth); Assert.Equal(.4, Instrument().WaveHigh); Assert.Equal(-.2, Instrument().WaveLow);

        var window = new Window { Width = 500, Height = 640, Content = panel };
        window.Show(); window.UpdateLayout();
        try
        {
            SetShape(Waveform.Triangle); Named<NumericUpDown>("OscillatorAmplitude").Value = 1;
            Named<NumericUpDown>("TrianglePeak").Value = 50;
            window.UpdateLayout(); var wave = Named<WaveformDisplay>("WaveformEditor");
            Point P(double x, double value) => wave.TranslatePoint(new Point(8 + (wave.Bounds.Width - 38) * x,
                10 + (wave.Bounds.Height - 20) / 2 - value * (wave.Bounds.Height - 20) * .47), window)!.Value;
            var before = SongFile.Write(editor.Song); var revision = editor.Revision;
            window.MouseDown(P(.5, 1), MouseButton.Left); window.MouseMove(P(.3, .4));
            Assert.True(wave.IsDragging); Assert.InRange(wave.PreviewAmplitude, .399, .401);
            Assert.InRange((double)Named<NumericUpDown>("OscillatorAmplitude").Value!, .399, .401);
            Assert.Equal(revision, editor.Revision); Assert.Equal(before, SongFile.Write(editor.Song));
            window.MouseUp(P(.3, .4), MouseButton.Left);
            Assert.False(wave.IsDragging); Assert.Equal(revision + 1, editor.Revision);
            Assert.InRange(Instrument().TrianglePeak, .299, .301); Assert.InRange(Instrument().OscillatorAmplitude, .399, .401);
            Assert.True(editor.Undo()); Assert.Equal(before, SongFile.Write(editor.Song)); Show();
            // Escape and capture loss both cancel; a release after cancellation is not a commit.
            window.MouseDown(P(.5, 1), MouseButton.Left); window.MouseMove(P(.2, .2));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(P(.2, .2), MouseButton.Left);
            Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(1m, Named<NumericUpDown>("OscillatorAmplitude").Value);
            window.MouseDown(P(.5, 1), MouseButton.Left); window.MouseMove(P(.7, .2));
            wave.RaiseEvent(new PointerCaptureLostEventArgs(wave, new Pointer(400, PointerType.Mouse, true)));
            window.MouseUp(P(.7, .2), MouseButton.Left); Assert.Equal(before, SongFile.Write(editor.Song));
            // Pointer capture clamps extreme drags, rather than overflowing samples or numeric controls.
            window.MouseDown(P(.5, 1), MouseButton.Left); window.MouseMove(P(4, -3)); window.MouseUp(P(4, -3), MouseButton.Left);
            Assert.Equal(.99, Instrument().TrianglePeak); Assert.Equal(0, Instrument().OscillatorAmplitude);
            Assert.True(editor.Undo()); Show();
            // The amplitude rail remains reachable at zero, independent of point mode or preset.
            Point Rail(double value) => wave.TranslatePoint(new Point(wave.Bounds.Width - 13,
                10 + (wave.Bounds.Height - 20) / 2 - value * (wave.Bounds.Height - 20) * .47), window)!.Value;
            Named<NumericUpDown>("OscillatorAmplitude").Value = 0; revision = editor.Revision;
            window.MouseDown(Rail(0), MouseButton.Left); window.MouseMove(Rail(.25)); window.MouseUp(Rail(.25), MouseButton.Left);
            Assert.Equal(revision + 1, editor.Revision); Assert.InRange(Instrument().OscillatorAmplitude, .249, .251);
            foreach (var pulse in new[] { Waveform.Square, Waveform.Pulse })
            {
                SetShape(pulse); Named<NumericUpDown>("OscillatorAmplitude").Value = .5m;
                Named<NumericUpDown>(pulse == Waveform.Square ? "SquareWidth" : "PulseWidth").Value = 50;
                Named<NumericUpDown>("WaveHigh").Value = 1; Named<NumericUpDown>("WaveLow").Value = -1;
                window.UpdateLayout(); before = SongFile.Write(editor.Song); revision = editor.Revision;
                window.MouseDown(P(.5, 0), MouseButton.Left); window.MouseMove(P(.25, 0));
                Assert.Equal(revision, editor.Revision); window.MouseUp(P(.25, 0), MouseButton.Left);
                Assert.Equal(revision + 1, editor.Revision);
                Assert.InRange(pulse == Waveform.Square ? Instrument().SquareWidth : Instrument().PulseWidth, .249, .251);
                Assert.True(editor.Undo()); Assert.Equal(before, SongFile.Write(editor.Song)); Show();
                window.MouseDown(P(.25, .5), MouseButton.Left); window.MouseMove(P(.25, .2)); window.MouseUp(P(.25, .2), MouseButton.Left);
                Assert.InRange(Instrument().WaveHigh, .399, .401); Assert.Equal(.5, Instrument().OscillatorAmplitude);
                window.MouseDown(P(.75, -.5), MouseButton.Left); window.MouseMove(P(.75, -.1)); window.MouseUp(P(.75, -.1), MouseButton.Left);
                Assert.InRange(Instrument().WaveLow, -.201, -.199); Assert.Equal(.5, Instrument().OscillatorAmplitude);
            }
            // Frame drawing updates only the selected frame, keeps its existing resolution, and is one undo.
            editor.Change(s => { var i = s.FindInstrument(id)!; i.Waveform = Waveform.Wavetable; i.OscillatorAmplitude = .5;
                i.Wavetable = [[1000, -1000, 1000, -1000], [2000, -2000, 2000, -2000, 2000, -2000, 2000, -2000]]; }); Show();
            window.UpdateLayout();
            var overview = Named<WavetableOverview>("WavetableOverview");
            Assert.True(overview.IsVisible); Assert.True(overview.Bounds.Height >= 240);
            overview.BringIntoView(); window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var overviewPoint = overview.TranslatePoint(new Point(12, 116 - 2000 / 32768d * 30), window)!.Value;
            var overviewRevision = editor.Revision;
            window.MouseDown(overviewPoint, MouseButton.Left); window.MouseUp(overviewPoint, MouseButton.Left);
            Assert.Equal(1, Named<ComboBox>("WavetableFrameSelector").SelectedIndex);
            Assert.Equal(overviewRevision, editor.Revision);
            Named<WaveformDisplay>("WaveformEditor").BringIntoView(); window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Named<ComboBox>("WavetableFrameSelector").SelectedIndex = 1;
            before = SongFile.Write(editor.Song); revision = editor.Revision;
            window.MouseDown(P(.1, .1), MouseButton.Left); window.MouseMove(P(.8, -.2));
            Assert.Equal(before, SongFile.Write(editor.Song));
            window.MouseUp(P(.8, -.2), MouseButton.Left); Assert.Equal(revision + 1, editor.Revision);
            Assert.Equal(new short[] { 1000, -1000, 1000, -1000 }, Instrument().Wavetable[0]);
            Assert.Equal(8, Instrument().Wavetable[1].Length); Assert.Equal(.5, Instrument().OscillatorAmplitude);
            Assert.True(editor.Undo()); Assert.Equal(before, SongFile.Write(editor.Song)); Show();
            // Switching model snapshots while drawing cannot apply the obsolete gesture later.
            window.MouseDown(P(.2, .1), MouseButton.Left); window.MouseMove(P(.5, .2));
            editor.Change(s => s.FindInstrument(id)!.Name = "New snapshot"); Show(); revision = editor.Revision;
            window.MouseUp(P(.5, .2), MouseButton.Left); Assert.Equal(revision, editor.Revision);
            // Compact inspector stays horizontally bounded with every conditional field set.
            window.Width = 266; window.UpdateLayout();
            var scroll = (ScrollViewer)panel.Content!;
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        }
        finally { window.Close(); }
    }
}
