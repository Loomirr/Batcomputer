using System.Security.Cryptography;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

// Deliberately bounded: this changes the successful-hit mesh, not Niagara scripts or behavior.
internal static class EquipmentImpactMeshService
{
    internal const string System = "/Game/VFX/Mechanic/Gadgets/Batarang/Emitters/NS_Batarang_SuccessfulHit";
    internal const string Mesh = "/Game/Models/Gadgets/GA_Batarang/SM_GA_Batarang";
    internal static bool Supports(EquipmentAssetPart part) => part.AssetClass == "NiagaraSystem" &&
        part.Package == System && part.PropertyPath == "ProjectileSuccessfulVFX";
    internal static string Bake(EquipmentAssetPart part, WeaponModelRecipe model, string root,
        string extracted, string content, string mapping, Usmap maps)
    {
        if (!Supports(part)) throw new InvalidDataException("This effect does not have a supported mesh adapter.");
        var folder = root + "/Impact_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(part.Key)))[..12];
        var meshPackage = folder + "/SM_Impact";
        var systemPackage = folder + "/NS_Impact";
        WeaponModelService.Bake(model, extracted, mapping, content, meshPackage);
        var asset = EquipmentAssetService.Read(extracted, System, maps);
        var renderer = asset.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "NiagaraMeshRendererProperties");
        var meshReferences = EquipmentAssetService.Properties(renderer.Data).Where(p => EquipmentAssetService.Reference(asset, p.Property)?.Package == Mesh).ToArray();
        if (meshReferences.Length != 1) throw new InvalidDataException("Native Batarang impact renderer changed. Refresh extraction before editing this effect.");
        CustomEquipmentService.ReplaceReference(asset, renderer, meshReferences[0].Property, meshPackage, "StaticMesh");
        renderer.Data.OfType<BoolPropertyData>().Single(p => p.Name.ToString() == "bOverrideMaterials").Value = false;
        renderer.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "OverrideMaterials").Value = [];
        CustomEquipmentService.Rename(asset, new Dictionary<string,string> { [System] = systemPackage });
        asset.FolderName = new FString(systemPackage);
        var file = Path.Combine(content, systemPackage[6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset";
        asset.Write(file);
        var check = EquipmentAssetService.Read(content, systemPackage, maps);
        if (check.Exports.Count != asset.Exports.Count) throw new InvalidDataException("Impact effect failed its cooked roundtrip.");
        var written = check.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "NiagaraMeshRendererProperties");
        if (written.Data.OfType<BoolPropertyData>().Single(p => p.Name.ToString() == "bOverrideMaterials").Value ||
            !EquipmentAssetService.Properties(written.Data).Any(p => EquipmentAssetService.Reference(check,p.Property)?.Package == meshPackage))
            throw new InvalidDataException("Impact mesh/material binding failed its cooked roundtrip.");
        return systemPackage;
    }
}
