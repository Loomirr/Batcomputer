using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Clones a Batman DinnerCharacterMetaData (DCMD) - which carries the Batman menu
/// icon (DA_UIMD_Batman) and equipment (DA_ETA_Batarang / DA_ETA_Batclaw) - and
/// repoints its Pawn / MenuActor / CinematicsActor class paths to the suit's
/// generated playable + cutscene classes. The soft-object class paths are stored
/// as name-map FNames, so a targeted name-map string replacement repoints them
/// without touching the icon/equipment references. The result is a self-contained
/// DCMD written next to the generated BPs so it packages into the trio.
/// </summary>
public sealed class DcmdGenService
{
    // The base DCMD to clone (unlocked-by-default Batman with icon + gadgets).
    private const string BaseDcmdPackage = "Characters/Minifig/Batman/DA_DCMD_Batman_TheBatman2025_Playable";
    private const string SrcPlayablePkg = "/Game/Characters/Minifig/Batman/BP_Batman_TheBatman2025_Playable";
    private const string SrcCutscenePkg = "/Game/Characters/Minifig/Batman/BP_Batman_TheBatman2025_Default_Cutscene";
    private const string SrcDcmdPkg = "/Game/Characters/Minifig/Batman/DA_DCMD_Batman_TheBatman2025_Playable";
    private const string SrcPlayableAsset = "BP_Batman_TheBatman2025_Playable";
    private const string SrcCutsceneAsset = "BP_Batman_TheBatman2025_Default_Cutscene";
    private const string SrcDcmdAsset = "DA_DCMD_Batman_TheBatman2025_Playable";
    private const string SrcPawnTag = "Pawns.Playable.Batman.TheBatman2025";
    private const string SrcUimdPkg = "/Game/Characters/Minifig/Batman/DA_UIMD_Batman";
    private const string SrcUimdAsset = "DA_UIMD_Batman";

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

    /// <summary>Resolves the base Batman DCMD .uasset under the extracted content root.</summary>
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
        string? displayNameKey = null)
    {
        var result = new GenResult();
        try
        {
            var baseUasset = ResolveBaseDcmdPath();
            if (!File.Exists(baseUasset))
            {
                result.Status = "missing-base";
                result.Error = $"Base Batman DCMD not found: {baseUasset}";
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputBasePath)!);
            var baseNoExt = Path.Combine(
                Path.GetDirectoryName(baseUasset)!,
                Path.GetFileNameWithoutExtension(baseUasset));
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

            // Longest keys first so package paths and *_C names win over bare names.
            var replacements = new List<KeyValuePair<string, string>>
            {
                new(SrcPlayablePkg, playablePackagePath),
                new(SrcCutscenePkg, cutscenePackagePath),
                new(SrcDcmdPkg, dcmdPackagePath),
                new(SrcPlayableAsset + "_C", playableStem + "_C"),
                new(SrcCutsceneAsset + "_C", cutsceneStem + "_C"),
                new(SrcDcmdAsset, dcmdStem),
                new(SrcPlayableAsset, playableStem),
                new(SrcCutsceneAsset, cutsceneStem),
            };

            if (!string.IsNullOrWhiteSpace(targetPawnTag))
            {
                replacements.Add(new(SrcPawnTag, targetPawnTag!));
            }

            if (!string.IsNullOrWhiteSpace(uimdPackagePath))
            {
                replacements.Add(new(SrcUimdPkg, uimdPackagePath!));
                replacements.Add(new(SrcUimdAsset, UnrealPathUtil.AssetName(uimdPackagePath!)));
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
