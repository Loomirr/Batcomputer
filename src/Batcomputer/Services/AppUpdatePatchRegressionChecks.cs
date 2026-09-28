using System.IO.Compression;
using System.Text.Json;

namespace Batcomputer;

internal static class AppUpdatePatchRegressionChecks
{
    internal static void Run(string root, Action<string, Action> check, Action<Action> reject)
    {
        var publish = Path.Combine(root, "patch-publish"); Directory.CreateDirectory(publish);
        File.Copy(Environment.ProcessPath!, Path.Combine(publish, "Batcomputer.exe"));
        var random = new byte[512 * 1024]; new Random(1718).NextBytes(random);
        File.WriteAllBytes(Path.Combine(publish, "unchanged.dll"), random);
        File.WriteAllText(Path.Combine(publish, "changed.dll"), "new content");
        var full = AppUpdatePackageService.Create(publish, Path.Combine(root, "patch-package"));
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(root, "patch-package", AppUpdateService.ManifestName)), AppUpdateService.Json)!;
        var patch = Path.Combine(root, "patch.zip");
        using (var zip = ZipFile.Open(patch, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(AppUpdateService.ManifestName).Open())) writer.Write(JsonSerializer.Serialize(manifest, AppUpdateService.Json));
            zip.CreateEntryFromFile(Path.Combine(publish, "changed.dll"), "changed.dll");
        }
        var catalog = new PatchUpdateCatalog(1, "1.0.0-0", manifest, ["changed.dll"],
            "Batcomputer-update-from-1.0.0-win-x64.zip", new FileInfo(patch).Length, AppUpdateService.Hash(patch));
        string Install()
        {
            var install = Path.Combine(root, "patch-install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(install);
            File.Copy(Path.Combine(publish, "Batcomputer.exe"), Path.Combine(install, "Batcomputer.exe"));
            File.Copy(Path.Combine(publish, "unchanged.dll"), Path.Combine(install, "unchanged.dll"));
            File.WriteAllText(Path.Combine(install, "changed.dll"), "old content"); return install;
        }
        check("patch ZIP staging verifies changed files and the complete reconstructed application", () =>
        {
            var payload = Install(); File.Delete(Path.Combine(payload, "changed.dll"));
            AppUpdateService.StagePatch(patch, payload, catalog, CancellationToken.None);
            foreach (var file in manifest.Files) AppUpdateService.VerifyFile(UpdatePaths.Resolve(payload, file.Path), file);
        });
        check("patch selection requires the exact base version and hashes of every reused file", () =>
        {
            var install = Install();
            var future = catalog with { BaseVersion = AppVersion.Current, Manifest = manifest with { Version = "99.0.0" } };
            AppUpdateService.ValidatePatchCatalog(future);
            if (!AppUpdateService.PatchMatchesInstallation(install, future) || AppUpdateService.PatchMatchesInstallation(install, future with { BaseVersion = "98.0.0" })) throw new Exception("Base-version check failed.");
            File.WriteAllText(Path.Combine(install, "unchanged.dll"), "modified");
            if (AppUpdateService.PatchMatchesInstallation(install, future)) throw new Exception("Modified dependency accepted.");
            File.Delete(Path.Combine(install, "unchanged.dll"));
            if (AppUpdateService.PatchMatchesInstallation(install, future)) throw new Exception("Missing dependency accepted.");
        });
        check("patch catalogs reject unknown/duplicate paths, unsafe ZIP names and non-forward versions", () =>
        {
            foreach (var bad in new[] { catalog with { ChangedPaths = ["../oops.dll"] }, catalog with { ChangedPaths = ["changed.dll", "CHANGED.dll"] },
                catalog with { Asset = "../patch.zip" }, catalog with { BaseVersion = catalog.Manifest.Version }, catalog with { DownloadSha256 = "wrong" } })
                reject(() => AppUpdateService.ValidatePatchCatalog(bad));
            var wrong = catalog with { Manifest = manifest with { Version = "98.0.0" } };
            var payload = Install(); File.Delete(Path.Combine(payload, "changed.dll"));
            reject(() => AppUpdateService.StagePatch(patch, payload, wrong, CancellationToken.None));
        });
        using var server = new AppUpdateRegressionChecks.LocalServer();
        server.Routes["/full.zip"] = File.ReadAllBytes(full);
        server.Routes["/patch.zip"] = File.ReadAllBytes(patch);
        var installRoot = Install();
        var selection = catalog with { BaseVersion = AppVersion.Current, Manifest = manifest with { Version = "99.0.0" } };
        void Feed(bool badDigest = false, bool missingPatch = false)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(selection, AppUpdateService.Json);
            server.Routes["/" + AppUpdateService.PatchCatalogName] = bytes;
            object Asset(string name, string route, byte[] data, bool corrupt = false) => new { name, size = data.Length,
                digest = "sha256:" + (corrupt ? new string('0', 64) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))), browser_download_url = server.Url + route };
            var assets = new List<object> { Asset(AppUpdateService.AssetName, "full.zip", File.ReadAllBytes(full)),
                Asset(AppUpdateService.PatchCatalogName, AppUpdateService.PatchCatalogName, bytes, badDigest) };
            if (!missingPatch) assets.Add(Asset(selection.Asset, "patch.zip", File.ReadAllBytes(patch)));
            server.Routes["/releases.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "99.0.0", draft = false, prerelease = false, assets } });
        }
        check("authenticated patch catalog selects the small ZIP; incompatible or full-only clients select the full ZIP", () =>
        {
            Feed(); using var client = new AppUpdateService(new Uri(server.Url + "releases.json"), installRoot);
            var selected = client.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult();
            if (selected?.PatchPlan is null || selected.Size != catalog.DownloadSize) throw new Exception("Patch was not selected.");
            using var fullOnly = new AppUpdateService(new Uri(server.Url + "releases.json"), installRoot, preferFileUpdates: false);
            if (fullOnly.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult()?.PatchPlan is not null) throw new Exception("Full-only request selected a patch.");
            File.Delete(Path.Combine(installRoot, "unchanged.dll"));
            if (client.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult()?.PatchPlan is not null) throw new Exception("Unsafe patch selected.");
        });
        check("patch catalogs reject corrupted metadata and missing ZIP assets before installation", () =>
        {
            using var client = new AppUpdateService(new Uri(server.Url + "releases.json"), Install());
            Feed(badDigest: true); reject(() => client.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult());
            Feed(missingPatch: true); reject(() => client.CheckAsync(true, CancellationToken.None).GetAwaiter().GetResult());
        });
        check("a patch no longer compatible at Download safely falls back to the full ZIP", () =>
        {
            var install = Install();
            using var client = new AppUpdateService(new Uri(server.Url + "releases.json"), install);
            var release = new AppUpdateRelease(manifest.Version, "", new Uri(server.Url + "full.zip"), catalog.DownloadSize,
                AppUpdateService.Hash(full), PatchPlan: new(catalog, new Uri(server.Url + "patch.zip"), new FileInfo(full).Length));
            var stage = client.DownloadAsync(release, AppUpdateInstaller.NewTransaction(install), null, CancellationToken.None).GetAwaiter().GetResult();
            if (!server.Requests.Contains("/full.zip")) throw new Exception("No full fallback download.");
            foreach (var file in manifest.Files) AppUpdateService.VerifyFile(UpdatePaths.Resolve(Path.Combine(stage.Directory, "payload"), file.Path), file);
            if (File.ReadAllText(Path.Combine(install, "changed.dll")) != "old content") throw new Exception("Staging changed the installation.");
        });
    }
}
