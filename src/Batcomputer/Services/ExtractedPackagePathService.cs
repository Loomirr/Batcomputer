namespace Batcomputer;

/// <summary>
/// Maps Unreal package mounts to their physical roots in a retoc extraction.
/// The base game mounts <c>LEGOBatmanLotDK\Content</c> as <c>/Game</c>, while
/// installed Game Feature DLC mounts each plugin's Content folder at its own
/// root (for example <c>/DLC_BeyondPack</c>).
/// </summary>
internal static class ExtractedPackagePathService
{
    internal sealed record Mount(string PackageRoot, string ContentRoot);

    public static IReadOnlyList<Mount> EnumerateMounts(string baseContentRoot)
    {
        if (string.IsNullOrWhiteSpace(baseContentRoot))
        {
            return Array.Empty<Mount>();
        }

        string contentRoot;
        try
        {
            contentRoot = Path.GetFullPath(baseContentRoot.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return Array.Empty<Mount>();
        }

        if (!Directory.Exists(contentRoot))
        {
            return Array.Empty<Mount>();
        }

        var mounts = new List<Mount> { new("/Game", contentRoot) };
        var gameRoot = Directory.GetParent(contentRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return mounts;
        }

        var gameFeaturesRoot = Path.Combine(gameRoot, "Plugins", "GameFeatures");
        if (!Directory.Exists(gameFeaturesRoot))
        {
            return mounts;
        }

        foreach (var pluginDirectory in Directory.EnumerateDirectories(
                     gameFeaturesRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var pluginName = Path.GetFileName(pluginDirectory);
            if (string.IsNullOrWhiteSpace(pluginName) ||
                pluginName is "." or ".." ||
                pluginName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                continue;
            }

            var pluginContentRoot = Path.Combine(pluginDirectory, "Content");
            if (Directory.Exists(pluginContentRoot))
            {
                mounts.Add(new Mount("/" + pluginName, Path.GetFullPath(pluginContentRoot)));
            }
        }

        return mounts
            .OrderBy(mount => mount.PackageRoot.Equals("/Game", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(mount => mount.PackageRoot, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? PackagePathFromFile(string baseContentRoot, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        foreach (var mount in EnumerateMounts(baseContentRoot))
        {
            if (!IsWithinOrEqual(fullPath, mount.ContentRoot))
            {
                continue;
            }

            var relative = Path.GetRelativePath(mount.ContentRoot, fullPath)
                .Replace('\\', '/');
            relative = StripKnownAssetExtension(relative).TrimStart('/');
            if (string.IsNullOrWhiteSpace(relative) || ContainsTraversal(relative))
            {
                return null;
            }

            return mount.PackageRoot + "/" + relative;
        }

        return null;
    }

    public static string? ContentRelativeFromFile(string baseContentRoot, string filePath)
    {
        var package = PackagePathFromFile(baseContentRoot, filePath);
        if (string.IsNullOrWhiteSpace(package))
        {
            return null;
        }

        var slash = package.IndexOf('/', 1);
        return slash < 0 ? null : package[(slash + 1)..];
    }

    public static string? ResolvePackageBase(string baseContentRoot, string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!IsContentPackagePath(package))
        {
            return null;
        }

        foreach (var mount in EnumerateMounts(baseContentRoot))
        {
            if (!package.Equals(mount.PackageRoot, StringComparison.OrdinalIgnoreCase) &&
                !package.StartsWith(mount.PackageRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = package.Length == mount.PackageRoot.Length
                ? ""
                : package[(mount.PackageRoot.Length + 1)..];
            if (ContainsTraversal(relative))
            {
                return null;
            }

            var candidate = Path.GetFullPath(Path.Combine(
                mount.ContentRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            return IsWithinOrEqual(candidate, mount.ContentRoot) ? candidate : null;
        }

        return null;
    }

    public static string? ResolvePackageUasset(string baseContentRoot, string packagePath)
    {
        var packageBase = ResolvePackageBase(baseContentRoot, packagePath);
        return string.IsNullOrWhiteSpace(packageBase) ? null : packageBase + ".uasset";
    }

    public static bool IsContentPackagePath(string? packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        return package.Length > 1 &&
               package.StartsWith('/') &&
               !package.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) &&
               !package.Contains('\\') &&
               !ContainsTraversal(package);
    }

    private static bool IsWithinOrEqual(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsTraversal(string path) =>
        path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");

    private static string StripKnownAssetExtension(string path)
    {
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^extension.Length];
            }
        }

        return path;
    }
}
