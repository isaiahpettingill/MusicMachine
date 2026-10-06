using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class EditorFieldFocusRegressionChecks
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "musicmachine-field-focus-" + Guid.NewGuid().ToString("N"));
        var oldData = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA");
        var oldLoad = EditorPlatform.LoadPreferences; var oldSave = EditorPlatform.SavePreferences; var oldStorage = EditorPlatform.ProjectStorage;
        Window? window = null;
        try
        {
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", directory);
            EditorPlatform.LoadPreferences = () => "workspace=Tracker\nlibrary=false\ninspector=true\ninspectorContent=instrument\ncheckForUpdates=false\n";
            EditorPlatform.SavePreferences = _ => { }; EditorPlatform.ProjectStorage = null;
            var view = new MainView(); window = new Window { Width = 1180, Height = 812, Content = view }; window.Show(); Pump(window);
            Invoke(view, "AddInstrument"); Pump(window);
            var editor = Field<SongEditor>(view, "editor"); var tracker = Field<TrackerGrid>(view, "tracker");
            // Envelope dragging previews locally, commits once on release, and supports undo/cancel.
            var envelopeGraph = Named<Control>(view, "AmplitudeEnvelope");
            envelopeGraph.BringIntoView(); Pump(window);
            var envelopePoints = (Point[])envelopeGraph.GetType().GetMethod("Points", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(envelopeGraph, null)!;
            var envelopePoint = envelopeGraph.TranslatePoint(envelopePoints[1], window)!.Value;
            var originalAttack = editor.Song.Instruments[0].Amplitude.AttackMs;
            var envelopeRevision = editor.Revision;
            window.MouseDown(envelopePoint, MouseButton.Left);
            window.MouseMove(envelopePoint + new Vector(45, 0)); Pump(window);
            Assert.Equal(envelopeRevision, editor.Revision);
            window.MouseUp(envelopePoint + new Vector(45, 0), MouseButton.Left); Pump(window);
            Assert.Equal(envelopeRevision + 1, editor.Revision);
            Assert.True(editor.Song.Instruments[0].Amplitude.AttackMs > originalAttack);
            Invoke(view, "Undo"); Pump(window);
            Assert.Equal(originalAttack, editor.Song.Instruments[0].Amplitude.AttackMs);
            envelopeRevision = editor.Revision;
            window.MouseDown(envelopePoint, MouseButton.Left); window.MouseMove(envelopePoint + new Vector(30, 0));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(envelopePoint + new Vector(30, 0), MouseButton.Left); Pump(window);
            Assert.Equal(envelopeRevision, editor.Revision); Assert.Equal(originalAttack, editor.Song.Instruments[0].Amplitude.AttackMs);
            var instrument = Field<InstrumentPanel>(view, "instrumentPanel"); var workspace = Field<ContentControl>(view, "workArea").Content;
            var amplitude = Named<NumericUpDown>(view, "OscillatorAmplitude");
            var text = amplitude.GetVisualDescendants().OfType<TextBox>().Single(); text.Focus(); text.SelectAll();
            var before = SongFile.Write(editor.Song); var revision = editor.Revision;
            foreach (var ch in "0.5") { window.KeyTextInput(ch.ToString()); Pump(window); Assert.Same(text, window.FocusManager!.GetFocusedElement()); }
            Assert.Equal(revision, editor.Revision); Assert.Equal("0.5", text.Text); Assert.False(tracker.HasPendingEdit);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump(window);
            Assert.Equal(revision + 1, editor.Revision); Assert.Equal(.5, editor.Song.FindInstrument(Field<string>(view, "selectedInstrument"))!.OscillatorAmplitude);
            Assert.Same(text, window.FocusManager!.GetFocusedElement()); Assert.Same(workspace, Field<ContentControl>(view, "workArea").Content);
            Assert.NotNull(instrument.GetVisualParent()); Assert.True(instrument.IsEffectivelyVisible);
            Invoke(view, "Undo"); Pump(window); Assert.Equal(before, SongFile.Write(editor.Song));
            Invoke(view, "Redo"); Pump(window); Assert.Equal(.5m, amplitude.Value);
            // Invalid text is never mistaken for the last successfully parsed prefix.
            text.Focus(); text.SelectAll(); window.KeyTextInput("0.2oops"); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump(window);
            Assert.Equal("0.2oops", text.Text); Assert.Equal(.5, editor.Song.FindInstrument(Field<string>(view, "selectedInstrument"))!.OscillatorAmplitude);
            Invoke(view, "Refresh", false); Pump(window); Assert.Equal("0.2oops", text.Text);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(window); Assert.Equal(.5m, amplitude.Value);
            // Leaving a valid field applies exactly once and honors the new focus.
            text.SelectAll(); window.KeyTextInput("0.75"); var beforeBlur = editor.Revision;
            tracker.Focus(); Pump(window);
            Assert.Equal(beforeBlur + 1, editor.Revision); Assert.True(tracker.IsFocused);
            Assert.Equal(.75, editor.Song.FindInstrument(Field<string>(view, "selectedInstrument"))!.OscillatorAmplitude);
            // A rejected blur retains the draft and never applies the valid prefix.
            text.Focus(); text.SelectAll(); window.KeyTextInput("0.3oops"); tracker.Focus(); Pump(window);
            Assert.Equal("0.3oops", text.Text); Assert.Equal(beforeBlur + 1, editor.Revision);
            Invoke(view, "Refresh", false); Pump(window); Assert.Equal("0.3oops", text.Text);
            text.Focus(); window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(window);
            // Values such as 20% cannot be applied character by character (the first
            // character is below the minimum of a 20–400 BPM control).
            var tempo = Field<NumericUpDown>(view, "tempo"); var tempoText = tempo.GetVisualDescendants().OfType<TextBox>().Single();
            tempoText.Focus(); tempoText.SelectAll(); var tempoRevision = editor.Revision;
            foreach (var ch in "240") { window.KeyTextInput(ch.ToString()); Pump(window); Assert.Same(tempoText, window.FocusManager!.GetFocusedElement()); }
            Assert.Equal(tempoRevision, editor.Revision);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump(window);
            Assert.Equal(240, editor.Song.Bpm); Assert.Equal(tempoRevision + 1, editor.Revision);
            Assert.Same(tempoText, window.FocusManager!.GetFocusedElement());
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null); Pump(window);
            Assert.Equal(241, editor.Song.Bpm); Assert.Equal(tempoRevision + 2, editor.Revision);
            foreach (var (shape, field, draft, expected) in new[] {
                (Waveform.Triangle, "TrianglePeak", "23", .23),
                (Waveform.Square, "SquareWidth", "37", .37),
                (Waveform.Pulse, "PulseWidth", "42", .42) })
            {
                Named<ComboBox>(view, "WaveformSelector").SelectedIndex = (int)shape; Pump(window);
                var number = Named<NumericUpDown>(view, field); var box = number.GetVisualDescendants().OfType<TextBox>().Single();
                box.Focus(); box.SelectAll(); var prior = editor.Revision;
                foreach (var ch in draft) { window.KeyTextInput(ch.ToString()); Pump(window); Assert.Same(box, window.FocusManager!.GetFocusedElement()); }
                Assert.Equal(prior, editor.Revision); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump(window);
                Assert.Equal(prior + 1, editor.Revision); Assert.Same(box, window.FocusManager!.GetFocusedElement());
                var current = editor.Song.FindInstrument(Field<string>(view, "selectedInstrument"))!;
                Assert.Equal(expected, shape == Waveform.Triangle ? current.TrianglePeak : shape == Waveform.Square ? current.SquareWidth : current.PulseWidth);
            }
            // Arrangement is snapshot-built: committing a repeat restores the same
            // field and caret, allowing a second edit without an extra click.
            Invoke(view, "OpenSongArrangement"); Pump(window);
            var repeats = Named<NumericUpDown>(view, "ArrangementRepeats"); var repeatText = repeats.GetVisualDescendants().OfType<TextBox>().Single();
            repeatText.Focus(); repeatText.SelectAll(); var repeatBefore = SongFile.Write(editor.Song); var repeatRevision = editor.Revision;
            foreach (var ch in "12") { window.KeyTextInput(ch.ToString()); Pump(window); Assert.Same(repeatText, window.FocusManager!.GetFocusedElement()); }
            Assert.Equal(repeatRevision, editor.Revision); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump(window);
            Assert.Equal(12, editor.Song.Arrangement[0].Repeats); Assert.Equal(repeatRevision + 1, editor.Revision);
            repeatText = Named<NumericUpDown>(view, "ArrangementRepeats").GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Same(repeatText, window.FocusManager!.GetFocusedElement()); Assert.Equal(2, repeatText.CaretIndex);
            Invoke(view, "Undo"); Pump(window); Assert.Equal(repeatBefore, SongFile.Write(editor.Song));
            Assert.Equal("1", Named<NumericUpDown>(view, "ArrangementRepeats").Text);
            // Blur may trigger a refresh while focus is moving to another control.
            repeatText = Named<NumericUpDown>(view, "ArrangementRepeats").GetVisualDescendants().OfType<TextBox>().Single();
            repeatText.Focus(); repeatText.SelectAll(); window.KeyTextInput("3");
            var play = Named<Button>(view, "ArrangementPlaySong"); play.Focus(); Pump(window);
            Assert.Equal(3, editor.Song.Arrangement[0].Repeats);
            repeatText = Named<NumericUpDown>(view, "ArrangementRepeats").GetVisualDescendants().OfType<TextBox>().Single();
            repeatText.Focus(); repeatText.SelectAll(); window.KeyTextInput("4");
            var append = Named<Button>(view, "ArrangementAppend"); var location = append.TranslatePoint(new Point(append.Bounds.Width / 2, append.Bounds.Height / 2), window)!.Value;
            var countBefore = editor.Song.Arrangement.Count;
            window.MouseDown(location, MouseButton.Left); Pump(window); window.MouseUp(location, MouseButton.Left); Pump(window);
            Assert.Equal(4, editor.Song.Arrangement[0].Repeats); Assert.Equal(countBefore + 1, editor.Song.Arrangement.Count);
            Invoke(view, "Undo"); Pump(window); Assert.Equal(countBefore, editor.Song.Arrangement.Count); Assert.Equal(4, editor.Song.Arrangement[0].Repeats);
            Invoke(view, "Undo"); Pump(window); Assert.Equal(3, editor.Song.Arrangement[0].Repeats);
            // Releasing beyond the view and cancelling the window interaction cannot
            // leave later refreshes permanently queued.
            window.MouseDown(new Point(600, 740), MouseButton.Left);
            window.MouseUp(new Point(-20, -20), MouseButton.Left); Pump(window);
            Assert.False(Field<bool>(view, "pointerInteraction"));
            window.MouseDown(new Point(600, 740), MouseButton.Left);
            Invoke(view, "PointerWindowDeactivated", view, EventArgs.Empty); Pump(window);
            window.MouseUp(new Point(-20, -20), MouseButton.Left); Pump(window);
            Assert.False(Field<bool>(view, "pointerInteraction"));
            Invoke(view, "SelectWorkspace", "Tracker"); Pump(window);
            // Ctrl+S to an existing native path must include the focused numeric
            // draft even though no file picker or explicit blur is needed.
            Directory.CreateDirectory(directory);
            var savedPath = Path.Combine(directory, "focused-number.song"); SongFile.Save(savedPath, editor.Song);
            typeof(MainView).GetField("filePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, savedPath);
            tempoText.Focus(); tempoText.SelectAll(); window.KeyTextInput("187");
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            for (var attempt = 0; attempt < 1000 && (!File.Exists(savedPath) || Field<bool>(view, "saveBusy")); attempt++) { Pump(window); Thread.Sleep(1); }
            Assert.Equal(187, SongFile.Load(savedPath).Bpm); Assert.False(editor.IsDirty);
            tempoText.Focus(); tempoText.SelectAll(); window.KeyTextInput("188oops");
            var blocked = (Task<bool>)typeof(MainView).GetMethod("SaveSong", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object[] { false })!;
            Assert.True(blocked.IsCompleted); Assert.False(blocked.Result); Assert.Equal(187, SongFile.Load(savedPath).Bpm);
            var closeBlocked = view.RequestCloseAsync(); Assert.True(closeBlocked.IsCompleted); Assert.False(closeBlocked.Result);
            tempoText.Focus(); window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(window);
            Invoke(view, "ApplyDrumPreset", false); Invoke(view, "SelectWorkspace", "Drums"); Pump(window);
            var drumGrid = Field<Control>(view, "drumGridSurface");
            var drumPoint = drumGrid.TranslatePoint(new Point(6, 50), window)!.Value;
            window.MouseDown(drumPoint, MouseButton.Left); Pump(window); window.MouseUp(drumPoint, MouseButton.Left); Pump(window);
            Assert.Same(Field<Control>(view, "drumGridSurface"), window.FocusManager!.GetFocusedElement());
            var priorStep = Field<int>(view, "selectedDrumStep");
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null); Pump(window);
            Assert.Equal(priorStep + 1, Field<int>(view, "selectedDrumStep"));
            Invoke(view, "SelectWorkspace", "Tracker"); Pump(window);
            for (var n = 0; n < 4; n++)
            {
                Invoke(view, "ShowFxReference"); Pump(window);
                Named<ComboBox>(view, "InspectorContentPicker").SelectedIndex = 0; Pump(window);
                Invoke(view, "AddEffectColumn"); Pump(window);
                Invoke(view, "AddTrack"); Pump(window);
                Invoke(view, "SelectWorkspace", "Instrument"); Pump(window);
                Invoke(view, "Refresh", false); Pump(window);
                Invoke(view, "SelectWorkspace", "Tracker"); Pump(window);
                Assert.True(instrument.IsEffectivelyVisible); Assert.NotNull(instrument.GetVisualParent());
            }
        }
        finally
        {
            if (window is not null) { window.Content = null; window.Close(); }
            EditorPlatform.LoadPreferences = oldLoad; EditorPlatform.SavePreferences = oldSave; EditorPlatform.ProjectStorage = oldStorage;
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", oldData); if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static T Named<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Pump(Window window) { for (var n = 0; n < 4; n++) Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
