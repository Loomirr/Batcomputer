using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class CharacterRegistryBundleRegressionChecks
{
    internal sealed record Result(bool Passed, string Description);

    internal static IReadOnlyList<Result> Run()
    {
        var results = new List<Result>();
        void Check(bool ok, string name) => results.Add(new(ok, name));
        var asset = new UAsset();
        asset.ClearNameIndexList();
        SoftObjectPropertyData Soft(string name, string path)
        {
            var dot = path.LastIndexOf('.');
            return new SoftObjectPropertyData(FName.FromString(asset, name))
            {
                Value = new FSoftObjectPath(new FTopLevelAssetPath(
                    FName.FromString(asset, dot < 0 ? "None" : path[..dot]),
                    FName.FromString(asset, dot < 0 ? "None" : path[(dot + 1)..])), new FString("")),
            };
        }
        const string package = "/Game/Mods/Test/DA_DCMD_Test";
        const string pawn = "/Game/Mods/Test/BP_Playable.BP_Playable_C";
        const string cinematic = "/Game/Mods/Test/BP_Cutscene.BP_Cutscene_C";
        const string eta = "/Game/Characters/Equipment/Mods/Test/DA_ETA_Test.DA_ETA_Test";
        const string native = "/Game/Characters/Equipment/BatClaw/DA_ETA_Batclaw.DA_ETA_Batclaw";
        const string upgrade = "/DLC_Test/Equipment/DA_Upgrades.DA_Upgrades";
        var equipment = new ArrayPropertyData(FName.FromString(asset, "EquipmentList"))
        {
            Value = [Soft("0", eta), Soft("1", native), Soft("2", "None"), Soft("3", eta)],
        };
        var metadata = new NormalExport
        {
            Data = [Soft("Pawn", pawn), Soft("CinematicsActor", cinematic), equipment,
                new ArrayPropertyData(FName.FromString(asset, "UpgradeDataAssets"))
                { Value = [Soft("0", upgrade), Soft("1", "None")] }],
        };
        var row = CharacterRegistryBundleService.CreateCharacterRowFromMetadata(package, metadata);
        var bundles = row.EffectiveBundles.ToDictionary(bundle => bundle.Name, bundle => bundle.Assets);
        Check(bundles.Count == 3 && bundles[RegistryPluginService.GameplayBundle].SequenceEqual([pawn]) &&
            bundles[RegistryPluginService.CinematicBundle].SequenceEqual([cinematic]) &&
            bundles[RegistryPluginService.MetadataBundle].SequenceEqual([eta, native, upgrade]),
            "character bundles use final actor/equipment/upgrade references, retain DLC mounts, omit nulls, and deduplicate loads");
        Check(RegistryPluginService.ValidateRows([row]).Count == 0 &&
            RegistryPluginService.SerializeBundles([row]).Contains($"{package}|ASSETBUNDLE_METADATA|{eta},{native},{upgrade}"),
            "metadata dependencies serialize separately from playable and cinematic bundles");
        var verification = "BATCOMPUTER_REGISTRY_WRITER_RESULT cooked_header=yes expected_primary_rows=1 " +
            "exact_primary_rows=1 exact_primary_ids=1 all_expected_rows=yes all_expected_primary_ids=yes " +
            "sentinel_enabled=yes sentinel_exact_row=yes sentinel_exact_primary_id=yes " +
            "exact_bundle_rows=1 exact_bundles=3 exact_bundle_assets=5 all_expected_bundles=yes";
        Check(RegistryPluginService.VerificationMatches(verification, [row]) &&
            !RegistryPluginService.VerificationMatches(verification.Replace("exact_bundles=3", "exact_bundles=2"), [row]) &&
            !RegistryPluginService.VerificationMatches(verification.Replace("exact_bundle_assets=5", "exact_bundle_assets=50"), [row]) &&
            !RegistryPluginService.VerificationMatches(verification.Replace("expected_primary_rows=1", "expected_primary_rows=10"), [row]) &&
            !RegistryPluginService.VerificationMatches(verification.Replace("all_expected_bundles=yes", "all_expected_bundles=no"), [row]),
            "registry verification requires exact counts and complete named-bundle roundtrips, not prefix matches");
        foreach (var invalid in new[]
                 {
                     new RegistryPluginService.AssetBundle("UNKNOWN", [native]),
                     new RegistryPluginService.AssetBundle(RegistryPluginService.MetadataBundle, []),
                     new RegistryPluginService.AssetBundle(RegistryPluginService.MetadataBundle, [native, native]),
                     new RegistryPluginService.AssetBundle(RegistryPluginService.MetadataBundle, [native + "|Injected"]),
                 })
            Check(RegistryPluginService.ValidateRows([row with { Bundles = [invalid] }]).Count > 0,
                "named bundle rejects unsupported names, empty lists, duplicates or delimiters: " + invalid.Name);
        Check(RegistryPluginService.ValidateRows([row with { Bundles = [row.Bundles![0], row.Bundles[0]] }]).Count > 0 &&
            RegistryPluginService.ValidateRows([row with { GameplayBundleAssets = [pawn] }]).Count > 0,
            "duplicate named bundles cannot silently merge, including the legacy gameplay parameter");
        equipment.Value = [new IntPropertyData(FName.FromString(asset, "0")) { Value = 1 }];
        bool rejected = false;
        try { CharacterRegistryBundleService.CreateCharacterRowFromMetadata(package, metadata); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "unreadable staged equipment metadata fails the build instead of producing an empty loading bundle");
        metadata.Data = [Soft("Pawn", pawn), Soft("CinematicsActor", "None")];
        Check(CharacterRegistryBundleService.CreateCharacterRowFromMetadata(package, metadata).EffectiveBundles.Count() == 1,
            "metadata without equipment or a cinematic actor does not invent loading dependencies");
        metadata.Data = [Soft("Pawn", "None")];
        rejected = false;
        try { CharacterRegistryBundleService.CreateCharacterRowFromMetadata(package, metadata); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "a character with no playable reference cannot pass registry bundle generation");
        const string owner = "/Game/Mods/Test/Equipment/slot_1";
        var upgradePackage = EquipmentUpgradeService.Destination(owner, EquipmentUpgradeService.NativeRoot);
        var otherUpgrade = EquipmentUpgradeService.Destination(owner + "_other", EquipmentUpgradeService.NativeRoot);
        Check(upgradePackage != otherUpgrade && UnrealPathUtil.AssetName(upgradePackage) != UnrealPathUtil.AssetName(otherUpgrade) &&
            upgradePackage == EquipmentUpgradeService.Destination(owner, EquipmentUpgradeService.NativeRoot),
            "upgrade primary IDs are stable and distinct between equipment owners");
        const string setPath = "/Game/Characters/Equipment/Batarang/Upgrades/DA_Batarang_NumberofBatarangsUpgrade.DA_Batarang_NumberofBatarangsUpgrade";
        metadata.Data = [new ArrayPropertyData(FName.FromString(asset, "UpgradeSets")) { Value = [Soft("0", setPath)] }];
        var upgradeRow = CharacterRegistryBundleService.CreateUpgradeRow(upgradePackage, metadata, true);
        Check(upgradeRow.EffectivePrimaryAssetType == RegistryPluginService.UpgradeRootType &&
            upgradeRow.EffectiveBundles.Single().Name == RegistryPluginService.MetadataBundle &&
            upgradeRow.EffectiveBundles.Single().Assets.SequenceEqual([setPath]) && RegistryPluginService.ValidateRows([upgradeRow]).Count == 0,
            "custom upgrade roots register their native metadata loading bundle under the equipment discovery root");
        metadata.Data = [];
        Check(!CharacterRegistryBundleService.CreateUpgradeRow(upgradePackage, metadata, true).EffectiveBundles.Any(),
            "no-upgrades roots remain valid primary assets with no fabricated dependencies");
        metadata.Data = [new ArrayPropertyData(FName.FromString(asset, "UpgradeFunctionality")) { Value = [Soft("0", pawn)] }, Soft("UpgradeData", setPath)];
        var setRow = CharacterRegistryBundleService.CreateUpgradeRow(upgradePackage + "_Set", metadata, false);
        Check(setRow.EffectivePrimaryAssetType == RegistryPluginService.UpgradeSetType &&
            setRow.EffectiveBundles.Single(b => b.Name == RegistryPluginService.GameplayBundle).Assets.SequenceEqual([pawn]) &&
            setRow.EffectiveBundles.Single(b => b.Name == RegistryPluginService.MetadataBundle).Assets.SequenceEqual([setPath]) &&
            RegistryPluginService.ValidateRows([setRow]).Count == 0,
            "upgrade sets register functionality classes separately from native purchase metadata");
        metadata.Data = [Soft("Equipment", pawn), Soft("DeployableCharacterMetaData", native)];
        var deployable = CharacterRegistryBundleService.CreateEquipmentRow(eta[..eta.LastIndexOf('.')], metadata, true);
        Check(deployable.EffectiveAssetClass == RegistryPluginService.DeployableEquipmentTaggedAssetClass &&
            deployable.EffectiveBundles.Single().Assets.SequenceEqual([native, pawn]) && RegistryPluginService.ValidateRows([deployable]).Count == 0,
            "deployable equipment registers both controlled-pawn metadata and equipment definition using its native class");
        metadata.Data = [Soft("Equipment", pawn)];
        rejected = false;
        try { CharacterRegistryBundleService.CreateEquipmentRow(package, metadata, true); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "missing deployable metadata fails instead of silently producing an ordinary equipment row");
        Check(RegistryPluginService.ValidateRows([upgradeRow with { PackagePath = EquipmentUpgradeService.NativeRoot }]).Count > 0,
            "upgrade registration cannot overwrite the native upgrade root");
        return results;
    }
}
