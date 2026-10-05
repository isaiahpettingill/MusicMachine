namespace MusicMachine.Core;

/// <summary>Transactional editing with bounded, content-based undo/redo and saved-state tracking.</summary>
public sealed class SongEditor
{
    private const int MaxHistory = 128;
    private const long MaxHistoryBytes = 64L * 1024 * 1024;
    private readonly List<byte[]> _undo = [];
    private readonly List<byte[]> _redo = [];
    private byte[] _saved;
    private byte[] _current;
    public Song Song { get; private set; }
    public long Revision { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => !_current.AsSpan().SequenceEqual(_saved);

    public SongEditor(Song song)
    {
        _current = SongFile.Write(song);
        _saved = _current;
        Song = SongFile.Read(_current);
    }
    public void Change(Action<Song> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var candidate = SongFile.Read(_current);
        change(candidate);
        var next = SongFile.Write(candidate);
        if (_current.AsSpan().SequenceEqual(next)) return;
        Push(_undo, _current);
        _redo.Clear();
        _current = next;
        Song = candidate;
        Revision++;
    }
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Push(_redo, _current);
        Restore(Pop(_undo));
        return true;
    }
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        Push(_undo, _current);
        Restore(Pop(_redo));
        return true;
    }
    public void MarkUnsaved() => _saved = [];
    public void MarkSaved() => _saved = _current;
    public void Load(Song song)
    {
        var data = SongFile.Write(song);
        Song = SongFile.Read(data);
        _current = _saved = data;
        _undo.Clear();
        _redo.Clear();
        Revision++;
    }
    private void Restore(byte[] data) { Song = SongFile.Read(data); _current = data; Revision++; }
    private static byte[] Pop(List<byte[]> list) { var data = list[^1]; list.RemoveAt(list.Count - 1); return data; }
    private static void Push(List<byte[]> list, byte[] data)
    {
        list.Add(data);
        var total = list.Sum(x => (long)x.Length);
        while (list.Count > MaxHistory || (total > MaxHistoryBytes && list.Count > 1)) { total -= list[0].Length; list.RemoveAt(0); }
    }
}
