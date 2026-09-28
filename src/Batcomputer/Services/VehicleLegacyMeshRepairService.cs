using System.Text.Json;

namespace Batcomputer;

/// <summary>Rebuilds pre-root-scale vehicle cooks from project-owned FBXs without replacing a working revision.</summary>
internal static class VehicleLegacyMeshRepairService
{
    internal static IReadOnlyList<string> Pending(string directory, VehicleProject project)
    {
        var pending = new List<string>();
        if (IsLegacy(directory, project.Model)) pending.Add("body");
        if (IsLegacy(directory, project.SummonModel)) pending.Add("summon body");
        return pending;
    }

    private static bool IsLegacy(string directory, SkinnedMeshImport? mesh)
    {
        if (mesh is null) return false;
        var cache = SkinnedMeshCookService.SafePath(directory, mesh.CacheRelativePath);
        var file = Path.Combine(cache, "validated.json");
        if (!File.Exists(file)) return false; // Other missing/invalid cooks keep their normal diagnostic.
        var manifest = JsonSerializer.Deserialize<SkinnedMeshCookService.CookManifest>(File.ReadAllText(file))
            ?? throw new InvalidDataException("Empty vehicle mesh validation record.");
        return manifest.RigValidationVersion < SkinnedMeshCookService.RigValidationVersion;
    }

    internal static async Task<VehicleProject> RepairAsync(VehicleProjectService projects, VehicleProject original,
        Action<string> log, CancellationToken cancellation = default)
    {
        var projectFile = projects.ProjectPath(original.Id);
        var before = SkinnedMeshCookService.Hash(projectFile);
        var directory = projects.DirectoryFor(original);
        var repaired = original.Clone();
        if (IsLegacy(directory, repaired.Model)) repaired.Model = await Rebuild(directory, repaired.Model!, "body", log, cancellation);
        if (IsLegacy(directory, repaired.SummonModel)) repaired.SummonModel = await Rebuild(directory, repaired.SummonModel!, "summon body", log, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (SkinnedMeshCookService.Hash(projectFile) != before)
            throw new InvalidDataException("The vehicle project changed while its FBXs were being rebuilt. Reopen it before saving; the new cooks remain in its import cache.");
        if (Pending(directory, repaired).Count > 0) throw new InvalidDataException("A vehicle mesh still needs rebuilding.");
        File.Copy(projectFile, projectFile + ".pre-rig-scale-" + Guid.NewGuid().ToString("N") + ".bak");
        projects.Save(repaired);
        log("Saved validated vehicle mesh revisions; previous cooks and FBXs remain available for recovery.");
        return repaired;
    }

    private static async Task<SkinnedMeshImport> Rebuild(string directory, SkinnedMeshImport old, string label,
        Action<string> log, CancellationToken cancellation)
    {
        var source = SkinnedMeshCookService.SafePath(directory, old.SourceRelativePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Saved " + label + " FBX is missing. Restore it before rebuilding.", source);
        if (SkinnedMeshCookService.Hash(source) != old.SourceSha256)
            throw new InvalidDataException("Saved " + label + " FBX has changed since the original import. Reimport it manually to review the rig.");
        log("Rebuilding " + label + " from its saved FBX…");
        var result = await SkinnedMeshCookService.ImportAsync(directory, source, old, log, cancellation);
        var manifest = SkinnedMeshStageService.ReadManifest(directory, result);
        if (!old.Materials.Select(m => m.SourceMaterialName).SequenceEqual(manifest.Slots, StringComparer.Ordinal))
            throw new InvalidDataException("The rebuilt " + label + " has different material slots. The vehicle was not changed; review the new import manually.");
        // Import defaults can evolve; preserve every material chosen in the saved vehicle.
        result.Materials = old.Materials.Select(m => new CustomStaticMeshMaterialSlot {
            Slot = m.Slot, SourceMaterialName = m.SourceMaterialName, StableSlotName = m.StableSlotName,
            MaterialPath = m.MaterialPath
        }).ToList();
        SkinnedMeshStageService.ValidateRecipe(result);
        return result;
    }
}
