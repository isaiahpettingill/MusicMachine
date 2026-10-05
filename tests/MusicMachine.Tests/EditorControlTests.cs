using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MusicMachine.App;
using MusicMachine.Core;
namespace MusicMachine.Tests;

[CollectionDefinition("Editor controls", DisableParallelization = true)]
public sealed class EditorControlCollection { }

[Collection("Editor controls")]
public sealed class EditorControlTests
{
    [Fact]
    public void TrackerAndSoundControlsCommitTransactionally()
    {
        // Deterministic control-level tests complement the real desktop/browser GUI smoke tests.
        // No audio device, windowing server, or machine-specific installed font is required.
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var editor = new SongEditor(DemoSong.CreateEmpty());
        var patternId = editor.Song.Patterns[0].Id;
        var tracker = new TrackerGrid();
        tracker.SetSong(editor.Song, patternId);
        tracker.Change += change => { editor.Change(change); tracker.SetSong(editor.Song, patternId); };
        tracker.Select(0, 0);
        tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "F#5" });
        Assert.True(tracker.HasPendingEdit); Assert.False(editor.IsDirty);
        Assert.True(tracker.CommitPending()); Assert.False(tracker.HasPendingEdit);
        Assert.Equal(78, editor.Song.Patterns[0].Tracks[0].Rows[0].Pitch);
        tracker.Select(1, 0); Assert.True(tracker.CommitText("F"));
        Assert.Equal(77, editor.Song.Patterns[0].Tracks[0].Rows[1].Pitch);
        Assert.True(tracker.CommitText("F#5 S"));
        Assert.Equal(NoteTiming.Swing, editor.Song.Patterns[0].Tracks[0].Rows[1].Timing);
        tracker.Select(2, 0); tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "C4" });
        tracker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
        tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "S" });
        Assert.True(tracker.CommitPending()); Assert.Equal(NoteTiming.Swing, editor.Song.Patterns[0].Tracks[0].Rows[2].Timing);
        editor.Change(s => s.Patterns[0].Tracks[0].Rows[0].Effects = ["A37", "V80"]);
        tracker.SetSong(editor.Song, patternId); tracker.Select(0, 0, 1);
        Assert.True(tracker.PasteText("V80\tA37"));
        Assert.Equal(new[] { "V80", "A37" }, editor.Song.Patterns[0].Tracks[0].Rows[0].Effects);
        var before = SongFile.Write(editor.Song); var revision = editor.Revision;
        tracker.Select(0, 0); Assert.False(tracker.PasteText("C4\nnot-a-note"));
        Assert.Equal(before, SongFile.Write(editor.Song)); Assert.Equal(revision, editor.Revision);
        tracker.Select(31, 0); Assert.False(tracker.PasteText("C4\nD4")); Assert.Equal(before, SongFile.Write(editor.Song));
        editor.MarkSaved(); editor.MarkUnsaved(); Assert.True(editor.IsDirty); editor.MarkSaved(); Assert.False(editor.IsDirty);
        tracker.Select(0, 0); tracker.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "D4" });
        var extra = new Pattern { Name = "Other", Length = 32 }; foreach (var t in editor.Song.Tracks) extra.GetTrack(t.Id);
        editor.Change(s => s.Patterns.Add(extra)); tracker.SetSong(editor.Song, extra.Id); Assert.False(tracker.HasPendingEdit);

        var instrument = new Instrument { Name = "Control test", VolumeDb = -72, FilterCutoff = 32000, IsLocal = true };
        var song = new Song { Instruments = [instrument] }; int edits = 0;
        InstrumentPanel? panel = null;
        panel = new InstrumentPanel(change => { edits++; change(song); panel!.ShowInstrument(song, instrument.Id); }, () => { }, () => { }, () => { });
        T Named<T>(string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        panel.ShowInstrument(song, instrument.Id); Assert.Equal(0, edits);
        Assert.Equal(-72, Named<Slider>("InstrumentVolumeDb").Value);
        Assert.Equal(32000, Named<NumericUpDown>("FilterCutoff").Value);
        Named<NumericUpDown>("AttackMs").Value = 47; Assert.Equal(1, edits); Assert.Equal(47, instrument.Amplitude.AttackMs);
        Named<ComboBox>("WaveformSelector").SelectedIndex = 6; Assert.Equal(2, edits); Assert.Equal(128, instrument.CustomWave.Length);
        Named<ComboBox>("WaveformSelector").SelectedIndex = 7; Assert.Equal(3, edits); Assert.Equal(2, instrument.Wavetable.Count);
        Named<ComboBox>("WavetableFrameSelector").SelectedIndex = 1; Assert.Equal(3, edits);
        var original = instrument.Wavetable[1][0];
        Named<Control>("WaveformEditor").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Assert.Equal(4, edits); Assert.True(instrument.Wavetable[1][0] < original);
        var slider = Named<Slider>("InstrumentVolumeDb"); slider.Value = -14; Assert.Equal(4, edits);
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right }); Assert.Equal(5, edits); Assert.Equal(-14, instrument.VolumeDb);
        Named<ComboBox>("VoiceKindSelector").SelectedIndex = 1; Assert.Equal(6, edits); Assert.Equal(DrumKind.Kick, instrument.Drum); Assert.True(instrument.IsLocal);
        panel.ShowInstrument(song, null); Assert.Equal(6, edits);
        SamplingControlTests.Run();

        // Repeated theme changes preserve brush identity and editor state, including
        // switching back and forth between Fluent's light and dark variants.
        var application = Application.Current!;
        application.Styles.Add(new FluentTheme());
        EditorThemes.Apply(EditorThemes.Default);
        Assert.Equal("catppuccin-mocha", EditorThemes.Current.Id);
        Assert.Equal(new[] { "solarized-light", "solarized-dark", "catppuccin-mocha", "catppuccin-latte", "dark", "gruvbox" },
            EditorThemes.All.Select(t => t.Id));
        var background = Assert.IsType<SolidColorBrush>(application.Resources["EditorBackground"]);
        var text = Assert.IsType<SolidColorBrush>(application.Resources["EditorText"]);
        var accent = Assert.IsType<SolidColorBrush>(application.Resources["EditorAccent"]);
        var mountedPanel = new Border { Background = background, Child = new TextBlock { Text = "Existing control", Foreground = text } };
        var beforeThemes = SongFile.Write(editor.Song);
        var notifications = 0;
        EventHandler onThemeChanged = (_, _) => notifications++;
        EditorThemes.ThemeChanged += onThemeChanged;
        try
        {
            foreach (var theme in EditorThemes.All.Concat(EditorThemes.All.Reverse()))
            {
                EditorThemes.Apply(theme.Id);
                var variant = theme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
                Assert.Equal(theme, EditorThemes.Current);
                Assert.Equal(variant, application.RequestedThemeVariant);
                Assert.Same(background, application.Resources["EditorBackground"]);
                Assert.Same(text, application.Resources["EditorText"]);
                Assert.Same(accent, application.Resources["EditorAccent"]);
                Assert.Same(background, mountedPanel.Background);
                Assert.Equal(Color.Parse(theme.Background), background.Color);
                Assert.Equal(Color.Parse(theme.Text), text.Color);
                Assert.Equal(Color.Parse(theme.Accent), accent.Color);
                Assert.Equal(Color.Parse(theme.Selection), Assert.IsType<SolidColorBrush>(application.Resources["EditorSelection"]).Color);
                Assert.Equal(Color.Parse(theme.Workspace), Assert.IsType<SolidColorBrush>(application.Resources["EditorWorkspace"]).Color);
                Assert.Equal(Color.Parse(theme.Accent), application.Styles.OfType<FluentTheme>().Single().Palettes[variant].Accent);
            }
            Assert.Equal(12, notifications);
            Assert.Equal(beforeThemes, SongFile.Write(editor.Song));
            EditorThemes.Apply("unknown-theme");
            Assert.Equal(EditorThemes.Default, EditorThemes.Current.Id);
            Assert.Equal(EditorThemes.Default, EditorThemes.Find(null).Id);
        }
        finally
        {
            EditorThemes.ThemeChanged -= onThemeChanged;
            EditorThemes.Apply(EditorThemes.Default);
        }
        WaveformEditorRegressionChecks.Run();
        StartupUiRegressionChecks.Run();
        FxReferencePanelChecks.Run();
        FxReferenceIntegrationChecks.Run();
        PaneLayoutRegressionChecks.Run();
        PaneKeyboardRegressionChecks.Run();
        ArrangementIntegrationChecks.Run();
        EditorFieldFocusRegressionChecks.Run();
        UpdateUiRegressionChecks.Run();
    }
}
