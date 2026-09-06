using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

internal static class SkinnedMeshStageService
{
    internal sealed record Target(string Component, string DonorMesh)
    { public override string ToString() => $"{Component} — {UnrealPathUtil.AssetName(DonorMesh)}"; }

    internal static IReadOnlyList<Target> Targets(string content, NativeSuitProject project)
    {
        var maps = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        var playable = EquipmentAssetService.Read(content, project.TargetPackages.Playable, maps);
        var cutscene = EquipmentAssetService.Read(content, project.TargetPackages.Cutscene, maps);
        var results = new List<Target>();
        foreach (var component in playable.Exports.OfType<NormalExport>())
        {
            var name = component.ObjectName.ToString().Replace("_GEN_VARIABLE", "");
            var properties = component.Data.OfType<ObjectPropertyData>();
            var mesh = properties.FirstOrDefault(p => p.Name.ToString() == "SkeletalMesh" && !p.Value.IsNull());
            var package = mesh is null ? null : EquipmentAssetService.Reference(playable, mesh)?.Package;
            if (package is null || MaterialReplaceService.FindComponentExport(cutscene, name) is not { } other ||
                !other.Data.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "SkeletalMesh" && !p.Value.IsNull())) continue;
            // These need facial/morph or cloth support, not just a replacement weighted mesh.
            if (name.Contains("Face", StringComparison.OrdinalIgnoreCase) || name.Contains("Cape", StringComparison.OrdinalIgnoreCase) ||
                package.Contains("/Cape/", StringComparison.OrdinalIgnoreCase) || package.Contains("LEGOface", StringComparison.OrdinalIgnoreCase)) continue;
            var original = project.SkinnedMeshes.FirstOrDefault(m => m.Component == name)?.DonorMeshPackage ?? package;
            if (!original.Contains("/Mods/", StringComparison.OrdinalIgnoreCase) &&
                !project.SkinnedMeshes.Any(m => m.Component.Equals(name, StringComparison.OrdinalIgnoreCase))) results.Add(new Target(name, original));
        }
        return results.OrderBy(t => t.Component == "CharacterMesh0" ? 0 : 1).ThenBy(t => t.Component).ToArray();
    }

    internal static void ValidateRecipe(SkinnedMeshImport mesh)
    {
        SkinnedMeshCookService.RequireNativeDonor(mesh.DonorMeshPackage);
        if (!mesh.MeshPackage.StartsWith("/Game/Mods/", StringComparison.Ordinal) ||
            mesh.MeshPackage.Split('/').Skip(1).Any(s => !UnrealPathUtil.IsValidIdentifier(s)))
            throw new InvalidDataException("The skinned mesh must use a valid mod-owned package.");
        if (string.IsNullOrWhiteSpace(mesh.Component) || string.IsNullOrWhiteSpace(mesh.DonorMeshPackage)) throw new InvalidDataException("Choose a target component and native rig donor.");
        if (mesh.Materials.Count == 0 || mesh.Materials.Select(m => m.Slot).Where((slot, index) => slot != index).Any() ||
            mesh.Materials.Any(m => !ExtractedPackagePathService.IsContentPackagePath(m.MaterialPath) ||
                m.MaterialPath.Split('/').Skip(1).Any(segment => !UnrealPathUtil.IsValidIdentifier(segment))))
            throw new InvalidDataException("Assign a valid cooked game material to each contiguous mesh slot.");
        if (mesh.HiddenComponents.Any(c => c.Equals(mesh.Component, StringComparison.OrdinalIgnoreCase) || c.Contains("CharacterMesh0", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A skinned replacement cannot hide its own component or the body.");
    }

    internal static void Apply(string content, string projectDirectory, NativeSuitProject project, Action<string> log)
    {
        if (project.SkinnedMeshes.Count == 0) return;
        if (project.SkinnedMeshes.Select(m => m.Component).Distinct(StringComparer.OrdinalIgnoreCase).Count() != project.SkinnedMeshes.Count)
            throw new InvalidDataException("Only one skinned replacement may own a component.");
        if (project.SkinnedMeshes.SelectMany(m => m.HiddenComponents).Any(hidden =>
                project.SkinnedMeshes.Any(m => m.Component.Equals(hidden, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("A skinned replacement cannot hide another skinned replacement. Remove that replacement first.");
        var maps = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        foreach (var mesh in project.SkinnedMeshes)
        {
            ValidateRecipe(mesh);
            BakeMesh(content, projectDirectory, mesh);
            foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene })
            {
                var asset = EquipmentAssetService.Read(content, package, maps);
                NativeBodyProfileService.EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
                var component = MaterialReplaceService.FindComponentExport(asset, mesh.Component)
                    ?? throw new InvalidDataException($"Skinned target '{mesh.Component}' is missing from {package}. Restore its native component first.");
                var current = component.Data.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == "SkeletalMesh");
                var currentPackage = current is null ? null : EquipmentAssetService.Reference(asset, current)?.Package;
                if (currentPackage != mesh.DonorMeshPackage && currentPackage != mesh.MeshPackage)
                    throw new InvalidDataException($"The native mesh on '{mesh.Component}' changed. Reimport against its new rig before building.");
                var reference = SwordCombatService.Obj(asset, mesh.MeshPackage, UnrealPathUtil.AssetName(mesh.MeshPackage), "/Script/Engine", "SkeletalMesh");
                SetObject(asset, component, "SkeletalMesh", reference); SetObject(asset, component, "SkinnedAsset", reference);
                if (!component.CreateBeforeSerializationDependencies.Contains(reference)) component.CreateBeforeSerializationDependencies.Add(reference);
                var overrides = component.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
                if (overrides is null) { overrides = new ArrayPropertyData(Name(asset, "OverrideMaterials")) { ArrayType = Name(asset, "ObjectProperty") }; component.Data.Add(overrides); }
                overrides.Value = mesh.Materials.Select((m, i) =>
                {
                    var reference = SwordCombatService.Obj(asset, m.MaterialPath, UnrealPathUtil.AssetName(m.MaterialPath), "/Script/Engine", "MaterialInstanceConstant");
                    if (!component.CreateBeforeSerializationDependencies.Contains(reference)) component.CreateBeforeSerializationDependencies.Add(reference);
                    return (PropertyData)new ObjectPropertyData(Name(asset, i.ToString())) { Value = reference };
                }).ToArray();
                foreach (var hidden in mesh.HiddenComponents)
                {
                    var visual = MaterialReplaceService.FindComponentExport(asset, hidden) ?? throw new InvalidDataException("The hidden visual component no longer exists: " + hidden);
                    foreach (var property in visual.Data.OfType<ObjectPropertyData>().Where(p => p.Name.ToString() is "SkeletalMesh" or "SkinnedAsset" or "StaticMesh")) property.Value = FPackageIndex.FromRawIndex(0);
                }
                var file = SkinnedMeshCookService.SafePath(content, package[6..] + ".uasset"); asset.Write(file);
                var written = EquipmentAssetService.Read(content, package, maps);
                var check = MaterialReplaceService.FindComponentExport(written, mesh.Component)?.Data.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == "SkeletalMesh");
                if (check is null || EquipmentAssetService.Reference(written, check)?.Package != mesh.MeshPackage) throw new InvalidDataException("Skinned component failed its cooked roundtrip.");
            }
            log($"Skinned mesh '{mesh.Name}': native rig retained; {mesh.Materials.Count} materials; playable + cutscene updated.");
        }
    }

    internal static SkinnedMeshCookService.CookManifest ReadManifest(string directory, SkinnedMeshImport mesh)
    {
        var root = SkinnedMeshCookService.SafePath(directory, mesh.CacheRelativePath);
        var manifest = JsonSerializer.Deserialize<SkinnedMeshCookService.CookManifest>(File.ReadAllText(Path.Combine(root, "validated.json")))
            ?? throw new InvalidDataException("Missing validated skeletal cook. Reimport the FBX.");
        if (manifest.SourceHash != mesh.SourceSha256 || manifest.Donor != mesh.DonorMeshPackage || manifest.Skeleton != mesh.SkeletonPackage || manifest.Package != mesh.MeshPackage ||
            SkinnedMeshCookService.Hash(SkinnedMeshCookService.SafePath(directory, mesh.SourceRelativePath)) != manifest.SourceHash)
            throw new InvalidDataException("Skinned source or rig changed since validation. Reimport before building.");
        if (manifest.Files.Count is < 2 or > 3 || !manifest.Files.ContainsKey("mesh.uasset") || !manifest.Files.ContainsKey("mesh.uexp")) throw new InvalidDataException("Invalid skeletal cook manifest.");
        foreach (var (file, hash) in manifest.Files)
        {
            if (file is not ("mesh.uasset" or "mesh.uexp" or "mesh.ubulk") || SkinnedMeshCookService.Hash(Path.Combine(root, file)) != hash)
                throw new InvalidDataException("Cooked skeletal data changed. Reimport before building.");
        }
        return manifest;
    }

    internal static void BakeMesh(string content, string directory, SkinnedMeshImport mesh)
    {
        ValidateRecipe(mesh); var manifest = ReadManifest(directory, mesh);
        if (!mesh.Materials.Select(m => m.SourceMaterialName).SequenceEqual(manifest.Slots)) throw new InvalidDataException("Material slots no longer match the validated cook.");
        var root = SkinnedMeshCookService.SafePath(directory, mesh.CacheRelativePath);
        if (!mesh.MeshPackage.StartsWith("/Game/Mods/", StringComparison.Ordinal)) throw new InvalidDataException("Skinned output must be mod-owned.");
        var file = SkinnedMeshCookService.SafePath(content, mesh.MeshPackage[6..] + ".uasset"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        foreach (var source in manifest.Files.Keys) File.Copy(Path.Combine(root, source), Path.ChangeExtension(file, Path.GetExtension(source)), true);
        var asset = new UAsset(file, EngineVersion.VER_UE5_6, null, CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal) {
            [manifest.TemporarySkeleton] = mesh.SkeletonPackage,
            [UnrealPathUtil.AssetName(manifest.TemporarySkeleton)] = UnrealPathUtil.AssetName(mesh.SkeletonPackage) };
        var parent = mesh.MeshPackage[..mesh.MeshPackage.LastIndexOf('/')];
        foreach (var material in mesh.Materials)
        { replacements[parent + "/MI_Slot_" + material.Slot] = material.MaterialPath; replacements["MI_Slot_" + material.Slot] = UnrealPathUtil.AssetName(material.MaterialPath); }
        RedirectImports(asset, replacements);
        asset.Write(file);
    }
    internal static void RedirectImports(UAsset asset, IReadOnlyDictionary<string, string> replacements)
    {
        // MI_Slot_0..N share an FName base and use its numeric suffix field. Editing only the
        // name map misses these references (or conflates slots). Replace each complete FName
        // on its original import, retaining indices used by the opaque render/skin buffers.
        foreach (var import in asset.Imports)
            if (replacements.TryGetValue(import.ObjectName.ToString(), out var value))
                import.ObjectName = Name(asset, value);
    }
    private static FName Name(UAsset asset, string value) { asset.AddNameReference(new FString(value)); return new FName(asset, value); }
    private static void SetObject(UAsset asset, NormalExport component, string name, FPackageIndex value)
    {
        var property = component.Data.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == name);
        if (property is null) component.Data.Add(new ObjectPropertyData(Name(asset, name)) { Value = value }); else property.Value = value;
    }
}
