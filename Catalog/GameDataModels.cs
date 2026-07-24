using System.Text.Json.Serialization;

namespace Batcomputer;

/// <summary>
/// Compatibility "intelligence" database mined once from an extracted game
/// content dump and shipped inside the tool (next to the .exe under
/// <c>gamedata/</c>). It contains only facts/paths/names derived from the base
/// game - never copyrighted cooked asset bytes - so every user gets equipment /
/// animation / family awareness with zero extraction on their end.
/// </summary>
public sealed class GameDataDb
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Game build string this DB was mined from (e.g. 5.6.1-1286904).</summary>
    public string GameBuild { get; set; } = "";

    public string GeneratedUtc { get; set; } = "";

    /// <summary>Playable character families (Batman, Catwoman, ThomasWayne, ...).</summary>
    public List<GameDataFamily> Families { get; set; } = new();

    /// <summary>Every gadget under Content/Characters/Equipment.</summary>
    public List<GameDataEquipment> Equipment { get; set; } = new();

    /// <summary>Per-gadget layer anim sets (LAS_Equipment_*) - the graft source.</summary>
    public List<GameDataLayerSet> EquipmentLayerSets { get; set; } = new();

    /// <summary>
    /// Broad catalog of every cooked asset in the game (path + top-level class),
    /// so the tool can browse/search/pick any asset - base suits, materials,
    /// textures, meshes, DCMDs, UIMDs - with ZERO extraction on the user's end.
    /// This holds only paths and class names, never cooked bytes. Generation that
    /// needs real donor bytes pulls them on-demand from the user's own paks.
    /// </summary>
    public List<GameDataAsset> Assets { get; set; } = new();

    /// <summary>
    /// Animation building blocks (TTAnimSet montage sets + TTLayerSet layer sets).
    /// Character composites (MAS_Char_*/LAS_Char_*) are what a suit's archetype
    /// points at; the rest are the categorized blocks (Equipment/Traversal/…) that
    /// compose into them via ParentSetsArray. This is the data behind the
    /// Animations tab and the equipment anim-graft.
    /// </summary>
    public List<GameDataAnimSet> AnimSets { get; set; } = new();
}

public sealed class GameDataAnimSet
{
    /// <summary>Asset name, e.g. MAS_Char_Batman, LAS_Equipment_Whip.</summary>
    public string Name { get; set; } = "";

    /// <summary>/Game package path.</summary>
    public string Package { get; set; } = "";

    /// <summary>"Montage" (TTAnimSet) or "Layer" (TTLayerSet).</summary>
    public string Kind { get; set; } = "";

    /// <summary>Folder category: Character, Equipment, Traversal, Interaction, Combat, Ride, StatusEffect, Default, Activity.</summary>
    public string Category { get; set; } = "";

    /// <summary>True for the per-family composites (MAS_Char_*/LAS_Char_*) an archetype references.</summary>
    public bool IsCharacterComposite { get; set; }

    /// <summary>For composites: the building-block set packages it pulls in (ParentSetsArray).</summary>
    public List<string> ParentSets { get; set; } = new();
}

public sealed class GameDataAsset
{
    /// <summary>/Game object path (no extension).</summary>
    public string Path { get; set; } = "";

    /// <summary>Top-level export class name (e.g. MaterialInstanceConstant, Texture2D).</summary>
    public string Class { get; set; } = "";
}

public sealed class GameDataFamily
{
    /// <summary>Family folder name under Characters/Minifig (Batman, Catwoman...).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The archetype package that owns the anim wiring. Usually BP_CAT_Archetype_&lt;Family&gt;,
    /// but the naming is not uniform (Catwoman uses BP_Catwoman_Archetype).
    /// </summary>
    public string ArchetypePackage { get; set; } = "";

    /// <summary>
    /// Number of BP_*_Playable classes in the family folder. This - not the presence of an
    /// archetype - is what makes a character playable, and therefore a valid suit base.
    /// Bosses/NPCs (Cluemaster, RasAlGhul, RedHoodOne, Firefly) own archetypes but have 0.
    /// </summary>
    public int PlayableCount { get; set; }

    /// <summary>/Game path of MAS_Char_&lt;Family&gt; (montage anim set), if found.</summary>
    public string MontageAnimSet { get; set; } = "";

    /// <summary>/Game path of LAS_Char_&lt;Family&gt; (layer anim set), if found.</summary>
    public string LayerAnimSet { get; set; } = "";

    /// <summary>Gadget names this family's DCMDs natively equip.</summary>
    public List<string> NativeEquipment { get; set; } = new();

    /// <summary>DCMD package paths belonging to this family.</summary>
    public List<string> Dcmds { get; set; } = new();
}

public sealed class GameDataEquipment
{
    /// <summary>Gadget folder name (Batarang, NinjaStar, Whip...).</summary>
    public string Name { get; set; } = "";

    /// <summary>DA_ETA_&lt;Gadget&gt; package path (the DCMD EquipmentList entry).</summary>
    public string EtaPackage { get; set; } = "";

    /// <summary>BP_&lt;Gadget&gt;_ED equipment-definition package path, if present.</summary>
    public string EdPackage { get; set; } = "";

    /// <summary>DA_UF_&lt;Gadget&gt;Upgrades package path (the UpgradeDataAssets entry), if present.</summary>
    public string UpgradePackage { get; set; } = "";

    /// <summary>Best-matched LAS_Equipment_&lt;Gadget&gt; package path, if any.</summary>
    public string LayerAnimSet { get; set; } = "";

    /// <summary>Best-matched MAS_Equipment_&lt;Gadget&gt; montage set package path, if any.</summary>
    public string MontageAnimSet { get; set; } = "";

    /// <summary>Families whose DCMDs natively include this gadget.</summary>
    public List<string> NativeFamilies { get; set; } = new();

    /// <summary>
    /// GA_Item_&lt;Gadget&gt;* LAM-managed ability packages that spawn/attach the
    /// gadget's held/carried visual. Granted via the character's ability set
    /// (not the ED). Empty = the gadget's visual comes from the ED's ActorsToSpawn.
    /// </summary>
    public List<string> VisualAbilities { get; set; } = new();
}

public sealed class GameDataLayerSet
{
    public string Name { get; set; } = "";
    public string Package { get; set; } = "";
}
