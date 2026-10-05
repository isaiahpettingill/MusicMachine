using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace MusicMachine.App.Updating;

public sealed class UpdatePlan
{
    public string Token { get; set; } = "";
    public string Target { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string Version { get; set; } = "";
    public string Commit { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public string PreviousCommit { get; set; } = "";
    public int ParentProcess { get; set; }
    public long ParentStartUtcTicks { get; set; }
    public string? InstallerSha256 { get; set; }
    public long InstallerSize { get; set; }
    [JsonIgnore] public string Prepared => Target + ".update-" + Token;
    [JsonIgnore] public string Backup => Target + ".previous-" + Token;
    [JsonIgnore] public string Executable => ReleaseClient.ExecutableName(Runtime)!;
}
[JsonSerializable(typeof(UpdatePlan))]
internal partial class PlanJson : JsonSerializerContext { }

/// <summary>Stage beside the install, then let a copy of the old NativeAOT host swap directories after exit.</summary>
public static class UpdateInstaller
{
    public static UpdatePlan Prepare(string archive, UpdateRelease release, string target, string workDirectory, CancellationToken cancellation = default)
    {
        ReleaseClient.Validate(release); target = UpdatePaths.ValidateInstallation(target, release.Runtime);
        var installed = ReleaseClient.ReadInstallation(target)!;
        if (release.Version <= installed.Version || release.AssetName != ReleaseClient.AssetName(installed)) throw new InvalidDataException("Update package does not match this installation.");
        workDirectory = UpdatePaths.Normalize(workDirectory); UpdatePaths.EnsureNoLinks(workDirectory);
        if (UpdatePaths.Same(workDirectory, target) || UpdatePaths.Within(workDirectory, target) || UpdatePaths.Within(target, workDirectory)) throw new IOException("Update work files must be outside the installation.");
        if (Directory.Exists(workDirectory)) throw new IOException("Update work directory must be new.");
        VerifyPackage(archive, release.Size, release.Sha256, cancellation);
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan { Token = Guid.NewGuid().ToString("N"), Target = target, Runtime = release.Runtime, Version = release.Version.ToString(), Commit = release.Commit, PreviousVersion = installed.Version.ToString(), PreviousCommit = installed.Commit, ParentProcess = parent.Id, ParentStartUtcTicks = parent.StartTime.ToUniversalTime().Ticks };
        UpdatePaths.EnsureNoLinks(plan.Prepared); UpdatePaths.EnsureNoLinks(plan.Backup);
        if (Directory.Exists(plan.Prepared) || Directory.Exists(plan.Backup)) throw new IOException("Staging collision.");
        UpdatePaths.CreatePrivateDirectory(workDirectory); UpdatePaths.CreatePrivateDirectory(plan.Prepared);
        try
        {
            CopyTree(target, plan.Prepared, cancellation);
            if (release.AssetName.EndsWith("-setup.exe", StringComparison.Ordinal))
            {
                plan.InstallerSha256 = release.Sha256; plan.InstallerSize = release.Size;
                File.Copy(archive, Path.Combine(workDirectory, "setup.exe"));
                VerifyPackage(Path.Combine(workDirectory, "setup.exe"), release.Size, release.Sha256, cancellation);
            }
            else
            {
                var payload = Path.Combine(workDirectory, "payload"); UpdateArchive.Extract(archive, payload, cancellation);
                VerifyPayload(payload, plan);
                CopyTree(payload, plan.Prepared, cancellation, overwrite: true);
                SetExecutable(plan.Prepared, plan.Executable);
            }
            cancellation.ThrowIfCancellationRequested(); Write(Path.Combine(plan.Prepared, ".update-token"), plan.Token);
            Write(Path.Combine(workDirectory, "plan.json"), JsonSerializer.Serialize(plan, PlanJson.Default.UpdatePlan)); return plan;
        }
        catch { if (Directory.Exists(plan.Prepared)) Directory.Delete(plan.Prepared, true); throw; }
    }
    internal static void VerifyPackage(string archive, long size, string hash, CancellationToken cancellation = default)
    {
        UpdatePaths.EnsureNoLinks(archive); UpdatePaths.CheckOwnedEntry(archive); using var input = File.OpenRead(archive);
        if (input.Length != size) throw new InvalidDataException("The downloaded update has an incorrect size.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; int count;
        while ((count = input.Read(buffer)) != 0) { cancellation.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, count); }
        if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded update no longer matches its checksum.");
    }
    internal static void VerifyPayload(string payload, UpdatePlan plan)
    {
        var next = ReleaseClient.ReadInstallation(payload);
        if (next is null || next.Runtime != plan.Runtime || next.Version.ToString() != plan.Version || next.Commit != plan.Commit || new FileInfo(Path.Combine(payload, plan.Executable)).Length == 0) throw new InvalidDataException("The package does not contain the expected release and executable.");
    }
    private static void SetExecutable(string directory, string executable)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(directory, executable), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
    internal static void CopyTree(string source, string destination, CancellationToken cancellation, bool overwrite = false)
    {
        long total = 0; var count = 0; Copy(source, destination);
        void Copy(string from, string to)
        {
            UpdatePaths.EnsureNoLinks(from); UpdatePaths.EnsureNoLinks(to);
            foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
            {
                cancellation.ThrowIfCancellationRequested();
                if (++count > 20000 || (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The application contains links or too many files; update manually.");
                UpdatePaths.CheckOwnedEntry(entry.FullName);
                var dest = Path.Combine(to, entry.Name); UpdatePaths.EnsureNoLinks(dest);
                if (entry is DirectoryInfo sub) { Directory.CreateDirectory(dest); Copy(sub.FullName, dest); }
                else
                {
                    if ((total += ((FileInfo)entry).Length) > 1024L * 1024 * 1024) throw new IOException("The installation contains more than 1 GiB of files. Move your own files outside it before updating.");
                    File.Copy(entry.FullName, dest, overwrite);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, File.GetUnixFileMode(entry.FullName));
                }
            }
        }
    }
    private static void VerifyTree(string directory)
    {
        UpdatePaths.EnsureNoLinks(directory); var queue = new Queue<string>(); queue.Enqueue(directory); var count = 0;
        while (queue.TryDequeue(out var folder)) foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (++count > 20000 || (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Installation contains unsafe links or too many files.");
            UpdatePaths.CheckOwnedEntry(entry.FullName);
            if (entry is DirectoryInfo child) queue.Enqueue(child.FullName);
        }
    }
    public static UpdatePlan ReadPlan(string file)
    {
        file = Path.GetFullPath(file); UpdatePaths.EnsureNoLinks(file); UpdatePaths.CheckOwnedEntry(file);
        if (Path.GetFileName(file) != "plan.json" || new FileInfo(file).Length > 65536) throw new InvalidDataException("Invalid update plan path or size.");
        using var document = ReleaseClient.Json(File.ReadAllText(file)); // Reject duplicates before source-generated deserialization.
        var plan = JsonSerializer.Deserialize(document.RootElement, PlanJson.Default.UpdatePlan) ?? throw new InvalidDataException("Missing update plan.");
        ValidatePlan(plan);
        var work = Path.GetDirectoryName(file)!;
        if (UpdatePaths.Same(work, plan.Target) || UpdatePaths.Within(work, plan.Target) || UpdatePaths.Within(plan.Target, work)) throw new InvalidDataException("Unsafe update work directory.");
        return plan;
    }
    private static void ValidatePlan(UpdatePlan plan)
    {
        if (!Guid.TryParseExact(plan.Token, "N", out _) || plan.ParentProcess <= 0 || plan.ParentStartUtcTicks <= 0 || !Path.IsPathFullyQualified(plan.Target) || !UpdatePaths.Same(plan.Target, UpdatePaths.Normalize(plan.Target)) || ReleaseClient.ExecutableName(plan.Runtime) is null) throw new InvalidDataException("Invalid update plan.");
        if (ReleaseClient.ParseVersion(plan.Version) <= ReleaseClient.ParseVersion(plan.PreviousVersion)) throw new InvalidDataException("The update must be newer than the installed release.");
        ReleaseClient.Hash(plan.Commit, 40); ReleaseClient.Hash(plan.PreviousCommit, 40);
        if (plan.InstallerSha256 is not null) { if (plan.Runtime != "win-x64" || plan.InstallerSize is <= 0 or > ReleaseClient.MaximumPackageSize) throw new InvalidDataException("Invalid installer plan."); ReleaseClient.Hash(plan.InstallerSha256, 64); }
        UpdatePaths.EnsureNoLinks(plan.Target); UpdatePaths.EnsureNoLinks(plan.Prepared); UpdatePaths.EnsureNoLinks(plan.Backup);
    }
    public static async Task LaunchHelper(UpdatePlan plan, string workDirectory, CancellationToken cancellation = default)
    {
        ValidatePlan(plan); var helperDirectory = Path.Combine(workDirectory, "helper"); UpdatePaths.CreatePrivateDirectory(helperDirectory); UpdatePaths.EnsureNoLinks(helperDirectory);
        // Native hosts have no framework/runtimeconfig dependency. Copy the loader's native siblings as well.
        foreach (var file in Directory.EnumerateFiles(plan.Target))
        {
            cancellation.ThrowIfCancellationRequested(); var name = Path.GetFileName(file);
            if (name == plan.Executable || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".so", StringComparison.Ordinal)) { UpdatePaths.EnsureNoLinks(file); File.Copy(file, Path.Combine(helperDirectory, name), false); }
        }
        SetExecutable(helperDirectory, plan.Executable);
        var start = new ProcessStartInfo(Path.Combine(helperDirectory, plan.Executable)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = helperDirectory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(Path.Combine(workDirectory, "plan.json"));
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
        try
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (TokenFile(workDirectory, "helper.ready", plan.Token)) return;
                if (helper.HasExited) throw new IOException("The update helper could not start. Your editor remains open.");
                await Task.Delay(100, cancellation);
            }
            throw new IOException("The update helper did not respond. Your editor remains open.");
        }
        catch { Write(Path.Combine(workDirectory, "abort"), plan.Token); throw; }
    }
    public static void Approve(UpdatePlan plan, string workDirectory)
    {
        ValidatePlan(plan);
        if (!TokenFile(workDirectory, "helper.ready", plan.Token) || File.Exists(Path.Combine(workDirectory, "abort"))) throw new IOException("The update helper is not ready.");
        var session = UpdateRecovery.Read(workDirectory); // A malformed or missing durable song snapshot must never authorize exit.
        if (session.SongBytes.Length == 0) throw new InvalidDataException("Missing song recovery snapshot.");
        Write(Path.Combine(workDirectory, "apply.approved"), plan.Token);
    }
    public static void Abandon(UpdatePlan plan, string workDirectory)
    {
        ValidatePlan(plan);
        if (TokenFile(workDirectory, "apply.approved", plan.Token)) return;
        Write(Path.Combine(workDirectory, "abort"), plan.Token);
        if (Directory.Exists(plan.Prepared) && TokenFile(plan.Prepared, ".update-token", plan.Token)) { VerifyTree(plan.Prepared); Directory.Delete(plan.Prepared, true); }
        // Deliberately retain any song snapshots even when the user cancels.
    }
    public static void Apply(UpdatePlan plan, Action<string, string>? moveDirectory = null, Action<TimeSpan>? pause = null)
    {
        using var installLock = LockInstallation(plan);
        ApplyCore(plan, moveDirectory, pause);
    }
    private static FileStream LockInstallation(UpdatePlan plan)
    {
        ValidatePlan(plan); UpdatePaths.ValidateInstallation(plan.Target, plan.Runtime);
        var path = plan.Target + ".update.lock"; UpdatePaths.EnsureNoLinks(path);
        if (File.Exists(path)) UpdatePaths.CheckOwnedEntry(path);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(path, options); }
        catch (IOException e) { throw new IOException("Another update is already using this installation. Wait for it to finish before trying again.", e); }
        // Keep this empty lock file after releasing it. Deleting a lock path would let contenders lock different inodes.
    }
    private static void ApplyCore(UpdatePlan plan, Action<string, string>? moveDirectory = null, Action<TimeSpan>? pause = null)
    {
        ValidatePlan(plan); UpdatePaths.ValidateInstallation(plan.Target, plan.Runtime);
        if (!Directory.Exists(plan.Prepared) || Directory.Exists(plan.Backup)) throw new InvalidDataException("The update is already applied or its staging directory is missing.");
        VerifyTree(plan.Target); VerifyTree(plan.Prepared); VerifyPayload(plan.Prepared, plan);
        var old = ReleaseClient.ReadInstallation(plan.Target)!;
        if (old.Version.ToString() != plan.PreviousVersion || old.Commit != plan.PreviousCommit || !TokenFile(plan.Prepared, ".update-token", plan.Token) || Directory.Exists(plan.Backup)) throw new InvalidDataException("The installation changed since preparation. Start a fresh update.");
        moveDirectory ??= Directory.Move; pause ??= Thread.Sleep;
        Move(plan.Target, plan.Backup, moveDirectory, pause);
        try { Move(plan.Prepared, plan.Target, moveDirectory, pause); }
        catch { Move(plan.Backup, plan.Target, moveDirectory, pause); throw; }
    }
    private static void Move(string source, string destination, Action<string, string> move, Action<TimeSpan> pause)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { move(source, destination); return; }
            catch (Exception e) when (attempt < 14 && (e is UnauthorizedAccessException || e is IOException && (e.HResult & 0xffff) is 5 or 32 or 33)) { pause(TimeSpan.FromMilliseconds(Math.Min(1000, (attempt + 1) * 100))); }
        }
    }
    public static int RunHelper(string planFile)
    {
        FileStream? installLock = null; UpdatePlan? plan = null; bool authorized = false, applying = false; var work = Path.GetDirectoryName(Path.GetFullPath(planFile))!; var log = Path.Combine(work, "install.log");
        try
        {
            if (!UpdatePaths.Within(work, UpdateHost.DataDirectory)) throw new InvalidDataException("Unrecognized update work directory.");
            plan = ReadPlan(planFile); UpdatePaths.ValidateInstallation(plan.Target, plan.Runtime);
            var helper = Path.Combine(work, "helper", plan.Executable);
            if (Environment.ProcessPath is null || !UpdatePaths.Same(Environment.ProcessPath, helper)) throw new InvalidDataException("The helper must run outside the installation from its own prepared copy.");
            installLock = LockInstallation(plan);
            Write(Path.Combine(work, "helper.ready"), plan.Token);
            var until = DateTime.UtcNow.AddMinutes(5);
            while (ParentRunning(plan))
            {
                if (TokenFile(work, "abort", plan.Token)) return 1;
                if (DateTime.UtcNow >= until) throw new IOException("The editor did not exit. Update cancelled.");
                Thread.Sleep(200);
            }
            if (!TokenFile(work, "apply.approved", plan.Token) || TokenFile(work, "abort", plan.Token)) return 1;
            authorized = true;
            if (plan.InstallerSha256 is not null) WindowsSetup.Stage(plan, work);
            applying = true; ApplyCore(plan);
            if (plan.InstallerSha256 is not null) WindowsSetup.Register(plan, work);
            using var restarted = Restart(plan, planFile);
            // Do not kill a living editor on a startup timeout. Keep the old installation and recovery snapshots intact.
            until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until)
            {
                if (TokenFile(work, "startup.ready", plan.Token)) { Write(log, "Update installed and song restored. The previous installation is retained at " + plan.Backup); return 0; }
                if (restarted.HasExited) throw new IOException("The updated editor exited before confirming startup. Restoring the previous version.");
                Thread.Sleep(200);
            }
            Write(log, "The editor started but did not confirm song recovery. The previous installation and all recovery snapshots were retained."); return 0;
        }
        catch (Exception error)
        {
            // Validation failures must not write to arbitrary caller-supplied paths.
            if (plan is null || !authorized) { Console.Error.WriteLine(error.Message); return 1; }
            try { Write(log, "Update failed: " + error.Message); } catch { }
            if (applying && Directory.Exists(plan.Backup))
            {
                try
                {
                    ValidatePlan(plan); VerifyTree(plan.Backup);
                    var backup = ReleaseClient.ReadInstallation(plan.Backup);
                    if (backup is null || backup.Commit != plan.PreviousCommit || backup.Version.ToString() != plan.PreviousVersion) throw new IOException("Rollback backup metadata does not match the old installation.");
                    if (Directory.Exists(plan.Target)) { if (Directory.Exists(plan.Prepared)) throw new IOException("Rollback staging already exists."); Move(plan.Target, plan.Prepared, Directory.Move, Thread.Sleep); }
                    Move(plan.Backup, plan.Target, Directory.Move, Thread.Sleep);
                    if (plan.InstallerSha256 is not null) WindowsSetup.RestoreRegistration(plan);
                }
                catch (Exception rollback) { Write(log, "Update failed: " + error.Message + "\nRollback could not finish: " + rollback.Message + "\nKeep the previous installation at " + plan.Backup); return 1; }
            }
            if (!ParentRunning(plan) && File.Exists(Path.Combine(plan.Target, plan.Executable)))
                try { using var restarted = Restart(plan, planFile); } catch (Exception restart) { Write(log, "Update failed: " + error.Message + "\nRestart failed: " + restart.Message); }
            return 1;
        }
        finally { installLock?.Dispose(); }
    }
    private static bool ParentRunning(UpdatePlan plan)
    {
        try { using var process = Process.GetProcessById(plan.ParentProcess); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == plan.ParentStartUtcTicks; }
        catch (ArgumentException) { return false; }
    }
    private static Process Restart(UpdatePlan plan, string planFile)
    {
        var start = new ProcessStartInfo(Path.Combine(plan.Target, plan.Executable)) { UseShellExecute = false, WorkingDirectory = plan.Target };
        start.ArgumentList.Add("--resume-update"); start.ArgumentList.Add(planFile);
        return Process.Start(start) ?? throw new IOException("Could not restart MusicMachine. Your song snapshot was preserved.");
    }
    public static void Complete(string planFile, string currentDirectory)
    {
        var plan = ReadPlan(planFile); var work = Path.GetDirectoryName(Path.GetFullPath(planFile))!;
        if (!UpdatePaths.Within(work, UpdateHost.DataDirectory) || !UpdatePaths.Same(UpdatePaths.ResolveInstallation(currentDirectory), plan.Target)) throw new InvalidDataException("Startup acknowledgment belongs to another installation.");
        var installed = ReleaseClient.ReadInstallation(plan.Target) ?? throw new InvalidDataException("Missing installation metadata.");
        if (installed.Commit != plan.Commit && installed.Commit != plan.PreviousCommit) throw new InvalidDataException("Unexpected restart version.");
        Write(Path.Combine(work, "startup.ready"), plan.Token);
        // Never delete the backup or any recovery files here. Users can recover even after a later crash.
    }
    private static bool TokenFile(string directory, string name, string token)
    {
        var file = Path.Combine(directory, name); UpdatePaths.EnsureNoLinks(file);
        if (!File.Exists(file)) return false;
        UpdatePaths.CheckOwnedEntry(file); return new FileInfo(file).Length == 32 && File.ReadAllText(file) == token;
    }
    private static void Write(string file, string text) => UpdateRecovery.AtomicWrite(file, Encoding.UTF8.GetBytes(text));
}
