using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Batcomputer;

internal sealed record UpdatePayload(string Path, long Size, string Sha256, string Asset, long DownloadSize, string DownloadSha256);
internal sealed record FileUpdateCatalog(int Schema, string Version, List<UpdatePayload> Files);
internal sealed record FileUpdatePlan(FileUpdateCatalog Catalog, Dictionary<string, Uri> Downloads, long FullSize, int ReusedFiles);

internal sealed partial class AppUpdateService
{
    internal const string FileCatalogName = "Batcomputer-update-win-x64.files.json";
    internal static UpdateManifest ManifestFor(FileUpdateCatalog catalog) => new(1, catalog.Version,
        catalog.Files.Select(f => new UpdateFile(f.Path, f.Size, f.Sha256)).ToList());

    internal static void ValidateCatalog(FileUpdateCatalog catalog)
    {
        if (catalog.Schema != 1 || catalog.Files == null) throw new InvalidDataException("Unsupported file update catalog.");
        ValidateManifest(ManifestFor(catalog));
        long compressed = 0;
        var assets = new Dictionary<string, UpdatePayload>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in catalog.Files)
        {
            if (f.Asset != "bc-file-" + f.Sha256.ToLowerInvariant() + ".gz" || f.DownloadSize <= 0
                || f.DownloadSize > MaxArchiveSize || !Regex.IsMatch(f.DownloadSha256 ?? "", "^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Invalid compressed file payload: " + f.Path);
            if (assets.TryGetValue(f.Asset, out var previous))
            {
                if (previous.Size != f.Size || previous.DownloadSize != f.DownloadSize || !previous.DownloadSha256.Equals(f.DownloadSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Conflicting shared payload.");
            }
            else { assets.Add(f.Asset, f); compressed = checked(compressed + f.DownloadSize); }
        }
        if (compressed > MaxArchiveSize) throw new InvalidDataException("File update exceeds the download limit.");
    }

    private async Task<AppUpdateRelease> ReadFileReleaseAsync(string version, JsonElement release, JsonElement index, CancellationToken ct)
    {
        var size = index.GetProperty("size").GetInt64();
        if (size <= 0 || size > 4 * 1024 * 1024) throw new InvalidDataException("File catalog too large.");
        var digest = ReadDigest(index);
        var url = new Uri(index.GetProperty("browser_download_url").GetString()!);
        using var response = await GetAsync(url, false, ct);
        var bytes = await ReadBoundedAsync(response, (int)size, ct);
        if (bytes.Length != size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File catalog SHA-256 check failed.");
        var catalog = JsonSerializer.Deserialize<FileUpdateCatalog>(bytes, Json) ?? throw new InvalidDataException("Missing file catalog.");
        ValidateCatalog(catalog);
        if (AppVersion.Compare(version, catalog.Version) != 0) throw new InvalidDataException("Release and file catalog versions differ.");
        var downloads = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var file in catalog.Files.DistinctBy(f => f.Asset))
        {
            var matches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == file.Asset).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Missing or duplicate release payload: " + file.Path);
            var asset = matches[0];
            if (asset.GetProperty("size").GetInt64() != file.DownloadSize || !ReadDigest(asset).Equals(file.DownloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release payload differs from catalog: " + file.Path);
            var download = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            ValidateUrl(download, false, false); downloads.Add(file.Asset, download);
        }
        var missing = await Task.Run(() => catalog.Files.Where(f => { ct.ThrowIfCancellationRequested(); return !MatchesLocal(_installRoot, f); }).ToList(), ct);
        var required = missing.DistinctBy(f => f.Asset).Sum(f => f.DownloadSize);
        var full = catalog.Files.DistinctBy(f => f.Asset).Sum(f => f.DownloadSize);
        return new(version, release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", url, required, digest,
            new(catalog, downloads, full, catalog.Files.Count - missing.Count));
    }

    private static string ReadDigest(JsonElement asset)
    {
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("Release asset has no GitHub SHA-256 digest.");
        return digest[7..];
    }

    internal static bool MatchesLocal(string root, UpdatePayload file) => MatchingLocalPath(root, file) != null;

    private static string? MatchingLocalPath(string root, UpdatePayload file)
    {
        var candidates = new List<string> { UpdatePaths.Resolve(root, file.Path) };
        if (file.Path.StartsWith("app/", StringComparison.Ordinal) && File.Exists(Path.Combine(root, "Batcomputer.dll")))
            candidates.Add(UpdatePaths.Resolve(root, file.Path[4..]));
        foreach (var path in candidates)
        {
            try { if (File.Exists(path) && new FileInfo(path).Length == file.Size && Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return path; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private async Task<StagedAppUpdate> DownloadFilesAsync(AppUpdateRelease release, string transaction, IProgress<string>? progress, CancellationToken ct)
    {
        var plan = release.FilePlan!; ValidateCatalog(plan.Catalog);
        if (AppVersion.Compare(plan.Catalog.Version, release.Version) != 0) throw new InvalidDataException("File update version mismatch.");
        var root = AppUpdateInstaller.InstallRoot(transaction);
        var manifest = ManifestFor(plan.Catalog);
        var payload = Path.Combine(transaction, "payload");
        if (Directory.Exists(payload)) throw new IOException("This update is already staged; check again.");
        CheckSpace(transaction, plan.FullSize + manifest.Files.Sum(f => f.Size) * 3);
        Directory.CreateDirectory(payload);
        var missing = new List<UpdatePayload>(); int reused = 0;
        // Copy first, then verify the copy: a file changed since Check is downloaded instead.
        await Task.Run(() =>
        {
            foreach (var file in plan.Catalog.Files)
            {
                ct.ThrowIfCancellationRequested(); var target = UpdatePaths.Resolve(payload, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (MatchingLocalPath(root, file) is { } matching)
                {
                    try
                    {
                        File.Copy(matching, target);
                        VerifyFile(target, new(file.Path, file.Size, file.Sha256)); reused++; continue;
                    }
                    catch (IOException) { if (File.Exists(target)) File.Delete(target); }
                    catch (UnauthorizedAccessException) { if (File.Exists(target)) File.Delete(target); }
                }
                missing.Add(file);
            }
        }, ct);
        long required = missing.DistinctBy(f => f.Asset).Sum(f => f.DownloadSize), downloaded = 0;
        progress?.Report($"Reusing {reused} unchanged files; downloading {missing.Count} changed or missing files ({required / 1048576d:0.00} MB).");
        foreach (var group in missing.GroupBy(f => f.Asset))
        {
            ct.ThrowIfCancellationRequested(); var file = group.First();
            if (!plan.Downloads.TryGetValue(file.Asset, out var download)) throw new InvalidDataException("Missing download URL.");
            var compressed = Path.Combine(transaction, file.Asset);
            using (var response = await GetAsync(download, false, ct))
            {
                if (response.Content.Headers.ContentLength is { } length && length != file.DownloadSize) throw new InvalidDataException("Payload length differs from catalog.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(compressed, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; long count = 0; int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    count += n; if (count > file.DownloadSize) throw new InvalidDataException("Payload exceeded its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0,n), ct);
                    progress?.Report($"Downloading {(downloaded + count) / 1048576d:0.00} / {required / 1048576d:0.00} MB · changed files only");
                }
                if (count != file.DownloadSize) throw new InvalidDataException("Incomplete payload.");
                downloaded += count;
            }
            if (!Hash(compressed).Equals(file.DownloadSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Compressed payload SHA-256 check failed.");
            await Task.Run(() =>
            {
                foreach (var item in group)
                {
                    var target = UpdatePaths.Resolve(payload, item.Path);
                    using var input = File.OpenRead(compressed); using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[81920]; long total = 0; int n;
                        while ((n = gzip.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); total += n; if (total > item.Size) throw new InvalidDataException("Expanded payload exceeds its declared size."); output.Write(buffer,0,n); }
                        if (total != item.Size) throw new InvalidDataException("Expanded payload size mismatch.");
                    }
                    VerifyFile(target, new(item.Path,item.Size,item.Sha256));
                }
            }, ct);
        }
        progress?.Report("Verifying the complete application, including reused files…");
        await Task.Run(() =>
        {
            foreach (var file in manifest.Files) { ct.ThrowIfCancellationRequested(); VerifyFile(UpdatePaths.Resolve(payload,file.Path),file); }
            ValidatePayloadVersion(payload, manifest.Version);
        },ct);
        File.WriteAllText(Path.Combine(transaction, ManifestName),JsonSerializer.Serialize(manifest,Json));
        File.WriteAllText(Path.Combine(transaction,"file-update-summary.json"),JsonSerializer.Serialize(new { reusedFiles=reused, downloadedFiles=missing.Count, downloadedBytes=downloaded, fullPayloadBytes=plan.FullSize },Json));
        File.WriteAllText(Path.Combine(transaction,"status.txt"),$"Download verified. Reused {reused} unchanged files; downloaded {downloaded / 1048576d:0.00} MB instead of {plan.FullSize / 1048576d:0.00} MB of file payloads. Ready to install.");
        return new(transaction,manifest);
    }

    internal static void ValidatePayloadVersion(string payload, string version)
    {
        var exeVersion = ProductVersion(Path.Combine(payload,"Batcomputer.exe"));
        if (exeVersion == null || AppVersion.Compare(exeVersion,version) != 0) throw new InvalidDataException("Executable version differs from manifest.");
        var organized = Path.Combine(payload,"app","Batcomputer.dll");
        var managed = File.Exists(organized) ? organized : Path.Combine(payload,"Batcomputer.dll");
        if (File.Exists(managed))
        {
            var dllVersion = ProductVersion(managed);
            if (dllVersion == null || AppVersion.Compare(dllVersion,version) != 0) throw new InvalidDataException("Managed application version differs from manifest.");
        }
    }
}
