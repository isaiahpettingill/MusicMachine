using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MusicMachine.Audio;
using MusicMachine.Core;

namespace MusicMachine.App;

public sealed partial class MainView
{
    private static readonly FilePickerFileType SamplingType = new("WAV / QOA audio") { Patterns = ["*.wav", "*.qoa"], MimeTypes = ["audio/wav", "audio/x-wav", "audio/qoa"] };
    private static readonly FilePickerFileType ConvertibleSamplingType = new("Other audio · best-effort FFmpeg conversion") { Patterns = ["*.flac", "*.mp3", "*.ogg", "*.opus", "*.m4a", "*.aac", "*.aif", "*.aiff", "*.wma", "*.caf"] };
    private string FfmpegConfigPath => Path.Combine(Path.GetDirectoryName(recoveryPath)!, "ffmpeg-path.txt");
    private string? ConfiguredFfmpeg()
    {
        if (OperatingSystem.IsBrowser() || !File.Exists(FfmpegConfigPath)) return null;
        var path = File.ReadAllText(FfmpegConfigPath).Trim();
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }
    private Control BuildSamplingWorkspace() { Ui.Detach(samplingPanel); return samplingPanel; }

    private async Task<SampleClip?> ImportSamplingAudio(CancellationToken cancellationToken)
    {
        if (!tracker.CommitPending()) throw new InvalidOperationException("Finish or cancel the pending tracker edit first.");
        var ffmpeg = ConfiguredFfmpeg();
        bool canConvert = OperatingSystem.IsBrowser() ? EditorPlatform.ConvertAudioToWaveAsync is not null : ffmpeg is not null;
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = canConvert ? "Import audio for waveform extraction (up to 30 seconds / 32 MiB)" : "Import WAV or QOA (up to 30 seconds / 32 MiB)",
            AllowMultiple = false, FileTypeFilter = canConvert ? [SamplingType, ConvertibleSamplingType] : [SamplingType]
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (files.Count == 0) return null;
        using var file = files[0];
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        if (extension is ".wav" or ".qoa")
        {
            await using var source = await file.OpenReadAsync();
            return await SampleImporter.ReadAsync(source, file.Name, cancellationToken);
        }
        if (OperatingSystem.IsBrowser())
        {
            if (EditorPlatform.ConvertAudioToWaveAsync is not { } convert) throw new InvalidOperationException("Audio conversion is unavailable here. Choose WAV or QOA.");
            SetStatus("Converting audio locally in your browser; the first conversion downloads FFmpeg. You can cancel in Sampling.");
            await using var input = await file.OpenReadAsync();
            byte[] encoded = await ReadSamplingBytes(input, cancellationToken);
            byte[] wave = await convert(file.Name, encoded, cancellationToken);
            if (wave.Length > SampleImporter.MaxEncodedBytes) throw new InvalidDataException("Converted audio exceeds the import size limit.");
            using var decoded = new MemoryStream(wave, writable: false);
            var clip = await SampleImporter.ReadAsync(decoded, "converted.wav", cancellationToken);
            return clip with { Name = file.Name, OriginalChannels = 0 };
        }
        if (ffmpeg is null) throw new InvalidOperationException("Choose WAV/QOA, or select your installed FFmpeg under File → Configure FFmpeg for best-effort import of other audio formats.");
        if (file.TryGetLocalPath() is { } path) return await SampleImporter.ReadFfmpegAsync(ffmpeg, path, cancellationToken);
        // StorageProvider files need not expose a path. Stage only bounded bytes for the local process.
        string temporary = Path.Combine(Path.GetTempPath(), "musicmachine-sampling-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            await using var source = await file.OpenReadAsync();
            await File.WriteAllBytesAsync(temporary, await ReadSamplingBytes(source, cancellationToken), cancellationToken);
            return (await SampleImporter.ReadFfmpegAsync(ffmpeg, temporary, cancellationToken)) with { Name = file.Name };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<byte[]> ReadSamplingBytes(Stream source, CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length - source.Position > SampleImporter.MaxEncodedBytes) throw new InvalidDataException("Choose audio smaller than 32 MiB.");
        using var buffer = new MemoryStream(); var chunk = new byte[32768];
        while (true)
        {
            int read = await source.ReadAsync(chunk, cancellationToken); if (read == 0) break;
            if (buffer.Length + read > SampleImporter.MaxEncodedBytes) throw new InvalidDataException("Choose audio smaller than 32 MiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private string ApplySampledWave(short[] wave, string sourceName, SamplingApplyMode mode)
    {
        if (!tracker.CommitPending()) return "Finish or cancel the pending tracker edit before applying.";
        var id = selectedInstrument;
        if (mode == SamplingApplyMode.NewInstrument)
        {
            if (editor.Song.Instruments.Count >= SongLimits.MaxInstruments) return "This song has reached its instrument limit.";
            var name = Path.GetFileNameWithoutExtension(sourceName); if (name.Length > 100) name = name[..100];
            var instrument = new Instrument { Name = name + " wave", Waveform = Waveform.Custom, CustomWave = (short[])wave.Clone(), VolumeDb = -14, IsLocal = true };
            Change(s => s.Instruments.Add(instrument));
            if (editor.Song.FindInstrument(instrument.Id) is null) return "Could not create instrument. Check the status bar.";
            selectedInstrument = instrument.Id; Refresh(); SetStatus("Created a self-contained sampled waveform instrument · Undo to remove");
            return "Created " + instrument.Name + ". The source recording is not saved with the song.";
        }
        var selected = editor.Song.FindInstrument(id);
        if (selected is null) return "Select an instrument first.";
        if (mode == SamplingApplyMode.AppendFrame && selected.Wavetable.Count >= SongLimits.MaxWaveFrames) return $"A wavetable can contain at most {SongLimits.MaxWaveFrames} frames. Remove a frame in Instrument first.";
        var previousRevision = editor.Revision;
        Change(s =>
        {
            var target = s.FindInstrument(id)!; target.Drum = DrumKind.None;
            if (mode == SamplingApplyMode.AppendFrame) { target.Wavetable.Add((short[])wave.Clone()); target.Waveform = Waveform.Wavetable; target.WavetablePosition = 1; }
            else { target.CustomWave = (short[])wave.Clone(); target.Waveform = Waveform.Custom; }
        });
        if (editor.Revision == previousRevision) return "The instrument is unchanged.";
        SetStatus("Applied extracted waveform · Undo restores the previous instrument");
        return mode == SamplingApplyMode.AppendFrame ? "Appended a self-contained wavetable frame. Undo restores the previous instrument." : "Replaced the selected waveform. All notes using this instrument now use it; Undo restores the previous sound.";
    }

    private void PreviewSampledWave(short[] wave)
    {
        var instrument = new Instrument { Name = "Sampling preview", Waveform = Waveform.Custom, CustomWave = wave, VolumeDb = -14 };
        Stop(); var song = DemoSong.CreateEmpty(); song.Instruments = [instrument];
        song.Tracks.RemoveRange(1, song.Tracks.Count - 1); song.Tracks[0].InstrumentId = instrument.Id;
        var pattern = song.Patterns[0]; pattern.Length = 8; pattern.Drums.Clear(); pattern.Tracks.RemoveAll(t => t.TrackId != song.Tracks[0].Id);
        pattern.GetTrack(song.Tracks[0].Id).Rows = Enumerable.Range(0, 8).Select(_ => new NoteEvent()).ToList();
        pattern.Tracks[0].Rows[0] = new() { Kind = NoteKind.Note, Pitch = 60 }; pattern.Tracks[0].Rows[4] = new() { Kind = NoteKind.Off };
        SongFile.Validate(song); player.Play(song); previewing = true; SetPlayingVisual(true); SetStatus("Auditioning extracted waveform at C4 · song unchanged");
    }

    private async Task ConfigureSamplingFfmpeg()
    {
        if (OperatingSystem.IsBrowser()) return;
        var executable = new TextBox { Text = ConfiguredFfmpeg() ?? "", PlaceholderText = "Absolute path to an installed FFmpeg executable" };
        var dialog = new EditorDialog { Title = "Configure optional FFmpeg", Width = 540, SizeToContent = SizeToContent.Height };
        var body = new StackPanel { Spacing = 12, Margin = new(24) };
        body.Children.Add(Ui.Label("Configure optional FFmpeg", 18));
        body.Children.Add(new TextBlock { Text = "Choose FFmpeg already installed from a trusted source. The same executable is used for optional export and best-effort sampling import. MusicMachine does not search PATH, download, or install it. WAV and QOA import work without it.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted });
        body.Children.Add(executable);
        body.Children.Add(Ui.Button("Browse executable…", () => _ = Pick()));
        async Task Pick() { var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose FFmpeg executable", AllowMultiple = false }); if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) executable.Text = path; }
        var error = Ui.Label("", 11, Ui.Error); body.Children.Add(error);
        body.Children.Add(Ui.Row(Ui.Button("Cancel", () => dialog.Close()), Ui.Button("Save configuration", () =>
        {
            var path = executable.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) { error.Text = "Choose an existing executable with an absolute path."; return; }
            try { AtomicWrite(FfmpegConfigPath, System.Text.Encoding.UTF8.GetBytes(path)); dialog.Close(); SetStatus("FFmpeg configured for optional export and best-effort audio import"); }
            catch (Exception e) { error.Text = e.Message; }
        }, accent: true)));
        dialog.Content = body; await dialog.ShowDialog(this);
    }
}
