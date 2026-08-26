namespace Batcomputer;

/// <summary>
/// Immutable reverse index used by the Faces browser. Building it once keeps face-tile rendering
/// from reopening the workspace material library and rescanning every native part for every tile.
/// </summary>
internal sealed class FaceMaterialCompatibilityLookup
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _meshesByMaterial;

    private FaceMaterialCompatibilityLookup(
        IReadOnlyDictionary<string, IReadOnlyList<string>> meshesByMaterial)
    {
        _meshesByMaterial = meshesByMaterial;
    }

    public static FaceMaterialCompatibilityLookup Build(
        Func<IEnumerable<GeneratedMaterialEntry>> projectMaterialLoader,
        Func<IEnumerable<GeneratedMaterialEntry>> workspaceMaterialLoader,
        Func<IEnumerable<NativeSuitPartRecord>> partLoader)
    {
        ArgumentNullException.ThrowIfNull(projectMaterialLoader);
        ArgumentNullException.ThrowIfNull(workspaceMaterialLoader);
        ArgumentNullException.ThrowIfNull(partLoader);

        // Material-library and part-index access may involve disk I/O. Invoke each source exactly
        // once, then make every tile lookup a case-insensitive dictionary read.
        var projectMaterials = (projectMaterialLoader() ?? Enumerable.Empty<GeneratedMaterialEntry>()).ToList();
        var workspaceMaterials = (workspaceMaterialLoader() ?? Enumerable.Empty<GeneratedMaterialEntry>()).ToList();
        var parts = (partLoader() ?? Enumerable.Empty<NativeSuitPartRecord>()).ToList();

        var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Where(IsFacePart))
        {
            var mesh = UnrealPathUtil.NormalizePackagePath(part.MeshPackagePath);
            if (string.IsNullOrWhiteSpace(mesh))
            {
                continue;
            }

            foreach (var material in part.Materials ?? Enumerable.Empty<NativeSuitObjectRef>())
            {
                AddObservedMesh(index, material.PackagePath, mesh);
                AddObservedMesh(index, material.ObjectPath, mesh);
            }
        }

        // Authored metadata is more precise than an observed native part. Workspace metadata wins
        // over the part index, and the current suit wins over both. Empty metadata deliberately
        // falls through so older entries do not hide a useful lower-priority observation.
        OverlayAuthoredMetadata(index, workspaceMaterials);
        OverlayAuthoredMetadata(index, projectMaterials);

        var snapshot = index.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        return new FaceMaterialCompatibilityLookup(snapshot);
    }

    public IReadOnlyList<string> Resolve(string? materialPath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(materialPath);
        return !string.IsNullOrWhiteSpace(package) && _meshesByMaterial.TryGetValue(package, out var meshes)
            ? meshes
            : Array.Empty<string>();
    }

    private static bool IsFacePart(NativeSuitPartRecord part) =>
        part.Slot.Contains("face", StringComparison.OrdinalIgnoreCase) ||
        part.SemanticKind.Equals("Face", StringComparison.OrdinalIgnoreCase);

    private static void AddObservedMesh(
        IDictionary<string, HashSet<string>> index,
        string? materialPath,
        string meshPackagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(materialPath);
        if (string.IsNullOrWhiteSpace(package))
        {
            return;
        }

        if (!index.TryGetValue(package, out var meshes))
        {
            meshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            index[package] = meshes;
        }
        meshes.Add(meshPackagePath);
    }

    private static void OverlayAuthoredMetadata(
        IDictionary<string, HashSet<string>> index,
        IEnumerable<GeneratedMaterialEntry> materials)
    {
        foreach (var material in materials)
        {
            var package = UnrealPathUtil.NormalizePackagePath(material.PackagePath);
            var meshes = (material.CompatibleFaceMeshPackagePaths ?? new List<string>())
                .Select(UnrealPathUtil.NormalizePackagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(package) || meshes.Count == 0)
            {
                continue;
            }

            index[package] = meshes;
        }
    }
}
