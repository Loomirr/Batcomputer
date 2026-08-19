using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Inventories exactly what a suit's staged Content tree will ship, and flags the failure class the
/// project keeps hitting: two packages claiming the SAME /Game/... object path. When that happens,
/// IoStore mount priority silently decides which asset wins, so the game can load an old/other
/// suit's asset while everything "looks right" in the tool and FModel.
///
/// Collisions are detected from two sources, both cheap and offline:
///   * WITHIN this staging tree - two files resolving to one package path (case/dupe staging).
///   * ACROSS other suits - their last build-manifest.json lists the package paths they shipped.
/// </summary>
public sealed class PackageContentPreviewService
{
    public sealed record StagedAsset(string PackagePath, string RelativeFile, long SizeBytes, bool HasUexp, bool HasUbulk);

    public sealed record Collision(string PackagePath, string Detail, string Severity); // "ERROR" | "WARN"

    public sealed class Preview
    {
        public string ContentRoot { get; init; } = "";
        public List<StagedAsset> Assets { get; } = new();
        public List<Collision> Collisions { get; } = new();
        public long TotalBytes => Assets.Sum(a => a.SizeBytes);
        public bool HasErrors => Collisions.Any(c => c.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
    }

    private readonly string _projectRoot;

    public PackageContentPreviewService(string projectRoot) => _projectRoot = projectRoot;

    /// <summary>
    /// Builds the preview for <paramref name="contentRoot"/>. <paramref name="slotId"/> is this
    /// suit's own slot - its own previous manifest is skipped so a re-package isn't a "collision".
    /// </summary>
    public Preview Build(string contentRoot, string slotId)
    {
        var preview = new Preview { ContentRoot = contentRoot };
        if (!Directory.Exists(contentRoot))
        {
            preview.Collisions.Add(new Collision("", $"Staged content root does not exist: {contentRoot}", "ERROR"));
            return preview;
        }

        // 1. Inventory every staged package.
        var byPackage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var uasset in Directory.EnumerateFiles(contentRoot, "*.uasset", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var pkg = FileToPackagePath(contentRoot, uasset);
            var noExt = Path.ChangeExtension(uasset, null);
            preview.Assets.Add(new StagedAsset(
                pkg,
                Path.GetRelativePath(contentRoot, uasset).Replace('\\', '/'),
                SafeLen(uasset) + SafeLen(noExt + ".uexp") + SafeLen(noExt + ".ubulk"),
                File.Exists(noExt + ".uexp"),
                File.Exists(noExt + ".ubulk")));

            if (!byPackage.TryGetValue(pkg, out var list))
            {
                byPackage[pkg] = list = new List<string>();
            }
            list.Add(Path.GetRelativePath(contentRoot, uasset).Replace('\\', '/'));
        }

        // 2. Duplicates within this staging tree.
        foreach (var (pkg, files) in byPackage.Where(kv => kv.Value.Count > 1))
        {
            preview.Collisions.Add(new Collision(pkg,
                $"{files.Count} staged files resolve to the same package path: {string.Join(", ", files)}", "ERROR"));
        }

        // 3. Collisions against other suits' last shipped manifests.
        foreach (var (otherSlot, otherPkgs) in OtherSuitShippedPackages(slotId))
        {
            foreach (var pkg in byPackage.Keys.Where(otherPkgs.Contains))
            {
                preview.Collisions.Add(new Collision(pkg,
                    $"also included by suit '{otherSlot}' — whichever pak mounts last wins in-game. Give each suit its own /Game/Mods/<mod>/ namespace.",
                    "ERROR"));
            }
        }

        return preview;
    }

    /// <summary>Package paths each OTHER suit shipped, read from their last build-manifest.json.</summary>
    private IEnumerable<(string SlotId, HashSet<string> Packages)> OtherSuitShippedPackages(string slotId)
    {
        var root = Path.Combine(AppSettings.GeneratedRootFor(_projectRoot), "NativeSuitGuiProjects");
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var other = Path.GetFileName(dir);
            if (other.Equals(slotId, StringComparison.OrdinalIgnoreCase))
            {
                continue; // our own previous build
            }

            var manifest = Path.Combine(dir, "IoStore", "build-manifest.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            HashSet<string>? packages = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("ShippedPackages", out var shipped) &&
                    shipped.ValueKind == JsonValueKind.Array)
                {
                    packages = shipped.EnumerateArray()
                        .Select(e => e.TryGetProperty("Package", out var p) ? p.GetString() ?? "" : "")
                        .Where(s => s.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { /* unreadable manifest — skip rather than block packaging */ }

            if (packages is { Count: > 0 })
            {
                yield return (other, packages);
            }
        }
    }

    private static long SafeLen(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static string FileToPackagePath(string contentRoot, string uassetPath)
    {
        var rel = Path.GetRelativePath(contentRoot, Path.ChangeExtension(uassetPath, null)).Replace('\\', '/');
        return "/Game/" + rel;
    }
}
