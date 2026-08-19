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

            var asset = new UAsset(outputBasePath + ".uasset", EngineVersion.VER_UE5_6, LoadMappings(), CustomSerializationFlags.SkipPreloadDependencyLoading);
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

            if (!string.IsNullOrWhiteSpace(targetPawnTag) &&
                NativeAssetTextPatch.SetGameplayTag(asset, "PawnTag", targetPawnTag.Trim()))
            {
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

            asset.Write(outputBasePath + ".uasset");
            result.OutputUasset = outputBasePath + ".uasset";
            result.Status = "created";
            return result;
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
    /// gadget's upgrade set (removing it when the new gadget has no upgrades, or
    /// appending when the slot had none). Slots at or beyond the current count are
    /// appended. Requires .usmap mappings so the DataAsset properties deserialize.
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

                // EquipmentList: replace at slot, or append if slot is out of range.
                if (gadget.Slot >= 0 && gadget.Slot < equip.Count)
                {
                    equip[gadget.Slot] = MakeSoft(asset, equipmentList.Name, etaPkg, etaName);
                }
                else
                {
                    equip.Add(MakeSoft(asset, equipmentList.Name, etaPkg, etaName));
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
                        else
                        {
                            upgrades.Add(entry);
                        }
                    }
                    else if (gadget.Slot >= 0 && gadget.Slot < upgrades.Count)
                    {
                        // New gadget has no upgrade tree - drop the slot's old one.
                        upgrades.RemoveAt(gadget.Slot);
                    }
                }

                result.Applied.Add($"slot {gadget.Slot + 1} = {gadget.Name}");
            }

            equipmentList.Value = equip.ToArray();
            if (upgradeList is not null)
            {
                upgradeList.Value = upgrades.ToArray();
            }

            asset.Write(dcmdUassetPath);
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
