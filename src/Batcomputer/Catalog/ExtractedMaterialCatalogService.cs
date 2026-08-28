namespace Batcomputer;

/// <summary>
/// Discovers cooked material instances from the active extracted Content tree and merges them with
/// the lightweight catalog shipped beside Batcomputer. The shipped catalog remains useful before
/// extraction, but it must never hide material instances present in the user's current game dump.
/// </summary>
public static class ExtractedMaterialCatalogService
{
    private static readonly object CacheGate = new();
    private static string _cachedContentRoot = "";
    private static IReadOnlyList<GameDataAsset> _cachedExtracted = Array.Empty<GameDataAsset>();

    /// <summary>Every material instance visible in either the active extraction or shipped catalog.</summary>
    public static IReadOnlyList<GameDataAsset> MergeWithActiveExtraction(
        IEnumerable<GameDataAsset> shippedMaterials)
    {
        string contentRoot;
        try
        {
            contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        }
        catch
        {
            contentRoot = "";
        }

        return Merge(shippedMaterials, ExtractedForRoot(contentRoot));
    }

    /// <summary>Forces the next query to rescan the active extracted Content tree.</summary>
    public static void Invalidate()
    {
        lock (CacheGate)
        {
            _cachedContentRoot = "";
            _cachedExtracted = Array.Empty<GameDataAsset>();
        }
    }

    internal static IReadOnlyList<GameDataAsset> ExtractedForRoot(string? contentRoot)
    {
        var normalizedRoot = NormalizeRoot(contentRoot);
        lock (CacheGate)
        {
            if (normalizedRoot.Equals(_cachedContentRoot, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedExtracted;
            }

            if (TryDiscover(normalizedRoot, out var discovered))
            {
                _cachedExtracted = discovered;
                _cachedContentRoot = normalizedRoot;
                return _cachedExtracted;
            }

            // Do not cache a transient OneDrive/access/enumeration failure. A later query can retry
            // the same active root without requiring the user to restart or change Settings.
            return Array.Empty<GameDataAsset>();
        }
    }

    internal static IReadOnlyList<GameDataAsset> MergeForRegression(
        IEnumerable<GameDataAsset> shippedMaterials,
        IEnumerable<GameDataAsset> extractedMaterials) =>
        Merge(shippedMaterials, extractedMaterials);

    private static bool TryDiscover(
        string contentRoot,
        out IReadOnlyList<GameDataAsset> discovered)
    {
        if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
        {
            discovered = Array.Empty<GameDataAsset>();
            return true;
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            // Every MaterialInstanceConstant in the current game catalog uses the MI_ package
            // prefix. Restricting the live scan to that convention avoids parsing thousands of
            // unrelated cooked packages on the UI thread while still covering every extracted MI.
            discovered = ExtractedPackagePathService
                .EnumerateMounts(contentRoot)
                .SelectMany(mount => Directory.EnumerateFiles(mount.ContentRoot, "MI_*.uasset", options))
                .Where(path => Path.GetFileNameWithoutExtension(path)
                    .StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
                .Select(path => new GameDataAsset
                {
                    Path = ExtractedPackagePathService.PackagePathFromFile(contentRoot, path) ?? "",
                    Class = "MaterialInstanceConstant",
                })
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Path))
                .DistinctBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }
        catch
        {
            // The bundled catalog remains a usable fallback if a removable or synchronized
            // extraction becomes unavailable during enumeration.
            discovered = Array.Empty<GameDataAsset>();
            return false;
        }
    }

    private static IReadOnlyList<GameDataAsset> Merge(
        IEnumerable<GameDataAsset> shippedMaterials,
        IEnumerable<GameDataAsset> extractedMaterials)
    {
        // Put extracted entries first so a duplicate path represents an asset proven to exist in
        // the active dump. GameDataAsset currently carries only path/class, but this ordering keeps
        // that rule intact if source metadata is added later.
        return extractedMaterials
            .Concat(shippedMaterials)
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Path))
            .DistinctBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeRoot(string? contentRoot)
    {
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            return "";
        }

        try
        {
            return AppSettings.NormalizeContentRoot(contentRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return "";
        }
    }
}
