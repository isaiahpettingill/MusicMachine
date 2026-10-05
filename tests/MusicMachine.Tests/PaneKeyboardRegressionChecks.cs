using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using MusicMachine.App;

namespace MusicMachine.Tests;

internal static class PaneKeyboardRegressionChecks
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "musicmachine-pane-keyboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousData = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA");
        var previousLoad = EditorPlatform.LoadPreferences; var previousSave = EditorPlatform.SavePreferences;
        var previousStorage = EditorPlatform.ProjectStorage;
        Window? window = null; MainView? view = null;
        try
        {
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", directory);
            var preferences = "library=true\ninspector=false\ncheckForUpdates=false\n";
            EditorPlatform.LoadPreferences = () => preferences; EditorPlatform.SavePreferences = text => preferences = text;
            EditorPlatform.ProjectStorage = null;
            view = new MainView(); window = new Window { Width = 1180, Height = 812, Content = view };
            window.Show(); window.UpdateLayout();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(view, "projectChangeBusy") && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
            Dispatcher.UIThread.RunJobs(); Assert.False(Field<bool>(view, "projectChangeBusy"));
            var tracker = Field<TrackerGrid>(view, "tracker"); tracker.Select(2, 1, 1); Assert.True(tracker.Focus());
            var workArea = Field<ContentControl>(view, "workArea"); var workspace = workArea.Content;
            var detached = 0; tracker.DetachedFromVisualTree += (_, _) => detached++;
            void Press(Key key)
            {
                window.KeyPress(key, RawInputModifiers.Control, key == Key.L ? PhysicalKey.L : PhysicalKey.I, key == Key.L ? "l" : "i");
                window.KeyRelease(key, RawInputModifiers.Control, key == Key.L ? PhysicalKey.L : PhysicalKey.I, key == Key.L ? "l" : "i");
            }
            // Route through actual focus/raw input, not MainView.RaiseEvent or a
            // direct TogglePane call. No click/refocus is allowed between keys.
            Press(Key.L);
            Assert.Contains("library=false\n", preferences);
            Assert.Equal(0, detached);
            Assert.Same(workspace, workArea.Content);
            Assert.True(tracker.IsFocused);
            Press(Key.I);
            Assert.Contains("inspector=true\n", preferences);
            Press(Key.L); Press(Key.I);
            Assert.Contains("library=true\n", preferences); Assert.Contains("inspector=false\n", preferences);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, detached); Assert.True(tracker.IsFocused);
            Assert.Equal(2, tracker.SelectedRow); Assert.Equal(1, tracker.SelectedTrack); Assert.Equal(1, tracker.SelectedColumn);

            // Closing a pane while one of its controls owns focus must hand
            // keyboard input back to the editor after layout.
            Assert.True(Field<ComboBox>(view, "libraryPicker").Focus());
            Press(Key.L); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains("library=false\n", preferences); Assert.True(tracker.IsFocused);
            Press(Key.I); Assert.Contains("inspector=true\n", preferences);
            Assert.True(Field<ComboBox>(view, "inspectorPicker").Focus());
            Press(Key.I); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains("inspector=false\n", preferences); Assert.True(tracker.IsFocused);
            Assert.Equal(0, detached);
        }
        finally
        {
            window?.Close();
            if (view is not null)
            {
                Field<DispatcherTimer>(view, "timer").Stop(); Field<DispatcherTimer>(view, "recoveryTimer").Stop();
                typeof(MainView).GetMethod("StopUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
                Field<IDisposable>(view, "player").Dispose();
            }
            EditorPlatform.LoadPreferences = previousLoad; EditorPlatform.SavePreferences = previousSave; EditorPlatform.ProjectStorage = previousStorage;
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", previousData);
            Directory.Delete(directory, true);
        }
    }
    private static T Field<T>(MainView view, string name) => (T)typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
}
