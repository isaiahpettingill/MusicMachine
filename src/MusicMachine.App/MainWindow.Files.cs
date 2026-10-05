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
    private async Task<bool> SaveSong(bool saveAs = false)
    {
        if (!tracker.CommitPending()) return false;
        try
        {
            IStorageFile? file = null; var destination = filePath;
            if (saveAs || filePath is null || OperatingSystem.IsBrowser())
            {
                file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save song", SuggestedFileName = SafeName(editor.Song.Title) + ".song", DefaultExtension = "song", FileTypeChoices = [SongType], ShowOverwritePrompt = true });
                if (file is null) { SetStatus("Save cancelled"); return false; } destination = file.TryGetLocalPath();
            }
            if (destination is not null && !OperatingSystem.IsBrowser()) SongFile.Save(destination, editor.Song);
            else if (file is not null) { await using var output = await file.OpenWriteAsync(); if (output.CanSeek) output.SetLength(0); await output.WriteAsync(SongFile.Write(editor.Song)); }
            else return false;
            filePath = destination; editor.MarkSaved(); Refresh(); ClearRecovery(); SetStatus("Saved " + (file?.Name ?? destination)); return true;
        }
        catch (Exception e) { SetStatus("Save failed: " + e.Message); return false; }
        finally { RestoreInputFocus(); }
    }
    private async Task OpenSong()
    {
        if (!await ConfirmDiscard()) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Open song", AllowMultiple = false, FileTypeFilter = [SongType] });
            if (files.Count == 0) { SetStatus("Open cancelled"); return; }
            var file = files[0]; if (!OperatingSystem.IsBrowser() && file.TryGetLocalPath() is { } path) await OpenPath(path);
            else { await using var stream = await file.OpenReadAsync(); LoadSong(await SongFile.ReadAsync(stream), null, file.Name); }
        }
        catch (Exception e) { SetStatus("Could not open song: " + e.Message); }
        finally { RestoreInputFocus(); }
    }
    private Task OpenPath(string path)
    {
        try { LoadSong(SongFile.Load(path), path, Path.GetFileName(path)); }
        catch (Exception e) { SetStatus("Could not open song: " + e.Message); } return Task.CompletedTask;
    }
    private void LoadSong(Song song, string? path, string name)
    {
        Stop(); editor.Load(song); filePath = path; activePattern = song.Patterns[0].Id; selectedInstrument = song.Instruments[0].Id; chosenTrack = 0; SynchronizeDrumSound(); Refresh(); ClearRecovery(); SetStatus("Opened " + name);
    }
    private async Task NewSong()
    {
        if (!await ConfirmDiscard()) return;
        Stop(); editor.Load(DemoSong.CreateEmpty()); filePath = null; activePattern = editor.Song.Patterns[0].Id; selectedInstrument = editor.Song.Instruments[0].Id; SynchronizeDrumSound(); Refresh(); ClearRecovery(); SetStatus("New song · type notes, add a beat, then arrange your patterns");
    }
    private async Task<bool> ConfirmDiscard()
    {
        if (!tracker.CommitPending()) return false;
        if (!editor.IsDirty) return true;
        var answer = await Ask("Save your changes?", $"{editor.Song.Title} has unsaved changes.", "Save", "Discard", "Cancel");
        return answer == "Discard" || answer == "Save" && await SaveSong();
    }
    public async Task<bool> RequestCloseAsync()
    {
        if (exportBusy) { SetStatus("An export is running. Finish or cancel it before closing."); return false; }
        if (updateRestartApproved) return true;
        updateOperation?.Cancel();
        // A close during extraction/handshake first cancels restart. It must never race update approval.
        if (updateInstalling) { updateMessage = "Cancelling restart…"; RefreshUpdateControls(); return false; }
        var close = await ConfirmDiscard();
        if (close) { StopUpdates(); ClearRecovery(); }
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
        if (!tracker.CommitPending()) return;
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
    private Task ShowHelp() => Ask("Make a loop", "1. Select a pattern and click a note cell. Type F, F#, F4 or F#4; Enter commits. Octave follows the nearest earlier note in that track, or 4. Arrows move; Delete clears; Esc cancels. Empty rows sustain. OFF releases; CUT stops. T / TT repeat held notes as eighth / sixteenth triplets; S swings.\n\n2. FX columns apply together: Axy arpeggio semitones, Vxx persistent volume (00–FF), Gxx gate fraction (00–FF), Uxx / Dxx slide in semitones/sec, Rxx retriggers per row (01–20 hex). Instrument here inserts a section change.\n\n3. Drums: click a step, right-click for an accent. Arrangement: append/reuse patterns, mix tracks, click or drag volume points; right-click deletes a point.\n\n4. Sampling: import WAV/QOA, select a region and find a stable cycle. Low-confidence audio can use a manual period. Shape and audition before applying an undoable custom waveform or wavetable frame. The full source recording is never saved in the song.\n\nSpace plays/stops. Ctrl+S saves; Ctrl+Shift+S saves as; Ctrl+O opens; Ctrl+Z undoes; Ctrl+Shift+Z redoes. F1 opens this guide.\n\nWAV/FLAC/QOA export the full arrangement. For smooth game loops, keep start/end levels and sustained notes compatible; loop markers are saved for your arrangement workflow.", "Got it");
    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static void AtomicWrite(string path, byte[] data)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(flushToDisk: true); } File.Move(temp, full, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async void ClearRecovery() { if (updateRestartApproved) return; recoveryTimer.Stop(); try { if (EditorPlatform.ClearRecoveryAsync is { } clear) await clear(); else if (File.Exists(recoveryPath)) File.Delete(recoveryPath); } catch (Exception e) { SetStatus("Recovery cleanup: " + e.Message); } }
}
