using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MusicMachine.App.Updating;
using MusicMachine.Core;
namespace MusicMachine.Tests;

public sealed class UpdateTests
{
    private const string OldCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", NewCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly UpdateInstallation Installed = new(new Version(0, 1, 0), "linux-x64", OldCommit, "MusicMachine.Desktop");
    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static UpdateRelease Release(byte[] bytes, string runtime = "linux-x64", string? name = null) => new(new Version(0, 2, 0), runtime, name ?? ReleaseClient.AssetName(runtime)!, ReleaseClient.AssetUrl(new Version(0, 2, 0), name ?? ReleaseClient.AssetName(runtime)!), Digest(bytes), bytes.Length, NewCommit, 1);
    private static string Manifest(UpdateRelease r, int protocol = 1) => JsonSerializer.Serialize(new { schema = 1, product = "MusicMachine", version = r.Version.ToString(), commit = r.Commit, updaterProtocol = protocol, assets = new[] { new { name = r.AssetName, size = r.Size, sha256 = r.Sha256, url = r.DownloadUrl.AbsoluteUri } } });
    private static string Api(UpdateRelease r, byte[]? manifest = null, bool prerelease = false, bool draft = false) => JsonSerializer.Serialize(new { tag_name = "v" + r.Version, prerelease, draft, assets = new[] { new { name = r.AssetName, size = r.Size, digest = "sha256:" + r.Sha256, browser_download_url = r.DownloadUrl.AbsoluteUri }, new { name = "release.json", size = (long)(manifest?.Length ?? 2), digest = "sha256:" + Digest(manifest ?? "{}"u8.ToArray()), browser_download_url = ReleaseClient.AssetUrl(r.Version, "release.json").AbsoluteUri } } });
    [Theory]
    [InlineData("0.0.0")][InlineData("1.2.3")][InlineData("21.100.999")]
    public void StableVersionsAreExact(string value) => Assert.Equal(value, ReleaseClient.ParseVersion(value).ToString());
    [Theory]
    [InlineData("1.2")][InlineData("1.2.3.4")][InlineData("v1.2.3")][InlineData("1.2.3-beta")][InlineData("01.2.3")][InlineData("1.2.-1")][InlineData(" 1.2.3")]
    public void InvalidVersionsAreRejected(string value) => Assert.Throws<InvalidDataException>(() => ReleaseClient.ParseVersion(value));
    [Theory]
    [InlineData("linux-x64", "MusicMachine-linux-x64.tar.gz")][InlineData("win-x64", "MusicMachine-win-x64.zip")]
    public void SelectsMatchingStablePlatform(string runtime, string name)
    {
        var r = Release([1, 2, 3], runtime); var installed = Installed with { Runtime = runtime };
        Assert.Equal(name, ReleaseClient.Select(Api(r), Manifest(r), installed)!.AssetName);
        Assert.Null(ReleaseClient.Select(Api(r, prerelease: true), "not-json", installed));
        Assert.Null(ReleaseClient.Select(Api(r, draft: true), "not-json", installed));
        Assert.Null(ReleaseClient.Select(Api(r), "not-json", installed with { Version = new Version(0, 2, 0) }));
        Assert.Null(ReleaseClient.Select(Api(r), "not-json", installed with { Version = new Version(1, 0, 0) }));
    }
    [Fact]
    public void RegisteredWindowsRequiresSetupAndPublishedStagingProtocol()
    {
        var r = Release([1], "win-x64", "MusicMachine-win-x64-setup.exe"); var installed = Installed with { Runtime = "win-x64", InstallerManaged = true };
        Assert.Equal(r.AssetName, ReleaseClient.Select(Api(r), Manifest(r), installed)!.AssetName);
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Select(Api(r), Manifest(r, 0), installed));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Select(Api(r), Manifest(r), Installed with { Runtime = "win-x64" }));
    }
    [Theory]
    [InlineData("\"schema\":1", "\"schema\":2")]
    [InlineData("\"product\":\"MusicMachine\"", "\"product\":\"Other\"")]
    [InlineData("\"version\":\"0.2.0\"", "\"version\":\"0.3.0\"")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "bad-commit")]
    [InlineData("\"size\":3", "\"size\":0")]
    [InlineData("\"size\":3", "\"size\":536870913")]
    [InlineData("github.com/isaiahpettingill", "evil.example/isaiahpettingill")]
    [InlineData("https://github.com/", "http://github.com/")]
    [InlineData("/releases/download/v0.2.0/", "/releases/latest/download/")]
    [InlineData("MusicMachine/releases/", "Other/releases/")]
    [InlineData("\"schema\":1", "\"schema\":1,\"schema\":1")]
    public void MaliciousOrMismatchedManifestFailsClosed(string original, string replacement)
    {
        var r = Release([1, 2, 3]); Assert.ThrowsAny<Exception>(() => ReleaseClient.Select(Api(r), Manifest(r).Replace(original, replacement), Installed));
    }
    [Fact]
    public void RejectsApiUrlSizeHashAndDuplicateAssets()
    {
        var r = Release([1, 2, 3]); var api = Api(r); var manifest = Manifest(r);
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Select(api.Replace("\"size\":3", "\"size\":4"), manifest, Installed));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Select(api.Replace(r.Sha256, new string('a', 64)), manifest, Installed));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Select(api.Replace("github.com/", "github.com.evil.example/"), manifest, Installed));
        using var doc = JsonDocument.Parse(manifest); var asset = doc.RootElement.GetProperty("assets")[0].GetRawText();
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Select(api, manifest.Replace("[" + asset + "]", "[" + asset + "," + asset + "]"), Installed));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Select(api, manifest, Installed with { Runtime = "linux-arm64" }));
    }
    [Fact]
    public async Task CheckUsesPinnedApiThenExactManifestAndHandlesFailures()
    {
        var r = Release([1, 2, 3]); var manifest = Encoding.UTF8.GetBytes(Manifest(r)); var urls = new List<string>();
        using var client = new HttpClient(new Handler((request, _) => { urls.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(Response(urls.Count == 1 ? Encoding.UTF8.GetBytes(Api(r, manifest)) : manifest)); }));
        Assert.Equal(r, await ReleaseClient.Check(Installed, client: client));
        Assert.Equal("https://api.github.com/repos/isaiahpettingill/MusicMachine/releases/latest", urls[0]); Assert.Equal(ReleaseClient.AssetUrl(r.Version, "release.json").AbsoluteUri, urls[1]);
        using var failure = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await Assert.ThrowsAsync<HttpRequestException>(() => ReleaseClient.Check(Installed, client: failure));
        using var oversized = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new byte[ReleaseClient.MaximumMetadataSize + 1]))));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseClient.Check(Installed, client: oversized));
    }
    [Fact]
    public async Task CheckCancellationIsBounded()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        using var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response([]); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReleaseClient.Check(Installed, cancellation.Token, client));
    }
    [Fact]
    public async Task DownloadVerifiesBytesAndReplacesOnlyAfterSuccess()
    {
        using var f = new Fixture(); var bytes = "valid package"u8.ToArray(); var release = Release(bytes); var dest = Path.Combine(f.Root, release.AssetName); File.WriteAllText(dest, "old cached package");
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(bytes))));
        Assert.Equal(dest, await ReleaseClient.Download(release, f.Root, client: client)); Assert.Equal(bytes, File.ReadAllBytes(dest)); Assert.Empty(Directory.GetFiles(f.Root, "*.partial"));
    }
    [Theory]
    [InlineData("truncated")][InlineData("hash")][InlineData("oversized")][InlineData("http")]
    public async Task FailedDownloadPreservesExistingFileAndRemovesPartial(string kind)
    {
        using var f = new Fixture(); var release = Release([1, 2, 3]); var dest = Path.Combine(f.Root, release.AssetName); File.WriteAllText(dest, "keep");
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(kind == "http" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response(kind == "truncated" ? [1, 2] : kind == "hash" ? [3, 2, 1] : [1, 2, 3, 4]))));
        await Assert.ThrowsAnyAsync<Exception>(() => ReleaseClient.Download(release, f.Root, client: client)); Assert.Equal("keep", File.ReadAllText(dest)); Assert.Empty(Directory.GetFiles(f.Root, "*.partial"));
    }
    [Fact]
    public async Task DownloadRejectsUntrustedUrlsRedirectsAndCancel()
    {
        using var f = new Fixture(); var r = Release([1]); var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.example/package"); return Task.FromResult(response); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseClient.Download(r with { DownloadUrl = new Uri("https://evil.example/package") }, f.Root, client: client)); Assert.Equal(0, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseClient.Download(r, f.Root, client: client)); Assert.Equal(1, calls);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        using var slow = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response([]); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReleaseClient.Download(r, f.Root, cancellation: cancellation.Token, client: slow)); Assert.Empty(Directory.GetFiles(f.Root, "*.partial"));
    }
    [Theory]
    [InlineData("../escape")][InlineData("/escape")][InlineData("docs/../../escape")][InlineData("docs\\escape")][InlineData("docs/file:stream")][InlineData("docs/CON")][InlineData("docs/trailing.")][InlineData("docs/trailing ")][InlineData("docs/a//b")][InlineData("unrelated.song")]
    public void ArchiveRejectsUnsafePaths(string path)
    {
        using var f = new Fixture(); var zip = f.Zip((path, "payload"u8.ToArray(), 0));
        Assert.Throws<InvalidDataException>(() => UpdateArchive.Extract(zip, Path.Combine(f.Root, "out"))); Assert.False(File.Exists(Path.Combine(f.Root, "escape")));
    }
    [Fact]
    public void ZipRejectsLinksDuplicatePathsAndExpansionBomb()
    {
        using var f = new Fixture(); var link = f.Zip(("docs/link", "target"u8.ToArray(), 0xa000 << 16));
        Assert.Throws<InvalidDataException>(() => UpdateArchive.Extract(link, Path.Combine(f.Root, "link")));
        var duplicate = f.Zip(("docs/A.txt", [1], 0), ("docs/a.txt", [2], 0)); Assert.Throws<InvalidDataException>(() => UpdateArchive.Extract(duplicate, Path.Combine(f.Root, "dup")));
        var bomb = f.Zip(("README.md", [1], 0)); var data = File.ReadAllBytes(bomb);
        for (var i = 0; i < data.Length - 46; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == 0x02014b50) { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i + 24), (uint)UpdateArchive.MaximumEntrySize + 1); break; }
        File.WriteAllBytes(bomb, data); Assert.Throws<InvalidDataException>(() => UpdateArchive.Extract(bomb, Path.Combine(f.Root, "bomb")));
    }
    [Theory]
    [InlineData(TarEntryType.SymbolicLink)][InlineData(TarEntryType.HardLink)][InlineData(TarEntryType.Fifo)]
    public void TarRejectsLinksAndSpecialFiles(TarEntryType type)
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "evil.tar.gz");
        using (var output = File.Create(path)) using (var gzip = new GZipStream(output, CompressionMode.Compress)) using (var tar = new TarWriter(gzip))
        { var entry = new PaxTarEntry(type, "docs/link"); if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "../../escape"; tar.WriteEntry(entry); }
        Assert.Throws<InvalidDataException>(() => UpdateArchive.Extract(path, Path.Combine(f.Root, "out")));
    }
    [Fact]
    public void ZipCentralDirectoryCountIsBoundedBeforeMaterialization()
    {
        using var f = new Fixture(); var zip = f.Zip(("README.md", [1], 0)); var data = File.ReadAllBytes(zip);
        var offset = data.Length - 22; BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 8), 20000); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 10), 20000);
        File.WriteAllBytes(zip, data); Assert.Throws<InvalidDataException>(() => UpdateArchive.Extract(zip, Path.Combine(f.Root, "out")));
    }
    [Fact]
    public void LinuxNamedPipesAreRejectedBeforeMetadataReads()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(); var target = f.Install(); var release = Path.Combine(target, "release.json"); File.Delete(release);
        Assert.Equal(0, mkfifo(release, 0x180)); Assert.Null(ReleaseClient.ReadInstallation(target));
        var plan = Path.Combine(f.Root, "plan.json"); Assert.Equal(0, mkfifo(plan, 0x180)); Assert.Throws<IOException>(() => UpdateInstaller.ReadPlan(plan));
        var recovery = Path.Combine(f.Root, "resume.json"); Assert.Equal(0, mkfifo(recovery, 0x180)); Assert.Throws<IOException>(() => UpdateRecovery.Read(f.Root));
    }
    [DllImport("libc", SetLastError = true)] private static extern int mkfifo(string path, uint mode);
    [Fact]
    public void ArchiveRequiresEmptyUnlinkedDestinationAndSupportsCancellation()
    {
        using var f = new Fixture(); var zip = f.Zip(("README.md", [1], 0)); var dest = Path.Combine(f.Root, "out"); Directory.CreateDirectory(dest); File.WriteAllText(Path.Combine(dest, "mine.song"), "mine");
        Assert.Throws<IOException>(() => UpdateArchive.Extract(zip, dest)); Assert.Equal("mine", File.ReadAllText(Path.Combine(dest, "mine.song")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => UpdateArchive.Extract(zip, Path.Combine(f.Root, "cancel"), cancellation.Token));
        if (!OperatingSystem.IsWindows()) { var alias = Path.Combine(f.Root, "alias"); Directory.CreateSymbolicLink(alias, dest); Assert.Throws<IOException>(() => UpdateArchive.Extract(zip, alias)); }
    }
    [NonElevatedInstallFact]
    public void PrepareAndApplyRetainUnrelatedFilesAndRollbackBackup()
    {
        using var f = new Fixture(); var target = f.Install(); File.WriteAllText(Path.Combine(target, "my-song.song"), "keep"); Directory.CreateDirectory(Path.Combine(target, "docs")); File.WriteAllText(Path.Combine(target, "docs", "my-notes.txt"), "notes");
        var archive = f.Payload(); var release = Release(File.ReadAllBytes(archive)); var work = Path.Combine(f.Root, "work");
        var plan = UpdateInstaller.Prepare(archive, release, target, work);
        Assert.Equal(new Version(0, 1, 0), ReleaseClient.ReadInstallation(target)!.Version); Assert.Equal("keep", File.ReadAllText(Path.Combine(plan.Prepared, "my-song.song")));
        Assert.Equal(plan.Token, UpdateInstaller.ReadPlan(Path.Combine(work, "plan.json")).Token);
        UpdateInstaller.Apply(plan); Assert.Equal(new Version(0, 2, 0), ReleaseClient.ReadInstallation(target)!.Version);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "my-song.song"))); Assert.Equal("notes", File.ReadAllText(Path.Combine(target, "docs", "my-notes.txt"))); Assert.Equal(new Version(0, 1, 0), ReleaseClient.ReadInstallation(plan.Backup)!.Version);
    }
    [NonElevatedInstallFact]
    public void ConcurrentApplyIsRefusedUnderInstallationLock()
    {
        using var f = new Fixture(); var target = f.Install(); var archive = f.Payload(); var release = Release(File.ReadAllBytes(archive));
        var first = UpdateInstaller.Prepare(archive, release, target, Path.Combine(f.Root, "first"));
        var second = UpdateInstaller.Prepare(archive, release, target, Path.Combine(f.Root, "second")); var checkedLock = false;
        UpdateInstaller.Apply(first, (from, to) =>
        {
            if (!checkedLock) { checkedLock = true; Assert.Throws<IOException>(() => UpdateInstaller.Apply(second)); }
            Directory.Move(from, to);
        });
        Assert.True(checkedLock); Assert.Throws<InvalidDataException>(() => UpdateInstaller.Apply(second));
        Assert.Equal(new Version(0, 2, 0), ReleaseClient.ReadInstallation(target)!.Version);
        Assert.Equal(new Version(0, 1, 0), ReleaseClient.ReadInstallation(first.Backup)!.Version);
    }
    [NonElevatedInstallFact]
    public void ApplyRestoresOldDirectoryWhenSecondRenameFails()
    {
        using var f = new Fixture(); var target = f.Install(); var archive = f.Payload(); var plan = UpdateInstaller.Prepare(archive, Release(File.ReadAllBytes(archive)), target, Path.Combine(f.Root, "work")); var calls = 0;
        Assert.Throws<IOException>(() => UpdateInstaller.Apply(plan, (from, to) => { if (++calls == 2) throw new IOException("Injected swap failure"); Directory.Move(from, to); }));
        Assert.Equal(3, calls); Assert.Equal(new Version(0, 1, 0), ReleaseClient.ReadInstallation(target)!.Version); Assert.True(Directory.Exists(plan.Prepared)); Assert.False(Directory.Exists(plan.Backup));
    }
    [NonElevatedInstallFact]
    public void ApplyRetriesWindowsSharingLocksAndRejectsChangedInstall()
    {
        using var f = new Fixture(); var target = f.Install(); var archive = f.Payload(); var plan = UpdateInstaller.Prepare(archive, Release(File.ReadAllBytes(archive)), target, Path.Combine(f.Root, "work")); var calls = 0;
        UpdateInstaller.Apply(plan, (from, to) => { if (++calls < 3) throw new SharingFailure(); Directory.Move(from, to); }, _ => { }); Assert.Equal(4, calls);
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Apply(plan));
    }
    [NonElevatedInstallFact]
    public void PrepareRejectsMismatchesHashTamperingAndSymlinksWithoutChangingInstall()
    {
        using var f = new Fixture(); var target = f.Install(); var archive = f.Payload(); var r = Release(File.ReadAllBytes(archive));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Prepare(archive, r with { Sha256 = new string('a', 64) }, target, Path.Combine(f.Root, "work1")));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Prepare(archive, r with { Commit = OldCommit }, target, Path.Combine(f.Root, "work2")));
        Assert.Equal(new Version(0, 1, 0), ReleaseClient.ReadInstallation(target)!.Version); Assert.Empty(Directory.GetDirectories(f.Root, "*.update-*"));
        if (!OperatingSystem.IsWindows()) { File.CreateSymbolicLink(Path.Combine(target, "linked.song"), Path.Combine(f.Root, "outside")); Assert.Throws<IOException>(() => UpdateInstaller.Prepare(archive, r, target, Path.Combine(f.Root, "work3"))); }
    }
    [Fact]
    public void OnlyRecognizedLinuxInstallerAliasCanResolve()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(); var original = f.Install(); var managed = Path.Combine(f.Root, "managed"); var target = Path.Combine(managed, "releases", "build.fixture", "app"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); Directory.Move(original, target);
        var marker = Path.Combine(managed, ".installer-owned"); File.WriteAllText(marker, "MusicMachine per-user installation v1\n"); var alias = Path.Combine(managed, "current"); Directory.CreateSymbolicLink(alias, target);
        Assert.Equal(target, UpdatePaths.ResolveInstallation(alias));
        File.WriteAllText(marker, new string('x', 129)); Assert.Throws<IOException>(() => UpdatePaths.ResolveInstallation(alias));
        var arbitrary = Path.Combine(f.Root, "other-alias"); Directory.CreateSymbolicLink(arbitrary, target); Assert.Throws<IOException>(() => UpdatePaths.ResolveInstallation(arbitrary));
    }
    [Fact]
    public void ForeignWritableLinuxInstallOrParentIsRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture(); var target = f.Install(); var original = File.GetUnixFileMode(target);
        try
        {
            File.SetUnixFileMode(target, original | UnixFileMode.OtherWrite);
            Assert.Throws<IOException>(() => UpdatePaths.ValidateInstallation(target, "linux-x64"));
            File.SetUnixFileMode(target, original); var parent = File.GetUnixFileMode(f.Root);
            File.SetUnixFileMode(f.Root, parent | UnixFileMode.GroupWrite);
            try { Assert.Throws<IOException>(() => UpdatePaths.ValidateInstallation(target, "linux-x64")); }
            finally { File.SetUnixFileMode(f.Root, parent); }
        }
        finally { File.SetUnixFileMode(target, original); }
    }
    [Fact]
    public void ElevatedWindowsProcessesAreBlocked()
    {
        UpdatePaths.RequireUserPrivileges(false);
        Assert.Throws<IOException>(() => UpdatePaths.RequireUserPrivileges(true));
    }
    [Fact]
    public void DevelopmentAndSystemLocationsCannotSelfUpdate()
    {
        using var f = new Fixture(); var target = f.Install(); File.WriteAllText(Path.Combine(target, ".git"), "gitdir: somewhere"); Assert.Throws<IOException>(() => UpdatePaths.ValidateInstallation(target, "linux-x64")); File.Delete(Path.Combine(target, ".git"));
        File.WriteAllText(Path.Combine(target, "MusicMachine.Desktop.runtimeconfig.json"), "{}"); Assert.Throws<IOException>(() => UpdatePaths.ValidateInstallation(target, "linux-x64"));
        if (OperatingSystem.IsLinux()) Assert.Throws<IOException>(() => UpdatePaths.ValidateInstallation("/usr", "linux-x64"));
    }
    [Fact]
    public void RecoveryReadIsNonDestructiveAndPreservesDirtySongAndWorkspace()
    {
        using var f = new Fixture(); var editor = new SongEditor(DemoSong.Create()); editor.Change(s => s.Title = "Do not lose this");
        UpdateRecovery.Save(editor, null, editor.Song.Patterns[0].Id, editor.Song.Instruments[0].Id, "Instrument", f.Root);
        var one = UpdateRecovery.Read(f.Root); var two = UpdateRecovery.Read(f.Root); Assert.True(one.Dirty); Assert.Null(one.FilePath); Assert.Equal("Instrument", one.Workspace); Assert.Equal(one.SongBytes, two.SongBytes); Assert.Equal("Do not lose this", SongFile.Read(one.SongBytes).Title); Assert.True(File.Exists(Path.Combine(f.Root, "resume.song")));
    }
    [NonElevatedInstallFact]
    public void ApprovalRequiresReadyHelperAndValidRecoveryAndAbandonKeepsRecovery()
    {
        using var f = new Fixture(); var target = f.Install(); var archive = f.Payload(); var work = Path.Combine(f.Root, "work"); var plan = UpdateInstaller.Prepare(archive, Release(File.ReadAllBytes(archive)), target, work);
        Assert.Throws<IOException>(() => UpdateInstaller.Approve(plan, work)); File.WriteAllText(Path.Combine(work, "helper.ready"), plan.Token);
        Assert.ThrowsAny<Exception>(() => UpdateInstaller.Approve(plan, work)); Assert.False(File.Exists(Path.Combine(work, "apply.approved")));
        UpdateRecovery.Save(new SongEditor(DemoSong.Create()), null, "", "", "Tracker", work); UpdateInstaller.Abandon(plan, work);
        Assert.False(Directory.Exists(plan.Prepared)); Assert.True(File.Exists(Path.Combine(work, "resume.song"))); Assert.Throws<IOException>(() => UpdateInstaller.Approve(plan, work));
    }
    [NonElevatedInstallFact]
    public void ApprovalDoesNotApplyAndCannotBeAbandonedAfterApproval()
    {
        using var f = new Fixture(); var target = f.Install(); var archive = f.Payload(); var work = Path.Combine(f.Root, "work"); var plan = UpdateInstaller.Prepare(archive, Release(File.ReadAllBytes(archive)), target, work);
        File.WriteAllText(Path.Combine(work, "helper.ready"), plan.Token); UpdateRecovery.Save(new SongEditor(DemoSong.Create()), null, "", "", "Tracker", work); UpdateInstaller.Approve(plan, work); UpdateInstaller.Abandon(plan, work);
        Assert.True(Directory.Exists(plan.Prepared)); Assert.Equal(new Version(0, 1, 0), ReleaseClient.ReadInstallation(target)!.Version); Assert.Equal(plan.Token, File.ReadAllText(Path.Combine(work, "apply.approved")));
    }
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class SharingFailure : IOException { public SharingFailure() => HResult = unchecked((int)0x80070020); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MusicMachine-update-tests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string Install() { var dir = Path.Combine(Root, "app"); Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "release.json"), Metadata("0.1.0", OldCommit)); File.WriteAllText(Path.Combine(dir, "MusicMachine.Desktop"), "old executable"); return dir; }
        private static byte[] Metadata(string version, string commit) => JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, product = "MusicMachine", version, runtime = "linux-x64", commit, executable = "MusicMachine.Desktop", repository = ReleaseClient.Repository, updaterProtocol = 1 });
        public string Payload()
        {
            var path = Path.Combine(Root, "MusicMachine-linux-x64.tar.gz"); using var output = File.Create(path); using var gzip = new GZipStream(output, CompressionMode.Compress); using var tar = new TarWriter(gzip);
            foreach (var (name, data) in new[] { ("./release.json", Metadata("0.2.0", NewCommit)), ("./MusicMachine.Desktop", "new executable"u8.ToArray()), ("./docs/new.txt", "new documentation"u8.ToArray()) }) { using var stream = new MemoryStream(data); tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = stream }); }
            return path;
        }
        public string Zip(params (string Name, byte[] Data, int Attributes)[] entries)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip"); using var archive = ZipFile.Open(path, ZipArchiveMode.Create); foreach (var entry in entries) { var file = archive.CreateEntry(entry.Name); file.ExternalAttributes = entry.Attributes; using var output = file.Open(); output.Write(entry.Data); } return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}

// CI's Windows runners may be elevated. Never weaken the production privilege guard to run fixture swaps.
// Metadata/archive/recovery tests still run there; full rename fixtures run as the ordinary Linux user.
public sealed class NonElevatedInstallFactAttribute : FactAttribute
{
    public NonElevatedInstallFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) Skip = "Run fixture installation tests from a non-elevated Windows test process.";
        }
    }
}
