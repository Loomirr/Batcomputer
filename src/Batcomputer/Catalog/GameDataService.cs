using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Loads the shipped compatibility DB (gamedata/*.json next to the .exe) at
/// runtime and answers family/equipment/animation compatibility questions.
/// Structured compatibility facts remain available without an extraction; the
/// broad material asset view additionally overlays the user's active extracted
/// Content tree so a newer or more complete dump is never hidden by this file.
/// </summary>
public sealed class GameDataService
{
    private static readonly Lazy<GameDataService> _instance = new(Load);

    public static GameDataService Instance => _instance.Value;

    public GameDataDb Db { get; }
    public bool Loaded { get; }
    public string SourcePath { get; }

    private readonly Dictionary<string, GameDataEquipment> _equipmentByName;
    private readonly Dictionary<string, GameDataFamily> _familyByName;

    private GameDataService(GameDataDb db, bool loaded, string source)
    {
        Db = db;
        Loaded = loaded;
        SourcePath = source;
        _equipmentByName = db.Equipment.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        _familyByName = db.Families.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static GameDataService Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "gamedata");
        try
        {
            if (Directory.Exists(dir))
            {
                // Newest json wins (supports multiple game-build files side by side).
                var file = Directory.EnumerateFiles(dir, "*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (file is not null)
                {
                    var db = JsonSerializer.Deserialize<GameDataDb>(
                        File.ReadAllText(file),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (db is not null)
                    {
                        return new GameDataService(db, loaded: true, file);
                    }
                }
            }
        }
        catch
        {
            // Fall through to empty DB - features degrade gracefully to "unknown".
        }

        return new GameDataService(new GameDataDb(), loaded: false, dir);
    }

    /// <summary>
    /// Every cataloged asset whose top-level class matches <paramref name="className"/>
    /// (case-insensitive). Powers pickers/browsers: pass
    /// "MaterialInstanceConstant", "Texture2D", "DinnerCharacterMetaData",
    /// "TtPawnUIMetaData", "StaticMesh", etc.
    /// Material instances merge the shipped fallback with every MI discovered in the active
    /// extracted Content tree; other classes currently use the shipped structured catalog.
    /// </summary>
    public IEnumerable<GameDataAsset> AssetsOfClass(string className)
    {
        var shipped = Db.Assets
            .Where(asset => asset.Class.Equals(className, StringComparison.OrdinalIgnoreCase));
        return className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)
            ? ExtractedMaterialCatalogService.MergeWithActiveExtraction(shipped)
            : shipped;
    }

    /// <summary>Catalog assets whose /Game path contains <paramref name="term"/>.</summary>
    public IEnumerable<GameDataAsset> AssetsMatching(string term) =>
        string.IsNullOrWhiteSpace(term)
            ? Db.Assets
            : Db.Assets.Where(a => a.Path.Contains(term, StringComparison.OrdinalIgnoreCase));

    public bool HasCatalog => Db.Assets.Count > 0;

    public bool HasAnimSets => Db.AnimSets.Count > 0;

    /// <summary>Distinct anim categories (Character, Equipment, Traversal, …) for the given kind.</summary>
    public IReadOnlyList<string> AnimCategories(string kind) =>
        Db.AnimSets.Where(a => a.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Anim sets filtered by kind ("Montage"/"Layer") and optional category.</summary>
    public IEnumerable<GameDataAnimSet> AnimSets(string kind, string? category = null) =>
        Db.AnimSets.Where(a =>
            a.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
            (category is null || a.Category.Equals(category, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Animation categories a suit can swap to another family's building-block set.
    /// Donor set names are the Batman donor's (all custom suits clone that archetype).
    /// </summary>
    public static readonly (string Category, string Kind, string DonorSet, string SetPrefix)[] AnimCategoryMap =
    {
        ("Locomotion (idle/walk/run)", "Layer", "LAS_Default_Batman", "LAS_Default_"),
        ("Traversal", "Layer", "LAS_Traversal_Batman", "LAS_Traversal_"),
        ("Movement (jump/land)", "Montage", "MAS_Movement_Batman", "MAS_Movement_"),
        ("Glide", "Montage", "MAS_Glide_Batman", "MAS_Glide_"),
        ("LedgeGrab", "Montage", "MAS_LedgeGrab_Batman", "MAS_LedgeGrab_"),
    };

    /// <summary>Builds an override for a category using another family's set, if that set exists.</summary>
    public AnimSetOverride? BuildAnimOverride(string category, string sourceFamily)
    {
        var map = AnimCategoryMap.FirstOrDefault(m => m.Category == category);
        if (map.Category is null)
        {
            return null;
        }
        var replacementName = map.SetPrefix + sourceFamily;
        var set = FindAnimSet(replacementName);
        if (set is null)
        {
            return null; // that family has no set for this category
        }
        return new AnimSetOverride
        {
            Category = category,
            Kind = map.Kind,
            DonorSet = map.DonorSet,
            ReplacementSet = replacementName,
            ReplacementPackage = set.Package,
        };
    }

    /// <summary>Families that have a building-block set for the given category.</summary>
    public IReadOnlyList<string> FamiliesWithAnimCategory(string category)
    {
        var map = AnimCategoryMap.FirstOrDefault(m => m.Category == category);
        if (map.Category is null)
        {
            return Array.Empty<string>();
        }
        return Db.AnimSets
            .Where(a => a.Name.StartsWith(map.SetPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name[map.SetPrefix.Length..])
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public GameDataAnimSet? FindAnimSet(string name) =>
        Db.AnimSets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The MAS_Char/LAS_Char composites a family's archetype points at.</summary>
    public GameDataAnimSet? CharacterComposite(string familyName, string kind)
    {
        var prefix = kind.Equals("Montage", StringComparison.OrdinalIgnoreCase) ? "MAS_Char_" : "LAS_Char_";
        return FindAnimSet(prefix + familyName);
    }

    /// <summary>All gadget names, sorted, for pickers.</summary>
    public IReadOnlyList<string> EquipmentNames =>
        Db.Equipment.Select(e => e.Name).ToList();

    public GameDataEquipment? FindEquipment(string gadgetName) =>
        _equipmentByName.TryGetValue(gadgetName, out var e) ? e : null;

    public GameDataFamily? FindFamily(string familyName) =>
        _familyByName.TryGetValue(familyName, out var f) ? f : null;

    /// <summary>
    /// Derives the character family from any base package path / tag by matching
    /// the family folder segment (…/Minifig/&lt;Family&gt;/…) or the family name
    /// inside a pawn tag (Pawns.Playable.&lt;Family&gt;.…) / BP name.
    /// </summary>
    public GameDataFamily? FamilyForBasePath(string? basePathOrTag)
    {
        if (string.IsNullOrWhiteSpace(basePathOrTag))
        {
            return null;
        }

        var text = basePathOrTag.Replace('\\', '/');
        foreach (var family in Db.Families)
        {
            // /Minifig/Batman/ segment, or ".Batman." / "_Batman_" token.
            if (text.Contains($"/Minifig/{family.Name}/", StringComparison.OrdinalIgnoreCase) ||
                text.Contains($".{family.Name}.", StringComparison.OrdinalIgnoreCase) ||
                text.Contains($"_{family.Name}_", StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return null;
    }

    public enum Compatibility
    {
        Unknown,     // no data / can't determine
        Native,      // gadget is native to the suit's own family
        Foreign,     // gadget belongs to another family — anims may break
    }

    public sealed record CompatResult(Compatibility Level, string Detail);

    /// <summary>
    /// Is <paramref name="gadgetName"/> animation-safe on a suit based on
    /// <paramref name="basePathOrTag"/>? Foreign gadgets are the ones that cause
    /// the "wrong animations on equipment swap" bug.
    /// </summary>
    public CompatResult CheckEquipment(string gadgetName, string? basePathOrTag)
    {
        if (!Loaded)
        {
            return new CompatResult(Compatibility.Unknown, "compat data not loaded");
        }

        var equipment = FindEquipment(gadgetName);
        if (equipment is null)
        {
            return new CompatResult(Compatibility.Unknown, $"unknown gadget '{gadgetName}'");
        }

        var family = FamilyForBasePath(basePathOrTag);
        if (family is null)
        {
            return new CompatResult(Compatibility.Unknown, "base family not recognized");
        }

        if (equipment.NativeFamilies.Contains(family.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new CompatResult(Compatibility.Native, $"native to {family.Name}");
        }

        var owners = equipment.NativeFamilies.Count > 0
            ? string.Join("/", equipment.NativeFamilies)
            : "no family";
        // The graft injects whichever anim sets the gadget ships (layer AND/OR montage), so both
        // count - reporting only the layer set understated what the tool can actually do.
        var animSet = !string.IsNullOrEmpty(equipment.LayerAnimSet) ? equipment.LayerAnimSet
            : !string.IsNullOrEmpty(equipment.MontageAnimSet) ? equipment.MontageAnimSet
            : "";
        var graft = string.IsNullOrEmpty(animSet)
            ? "no equipment anim set to graft"
            : "anims graft in via " + animSet[(animSet.LastIndexOf('/') + 1)..];
        return new CompatResult(
            Compatibility.Foreign,
            $"native to {owners}, not {family.Name} ({graft})");
    }
}
