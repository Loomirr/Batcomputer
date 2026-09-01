namespace Batcomputer;

/// <summary>
/// Adds animation assets proven to exist in the user's active base-game/DLC extraction to the
/// shipped fallback catalog. The game's cooked naming contract is exact for these asset classes:
/// A_* is AnimSequence, AM_* is AnimMontage, and ABP_* is AnimBlueprintGeneratedClass.
/// </summary>
public static class ExtractedAnimationCatalogService
{
    private static readonly object CacheGate = new();
    private static string _cachedContentRoot = "";
    private static IReadOnlyList<GameDataAsset> _cachedExtracted = Array.Empty<GameDataAsset>();

    public static IReadOnlyList<GameDataAsset> MergeWithActiveExtraction(
        IEnumerable<GameDataAsset> shippedAssets,
        string className)
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

        return Merge(shippedAssets, ExtractedForRoot(contentRoot), className);
    }

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
                _cachedContentRoot = normalizedRoot;
                _cachedExtracted = discovered;
                return _cachedExtracted;
            }

            // A synchronized/removable extraction may become readable again without a root change.
            return Array.Empty<GameDataAsset>();
        }
    }

    internal static IReadOnlyList<GameDataAsset> MergeForRegression(
        IEnumerable<GameDataAsset> shippedAssets,
        IEnumerable<GameDataAsset> extractedAssets,
        string className) =>
        Merge(shippedAssets, extractedAssets, className);

    private static bool TryDiscover(string contentRoot, out IReadOnlyList<GameDataAsset> discovered)
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
            var patterns = new (string Pattern, string ClassName)[]
            {
                ("A_*.uasset", "AnimSequence"),
                ("AM_*.uasset", "AnimMontage"),
                ("ABP_*.uasset", "AnimBlueprintGeneratedClass"),
            };

            discovered = ExtractedPackagePathService
                .EnumerateMounts(contentRoot)
                .SelectMany(mount => patterns.SelectMany(pattern =>
                    Directory.EnumerateFiles(mount.ContentRoot, pattern.Pattern, options)
                        .Select(path => new { Path = path, pattern.ClassName })))
                .Select(item => new GameDataAsset
                {
                    Path = ExtractedPackagePathService.PackagePathFromFile(contentRoot, item.Path) ?? "",
                    Class = item.ClassName,
                })
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Path))
                .DistinctBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }
        catch
        {
            discovered = Array.Empty<GameDataAsset>();
            return false;
        }
    }

    private static IReadOnlyList<GameDataAsset> Merge(
        IEnumerable<GameDataAsset> shippedAssets,
        IEnumerable<GameDataAsset> extractedAssets,
        string className) =>
        extractedAssets
            .Where(asset => asset.Class.Equals(className, StringComparison.OrdinalIgnoreCase))
            .Concat(shippedAssets)
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Path))
            .DistinctBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
