using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Clones a donor DCMD and repoints it at the generated character assets.</summary>
public sealed class DcmdGenService
{
    // The base DCMD to clone (unlocked-by-default Batman with icon + gadgets).
    private const string BaseDcmdPackage = "Characters/Minifig/Batman/DA_DCMD_Batman_TheBatman2025_Playable";
    private const string SrcPlayablePkg = "/Game/Characters/Minifig/Batman/BP_Batman_TheBatman2025_Playable";
    private const string SrcCutscenePkg = "/Game/Characters/Minifig/Batman/BP_Batman_TheBatman2025_Default_Cutscene";
    private const string SrcDcmdPkg = "/Game/Characters/Minifig/Batman/DA_DCMD_Batman_TheBatman2025_Playable";
    private const string SrcPawnTag = "Pawns.Playable.Batman.TheBatman2025";
    private const string SrcUimdPkg = "/Game/Characters/Minifig/Batman/DA_UIMD_Batman";

    public string ProjectRoot { get; }

    public DcmdGenService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public sealed class GenResult
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string OutputUasset { get; set; } = "";
        public List<string> Repointed { get; } = new();
    }

    /// <summary>Resolves the legacy default DCMD used by the command-line probe.</summary>
    public static string ResolveBaseDcmdPath()
    {
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        return Path.Combine(root, BaseDcmdPackage.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
    }

    /// <summary>
    /// Writes a repointed DCMD to <paramref name="outputBasePath"/> (filesystem path, no extension).
    /// </summary>
    public GenResult Generate(
        string outputBasePath,
        string dcmdPackagePath,
        string playablePackagePath,
        string cutscenePackagePath,
        string? uimdPackagePath = null,
        string? targetPawnTag = null,
        string? displayNameTableObjectPath = null,
        string? displayNameKey = null,
        string? progressTag = null,
        NativeMetadataDonorService.Donor? donor = null)
    {
        var result = new GenResult();
        try
        {
            var sourceUasset = donor?.DcmdUassetPath;
            if (donor is not null && (string.IsNullOrWhiteSpace(sourceUasset) || !File.Exists(sourceUasset)))
            {
                result.Status = "missing-donor";
                result.Error = $"Selected donor DCMD is not extracted: {sourceUasset}";
                return result;
            }
            if (donor is null)
            {
                sourceUasset = ResolveBaseDcmdPath();
            }
            if (!File.Exists(sourceUasset))
            {
                result.Status = "missing-base";
                result.Error = $"Donor DCMD not found: {sourceUasset}";
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputBasePath)!);
            var baseNoExt = Path.Combine(
                Path.GetDirectoryName(sourceUasset)!,
                Path.GetFileNameWithoutExtension(sourceUasset));
            CopyIfExists(baseNoExt + ".uasset", outputBasePath + ".uasset");
            CopyIfExists(baseNoExt + ".uexp", outputBasePath + ".uexp");

            var mappings = LoadMappings();
            var asset = new UAsset(outputBasePath + ".uasset", EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            dcmdPackagePath = UnrealPathUtil.NormalizePackagePath(dcmdPackagePath);
            playablePackagePath = UnrealPathUtil.NormalizePackagePath(playablePackagePath);
            cutscenePackagePath = UnrealPathUtil.NormalizePackagePath(cutscenePackagePath);
            uimdPackagePath = UnrealPathUtil.NormalizePackagePath(uimdPackagePath);

            asset.FolderName = new FString(dcmdPackagePath);

            var playableStem = UnrealPathUtil.AssetName(playablePackagePath);
            var cutsceneStem = UnrealPathUtil.AssetName(cutscenePackagePath);
            var dcmdStem = UnrealPathUtil.AssetName(dcmdPackagePath);
            var sourcePlayablePackage = !string.IsNullOrWhiteSpace(donor?.PlayablePackagePath)
                ? UnrealPathUtil.NormalizePackagePath(donor.PlayablePackagePath)
                : SrcPlayablePkg;
            var sourceCutscenePackage = !string.IsNullOrWhiteSpace(donor?.CutscenePackagePath)
                ? UnrealPathUtil.NormalizePackagePath(donor.CutscenePackagePath)
                : SrcCutscenePkg;
            var sourceDcmdPackage = !string.IsNullOrWhiteSpace(donor?.DcmdPackagePath)
                ? UnrealPathUtil.NormalizePackagePath(donor.DcmdPackagePath)
                : SrcDcmdPkg;
            var sourceUimdPackage = !string.IsNullOrWhiteSpace(donor?.UimdPackagePath)
                ? UnrealPathUtil.NormalizePackagePath(donor.UimdPackagePath)
                : SrcUimdPkg;
            var sourcePlayableStem = UnrealPathUtil.AssetName(sourcePlayablePackage);
            var sourceCutsceneStem = UnrealPathUtil.AssetName(sourceCutscenePackage);
            var sourceDcmdStem = UnrealPathUtil.AssetName(sourceDcmdPackage);
            var sourceUimdStem = UnrealPathUtil.AssetName(sourceUimdPackage);

            var replacements = new List<KeyValuePair<string, string>>
            {
                new(sourcePlayablePackage, playablePackagePath),
                new(sourceCutscenePackage, cutscenePackagePath),
                new(sourceDcmdPackage, dcmdPackagePath),
                new(sourcePlayableStem + "_C", playableStem + "_C"),
                new(sourceCutsceneStem + "_C", cutsceneStem + "_C"),
                new(sourceDcmdStem, dcmdStem),
                new(sourcePlayableStem, playableStem),
                new(sourceCutsceneStem, cutsceneStem),
            };

            if (!string.IsNullOrWhiteSpace(targetPawnTag))
            {
                var sourcePawnTag = string.IsNullOrWhiteSpace(donor?.PawnTag) ? SrcPawnTag : donor.PawnTag;
                replacements.Add(new(sourcePawnTag, targetPawnTag!));
            }

            if (!string.IsNullOrWhiteSpace(uimdPackagePath))
            {
                replacements.Add(new(sourceUimdPackage, uimdPackagePath!));
                replacements.Add(new(sourceUimdStem, UnrealPathUtil.AssetName(uimdPackagePath!)));
            }
            // Exact-match per name-map entry: these are all standalone package/asset
            // FNames, so whole-entry replacement avoids substring overlap (e.g. the
            // bare "DA_UIMD_Batman" re-matching inside a repointed UIMD path).
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in replacements)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value) && pair.Key != pair.Value)
                {
                    map[pair.Key] = pair.Value;
                }
            }

            var nameMap = asset.GetNameMapIndexList();
            for (var i = 0; i < nameMap.Count; i++)
            {
                var original = nameMap[i].ToString();
                if (map.TryGetValue(original, out var patched))
                {
                    asset.SetNameReference(i, new FString(patched));
                    result.Repointed.Add($"{original} -> {patched}");
                }
            }

            UnrealPathUtil.RepairSplitPathNameMapEntries(
                asset,
                new[] { dcmdPackagePath, playablePackagePath, cutscenePackagePath, uimdPackagePath ?? "" },
                result.Repointed);

            // These three soft references define which actors the game can instantiate when the
            // character is not already resident. Do not rely only on source-name replacement:
            // shipped DCMDs such as RobinDickGrayson's point at cutscene assets with unrelated
            // stems, and a stale CinematicsActor makes gameplay work while cold cutscenes silently
            // use the base/default suit.
            RepointActor("Pawn", playablePackagePath);
            RepointActor("MenuActor", playablePackagePath);
            RepointActor("CinematicsActor", cutscenePackagePath);

            if (!string.IsNullOrWhiteSpace(targetPawnTag))
            {
                if (!NativeAssetTextPatch.SetGameplayTag(asset, "PawnTag", targetPawnTag.Trim()))
                {
                    throw new InvalidDataException(
                        "The donor DCMD has no writable PawnTag. Batcomputer refused to emit mismatched native identity metadata.");
                }
                result.Repointed.Add($"PawnTag -> {targetPawnTag.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(progressTag) &&
                NativeAssetTextPatch.SetGameplayTag(asset, "ProgressTag", progressTag.Trim()))
            {
                result.Repointed.Add($"ProgressTag -> {progressTag.Trim()}");
            }

            // Native-suit menu name: repoint DisplayName to the mod StringTable + key
            // (property-level; §7.2 gap - DcmdGenService previously left DisplayName
            // pointing at the donor's ST_TagNames key).
            if (!string.IsNullOrWhiteSpace(displayNameTableObjectPath) &&
                !string.IsNullOrWhiteSpace(displayNameKey) &&
                NativeAssetTextPatch.SetStringTableText(asset, "DisplayName", displayNameTableObjectPath!, displayNameKey!))
            {
                result.Repointed.Add($"DisplayName -> {displayNameTableObjectPath}:{displayNameKey}");
            }

            RequirePersistedIdentityLinks(
                asset,
                playablePackagePath,
                cutscenePackagePath,
                uimdPackagePath,
                targetPawnTag);
            asset.Write(outputBasePath + ".uasset");
            var persisted = new UAsset(
                outputBasePath + ".uasset",
                EngineVersion.VER_UE5_6,
                mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            RequirePersistedIdentityLinks(
                persisted,
                playablePackagePath,
                cutscenePackagePath,
                uimdPackagePath,
                targetPawnTag);
            result.OutputUasset = outputBasePath + ".uasset";
            result.Status = "created";
            return result;

            void RepointActor(string propertyName, string packagePath)
            {
                var className = UnrealPathUtil.AssetName(packagePath) + "_C";
                if (NativeAssetTextPatch.SetOrAddSoftObject(
                        asset,
                        propertyName,
                        packagePath,
                        className,
                        "Pawn",
                        "MenuActor",
                        "CinematicsActor"))
                {
                    var written = NativeAssetTextPatch.GetSoftReference(asset, propertyName);
                    if (written is null ||
                        !written.Value.PackageName.Equals(packagePath, StringComparison.OrdinalIgnoreCase) ||
                        !written.Value.AssetName.Equals(className, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Generated DCMD did not retain {propertyName} -> {packagePath}.{className}.");
                    }
                    result.Repointed.Add($"{propertyName} -> {packagePath}.{className}");
                    return;
                }

                throw new InvalidDataException(
                    $"The donor DCMD has no writable {propertyName} soft-class field. " +
                    "Batcomputer refused to emit metadata that could fall back to the base suit.");
            }

        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>A gadget to place into a specific 0-based equipment slot.</summary>
    public sealed record EquipmentSlotRef(int Slot, string Name, string EtaPackage, string? UpgradePackage = null);

    public sealed class AddEquipmentResult
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public List<string> Applied { get; } = new();
        public List<string> Skipped { get; } = new();
    }

    /// <summary>
    /// Replaces gadgets at specific 0-based slots in a generated DCMD's
    /// EquipmentList, and swaps the matching UpgradeDataAssets entry to that
    /// gadget's upgrade set. UpgradeDataAssets stays exactly parallel with EquipmentList: a gadget
    /// without upgrades writes a null soft reference rather than removing an element and shifting
    /// every later slot. Only the exact next slot may be appended; sparse writes are rejected.
    /// Requires .usmap mappings so the DataAsset properties deserialize.
    /// </summary>
    public AddEquipmentResult ReplaceEquipment(string dcmdUassetPath, IReadOnlyList<EquipmentSlotRef> gadgets)
    {
        var result = new AddEquipmentResult();
        try
        {
            var mappings = LoadMappings();
            if (mappings is null)
            {
                result.Status = "no-mappings";
                result.Error = "A .usmap mappings file is required to edit EquipmentList. Configure one in settings.";
                return result;
            }
            if (!File.Exists(dcmdUassetPath))
            {
                result.Status = "missing";
                result.Error = $"DCMD not found: {dcmdUassetPath}";
                return result;
            }

            var asset = new UAsset(dcmdUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
            var export = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "EquipmentList"));
            if (export is null)
            {
                result.Status = "no-equipmentlist";
                result.Error = "DCMD has no EquipmentList property.";
                return result;
            }

            var equipmentList = export.Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "EquipmentList");
            var upgradeList = export.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "UpgradeDataAssets");

            var equip = equipmentList.Value.ToList();
            var upgrades = upgradeList?.Value.ToList() ?? new List<PropertyData>();

            foreach (var gadget in gadgets)
            {
                var etaPkg = UnrealPathUtil.NormalizePackagePath(gadget.EtaPackage);
                var etaName = UnrealPathUtil.AssetName(etaPkg);

                // EquipmentList: replace at slot, or append the exact next slot. Never collapse a
                // sparse runtime slot request into a different array index.
                if (gadget.Slot >= 0 && gadget.Slot < equip.Count)
                {
                    equip[gadget.Slot] = MakeSoft(asset, equipmentList.Name, etaPkg, etaName);
                }
                else if (gadget.Slot == equip.Count)
                {
                    equip.Add(MakeSoft(asset, equipmentList.Name, etaPkg, etaName));
                }
                else
                {
                    result.Status = "invalid-slot";
                    result.Error = $"Equipment slot {gadget.Slot + 1} is sparse or negative for a {equip.Count}-slot DCMD list.";
                    return result;
                }

                // UpgradeDataAssets: keep it parallel to the equipment slot.
                if (upgradeList is not null)
                {
                    var hasUpgrade = !string.IsNullOrWhiteSpace(gadget.UpgradePackage);
                    if (hasUpgrade)
                    {
                        var upPkg = UnrealPathUtil.NormalizePackagePath(gadget.UpgradePackage!);
                        var upName = UnrealPathUtil.AssetName(upPkg);
                        var entry = MakeSoft(asset, upgradeList.Name, upPkg, upName);
                        if (gadget.Slot >= 0 && gadget.Slot < upgrades.Count)
                        {
                            upgrades[gadget.Slot] = entry;
                        }
                        else if (gadget.Slot == upgrades.Count)
                        {
                            upgrades.Add(entry);
                        }
                        else
                        {
                            while (upgrades.Count < gadget.Slot)
                            {
                                upgrades.Add(MakeNullSoft(asset, upgradeList.Name));
                            }
                            upgrades.Add(entry);
                        }
                    }
                    else if (gadget.Slot >= 0 && gadget.Slot < upgrades.Count)
                    {
                        upgrades[gadget.Slot] = MakeNullSoft(asset, upgradeList.Name);
                    }
                    else
                    {
                        while (upgrades.Count <= gadget.Slot)
                        {
                            upgrades.Add(MakeNullSoft(asset, upgradeList.Name));
                        }
                    }
                }

                result.Applied.Add($"slot {gadget.Slot + 1} = {gadget.Name}");
            }

            equipmentList.Value = equip.ToArray();
            if (upgradeList is not null)
            {
                while (upgrades.Count < equip.Count)
                {
                    upgrades.Add(MakeNullSoft(asset, upgradeList.Name));
                }
                if (upgrades.Count > equip.Count)
                {
                    upgrades.RemoveRange(equip.Count, upgrades.Count - equip.Count);
                }
                upgradeList.Value = upgrades.ToArray();
            }

            asset.Write(dcmdUassetPath);
            var verify = new UAsset(
                dcmdUassetPath,
                EngineVersion.VER_UE5_6,
                mappings,
                CustomSerializationFlags.None);
            var verifyExport = verify.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.Data.Any(p => p.Name.ToString() == "EquipmentList"));
            var verifyEquipment = verifyExport?.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name.ToString() == "EquipmentList");
            var verifyUpgrades = verifyExport?.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name.ToString() == "UpgradeDataAssets");
            if (verifyEquipment is null ||
                !SoftPaths(verifyEquipment.Value).SequenceEqual(SoftPaths(equip), StringComparer.OrdinalIgnoreCase) ||
                (upgradeList is not null &&
                 (verifyUpgrades is null ||
                  !SoftPaths(verifyUpgrades.Value).SequenceEqual(SoftPaths(upgrades), StringComparer.OrdinalIgnoreCase))))
            {
                result.Status = "verification-failed";
                result.Error = "The staged DCMD did not reload with the exact requested equipment and parallel upgrade slot arrays.";
                return result;
            }
            result.Status = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    /// <summary>Reads the current EquipmentList slots (gadget names) from a DCMD.</summary>
    public List<string> ReadEquipmentSlots(string dcmdUassetPath)
    {
        var slots = new List<string>();
        var mappings = LoadMappings();
        if (mappings is null || !File.Exists(dcmdUassetPath))
        {
            return slots;
        }

        var asset = new UAsset(dcmdUassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.None);
        var list = asset.Exports.OfType<NormalExport>()
            .SelectMany(e => e.Data)
            .OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name.ToString() == "EquipmentList");
        if (list is null)
        {
            return slots;
        }

        foreach (var el in list.Value)
        {
            if (el is SoftObjectPropertyData sop)
            {
                slots.Add(sop.Value.AssetPath.AssetName.ToString());
            }
        }
        return slots;
    }

    private static SoftObjectPropertyData MakeSoft(UAsset asset, FName listName, string packagePath, string assetName) => new(listName)
    {
        Value = new FSoftObjectPath(
            new FTopLevelAssetPath(FName.FromString(asset, packagePath), FName.FromString(asset, assetName)),
            new FString(string.Empty)),
    };

    private static SoftObjectPropertyData MakeNullSoft(UAsset asset, FName listName) => new(listName)
    {
        Value = new FSoftObjectPath(
            new FTopLevelAssetPath(FName.FromString(asset, "None"), FName.FromString(asset, "None")),
            new FString(string.Empty)),
    };

    private static IEnumerable<string> SoftPaths(IEnumerable<PropertyData> entries) =>
        entries.Select(entry => entry is SoftObjectPropertyData soft
            ? NormalizeSoftPath(soft.Value.AssetPath.PackageName.ToString(), soft.Value.AssetPath.AssetName.ToString())
            : "<invalid>");

    private static string NormalizeSoftPath(string package, string asset)
    {
        if (string.IsNullOrWhiteSpace(package) || package.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        return UnrealPathUtil.NormalizePackagePath(package) + "." + asset;
    }

    /// <summary>
    /// Fails unless a freshly reloaded DCMD retains the complete native identity graph used by
    /// gameplay, menus, and cold cinematic spawning.
    /// </summary>
    internal static void RequirePersistedIdentityLinks(
        UAsset persistedAsset,
        string playablePackagePath,
        string cutscenePackagePath,
        string? uimdPackagePath,
        string? pawnTag)
    {
        VerifySoft("Pawn", playablePackagePath, UnrealPathUtil.AssetName(playablePackagePath) + "_C");
        VerifySoft("MenuActor", playablePackagePath, UnrealPathUtil.AssetName(playablePackagePath) + "_C");
        VerifySoft("CinematicsActor", cutscenePackagePath, UnrealPathUtil.AssetName(cutscenePackagePath) + "_C");
        if (!string.IsNullOrWhiteSpace(uimdPackagePath))
        {
            VerifyObject("UIMetaData", uimdPackagePath!, UnrealPathUtil.AssetName(uimdPackagePath!));
        }
        if (!string.IsNullOrWhiteSpace(pawnTag) &&
            !string.Equals(
                NativeAssetTextPatch.GetGameplayTag(persistedAsset, "PawnTag"),
                pawnTag.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Generated DCMD did not reload from disk with PawnTag '{pawnTag.Trim()}'.");
        }

        void VerifySoft(string propertyName, string packagePath, string assetName)
        {
            var written = NativeAssetTextPatch.GetSoftReference(persistedAsset, propertyName);
            if (written is null ||
                !UnrealPathUtil.NormalizePackagePath(written.Value.PackageName)
                    .Equals(UnrealPathUtil.NormalizePackagePath(packagePath), StringComparison.OrdinalIgnoreCase) ||
                !written.Value.AssetName.Equals(assetName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Generated DCMD did not reload from disk with {propertyName} -> {packagePath}.{assetName}.");
            }
        }

        void VerifyObject(string propertyName, string packagePath, string assetName)
        {
            var written = NativeAssetTextPatch.GetObjectReference(persistedAsset, propertyName);
            if (written is null ||
                !UnrealPathUtil.NormalizePackagePath(written.Value.PackageName)
                    .Equals(UnrealPathUtil.NormalizePackagePath(packagePath), StringComparison.OrdinalIgnoreCase) ||
                !written.Value.AssetName.Equals(assetName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Generated DCMD did not reload from disk with {propertyName} -> {packagePath}.{assetName}.");
            }
        }
    }

    private Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? MappingsCache.Load(configured) : null;
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}
