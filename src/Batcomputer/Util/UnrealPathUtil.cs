namespace Batcomputer;

using UAssetAPI;
using UAssetAPI.UnrealTypes;

internal static class UnrealPathUtil
{
    /// <summary>
    /// Produces a conservative Unreal/FName-safe identifier. Display names may contain
    /// punctuation, but generated package, object, and primary-asset names may not.
    /// </summary>
    public static string SanitizeIdentifier(string? value, string fallback = "Custom")
    {
        var source = value?.Trim() ?? "";
        var result = new System.Text.StringBuilder(source.Length);
        var pendingSeparator = false;
        foreach (var character in source)
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '_')
            {
                if (pendingSeparator && result.Length > 0 && result[^1] != '_')
                {
                    result.Append('_');
                }
                result.Append(character);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = result.Length > 0;
            }
        }

        var clean = result.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = fallback;
        }
        if (char.IsDigit(clean[0]))
        {
            clean = "_" + clean;
        }
        return clean;
    }

    public static bool IsValidIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || char.IsDigit(value[0]))
        {
            return false;
        }
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    public static string NormalizePackagePath(string? value)
    {
        var path = ExtractPath(value);
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        path = path.Replace('\\', '/').Trim();

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            var dot = path.IndexOf('.', lastSlash + 1);
            if (dot >= 0)
            {
                path = path[..dot];
            }
        }

        return path.TrimEnd('\'', '"');
    }

    public static string AssetName(string? value)
    {
        var packagePath = NormalizePackagePath(value);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return "";
        }

        var slash = packagePath.LastIndexOf('/');
        return slash >= 0 ? packagePath[(slash + 1)..] : packagePath;
    }

    public static string ObjectPath(string? value)
    {
        var packagePath = NormalizePackagePath(value);
        var assetName = AssetName(packagePath);
        return string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(assetName)
            ? ""
            : $"{packagePath}.{assetName}";
    }

    public static int RepairSplitPathNameMapEntries(
        UAsset asset,
        IEnumerable<string> packagePaths,
        ICollection<string>? log = null)
    {
        var repairs = 0;
        var cleanTargets = packagePaths
            .Select(NormalizePackagePath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Select(x => new
            {
                PackagePath = x,
                AssetName = AssetName(x)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.AssetName))
            .ToArray();

        if (cleanTargets.Length == 0)
        {
            return 0;
        }

        var nameMap = asset.GetNameMapIndexList();
        for (var i = 0; i < nameMap.Count; i++)
        {
            var original = nameMap[i].ToString();
            foreach (var target in cleanTargets)
            {
                if (TryRepairSplitPathEntry(original, target.PackagePath, target.AssetName, out var repaired))
                {
                    asset.SetNameReference(i, new FString(repaired));
                    log?.Add($"{original} -> {repaired} (cleaned duplicated object suffix)");
                    repairs++;
                    break;
                }
            }
        }

        return repairs;
    }

    private static bool TryRepairSplitPathEntry(
        string original,
        string packagePath,
        string assetName,
        out string repaired)
    {
        repaired = original;
        var objectSuffix = "." + assetName;

        var packageObjectPath = packagePath + objectSuffix;
        if (original.Equals(packageObjectPath, StringComparison.Ordinal) ||
            original.StartsWith(packageObjectPath + objectSuffix, StringComparison.Ordinal))
        {
            repaired = packagePath;
            return repaired != original;
        }

        var bareObjectPath = assetName + objectSuffix;
        if (original.Equals(bareObjectPath, StringComparison.Ordinal) ||
            original.StartsWith(bareObjectPath + objectSuffix, StringComparison.Ordinal))
        {
            repaired = assetName;
            return repaired != original;
        }

        return false;
    }

    private static string ExtractPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var path = value.Trim();

        var firstQuote = path.IndexOf('\'');
        if (firstQuote >= 0)
        {
            var secondQuote = path.IndexOf('\'', firstQuote + 1);
            if (secondQuote > firstQuote)
            {
                path = path[(firstQuote + 1)..secondQuote];
            }
        }

        var gameIndex = path.IndexOf("/Game/", StringComparison.OrdinalIgnoreCase);
        if (gameIndex >= 0)
        {
            path = path[gameIndex..];
        }

        var whitespace = path.IndexOfAny([' ', '\t', '\r', '\n']);
        if (whitespace > 0)
        {
            path = path[..whitespace];
        }

        return path.Trim().Trim('\'', '"');
    }
}
