using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Batcomputer;

internal sealed record UpdateFile(string Path, long Size, string Sha256);
internal sealed record UpdateManifest(int Schema, string Version, List<UpdateFile> Files);
internal sealed record AppUpdateRelease(string Version, string Notes, Uri Download, long Size, string Sha256, FileUpdatePlan? FilePlan = null);
internal sealed record StagedAppUpdate(string Directory, UpdateManifest Manifest);

/// <summary>Fixed-origin, bounded download + validated staging. Never writes application or user files.</summary>
internal sealed partial class AppUpdateService : IDisposable
{
    public const string AssetName = "Batcomputer-update-win-x64.zip";
    public const string ManifestName = "Batcomputer.update.json";
    public const string ReleasesPage = "https://github.com/Loomirr/Batcomputer/releases";
    // All 1.0 prereleases are supported; pre-1.0 archives remain manual-only legacy releases.
    internal static bool IsSupportedRelease(string version) => AppVersion.Compare(version, "1.0.0-0") >= 0;
    internal const long MaxArchiveSize = 512L * 1024 * 1024;
    internal const long MaxExpandedSize = 2L * 1024 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Uri? _testFeed;
    private readonly string _installRoot;
    private readonly bool _preferFileUpdates;

    public AppUpdateService(Uri? testFeed = null, string? installRoot = null, bool preferFileUpdates = true)
    {
        if (testFeed != null && (testFeed.Scheme != "http" || testFeed.Host != "127.0.0.1"))
            throw new InvalidDataException("Test feeds must use http://127.0.0.1 on a local port.");
        _testFeed = testFeed;
        _installRoot = installRoot ?? AppSettings.ToolRoot;
        _preferFileUpdates = preferFileUpdates;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Batcomputer/" + AppVersion.Current);
    }

    public async Task<AppUpdateRelease?> CheckAsync(bool includeBeta, CancellationToken ct)
    {
        AppUpdateRelease? best = null;
        // Bounded pagination: don't assume the most recently published release has the highest version.
        const int pageSize = 5; // File-based releases carry hundreds of assets; keep each response bounded.
        for (var page = 1; page <= (_testFeed == null ? 6 : 1); page++)
        {
            var url = _testFeed ?? new Uri($"https://api.github.com/repos/Loomirr/Batcomputer/releases?per_page={pageSize}&page={page}");
            using var response = await GetAsync(url, metadata: true, ct);
            var bytes = await ReadBoundedAsync(response, 8 * 1024 * 1024, ct);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid releases feed.");
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean()) continue;
                var version = release.GetProperty("tag_name").GetString() ?? "";
                try
                {
                    if (!IsSupportedRelease(version)
                        || (!includeBeta && (release.GetProperty("prerelease").GetBoolean() || AppVersion.IsPrerelease(version)))
                        || AppVersion.Compare(version, AppVersion.Current) <= 0
                        || (best != null && AppVersion.Compare(version, best.Version) <= 0)) continue;
                }
                catch (Exception e) when (e is InvalidDataException or OverflowException) { continue; }
                var fileAssets = release.GetProperty("assets").EnumerateArray()
                    .Where(a => a.GetProperty("name").GetString() == FileCatalogName).ToArray();
                if (fileAssets.Length > 1) throw new InvalidDataException("Ambiguous file update catalog.");
                if (_preferFileUpdates && fileAssets.Length == 1)
                {
                    best = await ReadFileReleaseAsync(version, release, fileAssets[0], ct);
                    continue;
                }
                var matches = release.GetProperty("assets").EnumerateArray()
                    .Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
                if (matches.Length == 0) continue; // Older manual-only releases are not updater packages.
                if (matches.Length != 1) throw new InvalidDataException("Ambiguous updater assets in release.");
                var asset = matches[0];
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
                    throw new InvalidDataException("The release has no GitHub SHA-256 digest. Use the manual release download.");
                var size = asset.GetProperty("size").GetInt64();
                if (size <= 0 || size > MaxArchiveSize) throw new InvalidDataException("Update archive exceeds the download limit.");
                var download = new Uri(asset.GetProperty("browser_download_url").GetString()!);
                ValidateUrl(download, metadata: false, redirected: false);
                best = new(version, release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    download, size, digest[7..]);
            }
            if (doc.RootElement.GetArrayLength() < pageSize) break;
        }
        return best;
    }

    public async Task<StagedAppUpdate> DownloadAsync(AppUpdateRelease release, string transaction,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (!IsSupportedRelease(release.Version)) throw new InvalidDataException("Pre-1.0 releases are legacy and cannot be downloaded through the updater.");
        if (release.FilePlan != null) return await DownloadFilesAsync(release, transaction, progress, ct);
        Directory.CreateDirectory(transaction);
        UpdatePaths.RejectLinks(transaction);
        CheckSpace(transaction, release.Size + MaxExpandedSize);
        var zipPath = Path.Combine(transaction, "download.zip");
        using (var response = await GetAsync(release.Download, metadata: false, ct))
        {
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("Download length differs from release metadata.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long total = 0; int n;
            var lastProgress = Environment.TickCount64;
            while ((n = await input.ReadAsync(buffer, ct)) != 0)
            {
                total += n;
                if (total > release.Size) throw new InvalidDataException("Download exceeded its declared size.");
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                if (Environment.TickCount64 - lastProgress >= 100 || total == release.Size)
                {
                    progress?.Report($"Downloading {total / 1048576d:0.0} / {release.Size / 1048576d:0.0} MB");
                    lastProgress = Environment.TickCount64;
                }
            }
            if (total != release.Size) throw new InvalidDataException("Download was incomplete.");
        }
        progress?.Report("Verifying download and application files…");
        ct.ThrowIfCancellationRequested();
        if (!(await Task.Run(() => Hash(zipPath), ct)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update SHA-256 check failed. Nothing was installed.");
        var staged = await Task.Run(() => Stage(zipPath, transaction, release.Version, ct), ct);
        File.WriteAllText(Path.Combine(transaction, "status.txt"), "Download verified. Ready to schedule installation; installed application files are unchanged.");
        return staged;
    }

    internal static StagedAppUpdate Stage(string zipPath, string transaction, string version, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("Too many archive entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // Updater packages contain files only: no implicit directories, links, aliases or wrappers.
            UpdatePaths.ValidateRelative(entry.FullName, manifestAllowed: true);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("Links are forbidden in updates.");
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Duplicate archive path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedSize) throw new InvalidDataException("Expanded update is too large.");
        }
        if (!entries.TryGetValue(ManifestName, out var manifestEntry) || manifestEntry.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Missing or oversized updater manifest.");
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestStream, Json) ?? throw new InvalidDataException("Empty manifest.");
        ValidateManifest(manifest);
        if (AppVersion.Compare(manifest.Version, version) != 0) throw new InvalidDataException("Release and package versions differ.");
        if (entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("Archive contains unlisted files.");
        var payload = Path.Combine(transaction, "payload");
        if (Directory.Exists(payload)) throw new IOException("This update was already staged. Check again to start a fresh download.");
        Directory.CreateDirectory(payload);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Size)
                throw new InvalidDataException("Missing file or size mismatch: " + file.Path);
            var target = UpdatePaths.Resolve(payload, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
            VerifyFile(target, file);
        }
        var productVersion = ProductVersion(Path.Combine(payload, "Batcomputer.exe"));
        if (productVersion == null || AppVersion.Compare(productVersion, manifest.Version) != 0)
            throw new InvalidDataException("The executable version does not match the update manifest.");
        File.WriteAllText(Path.Combine(transaction, ManifestName), JsonSerializer.Serialize(manifest, Json));
        return new(transaction, manifest);
    }

    internal static void ValidateManifest(UpdateManifest manifest, bool requireExecutable = true)
    {
        if (manifest.Schema != 1 || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > 19999)
            throw new InvalidDataException("Unsupported updater manifest.");
        _ = AppVersion.Compare(manifest.Version, manifest.Version);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in manifest.Files)
        {
            UpdatePaths.ValidateRelative(file.Path);
            if (!paths.Add(file.Path) || file.Size < 0 || file.Size > MaxExpandedSize || !Regex.IsMatch(file.Sha256 ?? "", "^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Invalid file manifest entry.");
            total = checked(total + file.Size);
        }
        if (total > MaxExpandedSize || (requireExecutable && !paths.Contains("Batcomputer.exe"))) throw new InvalidDataException("Invalid update payload.");
    }

    internal static void VerifyFile(string path, UpdateFile file)
    {
        UpdatePaths.RejectLinks(path);
        if (new FileInfo(path).Length != file.Size || !Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File checksum mismatch: " + file.Path);
    }
    internal static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    internal static string? ProductVersion(string path)
    {
        var full = Path.GetFullPath(path);
        // Win32 version-resource APIs silently return no version above MAX_PATH without this prefix.
        if (!full.StartsWith(@"\\?\", StringComparison.Ordinal))
            full = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        return FileVersionInfo.GetVersionInfo(full).ProductVersion;
    }
    internal static void CheckSpace(string directory, long required)
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace < required + 128 * 1024 * 1024)
            throw new IOException("Not enough free disk space to stage and back up this update.");
    }

    private void ValidateUrl(Uri url, bool metadata, bool redirected)
    {
        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment)) throw new InvalidDataException("Invalid update URL.");
        if (_testFeed != null)
        {
            if (url.Scheme == "http" && url.Host == "127.0.0.1" && url.Port == _testFeed.Port) return;
        }
        else if (url.Scheme == "https" && url.IsDefaultPort)
        {
            if (metadata && url.Host == "api.github.com" && url.AbsolutePath == "/repos/Loomirr/Batcomputer/releases") return;
            if (!metadata && url.Host == "github.com" && url.AbsolutePath.StartsWith("/Loomirr/Batcomputer/releases/download/", StringComparison.Ordinal)) return;
            if (!metadata && redirected && url.Host == "release-assets.githubusercontent.com") return;
        }
        throw new InvalidDataException("Update URL is outside the trusted release source.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, bool metadata, CancellationToken ct)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            ValidateUrl(url, metadata, redirect > 0);
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                response.Dispose(); continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode; response.Dispose();
                throw new HttpRequestException(code is 403 or 429 ? "GitHub rate limit reached. Try again later." : $"Update server returned HTTP {code}.");
            }
            return response;
        }
        throw new HttpRequestException("Too many update redirects.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream(); var buffer = new byte[81920]; int n;
        while ((n = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (memory.Length + n > limit) throw new InvalidDataException("Release metadata is too large.");
            memory.Write(buffer, 0, n);
        }
        return memory.ToArray();
    }
    public void Dispose() => _http.Dispose();
}

internal static class UpdatePaths
{
    internal static void ValidateRelative(string path, bool manifestAllowed = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path)) throw new InvalidDataException("Invalid update path.");
        var parts = path.Split('/');
        foreach (var part in parts)
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe update path: " + path);
        var first = parts[0];
        if (first.Equals("app", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
        {
            // app/ is binaries only, not an alternate writable settings/project root.
            if (parts.Length == 2 && (parts[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || new[] { "Batcomputer.deps.json", "Batcomputer.runtimeconfig.json", "createdump.exe" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase))) return;
            if (parts.Length > 2 && parts[1].Equals("runtimes", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidDataException("Unrecognized application binary path: " + path);
        }
        var allowedDirectory = new[] { "Tools", "Web", "gamedata", "licenses", "Documentation", "runtimes" };
        var allowedRoot = new[] { "Batcomputer.exe", "Batcomputer.dll", "Batcomputer.deps.json", "Batcomputer.runtimeconfig.json", "createdump.exe", "README.md", "CHANGELOG.md", "LICENSE", "THIRD_PARTY_NOTICES.md" };
        if (parts.Length == 1 && (allowedRoot.Contains(first, StringComparer.OrdinalIgnoreCase)
            || first.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || (manifestAllowed && first == AppUpdateService.ManifestName))) return;
        if (parts.Length > 1 && allowedDirectory.Contains(first, StringComparer.OrdinalIgnoreCase)) return;
        throw new InvalidDataException("Update may not replace user data or this unrecognized path: " + path);
    }

    internal static string Resolve(string root, string relative)
    {
        ValidateRelative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes root.");
        RejectLinks(path);
        return path;
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Updates do not follow linked folders or files: " + current);
        }
    }
}
