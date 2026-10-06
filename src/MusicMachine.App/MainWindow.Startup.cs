using MusicMachine.Core;

namespace MusicMachine.App;

public sealed partial class MainView
{
    private readonly IProjectStorage projectStorage;
    private readonly ProjectStartupService projectStartup;
    private readonly SemaphoreSlim recoveryWrites = new(1, 1);
    private string sessionRecoveryKey = NewRecoveryKey();
    private bool projectChangeBusy, saveBusy;
    private static string NewRecoveryKey() => "recovery-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

    private void SetProjectInputEnabled(bool enabled) { if (OverlayRoot.Children.Count > 0) OverlayRoot.Children[0].IsEnabled = enabled; }
    private async Task InitializeProjectAsync(string? initialPath)
    {
        projectChangeBusy = true; SetProjectInputEnabled(false);
        try { await RestoreStartupProjectAsync(initialPath); }
        finally { projectChangeBusy = false; SetProjectInputEnabled(true); RestoreInputFocus(); }
    }
    private async Task RestoreStartupProjectAsync(string? initialPath)
    {
        // Update resume includes dirty state and workspace, and always takes precedence.
        if (await RestoreUpdateSession()) { sessionRecoveryKey = "song"; return; }
        var startup = await projectStartup.ResolveAsync(initialPath);
        SetCurrentSong(startup.Song, startup.Path);
        if (startup.Notice is not null) SetStatus(startup.Notice);
        if (initialPath is not null)
        {
            var notice = startup.Notice ?? "Ready";
            if (startup.Path is not null && await RememberCurrentProjectAsync() is { } persistenceNotice) notice += " · " + persistenceNotice;
            try { if ((await RecoveryKeysAsync()).Length > 0) notice += " · Unsaved recovery kept in File → Recover unsaved song"; }
            catch (Exception ex) { notice += " · Recovery unavailable: " + ex.Message; }
            SetStatus(notice); return;
        }
        var recovered = await OfferRecoveryAsync();
        if (!recovered && startup.Notice is not null && status.Text != startup.Notice) SetStatus(startup.Notice + " · " + status.Text);
    }

    private void SetCurrentSong(Song song, string? path, bool dirty = false)
    {
        Stop(); editor.Load(song); if (dirty) editor.MarkUnsaved();
        filePath = path; activePattern = editor.Song.Patterns[0].Id; selectedInstrument = editor.Song.Instruments.FirstOrDefault()?.Id ?? ""; chosenTrack = 0;
        SynchronizeDrumSound(); Refresh();
    }
    private async Task<string?> RememberCurrentProjectAsync(Song? savedSong = null, string? savedPath = null)
    {
        try { await projectStartup.RememberAsync(savedSong ?? editor.Song, savedSong is null ? filePath : savedPath); return null; }
        catch (Exception ex) { var message = "Could not remember this project for next launch: " + ex.Message; SetStatus(message); return message; }
    }
    private async Task SaveRecoverySnapshotAsync()
    {
        recoveryTimer.Stop();
        if (!editor.IsDirty) { await ClearRecoveryAsync(); return; }
        var key = sessionRecoveryKey; var bytes = SongFile.Write(editor.Song);
        await recoveryWrites.WaitAsync();
        try { await projectStorage.WriteAsync(key, bytes); }
        catch (Exception ex) { SetStatus("Recovery save unavailable; save a .song copy: " + ex.Message); }
        finally { recoveryWrites.Release(); }
    }
    private Task ClearRecoveryAsync() => RemoveRecoveryAsync(sessionRecoveryKey);
    private async Task RemoveRecoveryAsync(string key)
    {
        if (updateRestartApproved) return;
        recoveryTimer.Stop();
        await recoveryWrites.WaitAsync();
        try { await projectStorage.DeleteAsync(key); }
        catch (Exception ex) { SetStatus("Recovery cleanup: " + ex.Message); }
        finally { recoveryWrites.Release(); }
    }
    private async Task WriteUpdateRecoveryAsync(byte[] bytes)
    {
        recoveryTimer.Stop(); await recoveryWrites.WaitAsync();
        try
        {
            var previous = await projectStorage.ReadAsync("song");
            if (sessionRecoveryKey != "song" && previous is not null && !previous.AsSpan().SequenceEqual(bytes))
                await projectStorage.WriteAsync(NewRecoveryKey(), previous);
            await projectStorage.WriteAsync("song", bytes);
            if (sessionRecoveryKey != "song") await projectStorage.DeleteAsync(sessionRecoveryKey);
            sessionRecoveryKey = "song";
        }
        finally { recoveryWrites.Release(); }
    }
    private async Task<string[]> RecoveryKeysAsync()
    {
        var keys = (await projectStorage.ListAsync("recovery-")).OrderDescending(StringComparer.Ordinal).ToList();
        if (await projectStorage.ReadAsync("song") is not null) keys.Insert(0, "song");
        return keys.ToArray();
    }
    private async Task<bool> OfferRecoveryAsync(string? excludedKey = null)
    {
        try
        {
            var keys = (await RecoveryKeysAsync()).Where(key => key != excludedKey).ToArray();
            if (keys.Length == 0) { if (excludedKey is not null) SetStatus("No other unsaved recovery copies were found"); return false; }
            var invalid = 0;
            foreach (var key in keys)
            {
                Song recovered;
                try { var bytes = await projectStorage.ReadAsync(key); if (bytes is null) continue; recovered = SongFile.Read(bytes); }
                catch { invalid++; continue; }
                var answer = await Ask("Recover your song?", $"An unsaved recovery copy of {recovered.Title} was found. Recover it now, or keep it for later in File → Recover unsaved song. Your last saved project is stored separately.", keys.Length > 1 ? ["Recover", "Next copy", "Keep for later"] : ["Recover", "Keep for later"]);
                if (answer == "Recover")
                {
                    // Adopt a durable independent copy before consuming the selected one. Two browser
                    // tabs recovering the same snapshot must not share an autosave/cleanup key.
                    var adoptedKey = NewRecoveryKey();
                    await projectStorage.WriteAsync(adoptedKey, SongFile.Write(recovered));
                    string? cleanupNotice = null;
                    try { await projectStorage.DeleteAsync(key); }
                    catch (Exception ex) { cleanupNotice = " · Original recovery also retained: " + ex.Message; }
                    sessionRecoveryKey = adoptedKey; SetCurrentSong(recovered, null, dirty: true);
                    SetStatus("Recovered song · use Save as to keep a named copy" + cleanupNotice); return true;
                }
                if (answer != "Next copy") { SetStatus("Unsaved recovery kept in File → Recover unsaved song"); return false; }
            }
            SetStatus(invalid > 0 ? "Some recovery copies could not be read; they were kept. The current project is unchanged." : "Recovery copies kept; the current project is unchanged");
        }
        catch (Exception ex) { SetStatus("Recovery unavailable: " + ex.Message); }
        return false;
    }
    private async Task RecoverSong()
    {
        if (projectChangeBusy || saveBusy) return;
        projectChangeBusy = true; SetProjectInputEnabled(false);
        try
        {
            if (!await ConfirmDiscard()) return;
            // Preserve current work until another copy has actually been chosen.
            var previousKey = sessionRecoveryKey;
            if (editor.IsDirty) await SaveRecoverySnapshotAsync();
            if (await OfferRecoveryAsync(previousKey) && previousKey != sessionRecoveryKey) await RemoveRecoveryAsync(previousKey);
        }
        finally { projectChangeBusy = false; SetProjectInputEnabled(true); RestoreInputFocus(); }
    }
    private async Task LoadDemo()
    {
        if (projectChangeBusy || saveBusy) return;
        projectChangeBusy = true; SetProjectInputEnabled(false);
        try
        {
            if (!await ConfirmDiscard()) return;
            await ClearRecoveryAsync(); sessionRecoveryKey = NewRecoveryKey();
            SetCurrentSong(DemoSong.Create(), null, dirty: true);
            // The demo is an explicit unsaved document; never make it the clean startup target.
            await SaveRecoverySnapshotAsync(); SetStatus("Demo song · use Save as to keep a copy");
        }
        finally { projectChangeBusy = false; SetProjectInputEnabled(true); RestoreInputFocus(); }
    }
}
