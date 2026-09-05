namespace Batcomputer;

/// <summary>
/// Cardinality learned from shipped DPRD loadouts. Combat packages are alternatives (every
/// playable DPRD has one); grapple-data packages are likewise one profile at a time. Traversal,
/// stealth, vehicle, interaction, equipment, and utility sets are additive.
/// </summary>
public enum AbilitySetCardinality
{
    Additive,
    OneCombatStyle,
    OneGrappleProfile,
}

public enum AbilityDependencySeverity
{
    Information,
    Warning,
    Error,
}

public sealed record AbilityDependencyIssue(
    AbilityDependencySeverity Severity,
    string Message);

public sealed record AbilityAnimationReplacement(
    string Kind,
    string DonorSetPrefix,
    string ReplacementPackage);

/// <summary>
/// Complete character-side closure for the current equipment and optional fighting-style preset.
/// The package-time graft consumes this projection so a UI edit cannot accidentally apply only
/// the visible AbilitySet while omitting its held item, combat effect, or animation graph.
/// </summary>
public sealed class AbilityDependencyPlan
{
    public FightingStyleProfile? FightingStyle { get; init; }
    public IReadOnlyList<string> RequiredAbilitySets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiredGameplayAbilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GameplayAbilitiesToBridge { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiredGameplayEffects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EquipmentOwnedAbilitySets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiredMontageAnimSets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiredLayerAnimSets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<FightingStyleLayerSlice> RequiredLayerSlices { get; init; } =
        Array.Empty<FightingStyleLayerSlice>();
    public IReadOnlyList<string> GameplayAbilitiesToRemove { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MontageAnimSetsToRemove { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LayerAnimSetsToRemove { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AbilityAnimationReplacement> AnimationReplacements { get; init; } =
        Array.Empty<AbilityAnimationReplacement>();
    public IReadOnlyList<AbilityDependencyIssue> Issues { get; init; } = Array.Empty<AbilityDependencyIssue>();

    public bool HasErrors => Issues.Any(issue => issue.Severity == AbilityDependencySeverity.Error);
}

/// <summary>
/// Resolves relationships which are authored across DPRD AbilitySets, equipment ED/ETA records,
/// LAM-managed held-item abilities, combat effects, and character MAS/LAS graphs. It deliberately
/// supports only fighting-style bundles whose full chain has been traced; unknown combat sets are
/// kept out of the "smart preset" path rather than pretending an AbilitySet-only swap is safe.
/// </summary>
public static class AbilityDependencyService
{
    public static AbilitySetCardinality CardinalityForSet(string? packagePath)
    {
        var package = Normalize(packagePath);
        if (package.Contains("/Characters/Abilities/MeleeAbilities/", StringComparison.OrdinalIgnoreCase))
        {
            return AbilitySetCardinality.OneCombatStyle;
        }
        if (package.Contains(
                "/Characters/Abilities/CoreAbilities/Grappling/GameplayDataSets/",
                StringComparison.OrdinalIgnoreCase))
        {
            return AbilitySetCardinality.OneGrappleProfile;
        }
        return AbilitySetCardinality.Additive;
    }

    public static bool IsCombatSet(string? packagePath) =>
        CardinalityForSet(packagePath) == AbilitySetCardinality.OneCombatStyle;

    public static FightingStyleProfile? StyleForMeleeSet(string? packagePath) =>
        FightingStyleProfileService.Catalog().FirstOrDefault(profile =>
            SamePackage(profile.MeleeAbilitySetPackage, packagePath));

    /// <summary>
    /// Applies one known fighting style to the declarative profile. Other combat sets are disabled
    /// (when inherited) or removed (when user-added); additive traversal/utility sets are untouched.
    /// Required supporting sets are enabled. Held-item/effect/animation dependencies are projected
    /// at build time by <see cref="Build"/>.
    /// </summary>
    public static IReadOnlyList<string> ApplyFightingStyle(
        AbilityLoadoutProfile loadout,
        FightingStyleProfile style,
        IReadOnlyCollection<string>? inheritedPackages = null)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(style);
        HeldItemService.Migrate(loadout);

        var inherited = inheritedPackages is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : inheritedPackages.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<string>();

        // A prior preset may have installed style-only support (Nightwing's staff/baton set is the
        // current shipped example). Remove/disable every support set owned only by another traced
        // style before enabling the new one, otherwise held-item grants from two styles coexist.
        var selectedSupport = style.SupportingAbilitySetPackages
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleSupport = FightingStyleProfileService.Catalog()
            .Where(candidate => !candidate.Id.Equals(style.Id, StringComparison.OrdinalIgnoreCase))
            .SelectMany(candidate => candidate.SupportingAbilitySetPackages)
            .Select(Normalize)
            .Where(package => !selectedSupport.Contains(package))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in loadout.AbilitySets
                     .Where(selection => staleSupport.Contains(Normalize(selection.PackagePath)))
                     .ToList())
        {
            if (inherited.Contains(Normalize(existing.PackagePath)))
            {
                if (existing.Enabled)
                {
                    existing.Enabled = false;
                    changes.Add($"disabled {AssetName(existing.PackagePath)}");
                }
            }
            else
            {
                loadout.AbilitySets.Remove(existing);
                changes.Add($"removed {AssetName(existing.PackagePath)}");
            }
        }

        foreach (var existing in loadout.AbilitySets
                     .Where(selection => IsCombatSet(selection.PackagePath) &&
                                         !SamePackage(selection.PackagePath, style.MeleeAbilitySetPackage))
                     .ToList())
        {
            if (inherited.Contains(Normalize(existing.PackagePath)))
            {
                if (existing.Enabled)
                {
                    existing.Enabled = false;
                    changes.Add($"disabled {AssetName(existing.PackagePath)}");
                }
            }
            else
            {
                loadout.AbilitySets.Remove(existing);
                changes.Add($"removed {AssetName(existing.PackagePath)}");
            }
        }

        EnsureSet(loadout, style.MeleeAbilitySetPackage, changes);
        foreach (var supportingSet in style.SupportingAbilitySetPackages)
        {
            EnsureSet(loadout, supportingSet, changes);
        }
        var previousStyle = loadout.FightingStyleId;
        loadout.FightingStyleId = style.Id;
        if (PlayerMeleeAdapterService.Enabled(style.Id)) {
            if (previousStyle != style.Id || loadout.SwordCombat is null) loadout.SwordCombat = PlayerMeleeAdapterService.Defaults(style.Id);
        }
        else loadout.SwordCombat = null;
        NormalizeOrder(loadout);
        changes.Add($"selected {style.DisplayName}");
        return changes;
    }

    public static void ClearFightingStyle(AbilityLoadoutProfile loadout)
    {
        HeldItemService.Migrate(loadout);
        loadout.FightingStyleId = "";
        loadout.SwordCombat = null;
    }

    public static AbilityDependencyPlan Build(
        NativeSuitProject project,
        string? donorFamily = null,
        IEnumerable<GameDataEquipment>? equipmentCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        donorFamily = string.IsNullOrWhiteSpace(donorFamily)
            ? project.BaseProfile?.GameplayFamily ?? ""
            : donorFamily.Trim();
        var equipment = (equipmentCatalog ?? GameDataService.Instance.Db.Equipment)
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var sets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var abilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bridgeAbilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var effects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var equipmentOwnedSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var montage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var abilitiesToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var montageToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layerToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacements = new List<AbilityAnimationReplacement>();
        var layerSlices = new List<FightingStyleLayerSlice>();
        var issues = new List<AbilityDependencyIssue>();
        var profile = project.AbilityLoadout;
        var enabledSelections = profile?.AbilitySets.Where(selection => selection.Enabled).ToList()
                               ?? new List<AbilitySetSelection>();
        var exactDonorAbilitySets = (profile?.DonorAbilitySetPackages ?? new List<string>())
            .Select(Normalize)
            .Where(package => package.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledCombat = enabledSelections.Where(selection => IsCombatSet(selection.PackagePath)).ToList();
        var enabledGrapple = enabledSelections.Where(selection =>
            CardinalityForSet(selection.PackagePath) == AbilitySetCardinality.OneGrappleProfile).ToList();

        var exactDonorEquipmentKnown = TryReadDonorRuntimeEquipmentSlots(
            project,
            equipment.Values,
            out var donorEquipmentSlots);
        var explicitEquipmentChanges = project.EquipmentSlots
            .Where(change => change.Slot >= 0)
            .GroupBy(change => change.Slot)
            .Select(group => group.Last())
            .ToList();
        foreach (var duplicateSlot in project.EquipmentSlots
                     .Where(change => change.Slot >= 0)
                     .GroupBy(change => change.Slot)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(new AbilityDependencyIssue(
                AbilityDependencySeverity.Error,
                $"Equipment slot {duplicateSlot.Key + 1} has multiple saved replacements. Re-select that slot so one exact equipment dependency owns it."));
        }
        if (project.EquipmentSlots.Any(change => change.Slot < 0))
        {
            issues.Add(new AbilityDependencyIssue(
                AbilityDependencySeverity.Error,
                "An equipment replacement has an invalid negative slot index. Remove and re-add that equipment before building."));
        }
        if (project.EquipmentSlots.Count > 0 && !exactDonorEquipmentKnown)
        {
            issues.Add(new AbilityDependencyIssue(
                AbilityDependencySeverity.Error,
                "The selected donor DPRD Equipment array could not be read and mapped exactly. Refresh/reselect the donor before changing equipment; Batcomputer will not guess from DCMD menu metadata."));
        }
        else if (exactDonorEquipmentKnown)
        {
            foreach (var change in explicitEquipmentChanges.Where(change =>
                         change.Slot >= donorEquipmentSlots.Count))
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"Equipment slot {change.Slot + 1} is outside the donor DPRD's {donorEquipmentSlots.Count}-slot runtime loadout. Choose an existing runtime slot; sparse or implicit appends are not certified."));
            }
        }
        var effectiveEquipmentSlots = new Dictionary<int, string>(donorEquipmentSlots);
        foreach (var change in explicitEquipmentChanges)
        {
            effectiveEquipmentSlots[change.Slot] = change.Gadget ?? "";
        }
        var donorEquipmentNames = donorEquipmentSlots.Values
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveEquipmentNames = effectiveEquipmentSlots.Values
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (profile is not null && enabledCombat.Count != 1)
        {
            issues.Add(new AbilityDependencyIssue(
                AbilityDependencySeverity.Error,
                enabledCombat.Count == 0
                    ? "A playable character needs exactly one melee/fighting-style AbilitySet. Restore the donor style or choose one complete fighting-style preset."
                    : "A character can have only one melee/fighting-style AbilitySet. Choose one fighting-style preset; traversal and utility sets may still be combined."));
        }
        if (enabledGrapple.Count > 1)
        {
            issues.Add(new AbilityDependencyIssue(
                AbilityDependencySeverity.Error,
                "A character can have only one grapple-data profile. Choose one grapple profile; other traversal and utility sets may still be combined."));
        }

        FightingStyleProfile? style = null;
        if (SwordCombatService.Enabled(profile))
            foreach (var error in PlayerMeleeAdapterService.Validate(profile!))
                issues.Add(new AbilityDependencyIssue(AbilityDependencySeverity.Error, error));
        var heldItems = HeldItemService.Resolve(profile);
        foreach (var error in HeldItemService.Validate(heldItems)) issues.Add(new AbilityDependencyIssue(AbilityDependencySeverity.Error, error));
        if (SwordCombatService.Enabled(profile) && !heldItems.Any(HeldItemService.SupportsSword))
            issues.Add(new(AbilityDependencySeverity.Error, "Player weapon combat needs a right-hand melee item visible during attacks. Add one in Held items; choosing this style does not add an item automatically."));
        if (heldItems.Count > 0 && !SwordCombatService.Enabled(profile))
            issues.Add(new(AbilityDependencySeverity.Warning, "Held items do not change combat animations or grant attacks. Native fighting-style weapons/gadgets may compete for the same hand; native hand-slot priority and block rules remain in effect."));
        if (!string.IsNullOrWhiteSpace(profile?.FightingStyleId))
        {
            style = FightingStyleProfileService.Find(profile.FightingStyleId);
            if (style is null)
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"The saved fighting-style preset '{profile.FightingStyleId}' is not supported by this version of Batcomputer."));
            }
            else
            {
                var matchingCombat = enabledCombat.Count(selection =>
                    SamePackage(selection.PackagePath, style.MeleeAbilitySetPackage));
                if (matchingCombat != 1 || enabledCombat.Count != 1)
                {
                    issues.Add(new AbilityDependencyIssue(
                        AbilityDependencySeverity.Error,
                        $"{style.DisplayName} must be the sole active combat set. Reapply its preset instead of editing its melee set independently."));
                }

                Add(sets, style.MeleeAbilitySetPackage);
                AddRange(sets, style.SupportingAbilitySetPackages);
                AddRange(abilities, style.HeldItemAbilityPackages);
                AddRange(bridgeAbilities, style.BridgeHeldItemAbilityPackages);
                Add(effects, style.CombatTypeEffectPackage);

                // A foreign whole-character composite is not itself a safe style preset (some
                // shipped composites do intentionally nest another character composite). Inject
                // only the traced style building blocks, and replace the exclusive controllers.
                AddRange(montage, style.RequiredMontageAnimSetPackages.Where(path =>
                    !IsCharacterComposite(path)));
                AddRange(layer, style.RequiredLayerAnimSetPackages.Where(path =>
                    !IsCharacterComposite(path) &&
                    !AssetName(path).StartsWith("LAS_Default_", StringComparison.OrdinalIgnoreCase)));
                if (!style.NativeGameplayFamily.Equals(donorFamily, StringComparison.OrdinalIgnoreCase))
                {
                    layerSlices.AddRange(style.RequiredLayerSlices);
                }
                var combatFlurry = style.RequiredMontageAnimSetPackages.FirstOrDefault(path =>
                    AssetName(path).StartsWith("MAS_Combat_Flurry_", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(combatFlurry))
                {
                    montage.Remove(Normalize(combatFlurry));
                    replacements.Add(new AbilityAnimationReplacement(
                        "Montage",
                        "MAS_Combat_Flurry_",
                        Normalize(combatFlurry)));
                }
                else
                {
                    replacements.Add(new AbilityAnimationReplacement(
                        "MontageRemove",
                        "MAS_Combat_Flurry_",
                        ""));
                }

                if (!style.NativeGameplayFamily.Equals(donorFamily, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new AbilityDependencyIssue(
                        AbilityDependencySeverity.Warning,
                        $"{style.DisplayName} is a cross-family experimental bundle. Batcomputer will keep its melee set, combat effect, held item, and MAS/LAS dependencies atomic, but it still needs an in-game combat test."));

                    var selectedFlurry = style.RequiredMontageAnimSetPackages.FirstOrDefault(path =>
                        AssetName(path).StartsWith("MAS_Combat_Flurry_", StringComparison.OrdinalIgnoreCase));
                    var exactFlurryConflict = project.AnimationSlotOverrides.Any(change =>
                        change.Kind.Equals("Montage", StringComparison.OrdinalIgnoreCase) &&
                        AssetName(change.OwnerSetPackage).StartsWith("MAS_Combat_Flurry_", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(selectedFlurry) ||
                         !SamePackage(change.OwnerSetPackage, selectedFlurry)));
                    if (exactFlurryConflict)
                    {
                        issues.Add(new AbilityDependencyIssue(
                            AbilityDependencySeverity.Error,
                            $"{style.DisplayName} replaces the exclusive combat-flurry animation set, but this suit has an exact override inside the old flurry set. Reset that override before applying the fighting style."));
                    }
                }

                // Switching a certified style is a replacement, not an append. Remove every
                // non-composite MAS/LAS dependency declared by the other certified profiles; the
                // selected profile's declarations and active equipment are added back below.
                var selectedMontageDependencies = style.RequiredMontageAnimSetPackages
                    .Select(Normalize)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selectedLayerDependencies = style.RequiredLayerAnimSetPackages
                    .Select(Normalize)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var outgoing in FightingStyleProfileService.Catalog().Where(candidate =>
                             !candidate.Id.Equals(style.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    AddRange(abilitiesToRemove, outgoing.HeldItemAbilityPackages.Where(package =>
                        !abilities.Contains(Normalize(package))));
                    AddRange(montageToRemove, outgoing.RequiredMontageAnimSetPackages.Where(package =>
                        !IsCharacterComposite(package) &&
                        !selectedMontageDependencies.Contains(Normalize(package))));
                    AddRange(layerToRemove, outgoing.RequiredLayerAnimSetPackages.Where(package =>
                        !IsCharacterComposite(package) &&
                        !AssetName(package).StartsWith("LAS_Default_", StringComparison.OrdinalIgnoreCase) &&
                        !selectedLayerDependencies.Contains(Normalize(package))));
                }
            }
        }
        else
        {
            var knownEnabled = enabledCombat.Select(selection => StyleForMeleeSet(selection.PackagePath))
                .FirstOrDefault(candidate => candidate is not null);
            var combatIsExactDonor = enabledCombat.Count == 1 &&
                                     exactDonorAbilitySets.Contains(Normalize(enabledCombat[0].PackagePath));
            if (profile is not null && enabledCombat.Count == 1 &&
                exactDonorAbilitySets.Count == 0)
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    "This edited loadout is missing its exact donor AbilitySet provenance. Reopen the Abilities editor to refresh the selected donor before building."));
            }
            else if (profile is not null && enabledCombat.Count == 1 && knownEnabled is null && !combatIsExactDonor)
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"{AssetName(enabledCombat[0].PackagePath)} is not a fully traced fighting-style bundle. It cannot be enabled as a raw melee set."));
            }
            else if (knownEnabled is not null && !combatIsExactDonor)
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"{AssetName(knownEnabled.MeleeAbilitySetPackage)} was added without its fighting-style preset. Apply '{knownEnabled.DisplayName}' so its effect, held item, and animation graph are included together."));
            }
        }

        foreach (var candidate in FightingStyleProfileService.Catalog())
        {
            if (enabledSelections.Any(selection =>
                    SamePackage(selection.PackagePath, candidate.CharacterAbilitySetPackage)) &&
                !exactDonorAbilitySets.Contains(Normalize(candidate.CharacterAbilitySetPackage)))
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"{AssetName(candidate.CharacterAbilitySetPackage)} is a complete foreign character set and cannot be appended to {donorFamily}. Use the {candidate.DisplayName} preset, which bridges only its combat effect and held-item dependencies."));
            }
            foreach (var supportSet in candidate.SupportingAbilitySetPackages)
            {
                if (enabledSelections.Any(selection => SamePackage(selection.PackagePath, supportSet)) &&
                    !exactDonorAbilitySets.Contains(Normalize(supportSet)) &&
                    !(style?.SupportingAbilitySetPackages.Any(package => SamePackage(package, supportSet)) ?? false))
                {
                    issues.Add(new AbilityDependencyIssue(
                        AbilityDependencySeverity.Error,
                        $"{AssetName(supportSet)} is managed by the {candidate.DisplayName} preset and cannot be enabled independently."));
                }
            }
        }
        foreach (var selection in enabledSelections.Where(selection =>
                     IsCharacterScopedAbilitySet(selection.PackagePath) &&
                     !exactDonorAbilitySets.Contains(Normalize(selection.PackagePath))))
        {
            issues.Add(new AbilityDependencyIssue(
                AbilityDependencySeverity.Error,
                $"{AssetName(selection.PackagePath)} is a character-scoped AbilitySet that is not inherited by this exact donor DPRD. It cannot be appended as a generic ability bundle; use a fully traced preset instead."));
        }

        // The complete effective loadout owns the abilities that must remain available. This
        // includes unchanged exact-donor equipment; otherwise a user could remove a donor
        // Batarang ability while leaving the Batarang equipped.
        foreach (var item in effectiveEquipmentNames
                     .Select(name => equipment.GetValueOrDefault(name))
                     .Where(item => item is not null)
                     .Cast<GameDataEquipment>())
        {
            var dependency = EquipmentDependencyService.Analyze(item, donorFamily);
            AddRange(equipmentOwnedSets, dependency.AbilitySets);
            if (dependency.AbilitySets.Count == 0)
            {
                AddRange(abilities, item.VisualAbilities);
                AddRange(abilities, dependency.ExtraGrantedAbilities);
            }
        }

        foreach (var change in explicitEquipmentChanges)
        {
            if (!equipment.TryGetValue(change.Gadget ?? "", out var item))
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"Equipment '{change.Gadget}' is absent from the active catalog, so its abilities and animations cannot be resolved."));
                continue;
            }

            var dependency = EquipmentDependencyService.Analyze(item, donorFamily);
            if (dependency.Support == EquipmentSupportKind.Controller &&
                !string.IsNullOrWhiteSpace(dependency.RequiredGameplayFamily) &&
                !dependency.RequiredGameplayFamily.Equals(donorFamily, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"{item.Name} requires the {dependency.RequiredGameplayFamily} gameplay family; its remote-pawn controller cannot be made compatible with {donorFamily}."));
                continue;
            }

            var native = exactDonorEquipmentKnown && donorEquipmentNames.Contains(item.Name);
            // These sets are authored on the ED's AbilitySetsToGrant, not the playable DPRD.
            // For a newly introduced standard gadget, only bridge character-owned grants when
            // the exact donor did not already carry that equipment and therefore its grant.
            if (!native && dependency.AbilitySets.Count == 0)
            {
                AddRange(bridgeAbilities, item.VisualAbilities);
                AddRange(bridgeAbilities, dependency.ExtraGrantedAbilities);
            }
            if (!native)
            {
                Add(montage, item.MontageAnimSet);
                Add(layer, item.LayerAnimSet);
            }
        }

        // Do not prune anything that the effective equipment closure still needs. Certified-style
        // cleanup is intentionally seeded before equipment resolution so an active gadget wins.
        montageToRemove.ExceptWith(montage);
        layerToRemove.ExceptWith(layer);

        // Remove the input/animation closure of equipment displaced from the exact donor loadout,
        // but only when no effective slot still carries that item and no remaining item shares the
        // same dependency. If the donor DPRD cannot be inspected, additions remain conservative and
        // pruning is intentionally skipped rather than guessing from family aggregation.
        if (exactDonorEquipmentKnown)
        {
            var effectiveItems = effectiveEquipmentNames
                .Select(name => equipment.GetValueOrDefault(name))
                .Where(item => item is not null)
                .Cast<GameDataEquipment>()
                .ToList();
            foreach (var removedName in donorEquipmentNames.Except(
                         effectiveEquipmentNames,
                         StringComparer.OrdinalIgnoreCase))
            {
                if (!equipment.TryGetValue(removedName, out var removedItem)) continue;
                AddRange(abilitiesToRemove, removedItem.VisualAbilities.Where(package =>
                    !abilities.Contains(Normalize(package)) &&
                    !effectiveItems.Any(item => item.VisualAbilities.Any(other => SamePackage(other, package)))));
                if (!string.IsNullOrWhiteSpace(removedItem.MontageAnimSet) &&
                    !montage.Contains(Normalize(removedItem.MontageAnimSet)) &&
                    !effectiveItems.Any(item => SamePackage(item.MontageAnimSet, removedItem.MontageAnimSet)))
                {
                    Add(montageToRemove, removedItem.MontageAnimSet);
                }
                if (!string.IsNullOrWhiteSpace(removedItem.LayerAnimSet) &&
                    !layer.Contains(Normalize(removedItem.LayerAnimSet)) &&
                    !effectiveItems.Any(item => SamePackage(item.LayerAnimSet, removedItem.LayerAnimSet)))
                {
                    Add(layerToRemove, removedItem.LayerAnimSet);
                }
            }
        }
        // An unreadable donor runtime loadout is already a hard error above. Additions may still be
        // projected for diagnostics, but packaging cannot proceed until exact slot authority exists.

        // Controller/support AbilitySets are not safe shortcuts: their inherent grants assume the
        // corresponding ED/ETA or fighting-style bundle even when the user adds no individual GA.
        // Equipment-owned sets are granted by the ED itself and must never also be emitted into the
        // DPRD, even when that equipment is active.
        foreach (var selection in enabledSelections)
        {
            var owners = equipment.Values.Where(item =>
                    EquipmentDependencyService.Analyze(item, donorFamily).AbilitySets
                        .Any(package => SamePackage(package, selection.PackagePath)))
                .ToList();
            if (owners.Count > 0)
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"{AssetName(selection.PackagePath)} is owned and granted by the {string.Join(" or ", owners.Select(item => item.Name))} equipment definition. Remove it from the character DPRD loadout; selecting the equipment carries that controller set automatically."));
            }
        }

        // A manually added equipment GA is unsafe without the matching ETA/ED loadout. Native
        // equipment owned by the gameplay donor counts as present even when no override is saved.
        foreach (var grant in enabledSelections.SelectMany(selection => selection.AddedGameplayAbilities))
        {
            var requiredItem = EquipmentForAbility(grant.PackagePath, equipment.Values);
            if (requiredItem is not null && !effectiveEquipmentNames.Contains(requiredItem.Name))
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    exactDonorEquipmentKnown
                        ? $"{AssetName(grant.PackagePath)} belongs to {requiredItem.Name}, but the selected donor DPRD does not equip it. Add the equipment first; Batcomputer will then grant its complete ability/animation closure."
                        : $"{AssetName(grant.PackagePath)} belongs to {requiredItem.Name}, but the selected donor DPRD could not be inspected. Add that equipment explicitly before granting its ability so Batcomputer does not guess from family-wide data."));
            }

            var heldStyle = FightingStyleProfileService.Catalog().FirstOrDefault(candidate =>
                candidate.HeldItemAbilityPackages.Any(path => SamePackage(path, grant.PackagePath)));
            if (heldStyle is not null && !(style?.HeldItemAbilityPackages.Any(path => SamePackage(path, grant.PackagePath)) ?? false))
            {
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"{AssetName(grant.PackagePath)} is managed by the {heldStyle.DisplayName} preset and cannot be granted by itself."));
            }
        }

        var removed = enabledSelections.SelectMany(selection => selection.RemovedGameplayAbilities)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in abilities.Where(removed.Contains))
        {
            issues.Add(new AbilityDependencyIssue(
                AbilityDependencySeverity.Error,
                $"{AssetName(required)} is required by active equipment or the fighting style and cannot be removed. Remove that dependency first or restore the grant."));
        }
        if (!exactDonorEquipmentKnown)
        {
            foreach (var removedAbility in removed)
            {
                var possibleOwner = EquipmentForAbility(removedAbility, equipment.Values);
                if (possibleOwner is null || effectiveEquipmentNames.Contains(possibleOwner.Name)) continue;
                issues.Add(new AbilityDependencyIssue(
                    AbilityDependencySeverity.Error,
                    $"{AssetName(removedAbility)} belongs to {possibleOwner.Name}, but the selected donor DPRD could not be inspected to prove that equipment is absent. Refresh/reselect the donor before removing its ability."));
            }
        }

        // A user-authored grant is already emitted by its suit-local AbilitySet clone. Do not also
        // bridge the same GA into the character set, which would create two active grants when the
        // user deliberately placed it in another set.
        var manuallyAddedAbilities = enabledSelections
            .SelectMany(selection => selection.AddedGameplayAbilities)
            .Select(grant => Normalize(grant.PackagePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bridgeAbilities.RemoveWhere(manuallyAddedAbilities.Contains);

        // An exclusive category replacement needs the donor category to remain present as its
        // verified anchor. Generic outgoing-style/equipment cleanup must not delete that anchor
        // first; SetExclusiveParentSet will atomically remove every matching parent and install
        // the selected one. This is especially important for Batman -> Nightwing, where the
        // outgoing LAS_Default_Batman is the exact parent the LAS_Default_* replacement consumes.
        var montageReplacementPrefixes = replacements
            .Where(replacement =>
                replacement.Kind.Equals("Montage", StringComparison.OrdinalIgnoreCase) ||
                replacement.Kind.Equals("MontageRemove", StringComparison.OrdinalIgnoreCase))
            .Select(replacement => replacement.DonorSetPrefix)
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var layerReplacementPrefixes = replacements
            .Where(replacement => replacement.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase))
            .Select(replacement => replacement.DonorSetPrefix)
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        montageToRemove.RemoveWhere(package => montageReplacementPrefixes.Any(prefix =>
            AssetName(package).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        layerToRemove.RemoveWhere(package => layerReplacementPrefixes.Any(prefix =>
            AssetName(package).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

        return new AbilityDependencyPlan
        {
            FightingStyle = style,
            RequiredAbilitySets = Ordered(sets),
            RequiredGameplayAbilities = Ordered(abilities),
            GameplayAbilitiesToBridge = Ordered(bridgeAbilities),
            RequiredGameplayEffects = Ordered(effects),
            EquipmentOwnedAbilitySets = Ordered(equipmentOwnedSets),
            RequiredMontageAnimSets = Ordered(montage),
            RequiredLayerAnimSets = Ordered(layer),
            RequiredLayerSlices = layerSlices
                .DistinctBy(slice =>
                    Normalize(slice.SourcePackage) + "|" +
                    string.Join("|", slice.RequiredContextTags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)) + "+" +
                    string.Join("|", (slice.AdditionalContextTags ?? []).OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase)
                .ToList(),
            GameplayAbilitiesToRemove = Ordered(abilitiesToRemove),
            MontageAnimSetsToRemove = Ordered(montageToRemove),
            LayerAnimSetsToRemove = Ordered(layerToRemove),
            AnimationReplacements = replacements
                .DistinctBy(item => $"{item.Kind}|{item.DonorSetPrefix}", StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Issues = issues.DistinctBy(issue => $"{issue.Severity}|{issue.Message}", StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    public static string? RequiredSetRemovalReason(NativeSuitProject project, string packagePath)
    {
        var plan = Build(project);
        return plan.RequiredAbilitySets.Contains(Normalize(packagePath), StringComparer.OrdinalIgnoreCase)
            ? $"{AssetName(packagePath)} is required by " +
              (plan.FightingStyle is not null ? plan.FightingStyle.DisplayName : "the active equipment") +
              ". Remove or change that preset/equipment first."
            : null;
    }

    public static string? RequiredGrantRemovalReason(NativeSuitProject project, string packagePath)
    {
        var plan = Build(project);
        return plan.RequiredGameplayAbilities.Contains(Normalize(packagePath), StringComparer.OrdinalIgnoreCase)
            ? $"{AssetName(packagePath)} is required by active equipment or the selected fighting style. Remove that dependency first."
            : null;
    }

    public static string? AddedGrantCompatibilityError(
        NativeSuitProject project,
        string packagePath,
        IEnumerable<GameDataEquipment>? equipmentCatalog = null)
    {
        var catalog = (equipmentCatalog ?? GameDataService.Instance.Db.Equipment).ToList();
        var item = EquipmentForAbility(packagePath, catalog);
        TryReadEffectiveEquipment(project, catalog, out var effectiveEquipment);
        if (item is not null && !effectiveEquipment.Contains(item.Name))
        {
            return $"{AssetName(packagePath)} belongs to {item.Name}, but the selected donor DPRD does not prove that equipment is active. Add it explicitly first so its ETA/ED, abilities, and animations are installed together.";
        }

        var heldStyle = FightingStyleProfileService.Catalog().FirstOrDefault(candidate =>
            candidate.HeldItemAbilityPackages.Any(path => SamePackage(path, packagePath)));
        if (heldStyle is not null &&
            !(FightingStyleProfileService.Find(project.AbilityLoadout?.FightingStyleId)?.HeldItemAbilityPackages
                .Any(path => SamePackage(path, packagePath)) ?? false))
        {
            return $"{AssetName(packagePath)} is a held-item ability managed by {heldStyle.DisplayName}. Apply that fighting-style preset instead of granting the item by itself.";
        }
        return null;
    }

    public static string? AddedSetCompatibilityError(
        NativeSuitProject project,
        string packagePath,
        IEnumerable<GameDataEquipment>? equipmentCatalog = null)
    {
        var package = Normalize(packagePath);
        var donorFamily = project.BaseProfile?.GameplayFamily ?? "";
        var exactDonorAbilitySets = (project.AbilityLoadout?.DonorAbilitySetPackages ?? new List<string>())
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (IsCharacterScopedAbilitySet(package))
        {
            return $"{AssetName(package)} is a character-scoped AbilitySet that is not inherited by this exact donor. It cannot be appended as a generic ability bundle; use a fully traced preset instead.";
        }
        foreach (var style in FightingStyleProfileService.Catalog())
        {
            if (SamePackage(package, style.CharacterAbilitySetPackage))
            {
                return $"{AssetName(package)} is a complete foreign character set. Apply the {style.DisplayName} preset so only its compatible combat dependencies are bridged.";
            }
            if (style.SupportingAbilitySetPackages.Any(candidate => SamePackage(candidate, package)) &&
                !(FightingStyleProfileService.Find(project.AbilityLoadout?.FightingStyleId)?.SupportingAbilitySetPackages
                    .Any(candidate => SamePackage(candidate, package)) ?? false))
            {
                return $"{AssetName(package)} is managed by the {style.DisplayName} preset and cannot be enabled independently.";
            }
        }

        var catalog = (equipmentCatalog ?? GameDataService.Instance.Db.Equipment).ToList();
        var owners = catalog.Where(item => EquipmentDependencyService.Analyze(item, donorFamily)
                .AbilitySets.Any(candidate => SamePackage(candidate, package)))
            .ToList();
        if (owners.Count > 0)
        {
            return $"{AssetName(package)} is granted by the {string.Join(" or ", owners.Select(item => item.Name))} equipment definition and must not be added to the character DPRD. Add the equipment itself instead.";
        }
        return null;
    }

    public static GameDataEquipment? EquipmentForAbility(
        string? abilityPackage,
        IEnumerable<GameDataEquipment>? equipmentCatalog = null)
    {
        var package = Normalize(abilityPackage);
        if (string.IsNullOrWhiteSpace(package)) return null;
        foreach (var item in equipmentCatalog ?? GameDataService.Instance.Db.Equipment)
        {
            if (item.VisualAbilities.Any(path => SamePackage(path, package)) ||
                package.Contains($"/Characters/Equipment/{item.Name}/", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the selected donor DPRD instead of treating DCMD menu metadata or family-wide
    /// NativeFamilies aggregation as proof of the runtime loadout. Saved slot edits are overlaid by
    /// exact index.
    /// </summary>
    internal static bool TryReadEffectiveEquipment(
        NativeSuitProject project,
        IEnumerable<GameDataEquipment> equipmentCatalog,
        out HashSet<string> equipmentNames)
    {
        var catalog = equipmentCatalog.ToList();
        var exactDonorKnown = TryReadDonorRuntimeEquipmentSlots(project, catalog, out var effectiveSlots);
        foreach (var change in project.EquipmentSlots.Where(change => change.Slot >= 0))
        {
            effectiveSlots[change.Slot] = change.Gadget ?? "";
        }
        equipmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in effectiveSlots.Values.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            equipmentNames.Add(name);
        }
        return exactDonorKnown;
    }

    internal static bool IsEquipmentPresentInDonor(
        NativeSuitProject project,
        GameDataEquipment equipment,
        IEnumerable<GameDataEquipment>? equipmentCatalog = null)
    {
        var known = TryReadDonorRuntimeEquipmentSlots(
            project,
            equipmentCatalog ?? GameDataService.Instance.Db.Equipment,
            out var donorSlots);
        return known && donorSlots.Values.Contains(equipment.Name, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool TryReadDonorRuntimeEquipmentSlots(
        NativeSuitProject project,
        IEnumerable<GameDataEquipment> equipmentCatalog,
        out Dictionary<int, string> donorSlots)
    {
        donorSlots = new Dictionary<int, string>();
        var mappings = AppSettings.Current.EffectiveUsmapPath();
        if (string.IsNullOrWhiteSpace(mappings) || !File.Exists(mappings))
        {
            return false;
        }

        try
        {
            var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var usmap = MappingsCache.Load(mappings);
            var donor = AnimArchetypeGraftService.DetectDonorForProject(project, extractedRoot, usmap);
            var dprdPackage = !string.IsNullOrWhiteSpace(donor?.DprdPackage)
                ? donor!.DprdPackage
                : project.AbilityLoadout?.DonorDprdPackage ?? "";
            var donorDprd = ExtractedPackagePathService.ResolvePackageUasset(
                extractedRoot,
                dprdPackage) ?? "";
            return TryReadRuntimeEquipmentSlots(donorDprd, equipmentCatalog, out donorSlots);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadRuntimeEquipmentSlots(
        string dprdUasset,
        IEnumerable<GameDataEquipment> equipmentCatalog,
        out Dictionary<int, string> slots)
    {
        slots = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(dprdUasset) || !File.Exists(dprdUasset)) return false;
        var inspection = new AbilityAssetMutationService().InspectDprdEquipment(dprdUasset);
        if (!inspection.Success) return false;

        var catalog = equipmentCatalog.ToList();
        var allMapped = true;
        foreach (var reference in inspection.Equipment.OrderBy(reference => reference.Index))
        {
            if (reference.IsNull)
            {
                slots[reference.Index] = "";
                continue;
            }
            var item = catalog.FirstOrDefault(candidate =>
                SamePackage(candidate.EdPackage, reference.PackagePath) ||
                (AssetName(candidate.EdPackage) + "_C").Equals(
                    reference.ObjectName,
                    StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                slots[reference.Index] = "";
                allMapped = false;
            }
            else
            {
                slots[reference.Index] = item.Name;
            }
        }
        return allMapped;
    }

    private static void EnsureSet(AbilityLoadoutProfile loadout, string packagePath, ICollection<string> changes)
    {
        var package = Normalize(packagePath);
        var selection = loadout.AbilitySets.FirstOrDefault(item => SamePackage(item.PackagePath, package));
        if (selection is null)
        {
            loadout.AbilitySets.Add(new AbilitySetSelection
            {
                PackagePath = package,
                Enabled = true,
                Order = loadout.AbilitySets.Where(item => item.Enabled).Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1,
            });
            changes.Add($"added {AssetName(package)}");
        }
        else if (!selection.Enabled)
        {
            selection.Enabled = true;
            changes.Add($"restored {AssetName(package)}");
        }
    }

    private static void NormalizeOrder(AbilityLoadoutProfile loadout)
    {
        var order = 0;
        foreach (var item in loadout.AbilitySets.Where(item => item.Enabled)
                     .OrderBy(item => item.Order)
                     .ThenBy(item => item.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            item.Order = order++;
        }
    }

    private static bool IsCharacterComposite(string packagePath)
    {
        var name = AssetName(packagePath);
        return name.StartsWith("MAS_Char_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("LAS_Char_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCharacterScopedAbilitySet(string packagePath)
    {
        var normalized = Normalize(packagePath);
        return AssetName(normalized).StartsWith("AS_", StringComparison.OrdinalIgnoreCase) &&
               (normalized.Contains("/Characters/Minifig/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/Characters/Smallfig/", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Ordered(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void Add(ISet<string> target, string? value)
    {
        var normalized = Normalize(value);
        if (!string.IsNullOrWhiteSpace(normalized)) target.Add(normalized);
    }

    private static void AddRange(ISet<string> target, IEnumerable<string> values)
    {
        foreach (var value in values) Add(target, value);
    }

    private static bool SamePackage(string? left, string? right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) =>
        UnrealPathUtil.NormalizePackagePath(value ?? "");

    private static string AssetName(string? packagePath) =>
        UnrealPathUtil.AssetName(Normalize(packagePath));
}
