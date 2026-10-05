using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MusicMachine.App.Updating;

public sealed record UpdateInstallation(Version Version, string Runtime, string Commit, string Executable, bool InstallerManaged = false);
public sealed record UpdateRelease(Version Version, string Runtime, string AssetName, Uri DownloadUrl, string Sha256, long Size, string Commit, int UpdaterProtocol = 0);

/// <summary>GitHub HTTPS and published SHA-256 provide transport/integrity checks, not independent publisher signatures.</summary>
public static class ReleaseClient
{
    public const string Repository = "isaiahpettingill/MusicMachine";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases/latest";
    public const long MaximumPackageSize = 512L * 1024 * 1024;
    public const int MaximumMetadataSize = 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicMachine-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    public static string? AssetName(string runtime) => runtime switch { "win-x64" => "MusicMachine-win-x64.zip", "linux-x64" => "MusicMachine-linux-x64.tar.gz", _ => null };
    public static string? AssetName(UpdateInstallation installation) => installation.InstallerManaged && installation.Runtime == "win-x64" ? "MusicMachine-win-x64-setup.exe" : AssetName(installation.Runtime);
    public static string? ExecutableName(string runtime) => runtime switch { "win-x64" => "MusicMachine.Desktop.exe", "linux-x64" => "MusicMachine.Desktop", _ => null };
    public static Version ParseVersion(string value)
    {
        if (!Version.TryParse(value, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != value)
            throw new InvalidDataException("A stable X.Y.Z release version is required.");
        return version;
    }
    internal static string Hash(string value, int length)
    {
        if (value.Length != length || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid release checksum or commit.");
        return value.ToLowerInvariant();
    }
    internal static JsonDocument Json(string value)
    {
        if (value.Length > MaximumMetadataSize) throw new InvalidDataException("Release metadata is too large.");
        var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
        try { Unique(document.RootElement); return document; } catch { document.Dispose(); throw; }
        static void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate release metadata field."); Unique(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
        }
    }
    internal static string String(JsonElement json, string name) => json.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing " + name + ".");
    private static void Header(JsonElement root)
    {
        if (root.GetProperty("schema").GetInt32() != 1 || String(root, "product") != "MusicMachine") throw new InvalidDataException("Unsupported release metadata.");
    }
    public static UpdateInstallation? ReadInstallation(string directory)
    {
        try
        {
            var file = Path.Combine(directory, "release.json"); UpdatePaths.EnsureNoLinks(file); UpdatePaths.CheckOwnedEntry(file);
            if (new FileInfo(file).Length > MaximumMetadataSize) return null;
            using var document = Json(File.ReadAllText(file)); var root = document.RootElement; Header(root);
            var runtime = String(root, "runtime"); var executable = ExecutableName(runtime);
            if (executable is null || String(root, "executable") != executable || String(root, "repository") != Repository || !File.Exists(Path.Combine(directory, executable))) return null;
            UpdatePaths.CheckOwnedEntry(Path.Combine(directory, executable));
            return new(ParseVersion(String(root, "version")), runtime, Hash(String(root, "commit"), 40), executable, runtime == "win-x64" && File.Exists(Path.Combine(directory, "Uninstall.exe")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
    public static Uri AssetUrl(Version version, string name)
    {
        if (name.Length is 0 or > 120 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || name is "." or "..") throw new InvalidDataException("Invalid release asset name.");
        return new($"https://github.com/{Repository}/releases/download/v{ParseVersion(version.ToString())}/{name}");
    }
    public static UpdateRelease? Select(string releaseJson, string manifestJson, UpdateInstallation installed)
    {
        using var releaseDoc = Json(releaseJson); var release = releaseDoc.RootElement;
        var version = Candidate(release, installed); if (version is null) return null;
        using var manifestDoc = Json(manifestJson); var manifest = manifestDoc.RootElement; Header(manifest);
        if (ParseVersion(String(manifest, "version")) != version) throw new InvalidDataException("Release versions do not match.");
        var protocol = manifest.TryGetProperty("updaterProtocol", out var p) ? p.GetInt32() : 0;
        if (installed.InstallerManaged && protocol != 1) throw new InvalidDataException("This release installer does not support safe staged updates. Download it manually.");
        var commit = Hash(String(manifest, "commit"), 40); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); UpdateRelease? result = null;
        foreach (var asset in manifest.GetProperty("assets").EnumerateArray())
        {
            var name = String(asset, "name"); var expected = AssetUrl(version, name); var size = asset.GetProperty("size").GetInt64(); var hash = Hash(String(asset, "sha256"), 64);
            if (!names.Add(name) || names.Count > 50 || String(asset, "url") != expected.AbsoluteUri || size is <= 0 or > MaximumPackageSize) throw new InvalidDataException("Invalid or untrusted release asset metadata.");
            if (name != AssetName(installed)) continue;
            var github = FindAsset(release, name, version, MaximumPackageSize);
            if (github.GetProperty("size").GetInt64() != size) throw new InvalidDataException("GitHub and the release manifest disagree on the package size.");
            if (github.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null && digest.GetString() != "sha256:" + hash)
                throw new InvalidDataException("GitHub and the release manifest disagree on the package checksum.");
            result = new(version, installed.Runtime, name, expected, hash, size, commit, protocol);
        }
        return result ?? throw new InvalidDataException("The release does not yet contain this platform's portable package. Try again after publication finishes.");
    }
    private static Version? Candidate(JsonElement root, UpdateInstallation installed)
    {
        if (AssetName(installed.Runtime) is null) throw new InvalidDataException("Unsupported desktop runtime.");
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = String(root, "tag_name");
        if (!tag.StartsWith('v')) throw new InvalidDataException("Unexpected release tag.");
        var version = ParseVersion(tag[1..]); return version > installed.Version ? version : null;
    }
    private static JsonElement FindAsset(JsonElement release, string name, Version version, long maximum)
    {
        JsonElement? found = null; var count = 0;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (++count > 100) throw new InvalidDataException("Too many release assets.");
            if (String(asset, "name") != name) continue;
            if (found is not null || String(asset, "browser_download_url") != AssetUrl(version, name).AbsoluteUri || asset.GetProperty("size").GetInt64() is var size && (size <= 0 || size > maximum)) throw new InvalidDataException("Invalid GitHub asset metadata.");
            found = asset;
        }
        return found ?? throw new InvalidDataException("The release is not fully published yet. Try again shortly.");
    }
    public static async Task<UpdateRelease?> Check(UpdateInstallation installation, CancellationToken cancellation = default, HttpClient? client = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var json = await ReadText(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), MaximumMetadataSize, client ?? Client, timeout.Token);
        using var document = Json(json); var version = Candidate(document.RootElement, installation); if (version is null) return null;
        var manifest = FindAsset(document.RootElement, "release.json", version, MaximumMetadataSize);
        var bytes = await ReadBytes(AssetUrl(version, "release.json"), manifest.GetProperty("size").GetInt64(), client ?? Client, timeout.Token, exact: true);
        if (manifest.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null && digest.GetString() != "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) throw new InvalidDataException("Release manifest checksum verification failed.");
        return Select(json, System.Text.Encoding.UTF8.GetString(bytes), installation);
    }
    internal static void Validate(UpdateRelease release)
    {
        if (!(AssetName(release.Runtime) == release.AssetName || release.Runtime == "win-x64" && release.AssetName == "MusicMachine-win-x64-setup.exe") || release.DownloadUrl.AbsoluteUri != AssetUrl(release.Version, release.AssetName).AbsoluteUri || release.Size is <= 0 or > MaximumPackageSize) throw new InvalidDataException("Unexpected update package.");
        Hash(release.Sha256, 64); Hash(release.Commit, 40);
        if (release.AssetName.EndsWith("-setup.exe", StringComparison.Ordinal) && release.UpdaterProtocol != 1) throw new InvalidDataException("The installer has no safe-update protocol.");
    }
    public static async Task<string> Download(UpdateRelease release, string directory, IProgress<double>? progress = null, CancellationToken cancellation = default, HttpClient? client = null)
    {
        Validate(release); UpdatePaths.CreatePrivateDirectory(directory);
        var destination = Path.Combine(directory, release.AssetName); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        UpdatePaths.EnsureNoLinks(destination);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var response = await Send(release.DownloadUrl, client ?? Client, timeout.Token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } announced && announced != release.Size) throw new InvalidDataException("Unexpected download length.");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += count; if (total > release.Size) throw new InvalidDataException("Download exceeds its published size.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); progress?.Report((double)total / release.Size);
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Download integrity check failed. The installed application is unchanged.");
                output.Flush(flushToDisk: true);
            }
            timeout.Token.ThrowIfCancellationRequested(); UpdatePaths.EnsureNoLinks(destination); File.Move(temporary, destination, overwrite: true); return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<string> ReadText(Uri uri, int limit, HttpClient client, CancellationToken ct) => System.Text.Encoding.UTF8.GetString(await ReadBytes(uri, limit, client, ct, false));
    private static async Task<byte[]> ReadBytes(Uri uri, long limit, HttpClient client, CancellationToken ct, bool exact)
    {
        using var response = await Send(uri, client, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && (length > limit || exact && length != limit)) throw new InvalidDataException("Invalid metadata length.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > limit) throw new InvalidDataException("Metadata exceeds its size limit."); output.Write(buffer, 0, count); }
        if (exact && output.Length != limit) throw new InvalidDataException("Truncated release metadata.");
        return output.ToArray();
    }
    private static async Task<HttpResponseMessage> Send(Uri uri, HttpClient client, CancellationToken ct)
    {
        for (var redirect = 0; ; redirect++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            // The production client has redirects disabled. Injected clients must preserve this contract.
            if (response.RequestMessage?.RequestUri is { } final && !AllowedRedirect(final)) { response.Dispose(); throw new InvalidDataException("Untrusted download redirect."); }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (redirect >= 4 || location is null) throw new InvalidDataException("Too many or invalid download redirects.");
            uri = new Uri(uri, location); if (!AllowedRedirect(uri)) throw new InvalidDataException("Untrusted download redirect.");
        }
    }
    private static bool AllowedRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo == "" && uri.Fragment == "" && uri.Host is "github.com" or "api.github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
}
