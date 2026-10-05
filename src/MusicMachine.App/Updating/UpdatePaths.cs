using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MusicMachine.App.Updating;

public static class UpdatePaths
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Same(string left, string right) => Normalize(left).Equals(Normalize(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static bool Within(string child, string parent) => Normalize(child).StartsWith(Normalize(parent) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static void EnsureNoLinks(string path)
    {
        for (var current = Normalize(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (FileAttributesReader.Get(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links and junctions require a manual update.");
    }
    public static string ResolveInstallation(string directory)
    {
        var full = Normalize(directory);
        // The Linux installer owns exactly one alias. Never follow arbitrary application-folder links.
        var current = new DirectoryInfo(full);
        if (OperatingSystem.IsLinux() && current.LinkTarget is not null && current.Name == "current" && current.Parent is { } root)
        {
            EnsureNoLinks(root.FullName);
            var marker = Path.Combine(root.FullName, ".installer-owned"); EnsureNoLinks(marker); CheckOwnedEntry(marker);
            var target = current.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Broken installation alias.");
            var relative = Path.GetRelativePath(root.FullName, target).Split(Path.DirectorySeparatorChar);
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker).Trim() != "MusicMachine per-user installation v1" || relative.Length != 3 || relative[0] != "releases" || !relative[1].StartsWith("build.", StringComparison.Ordinal) || relative[2] != "app") throw new IOException("Unrecognized managed installation alias.");
            full = Normalize(target);
        }
        EnsureNoLinks(full); return full;
    }
    public static string ValidateInstallation(string path, string runtime)
    {
        var target = ResolveInstallation(path);
        if (Same(target, Path.GetPathRoot(target)!) || !Directory.Exists(target)) throw new IOException("Invalid installation directory.");
        for (var parent = new DirectoryInfo(target); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, ".git", "HEAD")) || File.Exists(Path.Combine(parent.FullName, ".git")) || parent.Name is "bin" or "obj") throw new IOException("Source checkouts and development build folders must be updated manually.");
        if (Directory.EnumerateFiles(target, "*.csproj").Any() || Directory.EnumerateFiles(target, "*.runtimeconfig.json").Any()) throw new IOException("Only packaged NativeAOT releases can update themselves.");
        if (OperatingSystem.IsLinux())
        {
            foreach (var system in new[] { "/bin", "/sbin", "/lib", "/lib64", "/usr", "/opt", "/etc", "/var", "/root" })
                if (Same(target, system) || Within(target, system)) throw new IOException("System-owned installations require a manual update by their owner.");
            if (geteuid() == 0) throw new IOException("Do not run the desktop updater as root.");
            // Linux statx avoids guessing struct stat layouts. Verify each mutable install component is owned by this user.
            CheckOwnedEntry(target); CheckOwnedEntry(Path.GetDirectoryName(target)!); CheckMutableAncestors(target);
        }
        else if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            RequireUserPrivileges(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
            foreach (var system in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Where(s => s.Length > 0))
                if (Same(target, system) || Within(target, system)) throw new IOException("Machine-wide installations require a manual update.");
            if (target.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Network installations require a manual update.");
        }
        if (ReleaseClient.ReadInstallation(target) is not { } installed || installed.Runtime != runtime) throw new InvalidDataException("Packaged installation metadata is missing or does not match this platform.");
        return target;
    }
    internal static void CreatePrivateDirectory(string path)
    {
        EnsureNoLinks(path); CheckMutableAncestors(path);
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(path);
        EnsureNoLinks(path); CheckOwnedEntry(path);
    }
    private static void CheckMutableAncestors(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            if (statx(-100, directory.FullName, 0x100, 0xb, out var stat) != 0 || (stat.Mask & 0xb) != 0xb) throw new IOException("Cannot verify ownership of the update path.");
            // A sticky system temporary directory protects the owned child from other users' renames.
            if ((stat.Mode & 0x12) != 0 && !((stat.Mode & 0x200) != 0 && stat.Uid == 0)) throw new IOException("Group- or world-writable installation paths require a manual update.");
        }
    }
    public static void RequireUserPrivileges(bool elevated)
    {
        if (elevated) throw new IOException("Close MusicMachine and reopen it without administrator privileges before updating.");
    }
    internal static void CheckOwnedEntry(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (statx(-100, path, 0x100, 0xb, out var stat) != 0 || (stat.Mask & 0xb) != 0xb || stat.Uid != geteuid() || (stat.Mode & 0x12) != 0 || (stat.Mode & 0xf000) is not (0x8000 or 0x4000)) throw new IOException("Automatic updates require an installation and parent folder owned by the current user.");
    }
    // Linux UAPI statx is architecture-independent, unlike libc's struct stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx { [FieldOffset(0)] public uint Mask; [FieldOffset(20)] public uint Uid; [FieldOffset(28)] public ushort Mode; }
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)] private static extern int statx(int dirfd, string path, int flags, uint mask, out Statx buffer);
    [DllImport("libc")] private static extern uint geteuid();
}

internal static class FileAttributesReader
{
    public static FileAttributes? Get(string path)
    {
        try { return System.IO.File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
    }
}
