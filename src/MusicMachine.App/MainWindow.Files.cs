using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MusicMachine.Core;
using MusicMachine.Audio;
namespace MusicMachine.App;
public sealed partial class MainView
{
    private static readonly FilePickerFileType SongType = new("MusicMachine song") { Patterns = ["*.song"] };
    private static readonly FilePickerFileType InstrumentType = new("MusicMachine instrument") { Patterns = ["*.instrument"] };
    private async Task<ProjectFileSelection?> PickSongToSaveAsync()
    {
        var name = SafeName(editor.Song.Title) + ".song";
        if (suppliedProjectFiles is not null) return await suppliedProjectFiles.SaveAsync(name);
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save song", SuggestedFileName = name, DefaultExtension = "song", FileTypeChoices = [SongType], ShowOverwritePrompt = true });
        return file is null ? null : ProjectFileSelection.From(file);
    }
    private async Task<ProjectFileSelection?> PickSongToOpenAsync()
    {
        if (suppliedProjectFiles is not null) return await suppliedProjectFiles.OpenAsync();
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Open song", AllowMultiple = false, FileTypeFilter = [SongType] });
        return files.Count == 0 ? null : ProjectFileSelection.From(files[0]);
    }
    private async Task<bool> SaveSong(bool saveAs = false)
    {
        if (saveBusy || !tracker.CommitPending() || !CommitEditorFields()) return false;
        saveBusy = true; SetProjectInputEnabled(false);
        try
        {
            ProjectFileSelection? file = null; var destination = filePath;
            if (saveAs || filePath is null || projectStorage.UsesProjectSnapshots)
            {
                file = await PickSongToSaveAsync();
                if (file is null) { SetStatus("Save cancelled"); return false; }
                destination = projectStorage.UsesProjectSnapshots ? null : file.LocalPath;
            }
            var snapshot = SongFile.Clone(editor.Song); var revision = editor.Revision;
            if (destination is not null && !projectStorage.UsesProjectSnapshots) SongFile.Save(destination, snapshot);
            else if (file is not null)
            {
                await using var output = await file.OpenWriteAsync();
                if (output.CanSeek) output.SetLength(0);
                await output.WriteAsync(SongFile.Write(snapshot)); await output.FlushAsync();
            }
            else return false;
            // Closing/flushing the selected destination must succeed before changing saved state.
            filePath = destination;
            if (editor.Revision == revision) { editor.MarkSaved(); await ClearRecoveryAsync(); }
            var notice = await RememberCurrentProjectAsync(snapshot, destination);
            Refresh(); SetStatus("Saved " + (file?.Name ?? destination) + (notice is null ? "" : " · " + notice)); return true;
        }
        catch (Exception e) { SetStatus("Save failed: " + e.Message); return false; }
        finally { saveBusy = false; if (!projectChangeBusy) SetProjectInputEnabled(true); RestoreInputFocus(); }
    }
    private async Task OpenSong()
    {
        if (projectChangeBusy || saveBusy) return;
        projectChangeBusy = true; SetProjectInputEnabled(false);
        try
        {
            if (!await ConfirmDiscard()) return;
            var file = await PickSongToOpenAsync();
            if (file is null) { SetStatus("Open cancelled"); return; }
            if (!projectStorage.UsesProjectSnapshots && file.LocalPath is { } path) await OpenPath(path);
            else
            {
                Song song;
                await using (var stream = await file.OpenReadAsync()) { song = await SongFile.ReadAsync(stream); }
                await LoadSong(song, null, file.Name);
            }
        }
        catch (Exception e) { SetStatus("Could not open song: " + e.Message); }
        finally { projectChangeBusy = false; SetProjectInputEnabled(true); RestoreInputFocus(); }
    }
    private async Task OpenPath(string path)
    {
        try { var full = Path.GetFullPath(path); var song = SongFile.Load(full); await LoadSong(song, full, Path.GetFileName(full)); }
        catch (Exception e) { SetStatus("Could not open song: " + e.Message); }
    }
    private async Task LoadSong(Song song, string? path, string name)
    {
        SongFile.Validate(song);
        await ClearRecoveryAsync(); sessionRecoveryKey = NewRecoveryKey();
        SetCurrentSong(song, path);
        var notice = await RememberCurrentProjectAsync();
        SetStatus("Opened " + name + (notice is null ? "" : " · " + notice));
    }
    private async Task NewSong()
    {
        if (projectChangeBusy || saveBusy) return;
        projectChangeBusy = true; SetProjectInputEnabled(false);
        try
        {
            if (!await ConfirmDiscard()) return;
            await projectStartup.ResetAsync();
            await ClearRecoveryAsync(); sessionRecoveryKey = NewRecoveryKey();
            SetCurrentSong(DemoSong.CreateEmpty(), null);
            SetStatus("New song · type notes, add a beat, then arrange your patterns");
        }
        catch (Exception ex) { SetStatus("Could not start a new song: " + ex.Message); }
        finally { projectChangeBusy = false; SetProjectInputEnabled(true); RestoreInputFocus(); }
    }
    private async Task<bool> ConfirmDiscard()
    {
        if (!tracker.CommitPending() || !CommitEditorFields()) return false;
        if (!editor.IsDirty) return true;
        var answer = await Ask("Save your changes?", $"{editor.Song.Title} has unsaved changes.", "Save", "Discard", "Cancel");
        return answer == "Discard" || answer == "Save" && await SaveSong();
    }
    public async Task<bool> RequestCloseAsync()
    {
        if (exportBusy) { SetStatus("An export is running. Finish or cancel it before closing."); return false; }
        if (updateRestartApproved) return true;
        if (projectChangeBusy || saveBusy) { SetStatus("Finish or cancel the current file operation before closing."); return false; }
        updateOperation?.Cancel();
        // A close during extraction/handshake first cancels restart. It must never race update approval.
        if (updateInstalling) { updateMessage = "Cancelling restart…"; RefreshUpdateControls(); return false; }
        var close = await ConfirmDiscard();
        if (close) { StopUpdates(); await ClearRecoveryAsync(); }
        return close;
    }
    private async Task<string?> Ask(string title, string message, params string[] choices)
    {
        var dialog = new EditorDialog { Title = title, Width = 450, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin = new(24), Spacing = 16 }; stack.Children.Add(Ui.Label(title, 19)); stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted });
        var row = Ui.Row(); row.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var choice in choices) row.Children.Add(Ui.Button(choice, () => dialog.Close(choice), accent: choice == choices[0]));
        stack.Children.Add(row); dialog.Content = stack; return await dialog.ShowDialog<string?>(this);
    }
    private async Task ExportAudio()
    {
        if (!tracker.CommitPending() || !CommitEditorFields()) return;
        if (exportBusy) { SetStatus("An export is already running"); return; }
        var choice = await Ask("Export game audio", "WAV is lossless and imports directly into Godot. FLAC is lossless and compact; check your game engine’s codec support. QOA is compact and lossy; check your Godot version or decoder. All three are built in, including in the browser, and export the complete arrangement at 48 kHz stereo.", OperatingSystem.IsBrowser() ? ["WAV", "FLAC", "QOA", "Cancel"] : ["WAV", "FLAC", "QOA", "FFmpeg…", "Cancel"]);
        if (choice == "FFmpeg…") { await ExportWithFfmpeg(); return; }
        if (choice is not ("WAV" or "FLAC" or "QOA")) return;
        var ext = choice.ToLowerInvariant(); var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Export " + choice, SuggestedFileName = SafeName(editor.Song.Title) + "." + ext, DefaultExtension = ext, FileTypeChoices = [new(choice) { Patterns = ["*." + ext] }], ShowOverwritePrompt = true });
        if (file is null) { SetStatus("Export cancelled"); return; }
        var local = OperatingSystem.IsBrowser() ? null : file.TryGetLocalPath();
        var path = local ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "." + ext);
        var snapshot = SongFile.Clone(editor.Song); exportBusy = true; SetStatus("Rendering " + choice + "…");
        using var cancellation = new CancellationTokenSource();
        bool finalizing = false;
        var progress = ExportProgress(choice, cancellation, out var beginFinalizing); var showing = progress.ShowDialog(this);
        try
        {
            if (choice == "FLAC") await OfflineExporter.WriteFlacAsync(path, snapshot, cancellation.Token);
            else if (choice == "QOA") await OfflineExporter.WriteQoaAsync(path, snapshot, cancellation.Token);
            else await OfflineExporter.WriteWavAsync(path, snapshot, cancellation.Token);
            if (local is null) await CommitExportAsync(path, file.OpenWriteAsync, cancellation.Token, () =>
            {
                finalizing = true; beginFinalizing(); SetStatus("Finalizing " + choice + "…");
            });
            SetStatus($"Exported {file.Name} · {new FileInfo(path).Length / 1024:N0} KB · 48 kHz stereo");
        }
        catch (OperationCanceledException) when (!finalizing) { SetStatus("Export cancelled; existing destination preserved"); }
        catch (Exception e) { SetStatus(finalizing ? "Saving failed; destination may be incomplete: " + e.Message : "Export failed: " + e.Message); }
        finally { exportBusy = false; progress.Close(); await showing; if (local is null && File.Exists(path)) File.Delete(path); }
    }
    private static async Task CommitExportAsync(string path, Func<Task<Stream>> openDestination, CancellationToken cancellationToken, Action beginFinalizing)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = File.OpenRead(path);
        cancellationToken.ThrowIfCancellationRequested();
        // IStorageFile exposes only Stream, with no commit/abort contract. Avalonia's browser
        // provider opens createWritable({ keepExistingData: false }) and closes on disposal.
        // After this boundary, complete the bounded copy/close without user cancellation;
        // provider I/O failures still cannot promise rollback and are reported separately.
        beginFinalizing();
        await using var output = await openDestination();
        if (output.CanSeek) output.SetLength(0);
        await input.CopyToAsync(output, 81920, CancellationToken.None);
        await output.FlushAsync(CancellationToken.None);
    }
    private EditorDialog ExportProgress(string format, CancellationTokenSource cancellation, out Action beginFinalizing)
    {
        bool finalizing = false;
        var window = new EditorDialog { Title = "Rendering " + format, Width = 370, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin = new(22), Spacing = 14 };
        var heading = Ui.Label("Rendering " + format + "…", 18); stack.Children.Add(heading);
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5 });
        var detail = Ui.Label("Same synth. Full arrangement. 48 kHz stereo.", 11, Ui.Muted); stack.Children.Add(detail);
        var cancel = Ui.Button("Cancel export", () => { if (!finalizing) cancellation.Cancel(); });
        stack.Children.Add(cancel); window.Content = stack;
        beginFinalizing = () =>
        {
            finalizing = true; cancel.IsEnabled = false; heading.Text = "Finalizing " + format + "…";
            detail.Text = "Finishing the save. Please keep this window open.";
        };
        window.Closing += (_, _) => { if (exportBusy && !finalizing) cancellation.Cancel(); }; return window;
    }
    private async Task ExportWithFfmpeg()
    {
        var config = FfmpegConfigPath;
        var exe = new TextBox { Text = File.Exists(config) ? File.ReadAllText(config) : "", PlaceholderText = "Absolute path to FFmpeg executable" };
        var format = new ComboBox { ItemsSource = new[] { "MP3", "Opus", "Ogg", "M4A" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new EditorDialog { Title = "Optional FFmpeg export", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin = new(24), Spacing = 12 }; stack.Children.Add(Ui.Label("Optional FFmpeg export", 20));
        stack.Children.Add(new TextBlock { Text = "Choose an FFmpeg executable you have installed from a trusted source. MusicMachine does not download or bundle it. Available encoders depend on your FFmpeg build.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted });
        stack.Children.Add(exe); stack.Children.Add(Ui.Button("Browse executable…", () => _ = Pick()));
        async Task Pick() { var picked = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose FFmpeg executable", AllowMultiple = false }); if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) exe.Text = path; }
        stack.Children.Add(format); var error = Ui.Label("", 11, Ui.Orange); stack.Children.Add(error);
        stack.Children.Add(Ui.Row(Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button("Choose output…", () => { if (string.IsNullOrWhiteSpace(exe.Text) || !Path.IsPathFullyQualified(exe.Text) || !File.Exists(exe.Text)) { error.Text = "Choose an existing executable with an absolute path"; return; } dialog.Close(true); }, accent: true))); dialog.Content = stack;
        if (!await dialog.ShowDialog<bool>(this)) return;
        var extension = (format.SelectedItem?.ToString() ?? "MP3").ToLowerInvariant();
        var destination = await StorageProvider.SaveFilePickerAsync(new() { Title = "Export " + extension.ToUpperInvariant(), SuggestedFileName = SafeName(editor.Song.Title) + "." + extension, DefaultExtension = extension, ShowOverwritePrompt = true });
        if (destination?.TryGetLocalPath() is not { } output) return;
        var snapshot = SongFile.Clone(editor.Song); exportBusy = true; using var cancellation = new CancellationTokenSource();
        var progress = ExportProgress(extension.ToUpperInvariant(), cancellation, out _); var showing = progress.ShowDialog(this);
        try { await OfflineExporter.WriteFfmpegAsync(exe.Text!, output, snapshot, cancellation.Token); AtomicWrite(config, System.Text.Encoding.UTF8.GetBytes(exe.Text!)); SetStatus("Exported " + Path.GetFileName(output)); }
        catch (OperationCanceledException) { SetStatus("Export cancelled; existing destination preserved"); }
        catch (Exception e) { SetStatus("FFmpeg export: " + e.Message); }
        finally { exportBusy = false; progress.Close(); await showing; }
    }
    private async Task ExportInstrument()
    {
        if (!tracker.CommitPending() || !CommitEditorFields()) return;
        try
        {
            var ins = editor.Song.FindInstrument(selectedInstrument)!;
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save instrument", SuggestedFileName = SafeName(ins.Name) + ".instrument", DefaultExtension = "instrument", FileTypeChoices = [InstrumentType], ShowOverwritePrompt = true });
            if (file is null) return;
            if (!OperatingSystem.IsBrowser() && file.TryGetLocalPath() is { } path) InstrumentFile.Save(path, ins);
            else { await using var stream = await file.OpenWriteAsync(); if (stream.CanSeek) stream.SetLength(0); await stream.WriteAsync(InstrumentFile.Write(ins)); }
            SetStatus("Saved reusable instrument · " + file.Name);
        }
        catch (Exception e) { SetStatus("Instrument export: " + e.Message); }
    }
    private async Task ImportInstrument()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Import instrument", AllowMultiple = false, FileTypeFilter = [InstrumentType] });
            if (files.Count == 0) return;
            Instrument ins;
            if (!OperatingSystem.IsBrowser() && files[0].TryGetLocalPath() is { } path) ins = InstrumentFile.Load(path);
            else { await using var stream = await files[0].OpenReadAsync(); ins = await InstrumentFile.ReadAsync(stream); } ins.Id = Guid.NewGuid().ToString("N"); Change(s => s.Instruments.Add(ins)); selectedInstrument = ins.Id; Refresh(); SetStatus("Imported a self-contained instrument copy");
        }
        catch (Exception e) { SetStatus("Instrument import: " + e.Message); }
    }
    private Task ShowHelp() => Ask("Make a loop", "1. Select a pattern and click a note cell. Type F, F#, F4 or F#4; Enter commits. Octave follows the nearest earlier note in that track, or 4. Arrows move; Delete clears; Esc cancels. Empty rows sustain. OFF releases; CUT stops. T / TT repeat held notes as eighth / sixteenth triplets; S swings.\n\n2. Press F2 for the FX reference. Search by name, change decimal parameters, or choose an example, then insert. A selected note cell keeps its pitch and adds or updates a same-row effect; a selected FX cell is replaced. Effects happen together, without an extra time slot. Ctrl+I hides the reference. Track → Insert instrument change adds a section header.\n\n3. Drums: click a step, right-click for an accent. Arrangement: append/reuse patterns, mix tracks, click or drag volume points; right-click deletes a point.\n\n4. Sampling: import WAV/QOA, select a region and find a stable cycle. Low-confidence audio can use a manual period. Shape and audition before applying an undoable custom waveform or wavetable frame. The full source recording is never saved in the song.\n\nSpace plays/stops. Ctrl+S saves; Ctrl+Shift+S saves as; Ctrl+O opens; Ctrl+Z undoes; Ctrl+Shift+Z redoes. F1 opens this guide. File → Open demo song opens Neon Orchard. Startup reopens your last saved project, or starts blank. Unsaved recovery is offered separately; New resets the next launch to blank.\n\nWAV/FLAC/QOA export the full arrangement. For smooth game loops, keep start/end levels and sustained notes compatible; loop markers are saved for your arrangement workflow.", "Got it");
    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static void AtomicWrite(string path, byte[] data)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(flushToDisk: true); } File.Move(temp, full, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
