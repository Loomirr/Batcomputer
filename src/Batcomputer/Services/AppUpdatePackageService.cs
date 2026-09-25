using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Batcomputer;

internal static class AppUpdatePackageService
{
    internal static string CreateWithFilePayloads(string publishDirectory, string outputDirectory)
    {
        var archive = Create(publishDirectory, outputDirectory);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(outputDirectory, AppUpdateService.ManifestName)), AppUpdateService.Json)!;
        var payloads = new List<UpdatePayload>();
        foreach (var file in manifest.Files)
        {
            var asset = "bc-file-" + file.Sha256.ToLowerInvariant() + ".gz";
            var output = Path.Combine(outputDirectory, asset);
            if (!File.Exists(output))
            {
                using var input = File.OpenRead(UpdatePaths.Resolve(publishDirectory, file.Path));
                using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                using var gzip = new GZipStream(target, CompressionLevel.SmallestSize);
                input.CopyTo(gzip);
            }
            payloads.Add(new(file.Path, file.Size, file.Sha256, asset, new FileInfo(output).Length, AppUpdateService.Hash(output)));
        }
        var catalog = new FileUpdateCatalog(1, manifest.Version, payloads);
        AppUpdateService.ValidateCatalog(catalog);
        var index = Path.Combine(outputDirectory, AppUpdateService.FileCatalogName);
        File.WriteAllText(index, JsonSerializer.Serialize(catalog, AppUpdateService.Json));
        File.WriteAllText(index + ".sha256", AppUpdateService.Hash(index).ToLowerInvariant() + "  " + AppUpdateService.FileCatalogName + Environment.NewLine);
        return archive;
    }

    // Run against a clean publish directory, never a user's portable installation.
    internal static string Create(string publishDirectory, string outputDirectory)
    {
        var root = Path.GetFullPath(publishDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || output.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Package output must be outside the publish directory.");
        UpdatePaths.RejectLinks(root);
        var version = AppUpdateService.ProductVersion(Path.Combine(root, "Batcomputer.exe"))
            ?? throw new InvalidDataException("Executable has no product version.");
        var files = new List<UpdateFile>();
        Collect(root, root, files);
        var manifest = new UpdateManifest(1, version, files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
        AppUpdateService.ValidateManifest(manifest);
        Directory.CreateDirectory(output);
        var archive = Path.Combine(output, AppUpdateService.AssetName);
        using (var zip = new ZipArchive(new FileStream(archive, FileMode.CreateNew), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(AppUpdateService.ManifestName);
            using (var writer = new StreamWriter(entry.Open())) writer.Write(JsonSerializer.Serialize(manifest, AppUpdateService.Json));
            foreach (var file in manifest.Files)
                zip.CreateEntryFromFile(UpdatePaths.Resolve(root, file.Path), file.Path, CompressionLevel.Optimal);
        }
        File.WriteAllText(archive + ".sha256", AppUpdateService.Hash(archive).ToLowerInvariant() + "  " + Path.GetFileName(archive) + Environment.NewLine);
        File.WriteAllText(Path.Combine(output, AppUpdateService.ManifestName), JsonSerializer.Serialize(manifest, AppUpdateService.Json));
        return archive;
    }

    private static void Collect(string root, string directory, List<UpdateFile> files)
    {
        UpdatePaths.RejectLinks(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            // Our separately authored module is intentionally retained by the release owner.
            // Do not mistake this exception for legal clearance, or allow Epic engine DLLs.
            var ownWriter = relative.Equals("Tools/BatcomputerRegistryWriter/Prebuilt/Win64/UnrealEditor-BatcomputerRegistryWriter.dll", StringComparison.OrdinalIgnoreCase);
            if (!ownWriter && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^(oo2core.*\.dll|oodle-data-shared\.dll|liboodle.*\.(so|dylib)|LOTDKExpanded\.dll|UnrealEditor.*\.(dll|exe))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException("Release packaging blocked pending redistribution review (game/Oodle runtime or Unreal editor binary): " + relative);
            if (relative == AppUpdateInstaller.LockFile) continue;
            // Optional .NET crash-dump CLI, not needed to run the app. Keep the full ZIP
            // compatible with the original updater's app-file allowlist.
            if (relative == "createdump.exe") continue;
            // Publish creates this empty workspace placeholder; it is not an application file.
            if (relative == "Generated/.gitkeep" && new FileInfo(file).Length == 0) continue;
            UpdatePaths.ValidateRelative(relative); UpdatePaths.RejectLinks(file);
            files.Add(new(relative, new FileInfo(file).Length, AppUpdateService.Hash(file)));
        }
        foreach (var child in Directory.EnumerateDirectories(directory)) Collect(root, child, files);
    }
}
