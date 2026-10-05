using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using MusicMachine.App.Updating;

namespace MusicMachine.Tests;

public sealed class LinuxInstallerTests
{
    [Fact]
    public async Task GroupWritableUmaskInstallsAndRepairsUpdaterEligiblePayload()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var alias = fixture.Current;
        var unchanged = new[] { fixture.Home, fixture.Commands, fixture.Unrelated };
        var modes = unchanged.ToDictionary(path => path, File.GetUnixFileMode);
        await fixture.Install();
        fixture.AssertEligible();
        fixture.AssertPayloadPermissions();

        // Simulate the managed entries left by the older installer with umask 0002.
        foreach (var path in new[] { fixture.Root, Path.Combine(fixture.Root, "releases"), Path.Combine(fixture.Root, ".installer-owned") })
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.GroupWrite);
        Assert.Throws<IOException>(() => UpdatePaths.ValidateInstallation(alias, "linux-x64"));
        await fixture.Install();
        await fixture.Install(); // A second repair must remain idempotent.
        fixture.AssertEligible();
        fixture.AssertPayloadPermissions();
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "releases")));
        foreach (var path in unchanged) Assert.Equal(modes[path], File.GetUnixFileMode(path));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Unrelated, "song.song")));
    }

    [Fact]
    public async Task SharedAncestorIsRefusedWithoutChangingUserDirectories()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var alias = fixture.Current;
        await fixture.Install();
        fixture.AssertEligible();
        var original = File.GetUnixFileMode(fixture.Base);
        File.SetUnixFileMode(fixture.Base, original | UnixFileMode.GroupWrite);
        var current = new DirectoryInfo(fixture.Current).LinkTarget;
        var sharedMode = File.GetUnixFileMode(fixture.Base);
        try
        {
            // A private HOME beneath a shared ancestor must not bypass the updater guard.
            var failure = Assert.Throws<IOException>(() => UpdatePaths.ValidateInstallation(alias, "linux-x64"));
            Assert.Contains("writable installation paths", failure.Message);
            var result = await fixture.Install(success: false);
            Assert.Contains("group- or world-writable", result);
            Assert.Contains("XDG_DATA_HOME", result);
            Assert.Contains(fixture.Base, result);
            Assert.Equal(sharedMode, File.GetUnixFileMode(fixture.Base));
            Assert.Equal(current, new DirectoryInfo(fixture.Current).LinkTarget);
        }
        finally { File.SetUnixFileMode(fixture.Base, original); }
    }

    [Fact]
    public async Task RepairNeverChangesPermissionsThroughManagedDirectoryOrMarkerLinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        await fixture.Install();
        var releases = Path.Combine(fixture.Root, "releases");
        var saved = releases + ".saved";
        Directory.Move(releases, saved);
        Directory.CreateSymbolicLink(releases, fixture.Unrelated);
        var mode = File.GetUnixFileMode(fixture.Unrelated);
        await fixture.Install(success: false);
        Assert.Equal(mode, File.GetUnixFileMode(fixture.Unrelated));
        Directory.Delete(releases);
        Directory.Move(saved, releases);
        var marker = Path.Combine(fixture.Root, ".installer-owned");
        var outsideMarker = Path.Combine(fixture.Unrelated, "marker");
        File.Move(marker, outsideMarker);
        File.SetUnixFileMode(outsideMarker, (UnixFileMode)0x1b4); // 0664
        File.CreateSymbolicLink(marker, outsideMarker);
        await fixture.Install(success: false);
        Assert.Equal((UnixFileMode)0x1b4, File.GetUnixFileMode(outsideMarker));
    }

    [SupportedOSPlatform("linux")]
    private sealed class Fixture : IDisposable
    {
        public string Base { get; } = Directory.CreateTempSubdirectory("MusicMachine-installer-").FullName;
        public string Home => Path.Combine(Base, "home with spaces");
        public string Commands => Path.Combine(Home, "commands");
        public string Unrelated => Path.Combine(Home, "shared songs");
        public string Root => Path.Combine(Home, ".local", "share", "musicmachine");
        public string Current => Path.Combine(Root, "current");
        private string Archive => Path.Combine(Base, "MusicMachine-linux-x64.tar.gz");
        public Fixture()
        {
            Directory.CreateDirectory(Home, (UnixFileMode)0x1c0); // 0700
            foreach (var path in new[] { Commands, Unrelated })
            {
                Directory.CreateDirectory(path);
                File.SetUnixFileMode(path, (UnixFileMode)0x1fd); // 0775, unrelated user-owned directories
            }
            File.WriteAllText(Path.Combine(Unrelated, "song.song"), "keep");
            // Deliberately permissive archive modes exercise tar's umask handling. The shell
            // executable is a transparent fixture; validation calls the production updater.
            using var output = File.Create(Archive);
            using var gzip = new GZipStream(output, CompressionMode.Compress);
            using var tar = new TarWriter(gzip);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "docs") { Mode = (UnixFileMode)0x1ff });
            var metadata = JsonSerializer.Serialize(new { schema = 1, product = "MusicMachine", version = "0.1.0", runtime = "linux-x64", commit = new string('a', 40), executable = "MusicMachine.Desktop", repository = ReleaseClient.Repository, updaterProtocol = 1 });
            foreach (var (name, text) in new[] { ("MusicMachine.Desktop", "#!/bin/sh\nexit 0\n"), ("musicmachine.svg", "<svg/>"), ("release.json", metadata), ("docs/help.txt", "help") })
            {
                using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = bytes, Mode = (UnixFileMode)0x1b6 }); // 0666
            }
        }
        public async Task<string> Install(bool success = true)
        {
            var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-c", "umask 0002; exec bash \"$@\"", "installer-test", Path.Combine(AppContext.BaseDirectory, "install-linux.sh"), "--archive", Archive }) info.ArgumentList.Add(argument);
            info.Environment["HOME"] = Home;
            info.Environment.Remove("XDG_DATA_HOME");
            info.Environment["XDG_CACHE_HOME"] = Path.Combine(Home, ".cache");
            info.Environment["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config");
            info.Environment["MUSICMACHINE_BIN_DIR"] = Commands;
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            var result = await stdout + await stderr;
            Assert.True((process.ExitCode == 0) == success, result);
            return result;
        }
        public void AssertEligible()
        {
            var target = new DirectoryInfo(Current).ResolveLinkTarget(true)!.FullName;
            Assert.Equal(target, UpdatePaths.ValidateInstallation(Current, "linux-x64"));
            Assert.Equal(target, UpdatePaths.ValidateInstallation(target, "linux-x64"));
        }
        public void AssertPayloadPermissions()
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Prepend(Root))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
            Assert.NotEqual((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(Current, "MusicMachine.Desktop")) & UnixFileMode.UserExecute);
        }
        public void Dispose() => Directory.Delete(Base, recursive: true);
    }
}
