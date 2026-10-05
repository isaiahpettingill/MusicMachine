using System.Runtime.InteropServices;
namespace MusicMachine.App.Updating;

public static class UpdateHost
{
    public static bool DesktopEnabled { get; set; }
    public static string? ResumePlan { get; set; }
    public static string DataDirectory => Path.Combine(Environment.GetEnvironmentVariable("MUSICMACHINE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicMachine"), "updates");
    public static string InstallationProblem { get; private set; } = "Automatic updates are available only in packaged Windows and Linux releases.";
    public static UpdateInstallation? Installation
    {
        get
        {
            if (!DesktopEnabled || OperatingSystem.IsBrowser() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
            var runtime = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "";
            try
            {
                var target = UpdatePaths.ValidateInstallation(AppContext.BaseDirectory, runtime);
                var installed = ReleaseClient.ReadInstallation(target)!;
                var version = typeof(UpdateHost).Assembly.GetName().Version;
                if (version is null || new Version(version.Major, version.Minor, Math.Max(0, version.Build)) != installed.Version) throw new IOException("The executable version and release metadata disagree. Reinstall the official package.");
                return installed;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { InstallationProblem = e.Message; return null; }
        }
    }
}
