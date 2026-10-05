using System.Text.Json;
using System.Text.Json.Serialization;
using MusicMachine.Core;

namespace MusicMachine.App.Updating;

public sealed class UpdateSession
{
    public byte[] SongBytes { get; set; } = [];
    public string? FilePath { get; set; }
    public bool Dirty { get; set; }
    public string ActivePattern { get; set; } = "";
    public string SelectedInstrument { get; set; } = "";
    public string Workspace { get; set; } = "Tracker";
}
[JsonSerializable(typeof(UpdateSession))]
internal partial class SessionJson : JsonSerializerContext { }
public static class UpdateRecovery
{
    public static void Save(SongEditor editor, string? filePath, string activePattern, string selectedInstrument, string workspace, string workDirectory)
    {
        UpdatePaths.CreatePrivateDirectory(workDirectory);
        var session = new UpdateSession { SongBytes = SongFile.Write(editor.Song), FilePath = filePath is null ? null : Path.GetFullPath(filePath), Dirty = editor.IsDirty, ActivePattern = activePattern, SelectedInstrument = selectedInstrument, Workspace = workspace };
        // Keep a plain song too: it remains manually recoverable even if session metadata cannot be read by a later build.
        AtomicWrite(Path.Combine(workDirectory, "resume.song"), session.SongBytes);
        AtomicWrite(Path.Combine(workDirectory, "resume.json"), JsonSerializer.SerializeToUtf8Bytes(session, SessionJson.Default.UpdateSession));
    }
    public static UpdateSession Read(string workDirectory)
    {
        var file = Path.Combine(workDirectory, "resume.json"); UpdatePaths.EnsureNoLinks(file); UpdatePaths.CheckOwnedEntry(file);
        if (new FileInfo(file).Length > 96L * 1024 * 1024) throw new InvalidDataException("Recovery snapshot exceeds its size limit.");
        var session = JsonSerializer.Deserialize(File.ReadAllBytes(file), SessionJson.Default.UpdateSession) ?? throw new InvalidDataException("The update recovery snapshot is empty.");
        SongFile.Read(session.SongBytes); // Validate before the UI changes its active song. Never consume the snapshot on read.
        if (session.FilePath is not null && !Path.IsPathFullyQualified(session.FilePath)) throw new InvalidDataException("Invalid recovery song path.");
        return session;
    }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        UpdatePaths.EnsureNoLinks(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
