using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MusicMachine.App.Updating;
using MusicMachine.Core;

namespace MusicMachine.App;

public sealed partial class MainView
{
    private CancellationTokenSource? updateLifetime, updateOperation;
    private UpdateRelease? availableUpdate;
    private string? updatePackage;
    private bool updateBusy, updateInstalling, updateRestartApproved;
    private string updateMessage = "Checks for stable MusicMachine releases on GitHub.";
    private MenuItem? updateMenu, helpMenu;
    private EditorDialog? updateDialog;
    private TextBlock? updateStatus;
    private ProgressBar? updateProgress;
    private Button? updateAction, updateCancel;

    private MenuItem BuildHelpMenu(MenuItem guide)
    {
        helpMenu = new MenuItem { Header = "_Help" };
        helpMenu.Items.Add(guide);
        // The shared browser UI has no access to desktop installation or update helpers.
        if (!UpdateHost.DesktopEnabled || OperatingSystem.IsBrowser()) return helpMenu;
        updateMenu = new MenuItem { Header = "Check for updates…", Name = "CheckForUpdates" };
        updateMenu.Click += (_, _) => _ = ShowUpdates();
        helpMenu.Items.Add(new Separator()); helpMenu.Items.Add(updateMenu);
        return helpMenu;
    }

    private void RefreshUpdateControls()
    {
        if (updateMenu is not null) updateMenu.Header = availableUpdate is null ? "Check for updates…" : $"Update {availableUpdate.Version} available…";
        if (helpMenu is not null) helpMenu.Header = availableUpdate is null ? "_Help" : "_Help • Update";
        if (updateStatus is not null) updateStatus.Text = updateMessage;
        if (updateAction is not null)
        {
            updateAction.IsEnabled = !updateBusy && UpdateHost.Installation is not null;
            updateAction.Content = updateBusy ? "Please wait…" : updatePackage is not null ? "Restart and install" : availableUpdate is not null ? "Download update" : "Check now";
        }
        if (updateCancel is not null)
        {
            updateCancel.IsVisible = updateBusy && !updateRestartApproved;
            updateCancel.Content = updateInstalling ? "Cancel restart" : updatePackage is null && availableUpdate is not null ? "Cancel download" : "Cancel";
        }
        if (updateProgress is not null) updateProgress.IsVisible = updateBusy;
    }

    private async Task ShowUpdates()
    {
        if (!UpdateHost.DesktopEnabled || OperatingSystem.IsBrowser() || updateDialog is not null || updateInstalling) return;
        if (!tracker.CommitPending()) return;
        var installation = UpdateHost.Installation;
        var dialog = new EditorDialog { Title = "Software updates", Name = "SoftwareUpdates", Width = 530, SizeToContent = SizeToContent.Height, CanResize = false };
        updateDialog = dialog;
        var body = new StackPanel { Margin = new(24), Spacing = 14 };
        body.Children.Add(Ui.Label(installation is null ? "MusicMachine updates" : $"MusicMachine {installation.Version}", 19));
        updateStatus = new TextBlock { Name = "UpdateStatus", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text };
        body.Children.Add(updateStatus);
        var automatic = new CheckBox { Name = "AutoCheckUpdates", Content = "Automatically check for updates", IsChecked = viewSettings.CheckForUpdates, IsEnabled = installation is not null };
        automatic.IsCheckedChanged += (_, _) => { viewSettings.CheckForUpdates = automatic.IsChecked == true; SaveViewSettings(); };
        body.Children.Add(automatic);
        body.Children.Add(new TextBlock
        {
            Text = installation is null
                ? UpdateHost.InstallationProblem + " Updates are available only in supported release packages."
                : "Downloads are verified before installation. Restart when ready; your song changes and saved editor preferences will return. Imported Sampling audio must be applied to an instrument first. Undo history resets after restarting.",
            TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted
        });
        body.Children.Add(new TextBlock { Text = "SHA-256 checks detect damaged downloads. Packages are not independently publisher-signed.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, FontSize = 11 });
        updateProgress = new ProgressBar { Name = "UpdateProgress", Minimum = 0, Maximum = 100, Height = 6, IsIndeterminate = true };
        body.Children.Add(updateProgress);
        updateAction = Ui.Button("Check now", () => _ = RunUpdateAction(), accent: true); updateAction.Name = "UpdateAction";
        updateCancel = Ui.Button("Cancel", () => updateOperation?.Cancel()); updateCancel.Name = "CancelUpdate";
        body.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { updateAction, updateCancel } });
        body.Children.Add(Ui.Button("Release notes & downloads", () => _ = OpenUpdateReleases()));
        body.Children.Add(Ui.Button("Close", () => dialog.Close()));
        dialog.Content = body;
        dialog.Closing += (_, _) => { if (!updateRestartApproved) updateOperation?.Cancel(); };
        RefreshUpdateControls();
        try { await dialog.ShowDialog(this); }
        finally
        {
            updateDialog = null; updateStatus = null; updateProgress = null; updateAction = null; updateCancel = null;
        }
    }

    private async Task OpenUpdateReleases()
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null && !await launcher.LaunchUriAsync(new Uri(ReleaseClient.ReleasesUrl)))
                throw new InvalidOperationException("The system browser could not be opened.");
        }
        catch (Exception ex) { updateMessage = "Could not open release notes: " + ex.Message; RefreshUpdateControls(); }
    }

    private CancellationTokenSource BeginUpdateOperation()
    {
        updateBusy = true;
        updateOperation = updateLifetime is null ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        return updateOperation;
    }

    private void EndUpdateOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(updateOperation, operation)) { updateOperation = null; updateBusy = false; }
        RefreshUpdateControls();
    }

    private async Task RunUpdateAction()
    {
        if (updateBusy || !UpdateHost.DesktopEnabled || UpdateHost.Installation is null) return;
        if (availableUpdate is null) { await CheckUpdates(manual: true); return; }
        if (updatePackage is not null) { await InstallUpdate(); return; }
        var release = availableUpdate;
        using var operation = BeginUpdateOperation();
        updateMessage = $"Downloading {release.Version}…";
        if (updateProgress is not null) { updateProgress.IsIndeterminate = false; updateProgress.Value = 0; }
        RefreshUpdateControls();
        try
        {
            var folder = Path.Combine(UpdateHost.DataDirectory, "downloads", release.Version.ToString());
            var package = await ReleaseClient.Download(release, folder, new Progress<double>(value =>
            {
                // A queued progress notification from a cancelled operation cannot change a later dialog.
                if (!ReferenceEquals(updateOperation, operation) || operation.IsCancellationRequested) return;
                if (updateProgress is not null) updateProgress.Value = value * 100;
                updateMessage = $"Downloading {release.Version}: {value:P0}";
                if (updateStatus is not null) updateStatus.Text = updateMessage;
            }), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            updatePackage = package;
            updateMessage = $"Update {release.Version} is downloaded and verified. Restart when you’re ready.";
        }
        catch (OperationCanceledException) { updateMessage = "Download cancelled or timed out. You can retry."; }
        catch (Exception ex) { updateMessage = "Download failed: " + ex.Message; }
        finally { EndUpdateOperation(operation); }
    }

    private async Task CheckUpdates(bool manual)
    {
        var installation = UpdateHost.Installation;
        if (!UpdateHost.DesktopEnabled || installation is null || updateBusy || updatePackage is not null) return;
        using var operation = BeginUpdateOperation();
        updateMessage = "Checking GitHub for updates…";
        if (updateProgress is not null) updateProgress.IsIndeterminate = true;
        RefreshUpdateControls();
        try
        {
            var release = await ReleaseClient.Check(installation, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            availableUpdate = release;
            updateMessage = release is null ? "You’re up to date." : $"Version {release.Version} is available ({release.Size / 1048576.0:F1} MB).";
        }
        catch (OperationCanceledException) { updateMessage = "The update check was cancelled or timed out. You can retry."; }
        catch (Exception ex) { updateMessage = "Could not check for updates: " + ex.Message; }
        finally { EndUpdateOperation(operation); }
        if (manual && availableUpdate is not null) SetStatus(updateMessage);
    }

    private async Task<bool> ConfirmSamplingRestart()
    {
        if (samplingPanel.IsBusy)
        {
            updateMessage = "Finish or cancel the Sampling import or analysis before restarting.";
            RefreshUpdateControls(); return false;
        }
        if (!samplingPanel.HasSource) return true;
        var answer = await Ask("Restart with imported audio?", "Applied instruments are saved with your song. The imported Sampling source clip, selection and shaping controls are temporary and will be cleared by restarting. Apply any waveform you want to keep before continuing.", "Restart", "Cancel");
        return answer == "Restart";
    }

    private async Task InstallUpdate()
    {
        if (availableUpdate is null || updatePackage is null || updateBusy) return;
        if (exportBusy) { updateMessage = "Wait for the audio export to finish, then restart."; RefreshUpdateControls(); return; }
        if (!tracker.CommitPending()) { updateMessage = "Finish the current note edit before restarting."; RefreshUpdateControls(); return; }
        if (TopLevel.GetTopLevel(this) is not MainWindow window) { updateMessage = "Desktop restart is not available in this host."; RefreshUpdateControls(); return; }
        var release = availableUpdate; var package = updatePackage;
        UpdatePlan? plan = null; string? directory = null;
        using var operation = BeginUpdateOperation();
        updateInstalling = true;
        if (updateProgress is not null) updateProgress.IsIndeterminate = true;
        updateMessage = "Preparing to restart…"; RefreshUpdateControls();
        try
        {
            if (!await ConfirmSamplingRestart()) { if (!samplingPanel.IsBusy) updateMessage = "Restart cancelled. Your Sampling workspace is unchanged."; return; }
            operation.Token.ThrowIfCancellationRequested();
            if (editor.IsDirty)
            {
                var answer = await Ask("Restart with your song?", "Your song has unsaved changes. Save it before restarting, or preserve the current unsaved song for recovery after the update. Imported Sampling audio must be applied to an instrument first.", "Save & restart", "Preserve & restart", "Cancel");
                operation.Token.ThrowIfCancellationRequested();
                if (answer is not ("Save & restart" or "Preserve & restart")) { updateMessage = "Restart cancelled. The downloaded update is ready when you are."; return; }
                if (answer == "Save & restart" && !await SaveSong()) { updateMessage = "Song was not saved. Restart cancelled."; return; }
            }
            operation.Token.ThrowIfCancellationRequested();
            Stop();
            updateMessage = "Preparing the update and saving your workspace…"; RefreshUpdateControls();
            directory = Path.Combine(UpdateHost.DataDirectory, "install-" + Guid.NewGuid().ToString("N"));
            plan = await Task.Run(() => UpdateInstaller.Prepare(package, release, AppContext.BaseDirectory, directory, operation.Token), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            recoveryTimer.Stop();
            // Snapshot every song, including clean songs and untitled demos. Neither snapshot is consumed on restart.
            UpdateRecovery.Save(editor, filePath is null ? null : Path.GetFullPath(filePath), activePattern, selectedInstrument, mode, directory);
            AtomicWrite(recoveryPath, SongFile.Write(editor.Song));
            await UpdateInstaller.LaunchHelper(plan, directory, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            // This is the only path allowed to bypass the ordinary discard dialog or its recovery cleanup.
            // Both durable snapshots and the helper handshake have completed before approval is written.
            UpdateInstaller.Approve(plan, directory);
            updateRestartApproved = true;
            updateDialog?.Close(); // Do not leave the owned update window keeping the desktop lifetime alive.
            window.Close();
        }
        catch (OperationCanceledException) { updateMessage = "Restart cancelled. Your song is still open."; }
        catch (Exception ex)
        {
            if (ex is InvalidDataException or FileNotFoundException) updatePackage = null;
            updateMessage = "The update could not start: " + ex.Message;
        }
        finally
        {
            if (!updateRestartApproved && plan is not null && directory is not null)
            {
                try { UpdateInstaller.Abandon(plan, directory); }
                catch (Exception ex) { updateMessage += " Cleanup could not finish: " + ex.Message + " Recovery copies were kept."; }
            }
            updateInstalling = false;
            if (!updateRestartApproved && editor.IsDirty) recoveryTimer.Start();
            EndUpdateOperation(operation);
        }
    }

    private async Task<bool> RestoreUpdateSession()
    {
        if (OperatingSystem.IsBrowser() || !UpdateHost.DesktopEnabled || UpdateHost.ResumePlan is not { } planFile) return false;
        var restored = false;
        try
        {
            var fullPlan = Path.GetFullPath(planFile);
            var directory = Path.GetDirectoryName(fullPlan)!;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(UpdateHost.DataDirectory)) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, comparison)) throw new InvalidDataException("The update recovery directory is not recognized.");
            var plan = UpdateInstaller.ReadPlan(fullPlan);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Target)), Path.TrimEndingDirectorySeparator(UpdatePaths.ResolveInstallation(AppContext.BaseDirectory)), comparison))
                throw new InvalidDataException("The update recovery belongs to a different installation.");
            var session = UpdateRecovery.Read(directory);
            var song = SongFile.Read(session.SongBytes);
            editor.Load(song); if (session.Dirty) editor.MarkUnsaved();
            filePath = session.FilePath; activePattern = session.ActivePattern; selectedInstrument = session.SelectedInstrument;
            if (session.Workspace is "Tracker" or "Drums" or "Instrument" or "Arrangement" or "Automation" or "Sampling") mode = session.Workspace;
            chosenTrack = 0; Refresh(); restored = true;
            // Reassert the plain recovery copy too; a later crash can recover without a resume argument.
            AtomicWrite(recoveryPath, session.SongBytes);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => UpdateInstaller.Complete(fullPlan, AppContext.BaseDirectory), Avalonia.Threading.DispatcherPriority.Background);
            if (ReadUpdateFailure(directory) is { } failure)
            {
                updateMessage = failure;
                SetStatus("The update failed; your song and workspace were restored");
                await Ask("Update could not finish", failure + "\n\nYour previous version, song and workspace were restored. Your recovery copies are still available.", "Continue");
            }
            else SetStatus("Your song and workspace were restored after restarting · undo history reset");
        }
        catch (Exception ex)
        {
            updateMessage = "Update recovery: " + ex.Message;
            SetStatus(updateMessage + " · recovery copies were kept");
        }
        return restored;
    }

    private static string? ReadUpdateFailure(string directory)
    {
        var file = Path.Combine(directory, "install.log"); UpdatePaths.EnsureNoLinks(file);
        if (!File.Exists(file)) return null;
        using var input = new StreamReader(file);
        var buffer = new char[8192]; var length = input.ReadBlock(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, length);
        return text.StartsWith("Update failed:", StringComparison.Ordinal) ? text : null;
    }

    private async void StartUpdates()
    {
        if (!UpdateHost.DesktopEnabled || OperatingSystem.IsBrowser() || UpdateHost.Installation is null || updateLifetime is not null) return;
        updateLifetime = new CancellationTokenSource(); var cancellation = updateLifetime.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
            while (!cancellation.IsCancellationRequested)
            {
                if (viewSettings.CheckForUpdates) await CheckUpdates(manual: false);
                await Task.Delay(TimeSpan.FromHours(4), cancellation);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void StopUpdates()
    {
        updateLifetime?.Cancel(); updateLifetime?.Dispose(); updateLifetime = null;
        updateOperation?.Cancel();
    }
}
