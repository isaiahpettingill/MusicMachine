using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
namespace MusicMachine.App.Updating;

internal static class WindowsSetup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicMachine";
    public static void Stage(UpdatePlan plan, string workDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows setup requires Windows.");
        var package = Path.Combine(workDirectory, "setup.exe"); UpdateInstaller.VerifyPackage(package, plan.InstallerSize, plan.InstallerSha256!);
        Run(package, "/STAGE", plan.Prepared);
        UpdateInstaller.VerifyPayload(plan.Prepared, plan);
        using var metadata = ReleaseClient.Json(File.ReadAllText(Path.Combine(plan.Prepared, "release.json")));
        if (!metadata.RootElement.TryGetProperty("updaterProtocol", out var protocol) || protocol.GetInt32() != 1 || !File.Exists(Path.Combine(plan.Prepared, "Uninstall.exe"))) throw new InvalidDataException("This installer does not support safe staged updates.");
    }
    public static void Register(UpdatePlan plan, string workDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Run(Path.Combine(workDirectory, "setup.exe"), "/REGISTER", plan.Target);
    }
    private static void Run(string package, string mode, string destination)
    {
        var start = new ProcessStartInfo(package) { UseShellExecute = false, CreateNoWindow = true };
        // NSIS requires /D last, with no quotes even when it contains spaces.
        if (destination.IndexOfAny(['"', '\r', '\n']) >= 0) throw new IOException("Unsupported Windows installation path.");
        start.Arguments = "/S " + mode + " /D=" + destination;
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows setup.");
        if (!process.WaitForExit(180000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new IOException("Windows setup timed out."); }
        if (process.ExitCode != 0) throw new IOException($"Windows setup failed ({process.ExitCode}).");
    }
    public static void RestoreRegistration(UpdatePlan plan)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
        using var key = root.OpenSubKey(Key, writable: true);
        if (key?.GetValue("InstallLocation") is string path && UpdatePaths.Same(path, plan.Target)) key.SetValue("DisplayVersion", plan.PreviousVersion);
    }
}
