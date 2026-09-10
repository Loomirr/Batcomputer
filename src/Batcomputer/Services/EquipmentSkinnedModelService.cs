using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class EquipmentSkinnedModelService
{
    internal const string Donor = "/Game/Models/Gadgets/GA_RubberBulletPistol_Gordon/Sk_GA_RubberBulletPistol_Gordon";
    internal const string Weapon = "/Game/Characters/Equipment/RubberBulletGun/BP_SKRubberBulletGun_Weapon";
    internal const string Eta = "/Game/Characters/Equipment/RubberBulletGun/DA_ETA_RubberBulletGun";
    internal static bool IsTested(EquipmentAssetPart part) => part.OwnerPackage == Weapon && part.ExportName == "WeaponMesh" && part.Package == Donor;
    internal static bool Supports(EquipmentAssetPart part) => part.AssetClass == "SkeletalMesh" &&
        ExtractedPackagePathService.IsContentPackagePath(part.Package) && !part.Package.Contains("/Mods/", StringComparison.OrdinalIgnoreCase) &&
        part.PropertyPath is "SkeletalMesh" or "SkinnedAsset";
    internal static IEnumerable<SkinnedMeshImport> Models(NativeSuitProject project) => project.EquipmentSlots
        .Where(s => s.Custom is not null).SelectMany(s => s.Custom!.Parts).Where(p => p.SkinnedModel is not null).Select(p => p.SkinnedModel!);

    internal static string Bake(NativeSuitProject project, string content, string root, EquipmentAssetPart part,
        EquipmentPartEdit edit, UAsset asset, NormalExport component)
    {
        var mesh = edit.SkinnedModel ?? throw new InvalidDataException("Import a weighted FBX before saving this skeletal replacement.");
        if (!Supports(part) || edit.Model is not null || mesh.DonorMeshPackage != part.Package || mesh.Component != part.ExportName ||
            !mesh.MeshPackage.StartsWith(root + "/", StringComparison.Ordinal) || mesh.HiddenComponents.Count != 0)
            throw new InvalidDataException("The skeletal equipment recipe no longer matches its supported native rig. Reimport it in the equipment workshop.");
        SkinnedMeshStageService.ValidateRecipe(mesh);
        if (!component.GetExportClassType()!.ToString().Contains("SkeletalMeshComponent", StringComparison.Ordinal))
            throw new InvalidDataException("This skeletal reference is not a native skeletal mesh component. It needs a separate adapter.");
        var directory = new SuitProjectService(AppSettings.Current.EffectiveProjectRoot()).ProjectOutputDirectory(project);
        SkinnedMeshStageService.BakeMesh(content, directory, mesh);
        foreach (var name in new[] { "SkeletalMesh", "SkinnedAsset" })
        {
            var property = component.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == name);
            if (property is null) { property = new ObjectPropertyData(new FName(asset, name)); component.Data.Add(property); }
            CustomEquipmentService.ReplaceReference(asset, component, property, mesh.MeshPackage, "SkeletalMesh");
        }
        // Native component overrides otherwise hide the imported mesh's material slots.
        var materials = component.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
        if (materials is not null) materials.Value = [];
        return mesh.MeshPackage;
    }
}
