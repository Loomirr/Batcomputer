using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Appearance edits retain the original Blueprint, its identity and all opaque behavior.</summary>
internal static class CharacterReplacementService
{
    internal sealed record Reference(string Package, string Kind)
    {
        public override string ToString() => $"{Kind} · {Path.GetFileName(Package)}";
    }

    internal static string Package(string path)
    {
        path = UnrealPathUtil.NormalizePackagePath(path.Replace('\\', '/'));
        const string prefix = "LEGOBatmanLotDK/Content/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "/Game/" + path[prefix.Length..];
        return "/" + path.TrimStart('/');
    }

    internal static bool Supported(string kind) => kind is "MaterialInstanceConstant" or "SkeletalMesh" or "StaticMesh";

    internal static Reference[] References(UAsset asset) => asset.Imports
        .Where(i => Supported(i.ClassName.ToString()) && i.OuterIndex.IsImport())
        .Select(i => new Reference(i.OuterIndex.ToImport(asset).ObjectName.ToString(), i.ClassName.ToString()))
        .Where(r => r.Package.StartsWith("/Game/", StringComparison.Ordinal))
        .Distinct().OrderBy(r => r.Kind).ThenBy(r => r.Package).ToArray();

    internal static Reference[] Inspect(string target, string content, string mappings)
    {
        NpcAppearanceTestService.Validate(new(target, Changes: [new("/Game/MI_A", "/Game/MI_B")]));
        return References(Read(Path.Combine(content, target[6..] + ".uasset"), new Usmap(mappings)));
    }

    private static UAsset Read(string path, Usmap maps) => new(path, EngineVersion.VER_UE5_6, maps,
        CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);

    internal static void ValidateSelection(string target, string original, string replacement, string content, string mappings, IReadOnlyList<string> roots)
    {
        NpcAppearanceTestService.Validate(new(target, Changes: [new(original, replacement)], VisualContentRoots: roots.ToArray()));
        var maps = new Usmap(mappings);
        ValidateAssets(Read(Path.Combine(content, target[6..] + ".uasset"), maps), new(original, replacement), content, maps, roots);
    }

    internal static string ResolveAsset(string package, string nativeContent, IReadOnlyList<string> visualRoots)
    {
        if (!package.StartsWith("/Game/", StringComparison.Ordinal) || package.Contains("..") ||
            package.Any(c => !(char.IsLetterOrDigit(c) || c is '/' or '_')))
            throw new InvalidDataException("Invalid visual dependency package: " + package);
        var relative = package[6..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) return Path.Combine(nativeContent, relative);
        var matches = visualRoots.Select(root => Path.GetFullPath(Path.Combine(root, relative)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Custom visual dependency must resolve to exactly one source: {package} ({matches.Length} found).");
        return matches[0];
    }

    internal static void CopyVisualDependencies(IEnumerable<string> packages, string nativeContent, IReadOnlyList<string> roots, string stage, Usmap maps)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(packages);
        while (pending.TryDequeue(out var package))
        {
            if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) || !visited.Add(package)) continue;
            var source = ResolveAsset(package, nativeContent, roots);
            var asset = Read(source, maps);
            foreach (var export in asset.Exports)
            {
                var kind = export.ClassIndex.IsImport() ? export.ClassIndex.ToImport(asset).ObjectName.ToString() : "";
                if (kind is not ("MaterialInstanceConstant" or "Texture2D" or "StaticMesh" or "SkeletalMesh" or "Skeleton" or "PhysicsAsset" or "SkeletalBodySetup" or "BodySetup" or "AssetImportData"))
                    throw new InvalidDataException("Non-visual or unsupported custom dependency: " + package + " (" + kind + "). Blueprints, animation graphs and gameplay assets cannot be bundled here.");
            }
            if (!asset.VerifyBinaryEquality()) throw new InvalidDataException("Custom visual asset does not round-trip with current mappings: " + package);
            foreach (var import in asset.Imports.Where(i => i.ClassName.ToString() == "Package"))
            {
                var dependency = import.ObjectName.ToString();
                if (dependency.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) pending.Enqueue(dependency);
            }
            foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                var file = Path.ChangeExtension(source, ext);
                if (!File.Exists(file)) continue;
                var destination = Path.Combine(stage, package[6..] + ext);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: false);
                if (!File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(destination))) throw new IOException("Visual dependency changed during staging: " + package);
            }
        }
    }

    internal static void ValidateAssets(UAsset target, NpcAppearanceTestService.Change change, string content, Usmap maps, IReadOnlyList<string>? visualRoots = null)
    {
        var reference = References(target).SingleOrDefault(r => r.Package == change.Original)
            ?? throw new InvalidDataException("Only directly referenced materials and meshes can be edited: " + change.Original);
        UAsset Load(string package) => Read(ResolveAsset(package, content, visualRoots ?? []), maps);
        var before = Load(change.Original);
        var after = Load(change.Replacement);
        string Kind(UAsset a) => a.Exports.Where(e => e.ClassIndex.IsImport())
            .Select(e => e.ClassIndex.ToImport(a).ObjectName.ToString()).FirstOrDefault(Supported) ?? "";
        if (Kind(before) != reference.Kind || Kind(after) != reference.Kind)
            throw new InvalidDataException("Replacement must have the same cooked asset class: " + reference.Kind);
        if (reference.Kind == "SkeletalMesh")
        {
            string[] Skeletons(UAsset a) => a.Imports.Where(i => i.ClassName.ToString() == "Skeleton" && i.OuterIndex.IsImport())
                .Select(i => i.OuterIndex.ToImport(a).ObjectName.ToString()).Distinct().Order().ToArray();
            var expected = Skeletons(before);
            if (expected.Length != 1 || !expected.SequenceEqual(Skeletons(after)))
                throw new InvalidDataException("Mesh replacement requires the exact same skeleton. Retargeted/custom rigs need a separate compatibility workflow.");
        }
    }
}
