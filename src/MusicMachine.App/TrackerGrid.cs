using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using MusicMachine.Core;
namespace MusicMachine.App;

// A single virtual drawing surface keeps a 256-row, 32-track pattern light.
// Text entry is buffered until commit, so typing never makes partial invalid edits.
public sealed class TrackerGrid : Control
{
    private Song song = new();
    private Pattern? pattern;
    private int row, column, anchorRow, anchorColumn;
    private string? edit;
    public int EffectColumns { get; set; } = 2;
    public int SelectedRow => row;
    public int SelectedTrack => Math.Clamp(column / (EffectColumns + 1), 0, Math.Max(0, song.Tracks.Count - 1));
    public int SelectedColumn => column % (EffectColumns + 1);
    public int PlaybackRow { get; set; } = -1;
    public event Action<Action<Song>>? Change;
    public event Action<string>? Status;
    public event Action? EditChanged;
    public event Action<int, int>? SelectionChanged;
    public event Action<int>? TrackHeaderClicked;
    public event Action<int>? TrackHeaderContextRequested;
    public event Action<string>? InstrumentHeaderClicked;
    public const double RowHeight = 20, HeaderHeight = 34, Gutter = 40;
    private double MinimumTrackWidth => 72 + EffectColumns * 38;
    private double TrackWidth => Math.Max(MinimumTrackWidth, (Bounds.Width - Gutter) / Math.Max(1, song.Tracks.Count));
    public TrackerGrid()
    {
        Focusable = true; ClipToBounds = true;
        Avalonia.Automation.AutomationProperties.SetName(this, "Pattern editor. Type notes, use arrows to navigate, Enter to commit, Escape to cancel, F2 for FX help, Delete to clear.");
    }
    public void SetSong(Song value, string patternId)
    {
        var changedPattern = pattern?.Id != patternId; song = value; pattern = song.FindPattern(patternId); if (changedPattern) ClearEdit();
        row = Math.Clamp(row, 0, Math.Max(0, (pattern?.Length ?? 1) - 1));
        column = Math.Clamp(column, 0, Math.Max(0, song.Tracks.Count * (EffectColumns + 1) - 1));
        anchorRow = Math.Clamp(anchorRow, 0, Math.Max(0, (pattern?.Length ?? 1) - 1)); anchorColumn = Math.Clamp(anchorColumn, 0, Math.Max(0, song.Tracks.Count * (EffectColumns + 1) - 1));
        Width = double.NaN; MinWidth = Gutter + song.Tracks.Count * MinimumTrackWidth;
        Height = HeaderHeight + (pattern?.Length ?? 0) * RowHeight;
        InvalidateVisual();
    }
    public void Select(int r, int track, int col = 0)
    {
        row = Math.Clamp(r, 0, Math.Max(0, (pattern?.Length ?? 1) - 1));
        column = Math.Clamp(track * (EffectColumns + 1) + col, 0, Math.Max(0, song.Tracks.Count * (EffectColumns + 1) - 1));
        anchorRow = row; anchorColumn = column; ClearEdit(); SelectionChanged?.Invoke(row, SelectedTrack); InvalidateVisual();
    }
    private double CellX(int col) => Gutter + col / (EffectColumns + 1) * TrackWidth + (col % (EffectColumns + 1) == 0 ? 0 : 72 + (col % (EffectColumns + 1) - 1) * 38);
    private NoteEvent? Current => pattern?.Tracks.FirstOrDefault(t => t.TrackId == song.Tracks.ElementAtOrDefault(SelectedTrack)?.Id)?.Rows.ElementAtOrDefault(row);
    public string CurrentText => SelectedColumn == 0 ? Current is null ? "" : NoteParser.Format(Current) : Current?.Effects.ElementAtOrDefault(SelectedColumn - 1) ?? "";
    public string EditText => edit ?? CurrentText;
    public bool HasPendingEdit => edit is not null;
    public string? EditError { get; private set; }
    public bool CommitPending() => Commit();
    public void CancelPendingEdit()
    {
        if (edit is null) return;
        ClearEdit(); Status?.Invoke("Edit cancelled · original cell kept"); InvalidateVisual();
    }
    private void ClearEdit() { edit = null; EditError = null; ToolTip.SetTip(this, null); EditChanged?.Invoke(); }
    private bool RejectEdit(string error, string attemptedText)
    {
        // A rejected reference/paste replacement must not mislabel the draft still shown.
        if (edit is not null && string.Equals(edit.Trim(), attemptedText.Trim(), StringComparison.OrdinalIgnoreCase)) EditError = error;
        Status?.Invoke($"{(SelectedColumn == 0 ? "Note" : "FX")} not saved · Esc cancels{(SelectedColumn > 0 ? " · F2 for FX help" : "")} · {error}");
        EditChanged?.Invoke(); InvalidateVisual(); return false;
    }
    private void UpdateTypingStatus()
    {
        EditError = null; ToolTip.SetTip(this, null); EditChanged?.Invoke();
        Status?.Invoke($"Typing {(SelectedColumn == 0 ? "note" : "FX")} {edit} · Enter saves · Esc cancels{(SelectedColumn > 0 ? " · F2 for help" : "")}");
    }
    private string? FinishForNavigation()
    {
        if (edit is null || Commit()) return null;
        // Navigation is always an escape route. Only a rejected draft is discarded;
        // valid edits are committed above and the original saved cell is never cleared.
        var rejected = edit; var cell = SelectedColumn == 0 ? "note" : "FX";
        ClearEdit();
        var notice = $"Discarded invalid {cell} \u201c{rejected}\u201d · original cell kept{(cell == "FX" ? " · F2 for FX help" : "")}";
        Status?.Invoke(notice); return notice;
    }
    public bool CommitText(string text)
    {
        if (pattern is null || song.Tracks.Count == 0) return false;
        var pid = pattern.Id; var tid = song.Tracks[SelectedTrack].Id; var r = row; var cellColumn = SelectedColumn;
        if (cellColumn == 0)
        {
            int octave = 4;
            var notes = pattern.Tracks.FirstOrDefault(t => t.TrackId == tid)?.Rows;
            for (int i = r - 1; i >= 0; i--) if (notes?.ElementAtOrDefault(i) is { Kind: NoteKind.Note } prev) { octave = prev.Pitch / 12 - 1; break; }
            if (!NoteParser.TryParse(text, octave, out var note, out var error)) return RejectEdit(error, text);
            ClearEdit(); Change?.Invoke(s => { var dest = s.FindPattern(pid)!.GetTrack(tid).Rows[r]; dest.Kind = note.Kind; dest.Pitch = note.Pitch; dest.Timing = note.Timing; });
        }
        else
        {
            var normalized = text.Trim().ToUpperInvariant();
            if (!FxParser.TryParse(normalized, out _, out var error)) return RejectEdit(error, text);
            var existing = Current?.Effects.ToList() ?? [];
            while (existing.Count < cellColumn) existing.Add("");
            existing[cellColumn - 1] = normalized;
            try { FxParser.Validate(existing); } catch (Exception e) { return RejectEdit(e.Message, text); }
            ClearEdit(); Change?.Invoke(s => s.FindPattern(pid)!.GetTrack(tid).Rows[r].Effects = existing);
        }
        Status?.Invoke($"Row {row + 1:00} · {song.Tracks[SelectedTrack].Name} · {(string.IsNullOrWhiteSpace(text) ? "cleared" : text.Trim().ToUpperInvariant())}"); InvalidateVisual(); return true;
    }
    private bool Commit() => edit is null || CommitText(edit);
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (pattern is null || song.Tracks.Count == 0) return;
        var p = e.GetPosition(this); if (p.X < Gutter) return;
        var t = Math.Clamp((int)((p.X - Gutter) / TrackWidth), 0, song.Tracks.Count - 1);
        if (p.Y < HeaderHeight)
        {
            var notice = FinishForNavigation(); TrackHeaderClicked?.Invoke(t);
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) TrackHeaderContextRequested?.Invoke(t);
            if (notice is not null) Status?.Invoke(notice);
            return;
        }
        var inner = p.X - Gutter - t * TrackWidth;
        var c = inner < 72 ? 0 : Math.Min(EffectColumns, 1 + (int)((inner - 72) / 38));
        var targetRow = Math.Clamp((int)((p.Y - HeaderHeight) / RowHeight), 0, pattern.Length - 1);
        // Clicking the active draft returns keyboard focus without appending or erasing it.
        if (edit is not null && targetRow == row && t == SelectedTrack && c == SelectedColumn)
        { Focus(); e.Handled = true; return; }
        var navigationNotice = FinishForNavigation();
        var ar = anchorRow; var ac = anchorColumn; Select((int)((p.Y - HeaderHeight) / RowHeight), t, c); if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { anchorRow = ar; anchorColumn = ac; } Focus();
        if ((p.Y - HeaderHeight) % RowHeight < 11 && Current?.InstrumentId is { Length: > 0 } id) InstrumentHeaderClicked?.Invoke(id);
        if (e.ClickCount == 2) { edit = CurrentText; UpdateTypingStatus(); }
        if (navigationNotice is not null) Status?.Invoke(navigationNotice);
        e.Handled = true;
    }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        edit = (edit ?? "") + e.Text; if (edit.Length > 20) edit = edit[..20];
        UpdateTypingStatus(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.C) { _ = Copy(); e.Handled = true; }
            if (e.Key == Key.V) { _ = Paste(); e.Handled = true; }
            return;
        }
        var ar = anchorRow; var ac = anchorColumn;
        switch (e.Key)
        {
            case Key.Escape: CancelPendingEdit(); break;
            case Key.Delete: ClearEdit(); CommitText(""); break;
            case Key.Back: if (edit is null) edit = ""; else if (edit.Length > 0) edit = edit[..^1]; UpdateTypingStatus(); break;
            case Key.Enter: if (Commit()) Move(1, 0); break;
            case Key.Tab: FinishForNavigation(); Move(0, (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1) * (EffectColumns + 1)); break;
            case Key.Space: if (edit is null) return; edit += " "; UpdateTypingStatus(); break;
            case Key.Up: FinishForNavigation(); Move(-1, 0); break;
            case Key.Down: FinishForNavigation(); Move(1, 0); break;
            case Key.Left: FinishForNavigation(); Move(0, -1); break;
            case Key.Right: FinishForNavigation(); Move(0, 1); break;
            case Key.Home: FinishForNavigation(); Select(0, SelectedTrack, SelectedColumn); break;
            case Key.End: FinishForNavigation(); Select((pattern?.Length ?? 1) - 1, SelectedTrack, SelectedColumn); break;
            case Key.PageDown: FinishForNavigation(); Move(16, 0); break;
            case Key.PageUp: FinishForNavigation(); Move(-16, 0); break;
            default: return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { anchorRow = ar; anchorColumn = ac; } else { anchorRow = row; anchorColumn = column; }
        Focus(); Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus(), Avalonia.Threading.DispatcherPriority.Background); InvalidateVisual(); e.Handled = true;
    }
    private void Move(int r, int c)
    {
        row = Math.Clamp(row + r, 0, (pattern?.Length ?? 1) - 1);
        column = Math.Clamp(column + c, 0, Math.Max(0, song.Tracks.Count * (EffectColumns + 1) - 1));
        ClearEdit(); SelectionChanged?.Invoke(row, SelectedTrack); this.BringIntoView(new Rect(CellX(column), HeaderHeight + row * RowHeight, 90, RowHeight));
    }
    public Task CopySelectionAsync() => Copy();
    public Task PasteSelectionAsync() => Paste();
    private async Task Copy()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard is null || pattern is null) return;
        var lines = new List<string>();
        for (int r = Math.Min(row, anchorRow); r <= Math.Max(row, anchorRow); r++)
        {
            var cells = new List<string>();
            for (int c = Math.Min(column, anchorColumn); c <= Math.Max(column, anchorColumn); c++)
            {
                var note = pattern.GetTrack(song.Tracks[c / (EffectColumns + 1)].Id).Rows[r]; var part = c % (EffectColumns + 1);
                cells.Add(part == 0 ? NoteParser.Format(note) : note.Effects.ElementAtOrDefault(part - 1) ?? "");
            }
            lines.Add(string.Join('\t', cells));
        }
        await clipboard.SetTextAsync(string.Join('\n', lines)); Status?.Invoke("Copied selected cells");
    }
    private async Task Paste()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard is null) return;
        var text = await clipboard.TryGetTextAsync(); if (text is not null) PasteText(text);
    }
    public bool PasteText(string text)
    {
        if (pattern is null || text.Length > 65536) return false;
        try
        {
            var copy = SongFile.Clone(song); var target = copy.FindPattern(pattern.Id)!;
            var lines = text.Replace("\r", "").TrimEnd('\n').Split('\n');
            if (row + lines.Length > target.Length) throw new ArgumentException("Paste extends beyond this pattern. Add rows first.");
            for (int y = 0; y < lines.Length; y++)
            {
                var cells = lines[y].Split('\t');
                if (column + cells.Length > song.Tracks.Count * (EffectColumns + 1)) throw new ArgumentException("Paste extends beyond visible columns. Add tracks or FX first.");
                for (int x = 0; x < cells.Length; x++)
                {
                    int c = column + x, r = row + y; var notes = target.GetTrack(song.Tracks[c / (EffectColumns + 1)].Id).Rows; var dest = notes[r]; var part = c % (EffectColumns + 1);
                    if (part == 0)
                    {
                        int octave = 4; for (int prev = r - 1; prev >= 0; prev--) if (notes[prev].Kind == NoteKind.Note) { octave = NoteParser.GetOctave(notes[prev]); break; }
                        if (!NoteParser.TryParse(cells[x], octave, out var n, out var error)) throw new ArgumentException($"Row {r + 1}: {error}");
                        dest.Kind = n.Kind; dest.Pitch = n.Pitch; dest.Timing = n.Timing;
                    }
                    else { while (dest.Effects.Count < part) dest.Effects.Add(""); dest.Effects[part - 1] = cells[x].Trim().ToUpperInvariant(); }
                }
            }
            SongFile.Validate(copy); ClearEdit(); Change?.Invoke(s => s.Patterns = copy.Patterns); Focus(); Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus(), Avalonia.Threading.DispatcherPriority.Background); Status?.Invoke($"Pasted {lines.Length} rows · Ctrl+Z undoes the complete paste"); return true;
        }
        catch (Exception e) { Status?.Invoke(e.Message); return false; }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var p = e.GetPosition(this); if (pattern is null || p.X < Gutter || p.Y < HeaderHeight) return;
        var t = (int)((p.X - Gutter) / TrackWidth); var r = (int)((p.Y - HeaderHeight) / RowHeight); if (t >= song.Tracks.Count || r >= pattern.Length) return;
        var inner = p.X - Gutter - t * TrackWidth;
        var c = inner < 72 ? 0 : Math.Min(EffectColumns, 1 + (int)((inner - 72) / 38));
        if (r == row && t == SelectedTrack && c == SelectedColumn && EditError is not null)
        {
            ToolTip.SetTip(this, $"{EditError} · Correct the text and press Enter, or Esc to cancel{(c > 0 ? "; F2 opens FX help" : "")}");
            return;
        }
        var n = pattern.Tracks.FirstOrDefault(x => x.TrackId == song.Tracks[t].Id)?.Rows.ElementAtOrDefault(r); if (n is null) return;
        var timing = n.Timing switch { NoteTiming.TripletEighth => " · eighth-note triplet retriggers while held", NoteTiming.TripletSixteenth => " · sixteenth-note triplet retriggers while held", NoteTiming.Swing => " · delayed on odd rows by the song swing amount", _ => "" };
        var description = n.Kind switch { NoteKind.Empty => "Sustain the previous note", NoteKind.Off => "Release the envelope", NoteKind.Cut => "Silence immediately", _ => NoteParser.Format(n) + timing };
        if (n.InstrumentId is { Length: > 0 } id) description += " · instrument section: " + song.FindInstrument(id)?.Name;
        ToolTip.SetTip(this, description + " · Shift-click selects a block; Ctrl+C / Ctrl+V copy and paste");
    }
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx); ctx.FillRectangle(Ui.Background, new Rect(Bounds.Size)); if (pattern is null) return;
        var beat = Math.Max(.25, song.RowsPerBeat * 4.0 / song.BeatUnit); var bar = beat * song.BeatsPerBar;
        ctx.FillRectangle(Ui.Surface, new Rect(0, 0, Bounds.Width, HeaderHeight));
        ctx.DrawText(Ui.Fmt("ROW", 9, Ui.Muted, true), new Point(8, 17));
        for (int t = 0; t < song.Tracks.Count; t++)
        {
            var track = song.Tracks[t]; var x = Gutter + t * TrackWidth; var color = Ui.ThemeBrush(track.Color);
            ctx.FillRectangle(Ui.Line, new Rect(x, 0, TrackWidth - 1, 1));
            ctx.DrawText(Ui.Fmt($"{t + 1:00}  {track.Name}", 11, t == SelectedTrack ? Ui.Accent : Ui.Muted), new Point(x + 8, 6));
            ctx.DrawText(Ui.Fmt("NOTE", 8, Ui.Muted, true), new Point(x + 8, 22));
            for (int c = 0; c < EffectColumns; c++) ctx.DrawText(Ui.Fmt($"FX {c + 1}", 8, Ui.Muted, true), new Point(x + 76 + c * 38, 22));
        }
        for (int r = 0; r < pattern.Length; r++)
        {
            var y = HeaderHeight + r * RowHeight;
            if (r % beat == 0) ctx.FillRectangle(Ui.ThemeBrush(r % bar == 0 ? "#1C2B3D" : "#162230"), new Rect(0, y, Bounds.Width, RowHeight));
            if (r == PlaybackRow) ctx.FillRectangle(Ui.ThemeBrush("#235449"), new Rect(0, y, Bounds.Width, RowHeight));
            ctx.DrawText(Ui.Fmt($"{r + 1:00}", 11, r % beat == 0 ? Ui.Text : Ui.Muted, true), new Point(12, y + 3));
            ctx.DrawLine(new Pen(Ui.Line, .35), new Point(0, y + RowHeight), new Point(Bounds.Width, y + RowHeight));
            for (int t = 0; t < song.Tracks.Count; t++)
            {
                var track = song.Tracks[t]; var x = Gutter + t * TrackWidth;
                var n = pattern.Tracks.FirstOrDefault(pt => pt.TrackId == track.Id)?.Rows.ElementAtOrDefault(r);
                ctx.DrawLine(new Pen(Ui.Line, 1), new Point(x, y), new Point(x, y + RowHeight));
                for (int c = 0; c <= EffectColumns; c++)
                {
                    var col = t * (EffectColumns + 1) + c; var cx = CellX(col); var selected = r >= Math.Min(row, anchorRow) && r <= Math.Max(row, anchorRow) && col >= Math.Min(column, anchorColumn) && col <= Math.Max(column, anchorColumn);
                    var activeEdit = r == row && col == column && edit is not null;
                    if (selected) { ctx.FillRectangle(Ui.Selection, new Rect(cx + 1, y + 1, (c == 0 ? 71 : 37) - 2, RowHeight - 2)); ctx.DrawRectangle(new Pen(activeEdit && EditError is not null ? Ui.Error : Ui.Accent, activeEdit && EditError is not null ? 2 : 1), new Rect(cx + 1, y + 1, (c == 0 ? 71 : 37) - 2, RowHeight - 2)); }
                    var text = c == 0 ? n is null || n.Kind == NoteKind.Empty ? "· · ·" : NoteParser.Format(n) : n?.Effects.ElementAtOrDefault(c - 1) ?? "";
                    if (activeEdit) text = edit + "▏";
                    var brush = activeEdit && EditError is not null ? Ui.Error : c > 0 ? Ui.Muted : n?.Kind is NoteKind.Off or NoteKind.Cut ? Ui.Muted : Ui.Text;
                    ctx.DrawText(Ui.Fmt(text, 11, brush, true), new Point(cx + 8, y + (!string.IsNullOrEmpty(n?.InstrumentId) ? 9 : 3)));
                }
                if (!string.IsNullOrEmpty(n?.InstrumentId)) { ctx.FillRectangle(Ui.ThemeBrush("#47362A"), new Rect(x + 1, y, TrackWidth - 2, 8)); ctx.DrawText(Ui.Fmt(song.FindInstrument(n.InstrumentId)?.Name ?? "Instrument", 7, Ui.Muted), new Point(x + 7, y)); }
            }
        }
    }
}
