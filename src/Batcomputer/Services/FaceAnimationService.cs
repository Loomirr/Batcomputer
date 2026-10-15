using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Scoped facial-controller selection, independent of face materials and the body animation graph.</summary>
internal static class FaceAnimationService
{
    internal const string BruceWayne = "/Game/Characters/Attachments/LEGOface/ABP_LEGOface_BruceWayne";
    internal const string FaceMesh = "/Game/Characters/Attachments/LEGOface/SK_LEGOface";

    internal static bool Apply(UAsset asset, string controller)
    {
        if (string.IsNullOrWhiteSpace(controller)) return false;
        // Expand only after another native controller/rig pair has been checked.
        if (controller != BruceWayne) throw new InvalidDataException("Unsupported facial animation controller.");
        var face = asset.Exports.OfType<NormalExport>().SingleOrDefault(e => e.ObjectName.ToString() == "Face_GEN_VARIABLE");
        // Suits can intentionally remove the face while retaining an inherited
        // controller preference. Do not recreate it or mutate the saved recipe.
        if (face is null) return false;
        var meshes = face.Data.OfType<ObjectPropertyData>().Where(p => p.Name.ToString() is "SkeletalMesh" or "SkinnedAsset").ToArray();
        if (meshes.Length > 0 && meshes.All(p => p.Value.IsNull())) return false;
        var activeMeshes = meshes.Where(p => !p.Value.IsNull()).ToArray();
        if (activeMeshes.Length == 0 || activeMeshes.Any(p => !p.Value.IsImport() || SwordCombatService.Package(asset, p.Value) != FaceMesh))
            throw new InvalidDataException("Bruce Wayne's facial controller requires the native SK_LEGOface rig.");
        var anim = face.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "AnimClass")
            ?? throw new InvalidDataException("The face has no native animation-class field.");
        var index = SwordCombatService.Obj(asset, controller, UnrealPathUtil.AssetName(controller) + "_C", "/Script/Engine", "AnimBlueprintGeneratedClass");
        anim.Value = index;
        if (!face.CreateBeforeSerializationDependencies.Contains(index)) face.CreateBeforeSerializationDependencies.Add(index);
        return true;
    }

    internal static void ApplyToPackagedRoot(NativeSuitProject project, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(project.FaceAnimationBlueprintPackage)) return;
        var mappings = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath() ?? throw new InvalidDataException("Configure game mappings first."));
        foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
        {
            if (!package.StartsWith("/Game/Mods/", StringComparison.Ordinal) || !HeldItemService.ValidPackage(package))
                throw new InvalidDataException("Facial-controller changes may only edit suit-local character packages.");
            var file = Path.GetFullPath(Path.Combine(contentRoot, package[6..] + ".uasset"));
            var asset = new UAsset(file, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            NativeBlueprintSchemaService.EnsureParents(asset);
            if (!Apply(asset, project.FaceAnimationBlueprintPackage)) continue;
            asset.Write(file);
            var after = new UAsset(file, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            var face = after.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "Face_GEN_VARIABLE");
            var anim = face.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "AnimClass");
            if (SwordCombatService.Package(after, anim.Value) != project.FaceAnimationBlueprintPackage)
                throw new InvalidDataException("The facial controller did not survive serialization.");
        }
    }
}
