using MusicMachine.Core;
namespace MusicMachine.App;

public partial class MainView
{
    private List<Instrument> instrumentHistory = [];
    private async Task LoadInstrumentHistory()
    {
        try { instrumentHistory = await new InstrumentHistory(projectStorage).LoadAsync(); Refresh(); }
        catch (Exception e) { SetStatus("Instrument history: " + e.Message); }
    }
    private async Task SaveInstrument()
    {
        if (!tracker.CommitPending() || !CommitEditorFields()) return;
        var instrument = editor.Song.FindInstrument(selectedInstrument);
        if (instrument is null) { SetStatus("Create or select an instrument first"); return; }
        try
        {
            await new InstrumentHistory(projectStorage).SaveAsync(instrument);
            await LoadInstrumentHistory(); SetStatus("Saved instrument · " + instrument.Name);
        }
        catch (Exception e) { SetStatus("Instrument save failed: " + e.Message); }
    }
    private bool PreparePlayback()
    {
        if (!tracker.CommitPending() || !CommitEditorFields()) return false;
        var fallback = editor.Song.FindInstrument(selectedInstrument) ?? editor.Song.Instruments.FirstOrDefault();
        if (fallback is null) return true;
        var missing = editor.Song.Tracks.Where(t => string.IsNullOrEmpty(t.InstrumentId)
            && editor.Song.Patterns.Any(p => p.Tracks.Any(part => part.TrackId == t.Id && part.Rows.Any(n => n.Kind == NoteKind.Note))))
            .Select(t => t.Id).ToHashSet();
        if (missing.Count > 0) Change(s =>
        {
            foreach (var track in s.Tracks.Where(t => missing.Contains(t.Id))) track.InstrumentId = fallback.Id;
        });
        return true;
    }
    private void AddAndAssignInstrument(Song song, Instrument instrument)
    {
        var first = song.Instruments.Count == 0; song.Instruments.Add(instrument);
        if (first) foreach (var track in song.Tracks.Where(t => string.IsNullOrEmpty(t.InstrumentId))) track.InstrumentId = instrument.Id;
        if (song.Tracks.Count > 0) song.Tracks[Math.Clamp(chosenTrack, 0, song.Tracks.Count - 1)].InstrumentId = instrument.Id;
    }
    private void AssignTrackInstrument(string id)
    {
        var index = Math.Clamp(chosenTrack, 0, Math.Max(0, editor.Song.Tracks.Count - 1));
        var track = editor.Song.Tracks.ElementAtOrDefault(index);
        if (track is not null && track.InstrumentId != id && editor.Song.FindInstrument(id) is not null)
            Change(s => s.Tracks[index].InstrumentId = id);
        selectedInstrument = id;
    }
    private void ChooseInstrument(string id)
    {
        if (!tracker.CommitPending()) return;
        AssignTrackInstrument(id); OpenInstrument(id);
    }
    private void OpenInstrument(string id)
    {
        if (!tracker.CommitPending()) return;
        selectedInstrument = id; viewSettings.Inspector = true; viewSettings.InspectorContent = "instrument";
        SaveViewSettings(); Refresh();
    }
    private void OpenSampling()
    {
        if (!tracker.CommitPending()) return;
        SelectWorkspace("Sampling");
        _ = samplingPanel.ImportAsync();
    }
    private void UseSavedInstrument(Instrument saved)
    {
        var instrument = InstrumentLibrary.CreateLocalCopy(saved);
        Change(s => AddAndAssignInstrument(s, instrument));
        OpenInstrument(instrument.Id);
    }
}
