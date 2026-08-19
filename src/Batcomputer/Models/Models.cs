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
    public string Playable { get; set; } = "";

    [JsonPropertyName("cutscene")]
    public string Cutscene { get; set; } = "";

    [JsonPropertyName("dcmd")]
    public string Dcmd { get; set; } = "";
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
    public string SlotId { get; set; } = "custom_suit";
    public string DisplayName { get; set; } = "New Suit";
    public string Description { get; set; } = "Custom native suit.";

    // Native registry identity for this suit.
    public string PawnTag { get; set; } = "";

    // Localized UI text for the native suit menu. DisplayName above is the suit name;
    // Description is the menu description. LockedDescription is shown when the suit is
    // gated (empty = no locked text, NOT the inherited base-game unlock string - see
    // §7.1 of the plan doc: never silently retain the Zoo-activity text).
    public string LockedDescription { get; set; } = "";

    // Progress gate for the native suit menu.
    public string ProgressTag { get; set; } = "";
    // Local artwork copied into the suit project for its Home tile.
    public string CoverImagePath { get; set; } = "";
    public string PackageBaseName { get; set; } = "CUSTOM_SUIT_P";
    public TargetPackages TargetPackages { get; set; } = new();
    public TemplateRecord? PlayableTemplate { get; set; }
    public TemplateRecord? CutsceneTemplate { get; set; }
    public TemplateRecord? DcmdTemplate { get; set; }
    public TemplateRecord? VisualSourceTemplate { get; set; }
    public TemplateRecord? VisualCutsceneSourceTemplate { get; set; }
    public TemplateRecord? StaticMeshComponentShapeTemplate { get; set; }

    // Separates the visual cutscene source from the playable machinery donor.
    public SuitBaseProfile? BaseProfile { get; set; }
    public List<NativeSuitRequirement> Requirements { get; set; } = new();

    // UIMD icon texture paths. Empty keeps the donor icon.
    public string IconMenu { get; set; } = "";
    public string IconSuit { get; set; } = "";
    public string IconLeft { get; set; } = "";
    public string IconRight { get; set; } = "";

    // DCMD equipment replacements, indexed from zero.
    public List<EquipmentSlotChange> EquipmentSlots { get; set; } = new();

    // Glider visual: empty, cape, wingsuit, or glider.
    public string GliderType { get; set; } = "";

    // Material applied by the package-time glider rewire.
    public string GliderMaterial { get; set; } = "";

    // Prevents duplicate glider components after later material edits.
    public bool GliderGrafted { get; set; }

    // Donor glide animation sets for cross-type glider parts.
    public string GliderAnimLas { get; set; } = "";   // /Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_<Char>
    public string GliderAnimMas { get; set; } = "";   // /Game/Animation/MontageAnimSets/Traversal/MAS_Glide_<Char>

    // Clone a mod-local archetype before applying animation changes.
    public bool UseCustomArchetype { get; set; }

    // Hero playable used when a visual base lacks runtime machinery.
    public string MachineryDonorPlayable { get; set; } = "";

    // Material assignments replayed after rebuilding staged assets.
    public List<SavedMaterialAssignment> MaterialAssignments { get; set; } = new();

    // Declarative part grafts replayed from the clean base on each rebuild.
    public List<SavedPartGraft> PartGrafts { get; set; } = new();

    // OBJ static meshes created by Batcomputer. These are separate from native grafts because
    // their cooked mesh is rebuilt from a project-owned source file on every fresh stage.
    public List<CustomStaticMeshImport> CustomStaticMeshes { get; set; } = new();

    // Per-suit preview offsets layered over the donor transform.
    public List<SavedPreviewPartPlacement> PreviewPartPlacements { get; set; } = new();

    // Change log shown in Review.
    public List<SavedChange> Changes { get; set; } = new();

    // Animation building-block swaps: replace a donor set in the suit's MAS/LAS
    // composition (e.g. locomotion LAS_Default_Batman → LAS_Default_Catwoman) so
    // the suit uses another family's (or a custom) animations for a category.
    public List<AnimSetOverride> AnimationOverrides { get; set; } = new();

    // Per-animation locomotion pose overrides (idle/walk/run), applied by cloning
    // the suit's OWN ABP_Core and repointing the individual AnimSequences - the
    // crash-free alternative to swapping another family's whole AnimBlueprint.
    public List<AnimSequenceOverride> LocomotionOverrides { get; set; } = new();

    // Cooked Texture2D imports staged into the suit's IoStore trio.
    public List<GeneratedTextureEntry> GeneratedTextures { get; set; } = new();

    // Material instances authored by Batcomputer. Older projects did not record
    // these and are still discovered from disk; new entries retain their donor
    // material and face-mesh compatibility so the Faces browser can reject a
    // cross-rig assignment before it reaches the game.
    public List<GeneratedMaterialEntry> GeneratedMaterials { get; set; } = new();
}

/// <summary>Visual source and runtime donor chosen for a suit base.</summary>
public sealed class SuitBaseProfile
{
    public string VisualBasePackage { get; set; } = "";
    public string VisualBaseKind { get; set; } = ""; // cutscene | playable | character
    public string VisualFamily { get; set; } = "";
    public string GameplayDonorPackage { get; set; } = "";
    public string GameplayFamily { get; set; } = "";
    public string Eligibility { get; set; } = ""; // ready | missing-visual | missing-gameplay-donor
    public string EligibilityDetail { get; set; } = "";
}

/// <summary>
/// A mod is the release/package/localization unit; a suit is a native identity
/// contained inside it. A mod COMPOSES suit projects by reference (it does not
/// absorb their authoring data) - the same suit can live in multiple mods. Persisted
/// as <c>Generated/NativeSuitModProjects/&lt;ModId&gt;.native-suit-mod-project.json</c>
/// (or the migrated legacy Generated workspace selected by the author).
///
/// Everything keys off <see cref="ModId"/>: the pak trio name, the
/// <c>/Game/Mods/&lt;ModId&gt;</c> content root, the <c>ST_&lt;ModId&gt;</c> StringTable,
/// the <c>&lt;ModId&gt;Tags.ini</c>, the runtime manifest folder, and install
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
    // Free-form author notebook for useful donor/material/texture paths and release notes.
    // This belongs to the mod (the packaging unit), so every suit in the collection sees
    // the same references and the notes travel with the authoring project.
    public string NotebookText { get; set; } = "";

    // Derived from ModId ("<ModId>_P"), stored for review/diagnostics.
    public string PackageBaseName { get; set; } = "";
    // "/Game/Mods/<ModId>" - where the aggregate StringTable lives. Suit ASSETS are
    // NOT rebased here; each suit keeps its own content root (one pak, many roots).
    public string ContentRoot { get; set; } = "";
    // "/Game/Mods/<ModId>/Localization/ST_<ModId>.ST_<ModId>".
    public string StringTablePackage { get; set; } = "";

    // Technical IDs previously used by this same authoring project. Batcomputer
    // keeps these solely so the next local Install can remove the exact stale
    // trio / manifest / registry folders left by an intentional pre-release ID
    // change. Published Mod IDs remain an external compatibility contract.
    public List<string> PreviousModIds { get; set; } = new();

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
    // The native template profile chosen for this texture. Keeping it on the entry
    // prevents later staging from silently replacing an intentional compact cook.
    public string CookProfile { get; set; } = "";
    public int CookWidth { get; set; }
    public int CookHeight { get; set; }
    public string CookPixelFormat { get; set; } = "";
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

public sealed class GeneratedMaterialEntry
{
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "Material"; // Material | Face
    public string PackagePath { get; set; } = "";
    public string SourceMaterialPackagePath { get; set; } = "";
    public string ParentMaterialPath { get; set; } = "";
    public List<string> CompatibleFaceMeshPackagePaths { get; set; } = new();
    // Empty for legacy/direct-clone materials. Recipe metadata keeps paired outputs and
    // compatibility provenance intact without breaking older suit project JSON files.
    public string TemplateRecipeId { get; set; } = "";
    public string TemplateOutputRole { get; set; } = "";
    public string TemplateGroupId { get; set; } = "";
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

/// <summary>Sequence swap within the suit's own animation blueprint.</summary>
public sealed class AnimSequenceOverride
{
    public string DonorSequence { get; set; } = "";        // e.g. A_Idle_ThomasWayne (the ABP_Core slot)
    public string DonorSequencePackage { get; set; } = ""; // /Game/Animation/LEGOfig/ThomasWayne/Movement/A_Idle_ThomasWayne
    public string ReplacementSequence { get; set; } = "";  // e.g. A_Idle_Catwoman or a custom name
    public string ReplacementPackage { get; set; } = "";   // /Game path of the replacement AnimSequence
}

/// <summary>Catalog entry for a cooked animation used by an override.</summary>
public sealed class AnimLibraryEntry
{
    // Stable identity used by overrides.
    public string Id { get; set; } = "";
    // Incremented when an entry is imported again.
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    // Optional filter category.
    public string Category { get; set; } = "";

    // Delivery mode: base-game, external, preserve-path, or proven-clone.
    public string SourceMode { get; set; } = "external";

    // Package path for base-game, external, or preserve-path assets.
    public string PackagePath { get; set; } = "";

    // Best-effort inspection results.
    public string AssetClass { get; set; } = "";     // AnimSequence | AnimMontage | TTLayerSet | TTAnimSet | …
    public string Skeleton { get; set; } = "";       // referenced USkeleton import path
    public bool RootMotion { get; set; }
    public string AdditiveMode { get; set; } = "";   // e.g. AAT_None | AAT_LocalSpaceBase | AAT_RotationOffsetMeshSpace
    public List<string> Dependencies { get; set; } = new(); // /Game import paths the asset pulls in
    public bool Inspected { get; set; }              // true once inspection actually ran against bytes

    // Imported cooked files relative to the library cache.
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

/// <summary>A project-owned static OBJ attachment and its authored import transform.</summary>
public sealed class CustomStaticMeshImport
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SourceObjRelativePath { get; set; } = "";
    // The CAE attachment slot selected by the author, for example Head or Hip.
    public string Target { get; set; } = "Head";
    // The matching socket declared by CAE_Default_AttachmentDef.
    public string AttachSocket { get; set; } = "HeadStud_Attach_Socket";
    public float Scale { get; set; } = 150f;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }
    // Unreal rotator degrees baked into the generated StaticMesh: pitch, yaw, then roll.
    public float RotationPitch { get; set; }
    public float RotationYaw { get; set; }
    public float RotationRoll { get; set; }
    public bool HideBaseHead { get; set; } = true;
    public string MaterialPath { get; set; } = "";
    public string MeshPackagePath { get; set; } = "";
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

/// <summary>A saved viewer-only alignment and UV selection for one component.</summary>
public sealed class SavedPreviewPartPlacement
{
    public string Component { get; set; } = "";
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }

    // Null means use the mesh's authored/default UV channel.
    public int? UvChannel { get; set; }
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
    public string Status { get; set; } = "reference-plan";
    public string Backend { get; set; } = "UAssetAPI staged rewrite plus native Asset Registry generation.";
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
