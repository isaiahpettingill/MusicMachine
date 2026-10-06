using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Automation;
using IconPacks.Avalonia.Material;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MusicMachine.App;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Tests;

// Called inside the established headless editor test, on the same Avalonia UI thread.
internal static class SamplingControlTests
{
    internal static void Run()
    {
        var editor = new SongEditor(TestSong.CreateEmpty()); var original = SongFile.Write(editor.Song);
        var source = Enumerable.Range(0, 4800).Select(i => (float)(.6 * Math.Sin(2 * Math.PI * i / 128) + .12 * Math.Sin(4 * Math.PI * i / 128) + .1)).ToArray();
        var untouched = (float[])source.Clone(); int applies = 0, auditions = 0;
        var panel = new SamplingPanel(_ => Task.FromResult<SampleClip?>(null), (wave, name, mode) =>
        {
            applies++;
            editor.Change(s =>
            {
                if (mode == SamplingApplyMode.NewInstrument) s.Instruments.Add(new Instrument { Name = name, Waveform = Waveform.Custom, CustomWave = wave });
                else if (mode == SamplingApplyMode.ReplaceWave) { s.Instruments[0].Waveform = Waveform.Custom; s.Instruments[0].CustomWave = wave; }
                else { s.Instruments[0].Waveform = Waveform.Wavetable; s.Instruments[0].Wavetable.Add(wave); }
            });
            return "Applied";
        }, wave => { auditions++; Assert.Equal(128, wave.Length); }, () => { });
        T Named<T>(string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        void Click(string name) => Named<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(Named<Button>("SamplingApply").IsEnabled);
        Assert.False(Named<Expander>("SamplingManualControls").IsExpanded);
        foreach (var name in new[] { "SamplingImport", "SamplingDetect", "SamplingExtract", "SamplingCancel", "SamplingReset", "SamplingApply" })
        {
            if (name == "SamplingDetect") Assert.Equal("Find cycle", Named<Button>(name).Content);
            else if (name == "SamplingImport") Assert.Equal("Choose audio…", Named<Button>(name).Content);
            else if (name == "SamplingExtract") Assert.Equal("Extract manual cycle", Named<Button>(name).Content);
            else if (name == "SamplingApply") Assert.Equal("Create instrument", Named<Button>(name).Content);
            else Assert.IsType<PackIconMaterial>(Named<Button>(name).Content);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(Named<Button>(name))));
            Assert.NotNull(ToolTip.GetTip(Named<Button>(name)));
        }
        panel.LoadClip(new SampleClip(source, 48000, 1, "test.wav"));
        Assert.True(panel.HasSource); Assert.False(editor.IsDirty); Assert.Equal(original, SongFile.Write(editor.Song));
        Assert.False(Named<Button>("SamplingApply").IsEnabled);
        Click("SamplingExtract"); Assert.Equal(128, panel.ExtractedWave.Length); Assert.True(Named<Button>("SamplingApply").IsEnabled);
        var unshaped = panel.ExtractedWave; Named<Slider>("SamplingDrive").Value = .7; Named<Slider>("SamplingBlend").Value = .25;
        Assert.False(unshaped.SequenceEqual(panel.ExtractedWave)); Assert.Equal(untouched, source); Assert.False(editor.IsDirty);
        Click("SamplingReset"); Assert.Equal(unshaped, panel.ExtractedWave);
        Click("SamplingAudition"); Assert.Equal(1, auditions); Assert.False(editor.IsDirty);
        Click("SamplingApply"); Assert.Equal(1, applies); Assert.True(editor.IsDirty); Assert.True(editor.CanUndo);
        Assert.Equal(unshaped, editor.Song.Instruments[^1].CustomWave);
        var instrumentBytes = InstrumentFile.Write(editor.Song.Instruments[^1]); Assert.True(instrumentBytes.Length < 2048);
        Assert.True(editor.Undo()); Assert.Equal(original, SongFile.Write(editor.Song)); Assert.True(editor.Redo());
        var afterApply = SongFile.Write(editor.Song);
        Named<ComboBox>("SamplingApplyMode").SelectedIndex = 2;
        Assert.Equal("Append a frame to the selected wavetable", AutomationProperties.GetName(Named<Button>("SamplingApply")));
        Assert.Equal("Add frame to selected instrument", Named<Button>("SamplingApply").Content);
        Click("SamplingApply");
        Assert.Equal(Waveform.Wavetable, editor.Song.Instruments[0].Waveform); Assert.Single(editor.Song.Instruments[0].Wavetable);
        Assert.True(editor.Undo()); Assert.Equal(afterApply, SongFile.Write(editor.Song));
        // Region changes invalidate the candidate, requiring an intentional fresh extraction.
        panel.SetRegion(1000, 1300); Assert.Empty(panel.ExtractedWave); Assert.False(Named<Button>("SamplingApply").IsEnabled);
        Named<NumericUpDown>("SamplingCycleStart").Value = 1290;
        Click("SamplingExtract"); Assert.Empty(panel.ExtractedWave); Assert.Equal(2, applies);
        Named<NumericUpDown>("SamplingCycleStart").Value = 1000; Click("SamplingExtract"); Assert.Equal(128, panel.ExtractedWave.Length);
        Named<NumericUpDown>("SamplingPeriod").Value = 64; Assert.Empty(panel.ExtractedWave);
        Click("SamplingExtract"); Assert.Equal(128, panel.ExtractedWave.Length);
        panel.SetRegion(0, 4800); Click("SamplingExtract");
        var beforeClear = SongFile.Write(editor.Song); Click("SamplingClear");
        Assert.False(panel.HasSource); Assert.Empty(panel.ExtractedWave); Assert.Equal(beforeClear, SongFile.Write(editor.Song));
        Assert.False(Named<Button>("SamplingApply").IsEnabled);
        // Silent clips report an unusable wave and cannot accidentally create silent instruments.
        panel.LoadClip(new SampleClip(new float[1024], 48000, 1, "silence.wav")); Click("SamplingExtract");
        Assert.False(Named<Button>("SamplingApply").IsEnabled);
        // Failure and dismissal complete without destroying the prior candidate.
        var cancellation = new SamplingPanel(_ => Task.FromResult<SampleClip?>(null), (_, _, _) => "", _ => { }, () => { });
        cancellation.LoadClip(new SampleClip(source, 48000, 1, "retained.wav"));
        Assert.True(cancellation.ImportAsync().IsCompletedSuccessfully); Assert.True(cancellation.HasSource); Assert.False(cancellation.IsBusy);
        var failure = new SamplingPanel(_ => Task.FromException<SampleClip?>(new InvalidDataException("Rejected test input")), (_, _, _) => "", _ => { }, () => { });
        failure.LoadClip(new SampleClip(source, 48000, 1, "retained.wav"));
        Assert.True(failure.ImportAsync().IsCompletedSuccessfully); Assert.True(failure.HasSource); Assert.False(failure.IsBusy);
        var tiny = new SamplingPanel(_ => Task.FromResult<SampleClip?>(null), (_, _, _) => "", _ => { }, () => { });
        tiny.LoadClip(new SampleClip(new float[] { 0, .5f, -.5f }, 48000, 1, "tiny.wav"));
        tiny.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SamplingExtract").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(128, tiny.ExtractedWave.Length);
        Assert.Equal(untouched, source);
        // Repeated import clicks cannot start parallel decoders; a late result after Clear is ignored.
        int importCalls = 0;
        var pending = new TaskCompletionSource<SampleClip?>();
        var delayed = new SamplingPanel(_ => { importCalls++; return pending.Task; }, (_, _, _) => "", _ => { }, () => { });
        var firstImport = delayed.ImportAsync(); Assert.True(delayed.IsBusy);
        Assert.True(delayed.ImportAsync().IsCompletedSuccessfully); Assert.Equal(1, importCalls);
        delayed.Clear(); pending.SetResult(new SampleClip(source, 48000, 1, "late.wav"));
        Assert.True(SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return firstImport.IsCompleted; }, 1000));
        firstImport.GetAwaiter().GetResult(); Assert.False(delayed.HasSource); Assert.False(delayed.IsBusy);
        // Center pane at a minimum-size desktop with both side panes visible remains horizontally bounded.
        // The normal center size fits the collapsed workflow without either scrollbar.
        var layoutWindow = new Window { Width = 392, Height = 510, Content = panel };
        layoutWindow.Show(); layoutWindow.UpdateLayout();
        try
        {
            var scroll = (ScrollViewer)panel.Content!;
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"Small Sampling pane overflowed horizontally: {scroll.Extent.Width}/{scroll.Viewport.Width}");
            layoutWindow.Width = 992; layoutWindow.Height = 730; layoutWindow.UpdateLayout();
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1, $"Normal Sampling pane required scrolling: {scroll.Extent.Height}/{scroll.Viewport.Height}");
        }
        finally { layoutWindow.Close(); }
        // Real routed pointer events exercise selection commit, cancellation, and repeat gestures.
        var canvas = new SampleWaveCanvas { Width = 500, Height = 140, Selectable = true };
        canvas.SetSamples(source); canvas.SetSelection(0, source.Length);
        int selections = 0; int selectedStart = 0, selectedEnd = 0;
        canvas.RegionChanged += (start, end) => { selections++; selectedStart = start; selectedEnd = end; };
        var window = new Window { Width = 500, Height = 140, Content = canvas };
        window.Show(); window.UpdateLayout();
        try
        {
            window.MouseDown(new Point(30, 50), MouseButton.Left);
            window.MouseMove(new Point(350, 50)); window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(new Point(350, 50), MouseButton.Left); Assert.Equal(0, selections);
            window.MouseDown(new Point(30, 50), MouseButton.Left); window.MouseMove(new Point(250, 50));
            window.MouseUp(new Point(250, 50), MouseButton.Left); Assert.Equal(1, selections);
            Assert.InRange(selectedStart, 0, 4797); Assert.InRange(selectedEnd, selectedStart + 3, 4800);
            canvas.Zoom(2); canvas.Fit();
            Assert.Equal(1, selections); // Zooming never changes the selected region.
            // A backwards drag also normalizes to a nonempty ascending region.
            window.MouseDown(new Point(400, 50), MouseButton.Left); window.MouseMove(new Point(100, 50));
            window.MouseUp(new Point(100, 50), MouseButton.Left); Assert.Equal(2, selections); Assert.True(selectedStart < selectedEnd);
        }
        finally { window.Close(); }
    }
}
