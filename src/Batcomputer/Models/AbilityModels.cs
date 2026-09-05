namespace Batcomputer;

/// <summary>
/// Declarative, per-suit ability loadout. A null profile on <see cref="NativeSuitProject"/> means
/// "use the gameplay donor exactly". The donor package and fingerprint bind an edited profile to
/// the DPRD it was read from so changing bases cannot silently remove a different ability set.
/// </summary>
public sealed class AbilityLoadoutProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string DonorDprdPackage { get; set; } = "";
    public string DonorAbilitySetFingerprint { get; set; } = "";
    // Exact ordered DPRD AbilitySets observed on this profile's selected donor. This is separate
    // from AbilitySets because the latter is the edited result and cannot prove which support or
    // character sets were genuinely inherited. Dependency validation must never infer that from a
    // broad gameplay-family catalog.
    public List<string> DonorAbilitySetPackages { get; set; } = new();
    // Empty means no coordinated fighting-style swap. A non-empty id binds the selected melee
    // AbilitySet to its combat effect, held item, and animation closure; it is never interpreted as
    // permission to combine more than one melee style.
    public string FightingStyleId { get; set; } = "";
    // Serialized name retained for existing projects; shared by the sword/bat/baton adapters.
    public SwordCombatSettings? SwordCombat { get; set; }
    // Null is the legacy bundled-sword format. An explicit empty list means no held items.
    // Do not initialize this property: old JSON must remain distinguishable from an intentional removal.
    public List<HeldItemSettings>? HeldItems { get; set; }
    public bool AllowUnsafeCoreEdits { get; set; }
    public List<AbilitySetSelection> AbilitySets { get; set; } = new();
}

public enum HeldWeaponVisibility { WhileAttacking, InCombat, Always, OutsideCombat }
public enum HeldItemHand { Right, Left }

/// <summary>Independent suit-local held prop. Does not select combat animations or grant attacks.</summary>
public sealed class HeldItemSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Held item";
    public string TemplateId { get; set; } = "sword";
    public HeldItemHand Hand { get; set; }
    public HeldWeaponVisibility Visibility { get; set; } = HeldWeaponVisibility.Always;
    public string MeshPackage { get; set; } = "/Game/Models/Props/SM_Katana";
    public string MaterialPackage { get; set; } = "";
    public WeaponModelRecipe? CustomModel { get; set; }
    public List<HeldItemEffectSettings> Effects { get; set; } = [];
    public HeldItemSettings Clone() => new() { Id = Id, Name = Name, TemplateId = TemplateId, Hand = Hand,
        Visibility = Visibility, MeshPackage = MeshPackage, MaterialPackage = MaterialPackage, CustomModel = CustomModel?.Clone(), Effects = (Effects ?? []).Select(e => e.Clone()).ToList() };
}

/// <summary>Cosmetic Niagara placement in native mesh-local centimetres. Activation follows the held actor.</summary>
public sealed class HeldItemEffectSettings
{
    public string PresetId { get; set; } = "electric-trail";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public float Roll { get; set; }
    public float Scale { get; set; } = 1;
    public HeldItemEffectSettings Clone() => (HeldItemEffectSettings)MemberwiseClone();
}

/// <summary>Optional hit-target status for the player melee adapters, never an ability granted to the wielder.</summary>
public sealed class MeleeStatusSettings
{
    public string PresetId { get; set; } = "none";
    public float DurationSeconds { get; set; } = 2;
    public MeleeStatusSettings Clone() => (MeleeStatusSettings)MemberwiseClone();
}

/// <summary>Per-suit inputs for the player sword adapter; never base-game overrides.</summary>
public sealed class SwordCombatSettings
{
    public MeleeStatusSettings HitStatus { get; set; } = new();
    public HeldWeaponVisibility Visibility { get; set; } = HeldWeaponVisibility.WhileAttacking;
    public float AttackSpeed { get; set; } = 1.5f;
    public bool RequiresCombatTarget { get; set; }
    public string MeshPackage { get; set; } = "/Game/Models/Props/SM_Katana";
    public string MaterialPackage { get; set; } = "";
    public WeaponModelRecipe? CustomModel { get; set; }
    public List<string> AttackMontages { get; set; } = Enumerable.Range(1, 4)
        .Select(n => $"/Game/Animation/Enemies/RedHoodGang_Blade/Combat/AM_D0_AttackFwd_Chain_{n}_RHGBlade").ToList();

    public SwordCombatSettings Clone() => new()
    {
        Visibility = Visibility, AttackSpeed = AttackSpeed, RequiresCombatTarget = RequiresCombatTarget,
        MeshPackage = MeshPackage, MaterialPackage = MaterialPackage,
        CustomModel = CustomModel?.Clone(),
        HitStatus = (HitStatus ?? new()).Clone(),
        AttackMontages = (AttackMontages ?? []).ToList(),
    };
}

/// <summary>Self-contained weapon source. Transforms use the existing OBJ baker's centered origin and Unreal centimetres.</summary>
public sealed class WeaponModelRecipe
{
    public string SourceName { get; set; } = "";
    public string ObjText { get; set; } = "";
    public float Scale { get; set; } = 1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public float Roll { get; set; }
    public List<CustomStaticMeshMaterialSlot> Materials { get; set; } = [];
    public WeaponModelRecipe Clone() => new() { SourceName = SourceName, ObjText = ObjText,
        Scale = Scale, X = X, Y = Y, Z = Z, Pitch = Pitch, Yaw = Yaw, Roll = Roll,
        Materials = Materials.Select(m => new CustomStaticMeshMaterialSlot { Slot = m.Slot,
            SourceMaterialName = m.SourceMaterialName, StableSlotName = m.StableSlotName, MaterialPath = m.MaterialPath }).ToList() };
}

/// <summary>
/// One ordered DPRD AbilitySets entry plus suit-local edits to the selected set. Disabled donor
/// entries represent explicit removals; enabled non-donor entries represent additions.
/// </summary>
public sealed class AbilitySetSelection
{
    public string PackagePath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public List<CustomGameplayAbilityGrant> AddedGameplayAbilities { get; set; } = new();
    public List<string> RemovedGameplayAbilities { get; set; } = new();
}

/// <summary>An explicit gameplay-ability grant authored inside a suit-local AbilitySet clone.</summary>
public sealed class CustomGameplayAbilityGrant
{
    public string PackagePath { get; set; } = "";
    public int AbilityLevel { get; set; } = 1;
    public string InputTag { get; set; } = "";
}

/// <summary>
/// UI-neutral catalog snapshot consumed by <see cref="AbilityExplorerForm"/>. The production
/// AbilityCatalogService can implement <see cref="IAbilityCatalogSource"/>; keeping the form on
/// these small DTOs avoids coupling the editor to UAssetAPI objects or a particular cache.
/// </summary>
public sealed class AbilityEditorCatalog
{
    public string DonorDprdPackage { get; set; } = "";
    public string DonorAbilitySetFingerprint { get; set; } = "";
    public bool SavedLoadoutNeedsRemap { get; set; }
    public List<AbilitySetCatalogEntry> InheritedAbilitySets { get; set; } = new();
    public List<AbilitySetCatalogEntry> AvailableAbilitySets { get; set; } = new();
    public List<GameplayAbilityCatalogEntry> GameplayAbilities { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class AbilitySetCatalogEntry
{
    public string PackagePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Source { get; set; } = "Base game";
    public bool IsCore { get; set; }
    public bool IsAvailable { get; set; } = true;
    public List<GameplayAbilityCatalogEntry> GameplayAbilities { get; set; } = new();
}

public sealed class GameplayAbilityCatalogEntry
{
    public string PackagePath { get; set; } = "";
    public string SourceAbilitySetPackage { get; set; } = "";
    public int AbilityLevel { get; set; } = 1;
    public string InputTag { get; set; } = "";
}

/// <summary>
/// Compile-stable seam for the extracted-asset catalog. Implementations should return the donor's
/// ordered AbilitySets entries and lazily inspected grants; the UI supplies a path-only fallback
/// when no extractor-backed implementation is available.
/// </summary>
public interface IAbilityCatalogSource
{
    AbilityEditorCatalog BuildForProject(NativeSuitProject project);
}
