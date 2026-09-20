using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Resolve missing cooked Blueprint parent schemas from actual native exports, never empty guesses.</summary>
internal static class NativeBlueprintSchemaService
{
    internal static void EnsureParents(UAsset asset) => EnsureParents(asset, new HashSet<string>(StringComparer.Ordinal));

    private static void EnsureParents(UAsset asset, HashSet<string> visited)
    {
        if (asset.Mappings is not { } mappings) return;
        asset.GetParentClass(out var parentPath, out var parentName);
        var name = parentName?.ToString() ?? "";
        var package = UnrealPathUtil.NormalizePackagePath(parentPath?.ToString() ?? "");
        if (name.Length == 0 || mappings.Schemas.ContainsKey(name) ||
            !ExtractedPackagePathService.IsContentPackagePath(package)) return;
        if (visited.Count >= 32 || !visited.Add(package))
            throw new InvalidDataException("Cyclic or excessively deep native Blueprint inheritance: " + package);
        var file = ResolveParentFile(asset.FilePath, package, AppSettings.Current.EffectiveExtractedContentRoot());
        if (file is null || !File.Exists(file))
            throw new InvalidDataException(package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)
                ? $"The generated parent Blueprint {package} is missing from this character's staging folder. Rebuild the character from its saved project."
                : $"The parent Blueprint {package} is missing from the active extraction. Refresh game assets before editing this character.");
        var parent = new UAsset(file, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        EnsureParents(parent, visited);
        if (parent.GetClassExport() is not { } parentClass || parentClass.ObjectName.ToString() != name)
            throw new InvalidDataException("The extracted parent Blueprint has no matching class: " + package + "." + name);
        // Loading the class normally registers this schema; explicitly derive it if needed.
        if (!mappings.Schemas.ContainsKey(name))
            mappings.Schemas[name] = UAssetAPI.Unversioned.Usmap.GetSchemaFromStructExport(name, parent);
    }

    internal static string? ResolveParentFile(string? childFile, string package, string nativeContent)
    {
        if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            return ExtractedPackagePathService.ResolvePackageUasset(nativeContent, package);
        // Generated parents do not exist in the game's extraction. Resolve only against
        // this asset's own Content tree, never another suit's cache or an old release.
        if (string.IsNullOrWhiteSpace(childFile) || !Path.IsPathFullyQualified(childFile)) return null;
        for (var directory = Directory.GetParent(childFile); directory is not null; directory = directory.Parent)
        {
            if (!directory.Name.Equals("Content", StringComparison.OrdinalIgnoreCase)) continue;
            var childPackage = ExtractedPackagePathService.PackagePathFromFile(directory.FullName, childFile);
            if (childPackage?.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) != true) return null;
            var file = ExtractedPackagePathService.ResolvePackageUasset(directory.FullName, package);
            return file is not null && File.Exists(file) && File.Exists(Path.ChangeExtension(file, ".uexp")) ? file : null;
        }
        return null;
    }
}
