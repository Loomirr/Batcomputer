using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Overlay new character attachment meshes without depending on the shipped catalog's age.</summary>
internal static class ExtractedAttachmentMeshCatalogService
{
    private static readonly object Gate = new();
    private static string _root = "";
    private static IReadOnlyList<GameDataAsset> _assets = [];

    internal static void Invalidate()
    {
        lock (Gate) { _root = ""; _assets = []; }
    }

    internal static IReadOnlyList<GameDataAsset> MergeWithActiveExtraction(IEnumerable<GameDataAsset> shipped, string className)
    {
        var content = AppSettings.Current.EffectiveExtractedContentRoot();
        return Discover(content).Where(a => a.Class == className).Concat(shipped)
            .DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<GameDataAsset> Discover(string content)
    {
        lock (Gate)
        {
            if (!Directory.Exists(content)) return [];
            content = Path.GetFullPath(content);
            if (content.Equals(_root, StringComparison.OrdinalIgnoreCase)) return _assets;
            var found = new List<GameDataAsset>();
            var shippedClasses = GameDataService.Instance.Db.Assets
                .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Class, StringComparer.OrdinalIgnoreCase);
            foreach (var characters in CharacterContentRootService.Enumerate(content))
            {
                var attachments = Path.Combine(characters, "Attachments");
                if (!Directory.Exists(attachments)) continue;
                foreach (var file in Directory.EnumerateFiles(attachments, "*.uasset", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var package = ExtractedPackagePathService.PackagePathFromFile(content, file);
                    if (package is null) continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    var cls = shippedClasses.GetValueOrDefault(package)
                        ?? (name.StartsWith("SK_", StringComparison.OrdinalIgnoreCase) ? "SkeletalMesh" :
                            name.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) ? "StaticMesh" :
                            ReadMeshClass(file));
                    if (cls is "StaticMesh" or "SkeletalMesh")
                        found.Add(new GameDataAsset { Path = package, Class = cls });
                }
            }
            _assets = found.DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            _root = content;
            return _assets;
        }
    }

    private static string ReadMeshClass(string file)
    {
        try
        {
            var asset = new UAsset(file, EngineVersion.VER_UE5_6, null,
                CustomSerializationFlags.SkipPreloadDependencyLoading | CustomSerializationFlags.SkipParsingExports);
            foreach (var export in asset.Exports)
            {
                if (export.OuterIndex.Index != 0 || !export.ClassIndex.IsImport()) continue;
                var cls = export.ClassIndex.ToImport(asset)?.ObjectName.ToString();
                if (cls is "StaticMesh" or "SkeletalMesh") return cls;
            }
        }
        catch { /* Unknown assets stay out of the mesh catalog; never guess their class. */ }
        return "";
    }
}
