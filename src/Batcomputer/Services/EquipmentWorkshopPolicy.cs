namespace Batcomputer;

/// <summary>One fail-closed policy shared by the workshop and the custom-equipment builder.</summary>
internal static class EquipmentWorkshopPolicy
{
    internal sealed record Access(bool CanCustomize, string Reason, IReadOnlyList<string> PlayableOwners);
    internal static IReadOnlyList<string> PlayableOwners(GameDataEquipment? equipment, GameDataDb db) => equipment is null ? [] :
        db.Families.Where(f => equipment.NativeFamilies.Contains(f.Name, StringComparer.OrdinalIgnoreCase) &&
            (f.PlayableCount > 0 || db.Assets.Any(a => a.Class == "BlueprintGeneratedClass" &&
                a.Path.StartsWith($"/Game/Characters/Minifig/{f.Name}/", StringComparison.OrdinalIgnoreCase) &&
                UnrealPathUtil.AssetName(a.Path).StartsWith("BP_", StringComparison.OrdinalIgnoreCase) &&
                UnrealPathUtil.AssetName(a.Path).EndsWith("_Playable", StringComparison.OrdinalIgnoreCase))))
            .Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();

    internal static Access Evaluate(GameDataEquipment? equipment, EquipmentAssetProfile? profile, GameDataDb db)
    {
        var owners = PlayableOwners(equipment, db);
        if (equipment is null)
            return new(false, "No confirmed playable owner. NPC, boss and unverified equipment can only be inspected.", owners);
        if (profile is null)
            return new(false, "Equipment files have not been verified. Wait for inspection or refresh the extraction.", owners);
        if (!equipment.EtaPackage.Equals(profile.EtaPackage, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(equipment.EdPackage) || !equipment.EdPackage.Equals(profile.DefinitionPackage, StringComparison.OrdinalIgnoreCase))
            return new(false, "The extracted variant has no matching equipment definition in the catalog.", owners);
        var skinned = profile.Parts.Where(p => p.AssetClass == "SkeletalMesh").ToArray();
        // Count roles, not references: a skeletal gun can have many static bullets;
        // UE also stores SkeletalMesh and SkinnedAsset for the same body.
        if (owners.Count == 0 && skinned.Length == 0)
            return new(false, "No confirmed playable owner. NPC and boss equipment can only be inspected.", owners);
        var provenPistol = equipment.EtaPackage == EquipmentSkinnedModelService.Eta && skinned.Length > 0 && skinned.All(EquipmentSkinnedModelService.IsTested);
        return new(true, provenPistol ? "Gordon pistol: tested in-game. Weighted FBX on its original rig; native animations retained." : skinned.Length > 0 ?
            "EXPERIMENTAL · Untested skeletal equipment. Use its original rig and test animations, sockets and gameplay in-game." + (owners.Count == 0 ? " No confirmed playable owner; its abilities may require NPC-specific setup." : "") :
            "Customize static models and icons. Controls and upgrades stay with the native equipment.", owners);
    }
    private static bool IsMainBody(EquipmentAssetPart part) =>
        part.ExportName.Equals("WeaponMesh", StringComparison.OrdinalIgnoreCase) || part.ExportName.Equals("WeaponMesh_GEN_VARIABLE", StringComparison.OrdinalIgnoreCase);
    internal static void RequireEditable(GameDataEquipment? equipment, EquipmentAssetProfile profile, GameDataDb db)
    {
        var access = Evaluate(equipment, profile, db);
        if (!access.CanCustomize)
            throw new InvalidDataException($"Custom equipment '{equipment?.Name ?? UnrealPathUtil.AssetName(profile.EtaPackage)}' is view-only: {access.Reason} Choose a supported donor in the Equipment workshop or restore the native slot.");
    }
}
