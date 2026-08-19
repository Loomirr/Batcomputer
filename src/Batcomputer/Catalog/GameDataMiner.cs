using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Mines the shippable <see cref="GameDataDb"/> from an extracted game content
/// dump. Runs once (dev-side) whenever the game patches; the resulting JSON is
/// shipped in the tool so end users never extract anything for compatibility
/// features. Only reads import tables (no .usmap required) and enumerates files
/// on disk - it never copies asset bytes.
/// </summary>
public sealed class GameDataMiner
{
    private readonly string _contentRoot;
    private readonly Usmap? _mappings;

    public GameDataMiner(string extractedContentRoot)
    {
        _contentRoot = NormalizeRoot(extractedContentRoot);
        var usmap = AppSettings.Current.EffectiveUsmapPath();
        _mappings = !string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap) ? MappingsCache.Load(usmap) : null;
    }

    public sealed class MineResult
    {
        public GameDataDb Db { get; } = new();
        public int AssetsScanned { get; set; }
        public int Errors { get; set; }
        public List<string> Warnings { get; } = new();
    }

    public MineResult Mine(string gameBuild, bool includeFullCatalog = false)
    {
        var result = new MineResult();
        result.Db.GameBuild = gameBuild;
        result.Db.GeneratedUtc = DateTime.UtcNow.ToString("o");

        var minifigRoot = Path.Combine(_contentRoot, "Characters", "Minifig");
        var equipmentRoot = Path.Combine(_contentRoot, "Characters", "Equipment");
        var equipLayerRoot = Path.Combine(_contentRoot, "Animation", "LayerAnimSets", "Equipment");
        var equipMontageRoot = Path.Combine(_contentRoot, "Animation", "MontageAnimSets", "Equipment");
        var lamAbilityRoot = Path.Combine(_contentRoot, "Characters", "Abilities", "LAMManagedAbilities");
        var lamItemAbilities = Directory.Exists(lamAbilityRoot)
            ? Directory.EnumerateFiles(lamAbilityRoot, "GA_Item_*.uasset").Select(f => Path.GetFileNameWithoutExtension(f)).ToList()
            : new List<string>();

        // 1) Equipment layer sets (LAS_Equipment_*) - pure disk enumeration.
        if (Directory.Exists(equipLayerRoot))
        {
            foreach (var file in Directory.EnumerateFiles(equipLayerRoot, "LAS_Equipment_*.uasset"))
            {
                result.Db.EquipmentLayerSets.Add(new GameDataLayerSet
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Package = ToGamePath(file),
                });
            }
        }
        else
        {
            result.Warnings.Add($"Equipment layer-set folder not found: {equipLayerRoot}");
        }

        // Equipment montage sets (MAS_Equipment_*) - the montage half of a gadget's anims.
        var equipMontageSets = new List<GameDataLayerSet>();
        if (Directory.Exists(equipMontageRoot))
        {
            foreach (var file in Directory.EnumerateFiles(equipMontageRoot, "MAS_Equipment_*.uasset"))
            {
                equipMontageSets.Add(new GameDataLayerSet
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Package = ToGamePath(file),
                });
            }
        }

        // 2) Equipment catalog - one entry per gadget folder that has a DA_ETA_*.
        var equipmentByName = new Dictionary<string, GameDataEquipment>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(equipmentRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(equipmentRoot))
            {
                var eta = Directory.EnumerateFiles(dir, "DA_ETA_*.uasset").FirstOrDefault();
                if (eta is null)
                {
                    continue;
                }

                var gadget = Path.GetFileName(dir);
                // Pick the CANONICAL loadout ED, not just the first BP_*_ED alphabetically.
                // Folders often hold variants (…Rapid_ED, …_P2_ED) that sort before the
                // base ED (e.g. BP_RasThrowingStarsRapid_ED < BP_RasThrowingStars_ED because
                // 'R' < '_'). The base-game loadout + the DA_ETA both use "BP_<Gadget>_ED",
                // so prefer that exact name; then the shortest ED name; else the first.
                var eds = Directory.EnumerateFiles(dir, "BP_*_ED.uasset").ToList();
                var preferredEdName = $"BP_{gadget}_ED";
                var ed = eds.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                             .Equals(preferredEdName, StringComparison.OrdinalIgnoreCase))
                         ?? eds.OrderBy(f => Path.GetFileNameWithoutExtension(f).Length)
                             .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault();
                var upgradesDir = Path.Combine(dir, "Upgrades");
                var upgrade = Directory.Exists(upgradesDir)
                    ? Directory.EnumerateFiles(upgradesDir, "DA_UF_*.uasset").FirstOrDefault()
                    : null;
                // Abilities to grant into the suit's ability set so the gadget actually functions.
                // Most hand/thrown gadgets (whip, batarang, ninjastar) use LAM-managed
                // GA_Item_<Gadget>* packages. WEAPON-style gadgets (FreezeGun, MachineGun,
                // RocketLauncher, Pistol, …) instead keep their gameplay abilities in the gadget's
                // OWN Abilities/ subfolder (GA_Aim*/GA_Fire*/GA_*Beam). Without granting those the
                // weapon equips but does nothing. So: prefer the LAM GA_Item_ set; if none, fall
                // back to the gadget folder's own GA_*.
                var visualAbilities = lamItemAbilities
                    .Where(a => a.StartsWith($"GA_Item_{gadget}", StringComparison.OrdinalIgnoreCase))
                    .Select(a => $"/Game/Characters/Abilities/LAMManagedAbilities/{a}")
                    .ToList();
                if (visualAbilities.Count == 0)
                {
                    var abilitiesDir = Path.Combine(dir, "Abilities");
                    if (Directory.Exists(abilitiesDir))
                    {
                        visualAbilities = Directory.EnumerateFiles(abilitiesDir, "GA_*.uasset")
                            .Select(ToGamePath)
                            .ToList();
                    }
                }
                var entry = new GameDataEquipment
                {
                    Name = gadget,
                    EtaPackage = ToGamePath(eta),
                    EdPackage = ed is null ? "" : ToGamePath(ed),
                    UpgradePackage = upgrade is null ? "" : ToGamePath(upgrade),
                    LayerAnimSet = MatchLayerSet(gadget, result.Db.EquipmentLayerSets),
                    MontageAnimSet = MatchAnimSetForGadget(gadget, equipMontageSets, "MAS_Equipment_"),
                    VisualAbilities = visualAbilities,
                };
                equipmentByName[gadget] = entry;
                result.Db.Equipment.Add(entry);
            }
        }
        else
        {
            result.Warnings.Add($"Equipment folder not found: {equipmentRoot}");
        }

        // Index ETA object-name -> gadget so DCMD imports can be resolved to gadgets.
        var etaNameToGadget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in result.Db.Equipment)
        {
            var etaLeaf = LeafName(e.EtaPackage);
            if (etaLeaf.Length > 0)
            {
                etaNameToGadget[etaLeaf] = e.Name;
            }
        }

        // 3) Families - one per Minifig/<Family> folder that has an archetype AND at least
        // one playable class. An archetype alone is NOT enough: bosses/NPCs own archetypes
        // too (Cluemaster, RasAlGhul, RedHoodOne, Firefly all have one and zero playables),
        // and admitting them offered bases that can never be worn. Of 95 character folders
        // only 11 have any playable class; the 7 in the roster all have >= 10.
        if (Directory.Exists(minifigRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(minifigRoot))
            {
                var familyName = Path.GetFileName(dir);
                var archetype = Directory
                    .EnumerateFiles(dir, "BP_*Archetype*.uasset")
                    .FirstOrDefault();
                if (archetype is null)
                {
                    continue;
                }

                var playableCount = Directory
                    .EnumerateFiles(dir, "BP_*_Playable.uasset", SearchOption.AllDirectories)
                    .Count();
                if (playableCount == 0)
                {
                    result.Warnings.Add(
                        $"Skipped '{familyName}': has an archetype but no BP_*_Playable class " +
                        "(boss/NPC, not a playable family).");
                    continue;
                }

                var family = new GameDataFamily
                {
                    Name = familyName,
                    ArchetypePackage = ToGamePath(archetype),
                    PlayableCount = playableCount,
                };

                // MAS/LAS come from the archetype's import table (no usmap needed).
                foreach (var import in SafeImportObjectNames(archetype, result))
                {
                    if (import.Contains("/MontageAnimSets/", StringComparison.OrdinalIgnoreCase) &&
                        LeafName(import).StartsWith("MAS_Char_", StringComparison.OrdinalIgnoreCase))
                    {
                        family.MontageAnimSet = import;
                    }
                    else if (import.Contains("/LayerAnimSets/", StringComparison.OrdinalIgnoreCase) &&
                             LeafName(import).StartsWith("LAS_Char_", StringComparison.OrdinalIgnoreCase))
                    {
                        family.LayerAnimSet = import;
                    }
                }

                // Native equipment: DCMD EquipmentList entries are SOFT object
                // paths, so they live in the name map (not the import table).
                foreach (var dcmd in Directory.EnumerateFiles(dir, "DA_DCMD_*.uasset"))
                {
                    family.Dcmds.Add(ToGamePath(dcmd));
                    foreach (var entry in SafeNameMapEntries(dcmd, result))
                    {
                        var leaf = LeafName(entry);
                        if (!leaf.StartsWith("DA_ETA_", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (etaNameToGadget.TryGetValue(leaf, out var gadget) &&
                            !family.NativeEquipment.Contains(gadget))
                        {
                            family.NativeEquipment.Add(gadget);
                            if (equipmentByName.TryGetValue(gadget, out var eq) &&
                                !eq.NativeFamilies.Contains(familyName))
                            {
                                eq.NativeFamilies.Add(familyName);
                            }
                        }
                    }
                }

                family.NativeEquipment.Sort(StringComparer.OrdinalIgnoreCase);
                result.Db.Families.Add(family);
            }
        }
        else
        {
            result.Warnings.Add($"Minifig folder not found: {minifigRoot}");
        }

        result.Db.Families.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.Db.Equipment.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var e in result.Db.Equipment)
        {
            e.NativeFamilies.Sort(StringComparer.OrdinalIgnoreCase);
        }

        MineAnimSets(result);

        if (includeFullCatalog)
        {
            MineCatalog(result);
        }

        return result;
    }

    /// <summary>
    /// Catalogs the animation building blocks under Content/Animation/{MontageAnimSets,
    /// LayerAnimSets}. For character composites (MAS_Char_*/LAS_Char_*) it also reads
    /// the ParentSetsArray composition from the import table (the parent set packages).
    /// </summary>
    private void MineAnimSets(MineResult result)
    {
        var animRoot = Path.Combine(_contentRoot, "Animation");
        foreach (var (sub, kind) in new[] { ("MontageAnimSets", "Montage"), ("LayerAnimSets", "Layer") })
        {
            var root = Path.Combine(animRoot, sub);
            if (!Directory.Exists(root))
            {
                result.Warnings.Add($"Anim folder not found: {root}");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.uasset", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var category = Path.GetFileName(Path.GetDirectoryName(file)!);
                var isComposite = name.StartsWith("MAS_Char_", StringComparison.OrdinalIgnoreCase) ||
                                  name.StartsWith("LAS_Char_", StringComparison.OrdinalIgnoreCase);

                var entry = new GameDataAnimSet
                {
                    Name = name,
                    Package = ToGamePath(file),
                    Kind = kind,
                    Category = category,
                    IsCharacterComposite = isComposite,
                };

                if (isComposite)
                {
                    // ParentSetsArray entries resolve to other MAS_/LAS_ packages
                    // in the import table (Package imports under /Game/Animation).
                    foreach (var import in SafeImportObjectNames(file, result))
                    {
                        if ((import.StartsWith("MAS_", StringComparison.OrdinalIgnoreCase) ||
                             import.StartsWith("LAS_", StringComparison.OrdinalIgnoreCase)) &&
                            !import.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                            !entry.ParentSets.Contains(import, StringComparer.OrdinalIgnoreCase))
                        {
                            entry.ParentSets.Add(import);
                        }
                    }
                }

                result.Db.AnimSets.Add(entry);
            }
        }

        result.Db.AnimSets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enumerates every .uasset under the content root and records its /Game path
    /// plus top-level export class. This is the "browse anything without
    /// extraction" index. Records paths/class names only - never asset bytes.
    /// </summary>
    private void MineCatalog(MineResult result)
    {
        foreach (var file in Directory.EnumerateFiles(_contentRoot, "*.uasset", SearchOption.AllDirectories))
        {
            var gamePath = ToGamePath(file);
            var cls = SafeTopLevelClass(file, result);
            result.Db.Assets.Add(new GameDataAsset { Path = gamePath, Class = cls });
        }

        result.Db.Assets.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
    }

    private string SafeTopLevelClass(string assetPath, MineResult result)
    {
        result.AssetsScanned++;
        try
        {
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, _mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            foreach (var export in asset.Exports)
            {
                // Top-level export (not nested under another export).
                if (export.OuterIndex.Index != 0)
                {
                    continue;
                }

                var classIndex = export.ClassIndex;
                if (classIndex.IsImport())
                {
                    var import = classIndex.ToImport(asset);
                    var name = import?.ObjectName.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors++;
            result.Warnings.Add($"class-failed {Path.GetFileName(assetPath)}: {ex.GetType().Name}");
        }

        return "";
    }

    private IEnumerable<string> SafeImportObjectNames(string assetPath, MineResult result)
    {
        result.AssetsScanned++;
        List<string> names = new();
        try
        {
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, _mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            foreach (var import in asset.Imports)
            {
                var name = import.ObjectName.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors++;
            result.Warnings.Add($"parse-failed {Path.GetFileName(assetPath)}: {ex.GetType().Name}");
        }

        return names;
    }

    private IEnumerable<string> SafeNameMapEntries(string assetPath, MineResult result)
    {
        result.AssetsScanned++;
        List<string> names = new();
        try
        {
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, _mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            foreach (var name in asset.GetNameMapIndexList())
            {
                var text = name.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    names.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors++;
            result.Warnings.Add($"namemap-failed {Path.GetFileName(assetPath)}: {ex.GetType().Name}");
        }

        return names;
    }

    private static string MatchAnimSetForGadget(string gadget, List<GameDataLayerSet> sets, string prefix)
    {
        var exact = sets.FirstOrDefault(s => s.Name.Equals($"{prefix}{gadget}", StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Package;
        }
        var loose = sets.FirstOrDefault(s => s.Name.Contains(gadget, StringComparison.OrdinalIgnoreCase));
        return loose?.Package ?? "";
    }

    private static string MatchLayerSet(string gadget, List<GameDataLayerSet> layerSets)
    {
        // Exact "LAS_Equipment_<Gadget>" first, then a loose contains match
        // (handles suffixed variants like Boomerang_Batman).
        var exact = layerSets.FirstOrDefault(l =>
            l.Name.Equals($"LAS_Equipment_{gadget}", StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Package;
        }

        var loose = layerSets.FirstOrDefault(l =>
            l.Name.Contains(gadget, StringComparison.OrdinalIgnoreCase));
        return loose?.Package ?? "";
    }

    private static string LeafName(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath))
        {
            return "";
        }

        var slash = gamePath.LastIndexOf('/');
        var leaf = slash >= 0 ? gamePath[(slash + 1)..] : gamePath;
        var dot = leaf.IndexOf('.');
        return dot >= 0 ? leaf[..dot] : leaf;
    }

    private string ToGamePath(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var rel = Path.GetRelativePath(_contentRoot, full).Replace('\\', '/');
        var dot = rel.LastIndexOf('.');
        if (dot >= 0)
        {
            rel = rel[..dot];
        }

        return "/Game/" + rel;
    }

    private static string NormalizeRoot(string root)
    {
        var trimmed = root.TrimEnd('\\', '/');
        // Accept either the .../Content dir or a parent that contains it.
        if (Directory.Exists(Path.Combine(trimmed, "Characters")))
        {
            return trimmed;
        }

        var content = Path.Combine(trimmed, "Content");
        return Directory.Exists(content) ? content : trimmed;
    }
}
