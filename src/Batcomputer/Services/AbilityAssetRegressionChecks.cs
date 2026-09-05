using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Staged-copy regression coverage for exact DPRD AbilitySet mutation and individual gameplay-
/// ability grants. The real-asset portion is optional so the portable executable can still run its
/// release checks before a game extraction has been configured.
/// </summary>
internal static class AbilityAssetRegressionChecks
{
    public static void Run(List<string> failures, TextWriter output)
    {
        var boundaryRoot = Path.Combine(Path.GetTempPath(), "Batcomputer-ability-root");
        Check(
            AbilityAssetMutationService.IsUnderRootForTest(
                Path.Combine(boundaryRoot, "Characters", "AS_Test.uasset"),
                boundaryRoot) &&
            !AbilityAssetMutationService.IsUnderRootForTest(
                boundaryRoot + "-sibling" + Path.DirectorySeparatorChar + "AS_Test.uasset",
                boundaryRoot),
            "ability mutation read-only root checks use a directory boundary",
            failures,
            output);

        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
        var dprdSource = Path.Combine(
            extractedRoot,
            "Characters", "Minifig", "Batman", "DA_DPRD_TheBatmanCharacterData.uasset");
        var abilitySetSource = Path.Combine(
            extractedRoot,
            "Characters", "Abilities", "MeleeAbilities", "AS_Melee_Batman.uasset");
        var characterAbilitySetSource = Path.Combine(
            extractedRoot,
            "Characters", "Minifig", "Batman", "Abilities", "AS_Batman.uasset");
        var catwomanAbilitySetSource = Path.Combine(
            extractedRoot,
            "Characters", "Minifig", "Catwoman", "AS_Catwoman.uasset");
        if (!File.Exists(dprdSource) || !File.Exists(abilitySetSource) ||
            !File.Exists(characterAbilitySetSource) || !File.Exists(catwomanAbilitySetSource) ||
            string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath))
        {
            output.WriteLine("PASS: ability staged-copy mutation fixture skipped (game extract or .usmap unavailable)");
            return;
        }

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-ability-asset-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            var service = new AbilityAssetMutationService();

            // UAssetAPI opens split .uexp streams read/write even for parsing, so regression
            // fixtures always parse private copies and never request write access to the extract.
            var readableDprd = CopyCookedAsset(
                dprdSource,
                Path.Combine(fixtureRoot, "ReadableDonors", "DPRD"));
            var readableAbilitySet = CopyCookedAsset(
                abilitySetSource,
                Path.Combine(fixtureRoot, "ReadableDonors", "AbilitySet"));
            var readableCharacterSet = CopyCookedAsset(
                characterAbilitySetSource,
                Path.Combine(fixtureRoot, "ReadableDonors", "CharacterAbilitySet"));
            var readableCatwomanSet = CopyCookedAsset(
                catwomanAbilitySetSource,
                Path.Combine(fixtureRoot, "ReadableDonors", "CatwomanAbilitySet"));
            var donorDprd = service.InspectDprdAbilitySets(readableDprd);
            Check(
                donorDprd.Success && donorDprd.AbilitySets.Count > 0 &&
                donorDprd.AbilitySets.Select(entry => entry.PackagePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() == donorDprd.AbilitySets.Count,
                "Batman DPRD exposes an ordered list of exact AbilitySet packages",
                failures,
                output);
            var donorEquipment = service.InspectDprdEquipment(readableDprd);
            Check(
                donorEquipment.Success &&
                donorEquipment.Equipment.Select(entry => entry.Index)
                    .SequenceEqual(Enumerable.Range(0, donorEquipment.Equipment.Count)),
                "DPRD runtime equipment inspection preserves exact order and null slot indices",
                failures,
                output);
            RunBruceWayneEquipmentAuthorityCheck(extractedRoot, fixtureRoot, failures, output);

            var baseWriteBlocked = service.SetDprdAbilitySets(
                dprdSource,
                Array.Empty<string>());
            Check(
                !baseWriteBlocked.Success &&
                baseWriteBlocked.Status.Equals("base-asset-read-only", StringComparison.Ordinal),
                "ability mutation refuses to write the active base-game extraction",
                failures,
                output);

            var stagedDprd = CopyCookedAsset(dprdSource, Path.Combine(fixtureRoot, "DPRD"));
            const string sameStemA = "/Game/Mods/AbilityRegression/First/AS_SharedStem";
            const string sameStemB = "/Game/Mods/AbilityRegression/Second/AS_SharedStem";
            var ordered = new[] { sameStemB, sameStemA };
            var setResult = service.SetDprdAbilitySets(stagedDprd, ordered);
            var setInspection = service.InspectDprdAbilitySets(stagedDprd);
            Check(
                setResult.Success && setInspection.Success &&
                setInspection.AbilitySets.Select(entry => entry.PackagePath)
                    .SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase),
                "DPRD mutation preserves order and distinguishes equal asset stems in different packages",
                failures,
                output);

            const string replacement = "/Game/Mods/AbilityRegression/Replacement/AS_Replaced";
            var replaceResult = service.ReplaceDprdAbilitySet(stagedDprd, sameStemB, replacement);
            var removeResult = service.RemoveDprdAbilitySet(stagedDprd, sameStemA);
            var addResult = service.AddDprdAbilitySet(stagedDprd, sameStemA, insertIndex: 0);
            var idempotentAdd = service.AddDprdAbilitySet(stagedDprd, sameStemA, insertIndex: 1);
            var finalDprd = service.InspectDprdAbilitySets(stagedDprd);
            Check(
                replaceResult.Success && removeResult.Success && addResult.Success &&
                idempotentAdd.Success && finalDprd.Success &&
                finalDprd.AbilitySets.Select(entry => entry.PackagePath).SequenceEqual(
                    new[] { sameStemA, replacement },
                    StringComparer.OrdinalIgnoreCase),
                "exact DPRD add/remove/replace operations are ordered and idempotent",
                failures,
                output);

            var firstRuntimeEquipment = donorEquipment.Equipment.FirstOrDefault(entry => !entry.IsNull);
            if (firstRuntimeEquipment is not null)
            {
                var equipmentDprd = CopyCookedAsset(dprdSource, Path.Combine(fixtureRoot, "EquipmentDPRD"));
                var graft = new AnimGraftService();
                var replaceEquipment = graft.SetEquipmentSlot(
                    equipmentDprd,
                    firstRuntimeEquipment.Index,
                    firstRuntimeEquipment.PackagePath);
                var rejectSparse = graft.SetEquipmentSlot(
                    equipmentDprd,
                    donorEquipment.Equipment.Count + 1,
                    firstRuntimeEquipment.PackagePath);
                var equipmentAfter = service.InspectDprdEquipment(equipmentDprd);
                Check(
                    replaceEquipment.Status.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
                    !rejectSparse.Status.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
                    equipmentAfter.Success &&
                    equipmentAfter.Equipment[firstRuntimeEquipment.Index].PackagePath.Equals(
                        firstRuntimeEquipment.PackagePath,
                        StringComparison.OrdinalIgnoreCase),
                    "DPRD equipment writes reload at the exact index and reject sparse slot collapse",
                    failures,
                    output);
            }
            else
            {
                output.WriteLine("PASS: DPRD exact equipment-slot write fixture skipped (donor has no runtime equipment)");
            }

            RunGameplayAbilityChecks(service, readableAbilitySet, fixtureRoot, failures, output);
            RunGameplayEffectChecks(
                service,
                readableCharacterSet,
                readableCatwomanSet,
                fixtureRoot,
                failures,
                output);
            RunBatmanAuthoredNullParentCheck(extractedRoot, fixtureRoot, failures, output);
            RunNightwingCombatLayerSliceCheck(extractedRoot, fixtureRoot, failures, output);
            RunNightwingToCatwomanCleanup(extractedRoot, fixtureRoot, failures, output);
        }
        catch (Exception ex)
        {
            Check(
                false,
                $"ability staged-copy mutation fixture completed ({ex.Message})",
                failures,
                output);
        }
        finally
        {
            try { Directory.Delete(fixtureRoot, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }

    private static void RunNightwingCombatLayerSliceCheck(
        string extractedRoot,
        string fixtureRoot,
        List<string> failures,
        TextWriter output)
    {
        const string nightwingDefault =
            "/Game/Animation/LayerAnimSets/Default/LAS_Default_Nightwing";
        var source = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, nightwingDefault) ?? "";
        if (!File.Exists(source))
        {
            output.WriteLine("PASS: Nightwing combat-layer slice fixture skipped (animation donor unavailable)");
            return;
        }

        var staged = CopyCookedAsset(
            source,
            Path.Combine(fixtureRoot, "NightwingCombatLayerSlice"));
        var graft = new AnimGraftService();
        var filtered = graft.KeepOnlyLayerEntriesMatchingContexts(
            staged,
            new[] { "Animation.Equipment.Batons" });
        Check(
            filtered.Status.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
            filtered.Added.Count == 3 &&
            filtered.Added.All(row =>
                row.Contains("Animation.Layer.Base", StringComparison.OrdinalIgnoreCase) &&
                row.Contains("Animation.Equipment.Batons", StringComparison.OrdinalIgnoreCase)) &&
            filtered.Added.All(row =>
                !row.Contains("Animation.Layer.Default", StringComparison.OrdinalIgnoreCase) &&
                !row.Contains("Animation.Status.Perch", StringComparison.OrdinalIgnoreCase)),
            "Nightwing combat layer retains only the three baton-context rows and drops generic movement/perch rows",
            failures,
            output);
    }

    private static void RunBatmanAuthoredNullParentCheck(
        string extractedRoot,
        string fixtureRoot,
        List<string> failures,
        TextWriter output)
    {
        const string batmanLas = "/Game/Animation/LayerAnimSets/Character/LAS_Char_Batman";
        var source = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, batmanLas) ?? "";
        if (!File.Exists(source))
        {
            output.WriteLine("PASS: Batman authored-null LAS fixture skipped (animation donor unavailable)");
            return;
        }

        var staged = CopyCookedAsset(
            source,
            Path.Combine(fixtureRoot, "BatmanAuthoredNullLas"));
        var graft = new AnimGraftService();
        var before = graft.InspectParentSets(staged);
        const string staffParent =
            "/Game/Animation/LayerAnimSets/LAS_DEPRECATED_StaffInteractions";
        var injection = graft.InjectParentSets(
            staged,
            "TTLayerSet",
            new[] { staffParent });
        var after = graft.InspectParentSets(staged);

        var malformed = CopyCookedAsset(
            source,
            Path.Combine(fixtureRoot, "BatmanNonImportLas"));
        var mappingsPath = AppSettings.Current.EffectiveUsmapPath();
        var malformedAsset = new UAsset(
            malformed,
            EngineVersion.VER_UE5_6,
            MappingsCache.Load(mappingsPath!),
            CustomSerializationFlags.None);
        var malformedArray = malformedAsset.Exports.OfType<NormalExport>()
            .SelectMany(export => export.Data)
            .OfType<ArrayPropertyData>()
            .First(property => property.Name.ToString().Equals(
                "ParentSetsArray",
                StringComparison.OrdinalIgnoreCase));
        var malformedReference = (ObjectPropertyData)malformedArray.Value[0];
        malformedReference.Value = FPackageIndex.FromExport(0);
        malformedAsset.Write(malformed);
        var rejected = graft.InspectParentSets(malformed);
        Check(
            before.Success &&
            before.AuthoredNullEntries == 1 &&
            before.PackagePaths.Count == 8 &&
            before.PackagePaths.Any(package => package.EndsWith(
                "LAS_Default_Batman",
                StringComparison.OrdinalIgnoreCase)) &&
            injection.Status.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
            after.Success &&
            after.AuthoredNullEntries == 1 &&
            after.PackagePaths.Count == 9 &&
            after.PackagePaths.Contains(staffParent, StringComparer.OrdinalIgnoreCase) &&
            !rejected.Success &&
            rejected.Status.Equals("invalid-parentset", StringComparison.OrdinalIgnoreCase),
            "animation dependency inspection preserves a shipped null slot through a graft while rejecting non-null non-import parents",
            failures,
            output);
    }

    private static void RunBruceWayneEquipmentAuthorityCheck(
        string extractedRoot,
        string fixtureRoot,
        List<string> failures,
        TextWriter output)
    {
        const string playablePackage = "/Game/Characters/Minifig/BruceWayne/BP_BruceWayne_Suit_Playable";
        const string dcmdPackage = "/Game/Characters/Minifig/BruceWayne/DA_DCMD_BruceWayne_Suit_Playable";
        const string dprdPackage = "/Game/Characters/Minifig/BruceWayne/DA_DPRD_BruceWayneBatarangsCharacterData";
        var playable = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, playablePackage) ?? "";
        var dcmd = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, dcmdPackage) ?? "";
        if (!File.Exists(playable) || !File.Exists(dcmd))
        {
            output.WriteLine("PASS: Bruce Wayne DCMD/DPRD authority fixture skipped (variant unavailable)");
            return;
        }
        var readableDcmd = CopyCookedAsset(
            dcmd,
            Path.Combine(fixtureRoot, "ReadableDonors", "BruceWayneDcmd"));
        var project = new NativeSuitProject
        {
            PlayableTemplate = new TemplateRecord { PackagePath = playablePackage, Uasset = playable },
            DcmdTemplate = new TemplateRecord { PackagePath = dcmdPackage, Uasset = readableDcmd },
            AbilityLoadout = new AbilityLoadoutProfile { DonorDprdPackage = dprdPackage },
        };
        var menuSlots = new DcmdGenService("").ReadEquipmentSlots(readableDcmd);
        var runtimeKnown = AbilityDependencyService.TryReadDonorRuntimeEquipmentSlots(
            project,
            GameDataService.Instance.Db.Equipment,
            out var runtimeSlots);
        var authorityMatches =
            menuSlots.Count > 0 &&
            menuSlots[0].Contains("NinjaStar", StringComparison.OrdinalIgnoreCase) &&
            runtimeKnown && runtimeSlots.TryGetValue(0, out var runtime) &&
            runtime.Equals("Batarang", StringComparison.OrdinalIgnoreCase);
        if (!authorityMatches)
        {
            output.WriteLine(
                $"  Bruce Wayne authority diagnostic: menu=[{string.Join(", ", menuSlots)}], " +
                $"runtime-known={runtimeKnown}, runtime=[{string.Join(", ", runtimeSlots.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}]");
        }
        Check(
            authorityMatches,
            "Bruce Wayne proves DCMD menu NinjaStar metadata cannot override DPRD runtime Batarang authority",
            failures,
            output);
    }

    private static void RunNightwingToCatwomanCleanup(
        string extractedRoot,
        string fixtureRoot,
        List<string> failures,
        TextWriter output)
    {
        var robin = FightingStyleProfileService.Find("robin-dual-sticks")!;
        foreach (var slice in robin.RequiredLayerSlices)
        {
            var source = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, slice.SourcePackage) ?? "";
            if (!File.Exists(source)) continue;
            var copy = CopyCookedAsset(source, Path.Combine(fixtureRoot, "RobinLayer", slice.RequiredContextTags[0]));
            var filtered = new AnimGraftService().KeepOnlyLayerEntriesMatchingContexts(copy, slice.RequiredContextTags, slice.AdditionalContextTags);
            Check(filtered.Status == "ok" && filtered.Added.Count == 1 &&
                  filtered.Added[0].Contains("Animation.Equipment.Batons", StringComparison.Ordinal),
                "Robin's " + slice.RequiredContextTags[0] + " layer survives cooked reload with an additional held-batons condition", failures, output);
        }
        const string nightwingMas = "/Game/Animation/MontageAnimSets/Character/MAS_Char_Nightwing";
        const string nightwingLas = "/Game/Animation/LayerAnimSets/Character/LAS_Char_Nightwing";
        var sourceMas = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, nightwingMas) ?? "";
        var sourceLas = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, nightwingLas) ?? "";
        var catwoman = FightingStyleProfileService.Find("catwoman-agile");
        if (!File.Exists(sourceMas) || !File.Exists(sourceLas) || catwoman is null)
        {
            output.WriteLine("PASS: staged Nightwing-to-Catwoman cleanup fixture skipped (animation donors unavailable)");
            return;
        }

        var loadout = new AbilityLoadoutProfile
        {
            DonorAbilitySetPackages = new List<string>
            {
                "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_NightWing",
                "/Game/Characters/Abilities/CoreAbilities/AS_StaffInteractions_Electric",
                "/Game/Characters/Minifig/Nightwing/AS_Nightwing",
            },
            AbilitySets =
            {
                new AbilitySetSelection
                {
                    PackagePath = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_NightWing",
                    Enabled = true,
                    Order = 0,
                },
                new AbilitySetSelection
                {
                    PackagePath = "/Game/Characters/Abilities/CoreAbilities/AS_StaffInteractions_Electric",
                    Enabled = true,
                    Order = 1,
                },
                new AbilitySetSelection
                {
                    PackagePath = "/Game/Characters/Minifig/Nightwing/AS_Nightwing",
                    Enabled = true,
                    Order = 2,
                },
            },
        };
        AbilityDependencyService.ApplyFightingStyle(loadout, catwoman, loadout.DonorAbilitySetPackages);
        var plan = AbilityDependencyService.Build(
            new NativeSuitProject
            {
                BaseProfile = new SuitBaseProfile { GameplayFamily = "Nightwing" },
                AbilityLoadout = loadout,
            },
            "Nightwing",
            Array.Empty<GameDataEquipment>());

        var stagedMas = CopyCookedAsset(sourceMas, Path.Combine(fixtureRoot, "NightwingToCatwoman", "MAS"));
        var stagedLas = CopyCookedAsset(sourceLas, Path.Combine(fixtureRoot, "NightwingToCatwoman", "LAS"));
        var graft = new AnimGraftService();
        var operations = new List<AnimGraftService.GraftResult>
        {
            graft.RemoveParentSets(stagedMas, plan.MontageAnimSetsToRemove),
            graft.RemoveParentSets(stagedLas, plan.LayerAnimSetsToRemove),
        };
        foreach (var replacement in plan.AnimationReplacements)
        {
            operations.Add(replacement.Kind switch
            {
                "Montage" => graft.SetExclusiveParentSet(
                    stagedMas, "TTAnimSet", replacement.DonorSetPrefix, replacement.ReplacementPackage, false),
                "MontageRemove" => graft.RemoveParentSetsByPrefix(stagedMas, replacement.DonorSetPrefix),
                "Layer" => graft.SetExclusiveParentSet(
                    stagedLas, "TTLayerSet", replacement.DonorSetPrefix, replacement.ReplacementPackage, false),
                _ => new AnimGraftService.GraftResult { Status = "ok" },
            });
        }
        operations.Add(graft.InjectParentSets(stagedMas, "TTAnimSet", plan.RequiredMontageAnimSets));
        operations.Add(graft.InjectParentSets(stagedLas, "TTLayerSet", plan.RequiredLayerAnimSets));
        var masAfter = graft.InspectParentSets(stagedMas);
        var lasAfter = graft.InspectParentSets(stagedLas);
        Check(
            operations.All(result => result.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)) &&
            masAfter.Success && lasAfter.Success &&
            !masAfter.PackagePaths.Any(package =>
                package.EndsWith("MAS_Interaction_Staff", StringComparison.OrdinalIgnoreCase) ||
                package.EndsWith("MAS_StatusEffect_ElectricityBatons", StringComparison.OrdinalIgnoreCase) ||
                package.EndsWith("MAS_Combat_Flurry_Nightwing", StringComparison.OrdinalIgnoreCase)) &&
            masAfter.PackagePaths.Count(package => UnrealPathUtil.AssetName(package).StartsWith(
                "MAS_Combat_Flurry_", StringComparison.OrdinalIgnoreCase)) == 1 &&
            masAfter.PackagePaths.Any(package =>
                package.EndsWith("MAS_Combat_Flurry_Catwoman", StringComparison.OrdinalIgnoreCase)) &&
            !lasAfter.PackagePaths.Any(package =>
                package.EndsWith("LAS_DEPRECATED_StaffInteractions", StringComparison.OrdinalIgnoreCase)) &&
            lasAfter.PackagePaths.Any(package =>
                package.EndsWith("LAS_Default_Nightwing", StringComparison.OrdinalIgnoreCase)) &&
            lasAfter.PackagePaths.Any(package =>
                package.EndsWith("LAS_Default_Minifig", StringComparison.OrdinalIgnoreCase)) &&
            !lasAfter.PackagePaths.Any(package =>
                package.EndsWith("LAS_Default_Catwoman", StringComparison.OrdinalIgnoreCase)),
            "staged Nightwing-to-Catwoman swap removes staff/baton parents and preserves the donor default controller",
            failures,
            output);
    }

    private static void RunGameplayEffectChecks(
        AbilityAssetMutationService service,
        string targetSource,
        string effectSource,
        string fixtureRoot,
        List<string> failures,
        TextWriter output)
    {
        const string agileEffect =
            "/Game/Characters/Abilities/GameplayEffects/Combat/CombatTypes/GE_CombatType_Agile";
        var sourceInspection = service.InspectAbilitySet(effectSource);
        var staged = CopyCookedAsset(targetSource, Path.Combine(fixtureRoot, "EffectAbilitySet"));
        var add = service.SetExclusiveCombatTypeEffect(
            staged,
            new AbilityAssetMutationService.GameplayEffectAddition
            {
                PackagePath = agileEffect,
                SourceAbilitySetUassetPath = effectSource,
                SourceEffectPackagePath = agileEffect,
            });
        var idempotent = service.SetExclusiveCombatTypeEffect(
            staged,
            new AbilityAssetMutationService.GameplayEffectAddition { PackagePath = agileEffect });
        var inspection = service.InspectAbilitySet(staged);
        var combatEffects = inspection.GameplayEffects
            .Where(effect => AbilityAssetMutationService.IsCombatTypeEffect(effect.PackagePath))
            .ToList();
        Check(
            sourceInspection.GameplayEffects.Any(effect =>
                effect.PackagePath.Equals(agileEffect, StringComparison.OrdinalIgnoreCase)) &&
            add.Success && idempotent.Success && inspection.Success &&
            combatEffects.Count == 1 &&
            combatEffects[0].PackagePath.Equals(agileEffect, StringComparison.OrdinalIgnoreCase),
            "fighting-style bridges replace competing combat effects, preserve one exact style, and remain idempotent",
            failures,
            output);
        var unrelated = inspection.GameplayEffects.Where(effect => !AbilityAssetMutationService.IsCombatTypeEffect(effect.PackagePath))
            .Select(effect => effect.PackagePath).ToList();
        var clear = service.SetExclusiveCombatTypeEffect(staged, null);
        var clearAgain = service.SetExclusiveCombatTypeEffect(staged, null);
        var cleared = service.InspectAbilitySet(staged);
        Check(clear.Success && clearAgain.Success && cleared.Success &&
              !cleared.GameplayEffects.Any(effect => AbilityAssetMutationService.IsCombatTypeEffect(effect.PackagePath)) &&
              cleared.GameplayEffects.Select(effect => effect.PackagePath).SequenceEqual(unrelated),
            "styles with no native combat effect remove the outgoing combat tag and preserve unrelated effects", failures, output);
    }

    private static void RunGameplayAbilityChecks(
        AbilityAssetMutationService service,
        string abilitySetSource,
        string fixtureRoot,
        List<string> failures,
        TextWriter output)
    {
        var donor = service.InspectAbilitySet(abilitySetSource);
        if (!donor.Success || donor.GameplayAbilities.Count == 0)
        {
            Check(false, "Batman melee AbilitySet exposes gameplay ability grants", failures, output);
            return;
        }

        Check(
            donor.GameplayAbilities.All(grant =>
                !string.IsNullOrWhiteSpace(grant.PackagePath) &&
                grant.AbilityLevel is not null),
            "AbilitySet inspection resolves exact grant packages and gameplay metadata",
            failures,
            output);

        var stagedAbilitySet = CopyCookedAsset(
            abilitySetSource,
            Path.Combine(fixtureRoot, "AbilitySet"));
        var original = donor.GameplayAbilities[0];
        const string replacement = "/Game/Mods/AbilityRegression/GA_Replacement";
        const string replacementTag = "InputTag.Ability.Regression";
        var replace = service.ApplyGameplayAbilityEdits(
            stagedAbilitySet,
            new[]
            {
                new AbilityAssetMutationService.GameplayAbilityEdit
                {
                    Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Replace,
                    TargetPackagePath = original.PackagePath,
                    ReplacementPackagePath = replacement,
                    AbilityLevelOverride = 7,
                    InputTagOverride = replacementTag,
                },
            });
        var afterReplace = service.InspectAbilitySet(stagedAbilitySet);
        var replacedGrant = afterReplace.GameplayAbilities.FirstOrDefault(grant =>
            grant.PackagePath.Equals(replacement, StringComparison.OrdinalIgnoreCase));
        Check(
            replace.Success && afterReplace.Success && replacedGrant is not null &&
            replacedGrant.AbilityLevel == 7 && replacedGrant.InputTag == replacementTag &&
            afterReplace.GameplayAbilities.All(grant =>
                !grant.PackagePath.Equals(original.PackagePath, StringComparison.OrdinalIgnoreCase)),
            "gameplay ability replacement writes exact class, level, and InputTag metadata",
            failures,
            output);

        const string addedPackage = "/Game/Mods/AbilityRegression/GA_AddedFromDonorMetadata";
        var add = service.ApplyGameplayAbilityEdits(
            stagedAbilitySet,
            new[]
            {
                new AbilityAssetMutationService.GameplayAbilityEdit
                {
                    Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Add,
                    ReplacementPackagePath = addedPackage,
                    InsertIndex = 0,
                    SourceAbilitySetUassetPath = abilitySetSource,
                    SourceAbilityPackagePath = original.PackagePath,
                },
            });
        var afterAdd = service.InspectAbilitySet(stagedAbilitySet);
        var addedGrant = afterAdd.GameplayAbilities.FirstOrDefault(grant =>
            grant.PackagePath.Equals(addedPackage, StringComparison.OrdinalIgnoreCase));
        Check(
            add.Success && afterAdd.Success && addedGrant is not null &&
            addedGrant.Index == 0 &&
            addedGrant.AbilityLevel == original.AbilityLevel &&
            addedGrant.InputTag == original.InputTag,
            "gameplay ability add can preserve metadata from an explicitly selected donor grant",
            failures,
            output);

        var remove = service.ApplyGameplayAbilityEdits(
            stagedAbilitySet,
            new[]
            {
                new AbilityAssetMutationService.GameplayAbilityEdit
                {
                    Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Remove,
                    TargetPackagePath = replacement,
                },
            });
        var afterRemove = service.InspectAbilitySet(stagedAbilitySet);
        Check(
            remove.Success && afterRemove.Success &&
            afterRemove.GameplayAbilities.All(grant =>
                !grant.PackagePath.Equals(replacement, StringComparison.OrdinalIgnoreCase)),
            "gameplay ability removal survives a cooked-asset reload",
            failures,
            output);
    }

    private static string CopyCookedAsset(string sourceUasset, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var sourceBase = Path.Combine(
            Path.GetDirectoryName(sourceUasset)!,
            Path.GetFileNameWithoutExtension(sourceUasset));
        var destinationBase = Path.Combine(
            destinationDirectory,
            Path.GetFileNameWithoutExtension(sourceUasset));
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
        {
            var source = sourceBase + extension;
            if (File.Exists(source))
            {
                File.Copy(source, destinationBase + extension, overwrite: true);
            }
        }
        return destinationBase + ".uasset";
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
