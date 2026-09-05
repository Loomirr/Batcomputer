namespace Batcomputer;

/// <summary>
/// How much of a fighting-style bundle is known to be safe. Native means the shipped character
/// already owns the complete bundle. Preset-required means the individual assets are known, but a
/// foreign gameplay donor must receive them as one coordinated transaction. Experimental means
/// that cross-family runtime behavior has not yet been certified in game.
/// </summary>
public enum FightingStyleSupportTier
{
    ShippedNative,
    AtomicPresetRequired,
    Experimental
}

/// <summary>
/// A read-only description of one shipped player fighting style. The character AbilitySet is
/// evidence for the combat GameplayEffect; it is deliberately not an instruction to append that
/// entire character set to another donor. Required animation-package lists describe closure
/// anchors that must resolve after grafting; they are not a list to append blindly to one
/// ParentSetsArray.
/// </summary>
public sealed record FightingStyleProfile(
    string Id,
    string DisplayName,
    string NativeGameplayFamily,
    string MeleeAbilitySetPackage,
    string CharacterAbilitySetPackage,
    string CombatTypeEffectPackage,
    string CombatTypeTag,
    IReadOnlyList<string> SupportingAbilitySetPackages,
    IReadOnlyList<string> HeldItemAbilityPackages,
    IReadOnlyList<string> BridgeHeldItemAbilityPackages,
    IReadOnlyList<string> RequiredMontageAnimSetPackages,
    IReadOnlyList<string> RequiredLayerAnimSetPackages,
    IReadOnlyList<FightingStyleLayerSlice> RequiredLayerSlices,
    IReadOnlyList<string> RequiredStateMachinePackages,
    IReadOnlyList<string> RequiredAnimationBlueprintPackages,
    FightingStyleSupportTier NativeSupport,
    FightingStyleSupportTier CrossFamilySupport,
    bool RequiresAtomicBundle,
    string SafetySummary,
    IReadOnlyList<string> SafetyNotes);

/// <summary>
/// A context-gated subset of a shipped layer set. Cross-family combat presets clone the source
/// set, retain only rows carrying every required context tag, and add that narrow derivative to
/// the donor's character composite. This preserves the donor's ordinary locomotion/default layer
/// while still supplying held-item poses such as Nightwing's baton layers.
/// </summary>
public sealed record FightingStyleLayerSlice(
    string SourcePackage,
    IReadOnlyList<string> RequiredContextTags,
    IReadOnlyList<string>? AdditionalContextTags = null);

/// <summary>Pure compatibility result consumed by the fighting-style preset picker.</summary>
public sealed class FightingStyleAnalysis
{
    public required FightingStyleProfile Profile { get; init; }
    public string DonorFamily { get; init; } = "";
    public bool IsNativeDonor { get; init; }
    public FightingStyleSupportTier Support { get; init; }
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasConflicts => Conflicts.Count > 0;
    public bool IsNativeComplete => IsNativeDonor && !HasConflicts && Warnings.Count == 0;
}

/// <summary>
/// Catalogues traced player fighting-style bundles. This service performs no file
/// access and never edits a project or Unreal asset; it only describes dependencies and diagnoses
/// a project's declarative AbilitySet choices.
/// </summary>
public static class FightingStyleProfileService
{
    private const string BatmanMelee =
        "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Batman";
    private const string CatwomanMelee =
        "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_CatWoman";
    private const string NightwingMelee =
        "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_NightWing";

    private static readonly IReadOnlyList<FightingStyleProfile> Profiles = Array.AsReadOnly(
        new[]
        {
            new FightingStyleProfile(
                Id: "batman-martial-arts",
                DisplayName: "Batman — martial arts",
                NativeGameplayFamily: "Batman",
                MeleeAbilitySetPackage: BatmanMelee,
                CharacterAbilitySetPackage: "/Game/Characters/Minifig/Batman/Abilities/AS_Batman",
                CombatTypeEffectPackage:
                    "/Game/Characters/Abilities/GameplayEffects/Combat/CombatTypes/GE_CombatType_MartialArts",
                CombatTypeTag: "CombatType.MartialArts",
                SupportingAbilitySetPackages: Array.Empty<string>(),
                HeldItemAbilityPackages: Array.Empty<string>(),
                BridgeHeldItemAbilityPackages: Array.Empty<string>(),
                RequiredMontageAnimSetPackages: new[]
                {
                    "/Game/Animation/MontageAnimSets/Character/MAS_Char_Batman"
                },
                RequiredLayerAnimSetPackages: new[]
                {
                    "/Game/Animation/LayerAnimSets/Character/LAS_Char_Batman"
                },
                RequiredLayerSlices: Array.Empty<FightingStyleLayerSlice>(),
                RequiredStateMachinePackages: new[]
                {
                    "/Game/Animation/LEGOfig/_Shared/Combat_Martial/GTSM_Combat_Martial",
                    "/Game/Animation/LEGOfig/_Shared/Combat_Martial/GTSM_Combat_Martial_PROP"
                },
                RequiredAnimationBlueprintPackages: Array.Empty<string>(),
                NativeSupport: FightingStyleSupportTier.ShippedNative,
                CrossFamilySupport: FightingStyleSupportTier.Experimental,
                RequiresAtomicBundle: true,
                SafetySummary:
                    "The shipped Batman bundle is verified. A foreign donor needs a suit-local combat-effect bridge and the complete martial animation graph.",
                SafetyNotes: new[]
                {
                    "AS_Batman contains Batman-specific behavior in addition to GE_CombatType_MartialArts; do not append the whole set to a foreign donor.",
                    "AS_Melee_Batman must replace the donor's melee set rather than coexist with another AS_Melee_* entry."
                }),

            new FightingStyleProfile(
                Id: "catwoman-agile",
                DisplayName: "Catwoman — agile",
                NativeGameplayFamily: "Catwoman",
                MeleeAbilitySetPackage: CatwomanMelee,
                CharacterAbilitySetPackage: "/Game/Characters/Minifig/Catwoman/AS_Catwoman",
                CombatTypeEffectPackage:
                    "/Game/Characters/Abilities/GameplayEffects/Combat/CombatTypes/GE_CombatType_Agile",
                CombatTypeTag: "CombatType.Agile",
                SupportingAbilitySetPackages: Array.Empty<string>(),
                HeldItemAbilityPackages: new[]
                {
                    "/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_CatClaws"
                },
                BridgeHeldItemAbilityPackages: new[]
                {
                    "/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_CatClaws"
                },
                RequiredMontageAnimSetPackages: new[]
                {
                    "/Game/Animation/MontageAnimSets/Character/MAS_Char_Catwoman",
                    "/Game/Animation/MontageAnimSets/Combat/MAS_Combat_Flurry_Catwoman"
                },
                RequiredLayerAnimSetPackages: new[]
                {
                    "/Game/Animation/LayerAnimSets/Character/LAS_Char_Catwoman"
                },
                RequiredLayerSlices: Array.Empty<FightingStyleLayerSlice>(),
                RequiredStateMachinePackages: new[]
                {
                    "/Game/Animation/LEGOfig/_Shared/Combat_Agile/GTSM_Combat_Agile",
                    "/Game/Animation/LEGOfig/_Shared/Combat_Agile/GTSM_Combat_Agile_PROP"
                },
                RequiredAnimationBlueprintPackages: Array.Empty<string>(),
                NativeSupport: FightingStyleSupportTier.ShippedNative,
                CrossFamilySupport: FightingStyleSupportTier.Experimental,
                RequiresAtomicBundle: true,
                SafetySummary:
                    "The shipped Catwoman bundle is verified. Cross-family agile combat also needs its claws, combat effect, and Catwoman layer/montage graph.",
                SafetyNotes: new[]
                {
                    "AS_Catwoman owns unrelated Catwoman gameplay as well as GE_CombatType_Agile and the claw item; a safe preset must bridge only the required grants.",
                    "The agile melee set and Catwoman animation dependencies must be installed together."
                }),

            new FightingStyleProfile(
                Id: "nightwing-dual-sticks",
                DisplayName: "Nightwing — dual sticks",
                NativeGameplayFamily: "Nightwing",
                MeleeAbilitySetPackage: NightwingMelee,
                CharacterAbilitySetPackage: "/Game/Characters/Minifig/Nightwing/AS_Nightwing",
                CombatTypeEffectPackage:
                    "/Game/Characters/Abilities/GameplayEffects/Combat/CombatTypes/GE_CombatType_DualSticks",
                CombatTypeTag: "CombatType.DualSticks",
                SupportingAbilitySetPackages: new[]
                {
                    "/Game/Characters/Abilities/CoreAbilities/AS_StaffInteractions_Electric"
                },
                HeldItemAbilityPackages: new[]
                {
                    "/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_Batons"
                },
                // AS_StaffInteractions_Electric already owns GA_Item_Batons. Adding it again to
                // the character bridge would create two grants for the same LAM item.
                BridgeHeldItemAbilityPackages: Array.Empty<string>(),
                RequiredMontageAnimSetPackages: new[]
                {
                    "/Game/Animation/MontageAnimSets/Character/MAS_Char_Nightwing",
                    "/Game/Animation/MontageAnimSets/Combat/MAS_Combat_Flurry_Nightwing",
                    "/Game/Animation/MontageAnimSets/Interaction/MAS_Interaction_Staff",
                    "/Game/Animation/MontageAnimSets/StatusEffects/MAS_StatusEffect_ElectricityBatons"
                },
                RequiredLayerAnimSetPackages: new[]
                {
                    "/Game/Animation/LayerAnimSets/Character/LAS_Char_Nightwing",
                    "/Game/Animation/LayerAnimSets/LAS_DEPRECATED_StaffInteractions"
                },
                RequiredLayerSlices: new[]
                {
                    new FightingStyleLayerSlice(
                        "/Game/Animation/LayerAnimSets/Default/LAS_Default_Nightwing",
                        new[] { "Animation.Equipment.Batons" })
                },
                RequiredStateMachinePackages: new[]
                {
                    "/Game/Animation/LEGOfig/_Shared/Combat_DualSticks/GTSM_Combat_DualSticks",
                    "/Game/Animation/LEGOfig/_Shared/Combat_DualSticks/GTSM_Combat_DualSticks_PROP"
                },
                RequiredAnimationBlueprintPackages: new[]
                {
                    "/Game/Animation/LEGOfig/Nightwing/ABP_Core_Batons_Nightwing",
                    "/Game/Animation/LEGOfig/Nightwing/ABP_Core_Batons_Alert_Nightwing",
                    "/Game/Animation/LEGOfig/Nightwing/ABP_Core_Batons_Stealth_Nightwing"
                },
                NativeSupport: FightingStyleSupportTier.ShippedNative,
                CrossFamilySupport: FightingStyleSupportTier.Experimental,
                RequiresAtomicBundle: true,
                SafetySummary:
                    "Nightwing's sticks are a LAM-managed two-hand prop and animation bundle, not an Equipment ED. The melee set alone cannot make them appear.",
                SafetyNotes: new[]
                {
                    "AS_StaffInteractions_Electric supplies GA_Item_Batons; the ability attaches two BP_Baton_Robin actors through LAM.RightHand and LAM.LeftHand.",
                    "Only the Animation.Equipment.Batons rows from LAS_Default_Nightwing are grafted; the gameplay donor keeps its own default movement, alert, stealth, traversal, glide, and perch layers.",
                    "AS_Nightwing contains unrelated Nightwing gameplay in addition to GE_CombatType_DualSticks; do not append the whole set to a foreign donor."
                }),

            Player("batgirl-martial-arts", "Batgirl — martial arts", "Batgirl",
                "AS_Melee_Batgirl", "/Game/Characters/Minifig/Batgirl/AS_Batgirl",
                "MartialArts", "_Shared/Combat_Martial/GTSM_Combat_Martial",
                "Includes Batgirl's own takedowns; does not copy her tablet or other character abilities."),
            Player("gordon-brawler", "Gordon — brawler", "Gordon",
                "AS_Melee_Gordon", "/Game/Characters/Minifig/Gordon/Abilities/AS_Gordon",
                "Brawler", "_Shared/Combat_Brawler/GTSM_Combat_Brawler",
                "Includes Gordon's melee, dodge and takedown grants, without replacing movement or gadgets."),
            Player("talia-agile", "Talia — agile (unarmed)", "TaliaAlGhul",
                "AS_Melee_TaliaAlGhul", "/Game/Characters/Minifig/TaliaAlGhul/AS_Talia",
                "Agile", "_Shared/Combat_Agile/GTSM_Combat_Agile",
                "The playable Talia set is agile combat, not the enemy/boss sword controller. Special takedowns need runtime testing."),
            Player("talia-training", "Talia — prologue / ninja training", "TaliaAlGhul",
                "AS_Melee", "/Game/Characters/Minifig/TaliaAlGhul/AS_Talia",
                "Agile", "_Shared/Combat_Training/GTSM_Combat_Training",
                "The playable prologue/ninja variants combine the training melee set with Talia's agile effect. This is distinct from her later agile melee set, and does not grant a sword."),
            Player("lucius-unarmed", "Lucius Fox — unarmed", "LuciusFox",
                "AS_Melee_LuciusFox", "/Game/Characters/Minifig/LuciusFox/AS_LuciusFox",
                "", "BruceWayneYoungAdult/Combat_BruceWayneYoungAdult/GTSM_Combat_BruceWayneYoungAdult",
                "Uses the young-adult unarmed graph with Lucius's fuller combat grants. The native character has no combat-type effect."),
            Player("bruce-training", "Bruce Wayne / Cluemaster — training", "BruceWayne",
                "AS_Melee", "/Game/Characters/Minifig/BruceWayne/AS_BruceWayne",
                "", "_Shared/Combat_Training/GTSM_Combat_Training",
                "Training combat preserves the selected suit's identity, input and movement. It does not add gadgets."),
            Player("bruce-young-adult", "Young Bruce / Alfred / Thomas — limited combat", "BruceWayneYoungAdult",
                "AS_BruceWayneYoungAdult", "/Game/Characters/Minifig/BruceWayne/AS_BruceWayne",
                "", "BruceWayneYoungAdult/Combat_BruceWayneYoungAdult/GTSM_Combat_BruceWayneYoungAdult",
                "Story-limited set: intentionally suppresses focus HUD, critical hits, grabs and air attacks, and halves attack power. Choose Lucius for a fuller unarmed set."),
            Player("bruce-child", "Child Bruce — limited child combat", "BruceWayneChild",
                "AS_Melee_BruceWayneChild", "/Game/Characters/Minifig/BruceWayne/AS_BruceWayne",
                "", "BruceWayneChild/Combat_BruceWayneChild/GTSM_Combat_BruceWayneChild",
                "Only child melee and pedestrian interaction are granted. Child-rig animations may not align on an adult body."),
            Robin()
        });

    private static FightingStyleProfile Player(
        string id, string name, string family, string melee, string characterSet,
        string combatType, string graph, string note) => new(
        id, name, family, "/Game/Characters/Abilities/MeleeAbilities/" + melee, characterSet,
        combatType.Length == 0 ? "" : "/Game/Characters/Abilities/GameplayEffects/Combat/CombatTypes/GE_CombatType_" + combatType,
        combatType.Length == 0 ? "" : "CombatType." + combatType,
        [], [], [], [], [], [],
        graph.Contains("BruceWayneChild", StringComparison.Ordinal)
            ? ["/Game/Animation/LEGOfig/" + graph,
               "/Game/Animation/LEGOfig/BruceWayneYoungAdult/Combat_BruceWayneYoungAdult/GTSM_Combat_BruceWayneYoungAdult_PROP"]
            : ["/Game/Animation/LEGOfig/" + graph, "/Game/Animation/LEGOfig/" + graph + "_PROP"],
        [], FightingStyleSupportTier.ShippedNative, FightingStyleSupportTier.Experimental, true,
        "Traced from shipped player assets; cross-family use still needs an in-game test.",
        [note, "Only combat dependencies are changed; the donor keeps its character core, input, speech, movement and equipment."]);

    private static FightingStyleProfile Robin() => Player(
        "robin-dual-sticks", "Robin — dual sticks / staff (smallfig)", "RobinDickGrayson",
        "AS_Melee_Robin_DickGrayson", "/Game/Characters/Minifig/Robin_DickGrayson/AS_RobinDickGrayson",
        "DualSticks", "_Shared/Combat_DualSticks/GTSM_Combat_DualSticks",
        "Robin's counter, takedown, grab and staff interactions use smallfig animations. On an adult body, alignment needs testing.") with
        {
            SupportingAbilitySetPackages = ["/Game/Characters/Abilities/CoreAbilities/AS_StaffInteractions"],
            HeldItemAbilityPackages = ["/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_Batons",
                "/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_BattleStaff"],
            RequiredMontageAnimSetPackages = [
                "/Game/Animation/MontageAnimSets/Combat/MAS_Combat_Flurry_Nightwing",
                "/Game/Animation/MontageAnimSets/Interaction/MAS_Interaction_Staff_Smallfig",
                "/Game/Animation/MontageAnimSets/StatusEffects/MAS_StatusEffect_ElectricityBatons"],
            RequiredLayerAnimSetPackages = ["/Game/Animation/LayerAnimSets/LAS_DEPRECATED_StaffInteractions_Smallfig"],
            RequiredLayerSlices = [
                new("/Game/Animation/LayerAnimSets/Default/LAS_Default_RobinDickGrayson",
                    ["Animation.Status.Stealth"], ["Animation.Equipment.Batons"]),
                new("/Game/Animation/LayerAnimSets/Default/LAS_Default_RobinDickGrayson",
                    ["Animation.Status.Alert"], ["Animation.Equipment.Batons"])],
            RequiredAnimationBlueprintPackages = [
                "/Game/Animation/LEGOfig/Robin_DickGrayson/ABP_Core_Batons_Stealth_Robin_DickGrayson",
                "/Game/Animation/LEGOfig/Robin_DickGrayson/ABP_Core_Batons_Alert_Robin_DickGrayson"]
        };

    private static readonly FightingStyleProfile Sword = Profiles[0] with
    {
        Id = "player-sword",
        DisplayName = "Sword — player adapter (customizable)",
        NativeSupport = FightingStyleSupportTier.Experimental,
        SafetySummary = "Player-controlled sword attacks, separate from held items. Add a compatible right-hand item in Held items. Tested on Batman at 1.5x; new combinations need in-game testing.",
        SafetyNotes = ["Uses player combo metadata, not the enemy AI controller. Does not replace movement or gadgets.",
            "Sword settings controls speed, targeting and attack montages. Held items controls the model, material, hand and visibility independently.",
            "Counters, takedowns and prop attacks remain player defaults. Custom assets must be compatible cooked game assets; arbitrary enemy montages are not guaranteed compatible."],
    };
    private static readonly FightingStyleProfile Bat = Sword with {
        Id = PlayerMeleeAdapterService.Bat, DisplayName = "Baseball bat — player adapter (customizable)",
        SafetySummary = "Two native minifigure bat attacks adapted to player input; proven in-game on Batman. Add a right-hand held item separately.",
        SafetyNotes = ["Retains player targeting and combo rules, movement and gadgets. No enemy AI controller is granted.",
            "Combat settings controls speed/targeting; Held items controls the actor, model and visibility. The baseball-bat template is the tested match.",
            "Variations are contextual, not a fixed click sequence. Counters and takedowns retain player defaults."]
    };
    private static readonly FightingStyleProfile Baton = Sword with {
        Id = PlayerMeleeAdapterService.Baton, DisplayName = "Baton — player adapter (customizable)",
        SafetySummary = "One native baton slam adapted to player input; proven in-game on Batman. Add a right-hand held item separately. Electrical effects/stun are not included.",
        SafetyNotes = ["One deliberate slam variation, with player combo/recovery timing. Does not import enemy AI, AoE or self-status abilities.",
            "Combat settings controls speed/targeting; Held items controls appearance and visibility. The stun-baton template is the tested match.",
            "The native actor contains VFX components, but the attack currently does not activate electrical visuals or grant stun damage."]
    };
    private static readonly IReadOnlyList<FightingStyleProfile> AllProfiles = Profiles.Concat([Sword, Bat, Baton]).ToArray();
    public static IReadOnlyList<FightingStyleProfile> Catalog() => AllProfiles;

    public static FightingStyleProfile? Find(string? id) =>
        AllProfiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static FightingStyleProfile? FindForNativeFamily(string? gameplayFamily) =>
        Profiles.FirstOrDefault(profile =>
            profile.NativeGameplayFamily.Equals(gameplayFamily, StringComparison.OrdinalIgnoreCase)) ??
        (gameplayFamily?.ToLowerInvariant() switch
        {
            "alfred" or "thomaswayne" => Find("bruce-young-adult"),
            "cluemaster" => Find("bruce-training"),
            _ => null
        });

    /// <summary>Coverage uses the serialized melee/effect pair, not just a character name.</summary>
    public static FightingStyleProfile? FindForNativeCombat(string meleePackage, IEnumerable<string> combatEffects)
    {
        var effects = combatEffects.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Profiles.FirstOrDefault(profile => SamePackage(profile.MeleeAbilitySetPackage, meleePackage) &&
            (string.IsNullOrWhiteSpace(profile.CombatTypeEffectPackage) ? effects.Count == 0 :
                effects.Count == 1 && SamePackage(effects[0], profile.CombatTypeEffectPackage)));
    }

    /// <summary>
    /// Compares a profile with the donor family and the project's enabled DPRD AbilitySets. The
    /// result intentionally does not claim that an AbilitySet-only selection can carry a combat
    /// GameplayEffect or an animation graph; those require the atomic preset applicator.
    /// </summary>
    public static FightingStyleAnalysis Analyze(
        FightingStyleProfile profile,
        string? donorFamily,
        AbilityLoadoutProfile? projectAbilitySelections)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var donor = donorFamily?.Trim() ?? "";
        var isNative = profile.NativeGameplayFamily.Equals(donor, StringComparison.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        var warnings = new List<string>();

        var enabledSelections = projectAbilitySelections?.AbilitySets
            .Where(selection => selection.Enabled)
            .OrderBy(selection => selection.Order)
            .ToList() ?? new List<AbilitySetSelection>();
        var enabledPackages = enabledSelections
            .Select(selection => Normalize(selection.PackagePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var activeMeleeSets = enabledPackages.Where(IsMeleeAbilitySet).ToList();
        if (projectAbilitySelections is null)
        {
            var nativeDonorProfile = FindForNativeFamily(donor);
            if (nativeDonorProfile is not null && !isNative)
            {
                activeMeleeSets.Add(nativeDonorProfile.MeleeAbilitySetPackage);
            }
        }

        var foreignMeleeSets = activeMeleeSets
            .Where(path => !SamePackage(path, profile.MeleeAbilitySetPackage))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (foreignMeleeSets.Count > 0)
        {
            conflicts.Add(
                $"Replace the donor melee set(s) {JoinAssetNames(foreignMeleeSets)} with {AssetName(profile.MeleeAbilitySetPackage)}. Appending both produces competing melee grants.");
        }
        if (activeMeleeSets.Count > 1)
        {
            conflicts.Add(
                $"The current selection enables {activeMeleeSets.Count} AS_Melee_* sets. A fighting-style profile permits exactly one.");
        }
        if (projectAbilitySelections is not null &&
            !activeMeleeSets.Any(path => SamePackage(path, profile.MeleeAbilitySetPackage)))
        {
            warnings.Add($"The selected loadout does not currently enable {profile.MeleeAbilitySetPackage}.");
        }

        var selectedCharacterSet = enabledPackages.Any(path => SamePackage(path, profile.CharacterAbilitySetPackage));
        if (!isNative && selectedCharacterSet)
        {
            conflicts.Add(
                $"Do not append the whole foreign character set {profile.CharacterAbilitySetPackage}. It is only the authored source of {profile.CombatTypeEffectPackage}; a safe preset must copy that effect into a suit-local bridge set.");
        }

        var addedAbilities = enabledSelections
            .SelectMany(selection => selection.AddedGameplayAbilities ?? new List<CustomGameplayAbilityGrant>())
            .Select(grant => Normalize(grant.PackagePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        var removedAbilities = enabledSelections
            .SelectMany(selection => selection.RemovedGameplayAbilities ?? new List<string>())
            .Select(Normalize)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        if (addedAbilities.Any(path => SamePackage(path, profile.CombatTypeEffectPackage)))
        {
            conflicts.Add(
                $"{profile.CombatTypeEffectPackage} is a GameplayEffect, not a GameplayAbility. It cannot be added through the gameplay-ability grant list.");
        }

        foreach (var heldItem in profile.HeldItemAbilityPackages)
        {
            if (removedAbilities.Any(path => SamePackage(path, heldItem)))
            {
                conflicts.Add($"The selected loadout explicitly removes required held-item ability {heldItem}.");
                continue;
            }

            var suppliedBySupportingSet = profile.SupportingAbilitySetPackages.Any(required =>
                enabledPackages.Any(enabled => SamePackage(enabled, required)));
            if (!isNative &&
                !addedAbilities.Any(path => SamePackage(path, heldItem)) &&
                !suppliedBySupportingSet)
            {
                warnings.Add($"The cross-family bundle still needs held-item ability {heldItem}.");
            }
        }

        foreach (var supportingSet in profile.SupportingAbilitySetPackages)
        {
            if (!isNative && !enabledPackages.Any(path => SamePackage(path, supportingSet)))
            {
                warnings.Add($"The cross-family bundle still needs supporting AbilitySet {supportingSet}.");
            }
        }

        var requiredMeleeSelected = activeMeleeSets.Any(path => SamePackage(path, profile.MeleeAbilitySetPackage));
        if (!isNative)
        {
            warnings.Add(
                $"Cross-family {profile.DisplayName} is experimental. Apply its melee set, {profile.CombatTypeTag} effect, held-item grants, and animation dependencies atomically.");
            warnings.Add(
                $"Raw project AbilitySet choices cannot carry {profile.CombatTypeEffectPackage} or verify the required MAS/LAS graph. Apply the complete preset so Batcomputer creates a suit-local effect bridge and grafts the declared animation packages.");
            if (requiredMeleeSelected)
            {
                warnings.Add(
                    $"{AssetName(profile.MeleeAbilitySetPackage)} is selected, but that one set alone is not a complete fighting-style swap.");
            }
        }

        var editedRequiredMelee = enabledSelections.FirstOrDefault(selection =>
            SamePackage(selection.PackagePath, profile.MeleeAbilitySetPackage));
        if (editedRequiredMelee is { RemovedGameplayAbilities.Count: > 0 })
        {
            warnings.Add(
                $"{AssetName(profile.MeleeAbilitySetPackage)} has removed gameplay abilities, so it no longer matches the shipped profile and requires runtime testing.");
        }

        return new FightingStyleAnalysis
        {
            Profile = profile,
            DonorFamily = donor,
            IsNativeDonor = isNative,
            Support = isNative ? profile.NativeSupport : profile.CrossFamilySupport,
            Conflicts = conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static bool IsMeleeAbilitySet(string packagePath) => AbilityDependencyService.IsCombatSet(packagePath);

    private static bool SamePackage(string? left, string? right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? packagePath) =>
        UnrealPathUtil.NormalizePackagePath(packagePath);

    private static string AssetName(string packagePath) =>
        UnrealPathUtil.AssetName(Normalize(packagePath));

    private static string JoinAssetNames(IEnumerable<string> packages) =>
        string.Join(", ", packages.Select(AssetName));
}
