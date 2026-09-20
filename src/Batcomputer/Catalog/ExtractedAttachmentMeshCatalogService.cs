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
            foreach (var characters in CharacterContentRootService.Enumerate(content))
            {
                var attachments = Path.Combine(characters, "Attachments");
                if (!Directory.Exists(attachments)) continue;
                foreach (var (prefix, cls) in new[] { ("SK_", "SkeletalMesh"), ("SM_", "StaticMesh") })
                    foreach (var file in Directory.EnumerateFiles(attachments, prefix + "*.uasset", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        var package = ExtractedPackagePathService.PackagePathFromFile(content, file);
                        if (package is not null) found.Add(new GameDataAsset { Path = package, Class = cls });
                    }
            }
            _assets = found.DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            _root = content;
            return _assets;
        }
    }
}
