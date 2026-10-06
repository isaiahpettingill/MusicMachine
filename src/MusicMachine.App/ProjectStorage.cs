using System.Text;
using MusicMachine.Core;

namespace MusicMachine.App;

/// <summary>Stores editor-owned songs, instruments and startup metadata, never Sampling source clips.</summary>
public interface IProjectStorage
{
    bool UsesProjectSnapshots { get; }
    Task<byte[]?> ReadAsync(string key);
    Task WriteAsync(string key, byte[] bytes);
    Task DeleteAsync(string key);
    Task<string[]> ListAsync(string prefix);
}

public sealed class DesktopProjectStorage(string directory) : IProjectStorage
{
    public bool UsesProjectSnapshots => false;
    private string Location(string key)
    {
        if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid project storage key.");
        return Path.Combine(directory, key == "song" ? "recovery.song" : key == "last-project" ? "last-project.txt" : key + (key.StartsWith("instrument-", StringComparison.Ordinal) ? ".instrument" : ".song"));
    }
    public Task<byte[]?> ReadAsync(string key)
    {
        var path = Location(key);
        if (!File.Exists(path)) return Task.FromResult<byte[]?>(null);
        if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("Stored project exceeds its size limit.");
        return Task.FromResult<byte[]?>(File.ReadAllBytes(path));
    }
    public Task WriteAsync(string key, byte[] bytes)
    {
        var path = Location(key); Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string key) { File.Delete(Location(key)); return Task.CompletedTask; }
    public Task<string[]> ListAsync(string prefix) => Task.FromResult(Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, prefix.StartsWith("instrument-", StringComparison.Ordinal) ? "*.instrument" : "*.song").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray() : []);
}

public sealed record StartupProject(Song Song, string? Path, string? Notice);

/// <summary>Last clean project is independent of unsaved recovery. Reads never consume either.</summary>
public sealed class ProjectStartupService(IProjectStorage storage)
{
    public async Task<StartupProject> ResolveAsync(string? explicitPath = null)
    {
        if (explicitPath is not null)
        {
            try { var path = System.IO.Path.GetFullPath(explicitPath); return new(SongFile.Load(path), path, "Opened " + System.IO.Path.GetFileName(path)); }
            catch (Exception ex) { return new(DemoSong.CreateEmpty(), null, "Could not open requested song; started blank: " + ex.Message); }
        }
        try
        {
            var bytes = await storage.ReadAsync("last-project");
            if (bytes is null) return new(DemoSong.CreateEmpty(), null, null);
            if (storage.UsesProjectSnapshots) return new(SongFile.Read(bytes), null, "Reopened your last project from this browser’s storage");
            var path = new UTF8Encoding(false, true).GetString(bytes);
            if (!System.IO.Path.IsPathFullyQualified(path)) throw new InvalidDataException("The remembered project path is invalid.");
            return new(SongFile.Load(path), path, "Reopened " + System.IO.Path.GetFileName(path));
        }
        catch (Exception ex) { return new(DemoSong.CreateEmpty(), null, "Could not reopen your last project; started blank: " + ex.Message); }
    }
    public Task RememberAsync(Song song, string? path) => storage.UsesProjectSnapshots
        ? storage.WriteAsync("last-project", SongFile.Write(song))
        : path is null ? ResetAsync() : storage.WriteAsync("last-project", Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path)));
    public Task ResetAsync() => storage.DeleteAsync("last-project");
}
