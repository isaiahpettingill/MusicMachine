using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.App.Updating;
using MusicMachine.Core;
using MusicMachine.Audio;

namespace MusicMachine.Tests;

// Runs within the existing headless editor test so all controls share its UI dispatcher.
internal static class UpdateUiRegressionChecks
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "musicmachine-update-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousData = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA");
        var previousDesktop = UpdateHost.DesktopEnabled;
        var previousTheme = EditorThemes.Current.Id;
        var previousPreferences = EditorPlatform.LoadPreferences;
        var previousSave = EditorPlatform.SavePreferences;
        var previousRecovery = EditorPlatform.ClearRecoveryAsync;
        var views = new List<MainView>();
        try
        {
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", directory);
            string preferences = "theme=dark\nlibrary=false\ninspector=true\nworkspace=Sampling\ncheckForUpdates=false\n";
            EditorPlatform.LoadPreferences = () => preferences;
            EditorPlatform.SavePreferences = text => preferences = text;
            EditorPlatform.ClearRecoveryAsync = null;
            UpdateHost.DesktopEnabled = false;
            var nonDesktop = new MainView(); views.Add(nonDesktop);
            Assert.Null(Field<MenuItem?>(nonDesktop, "updateMenu"));
            Assert.Null(Field<CancellationTokenSource?>(nonDesktop, "updateLifetime"));
            Assert.False(Preference(nonDesktop, "CheckForUpdates"));
            Invoke(nonDesktop, "SaveViewSettings");
            Assert.Contains("checkForUpdates=false", preferences);
            Assert.Contains("workspace=Sampling", preferences);
            Assert.Equal("Sampling", Field<string>(nonDesktop, "mode"));
            Assert.Contains("theme=dark", preferences);

            UpdateHost.DesktopEnabled = true;
            var desktop = new MainView(); views.Add(desktop);
            StreamingExportTests.RunControlChecks(desktop);
            Assert.NotNull(Field<MenuItem?>(desktop, "updateMenu"));
            var first = (Task)Invoke(desktop, "ShowUpdates")!;
            Assert.False(first.IsCompleted);
            var dialog = Field<ContentControl>(desktop, "updateDialog");
            var second = (Task)Invoke(desktop, "ShowUpdates")!;
            Assert.True(second.IsCompletedSuccessfully);
            Assert.Same(dialog, Field<ContentControl>(desktop, "updateDialog"));
            Assert.Single(((Grid)desktop.Content!).Children.OfType<Border>());
            Assert.False(dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "AutoCheckUpdates").IsChecked);
            // No manifest means this is a development build, so a manual dialog must not enable updating.
            Assert.Null(UpdateHost.Installation);
            Assert.False(dialog.GetLogicalDescendants().OfType<Button>().Single(c => c.Name == "UpdateAction").IsEnabled);
            using var download = new CancellationTokenSource();
            Set(desktop, "updateOperation", download);
            Click(dialog.GetLogicalDescendants().OfType<Button>().Single(c => Equals(c.Content, "Close")));
            Assert.True(download.IsCancellationRequested);
            Pump(first); Assert.Null(Field<ContentControl?>(desktop, "updateDialog"));
            Set(desktop, "updateOperation", null);
            var reopened = (Task)Invoke(desktop, "ShowUpdates")!;
            Assert.False(reopened.IsCompleted);
            var reopenedDialog = Field<ContentControl>(desktop, "updateDialog");
            Assert.NotSame(dialog, reopenedDialog);
            Click(reopenedDialog.GetLogicalDescendants().OfType<Button>().Single(c => Equals(c.Content, "Close")));
            Pump(reopened);

            // A restart never silently discards transient Sampling work, even for a clean song.
            Assert.True(((Task<bool>)Invoke(desktop, "ConfirmSamplingRestart")!).GetAwaiter().GetResult());
            var sampling = Field<SamplingPanel>(desktop, "samplingPanel");
            sampling.LoadClip(new SampleClip(Enumerable.Range(0, 1024).Select(i => (float)Math.Sin(i * Math.PI / 64)).ToArray(), 48000, 1, "unsaved-source.wav"));
            var cancelSampling = (Task<bool>)Invoke(desktop, "ConfirmSamplingRestart")!;
            Assert.False(cancelSampling.IsCompleted);
            Click(desktop.GetLogicalDescendants().OfType<Button>().Single(c => Equals(c.Content, "Cancel")));
            Pump(cancelSampling); Assert.False(cancelSampling.Result); Assert.True(sampling.HasSource);
            var approveSampling = (Task<bool>)Invoke(desktop, "ConfirmSamplingRestart")!;
            Click(desktop.GetLogicalDescendants().OfType<Button>().Single(c => Equals(c.Content, "Restart")));
            Pump(approveSampling); Assert.True(approveSampling.Result); Assert.True(sampling.HasSource);
            using (var busySampling = new CancellationTokenSource())
            {
                var work = typeof(SamplingPanel).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!;
                work.SetValue(sampling, busySampling);
                Assert.False(((Task<bool>)Invoke(desktop, "ConfirmSamplingRestart")!).GetAwaiter().GetResult());
                work.SetValue(sampling, null);
            }

            var recovery = Path.Combine(directory, "recovery.song");
            var song = Field<SongEditor>(desktop, "editor");
            song.Change(s => s.Title = "Unsaved update recovery");
            SongFile.Save(recovery, song.Song);
            var snapshot = File.ReadAllBytes(recovery);
            // Approved restart preserves even dirty songs and never opens a discard prompt.
            Set(desktop, "updateRestartApproved", true);
            Assert.True(desktop.RequestCloseAsync().GetAwaiter().GetResult());
            Assert.Equal(snapshot, File.ReadAllBytes(recovery));
            Assert.Single(((Grid)desktop.Content!).Children);
            Set(desktop, "updateRestartApproved", false);
            using var preparing = new CancellationTokenSource();
            Set(desktop, "updateInstalling", true); Set(desktop, "updateOperation", preparing);
            Assert.False(desktop.RequestCloseAsync().GetAwaiter().GetResult());
            Assert.True(preparing.IsCancellationRequested);
            Assert.Equal(snapshot, File.ReadAllBytes(recovery));
            Set(desktop, "updateInstalling", false); Set(desktop, "updateOperation", null);
            var ordinaryClose = desktop.RequestCloseAsync();
            Assert.False(ordinaryClose.IsCompleted);
            var cancel = desktop.GetLogicalDescendants().OfType<Button>().Single(c => Equals(c.Content, "Cancel"));
            Click(cancel); Pump(ordinaryClose);
            Assert.False(ordinaryClose.GetAwaiter().GetResult());
            Assert.Equal(snapshot, File.ReadAllBytes(recovery));
            song.MarkSaved();
            Assert.True(desktop.RequestCloseAsync().GetAwaiter().GetResult());
            Assert.False(File.Exists(recovery));
        }
        finally
        {
            foreach (var view in views)
            {
                Field<DispatcherTimer>(view, "timer").Stop(); Field<DispatcherTimer>(view, "recoveryTimer").Stop();
                Invoke(view, "StopUpdates"); Field<IDisposable>(view, "player").Dispose();
            }
            UpdateHost.DesktopEnabled = previousDesktop; EditorThemes.Apply(previousTheme);
            EditorPlatform.LoadPreferences = previousPreferences; EditorPlatform.SavePreferences = previousSave;
            EditorPlatform.ClearRecoveryAsync = previousRecovery;
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", previousData);
            Directory.Delete(directory, true);
        }
    }

    private static bool Preference(MainView view, string name)
    {
        var settings = Field<object>(view, "viewSettings");
        return (bool)settings.GetType().GetField(name)!.GetValue(settings)!;
    }
    private static T Field<T>(MainView view, string name) => (T)typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static void Set(MainView view, string name, object? value) => typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
    private static object? Invoke(MainView view, string name) => typeof(MainView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "The headless UI operation did not finish.");
        task.GetAwaiter().GetResult();
    }
}
