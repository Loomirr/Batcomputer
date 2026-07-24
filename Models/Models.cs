using System.Text.Json.Serialization;

namespace Batcomputer;

public sealed class TemplateFeatureSet
{
    [JsonPropertyName("has_torso2")]
    public bool HasTorso2 { get; set; }

    [JsonPropertyName("has_batman_absolute_torso")]
    public bool HasBatmanAbsoluteTorso { get; set; }

    [JsonPropertyName("has_static_mesh_component")]
    public bool HasStaticMeshComponent { get; set; }

    [JsonPropertyName("has_skeletal_mesh_budgeted")]
    public bool HasSkeletalMeshBudgeted { get; set; }

    [JsonPropertyName("has_headstud_socket")]
    public bool HasHeadStudSocket { get; set; }

    [JsonPropertyName("has_chest_socket")]
    public bool HasChestSocket { get; set; }

    [JsonPropertyName("has_slickback")]
    public bool HasSlickBack { get; set; }

    [JsonPropertyName("has_any_hair")]
    public bool HasAnyHair { get; set; }

    [JsonPropertyName("has_head_asset_tag")]
    public bool HasHeadAssetTag { get; set; }

    [JsonPropertyName("has_face_asset_tag")]
    public bool HasFaceAssetTag { get; set; }

    [JsonPropertyName("has_cape_asset_tag")]
    public bool HasCapeAssetTag { get; set; }

    [JsonPropertyName("has_dcmd_soft_paths")]
    public bool HasDcmdSoftPaths { get; set; }

    [JsonPropertyName("has_equipment_strings")]
    public bool HasEquipmentStrings { get; set; }

    [JsonPropertyName("has_ninjastar")]
    public bool HasNinjaStar { get; set; }

    [JsonPropertyName("has_foamgun")]
    public bool HasFoamGun { get; set; }
}

public sealed class TemplateRecord
{
    [JsonPropertyName("package_path")]
    public string PackagePath { get; set; } = "";

    [JsonPropertyName("content_relative")]
    public string ContentRelative { get; set; } = "";

    [JsonPropertyName("stem")]
    public string Stem { get; set; } = "";

    [JsonPropertyName("character")]
    public string Character { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("template_key")]
    public string TemplateKey { get; set; } = "";

    [JsonPropertyName("uasset")]
    public string Uasset { get; set; } = "";

    [JsonPropertyName("uexp")]
    public string? Uexp { get; set; }

    [JsonPropertyName("ubulk")]
    public string? Ubulk { get; set; }

    [JsonPropertyName("json_export")]
    public string? JsonExport { get; set; }

    [JsonPropertyName("uasset_length")]
    public long UassetLength { get; set; }

    [JsonPropertyName("uexp_length")]
    public long UexpLength { get; set; }

    [JsonPropertyName("has_split_pair")]
    public bool HasSplitPair { get; set; }

    [JsonPropertyName("features")]
    public TemplateFeatureSet Features { get; set; } = new();

    [JsonPropertyName("has_pair")]
    public bool HasPair { get; set; }

    [JsonPropertyName("has_dcmd")]
    public bool HasDcmd { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }
}

public sealed class TargetPackages
{
    [JsonPropertyName("playable")]
    public string Playable { get; set; } = "/Game/Mods/Batman_Thomas/Characters/BP_Batman_Thomas_Playable";

    [JsonPropertyName("cutscene")]
    public string Cutscene { get; set; } = "/Game/Mods/Batman_Thomas/Characters/BP_Batman_Thomas_Cutscene";

    [JsonPropertyName("dcmd")]
    public string Dcmd { get; set; } = "/Game/Mods/Batman_Thomas/Characters/DA_DCMD_Batman_Thomas_Playable";
}

public sealed class RecommendedDonorPlan
{
    [JsonPropertyName("slot_id")]
    public string SlotId { get; set; } = "batman_thomas";

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "";

    [JsonPropertyName("playable_donor")]
    public TemplateRecord? PlayableDonor { get; set; }

    [JsonPropertyName("cutscene_donor")]
    public TemplateRecord? CutsceneDonor { get; set; }

    [JsonPropertyName("dcmd_donor")]
    public TemplateRecord? DcmdDonor { get; set; }

    [JsonPropertyName("thomas_source")]
    public TemplateRecord? ThomasSource { get; set; }

    [JsonPropertyName("thomas_cutscene_source")]
    public TemplateRecord? ThomasCutsceneSource { get; set; }

    [JsonPropertyName("thomas_dcmd_source")]
    public TemplateRecord? ThomasDcmdSource { get; set; }

    [JsonPropertyName("static_mesh_component_shape_donor")]
    public TemplateRecord? StaticMeshComponentShapeDonor { get; set; }

    [JsonPropertyName("target_packages")]
    public TargetPackages TargetPackages { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();
}

public sealed class NativeSuitProject
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = "0.1-plan-gui";
    public string SlotId { get; set; } = "batman_thomas";
    public string DisplayName { get; set; } = "Thomas Wayne";
    public string Description { get; set; } = "Generated native-suit prototype.";

    // Native identity (required for the native-suit path). The globally-unique pawn
    // tag this suit registers as, e.g. "Pawns.Playable.Batman.Electric". Empty on
    // legacy/donor-bridge suits - build-time validation flags an empty PawnTag when
    // packaging a native mod. This is the SUIT's source of truth; a mod does not
    // override it. See docs/native-suit-mod-bundles-...-2026-07-16.md.
    public string PawnTag { get; set; } = "";

    // Localized UI text for the native suit menu. DisplayName above is the suit name;
    // Description is the menu description. LockedDescription is shown when the suit is
    // gated (empty = no locked text, NOT the inherited base-game unlock string - see
    // §7.1 of the plan doc: never silently retain the Zoo-activity text).
    public string LockedDescription { get; set; } = "";

    // Progress/unlock gate. Defaults to the known unlocked Batman progress so custom
    // suits are usable by default; advanced override exposed later. A custom PawnTag
    // and a custom progress tag are separate registration problems.
    public string ProgressTag { get; set; } = "GameProgress.Definitions.Characters.Batman.TheBatman2025";
    // Optional local artwork shown on the Home screen suit tile. The builder
    // copies selected artwork into the suit's project folder so the tile does
    // not depend on the user's original PNG remaining in its old location.
    public string CoverImagePath { get; set; } = "";
    public string PackageBaseName { get; set; } = "THOMAS_NEWSLOT_GENERATED_P";
    public TargetPackages TargetPackages { get; set; } = new();
    public TemplateRecord? PlayableTemplate { get; set; }
    public TemplateRecord? CutsceneTemplate { get; set; }
    public TemplateRecord? DcmdTemplate { get; set; }
    public TemplateRecord? VisualSourceTemplate { get; set; }
    public TemplateRecord? VisualCutsceneSourceTemplate { get; set; }
    public TemplateRecord? StaticMeshComponentShapeTemplate { get; set; }
    public List<NativeSuitRequirement> Requirements { get; set; } = new();

    // UI icon texture object paths (/Game/...) for the generated UIMD. Empty =
    // keep the base Batman icon for that slot.
    public string IconMenu { get; set; } = "";
    public string IconSuit { get; set; } = "";
    public string IconLeft { get; set; } = "";
    public string IconRight { get; set; } = "";

    // Gadget slot replacements applied to this suit's DCMD EquipmentList at
    // generation time (slot is 0-based; gadget is a catalog name like "Whip").
    public List<EquipmentSlotChange> EquipmentSlots { get; set; } = new();

    // EXPERIMENTAL: glide visual choice - "" (keep base), "cape", "wingsuit", or
    // "glider". Recorded by the Gliders toybox; the archetype glider rewire runs at
    // package time.
    public string GliderType { get; set; } = "";

    // EXPERIMENTAL: the material (MI_DECAL_Wingsuit_* or any MI) dropped onto the
    // glider row. Recorded for the package-time glider rewire.
    public string GliderMaterial { get; set; } = "";

    // Set once the wingsuit glide component has been grafted into the stage, so we
    // don't add duplicate glider components on later material changes (the decal is
    // then just a material assignment on the Cape slot).
    public bool GliderGrafted { get; set; }

    // Cross-type glider: the donor character's glide ANIMATION sets, injected as parent
    // sets into the suit's cloned LAS_Char/MAS_Char at package time. Needed because the
    // glider mesh is a membrane driven by CopyPoseFromMesh - without the matching glide
    // body pose (e.g. Catwoman's arms-spread) the wingsuit collapses and is invisible.
    // Empty = no injection (the base already glides in this style, e.g. Batman cape).
    public string GliderAnimLas { get; set; } = "";   // /Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_<Char>
    public string GliderAnimMas { get; set; } = "";   // /Game/Animation/MontageAnimSets/Traversal/MAS_Glide_<Char>

    // EXPERIMENTAL (reparent PoC): when true, generation clones the donor family
    // archetype (BP_CAT_Archetype_Batman) into the mod and reparents the generated
    // playable + cutscene to the clone. Proves cooked-BP reparenting works before
    // we start customizing the clone's anim sets / mesh. Off = unchanged behavior.
    public bool UseCustomArchetype { get; set; }

    // Machinery donor: for base characters that lack their own playable machinery
    // (villains/NPCs with no BP_CAT_Archetype), the /Game path of a hero playable to
    // INHERIT abilities/equipment/animation/archetype from. The base supplies the visual
    // (body mesh + parts); this donor supplies the runtime family. Empty = the base has
    // its own machinery (normal heroes). Also used as the cutscene template when the base
    // has no cutscene sibling.
    public string MachineryDonorPlayable { get; set; } = "";

    // Material assignments applied in the editor, persisted so reloading a suit
    // restores them and re-applies them after the name-map stage is rebuilt
    // (the rebuild wipes the staged .uassets, so these must be replayed).
    public List<SavedMaterialAssignment> MaterialAssignments { get; set; } = new();

    // Declarative part grafts, keyed by visual slot (Head, Torso, …). Each dropped part
    // REPLACES any prior entry for its slot → one part per visual kind. On every stage
    // (re)build the whole list is re-grafted from the CLEAN base - exactly like
    // MaterialAssignments + remove-component Requirements - so parts never accumulate or
    // collide across repeated drops (the old imperative graft stacked duplicate exports).
    public List<SavedPartGraft> PartGrafts { get; set; } = new();

    // Persisted change log shown in the Review screen - reopening a suit restores
    // the full history of what you changed and what you changed it to.
    public List<SavedChange> Changes { get; set; } = new();

    // Animation building-block swaps: replace a donor set in the suit's MAS/LAS
    // composition (e.g. locomotion LAS_Default_Batman → LAS_Default_Catwoman) so
    // the suit uses another family's (or a custom) animations for a category.
    public List<AnimSetOverride> AnimationOverrides { get; set; } = new();

    // Per-animation locomotion pose overrides (idle/walk/run), applied by cloning
    // the suit's OWN ABP_Core and repointing the individual AnimSequences - the
    // crash-free alternative to swapping another family's whole AnimBlueprint.
    public List<AnimSequenceOverride> LocomotionOverrides { get; set; } = new();

    // Texture2D imports cooked from user PNGs. GUI imports now cook split texture
    // files only; the final suit package stages them into the suit's IoStore trio.
    // Generated material instances can reference PackagePath/ObjectPath directly.
    public List<GeneratedTextureEntry> GeneratedTextures { get; set; } = new();
}

/// <summary>
/// A mod is the release/package/localization unit; a suit is a native identity
/// contained inside it. A mod COMPOSES suit projects by reference (it does not
/// absorb their authoring data) - the same suit can live in multiple mods. Persisted
/// as <c>_generated/NativeSuitModProjects/&lt;ModId&gt;.native-suit-mod-project.json</c>.
///
/// Everything keys off <see cref="ModId"/>: the pak trio name, the
/// <c>/Game/Mods/&lt;ModId&gt;</c> content root, the <c>ST_&lt;ModId&gt;</c> StringTable,
/// the <c>&lt;ModId&gt;PawnTags.ini</c>, the runtime manifest folder, and install
/// tracking. It is IMMUTABLE after first release (changing it orphans installed files).
/// </summary>
public sealed class NativeSuitModProject
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = "0.1-native-mod";

    // Stable, filesystem- and Unreal-package-safe identifier. Immutable after release.
    public string ModId { get; set; } = "";
    // Human-readable title (spaces/punctuation allowed).
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    // Optional artwork shown on the Home "Mods" tile.
    public string CoverImagePath { get; set; } = "";

    // Derived from ModId ("<ModId>_P"), stored for review/diagnostics.
    public string PackageBaseName { get; set; } = "";
    // "/Game/Mods/<ModId>" - where the aggregate StringTable lives. Suit ASSETS are
    // NOT rebased here; each suit keeps its own content root (one pak, many roots).
    public string ContentRoot { get; set; } = "";
    // "/Game/Mods/<ModId>/Localization/ST_<ModId>.ST_<ModId>".
    public string StringTablePackage { get; set; } = "";

    public List<ModSuitEntry> Suits { get; set; } = new();
}

/// <summary>
/// A mod's reference to one suit: a pointer to the suit project on disk plus the
/// per-mod placement facts (order, enabled). Identity (PawnTag) and UI text live on
/// the SUIT project, not here - the mod aggregates what the suit already owns.
/// </summary>
public sealed class ModSuitEntry
{
    // Relative path (under the project root) to the suit's
    // <c>.native-suit-project.json</c>, so packs stay portable across machines.
    public string SuitProjectPath { get; set; } = "";
    // The suit's SlotId, cached for display/dedup without loading the project.
    public string SuitId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // Menu ordering hint within the mod (lower = earlier).
    public int MenuOrder { get; set; } = 100;
}

public sealed class GeneratedTextureEntry
{
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "Texture";
    public string SourcePng { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public string ObjectPath { get; set; } = "";
    public string TemplateJson { get; set; } = "";
    public string SourceRawRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public string IoStoreRoot { get; set; } = "";
    public string PackageBaseName { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class AnimSetOverride
{
    public string Category { get; set; } = "";       // Locomotion | Movement | Glide | LedgeGrab | Traversal
    public string Kind { get; set; } = "";            // Layer | Montage
    public string DonorSet { get; set; } = "";        // set replaced in the composite, e.g. LAS_Default_Batman
    public string ReplacementSet { get; set; } = "";  // e.g. LAS_Default_Catwoman
    public string ReplacementPackage { get; set; } = ""; // /Game path of the replacement set
}

/// <summary>
/// Per-animation locomotion override: replaces one AnimSequence the suit's own
/// ABP_Core plays (idle/walk/run pose) with a custom or borrowed sequence. Safe
/// because the animgraph stays the suit's own (shared LEGOFig base) - only the
/// pose asset changes. Applied by cloning ABP_Core + LAS_Default and repointing.
/// </summary>
public sealed class AnimSequenceOverride
{
    public string DonorSequence { get; set; } = "";        // e.g. A_Idle_ThomasWayne (the ABP_Core slot)
    public string DonorSequencePackage { get; set; } = ""; // /Game/Animation/LEGOfig/ThomasWayne/Movement/A_Idle_ThomasWayne
    public string ReplacementSequence { get; set; } = "";  // e.g. A_Idle_Catwoman or a custom name
    public string ReplacementPackage { get; set; } = "";   // /Game path of the replacement AnimSequence
}

/// <summary>
/// Phase 3 (cooked animation library). One catalogued animation the user can pick when
/// building an override, instead of hand-typing a /Game package path. The tool NEVER cooks
/// animations - modders author + cook them in Unreal themselves; the library just REGISTERS
/// and INSPECTS an already-cooked asset and remembers where it lives. Entries feed the
/// existing AnimationOverrides / LocomotionOverrides (their ReplacementPackage) at build time.
/// </summary>
public sealed class AnimLibraryEntry
{
    // Stable identity - never changes once created; overrides reference this, not the name/path.
    public string Id { get; set; } = "";
    // Bumped each time the same entry is re-imported/updated (item 1: IDs + versions).
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    // Optional hint for filtering: Locomotion | Movement | Glide | Traversal | Montage | ""
    public string Category { get; set; } = "";

    // How the referenced asset is delivered at runtime (item 6):
    //   base-game     - a stock cooked asset already in the game (referenced by /Game path)
    //   external      - lives in the MODDER'S OWN pak; we only reference it by /Game path
    //   preserve-path - imported into the library but keeps its original /Game path when packaged
    //   proven-clone  - cloned from a known-good donor asset at build time
    public string SourceMode { get; set; } = "external";

    // The /Game package path of the animation (for base-game / external / preserve-path).
    public string PackagePath { get; set; } = "";

    // --- Inspection results (item 4), best-effort when the asset bytes are resolvable ---
    public string AssetClass { get; set; } = "";     // AnimSequence | AnimMontage | TTLayerSet | TTAnimSet | …
    public string Skeleton { get; set; } = "";       // referenced USkeleton import path
    public bool RootMotion { get; set; }
    public string AdditiveMode { get; set; } = "";   // e.g. AAT_None | AAT_LocalSpaceBase | AAT_RotationOffsetMeshSpace
    public List<string> Dependencies { get; set; } = new(); // /Game import paths the asset pulls in
    public bool Inspected { get; set; }              // true once inspection actually ran against bytes

    // Relative paths (under the library cache) of imported cooked files (item 3): uasset (+ sidecars).
    public List<string> CachedFiles { get; set; } = new();

    public string Notes { get; set; } = "";
    public string AddedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

/// <summary>Top-level catalogue persisted as AnimationLibrary/library.json in the project root.</summary>
public sealed class AnimLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public List<AnimLibraryEntry> Entries { get; set; } = new();
}

public sealed class SavedMaterialAssignment
{
    public string Component { get; set; } = "";
    public int Slot { get; set; }
    public string MiPackagePath { get; set; } = "";
    public string Context { get; set; } = "both"; // both | playable | cutscene
}

/// <summary>
/// One declarative part graft on a suit, keyed by <see cref="Slot"/> (the resolved visual
/// slot the part lands on, e.g. "Head", or a glider's glide-visual component). Stores the
/// donor part records so the graft can be REPLAYED from a clean base on every stage rebuild.
/// Replacing the entry for a slot is how "one part per visual kind" is enforced.
/// </summary>
public sealed class SavedPartGraft
{
    public string Slot { get; set; } = "";            // requested target slot / visual kind key (e.g. "Head")
    public string Label { get; set; } = "";           // display label for logs/UI
    public bool IsGlider { get; set; }                 // glider parts get glide-visual retarget handling
    public SavedPartGraftDonor? Playable { get; set; } // donor part for the playable BP
    public SavedPartGraftDonor? Cutscene { get; set; } // donor part for the cutscene BP

    // Component-instance model. A stable per-drop id + a fine-grained OCCUPANCY GROUP
    // (e.g. "head.scalp_hair", "head.cowl", "head.hat", "cape.primary") that is narrower than the
    // broad Slot ("Head"). Adding a part REPLACES only within the SAME occupancy group and COEXISTS
    // across groups - so dropping hair no longer deletes the cowl. Empty on legacy suits; migrated
    // on load (see MigratePartGraftInstances). InstanceId drives per-instance right-click removal.
    public string InstanceId { get; set; } = "";
    public string OccupancyGroup { get; set; } = "";

    // The ACTUAL component name the graft resolved to in the staged asset (e.g. "Head_2" for a
    // cross-kind hair add, or "Torso" for a same-kind repoint). Written on each rebuild. Lets the
    // remove-component button map a removed component precisely back to its graft entry, without
    // confusing "Head_2" (the hair) with "Head" (the base cowl).
    public string ResolvedComponent { get; set; } = "";
}

/// <summary>
/// A donor part reference for a <see cref="SavedPartGraft"/>. We persist the source package +
/// mesh identity (enough to re-resolve the live <c>NativeSuitPartRecord</c> from the part index
/// on replay), rather than the whole record, so saved suits stay small and survive index rebuilds.
/// </summary>
public sealed class SavedPartGraftDonor
{
    public string SourcePackagePath { get; set; } = "";
    public string Slot { get; set; } = "";
    public string Context { get; set; } = "";         // playable | cutscene
    public string MeshObjectPath { get; set; } = "";  // disambiguates parts sharing a slot
    public string Stem { get; set; } = "";
    public string MeshKind { get; set; } = "";
    public string SemanticKind { get; set; } = "";
    public string TemplatePackagePath { get; set; } = "";
    public string TemplateUasset { get; set; } = "";
    public string TemplateSlot { get; set; } = "";
    public string TemplateComponentClass { get; set; } = "";
    public string ParentComponentOrVariableName { get; set; } = "";
    public string AttachSocket { get; set; } = "";
    public List<string> ComponentTags { get; set; } = new();
}

/// <summary>One entry in the suit's persisted change log (the Review screen).</summary>
public sealed class SavedChange
{
    public string When { get; set; } = "";       // ISO timestamp
    public string Category { get; set; } = "";     // Base | Materials | Parts | Equipment | Animations
    public string Target { get; set; } = "";       // slot / component it affects
    public string Detail { get; set; } = "";       // what it was changed to
    public string Status { get; set; } = "applied"; // applied | staged | pending
}

public sealed class EquipmentSlotChange
{
    public int Slot { get; set; }
    public string Gadget { get; set; } = "";
}

public sealed class NativeSuitRequirement
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SourcePackage { get; set; } = "";
    public string TargetComponent { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class NativeSuitPatchPlan
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = "0.1-plan-gui";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "plan-only";
    public string Backend { get; set; } = "UAssetAPI intended; direct writes not enabled in this first GUI pass.";
    public NativeSuitProject Project { get; set; } = new();
    public List<PatchStep> Steps { get; set; } = new();
}

public sealed class PatchStep
{
    public int Order { get; set; }
    public string Category { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Action { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class NativeSuitPartIndex
{
    public int SchemaVersion { get; set; } = 2;
    public string ToolVersion { get; set; } = "0.3-recipe-index";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "created";
    public string SourceContentRoot { get; set; } = "";
    public string SourceMinifigRoot { get; set; } = "";
    public string? MappingsPath { get; set; }
    public int AssetsFound { get; set; }
    public int AssetsParsed { get; set; }
    public int AssetsWithParts { get; set; }
    public List<NativeSuitPartRecord> Parts { get; set; } = new();
    public List<NativeSuitPartScanError> Errors { get; set; } = new();
}

public sealed class NativeSuitPartRecord
{
    public string SourcePackagePath { get; set; } = "";
    public string SourceUasset { get; set; } = "";
    public string ContentRelativePath { get; set; } = "";
    public string CharacterFolder { get; set; } = "";
    public string Stem { get; set; } = "";
    public string Context { get; set; } = "";
    public string Slot { get; set; } = "";
    public string ComponentClass { get; set; } = "";
    public string ComponentTemplateExport { get; set; } = "";
    public int ComponentTemplateExportIndex { get; set; }
    public string ScsNodeExport { get; set; } = "";
    public int ScsNodeExportIndex { get; set; }
    public string ParentComponentOrVariableName { get; set; } = "";
    public string AttachSocket { get; set; } = "";
    public string MeshKind { get; set; } = "";
    public string MeshObjectName { get; set; } = "";
    public string MeshPackagePath { get; set; } = "";
    public string MeshObjectPath { get; set; } = "";
    public string AnimClassObjectName { get; set; } = "";
    public string AnimClassPackagePath { get; set; } = "";
    public string AnimClassObjectPath { get; set; } = "";
    public List<NativeSuitObjectRef> Materials { get; set; } = new();
    public List<string> ComponentTags { get; set; } = new();
    public bool HasClassChildProperty { get; set; }
    public bool HasMesh => !string.IsNullOrWhiteSpace(MeshObjectName) || !string.IsNullOrWhiteSpace(MeshPackagePath);
    public bool IsKnownVisualSlot { get; set; }
    public bool IsLikelyGraftCandidate { get; set; }
    /// <summary>Normalized visual family used to choose a compatible component recipe.</summary>
    public string SemanticKind { get; set; } = "";
    /// <summary>Native BP package containing the component shell/SCS recipe.</summary>
    public string TemplatePackagePath { get; set; } = "";
    public string TemplateUasset { get; set; } = "";
    public string TemplateSlot { get; set; } = "";
    public string TemplateComponentClass { get; set; } = "";
    public bool IsSynthesized { get; set; }
    public string RecipeKey { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class NativeSuitObjectRef
{
    public string ObjectName { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public string ObjectPath { get; set; } = "";
    public string ClassName { get; set; } = "";
}

public sealed class NativeSuitPartScanError
{
    public string Uasset { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class PartGraftBatchResult
{
    public string Status { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string PatchedContentRoot { get; set; } = "";
    public string GraftedContentRoot { get; set; } = "";
    public string PartIndexPath { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public List<PartGraftPackageResult> PackageResults { get; set; } = new();
}

public sealed class PartGraftPackageResult
{
    public string Role { get; set; } = "";
    public string TargetSlot { get; set; } = "";
    public string CloneSlot { get; set; } = "";
    public string AttachSocket { get; set; } = "";
    public string TargetPackagePath { get; set; } = "";
    public string InputUasset { get; set; } = "";
    public string OutputUasset { get; set; } = "";
    public string DonorPackagePath { get; set; } = "";
    public string DonorMeshObjectPath { get; set; } = "";
    public bool Success { get; set; }
    public bool AlreadyHadTorso2 { get; set; }
    public int AddedImports { get; set; }
    public int AddedExports { get; set; }
    public int NewComponentExportIndex { get; set; }
    public int NewScsNodeExportIndex { get; set; }
    public string? Error { get; set; }
}
