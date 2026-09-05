namespace Batcomputer;

/// <summary>Portable checks for declarative ability-loadout resolution and combat-style safety.</summary>
internal static class AbilityLoadoutRegressionChecks
{
    public static void Run(List<string> failures, TextWriter output)
    {
        HeldItemRegressionChecks.Run(failures, output);
        PlayerMeleeAdapterRegressionChecks.Run(failures, output);
        var presentedSword = new AbilityLoadoutProfile { FightingStyleId = SwordCombatService.StyleId,
            SwordCombat = new() { CustomModel = new() { SourceName = "Sword.obj" } } };
        var presentedEntries = AbilityLoadoutPresentation.BundleEntries(presentedSword,
            "/Game/Mods/AbilitiesAndEquipmentTests/Characters/BP_Test_Playable");
        Check(presentedEntries.Count == 5 && presentedEntries.Any(e => e.Package.EndsWith("/GA_PlayerHeldSword")) &&
              presentedEntries.All(e => e.Package.StartsWith("/Game/Mods/AbilitiesAndEquipmentTests/CombatSword/")) &&
              presentedSword.AbilitySets.Count == 0 &&
              AbilityLoadoutPresentation.BundleEntries(new(), "/Game/Mods/Test/Characters/BP_Test").Count == 0,
            "loadout displays generated sword attacks/held-item dependencies in the actual suit namespace without mutating saved sets", failures, output);
        var styles = FightingStyleProfileService.Catalog();
        Check(styles.Count == 15 && styles.Select(x => x.Id).Distinct().Count() == styles.Count,
            "twelve native fighting-style bundles plus sword/bat/baton player adapters are cataloged", failures, output);
        Check(FightingStyleProfileService.FindForNativeCombat("/Game/Characters/Abilities/MeleeAbilities/AS_Melee",
                  ["/Game/Characters/Abilities/GameplayEffects/Combat/CombatTypes/GE_CombatType_Agile"])?.Id == "talia-training" &&
              FightingStyleProfileService.FindForNativeCombat("/Game/Characters/Abilities/MeleeAbilities/AS_Melee", [])?.Id == "bruce-training",
            "Talia story combat is distinguished from Bruce training by its serialized combat effect", failures, output);
        var sword = new AbilitySetCatalogEntry
        {
            PackagePath = "/Game/Global/AI/Abilities/AS_BladeGoon",
            GameplayAbilities = [new() { PackagePath = "/Game/Global/AI/Abilities/GA_AI_BladeMeleeGTSM" }]
        };
        var library = FightingStyleLibraryService.Build([sword, sword]);
        Check(library.Count(x => x.Source == sword) == 1 && library.Single(x => x.Source == sword).Profile is null &&
              !FightingStyleLibraryService.IsCandidate(new() { PackagePath = "/Game/Global/AI/Abilities/AS_BruteStartingStats" }),
            "enemy sword sources are deduplicated and inspect-only; stat sets are not fighting styles", failures, output);
        Check(FightingStyleLibraryService.IsCandidate(new()
            {
                PackagePath = "/DLC_Test/Combat/AS_NewStyle",
                GameplayAbilities = [new() { PackagePath = "/DLC_Test/Combat/GA_MeleeAttack_NewStyle" }]
            }), "DLC-mounted melee sources are discovered through their grants, not just /Game folders", failures, output);
        var switching = new AbilityLoadoutProfile();
        foreach (var style in styles)
        {
            AbilityDependencyService.ApplyFightingStyle(switching, style);
            var activeCombat = switching.AbilitySets.Where(x => x.Enabled && AbilityDependencyService.IsCombatSet(x.PackagePath)).ToList();
            Check(activeCombat.Count == 1 && activeCombat[0].PackagePath == style.MeleeAbilitySetPackage,
                $"switching to {style.Id} keeps exactly one combat style", failures, output);
        }
        var robinStyle = FightingStyleProfileService.Find("robin-dual-sticks")!;
        var swordDefaults = new SwordCombatSettings();
        Check(GameAssetRefreshService.AllCharacterFilters.Contains(GameAssetRefreshService.KatanaMeshFilter) &&
              GameAssetRefreshService.AllCharacterFilters.Contains(GameAssetRefreshService.KatanaMaterialFilter) &&
              GameAssetRefreshService.AllCharacterFilters.Contains(GameAssetRefreshService.KatanaTextureFilter),
            "first-time and full-refresh filters include sword mesh, material and texture donors", failures, output);
        Check(swordDefaults.Visibility == HeldWeaponVisibility.WhileAttacking && swordDefaults.AttackSpeed == 1.5f &&
              !swordDefaults.RequiresCombatTarget && SwordCombatService.ValidateSettings(swordDefaults).Count == 0,
            "sword defaults use attack-only visibility, tested 1.5x timing and target-free swings", failures, output);
        Check(SwordCombatService.VisibilityTags(HeldWeaponVisibility.WhileAttacking).SequenceEqual(["Abilities.Combat.MeleeAttack.GTSM"]) &&
              SwordCombatService.VisibilityTags(HeldWeaponVisibility.InCombat).Contains("Status.InCombat") &&
              SwordCombatService.VisibilityTags(HeldWeaponVisibility.Always).SequenceEqual(["Status.Batons.Request"]),
            "attack-only sword visibility does not grant a permanent held-item request", failures, output);
        Check(SwordCombatService.ValidateSettings(new() { AttackSpeed = float.NaN }).Count > 0 &&
              SwordCombatService.ValidateSettings(new() { AttackSpeed = 20 }).Count > 0 &&
              SwordCombatService.ValidateSettings(new() { MeshPackage = "/Game/../escape" }).Count > 0 &&
              SwordCombatService.ValidateSettings(new() { AttackMontages = [] }).Count > 0,
            "sword settings reject invalid speeds, traversal paths and incomplete animation chains", failures, output);
        var swordLoadout = new AbilityLoadoutProfile { FightingStyleId = SwordCombatService.StyleId, SwordCombat = swordDefaults };
        var swordFingerprint = AbilityLoadoutService.ConfigurationFingerprint(swordLoadout);
        foreach (var change in new Action<SwordCombatSettings>[] {
            value => value.Visibility = HeldWeaponVisibility.Always,
            value => value.AttackSpeed = 1.1f,
            value => value.RequiresCombatTarget = true,
            value => value.MeshPackage = "/Game/Test/SM_Weapon",
            value => value.MaterialPackage = "/Game/Test/MI_Weapon",
            value => value.AttackMontages[0] = "/Game/Test/AM_Attack" })
        {
            var changed = AbilityExplorerForm.CloneProfile(swordLoadout);
            change(changed.SwordCombat!);
            Check(AbilityLoadoutService.ConfigurationFingerprint(changed) != swordFingerprint,
                "each sword setting participates in generated-asset invalidation", failures, output);
        }
        var swordCopy = AbilityExplorerForm.CloneProfile(swordLoadout);
        var recipeProfile = AbilityExplorerForm.CloneProfile(swordLoadout);
        recipeProfile.SwordCombat!.CustomModel = new WeaponModelRecipe { SourceName = "test.obj", ObjText = "v 0 0 0\nv 1 0 0\nv 0 2 0\nf 1 2 3\n",
            Scale = .5f, Materials = [new() { Slot = 0, SourceMaterialName = "blade", MaterialPath = "/Game/Test/MI_Blade" }] };
        var recipeCopy = AbilityExplorerForm.CloneProfile(recipeProfile);
        recipeCopy.SwordCombat!.CustomModel!.Materials[0].MaterialPath = "/Game/Test/MI_Other";
        recipeCopy.SwordCombat.CustomModel.X = 3;
        Check(recipeProfile.SwordCombat.CustomModel.X == 0 && recipeProfile.SwordCombat.CustomModel.Materials[0].MaterialPath == "/Game/Test/MI_Blade",
            "weapon model editor clones transforms and material slots without mutating the saved suit", failures, output);
        var recipeReload = System.Text.Json.JsonSerializer.Deserialize<AbilityLoadoutProfile>(System.Text.Json.JsonSerializer.Serialize(recipeProfile))!;
        Check(recipeReload.SwordCombat!.CustomModel!.ObjText == recipeProfile.SwordCombat.CustomModel.ObjText &&
            AbilityLoadoutService.ConfigurationFingerprint(recipeCopy) != AbilityLoadoutService.ConfigurationFingerprint(recipeProfile) &&
            SwordCombatService.ValidateSettings(recipeReload.SwordCombat).Count == 0,
            "weapon source persists in project JSON and alignment/material edits invalidate cooking", failures, output);
        swordCopy.SwordCombat!.AttackSpeed = 1.2f;
        swordCopy.SwordCombat.AttackMontages[0] = "/Game/Test/AM_Alternate";
        Check(swordLoadout.SwordCombat.AttackSpeed == 1.5f && swordLoadout.SwordCombat.AttackMontages[0] != swordCopy.SwordCombat.AttackMontages[0] &&
              AbilityLoadoutService.ConfigurationFingerprint(swordCopy) != swordFingerprint,
            "sword editor uses private copies and settings invalidate generated-asset cache", failures, output);
        var roundTripSword = System.Text.Json.JsonSerializer.Deserialize<AbilityLoadoutProfile>(System.Text.Json.JsonSerializer.Serialize(swordCopy))!;
        Check(AbilityLoadoutService.ConfigurationFingerprint(roundTripSword) == AbilityLoadoutService.ConfigurationFingerprint(swordCopy),
            "sword settings survive project JSON roundtrip", failures, output);
        AbilityDependencyService.ApplyFightingStyle(swordCopy, FightingStyleProfileService.Find("batman-martial-arts")!);
        Check(swordCopy.SwordCombat is null && swordCopy.FightingStyleId == "batman-martial-arts",
            "switching away from the sword clears its saved adapter settings", failures, output);
        Check(robinStyle.RequiredLayerSlices.All(slice => slice.AdditionalContextTags?.Contains("Animation.Equipment.Batons") == true),
            "foreign Robin pose layers are gated by held batons, not ordinary alert/stealth alone", failures, output);
        Check(
            !AbilityCatalogService.KeepAbilitySetCandidate(
                isShippedAbilitySet: false,
                inspectedAsAbilitySet: false) &&
            AbilityCatalogService.KeepAbilitySetCandidate(
                isShippedAbilitySet: false,
                inspectedAsAbilitySet: true) &&
            AbilityCatalogService.KeepAbilitySetCandidate(
                isShippedAbilitySet: true,
                inspectedAsAbilitySet: false),
            "ability discovery excludes AS_-prefixed non-AbilitySet assets while retaining real DLC and unavailable shipped entries",
            failures,
            output);

        const string characterCore = "/Game/Characters/Abilities/CoreAbilities/AS_CharacterCoreAbilitySet";
        const string playableCore = "/Game/Characters/Abilities/CoreAbilities/AS_PlayableCoreAbilitySet";
        const string batmanMelee = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Batman";
        const string traversal = "/Game/Characters/Abilities/Traversal/AS_TestTraversal";
        const string requiredEquipment = "/Game/Characters/Equipment/Test/AS_TestEquipment";
        var donorSets = new[] { characterCore, batmanMelee, playableCore };
        var profile = new AbilityLoadoutProfile
        {
            DonorDprdPackage = "/Game/Characters/Minifig/Batman/DA_DPRD_TheBatmanCharacterData",
            DonorAbilitySetFingerprint = AbilityLoadoutService.Fingerprint(donorSets),
            DonorAbilitySetPackages = donorSets.ToList(),
            AbilitySets =
            {
                new AbilitySetSelection { PackagePath = traversal, Enabled = true, Order = 0 },
                new AbilitySetSelection { PackagePath = batmanMelee, Enabled = true, Order = 1 },
                new AbilitySetSelection { PackagePath = traversal, Enabled = true, Order = 2 },
            },
        };

        var safe = AbilityLoadoutService.Resolve(donorSets, profile, new[] { requiredEquipment, requiredEquipment });
        Check(
            safe.SequenceEqual(
                new[] { characterCore, traversal, playableCore, batmanMelee, requiredEquipment },
                StringComparer.OrdinalIgnoreCase),
            "ability loadouts preserve user order, de-duplicate sets, restore protected core sets, and retain required equipment sets",
            failures,
            output);

        profile.AllowUnsafeCoreEdits = true;
        var unsafeResult = AbilityLoadoutService.Resolve(donorSets, profile);
        Check(
            unsafeResult.SequenceEqual(new[] { traversal, batmanMelee }, StringComparer.OrdinalIgnoreCase),
            "the explicit unsafe-core unlock permits advanced users to remove protected donor sets",
            failures,
            output);

        Check(
            AbilityLoadoutService.DonorMatches(
                profile,
                profile.DonorDprdPackage,
                donorSets) &&
            !AbilityLoadoutService.DonorMatches(
                profile,
                profile.DonorDprdPackage,
                donorSets.Reverse().ToList()),
            "saved ability loadouts are bound to the exact ordered donor AbilitySet revision",
            failures,
            output);
        var missingFingerprint = AbilityExplorerForm.CloneProfile(profile);
        missingFingerprint.DonorAbilitySetFingerprint = "";
        Check(
            !AbilityLoadoutService.DonorMatches(
                missingFingerprint,
                missingFingerprint.DonorDprdPackage,
                donorSets),
            "edited ability loadouts with a blank donor fingerprint require an explicit remap",
            failures,
            output);

        var beforeGrantEdit = AbilityLoadoutService.ConfigurationFingerprint(profile);
        profile.AbilitySets[1].AddedGameplayAbilities.Add(new CustomGameplayAbilityGrant
        {
            PackagePath = "/Game/Characters/Abilities/Test/GA_TestAbility",
            AbilityLevel = 3,
            InputTag = "InputTag.Ability.Test",
        });
        var afterGrantEdit = AbilityLoadoutService.ConfigurationFingerprint(profile);
        var project = new NativeSuitProject { AbilityLoadout = profile, UseCustomArchetype = false };
        Check(
            !beforeGrantEdit.Equals(afterGrantEdit, StringComparison.Ordinal) &&
            AnimArchetypeGraftService.RequiresCustomArchetype(project) &&
            MainForm.ProjectRequiresCompletedGraftStage(project),
            "grant edits invalidate the stage and automatically require the mod-local archetype/DPRD pipeline",
            failures,
            output);

        var nightwing = FightingStyleProfileService.Find("nightwing-dual-sticks");
        Check(
            nightwing is not null &&
            nightwing.HeldItemAbilityPackages.Any(path => path.EndsWith("GA_Item_Batons", StringComparison.OrdinalIgnoreCase)) &&
            nightwing.SupportingAbilitySetPackages.Any(path => path.EndsWith("AS_StaffInteractions_Electric", StringComparison.OrdinalIgnoreCase)) &&
            nightwing.RequiredAnimationBlueprintPackages.Count > 0 &&
            nightwing.RequiredStateMachinePackages.Count > 0,
            "Nightwing's dual sticks are cataloged as an atomic ability, held-item, and animation bundle",
            failures,
            output);

        var traversalSet = "/Game/Characters/Abilities/CoreAbilities/Gliding/AS_Gliding";
        var donorBatmanMelee = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Batman";
        var styleLoadout = new AbilityLoadoutProfile
        {
            AbilitySets =
            {
                new AbilitySetSelection { PackagePath = donorBatmanMelee, Enabled = true, Order = 0 },
                new AbilitySetSelection { PackagePath = traversalSet, Enabled = true, Order = 1 },
            },
        };
        if (nightwing is not null)
        {
            AbilityDependencyService.ApplyFightingStyle(
                styleLoadout,
                nightwing,
                new[] { donorBatmanMelee, traversalSet });
        }
        Check(
            AbilityDependencyService.CardinalityForSet(donorBatmanMelee) == AbilitySetCardinality.OneCombatStyle &&
            AbilityDependencyService.CardinalityForSet(traversalSet) == AbilitySetCardinality.Additive &&
            styleLoadout.AbilitySets.Count(selection =>
                selection.Enabled && AbilityDependencyService.IsCombatSet(selection.PackagePath)) == 1 &&
            styleLoadout.AbilitySets.Any(selection =>
                selection.Enabled && selection.PackagePath.Equals(traversalSet, StringComparison.OrdinalIgnoreCase)) &&
            styleLoadout.AbilitySets.Any(selection =>
                selection.Enabled && selection.PackagePath.EndsWith("AS_StaffInteractions_Electric", StringComparison.OrdinalIgnoreCase)),
            "fighting-style presets replace the one exclusive combat set while preserving additive traversal and installing supporting sets",
            failures,
            output);

        var styleProject = new NativeSuitProject
        {
            BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
            AbilityLoadout = styleLoadout,
        };
        var stylePlan = AbilityDependencyService.Build(styleProject, "Batman", Array.Empty<GameDataEquipment>());
        Check(
            nightwing is not null &&
            !stylePlan.HasErrors &&
            stylePlan.FightingStyle?.Id == nightwing.Id &&
            stylePlan.RequiredAbilitySets.Contains(nightwing.MeleeAbilitySetPackage, StringComparer.OrdinalIgnoreCase) &&
            stylePlan.RequiredAbilitySets.Contains(
                "/Game/Characters/Abilities/CoreAbilities/AS_StaffInteractions_Electric",
                StringComparer.OrdinalIgnoreCase) &&
            stylePlan.RequiredGameplayAbilities.Contains(
                "/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_Batons",
                StringComparer.OrdinalIgnoreCase) &&
            stylePlan.RequiredGameplayEffects.Contains(nightwing.CombatTypeEffectPackage, StringComparer.OrdinalIgnoreCase) &&
            stylePlan.RequiredMontageAnimSets.Any(path => path.EndsWith("MAS_Interaction_Staff", StringComparison.OrdinalIgnoreCase)) &&
            stylePlan.RequiredLayerAnimSets.Any(path => path.EndsWith("LAS_DEPRECATED_StaffInteractions", StringComparison.OrdinalIgnoreCase)) &&
            stylePlan.AnimationReplacements.Any(replacement =>
                replacement.Kind == "Montage" &&
                replacement.ReplacementPackage.EndsWith("MAS_Combat_Flurry_Nightwing", StringComparison.OrdinalIgnoreCase)) &&
            stylePlan.RequiredLayerSlices.Count == 1 &&
            stylePlan.RequiredLayerSlices[0].SourcePackage.EndsWith(
                "LAS_Default_Nightwing",
                StringComparison.OrdinalIgnoreCase) &&
            stylePlan.RequiredLayerSlices[0].RequiredContextTags.SequenceEqual(
                new[] { "Animation.Equipment.Batons" },
                StringComparer.OrdinalIgnoreCase) &&
            stylePlan.AnimationReplacements.All(replacement =>
                replacement.DonorSetPrefix != "LAS_Default_") &&
            !stylePlan.LayerAnimSetsToRemove.Any(package =>
                UnrealPathUtil.AssetName(package).StartsWith("LAS_Default_", StringComparison.OrdinalIgnoreCase)) &&
            !stylePlan.MontageAnimSetsToRemove.Any(package =>
                UnrealPathUtil.AssetName(package).StartsWith("MAS_Combat_Flurry_", StringComparison.OrdinalIgnoreCase)),
            "Nightwing's smart style plan closes melee, batons, combat effect, and combat/held-item animations without replacing the donor default layer",
            failures,
            output);

        var batarang = new GameDataEquipment
        {
            Name = "Batarang",
            EtaPackage = "/Game/Characters/Equipment/Batarang/DA_ETA_Batarang",
            EdPackage = "/Game/Characters/Equipment/Batarang/BP_Batarang_ED",
            MontageAnimSet = "/Game/Animation/MontageAnimSets/Equipment/MAS_Equipment_Batarang",
            NativeFamilies = new List<string> { "Batman" },
            VisualAbilities = new List<string>
            {
                "/Game/Characters/Equipment/Batarang/Abilities/GA_FireWeapon_Batarang",
            },
        };
        var unsupportedGrant = new NativeSuitProject
        {
            BaseProfile = new SuitBaseProfile { GameplayFamily = "Catwoman" },
            AbilityLoadout = new AbilityLoadoutProfile
            {
                DonorAbilitySetPackages = new List<string>
                {
                    "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_CatWoman",
                    "/Game/Characters/Minifig/Catwoman/AS_Catwoman",
                },
                AbilitySets =
                {
                    new AbilitySetSelection
                    {
                        PackagePath = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_CatWoman",
                        Enabled = true,
                        Order = 0,
                    },
                    new AbilitySetSelection
                    {
                        PackagePath = "/Game/Characters/Minifig/Catwoman/AS_Catwoman",
                        Enabled = true,
                        Order = 1,
                        AddedGameplayAbilities = new List<CustomGameplayAbilityGrant>
                        {
                            new() { PackagePath = batarang.VisualAbilities[0] },
                        },
                    },
                },
            },
        };
        var missingEquipmentPlan = AbilityDependencyService.Build(
            unsupportedGrant,
            "Catwoman",
            new[] { batarang });
        unsupportedGrant.EquipmentSlots.Add(new EquipmentSlotChange { Slot = 0, Gadget = "Batarang" });
        var completeEquipmentPlan = AbilityDependencyService.Build(
            unsupportedGrant,
            "Catwoman",
            new[] { batarang });
        Check(
            missingEquipmentPlan.HasErrors &&
            missingEquipmentPlan.Issues.Any(issue => issue.Message.Contains("does not equip", StringComparison.OrdinalIgnoreCase) ||
                                                        issue.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase)) &&
            completeEquipmentPlan.HasErrors &&
            completeEquipmentPlan.Issues.Any(issue =>
                issue.Message.Contains("donor DPRD Equipment", StringComparison.OrdinalIgnoreCase)) &&
            completeEquipmentPlan.RequiredGameplayAbilities.Contains(
                batarang.VisualAbilities[0],
                StringComparer.OrdinalIgnoreCase) &&
            !completeEquipmentPlan.GameplayAbilitiesToBridge.Contains(
                batarang.VisualAbilities[0],
                StringComparer.OrdinalIgnoreCase) &&
            completeEquipmentPlan.RequiredMontageAnimSets.Contains(
                batarang.MontageAnimSet,
                StringComparer.OrdinalIgnoreCase),
            "equipment edits fail closed without authoritative donor DPRD slots while still projecting a non-duplicated ED/ETA dependency closure",
            failures,
            output);

        var removedEquipmentAbility = new NativeSuitProject
        {
            BaseProfile = new SuitBaseProfile { GameplayFamily = "Catwoman" },
            AbilityLoadout = new AbilityLoadoutProfile
            {
                DonorAbilitySetPackages = new List<string>
                {
                    "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_CatWoman",
                    "/Game/Characters/Minifig/Catwoman/AS_Catwoman",
                },
                AbilitySets =
                {
                    new AbilitySetSelection
                    {
                        PackagePath = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_CatWoman",
                        Enabled = true,
                        Order = 0,
                    },
                    new AbilitySetSelection
                    {
                        PackagePath = "/Game/Characters/Minifig/Catwoman/AS_Catwoman",
                        Enabled = true,
                        Order = 1,
                        RemovedGameplayAbilities = new List<string> { batarang.VisualAbilities[0] },
                    },
                },
            },
        };
        var unknownEquipmentRemovalPlan = AbilityDependencyService.Build(
            removedEquipmentAbility,
            "Catwoman",
            new[] { batarang });
        removedEquipmentAbility.EquipmentSlots.Add(new EquipmentSlotChange { Slot = 0, Gadget = "Batarang" });
        var activeEquipmentRemovalPlan = AbilityDependencyService.Build(
            removedEquipmentAbility,
            "Catwoman",
            new[] { batarang });
        Check(
            unknownEquipmentRemovalPlan.HasErrors &&
            unknownEquipmentRemovalPlan.Issues.Any(issue =>
                issue.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase)) &&
            activeEquipmentRemovalPlan.HasErrors &&
            activeEquipmentRemovalPlan.Issues.Any(issue =>
                issue.Message.Contains("cannot be removed", StringComparison.OrdinalIgnoreCase)),
            "equipment abilities cannot be removed while their item is active or while exact donor equipment is unknown",
            failures,
            output);

        var mixedCombat = AbilityExplorerForm.CloneProfile(styleLoadout);
        mixedCombat.AbilitySets.Add(new AbilitySetSelection
        {
            PackagePath = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_CatWoman",
            Enabled = true,
            Order = 99,
        });
        var mixedPlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
                AbilityLoadout = mixedCombat,
            },
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            mixedPlan.HasErrors &&
            mixedPlan.Issues.Any(issue => issue.Message.Contains("only one melee", StringComparison.OrdinalIgnoreCase)),
            "manual loadouts cannot mix multiple combat/fighting styles",
            failures,
            output);

        var catwoman = FightingStyleProfileService.Find("catwoman-agile");
        if (catwoman is not null)
        {
            AbilityDependencyService.ApplyFightingStyle(
                styleLoadout,
                catwoman,
                new[] { donorBatmanMelee, traversalSet });
        }
        Check(
            catwoman is not null &&
            styleLoadout.AbilitySets.Count(selection =>
                selection.Enabled && AbilityDependencyService.IsCombatSet(selection.PackagePath)) == 1 &&
            styleLoadout.AbilitySets.All(selection =>
                !selection.Enabled ||
                !selection.PackagePath.EndsWith("AS_StaffInteractions_Electric", StringComparison.OrdinalIgnoreCase)) &&
            styleLoadout.AbilitySets.Any(selection =>
                selection.Enabled && selection.PackagePath.Equals(traversalSet, StringComparison.OrdinalIgnoreCase)),
            "switching fighting-style presets removes prior style-only support while preserving traversal",
            failures,
            output);

        var catwomanStylePlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
                AbilityLoadout = AbilityExplorerForm.CloneProfile(styleLoadout),
            },
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            catwomanStylePlan.MontageAnimSetsToRemove.Any(package =>
                package.EndsWith("MAS_Interaction_Staff", StringComparison.OrdinalIgnoreCase)) &&
            catwomanStylePlan.MontageAnimSetsToRemove.Any(package =>
                package.EndsWith("MAS_StatusEffect_ElectricityBatons", StringComparison.OrdinalIgnoreCase)) &&
            catwomanStylePlan.LayerAnimSetsToRemove.Any(package =>
                package.EndsWith("LAS_DEPRECATED_StaffInteractions", StringComparison.OrdinalIgnoreCase)) &&
            styleLoadout.AbilitySets.All(selection =>
                !selection.Enabled ||
                !selection.PackagePath.EndsWith("AS_StaffInteractions_Electric", StringComparison.OrdinalIgnoreCase)),
            "Nightwing to Catwoman removes the outgoing staff/baton AbilitySet and exact MAS/LAS dependencies",
            failures,
            output);

        var conflictingStyleAnimations = new NativeSuitProject
        {
            BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
            AbilityLoadout = AbilityExplorerForm.CloneProfile(styleLoadout),
            AnimationOverrides =
            {
                new AnimSetOverride
                {
                    Kind = "Layer",
                    Category = "Locomotion (idle/walk/run)",
                    DonorSet = "LAS_Default_Batman",
                    ReplacementPackage = "/Game/Animation/LayerAnimSets/Default/LAS_Default_Nightwing",
                },
            },
        };
        var conflictingStyleAnimationPlan = AbilityDependencyService.Build(
            conflictingStyleAnimations,
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            !conflictingStyleAnimationPlan.HasErrors &&
            conflictingStyleAnimationPlan.AnimationReplacements.All(replacement =>
                replacement.DonorSetPrefix != "LAS_Default_"),
            "a cross-family fighting style preserves an independently selected locomotion/default layer",
            failures,
            output);

        var twoGrapples = new AbilityLoadoutProfile
        {
            DonorAbilitySetPackages = new List<string> { donorBatmanMelee },
            AbilitySets =
            {
                new AbilitySetSelection { PackagePath = donorBatmanMelee, Enabled = true, Order = 0 },
                new AbilitySetSelection
                {
                    PackagePath = "/Game/Characters/Abilities/CoreAbilities/Grappling/GameplayDataSets/AS_GrappleData_Batman",
                    Enabled = true,
                    Order = 1,
                },
                new AbilitySetSelection
                {
                    PackagePath = "/Game/Characters/Abilities/CoreAbilities/Grappling/GameplayDataSets/AS_GrappleData_Batgirl",
                    Enabled = true,
                    Order = 2,
                },
            },
        };
        var grapplePlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
                AbilityLoadout = twoGrapples,
            },
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            grapplePlan.HasErrors &&
            grapplePlan.Issues.Any(issue => issue.Message.Contains("one grapple-data", StringComparison.OrdinalIgnoreCase)),
            "grapple profiles are mutually exclusive while other traversal sets remain additive",
            failures,
            output);

        var sameFamilyUnknownDonor = new NativeSuitProject
        {
            BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
            EquipmentSlots = { new EquipmentSlotChange { Slot = 0, Gadget = "Batarang" } },
        };
        var sameFamilyPlan = AbilityDependencyService.Build(
            sameFamilyUnknownDonor,
            "Batman",
            new[] { batarang });
        Check(
            sameFamilyPlan.RequiredGameplayAbilities.Contains(
                batarang.VisualAbilities[0],
                StringComparer.OrdinalIgnoreCase) &&
            sameFamilyPlan.GameplayAbilitiesToBridge.Contains(
                batarang.VisualAbilities[0],
                StringComparer.OrdinalIgnoreCase) &&
            sameFamilyPlan.RequiredMontageAnimSets.Contains(
                batarang.MontageAnimSet,
                StringComparer.OrdinalIgnoreCase),
            "family-wide native aggregation does not suppress dependencies when the exact donor DPRD is unavailable",
            failures,
            output);

        var directStaff = new AbilityLoadoutProfile
        {
            DonorAbilitySetPackages = new List<string> { donorBatmanMelee },
            AbilitySets =
            {
                new AbilitySetSelection { PackagePath = donorBatmanMelee, Enabled = true, Order = 0 },
                new AbilitySetSelection
                {
                    PackagePath = "/Game/Characters/Abilities/CoreAbilities/AS_StaffInteractions_Electric",
                    Enabled = true,
                    Order = 1,
                },
            },
        };
        var directStaffPlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
                AbilityLoadout = directStaff,
            },
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            directStaffPlan.HasErrors &&
            directStaffPlan.Issues.Any(issue => issue.Message.Contains("managed by", StringComparison.OrdinalIgnoreCase)),
            "style-support sets cannot bypass their atomic fighting-style preset",
            failures,
            output);

        var drone = new GameDataEquipment
        {
            Name = "Drone",
            EtaPackage = "/Game/Characters/Equipment/Drone/DA_ETA_Drone",
            EdPackage = "/Game/Characters/Equipment/Drone/BP_Drone_ED",
            NativeFamilies = new List<string> { "Batgirl" },
        };
        var dronePlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batgirl" },
                EquipmentSlots = { new EquipmentSlotChange { Slot = 0, Gadget = "Drone" } },
            },
            "Batgirl",
            new[] { drone });
        Check(
            dronePlan.EquipmentOwnedAbilitySets.Any(path =>
                path.EndsWith("AS_DroneUser", StringComparison.OrdinalIgnoreCase)) &&
            !dronePlan.RequiredAbilitySets.Any(path =>
                path.EndsWith("AS_DroneUser", StringComparison.OrdinalIgnoreCase)) &&
            dronePlan.GameplayAbilitiesToBridge.Count == 0,
            "equipment ED AbilitySetsToGrant stays equipment-owned and is not duplicated in DPRD or the character bridge",
            failures,
            output);
        var directDroneController = new NativeSuitProject
        {
            BaseProfile = new SuitBaseProfile { GameplayFamily = "Batgirl" },
            EquipmentSlots = { new EquipmentSlotChange { Slot = 0, Gadget = "Drone" } },
            AbilityLoadout = new AbilityLoadoutProfile
            {
                DonorAbilitySetPackages = new List<string> { donorBatmanMelee },
                AbilitySets =
                {
                    new AbilitySetSelection { PackagePath = donorBatmanMelee, Enabled = true, Order = 0 },
                    new AbilitySetSelection
                    {
                        PackagePath = "/Game/Characters/Equipment/Drone/Abilities/AS_DroneUser",
                        Enabled = true,
                        Order = 1,
                    },
                },
            },
        };
        var directDronePlan = AbilityDependencyService.Build(
            directDroneController,
            "Batgirl",
            new[] { drone });
        Check(
            directDronePlan.HasErrors && directDronePlan.Issues.Any(issue =>
                issue.Message.Contains("owned and granted", StringComparison.OrdinalIgnoreCase)) &&
            AbilityDependencyService.AddedSetCompatibilityError(
                directDroneController,
                "/Game/Characters/Equipment/Drone/Abilities/AS_DroneUser",
                new[] { drone }) is not null,
            "equipment-owned controller sets remain blocked from DPRD even while their ED is equipped",
            failures,
            output);

        var tamperedNightwing = new AbilityLoadoutProfile
        {
            AbilitySets =
            {
                new AbilitySetSelection { PackagePath = donorBatmanMelee, Enabled = true, Order = 0 },
            },
        };
        if (nightwing is not null)
        {
            AbilityDependencyService.ApplyFightingStyle(tamperedNightwing, nightwing);
            tamperedNightwing.AbilitySets.First(selection =>
                    selection.PackagePath.EndsWith("AS_StaffInteractions_Electric", StringComparison.OrdinalIgnoreCase))
                .RemovedGameplayAbilities.Add(
                    "/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_Batons");
        }
        var tamperedNightwingPlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
                AbilityLoadout = tamperedNightwing,
            },
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            nightwing is not null && tamperedNightwingPlan.HasErrors &&
            tamperedNightwingPlan.Issues.Any(issue =>
                issue.Message.Contains("cannot be removed", StringComparison.OrdinalIgnoreCase)),
            "required held-item grants fail closed instead of claiming an unapplied removal will be restored",
            failures,
            output);

        var noCombatPlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
                AbilityLoadout = new AbilityLoadoutProfile
                {
                    DonorAbilitySetPackages = new List<string> { donorBatmanMelee },
                    AbilitySets =
                    {
                        new AbilitySetSelection { PackagePath = traversalSet, Enabled = true, Order = 0 },
                    },
                },
            },
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            noCombatPlan.HasErrors && noCombatPlan.Issues.Any(issue =>
                issue.Message.Contains("exactly one melee", StringComparison.OrdinalIgnoreCase)),
            "edited playable loadouts cannot remove every combat style",
            failures,
            output);

        var foreignCharacterSetPlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Batman" },
                AbilityLoadout = new AbilityLoadoutProfile
                {
                    DonorAbilitySetPackages = new List<string> { donorBatmanMelee },
                    AbilitySets =
                    {
                        new AbilitySetSelection { PackagePath = donorBatmanMelee, Enabled = true, Order = 0 },
                        new AbilitySetSelection
                        {
                            PackagePath = "/Game/Characters/Minifig/Catwoman/AS_Catwoman",
                            Enabled = true,
                            Order = 1,
                        },
                    },
                },
            },
            "Batman",
            Array.Empty<GameDataEquipment>());
        Check(
            foreignCharacterSetPlan.HasErrors && foreignCharacterSetPlan.Issues.Any(issue =>
                issue.Message.Contains("complete foreign character set", StringComparison.OrdinalIgnoreCase)),
            "foreign character AbilitySets cannot bypass the selective fighting-style bridge",
            failures,
            output);

        var exactNightwingDonorPackages = new List<string>
        {
            "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Nightwing",
            "/Game/Characters/Abilities/CoreAbilities/AS_StaffInteractions_Electric",
            "/Game/Characters/Minifig/Nightwing/AS_Nightwing",
        };
        var exactNightwingDonorPlan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                // Deliberately use a different broad family label. Exact DPRD provenance, not a
                // family-wide heuristic, proves these are inherited and safe to retain.
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Robin" },
                AbilityLoadout = new AbilityLoadoutProfile
                {
                    DonorAbilitySetPackages = exactNightwingDonorPackages.ToList(),
                    AbilitySets = exactNightwingDonorPackages.Select((package, index) =>
                        new AbilitySetSelection
                        {
                            PackagePath = package,
                            Enabled = true,
                            Order = index,
                        }).ToList(),
                },
            },
            "Robin",
            Array.Empty<GameDataEquipment>());
        Check(
            !exactNightwingDonorPlan.HasErrors,
            "exact donor DPRD provenance retains inherited melee, support, and character sets even when a broad family label differs",
            failures,
            output);
    }

    private static void Check(
        bool condition,
        string description,
        ICollection<string> failures,
        TextWriter output)
    {
        output.WriteLine($"{(condition ? "PASS" : "FAIL")}: {description}");
        if (!condition)
        {
            failures.Add(description);
        }
    }
}
