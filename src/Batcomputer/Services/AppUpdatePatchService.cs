using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Batcomputer;

internal sealed record PatchUpdateCatalog(int Schema, string BaseVersion, UpdateManifest Manifest,
    List<string> ChangedPaths, string Asset, long DownloadSize, string DownloadSha256);
internal sealed record PatchUpdatePlan(PatchUpdateCatalog Catalog, Uri Download, long FullZipSize);

internal sealed partial class AppUpdateService
{
    internal const string PatchCatalogName = "Batcomputer-update-win-x64.patches.json";

    internal static void ValidatePatchCatalog(PatchUpdateCatalog catalog)
    {
        if (catalog.Schema != 1 || catalog.Manifest is null || catalog.ChangedPaths is null)
            throw new InvalidDataException("Unsupported patch catalog.");
        ValidateManifest(catalog.Manifest);
        if (!IsSupportedRelease(catalog.BaseVersion) || AppVersion.Compare(catalog.Manifest.Version, catalog.BaseVersion) <= 0 ||
            !Regex.IsMatch(catalog.Asset ?? "", @"^Batcomputer-update-from-[0-9A-Za-z.-]+-win-x64\.zip$") ||
            catalog.DownloadSize <= 0 || catalog.DownloadSize > MaxArchiveSize ||
            !Regex.IsMatch(catalog.DownloadSha256 ?? "", "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("Invalid patch metadata.");
        var paths = catalog.Manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (catalog.ChangedPaths.Count == 0 || catalog.ChangedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.ChangedPaths.Count ||
            catalog.ChangedPaths.Any(p => !paths.Contains(p))) throw new InvalidDataException("Invalid patch file list.");
        foreach (var path in catalog.ChangedPaths) UpdatePaths.ValidateRelative(path);
    }

    internal static bool PatchMatchesInstallation(string root, PatchUpdateCatalog catalog)
    {
        var version = ProductVersion(Path.Combine(root, "Batcomputer.exe"));
        if (version is null || AppVersion.Compare(version, catalog.BaseVersion) != 0) return false;
        var changed = catalog.ChangedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog.Manifest.Files.Where(f => !changed.Contains(f.Path)).All(f => MatchingPatchFile(root, f) is not null);
    }

    private static string? MatchingPatchFile(string root, UpdateFile file) => MatchingLocalPath(root,
        new(file.Path, file.Size, file.Sha256, "", 0, ""));

    private async Task<AppUpdateRelease> ReadPatchReleaseAsync(AppUpdateRelease full, JsonElement release, CancellationToken ct)
    {
        var indexes = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == PatchCatalogName).ToArray();
        if (indexes.Length == 0) return full;
        if (indexes.Length != 1) throw new InvalidDataException("Ambiguous patch catalog.");
        var index = indexes[0]; var size = index.GetProperty("size").GetInt64();
        if (size <= 0 || size > 4 * 1024 * 1024) throw new InvalidDataException("Patch catalog too large.");
        var digest = ReadDigest(index);
        using var response = await GetAsync(new Uri(index.GetProperty("browser_download_url").GetString()!), false, ct);
        var bytes = await ReadBoundedAsync(response, (int)size, ct);
        if (bytes.Length != size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Patch catalog SHA-256 check failed.");
        var catalog = JsonSerializer.Deserialize<PatchUpdateCatalog>(bytes, Json) ?? throw new InvalidDataException("Empty patch catalog.");
        ValidatePatchCatalog(catalog);
        if (AppVersion.Compare(catalog.Manifest.Version, full.Version) != 0) throw new InvalidDataException("Patch version differs from release.");
        var patches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == catalog.Asset).ToArray();
        if (patches.Length != 1 || patches[0].GetProperty("size").GetInt64() != catalog.DownloadSize ||
            !ReadDigest(patches[0]).Equals(catalog.DownloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Patch ZIP is missing or differs from its catalog.");
        var url = new Uri(patches[0].GetProperty("browser_download_url").GetString()!); ValidateUrl(url, false, false);
        if (catalog.DownloadSize >= full.Size || !await Task.Run(() => PatchMatchesInstallation(_installRoot, catalog), ct)) return full;
        return full with { Size = catalog.DownloadSize, PatchPlan = new(catalog, url, full.Size) };
    }

    private async Task<StagedAppUpdate> DownloadPatchAsync(AppUpdateRelease release, string transaction, IProgress<string>? progress, CancellationToken ct)
    {
        var plan = release.PatchPlan!; var catalog = plan.Catalog; ValidatePatchCatalog(catalog);
        if (AppVersion.Compare(catalog.Manifest.Version, release.Version) != 0) throw new InvalidDataException("Patch version mismatch.");
        var install = AppUpdateInstaller.InstallRoot(transaction);
        Directory.CreateDirectory(transaction); UpdatePaths.RejectLinks(transaction);
        CheckSpace(transaction, plan.FullZipSize + catalog.Manifest.Files.Sum(f => f.Size) * 3);
        var payload = Path.Combine(transaction, "payload");
        if (Directory.Exists(payload)) throw new IOException("This update is already staged; check again.");
        var changed = catalog.ChangedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Recheck and verify the copies, not just the files examined by Check. Nothing
        // in the actual installation is written while selecting a fallback.
        var copied = await Task.Run(() =>
        {
            if (!PatchMatchesInstallation(install, catalog)) return false;
            Directory.CreateDirectory(payload);
            foreach (var file in catalog.Manifest.Files.Where(f => !changed.Contains(f.Path)))
            {
                ct.ThrowIfCancellationRequested();
                if (MatchingPatchFile(install, file) is not { } source) return false;
                var target = UpdatePaths.Resolve(payload, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(source, target); VerifyFile(target, file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
            }
            return true;
        }, ct);
        if (!copied)
        {
            if (Directory.Exists(payload))
            {
                UpdatePaths.RejectLinks(payload);
                if (!FileSystemPathUtil.IsWithinDirectory(payload, transaction)) throw new InvalidDataException("Unsafe patch staging path.");
                Directory.Delete(payload, true); // Only this new transaction's temporary application tree.
            }
            progress?.Report("Installed files changed or do not match this patch. Downloading the complete ZIP instead…");
            return await DownloadAsync(release with { PatchPlan = null, Size = plan.FullZipSize }, transaction, progress, ct);
        }
        progress?.Report($"Downloading patch for {catalog.BaseVersion}: {catalog.DownloadSize / 1048576d:0.00} MB · {changed.Count} changed files.");
        var zipPath = Path.Combine(transaction, "patch.zip");
        using (var response = await GetAsync(plan.Download, false, ct))
        {
            if (response.Content.Headers.ContentLength is { } length && length != catalog.DownloadSize) throw new InvalidDataException("Patch ZIP length mismatch.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; long total = 0; int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += n; if (total > catalog.DownloadSize) throw new InvalidDataException("Patch ZIP exceeded its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                progress?.Report($"Downloading patch {total / 1048576d:0.00} / {catalog.DownloadSize / 1048576d:0.00} MB");
            }
            if (total != catalog.DownloadSize) throw new InvalidDataException("Incomplete patch ZIP.");
        }
        if (!Hash(zipPath).Equals(catalog.DownloadSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Patch ZIP SHA-256 check failed.");
        await Task.Run(() => StagePatch(zipPath, payload, catalog, ct), ct);
        File.WriteAllText(Path.Combine(transaction, ManifestName), JsonSerializer.Serialize(catalog.Manifest, Json));
        File.WriteAllText(Path.Combine(transaction, "patch-update-summary.json"), JsonSerializer.Serialize(new {
            baseVersion = catalog.BaseVersion, reusedFiles = catalog.Manifest.Files.Count - changed.Count,
            downloadedFiles = changed.Count, downloadedBytes = catalog.DownloadSize, fullZipBytes = plan.FullZipSize }, Json));
        File.WriteAllText(Path.Combine(transaction, "status.txt"), "Patch and complete staged application verified. Ready to install.");
        return new(transaction, catalog.Manifest);
    }

    internal static void StagePatch(string zipPath, string payload, PatchUpdateCatalog catalog, CancellationToken ct)
    {
        ValidatePatchCatalog(catalog);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count != catalog.ChangedPaths.Count + 1) throw new InvalidDataException("Patch ZIP has missing or unlisted files.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested(); UpdatePaths.ValidateRelative(entry.FullName, manifestAllowed: true);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0 || !entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("Duplicate paths or links in patch ZIP.");
            expanded = checked(expanded + entry.Length); if (expanded > MaxExpandedSize) throw new InvalidDataException("Expanded patch too large.");
        }
        if (!entries.TryGetValue(ManifestName, out var index) || index.Length > 4 * 1024 * 1024) throw new InvalidDataException("Missing patch manifest.");
        using (var stream = index.Open())
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(stream, Json) ?? throw new InvalidDataException("Empty patch manifest.");
            ValidateManifest(manifest);
            if (manifest.Version != catalog.Manifest.Version || !manifest.Files.SequenceEqual(catalog.Manifest.Files)) throw new InvalidDataException("Patch manifest differs from authenticated catalog.");
        }
        foreach (var path in catalog.ChangedPaths)
        {
            ct.ThrowIfCancellationRequested(); var file = catalog.Manifest.Files.Single(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!entries.TryGetValue(path, out var entry) || entry.Length != file.Size) throw new InvalidDataException("Missing patch file: " + path);
            var target = UpdatePaths.Resolve(payload, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target); VerifyFile(target, file);
        }
        foreach (var file in catalog.Manifest.Files) { ct.ThrowIfCancellationRequested(); VerifyFile(UpdatePaths.Resolve(payload, file.Path), file); }
        ValidatePayloadVersion(payload, catalog.Manifest.Version);
    }
}
