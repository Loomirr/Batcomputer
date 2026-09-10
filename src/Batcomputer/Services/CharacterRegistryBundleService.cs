using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Recreates the native loading bundles from the final staged metadata, not the donor.</summary>
public static class CharacterRegistryBundleService
{
    public static IReadOnlyList<RegistryPluginService.RegistryRow> CreateRows(
        string stageContentRoot, IEnumerable<string> characterPackages, Usmap mappings)
    {
        var rows = new List<RegistryPluginService.RegistryRow>();
        var equipment = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upgrades = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddUpgrade(string package)
        {
            if (!IsModOwned(package) || !upgrades.Add(package)) return;
            if (upgrades.Count > 256) throw new InvalidDataException("Custom upgrade registry graph exceeds the supported bound.");
            var asset = LoadStaged(stageContentRoot, package, mappings);
            var export = asset.Exports.OfType<NormalExport>().SingleOrDefault(candidate =>
                candidate.GetExportClassType()?.ToString() is "UpgradeFunctionalityDataAsset" or "UpgradeFunctionalitySet")
                ?? throw new InvalidDataException($"'{package}' has no supported upgrade metadata export.");
            var isRoot = export.GetExportClassType()!.ToString() == "UpgradeFunctionalityDataAsset";
            var row = CreateUpgradeRow(package, export, isRoot);
            RequireOwnedRow(row); rows.Add(row);
            if (isRoot) foreach (var child in ReadArray(export, "UpgradeSets")) AddUpgrade(child[..child.LastIndexOf('.')]);
        }
        foreach (var package in characterPackages)
        {
            RequireOwnedRow(new RegistryPluginService.RegistryRow(package));
            var character = LoadStaged(stageContentRoot, package, mappings);
            rows.Add(CreateCharacterRow(package, character));
            var metadata = CharacterExport(character);
            foreach (var path in ReadArray(metadata, "UpgradeDataAssets")) AddUpgrade(path[..path.LastIndexOf('.')]);
            foreach (var path in ReadArray(metadata, "EquipmentList"))
            {
                var etaPackage = path[..path.LastIndexOf('.')];
                if (!IsModOwned(etaPackage) || !equipment.Add(etaPackage)) continue;
                var eta = LoadStaged(stageContentRoot, etaPackage, mappings);
                var export = eta.Exports.OfType<NormalExport>()
                    .SingleOrDefault(candidate => candidate.GetExportClassType()?.ToString() is "TtEquipmentTaggedAsset" or "TtDeployableEquipmentTaggedAsset")
                    ?? throw new InvalidDataException($"'{etaPackage}' has no supported equipment tagged-asset export.");
                var etaRow = CreateEquipmentRow(etaPackage, export, export.GetExportClassType()!.ToString() == "TtDeployableEquipmentTaggedAsset");
                RequireOwnedRow(etaRow); rows.Add(etaRow);
            }
        }

        var errors = RegistryPluginService.ValidateRows(rows);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        foreach (var path in rows.SelectMany(row => row.EffectiveBundles).SelectMany(bundle => bundle.Assets)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var package = path[..path.LastIndexOf('.')];
            if (IsModOwned(package)) RequireStagedFile(stageContentRoot, package);
        }
        return rows;
    }

    internal static RegistryPluginService.RegistryRow CreateCharacterRow(string package, UAsset character)
        => CreateCharacterRowFromMetadata(package, CharacterExport(character));

    internal static RegistryPluginService.RegistryRow CreateEquipmentRow(string package, NormalExport metadata, bool deployable)
    {
        var dependencies = new List<string>();
        if (deployable) dependencies.Add(ReadReference(metadata, "DeployableCharacterMetaData", required: true)!);
        dependencies.Add(ReadReference(metadata, "Equipment", required: true)!);
        return new(package, RegistryPluginService.EquipmentPrimaryAssetType,
            deployable ? RegistryPluginService.DeployableEquipmentTaggedAssetClass : RegistryPluginService.EquipmentTaggedAssetClass,
            GameplayBundleAssets: dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static RegistryPluginService.RegistryRow CreateUpgradeRow(string package, NormalExport metadata, bool isRoot)
    {
        var bundles = new List<RegistryPluginService.AssetBundle>();
        if (isRoot)
        {
            var sets = ReadArray(metadata, "UpgradeSets").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (sets.Length > 0) bundles.Add(new(RegistryPluginService.MetadataBundle, sets));
        }
        else
        {
            var functions = ReadArray(metadata, "UpgradeFunctionality").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (functions.Length > 0) bundles.Add(new(RegistryPluginService.GameplayBundle, functions));
            bundles.Add(new(RegistryPluginService.MetadataBundle, [ReadReference(metadata, "UpgradeData", required: true)!]));
        }
        return new(package, isRoot ? RegistryPluginService.UpgradeRootType : RegistryPluginService.UpgradeSetType,
            isRoot ? RegistryPluginService.UpgradeRootClass : RegistryPluginService.UpgradeSetClass, Bundles: bundles);
    }

    internal static RegistryPluginService.RegistryRow CreateCharacterRowFromMetadata(string package, NormalExport metadata)
    {
        var bundles = new List<RegistryPluginService.AssetBundle>();
        var pawn = ReadReference(metadata, "Pawn", required: true)!;
        bundles.Add(new(RegistryPluginService.GameplayBundle, [pawn]));
        var cinematic = ReadReference(metadata, "CinematicsActor", required: false);
        if (cinematic is not null) bundles.Add(new(RegistryPluginService.CinematicBundle, [cinematic]));
        var dependencies = ReadArray(metadata, "EquipmentList").Concat(ReadArray(metadata, "UpgradeDataAssets"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (dependencies.Length > 0) bundles.Add(new(RegistryPluginService.MetadataBundle, dependencies));
        return new RegistryPluginService.RegistryRow(package, Bundles: bundles);
    }

    private static NormalExport CharacterExport(UAsset character) => character.Exports.OfType<NormalExport>()
        .SingleOrDefault(candidate => candidate.GetExportClassType()?.ToString() == "DinnerCharacterMetaData")
        ?? throw new InvalidDataException("Staged character metadata has no DinnerCharacterMetaData export.");

    private static string? ReadReference(NormalExport export, string propertyName, bool required)
    {
        var property = export.Data.SingleOrDefault(candidate => candidate.Name.ToString() == propertyName);
        if (property is null && !required) return null;
        if (property is not SoftObjectPropertyData soft)
            throw new InvalidDataException($"Metadata '{propertyName}' is missing or is not a soft reference.");
        var path = ReadSoftPath(soft);
        if (path is null && required) throw new InvalidDataException($"Metadata '{propertyName}' is empty.");
        return path;
    }

    private static IEnumerable<string> ReadArray(NormalExport export, string propertyName)
    {
        var property = export.Data.SingleOrDefault(candidate => candidate.Name.ToString() == propertyName);
        if (property is null) yield break; // An omitted unversioned array uses the empty native default.
        if (property is not ArrayPropertyData array)
            throw new InvalidDataException($"Metadata '{propertyName}' is not an array.");
        foreach (var item in array.Value)
        {
            if (item is not SoftObjectPropertyData soft)
                throw new InvalidDataException($"Metadata '{propertyName}' contains a non-soft reference.");
            var path = ReadSoftPath(soft);
            if (path is not null) yield return path;
        }
    }

    private static string? ReadSoftPath(SoftObjectPropertyData soft)
    {
        var package = soft.Value.AssetPath.PackageName.ToString();
        var asset = soft.Value.AssetPath.AssetName.ToString();
        if ((string.IsNullOrEmpty(package) || package == "None") &&
            (string.IsNullOrEmpty(asset) || asset == "None")) return null;
        var path = package + "." + asset;
        if (!string.IsNullOrEmpty(soft.Value.SubPathString?.ToString()) ||
            !RegistryPluginService.IsBundleObjectPath(path))
            throw new InvalidDataException($"Invalid metadata bundle reference: '{path}'.");
        return path;
    }

    private static bool IsModOwned(string package) =>
        package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase) ||
        package.StartsWith("/Game/Characters/Equipment/Mods/", StringComparison.OrdinalIgnoreCase);

    private static void RequireOwnedRow(RegistryPluginService.RegistryRow row)
    {
        var errors = RegistryPluginService.ValidateRows([row]);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private static string RequireStagedFile(string root, string package)
    {
        var file = ExtractedPackagePathService.ResolvePackageUasset(root, package);
        if (file is null || !File.Exists(file))
            throw new FileNotFoundException($"Registry loading bundle references a mod asset missing from the stage: '{package}'.", file);
        return file;
    }

    private static UAsset LoadStaged(string root, string package, Usmap mappings)
    {
        var path = RequireStagedFile(root, package);
        // UAssetAPI's path reader can acquire write access. Parse a private in-memory
        // copy instead; bundle discovery must never lock or rewrite staged packages.
        using var bytes = new MemoryStream();
        using (var source = File.OpenRead(path)) source.CopyTo(bytes);
        var exports = Path.ChangeExtension(path, ".uexp");
        var split = File.Exists(exports);
        if (split)
        {
            using var source = File.OpenRead(exports);
            source.CopyTo(bytes);
        }
        bytes.Position = 0;
        using var reader = new AssetBinaryReader(bytes);
        return new UAsset(reader, EngineVersion.VER_UE5_6, mappings, split,
            CustomSerializationFlags.SkipPreloadDependencyLoading);
    }
}
