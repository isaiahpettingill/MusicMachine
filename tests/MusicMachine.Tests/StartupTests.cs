using System.Text;
using MusicMachine.App;
using MusicMachine.Core;

namespace MusicMachine.Tests;

public sealed class StartupTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FirstLaunchIsCleanBlankAndNeverPersistsADemo(bool browser)
    {
        var store = new MemoryProjectStorage(browser);
        var result = await new ProjectStartupService(store).ResolveAsync();
        AssertBlank(result.Song); Assert.Null(result.Path); Assert.Null(result.Notice); Assert.Empty(store.Data);
    }
    [Fact]
    public async Task DesktopRemembersAbsolutePathAndReadsLatestDiskContents()
    {
        using var temp = new StartupDirectory();
        var song = Named("Opened song"); var file = Path.Combine(temp.Path, "named.song"); SongFile.Save(file, song);
        var store = new DesktopProjectStorage(temp.Path); var startup = new ProjectStartupService(store);
        await startup.RememberAsync(song, file);
        Assert.Equal(file, Encoding.UTF8.GetString((await store.ReadAsync("last-project"))!));
        song.Title = "Saved changes"; SongFile.Save(file, song);
        Assert.Equal("Saved changes", (await startup.ResolveAsync()).Song.Title);
        await startup.ResetAsync(); AssertBlank((await startup.ResolveAsync()).Song); Assert.True(File.Exists(file));
    }
    [Fact]
    public async Task BrowserSnapshotIsDurableIndependentAndHasNoFilePath()
    {
        var store = new MemoryProjectStorage(true); var first = new ProjectStartupService(store);
        var song = Named("Browser saved song"); await first.RememberAsync(song, "/not-a-browser-permission.song");
        song.Title = "Unsaved mutation";
        await store.WriteAsync("recovery-independent", SongFile.Write(song));
        var second = new ProjectStartupService(store); var result = await second.ResolveAsync();
        Assert.Equal("Browser saved song", result.Song.Title); Assert.Null(result.Path);
        await second.ResetAsync(); AssertBlank((await first.ResolveAsync()).Song); Assert.True(store.Data.ContainsKey("recovery-independent"));
    }
    [Fact]
    public async Task MissingAndCorruptPathsStayRememberedWithBlankAndClearNotice()
    {
        using var temp = new StartupDirectory(); var path = Path.Combine(temp.Path, "missing.song");
        var store = new DesktopProjectStorage(temp.Path); var startup = new ProjectStartupService(store);
        await startup.RememberAsync(Named("Kept"), path); var previous = await store.ReadAsync("last-project");
        var result = await startup.ResolveAsync(); AssertBlank(result.Song); Assert.Contains("Could not reopen", result.Notice); Assert.Null(result.Path);
        File.WriteAllBytes(path, [0, 1, 2]); result = await startup.ResolveAsync(); AssertBlank(result.Song); Assert.Contains("started blank", result.Notice);
        Assert.Equal(previous, await store.ReadAsync("last-project"));
        await store.WriteAsync("last-project", Encoding.UTF8.GetBytes("relative.song")); result = await startup.ResolveAsync(); AssertBlank(result.Song); Assert.Contains("invalid", result.Notice);
    }
    [Fact]
    public async Task CorruptSnapshotAndUnavailableStorageAreNotDeletedOrReportedAsRestored()
    {
        var store = new MemoryProjectStorage(true); await store.WriteAsync("last-project", [3, 4, 5]);
        var service = new ProjectStartupService(store); var result = await service.ResolveAsync(); AssertBlank(result.Song); Assert.Contains("Could not reopen", result.Notice); Assert.Equal(new byte[] { 3, 4, 5 }, store.Data["last-project"]);
        store.FailReads = true; result = await service.ResolveAsync(); AssertBlank(result.Song); Assert.Contains("storage blocked", result.Notice);
    }
    [Fact]
    public async Task ExplicitPathBeatsLastProjectWithoutTouchingRecoveryAndFailureDoesNotFallBack()
    {
        using var temp = new StartupDirectory(); var path = Path.Combine(temp.Path, "requested.song"); SongFile.Save(path, Named("Requested"));
        var store = new MemoryProjectStorage(true); await store.WriteAsync("last-project", SongFile.Write(Named("Last"))); await store.WriteAsync("song", SongFile.Write(Named("Unsaved")));
        var service = new ProjectStartupService(store); var before = store.Data.ToDictionary(k => k.Key, v => v.Value.ToArray());
        var result = await service.ResolveAsync(path); Assert.Equal("Requested", result.Song.Title); Assert.Equal(path, result.Path);
        result = await service.ResolveAsync(path + ".missing"); AssertBlank(result.Song); Assert.Contains("requested song", result.Notice);
        Assert.Equal(before.Keys, store.Data.Keys); foreach (var key in before.Keys) Assert.Equal(before[key], store.Data[key]);
    }
    internal static Song Named(string name) { var song = DemoSong.CreateEmpty(); song.Title = name; return song; }
    internal static void AssertBlank(Song song)
    {
        Assert.Equal("Untitled song", song.Title); Assert.Single(song.Patterns);
        Assert.All(song.Patterns.SelectMany(p => p.Tracks).SelectMany(t => t.Rows), note => { Assert.Equal(NoteKind.Empty, note.Kind); Assert.Empty(note.Effects); });
        Assert.All(song.Patterns.SelectMany(p => p.Drums).SelectMany(d => d.Steps), step => Assert.Equal(0, step));
        Assert.All(song.Tracks, track => Assert.Empty(track.VolumeAutomation));
    }
}
internal sealed class StartupDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "musicmachine-startup-" + Guid.NewGuid().ToString("N"));
    public StartupDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
internal sealed class MemoryProjectStorage(bool browser) : IProjectStorage
{
    public bool UsesProjectSnapshots => browser;
    internal Dictionary<string, byte[]> Data { get; } = [];
    internal bool FailReads, FailWrites;
    internal TaskCompletionSource? PauseWrite;
    public Task<byte[]?> ReadAsync(string key) { if (FailReads) throw new IOException("storage blocked"); return Task.FromResult(Data.TryGetValue(key, out var bytes) ? bytes.ToArray() : null); }
    public async Task WriteAsync(string key, byte[] bytes) { if (FailWrites) throw new IOException("storage full"); if (PauseWrite is { } pause) await pause.Task; Data[key] = bytes.ToArray(); }
    public Task DeleteAsync(string key) { if (FailWrites) throw new IOException("storage full"); Data.Remove(key); return Task.CompletedTask; }
    public Task<string[]> ListAsync(string prefix) { if (FailReads) throw new IOException("storage blocked"); return Task.FromResult(Data.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray()); }
}
