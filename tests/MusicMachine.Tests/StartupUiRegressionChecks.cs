using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MusicMachine.App;
using MusicMachine.App.Updating;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.Tests;

internal static class StartupUiRegressionChecks
{
    internal static void Run()
    {
        using var temp = new StartupDirectory();
        var previousData = Environment.GetEnvironmentVariable("MUSICMACHINE_DATA"); var previousStore = EditorPlatform.ProjectStorage;
        var previousDesktop = UpdateHost.DesktopEnabled; var previousResume = UpdateHost.ResumePlan;
        var views = new List<MainView>();
        MainView View(MemoryProjectStorage store, TestPicker? picker = null)
        {
            EditorPlatform.ProjectStorage = store; var view = new MainView(projectFiles: picker?.Access); views.Add(view); return view;
        }
        try
        {
            Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", temp.Path); UpdateHost.DesktopEnabled = false; UpdateHost.ResumePlan = null;
            var empty = new MemoryProjectStorage(true); var first = View(empty);
            Pump(Call(first, "InitializeProjectAsync", (object?)null)); StartupTests.AssertBlank(Editor(first).Song); Assert.False(Editor(first).IsDirty); Assert.Empty(empty.Data);

            // A clean browser project restores from its own snapshot; no local path or dirty marker is fabricated.
            var store = new MemoryProjectStorage(true); store.Data["last-project"] = SongFile.Write(StartupTests.Named("Last clean"));
            var reopened = View(store); Pump(Call(reopened, "InitializeProjectAsync", (object?)null));
            Assert.Equal("Last clean", Editor(reopened).Song.Title); Assert.Null(Field<string?>(reopened, "filePath")); Assert.False(Editor(reopened).IsDirty);
            var source = Field<SamplingPanel>(reopened, "samplingPanel"); source.LoadClip(new SampleClip(new float[1024], 48000, 1, "private-raw-clip.wav"));
            Pump(Call(reopened, "RememberCurrentProjectAsync", null, null)); Assert.Equal(SongFile.Write(Editor(reopened).Song), store.Data["last-project"]);
            Assert.DoesNotContain("private-raw-clip", Encoding.UTF8.GetString(store.Data["last-project"]));

            // Keeping old recovery must survive later edits, Save, clean close, and a fresh view.
            var oldRecovery = SongFile.Write(StartupTests.Named("Unrelated unsaved")); store.Data["song"] = oldRecovery;
            var withRecovery = View(store); var startup = Call(withRecovery, "InitializeProjectAsync", (object?)null);
            Assert.False(startup.IsCompleted); Click(withRecovery, "Keep for later"); Pump(startup);
            Assert.Equal("Last clean", Editor(withRecovery).Song.Title); Assert.Equal(oldRecovery, store.Data["song"]);
            Editor(withRecovery).Change(s => s.Title = "Current edits"); Pump(Call(withRecovery, "SaveRecoverySnapshotAsync"));
            var currentKey = Field<string>(withRecovery, "sessionRecoveryKey"); Assert.True(store.Data.ContainsKey(currentKey)); Assert.Equal(oldRecovery, store.Data["song"]);
            var close = withRecovery.RequestCloseAsync(); Click(withRecovery, "Discard"); Pump(close); Assert.True(close.Result);
            Assert.False(store.Data.ContainsKey(currentKey)); Assert.Equal(oldRecovery, store.Data["song"]);
            var recover = View(store); startup = Call(recover, "InitializeProjectAsync", (object?)null); Click(recover, "Recover"); Pump(startup);
            Assert.Equal("Unrelated unsaved", Editor(recover).Song.Title); Assert.True(Editor(recover).IsDirty); Assert.StartsWith("recovery-", Field<string>(recover, "sessionRecoveryKey")); Assert.False(store.Data.ContainsKey("song")); Assert.Equal(oldRecovery, store.Data[Field<string>(recover, "sessionRecoveryKey")]);
            Assert.Equal("Last clean", SongFile.Read(store.Data["last-project"]).Title);

            // Explicit paths beat recovery and last-project restoration without opening a recovery prompt.
            var requestedPath = Path.Combine(temp.Path, "requested.song"); SongFile.Save(requestedPath, StartupTests.Named("Explicit project"));
            var desktopStore = new MemoryProjectStorage(false); desktopStore.Data["song"] = oldRecovery; desktopStore.Data["last-project"] = Encoding.UTF8.GetBytes("/missing-prior.song");
            var explicitView = View(desktopStore); Pump(Call(explicitView, "InitializeProjectAsync", requestedPath));
            Assert.Equal("Explicit project", Editor(explicitView).Song.Title); Assert.Equal(oldRecovery, desktopStore.Data["song"]); Assert.Equal(requestedPath, Encoding.UTF8.GetString(desktopStore.Data["last-project"]));
            Assert.Single(explicitView.OverlayChildren()); Assert.Contains("recovery kept", Field<TextBlock>(explicitView, "status").Text);

            // Native saves remember their successful destination; failed Save as retains the old path and dirty work.
            var nativePicker = new TestPicker(); var native = View(desktopStore, nativePicker);
            Pump(Call(native, "InitializeProjectAsync", requestedPath)); Editor(native).Change(s => s.Title = "Saved on desktop");
            Assert.True(((Task<bool>)Call(native, "SaveSong", false)).GetAwaiter().GetResult()); Assert.Equal("Saved on desktop", SongFile.Load(requestedPath).Title);
            Assert.Equal("Saved on desktop", new ProjectStartupService(desktopStore).ResolveAsync().GetAwaiter().GetResult().Song.Title);
            Editor(native).Change(s => s.Title = "Not saved yet"); var nativeBefore = SongFile.Write(Editor(native).Song);
            nativePicker.SaveFile = TestFile([]) with { LocalPath = Path.Combine(requestedPath, "impossible.song") };
            Assert.False(((Task<bool>)Call(native, "SaveSong", true)).GetAwaiter().GetResult()); Assert.Equal(requestedPath, Field<string?>(native, "filePath")); Assert.True(Editor(native).IsDirty);
            Assert.Equal(requestedPath, Encoding.UTF8.GetString(desktopStore.Data["last-project"])); Assert.Equal(nativeBefore, SongFile.Write(Editor(native).Song));
            nativePicker.SaveFile = null; Assert.False(((Task<bool>)Call(native, "SaveSong", true)).GetAwaiter().GetResult());
            Pump(Call(native, "OpenPath", requestedPath + ".missing")); Assert.Equal(nativeBefore, SongFile.Write(Editor(native).Song)); Assert.Equal(oldRecovery, desktopStore.Data["song"]);

            // New is transactional across Cancel and reset failure, and intentionally starts blank next time.
            var picker = new TestPicker(); var operations = View(new MemoryProjectStorage(true), picker);
            var operationStore = (MemoryProjectStorage)Field<IProjectStorage>(operations, "projectStorage");
            Pump(Call(operations, "LoadSong", StartupTests.Named("Before New"), null, "before.song")); Editor(operations).Change(s => s.Title = "Dirty before New");
            Pump(Call(operations, "SaveRecoverySnapshotAsync")); var before = SongFile.Write(Editor(operations).Song); var remembered = operationStore.Data["last-project"].ToArray();
            var makeNew = Call(operations, "NewSong"); Click(operations, "Cancel"); Pump(makeNew); Assert.Equal(before, SongFile.Write(Editor(operations).Song)); Assert.Equal(remembered, operationStore.Data["last-project"]);
            operationStore.FailWrites = true; makeNew = Call(operations, "NewSong"); Click(operations, "Discard"); Pump(makeNew); Assert.Equal(before, SongFile.Write(Editor(operations).Song)); operationStore.FailWrites = false;
            makeNew = Call(operations, "NewSong"); Click(operations, "Discard"); Pump(makeNew); StartupTests.AssertBlank(Editor(operations).Song); Assert.False(operationStore.Data.ContainsKey("last-project"));
            Editor(operations).Change(s => s.Title = "Untitled edits"); Pump(Call(operations, "SaveRecoverySnapshotAsync")); Assert.False(operationStore.Data.ContainsKey("last-project")); Assert.True(operationStore.Data.ContainsKey(Field<string>(operations, "sessionRecoveryKey")));

            // Cancelled/failed pickers, corrupt reads and failed stream disposal preserve the active song and startup target.
            picker.OpenFiles = []; var open = Call(operations, "OpenSong"); Click(operations, "Discard"); Pump(open); Assert.Equal("Untitled edits", Editor(operations).Song.Title);
            picker.OpenFiles = [TestFile([1, 2, 3])]; open = Call(operations, "OpenSong"); Click(operations, "Discard"); Pump(open); Assert.Equal("Untitled edits", Editor(operations).Song.Title); Assert.True(Editor(operations).IsDirty);
            picker.OpenFiles = [TestFile(SongFile.Write(StartupTests.Named("Must not replace")), failReadDispose: true)]; open = Call(operations, "OpenSong"); Click(operations, "Discard"); Pump(open); Assert.Equal("Untitled edits", Editor(operations).Song.Title); Assert.True(Editor(operations).IsDirty);
            Assert.False(((Task<bool>)Call(operations, "SaveSong", false)).GetAwaiter().GetResult()); Assert.True(Editor(operations).IsDirty); Assert.Null(Field<string?>(operations, "filePath"));
            picker.SaveFile = TestFile([], failWrite: true); Assert.False(((Task<bool>)Call(operations, "SaveSong", false)).GetAwaiter().GetResult()); Assert.True(Editor(operations).IsDirty);
            picker.SaveFile = TestFile([], failDispose: true); Assert.False(((Task<bool>)Call(operations, "SaveSong", false)).GetAwaiter().GetResult()); Assert.True(Editor(operations).IsDirty); Assert.False(operationStore.Data.ContainsKey("last-project"));
            picker.SaveFile = TestFile([]); Assert.True(((Task<bool>)Call(operations, "SaveSong", false)).GetAwaiter().GetResult()); Assert.False(Editor(operations).IsDirty); Assert.Equal("Untitled edits", SongFile.Read(operationStore.Data["last-project"]).Title); Assert.Null(Field<string?>(operations, "filePath"));
            picker.OpenFiles = [TestFile(SongFile.Write(StartupTests.Named("Browser import")))]; Pump(Call(operations, "OpenSong")); Assert.Equal("Browser import", SongFile.Read(operationStore.Data["last-project"]).Title);
            var reload = View(operationStore); Pump(Call(reload, "InitializeProjectAsync", (object?)null)); Assert.Equal("Browser import", Editor(reload).Song.Title);

            // A demo is available only by explicit request and remains an unsaved document.
            var previousTarget = operationStore.Data["last-project"].ToArray(); Pump(Call(operations, "LoadDemo"));
            Assert.Equal("Neon Orchard", Editor(operations).Song.Title); Assert.True(Editor(operations).IsDirty); Assert.Equal(previousTarget, operationStore.Data["last-project"]);

            // An in-flight autosave cannot resurrect a recovery entry after New deletes it.
            var racingStore = new MemoryProjectStorage(true); var racing = View(racingStore); Editor(racing).Change(s => s.Title = "Race");
            racingStore.PauseWrite = new TaskCompletionSource(); var writing = Call(racing, "SaveRecoverySnapshotAsync"); Assert.False(writing.IsCompleted);
            var pendingNew = Call(racing, "NewSong"); Click(racing, "Discard"); Assert.False(pendingNew.IsCompleted); racingStore.PauseWrite.SetResult(); Pump(writing); Pump(pendingNew); Assert.Empty(racingStore.Data); StartupTests.AssertBlank(Editor(racing).Song);

            // Undo back to the clean snapshot removes stale current-session autosaves only.
            var undoneStore = new MemoryProjectStorage(true); undoneStore.Data["song"] = oldRecovery; var undone = View(undoneStore);
            Editor(undone).Change(s => s.Title = "Temporary edit"); Pump(Call(undone, "SaveRecoverySnapshotAsync")); var undoneKey = Field<string>(undone, "sessionRecoveryKey");
            Assert.True(undoneStore.Data.ContainsKey(undoneKey)); Assert.True(Editor(undone).Undo()); Pump(Call(undone, "SaveRecoverySnapshotAsync")); Assert.False(undoneStore.Data.ContainsKey(undoneKey)); Assert.Equal(oldRecovery, undoneStore.Data["song"]);

            // Corrupt recovery is retained; no unusable copy replaces the valid clean project.
            var corruptStore = new MemoryProjectStorage(true); corruptStore.Data["last-project"] = SongFile.Write(StartupTests.Named("Valid clean")); corruptStore.Data["song"] = [1, 2, 3];
            var corrupt = View(corruptStore); Pump(Call(corrupt, "InitializeProjectAsync", (object?)null)); Assert.Equal("Valid clean", Editor(corrupt).Song.Title); Assert.Equal(new byte[] { 1, 2, 3 }, corruptStore.Data["song"]); Assert.Contains("could not be read", Field<TextBlock>(corrupt, "status").Text);

            // Update preparation archives an unrelated legacy copy before replacing its fallback slot.
            var updateStore = new MemoryProjectStorage(false); updateStore.Data["song"] = oldRecovery; var updating = View(updateStore);
            var updateBytes = SongFile.Write(StartupTests.Named("Update current")); Pump(Call(updating, "WriteUpdateRecoveryAsync", updateBytes));
            Assert.Equal(updateBytes, updateStore.Data["song"]); Assert.Contains(updateStore.Data.Where(p => p.Key.StartsWith("recovery-")), p => p.Value.SequenceEqual(oldRecovery));
            Set(updating, "updateRestartApproved", true); Assert.True(updating.RequestCloseAsync().GetAwaiter().GetResult()); Assert.Equal(updateBytes, updateStore.Data["song"]);

            // The real update-resume path wins over both an explicit path and the last project, including dirty state/workspace.
            var work = Path.Combine(UpdateHost.DataDirectory, "install-test"); Directory.CreateDirectory(work);
            var resumedEditor = new SongEditor(StartupTests.Named("Resumed updater song")); resumedEditor.MarkUnsaved();
            UpdateRecovery.Save(resumedEditor, null, resumedEditor.Song.Patterns[0].Id, resumedEditor.Song.Instruments[0].Id, "Sampling", work);
            var plan = new UpdatePlan { Token = Guid.NewGuid().ToString("N"), Target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Runtime = "linux-x64", Version = "0.2.0", PreviousVersion = "0.1.0", Commit = new string('a', 40), PreviousCommit = new string('b', 40), ParentProcess = Environment.ProcessId, ParentStartUtcTicks = DateTime.UtcNow.Ticks };
            var planPath = Path.Combine(work, "plan.json"); File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
            UpdateHost.DesktopEnabled = true; UpdateHost.ResumePlan = planPath;
            var resumeStore = new MemoryProjectStorage(false); resumeStore.Data["last-project"] = Encoding.UTF8.GetBytes(requestedPath); var resumed = View(resumeStore);
            Pump(Call(resumed, "InitializeProjectAsync", requestedPath)); Assert.Equal("Resumed updater song", Editor(resumed).Song.Title); Assert.True(Editor(resumed).IsDirty); Assert.Equal("Sampling", Field<string>(resumed, "mode")); Assert.True(File.Exists(Path.Combine(work, "resume.song"))); Assert.Equal("song", Field<string>(resumed, "sessionRecoveryKey"));
        }
        finally
        {
            foreach (var view in views) { Field<DispatcherTimer>(view, "timer").Stop(); Field<DispatcherTimer>(view, "recoveryTimer").Stop(); Field<IDisposable>(view, "player").Dispose(); }
            EditorPlatform.ProjectStorage = previousStore; UpdateHost.DesktopEnabled = previousDesktop; UpdateHost.ResumePlan = previousResume; Environment.SetEnvironmentVariable("MUSICMACHINE_DATA", previousData);
        }
    }
    private static SongEditor Editor(MainView view) => Field<SongEditor>(view, "editor");
    private static T Field<T>(MainView view, string name) => (T)typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static void Set(MainView view, string name, object value) => typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
    private static Task Call(MainView view, string name, params object?[] args) => (Task)typeof(MainView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args)!;
    private static void Click(MainView view, string text) => view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Avalonia.Controls.Controls OverlayChildren(this MainView view) => ((Grid)view.Content!).Children;
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "Startup operation did not finish."); task.GetAwaiter().GetResult();
    }
    private sealed class TestPicker
    {
        internal IReadOnlyList<ProjectFileSelection> OpenFiles = []; internal ProjectFileSelection? SaveFile;
        public ProjectFileAccess Access => new(() => Task.FromResult(OpenFiles.FirstOrDefault()), _ => Task.FromResult(SaveFile));
    }
    private static ProjectFileSelection TestFile(byte[] bytes, bool failWrite = false, bool failDispose = false, bool failReadDispose = false) =>
        new("fixture.song", null, () => Task.FromResult<Stream>(failReadDispose ? new FailedReadClose(bytes) : new MemoryStream(bytes)),
            () => failWrite ? Task.FromException<Stream>(new IOException("write failed")) : Task.FromResult<Stream>(new TestOutput(failDispose)));
    private sealed class FailedReadClose(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("read close failed"));
    }
    private sealed class TestOutput(bool failDispose) : MemoryStream
    {
        public override ValueTask DisposeAsync() => failDispose ? ValueTask.FromException(new IOException("close failed")) : base.DisposeAsync();
    }
}
