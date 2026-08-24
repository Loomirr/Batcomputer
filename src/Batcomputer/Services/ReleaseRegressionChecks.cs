namespace Batcomputer;

/// <summary>
/// Fast, dependency-free checks for release bugs that previously reached users.
/// Kept as a CLI command so the portable executable can verify its own behavior
/// without requiring a separate test SDK or a local copy of the game.
/// </summary>
internal static class ReleaseRegressionChecks
{
    public static int Run(TextWriter output)
    {
        var failures = new List<string>();

        Check(
            GameAssetRefreshService.AllCharacterFilters.Contains(
                GameAssetRefreshService.CharacterGadgetFilter,
                StringComparer.OrdinalIgnoreCase),
            "normal refresh extracts Content/Models/Gadgets",
            failures,
            output);
        Check(
            GameAssetRefreshService.DeveloperResearchFilters.Contains(
                GameAssetRefreshService.CharacterGadgetFilter,
                StringComparer.OrdinalIgnoreCase),
            "developer refresh extracts Content/Models/Gadgets",
            failures,
            output);
        Check(
            new[]
            {
                GameAssetRefreshService.BatmanFilters,
                GameAssetRefreshService.AllCharacterFilters,
                GameAssetRefreshService.DeveloperResearchFilters,
            }.All(filters => filters.Contains(
                GameAssetRefreshService.CapeTransparentMaterialFilter,
                StringComparer.OrdinalIgnoreCase)),
            "every refresh profile extracts the shared transparent cape material",
            failures,
            output);

        var questRegressionRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-quest-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var contentRoot = Path.Combine(questRegressionRoot, "Content");
            var smallfigRoot = Path.Combine(contentRoot, "Characters", "Smallfig");
            var batmiteRoot = Path.Combine(smallfigRoot, "Batmite");
            Directory.CreateDirectory(batmiteRoot);
            foreach (var stem in new[] { "BP_Batmite_Quest", "BP_Batmite_01_Quest", "BP_Batmite_02_Quest" })
            {
                File.WriteAllBytes(Path.Combine(batmiteRoot, stem + ".uasset"), Array.Empty<byte>());
            }
            File.WriteAllBytes(
                Path.Combine(batmiteRoot, "BP_CAT_Archetype_Batmite.uasset"),
                Array.Empty<byte>());

            var questPackages = BaseCharacterPicker.EnumerateExtractedQuestVisualPackages(contentRoot);
            var visualAssets = BaseCharacterPicker.BuildVisualAssetList(
                Array.Empty<GameDataAsset>(),
                contentRoot,
                playablesOnly: false);
            var gameplayAssets = BaseCharacterPicker.BuildVisualAssetList(
                Array.Empty<GameDataAsset>(),
                contentRoot,
                playablesOnly: true);
            Check(
                questPackages.Count == 3 &&
                questPackages.Contains(
                    "/Game/Characters/Smallfig/Batmite/BP_Batmite_Quest",
                    StringComparer.OrdinalIgnoreCase) &&
                visualAssets.Count == 3 &&
                gameplayAssets.Count == 0 &&
                BaseEligibilityService.RequiresSeparateGameplayDonor(
                    "/Game/Characters/Smallfig/Batmite/BP_Batmite_Quest") &&
                !BaseEligibilityService.RequiresSeparateGameplayDonor(
                    "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Default_Playable") &&
                BaseEligibilityService.CharacterStem(
                    "/Game/Characters/Smallfig/Batmite/BP_Batmite_01_Quest") == "Batmite_01" &&
                BaseEligibilityService.CharacterStem(
                    "/Game/Characters/Minifig/MrFreeze/BP_MrFreeze_BaR_Boss") ==
                BaseEligibilityService.CharacterStem(
                    "/Game/Characters/Minifig/MrFreeze/BP_MrFreeze_BaR_Cutscene") &&
                BaseEligibilityService.IsSameCharacterVariant(
                    "/Game/Characters/Minifig/Alfred/BP_Alfred_Default_Quest",
                    "/Game/Characters/Minifig/Alfred/BP_Alfred_Default_Cutscene") &&
                !BaseEligibilityService.IsSameCharacterVariant(
                    "/Game/Characters/Minifig/Alfred/BP_Alfred_Default_Quest",
                    "/Game/Characters/Minifig/Alfred/BP_Alfred_1966_Cutscene"),
                "extracted Smallfig _Quest Blueprints appear as visual bases and require an explicit gameplay donor",
                failures,
                output);

            var indexedBlueprints = PartIndexService.EnumerateCharacterBlueprintsForTest(contentRoot);
            Check(
                indexedBlueprints.Count == 3 &&
                indexedBlueprints.Any(path => Path.GetFileNameWithoutExtension(path)
                    .Equals("BP_Batmite_Quest", StringComparison.OrdinalIgnoreCase)) &&
                !PartIndexService.IsCurrentIndexForTest(new NativeSuitPartIndex { SchemaVersion = 2 }) &&
                PartIndexService.IsCurrentIndexForTest(new NativeSuitPartIndex()),
                "the native part index scans Smallfig quest-character Blueprints",
                failures,
                output);

            var originalSettings = AppSettings.Current;
            var partIndexTracksActiveExtract = false;
            try
            {
                var indexProjectRoot = Path.Combine(questRegressionRoot, "PartIndexProject");
                var indexService = new PartIndexService(indexProjectRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(indexService.PartIndexPath)!);
                File.WriteAllText(
                    indexService.PartIndexPath,
                    System.Text.Json.JsonSerializer.Serialize(new NativeSuitPartIndex
                    {
                        // Deliberately record a nested rig folder: normalization should still match
                        // the active Content root.
                        SourceContentRoot = smallfigRoot
                    }));

                AppSettings.Current = new AppSettings { ExtractedContentRoot = contentRoot };
                var normalizedMatchingIndexLoads = indexService.LoadPartIndex() is not null;

                var replacementContentRoot = Path.Combine(
                    questRegressionRoot,
                    "ReplacementExtract",
                    "Content");
                Directory.CreateDirectory(replacementContentRoot);
                AppSettings.Current.ExtractedContentRoot = replacementContentRoot;
                var sameSchemaStaleIndexIsRejected = indexService.LoadPartIndex() is null;

                partIndexTracksActiveExtract =
                    normalizedMatchingIndexLoads && sameSchemaStaleIndexIsRejected;
            }
            finally
            {
                AppSettings.Current = originalSettings;
            }
            Check(
                partIndexTracksActiveExtract,
                "a same-schema native part index is rejected after the active extracted Content root changes",
                failures,
                output);
            Check(
                AppSettings.NormalizeContentRoot(smallfigRoot)
                    .Equals(contentRoot, StringComparison.OrdinalIgnoreCase) &&
                AnimArchetypeGraftService.IsCharacterOwnedMaterialPackage(
                    "/Game/Characters/Smallfig/Batmite/Materials/MI_Batmite_EoM",
                    "Batmite"),
                "Smallfig extract roots and character-owned materials resolve like Minifig assets",
                failures,
                output);
        }
        catch (Exception ex)
        {
            output.WriteLine("FAIL: Smallfig quest-character regression threw: " + ex.Message);
            failures.Add("extracted Smallfig _Quest Blueprints appear as visual bases and require an explicit gameplay donor");
            failures.Add("the native part index scans Smallfig quest-character Blueprints");
            failures.Add("a same-schema native part index is rejected after the active extracted Content root changes");
            failures.Add("Smallfig extract roots and character-owned materials resolve like Minifig assets");
        }
        finally
        {
            try
            {
                if (Directory.Exists(questRegressionRoot))
                {
                    Directory.Delete(questRegressionRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of the uniquely named regression folder.
            }
        }

        Check(
            PartGraftService.AddsClassChildPropertyForTest(),
            "an appended SCS component gets one reflected generated-class field bound to its component class",
            failures,
            output);
        Check(
            PartGraftService.ClearsStaleAnimClassForDonorWithoutAnimForTest(),
            "a skeletal donor with no AnimClass clears an unrelated cloned-shell AnimClass and dependency",
            failures,
            output);
        Check(
            PartGraftService.AddsScsNodeDependencyInNativeOrderForTest(),
            "an appended SCS node is added once to SimpleConstructionScript create-before-serialization dependencies",
            failures,
            output);

        Check(
            PartGraftService.CanRepointExistingComponentForTest(false, false, false, false),
            "matching skeletal cosmetic components can be repointed",
            failures,
            output);
        Check(
            !PartGraftService.CanRepointExistingComponentForTest(false, false, true, false),
            "a runtime glider shell cannot be reused as a cosmetic cape",
            failures,
            output);
        Check(
            !PartGraftService.CanRepointExistingComponentForTest(false, false, false, true),
            "a cosmetic cape shell cannot be reused as a runtime glider",
            failures,
            output);
        Check(
            !PartGraftService.CanRepointExistingComponentForTest(true, false, false, false),
            "static and skeletal component shells are not mixed",
            failures,
            output);

        var cosmeticCape = new NativeSuitPartRecord
        {
            CharacterFolder = "Batman",
            Slot = "Cape",
            MeshKind = "SkeletalMesh",
            MeshObjectName = "SK_Cape_Spiked",
            MeshPackagePath = "/Game/Characters/Attachments/Cape/SK_Cape_Spiked",
            ComponentTags = new List<string> { "Cape" },
        };
        var wingsuit = new NativeSuitPartRecord
        {
            CharacterFolder = "Nightwing",
            Slot = "Cape",
            MeshKind = "SkeletalMesh",
            MeshObjectName = "SK_GA_Wingsuit_Nightwing",
            MeshPackagePath = "/Game/Models/Gadgets/GA_Wingsuit_Nightwing/SK_GA_Wingsuit_Nightwing",
            AnimClassObjectName = "ABP_Wingsuit_C",
            AnimClassPackagePath = "/Game/Models/Gadgets/GA_Wingsuit/ABP_Wingsuit",
            AnimClassObjectPath = "/Game/Models/Gadgets/GA_Wingsuit/ABP_Wingsuit.ABP_Wingsuit_C",
            ComponentTags = new List<string> { "Glider" },
        };
        Check(
            GliderService.IsCosmeticCapeAttachment(cosmeticCape) && !GliderService.IsNativeGliderPart(cosmeticCape),
            "a normal cape remains a visible cosmetic attachment",
            failures,
            output);
        Check(
            GliderService.IsNativeGliderPart(wingsuit) && !GliderService.IsCosmeticCapeAttachment(wingsuit),
            "a wingsuit remains a runtime glider visual",
            failures,
            output);
        var sharedCapeDriver = new NativeSuitPartRecord
        {
            AnimClassObjectName = "ABP_Cape_Glide_C"
        };
        var batgirlPartyCapeDriver = new NativeSuitPartRecord
        {
            AnimClassObjectName = "ABP_Cape_Glide_Batgirl_Party_C"
        };
        Check(
            GliderService.PairedCapeDriverForPart(sharedCapeDriver) == PairedCapeVisibilityDriver.PairedCapable &&
            GliderService.PairedCapeDriverForPart(batgirlPartyCapeDriver) == PairedCapeVisibilityDriver.PairedCapable &&
            GliderService.PairedCapeDriverForPart(wingsuit) == PairedCapeVisibilityDriver.GlideOnly &&
            GliderService.PairedCapeDriverForPart(new NativeSuitPartRecord { AnimClassObjectName = "ABP_TaliaGlider_C" }) == PairedCapeVisibilityDriver.GlideOnly &&
            GliderService.PairedCapeDriverForPart(new NativeSuitPartRecord { AnimClassObjectName = "ABP_GordonGlider_C" }) == PairedCapeVisibilityDriver.GlideOnly &&
            GliderService.PairedCapeDriverForPart(new NativeSuitPartRecord { AnimClassObjectName = "ABP_UnverifiedGlider_C" }) == PairedCapeVisibilityDriver.Unknown &&
            GliderService.PairedCapeDriverForPart(new NativeSuitPartRecord { AnimClassObjectName = "ABP_Cape_Glide_Experimental_C" }) == PairedCapeVisibilityDriver.Unknown &&
            GliderService.PairedCapeDriverForPart(new NativeSuitPartRecord
            {
                AnimClassPackagePath = "/Game/Mods/ABP_Cape_Glide/ABP_Experimental",
                AnimClassObjectPath = "/Game/Mods/ABP_Cape_Glide/ABP_Experimental.ABP_Experimental_C"
            }) == PairedCapeVisibilityDriver.Unknown,
            "paired-cape visibility drivers allow only the proven shared and Batgirl Party cape AnimBlueprints",
            failures,
            output);
        var savedWingsuitDonor = MainForm.PartToDonorForTest(wingsuit, "playable");
        Check(
            savedWingsuitDonor is not null &&
            savedWingsuitDonor.AnimClassObjectName == wingsuit.AnimClassObjectName &&
            savedWingsuitDonor.AnimClassPackagePath == wingsuit.AnimClassPackagePath &&
            savedWingsuitDonor.AnimClassObjectPath == wingsuit.AnimClassObjectPath &&
            GliderService.PairedCapeDriverForDonor(savedWingsuitDonor) == PairedCapeVisibilityDriver.GlideOnly,
            "saved part grafts persist the AnimClass identity needed for cape/glider release safety",
            failures,
            output);
        Check(
            GliderService.PairedCapeDriverForDonor(new SavedPartGraftDonor
            {
                MeshObjectPath = "/Game/Models/Gadgets/GA_Wingsuit_CatWoman/SK_GA_Wingsuit_CatWoman.SK_GA_Wingsuit_CatWoman"
            }) == PairedCapeVisibilityDriver.GlideOnly &&
            GliderService.PairedCapeDriverForDonor(new SavedPartGraftDonor
            {
                MeshObjectPath = "/Game/Characters/Attachments/Cape/SK_CAPE_Glide.SK_CAPE_Glide"
            }) == PairedCapeVisibilityDriver.PairedCapable,
            "legacy saved gliders recover a conservative visibility-driver classification from mesh identity",
            failures,
            output);
        var duplicateGliderProject = new NativeSuitProject
        {
            PartGrafts =
            [
                new SavedPartGraft
                {
                    IsGlider = true,
                    Playable = new SavedPartGraftDonor { AnimClassObjectName = "ABP_Wingsuit_C" }
                },
                new SavedPartGraft
                {
                    IsGlider = true,
                    Playable = new SavedPartGraftDonor { AnimClassObjectName = "ABP_Cape_Glide_C" }
                }
            ]
        };
        Check(
            GliderService.ProjectReplacementGliderDriver(duplicateGliderProject) == PairedCapeVisibilityDriver.PairedCapable,
            "legacy duplicate glider lists validate the last graft replayed by the declarative rebuild",
            failures,
            output);
        var unverifiedDonorProject = new NativeSuitProject
        {
            GliderType = "native:Unverified glide cape",
            PartGrafts =
            [
                new SavedPartGraft
                {
                    IsGlider = true,
                    Playable = new SavedPartGraftDonor { AnimClassObjectName = "ABP_UnverifiedGlider_C" }
                }
            ]
        };
        Check(
            GliderService.ProjectReplacementGliderDriver(unverifiedDonorProject) == PairedCapeVisibilityDriver.Unknown,
            "an unverified saved donor cannot be promoted to paired-capable by a glider display label",
            failures,
            output);
        var savedCapeProject = new NativeSuitProject
        {
            PartGrafts =
            [
                new SavedPartGraft
                {
                    IsGlider = false,
                    Playable = new SavedPartGraftDonor
                    {
                        Context = "playable",
                        Stem = "SK_CAPE_TwoHole_Spiked",
                        ComponentTags = ["TtCharacterAsset.Cape", "Cape"]
                    }
                }
            ]
        };
        Check(
            GliderService.ProjectHasCosmeticCape(savedCapeProject),
            "saved cosmetic cape grafts are detected for glide compatibility checks",
            failures,
            output);
        savedCapeProject.PartGrafts[0].Playable!.ComponentTags.Add("Glider");
        Check(
            !GliderService.ProjectHasCosmeticCape(savedCapeProject),
            "glider-tagged saved grafts are not mistaken for cosmetic capes",
            failures,
            output);
        savedCapeProject.CustomStaticMeshes.Add(new CustomStaticMeshImport { Target = "cApE" });
        Check(
            GliderService.ProjectHasCosmeticCape(savedCapeProject) &&
            GliderService.ProjectHasAdditiveCustomCape(savedCapeProject),
            "custom static meshes targeting Cape participate in glide compatibility checks",
            failures,
            output);
        var (las, mas) = GliderService.GliderAnimSetsForPart(wingsuit);
        Check(
            las == "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Nightwing" &&
            mas == "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Nightwing",
            "glider donor traversal sets are preserved",
            failures,
            output);

        var writerMessage = RegistryPluginService.DescribeWriterBuildFailureForTest(
            1,
            "Building...\nC:/Project/Source/Test.Build.cs(17): error CS1002: ; expected\nResult: Failed");
        Check(
            writerMessage.Contains("CS1002", StringComparison.Ordinal) &&
            writerMessage.Contains("First build error", StringComparison.Ordinal),
            "registry writer reports the first useful compiler error",
            failures,
            output);
        var netFxMessage = RegistryPluginService.DescribeWriterBuildFailureForTest(
            8,
            "Unable to instantiate module 'SwarmInterface': Could not find NetFxSDK install dir");
        Check(
            netFxMessage.Contains(".NET Framework 4.8 SDK", StringComparison.Ordinal),
            "registry writer gives an actionable NETFXSDK fallback message",
            failures,
            output);
        var spacedPathMessage = RegistryPluginService.DescribeWriterBuildFailureForTest(
            1,
            "'C:\\Program' is not recognized as an internal or external command");
        Check(
            spacedPathMessage.Contains("quoted-path fix", StringComparison.Ordinal),
            "registry writer diagnoses legacy spaced Build.bat invocation failures",
            failures,
            output);
        var structuredVerification =
            "BATCOMPUTER_REGISTRY_WRITER_RESULT cooked_header=yes expected_primary_rows=2 " +
            "exact_primary_rows=2 exact_primary_ids=2 all_expected_rows=yes " +
            "all_expected_primary_ids=yes sentinel_enabled=yes sentinel_exact_row=yes " +
            "sentinel_exact_primary_id=yes";
        Check(
            RegistryPluginService.VerificationMatches(structuredVerification, new[]
            {
                new RegistryPluginService.RegistryRow("/Game/Mods/Test/DA_One", "DA_One"),
                new RegistryPluginService.RegistryRow("/Game/Mods/Test/DA_Two", "DA_Two"),
            }),
            "registry verification trusts exact structured writer counts without fragile command-line text",
            failures,
            output);
        Check(
            UnrealPathUtil.SanitizeIdentifier("Joker TDKR (Jacket)") == "Joker_TDKR_Jacket" &&
            UnrealPathUtil.IsValidIdentifier("Joker_TDKR_Jacket") &&
            !UnrealPathUtil.IsValidIdentifier("JokerTDKR(Jacket)"),
            "generated Unreal identifiers remove display-name punctuation",
            failures,
            output);
        Check(
            AnimArchetypeGraftService.IsCharacterArchetypePackage(
                "/Game/Characters/Minifig/Batman/BP_CAT_Archetype_Batman") &&
            AnimArchetypeGraftService.IsCharacterArchetypePackage(
                "/Game/Characters/Minifig/Catwoman/BP_Catwoman_Archetype") &&
            !AnimArchetypeGraftService.IsCharacterArchetypePackage(
                "/Game/Characters/Minifig/Firefly/BP_Firefly_Boss_Archetype") &&
            !AnimArchetypeGraftService.IsCharacterArchetypePackage(
                "/Game/Characters/Materials/MI_Archetype_Test"),
            "nonstandard Catwoman character archetype names remain valid gameplay donors",
            failures,
            output);
        var completeCustomMeshGraft = new[]
        {
            new PartGraftPackageResult { Role = "playable", Success = true },
            new PartGraftPackageResult { Role = "cutscene", Success = true },
        };
        var partialCustomMeshGraft = new[]
        {
            new PartGraftPackageResult { Role = "playable", Success = true },
            new PartGraftPackageResult { Role = "cutscene", Success = false },
        };
        Check(
            CustomStaticMeshImportService.HasCompleteCharacterGraft(completeCustomMeshGraft) &&
            !CustomStaticMeshImportService.HasCompleteCharacterGraft(partialCustomMeshGraft) &&
            !CustomStaticMeshImportService.HasCompleteCharacterGraft(completeCustomMeshGraft.Take(1)),
            "custom meshes require successful playable and cutscene grafts",
            failures,
            output);
        var activeViewerProject = new NativeSuitProject
        {
            SlotId = "viewer-transform-regression",
            CustomStaticMeshes =
            [
                new CustomStaticMeshImport
                {
                    Id = "mesh-regression",
                    Scale = 150f,
                    OffsetX = 0f,
                    OffsetY = 0f,
                    OffsetZ = 0f,
                }
            ]
        };
        var diskLoadedViewerClone = new NativeSuitProject
        {
            SlotId = "VIEWER-TRANSFORM-REGRESSION",
            CustomStaticMeshes =
            [
                new CustomStaticMeshImport
                {
                    Id = "mesh-regression",
                    Scale = 150f,
                }
            ]
        };
        var canonicalViewerProject = MainForm.ResolveViewerProjectForEdit(
            diskLoadedViewerClone,
            activeViewerProject);
        MainForm.ApplyViewerCustomMeshTransform(
            canonicalViewerProject!.CustomStaticMeshes[0],
            new PreviewCustomMeshTransform(215f, 12f, -8f, 4f, 15f, 25f, 35f));
        // Model the next editor action that triggers a clean declarative rebuild. The transform
        // must live on the active recipe that the part-removal path will save and replay.
        canonicalViewerProject.Requirements.Add(new NativeSuitRequirement
        {
            Kind = "remove-component",
            TargetComponent = "Hat:0",
        });
        var unrelatedViewerProject = new NativeSuitProject { SlotId = "another-suit" };
        Check(
            ReferenceEquals(canonicalViewerProject, activeViewerProject) &&
            activeViewerProject.CustomStaticMeshes[0].Scale == 215f &&
            activeViewerProject.CustomStaticMeshes[0].OffsetX == 12f &&
            activeViewerProject.CustomStaticMeshes[0].OffsetY == -8f &&
            activeViewerProject.CustomStaticMeshes[0].OffsetZ == 4f &&
            activeViewerProject.CustomStaticMeshes[0].RotationPitch == 15f &&
            activeViewerProject.CustomStaticMeshes[0].RotationYaw == 25f &&
            activeViewerProject.CustomStaticMeshes[0].RotationRoll == 35f &&
            diskLoadedViewerClone.CustomStaticMeshes[0].Scale == 150f &&
            ReferenceEquals(
                MainForm.ResolveViewerProjectForEdit(unrelatedViewerProject, activeViewerProject),
                unrelatedViewerProject),
            "custom mesh viewer bakes update the active recipe used by later part-removal rebuilds",
            failures,
            output);
        var loneTransientGraft = new[]
        {
            new PartGraftPackageResult { Role = "cutscene", TransientFileLock = true },
        };
        var mixedTransientGraft = new[]
        {
            new PartGraftPackageResult { Role = "playable", Success = true },
            new PartGraftPackageResult { Role = "cutscene", TransientFileLock = true },
        };
        var loneRollbackRoles = PartGraftService.GetTransientBatchRollbackRolesForTest(
            loneTransientGraft,
            ["cutscene"]);
        var mixedRollbackRoles = PartGraftService.GetTransientBatchRollbackRolesForTest(
            mixedTransientGraft,
            ["playable", "cutscene"]);
        Check(
            PartGraftService.ShouldRollbackTransientBatchForTest(loneTransientGraft) &&
            loneRollbackRoles.SequenceEqual(["cutscene"], StringComparer.OrdinalIgnoreCase) &&
            PartGraftService.ShouldRollbackTransientBatchForTest(mixedTransientGraft) &&
            mixedRollbackRoles.Count == 2 &&
            mixedRollbackRoles.Contains("playable", StringComparer.OrdinalIgnoreCase) &&
            mixedRollbackRoles.Contains("cutscene", StringComparer.OrdinalIgnoreCase) &&
            !PartGraftService.ShouldRollbackTransientBatchForTest(completeCustomMeshGraft) &&
            PartGraftService.GetTransientBatchRollbackRolesForTest(
                completeCustomMeshGraft,
                ["playable", "cutscene"]).Count == 0,
            "any transient graft failure restores every targeted role before retry",
            failures,
            output);
        var materialOnlyProject = new NativeSuitProject
        {
            MaterialAssignments = [new SavedMaterialAssignment()]
        };
        var removalOnlyProject = new NativeSuitProject
        {
            Requirements = [new NativeSuitRequirement { Kind = "remove-component" }]
        };
        Check(
            MainForm.ProjectRequiresCompletedGraftStage(materialOnlyProject) &&
            MainForm.ProjectRequiresCompletedGraftStage(removalOnlyProject) &&
            !MainForm.ProjectRequiresCompletedGraftStage(new NativeSuitProject()),
            "material/removal-only projects require a completed declarative stage",
            failures,
            output);
        var neverCreatedGraftStage = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-never-created-graft-stage-" + Guid.NewGuid().ToString("N"));
        var freshStageMarkerCleanupSucceeded = false;
        try
        {
            freshStageMarkerCleanupSucceeded =
                !MainForm.DeleteCompletedGraftStageMarkerIfPresent(neverCreatedGraftStage);
        }
        catch
        {
            freshStageMarkerCleanupSucceeded = false;
        }
        Check(
            freshStageMarkerCleanupSucceeded,
            "a suit's first declarative rebuild tolerates a not-yet-created graft stage",
            failures,
            output);
        var baseTransactionRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-base-transaction-regression-" + Guid.NewGuid().ToString("N"));
        var missingBaseStagesAreSafe = false;
        var existingBaseStageWasRemoved = false;
        var markerOnlySlotWasRecoverable = false;
        var ownedSlotWasPreserved = false;
        try
        {
            var neverCreatedBaseStage = Path.Combine(baseTransactionRoot, "UnpatchedStage");
            missingBaseStagesAreSafe = !MainForm.DeleteGeneratedStageDirectoryIfPresent(neverCreatedBaseStage);

            var existingBaseStage = Path.Combine(baseTransactionRoot, "PatchedNameMapStage");
            Directory.CreateDirectory(existingBaseStage);
            File.WriteAllText(Path.Combine(existingBaseStage, ".batcomputer-stage-complete"), "complete");
            File.WriteAllText(Path.Combine(existingBaseStage, "payload.uasset"), "payload");
            existingBaseStageWasRemoved =
                MainForm.DeleteGeneratedStageDirectoryIfPresent(existingBaseStage) &&
                !Directory.Exists(existingBaseStage);

            var markerOnlySlot = Path.Combine(baseTransactionRoot, "marker-only-slot");
            Directory.CreateDirectory(markerOnlySlot);
            File.WriteAllText(
                Path.Combine(markerOnlySlot, ".batcomputer-declarative-stage-incomplete"),
                "incomplete");
            markerOnlySlotWasRecoverable = MainForm.IsRecoverableIncompleteSlotForTest(
                Path.Combine(baseTransactionRoot, "marker-only.native-suit-project.json"),
                markerOnlySlot);

            var ownedSlot = Path.Combine(baseTransactionRoot, "owned-slot");
            Directory.CreateDirectory(ownedSlot);
            File.WriteAllText(
                Path.Combine(ownedSlot, ".batcomputer-declarative-stage-incomplete"),
                "incomplete");
            File.WriteAllText(Path.Combine(ownedSlot, "saved-mesh.obj"), "owned");
            ownedSlotWasPreserved = !MainForm.IsRecoverableIncompleteSlotForTest(
                Path.Combine(baseTransactionRoot, "owned.native-suit-project.json"),
                ownedSlot);
        }
        finally
        {
            try
            {
                if (Directory.Exists(baseTransactionRoot))
                {
                    Directory.Delete(baseTransactionRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of the unique regression directory.
            }
        }
        Check(
            missingBaseStagesAreSafe &&
            existingBaseStageWasRemoved &&
            markerOnlySlotWasRecoverable &&
            ownedSlotWasPreserved,
            "base creation/reselection tolerates missing stages and reclaims only marker-only failed slots",
            failures,
            output);
        var meshMigrationRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-mesh-migration-regression-" + Guid.NewGuid().ToString("N"));
        var partialMeshCopyWasRolledBack = false;
        var preExistingMeshWasPreserved = false;
        var freshMeshDirectoriesWereRemoved = false;
        try
        {
            Directory.CreateDirectory(meshMigrationRoot);
            var firstSource = Path.Combine(meshMigrationRoot, "first-source.obj");
            var secondSource = Path.Combine(meshMigrationRoot, "second-source.obj");
            File.WriteAllText(firstSource, "first source");
            File.WriteAllText(secondSource, "second source");

            var occupiedProject = new NativeSuitProject { SlotId = "occupied-migration-slot" };
            var occupiedService = new SuitProjectService(meshMigrationRoot);
            var occupiedImportedRoot = Path.Combine(
                occupiedService.ProjectOutputDirectory(occupiedProject),
                "ImportedMeshes");
            Directory.CreateDirectory(occupiedImportedRoot);
            var preExistingDestination = Path.Combine(occupiedImportedRoot, "second.obj");
            File.WriteAllText(preExistingDestination, "pre-existing owned mesh");
            var occupiedMigration = new MainForm.BaseCustomMeshSourceMigration(
                meshMigrationRoot,
                occupiedProject);
            try
            {
                occupiedMigration.CopySources(
                    meshMigrationRoot,
                    occupiedProject,
                    new[]
                    {
                        (new CustomStaticMeshImport { Id = "first", DisplayName = "First" }, firstSource),
                        (new CustomStaticMeshImport { Id = "second", DisplayName = "Second" }, secondSource),
                    });
            }
            catch (IOException)
            {
                // The first file was copied, then the second exact destination refused overwrite.
            }
            occupiedMigration.Rollback();
            partialMeshCopyWasRolledBack = !File.Exists(Path.Combine(occupiedImportedRoot, "first.obj"));
            preExistingMeshWasPreserved =
                File.Exists(preExistingDestination) &&
                File.ReadAllText(preExistingDestination) == "pre-existing owned mesh";

            var freshProject = new NativeSuitProject { SlotId = "fresh-migration-slot" };
            var freshService = new SuitProjectService(meshMigrationRoot);
            var freshSlotRoot = freshService.ProjectOutputDirectory(freshProject);
            var freshMigration = new MainForm.BaseCustomMeshSourceMigration(
                meshMigrationRoot,
                freshProject);
            freshMigration.CopySources(
                meshMigrationRoot,
                freshProject,
                new[]
                {
                    (new CustomStaticMeshImport { Id = "fresh", DisplayName = "Fresh" }, firstSource),
                });
            freshMigration.Rollback();
            freshMeshDirectoriesWereRemoved = !Directory.Exists(freshSlotRoot);
        }
        finally
        {
            try
            {
                if (Directory.Exists(meshMigrationRoot))
                {
                    Directory.Delete(meshMigrationRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of the unique regression directory.
            }
        }
        Check(
            partialMeshCopyWasRolledBack &&
            preExistingMeshWasPreserved &&
            freshMeshDirectoriesWereRemoved,
            "failed slot-ID OBJ migration removes only files it created and leaves retries clean",
            failures,
            output);
        var materialOwnershipProject = new NativeSuitProject
        {
            GeneratedMaterials =
            [
                new GeneratedMaterialEntry { PackagePath = "/Game/Mods/Owned/MI_Referenced" },
                new GeneratedMaterialEntry { PackagePath = "/Game/Mods/Owned/MI_Unused" },
            ],
            MaterialAssignments =
            [
                new SavedMaterialAssignment { MiPackagePath = "/Game/Mods/Owned/MI_Referenced" },
                new SavedMaterialAssignment { MiPackagePath = "/Game/Characters/Base/MI_BaseGame" },
                new SavedMaterialAssignment { MiPackagePath = "/Game/Mods/External/MI_External" },
            ],
        };
        var ownedReferencedMaterials =
            MainForm.ReferencedGeneratedMaterialPackagesForRelease(materialOwnershipProject);
        Check(
            ownedReferencedMaterials.Count == 1 &&
            ownedReferencedMaterials[0].Equals(
                "/Game/Mods/Owned/MI_Referenced",
                StringComparison.OrdinalIgnoreCase),
            "release material checks require only referenced project-generated packages",
            failures,
            output);
        var sharingViolation = new IOException("locked", unchecked((int)0x80070020));
        Check(
            FileLockUtil.IsTransient(new InvalidOperationException("wrapped", sharingViolation)) &&
            FileLockUtil.IsTransient(new InvalidOperationException(
                "wrapped",
                new TransientFileLockException("structured lock"))) &&
            !FileLockUtil.IsTransient(new FileNotFoundException("missing")) &&
            !FileLockUtil.IsTransient(new IOException("sharing violation text without a sharing-violation code")),
            "only transient sharing violations enter the bounded file-lock retry path",
            failures,
            output);
        var emptyCapeProject = new NativeSuitProject();
        Check(
            GliderService.HasCapeAndGliderCombination(
                emptyCapeProject,
                AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
                addingCosmeticCape: true) &&
            GliderService.HasCapeAndGliderCombination(
                emptyCapeProject,
                AnimArchetypeGraftService.CapeGlideContractStatus.CapeOnly,
                addingGlider: true) &&
            !GliderService.HasCapeAndGliderCombination(
                emptyCapeProject,
                AnimArchetypeGraftService.CapeGlideContractStatus.Neither,
                addingCosmeticCape: true),
            "native and grafted cape/glider combinations use the same compatibility gate",
            failures,
            output);
        var pairedBaseWithRemovedCape = new NativeSuitProject
        {
            Requirements =
            [
                new NativeSuitRequirement
                {
                    Kind = "remove-component",
                    TargetComponent = "Cape:0"
                }
            ],
            PartGrafts =
            [
                new SavedPartGraft
                {
                    IsGlider = true,
                    Playable = new SavedPartGraftDonor
                    {
                        MeshObjectPath = "/Game/Models/Gadgets/GA_Wingsuit_CatWoman/SK_GA_Wingsuit_CatWoman.SK_GA_Wingsuit_CatWoman"
                    }
                }
            ]
        };
        Check(
            GliderService.ProjectExplicitlyRemovesComponent(pairedBaseWithRemovedCape, "Cape") &&
            !GliderService.HasCapeAndGliderCombination(
                pairedBaseWithRemovedCape,
                AnimArchetypeGraftService.CapeGlideContractStatus.Paired),
            "an explicitly removed native Cape permits a glide-only replacement without a double-cape false positive",
            failures,
            output);
        var additiveCapeOnPairedBase = new NativeSuitProject
        {
            CustomStaticMeshes = [new CustomStaticMeshImport { Target = "Cape" }]
        };
        Check(
            GliderService.HasAdditiveCapeAndGliderCombination(
                additiveCapeOnPairedBase,
                AnimArchetypeGraftService.CapeGlideContractStatus.Paired),
            "an additive custom Cape remains incompatible with a glider on a native paired-cape base",
            failures,
            output);
        var migratedGliderIntent = new NativeSuitProject
        {
            GliderType = "wingsuit",
            CustomStaticMeshes = [new CustomStaticMeshImport { Target = "Cape" }]
        };
        var baseSentinelGliderIntent = new NativeSuitProject
        {
            GliderType = "base",
            CustomStaticMeshes = [new CustomStaticMeshImport { Target = "Cape" }]
        };
        Check(
            GliderService.HasAdditiveCapeAndGliderCombination(
                migratedGliderIntent,
                AnimArchetypeGraftService.CapeGlideContractStatus.Neither) &&
            !GliderService.HasAdditiveCapeAndGliderCombination(
                baseSentinelGliderIntent,
                AnimArchetypeGraftService.CapeGlideContractStatus.Neither),
            "persisted non-base glider intent remains incompatible with an additive custom Cape",
            failures,
            output);
        var nativeCapeOnPairedBase = new NativeSuitProject
        {
            PartGrafts =
            [
                new SavedPartGraft
                {
                    Playable = new SavedPartGraftDonor
                    {
                        Stem = "SK_CAPE_TwoHole_Spiked",
                        ComponentTags = ["Cape"]
                    }
                }
            ]
        };
        Check(
            GliderService.ProjectHasNativeCosmeticCapeGraft(nativeCapeOnPairedBase) &&
            !GliderService.HasAdditiveCapeAndGliderCombination(
                nativeCapeOnPairedBase,
                AnimArchetypeGraftService.CapeGlideContractStatus.Paired),
            "a native cape graft keeps the paired-base visibility-wiring exemption",
            failures,
            output);
        var unsafeGeneratedNightwingCapePair = new NativeSuitProject
        {
            PartGrafts =
            [
                new SavedPartGraft
                {
                    Slot = "Cape",
                    Playable = new SavedPartGraftDonor
                    {
                        Context = "playable",
                        Stem = "SK_CAPE_TwoHole_Spiked",
                        ComponentTags = ["TtCharacterAsset.Cape", "Cape"]
                    }
                },
                new SavedPartGraft
                {
                    Slot = "Cape",
                    IsGlider = true,
                    Playable = new SavedPartGraftDonor
                    {
                        Context = "playable",
                        AnimClassObjectName = "ABP_Cape_Glide_C",
                        ComponentTags = ["Glider"]
                    }
                }
            ]
        };
        Check(
            GliderService.HasCapeAndGliderCombination(
                unsafeGeneratedNightwingCapePair,
                AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly) &&
            GliderService.ProjectReplacementGliderDriver(unsafeGeneratedNightwingCapePair) ==
                PairedCapeVisibilityDriver.PairedCapable &&
            StageValidationService.BlocksSyntheticCapePairOnGlideOnlyBaseForTest(
                unsafeGeneratedNightwingCapePair),
            "a synthetic Cape plus ABP_Cape_Glide remains a blocked cape/glider combination on a glide-only base",
            failures,
            output);

        var pairedCapePreflightProject = CreateCertifiedNightwingCapeAdapterProject();
        var incomingPreflightCape = pairedCapePreflightProject.PartGrafts.Single(graft => !graft.IsGlider);
        pairedCapePreflightProject.PartGrafts.Remove(incomingPreflightCape);
        var acceptsExactAuthoredShellPreflight = GliderService.CanConfigurePairedCapeAdapterWithIncoming(
            pairedCapePreflightProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            incomingPreflightCape,
            out _);
        var expectedPlayableCapeSlot = incomingPreflightCape.Playable!.TemplateSlot;
        var expectedCutsceneCapeSlot = incomingPreflightCape.Cutscene!.TemplateSlot;
        incomingPreflightCape.Playable.TemplateSlot = "Torso";
        incomingPreflightCape.Cutscene.TemplateSlot = "Torso";
        var rejectsNonExactAuthoredShellPreflight = !GliderService.CanConfigurePairedCapeAdapterWithIncoming(
            pairedCapePreflightProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            incomingPreflightCape,
            out _);
        incomingPreflightCape.Playable.TemplateSlot = expectedPlayableCapeSlot;
        incomingPreflightCape.Cutscene.TemplateSlot = expectedCutsceneCapeSlot;
        Check(
            acceptsExactAuthoredShellPreflight && rejectsNonExactAuthoredShellPreflight,
            "paired-cape preflight requires the donor's exact authored Cape plus Torso fields",
            failures,
            output);

        var certifiedNightwingCapePair = CreateCertifiedNightwingCapeAdapterProject();
        var adapterConfigured = GliderService.TryConfigurePairedCapeAdapter(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            nativeGliderComponent: "Cape",
            out var adapterConfigureDetail);
        var certifiedCosmeticCape = certifiedNightwingCapePair.PartGrafts.Single(graft => !graft.IsGlider);
        var certifiedGlideCape = certifiedNightwingCapePair.PartGrafts.Single(graft => graft.IsGlider);
        // Model the successful existing-field replay on the authored shell.
        certifiedCosmeticCape.ResolvedComponent = certifiedCosmeticCape.Slot;
        certifiedGlideCape.ResolvedComponent = certifiedGlideCape.Slot;
        GliderService.RefreshPairedCapeAdapterResolvedComponents(certifiedNightwingCapePair);
        var adapterIsResolved = GliderService.IsDeclaredPairedCapeAdapterValid(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            requireResolvedComponents: true,
            out var adapterValidationDetail);
        var authoredShellReady = GliderService.TryGetAuthoredPairedCapeShell(
            certifiedNightwingCapePair,
            out var authoredPlayableShell,
            out var authoredCutsceneShell,
            out _);
        Check(
            adapterConfigured &&
            adapterIsResolved &&
            authoredShellReady &&
            certifiedNightwingCapePair.PairedCapeAdapter is not null &&
            certifiedNightwingCapePair.PairedCapeAdapter.GameplayDonorPackage.Equals(
                "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Default_Playable",
                StringComparison.OrdinalIgnoreCase) &&
            authoredPlayableShell.EndsWith("BP_Batman_AnimatedSeries_Playable", StringComparison.OrdinalIgnoreCase) &&
            authoredCutsceneShell.EndsWith("BP_Batman_AnimatedSeries_Cutscene", StringComparison.OrdinalIgnoreCase) &&
            certifiedNightwingCapePair.PairedCapeAdapter.ResolvedCosmeticComponent == "Cape" &&
            certifiedNightwingCapePair.PairedCapeAdapter.ResolvedGliderComponent == "Torso" &&
            certifiedCosmeticCape.Slot == "Cape" &&
            certifiedGlideCape.Slot == "Torso" &&
            !certifiedCosmeticCape.PreferDonorComponentShell &&
            !certifiedGlideCape.PreferDonorComponentShell &&
            certifiedNightwingCapePair.GliderAnimLas.Equals(
                "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Batman",
                StringComparison.OrdinalIgnoreCase) &&
            certifiedNightwingCapePair.GliderAnimMas.Equals(
                "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Batman",
                StringComparison.OrdinalIgnoreCase) &&
            certifiedNightwingCapePair.PairedCapeAdapter.GlideAnimLasPackage.Equals(
                certifiedNightwingCapePair.GliderAnimLas,
                StringComparison.OrdinalIgnoreCase) &&
            certifiedNightwingCapePair.PairedCapeAdapter.GlideAnimMasPackage.Equals(
                certifiedNightwingCapePair.GliderAnimMas,
                StringComparison.OrdinalIgnoreCase) &&
            certifiedNightwingCapePair.UseCustomArchetype &&
            certifiedNightwingCapePair.GliderAutoEnabledCustomArchetype &&
            !StageValidationService.BlocksSyntheticCapePairOnGlideOnlyBaseForTest(certifiedNightwingCapePair),
            "a complete adapter keeps Nightwing's gameplay donor while binding its authored Cape plus Torso donor's Batman glide blocks",
            failures,
            output);
        if (!adapterConfigured || !adapterIsResolved)
        {
            output.WriteLine(
                $"  paired-cape adapter detail: configure='{adapterConfigureDetail}', validate='{adapterValidationDetail}'");
        }

        const string certifiedBatmanMas =
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Batman";
        const string certifiedBatmanLas =
            "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Batman";
        var nightwingMasParents = new[]
        {
            "/Game/Animation/MontageAnimSets/Activity/MAS_MenusUpgrades_Nightwing",
            "/Game/Animation/MontageAnimSets/Activity/MAS_Rewards_Nightwing",
            "/Game/Animation/MontageAnimSets/Character/MAS_Playable",
            "/Game/Animation/MontageAnimSets/Combat/MAS_Combat_Flurry_Nightwing",
            "/Game/Animation/MontageAnimSets/Equipment/MAS_Equipment_ElectricBirdarang",
            "/Game/Animation/MontageAnimSets/Equipment/MAS_Equipment_TetherLauncher",
            "/Game/Animation/MontageAnimSets/Interaction/MAS_Interaction_Staff",
            "/Game/Animation/MontageAnimSets/StatusEffects/MAS_StatusEffect_ElectricityBatons",
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Nightwing",
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Grapple_Nightwing",
            "/Game/Animation/MontageAnimSets/Traversal/MAS_LedgeGrab_Nightwing",
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Nightwing"
        };
        var bridgedNightwingMasParents = nightwingMasParents
            .Select(parent => parent.EndsWith("/MAS_Glide_Nightwing", StringComparison.OrdinalIgnoreCase)
                ? certifiedBatmanMas
                : parent)
            .ToList();
        var nightwingLasParents = new[]
        {
            "/Game/Animation/LayerAnimSets/Character/LAS_Playable",
            "/Game/Animation/LayerAnimSets/Default/LAS_Default_Minifig",
            "/Game/Animation/LayerAnimSets/Default/LAS_Default_Nightwing",
            "/Game/Animation/LayerAnimSets/Equipment/LAS_Equipment_Boomerang_Nightwing",
            "/Game/Animation/LayerAnimSets/Equipment/LAS_Equipment_TetherLauncher",
            "/Game/Animation/LayerAnimSets/LAS_DEPRECATED_StaffInteractions",
            "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Nightwing"
        };
        var bridgedNightwingLasParents = nightwingLasParents
            .Select(parent => parent.EndsWith("/LAS_Traversal_Nightwing", StringComparison.OrdinalIgnoreCase)
                ? certifiedBatmanLas
                : parent)
            .ToList();
        var reorderedMasParents = new[]
        {
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Nightwing",
            "/Game/Animation/MontageAnimSets/Character/MAS_Playable",
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Nightwing"
        };
        var reorderedMasBridge = reorderedMasParents
            .Select(parent => parent.EndsWith("/MAS_Glide_Nightwing", StringComparison.OrdinalIgnoreCase)
                ? certifiedBatmanMas
                : parent)
            .ToList();
        var reorderedLasParents = new[]
        {
            "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Nightwing",
            "/Game/Animation/LayerAnimSets/Character/LAS_Playable",
            "/Game/Animation/LayerAnimSets/Default/LAS_Default_Nightwing"
        };
        var reorderedLasBridge = reorderedLasParents
            .Select(parent => parent.EndsWith("/LAS_Traversal_Nightwing", StringComparison.OrdinalIgnoreCase)
                ? certifiedBatmanLas
                : parent)
            .ToList();
        var masBridgeReplacesNightwingGlide =
            StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingMasParents,
                bridgedNightwingMasParents,
                certifiedBatmanMas,
                "MAS_Glide_",
                out _);
        var lasBridgeReplacesNightwingGlide =
            StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingLasParents,
                bridgedNightwingLasParents,
                certifiedBatmanLas,
                "LAS_Traversal_",
                out _);
        var reorderedCookedParentsRemainSafe =
            StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                reorderedMasParents,
                reorderedMasBridge,
                certifiedBatmanMas,
                "MAS_Glide_",
                out _) &&
            StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                reorderedLasParents,
                reorderedLasBridge,
                certifiedBatmanLas,
                "LAS_Traversal_",
                out _);
        var missingCertifiedMasRejected =
            !StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingMasParents,
                nightwingMasParents,
                certifiedBatmanMas,
                "MAS_Glide_",
                out _);
        var droppedNightwingParentRejected =
            !StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingLasParents,
                bridgedNightwingLasParents
                    .Where(parent => !UnrealPathUtil.AssetName(parent).Equals(
                        "LAS_Default_Nightwing",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                certifiedBatmanLas,
                "LAS_Traversal_",
                out _);
        var competingNightwingGlideRejected =
            !StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingMasParents,
                nightwingMasParents.Append(certifiedBatmanMas).ToList(),
                certifiedBatmanMas,
                "MAS_Glide_",
                out _);
        var sameStemWrongPackageRejected =
            !StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingMasParents,
                bridgedNightwingMasParents
                    .Select(parent => parent.Equals(certifiedBatmanMas, StringComparison.OrdinalIgnoreCase)
                        ? "/Game/Mods/WrongAnimationAlias/MAS_Glide_Batman"
                        : parent)
                    .ToList(),
                certifiedBatmanMas,
                "MAS_Glide_",
                out _);
        var unresolvedStemOnlyRejected =
            !StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingLasParents,
                bridgedNightwingLasParents
                    .Select(UnrealPathUtil.AssetName)
                    .ToList(),
                certifiedBatmanLas,
                "LAS_Traversal_",
                out _);
        var unresolvedParentEntryRejected =
            !StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                nightwingMasParents,
                bridgedNightwingMasParents.Append("").ToList(),
                certifiedBatmanMas,
                "MAS_Glide_",
                out _);
        Check(
            masBridgeReplacesNightwingGlide &&
            lasBridgeReplacesNightwingGlide &&
            reorderedCookedParentsRemainSafe &&
            missingCertifiedMasRejected &&
            droppedNightwingParentRejected &&
            competingNightwingGlideRejected &&
            sameStemWrongPackageRejected &&
            unresolvedStemOnlyRejected &&
            unresolvedParentEntryRejected,
            "paired-cape MAS/LAS clones retain every non-glide Nightwing parent while replacing each native glide category with exactly one certified Batman package (duplicates and same-stem aliases rejected)",
            failures,
            output);

        const string authoredBatmanDprd =
            "/Game/Characters/Minifig/Batman/DA_DPRD_Batman";
        const string gameplayNightwingDprd =
            "/Game/Characters/Minifig/Nightwing/DA_DPRD_Nightwing";
        const string sameStemWrongNightwingDprd =
            "/Game/Mods/WrongAlias/DA_DPRD_Nightwing";
        const string regressionMod = "NightwingCapeBridgeRegression";
        var exactGameplayBehaviorBridgeAccepted =
            StageValidationService.BehaviorBridgeReferencesAreSafeForTest(
                [(gameplayNightwingDprd, "DA_DPRD_Nightwing")],
                authoredBatmanDprd,
                gameplayNightwingDprd);
        var sameStemWrongBehaviorBridgeRejected =
            !StageValidationService.BehaviorBridgeReferencesAreSafeForTest(
                [(sameStemWrongNightwingDprd, "DA_DPRD_Nightwing")],
                authoredBatmanDprd,
                gameplayNightwingDprd);
        var retainedAuthoredBehaviorBridgeRejected =
            !StageValidationService.BehaviorBridgeReferencesAreSafeForTest(
                [
                    (gameplayNightwingDprd, "DA_DPRD_Nightwing"),
                    (authoredBatmanDprd, "DA_DPRD_Batman")
                ],
                authoredBatmanDprd,
                gameplayNightwingDprd);
        var equipmentFreeDprd = StageValidationService.ExpectedPairedCapeDprdPackageForTest(
            certifiedNightwingCapePair,
            regressionMod,
            gameplayNightwingDprd,
            "Nightwing");
        certifiedNightwingCapePair.EquipmentSlots.Add(new EquipmentSlotChange
        {
            Slot = 0,
            Gadget = "Electrorang"
        });
        var nativeEquipmentDprd = StageValidationService.ExpectedPairedCapeDprdPackageForTest(
            certifiedNightwingCapePair,
            regressionMod,
            gameplayNightwingDprd,
            "Nightwing");
        certifiedNightwingCapePair.EquipmentSlots.Clear();
        certifiedNightwingCapePair.EquipmentSlots.Add(new EquipmentSlotChange
        {
            Slot = 0,
            Gadget = "Batarang"
        });
        var foreignEquipmentDprd = StageValidationService.ExpectedPairedCapeDprdPackageForTest(
            certifiedNightwingCapePair,
            regressionMod,
            gameplayNightwingDprd,
            "Nightwing");
        certifiedNightwingCapePair.EquipmentSlots.Clear();
        var generatedEquipmentDprd =
            $"/Game/Mods/{regressionMod}/Characters/DA_DPRD_{regressionMod}";
        var generatedEquipmentBehaviorBridgeAccepted =
            StageValidationService.BehaviorBridgeReferencesAreSafeForTest(
                [(generatedEquipmentDprd, $"DA_DPRD_{regressionMod}")],
                authoredBatmanDprd,
                foreignEquipmentDprd,
                gameplayNightwingDprd);
        var danglingGameplayDprdRejectedForGeneratedBridge =
            !StageValidationService.BehaviorBridgeReferencesAreSafeForTest(
                [
                    (generatedEquipmentDprd, $"DA_DPRD_{regressionMod}"),
                    (gameplayNightwingDprd, "DA_DPRD_Nightwing")
                ],
                authoredBatmanDprd,
                foreignEquipmentDprd,
                gameplayNightwingDprd);
        var ordinaryGliderAbilitySets = new List<string>();
        var ordinaryGliderProject = new NativeSuitProject
        {
            PartGrafts = [new SavedPartGraft { IsGlider = true }]
        };
        var ordinaryGliderDependencyWasEmitted =
            AnimArchetypeGraftService.EnsureGliderAbilitySetDependency(
                ordinaryGliderProject,
                usesPairedCapeAdapter: false,
                ordinaryGliderAbilitySets);
        var ordinaryGliderAbilitySetStillForcesGeneratedDprd =
            ordinaryGliderDependencyWasEmitted &&
            ordinaryGliderAbilitySets.Count == 1 &&
            ordinaryGliderAbilitySets[0].Equals(
                GliderService.GlidingAbilitySetPackage,
                StringComparison.OrdinalIgnoreCase) &&
            AnimArchetypeGraftService.RequiresGeneratedDprdFromResolvedDependencies(
                hasForeignEquipmentDefinitions: false,
                hasForeignAbilitySets: ordinaryGliderAbilitySets.Count > 0);
        var pairedGliderAbilitySets = new List<string>();
        var pairedGliderKeepsNativeAbilityLoadout =
            !AnimArchetypeGraftService.EnsureGliderAbilitySetDependency(
                certifiedNightwingCapePair,
                usesPairedCapeAdapter: true,
                pairedGliderAbilitySets) &&
            pairedGliderAbilitySets.Count == 0;
        var pairedNoEquipmentKeepsGameplayDprd =
            !AnimArchetypeGraftService.RequiresGeneratedDprd(
                certifiedNightwingCapePair,
                "Nightwing");
        const string sourceMasPackage =
            "/Game/Characters/Minifig/Nightwing/Animations/MAS_Char_Nightwing";
        var generatedMasPackage =
            $"/Game/Mods/{regressionMod}/Characters/MAS_Char_{regressionMod}";
        var nonCascadingCloneIdentity =
            AnimArchetypeGraftService.ApplyNameMapReplacementsForTest(
                sourceMasPackage + ".MAS_Char_Nightwing",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [sourceMasPackage] = generatedMasPackage,
                    ["MAS_Char_Nightwing"] = $"MAS_Char_{regressionMod}"
                })
            .Equals(
                generatedMasPackage + $".MAS_Char_{regressionMod}",
                StringComparison.Ordinal);
        Check(
            exactGameplayBehaviorBridgeAccepted &&
            sameStemWrongBehaviorBridgeRejected &&
            retainedAuthoredBehaviorBridgeRejected &&
            equipmentFreeDprd.Equals(gameplayNightwingDprd, StringComparison.OrdinalIgnoreCase) &&
            nativeEquipmentDprd.Equals(gameplayNightwingDprd, StringComparison.OrdinalIgnoreCase) &&
            foreignEquipmentDprd.Equals(generatedEquipmentDprd, StringComparison.OrdinalIgnoreCase) &&
            generatedEquipmentBehaviorBridgeAccepted &&
            danglingGameplayDprdRejectedForGeneratedBridge &&
            ordinaryGliderAbilitySetStillForcesGeneratedDprd &&
            pairedGliderKeepsNativeAbilityLoadout &&
            pairedNoEquipmentKeepsGameplayDprd &&
            nonCascadingCloneIdentity,
            "exact behavior bridges keep paired native equipment on Nightwing DPRD, switch foreign equipment exclusively to mod-local DPRD, retain ordinary glider AS_Gliding DPRD generation, and emit exact non-cascading mod-local package identities",
            failures,
            output);

        var expectedAnimClass = certifiedGlideCape.Playable!.AnimClassObjectName;
        certifiedGlideCape.Playable.AnimClassObjectName = "ABP_Wingsuit_C";
        var rejectsWrongAdapterAnimClass = !GliderService.IsDeclaredPairedCapeAdapterValid(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            requireResolvedComponents: true,
            out _);
        certifiedGlideCape.Playable.AnimClassObjectName = expectedAnimClass;

        var expectedGliderGraftId = certifiedNightwingCapePair.PairedCapeAdapter!.GlideCapeGraftInstanceId;
        certifiedNightwingCapePair.PairedCapeAdapter.GlideCapeGraftInstanceId = "removed-glider-graft";
        var rejectsStaleAdapterGraftId = !GliderService.IsDeclaredPairedCapeAdapterValid(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            requireResolvedComponents: true,
            out _);
        certifiedNightwingCapePair.PairedCapeAdapter.GlideCapeGraftInstanceId = expectedGliderGraftId;

        var expectedCosmeticSource = certifiedCosmeticCape.Playable!.SourcePackagePath;
        certifiedCosmeticCape.Playable.SourcePackagePath =
            "/Game/Characters/Minifig/Batman/BP_Batman_GrayGhost_Playable";
        var rejectsChangedAdapterSource = !GliderService.IsDeclaredPairedCapeAdapterValid(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            requireResolvedComponents: true,
            out _);
        certifiedCosmeticCape.Playable.SourcePackagePath = expectedCosmeticSource;
        var adapterRecoversAfterRestoringIdentity = GliderService.IsDeclaredPairedCapeAdapterValid(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            requireResolvedComponents: true,
            out _);
        certifiedNightwingCapePair.PartGrafts.Add(certifiedGlideCape);
        var duplicateShellLookupRejectedWithoutThrow = false;
        try
        {
            duplicateShellLookupRejectedWithoutThrow =
                !GliderService.TryGetAuthoredPairedCapeShell(
                    certifiedNightwingCapePair,
                    out var duplicatePlayableShell,
                    out var duplicateCutsceneShell,
                    out _) &&
                string.IsNullOrWhiteSpace(duplicatePlayableShell) &&
                string.IsNullOrWhiteSpace(duplicateCutsceneShell);
        }
        catch
        {
            duplicateShellLookupRejectedWithoutThrow = false;
        }
        var rejectsDuplicateActiveGlider = !GliderService.IsDeclaredPairedCapeAdapterValid(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            requireResolvedComponents: true,
            out _);
        certifiedNightwingCapePair.PartGrafts.RemoveAt(certifiedNightwingCapePair.PartGrafts.Count - 1);

        var adapterCertificate = certifiedNightwingCapePair.PairedCapeAdapter!;
        var expectedCertificateSource = adapterCertificate.GliderCutsceneSourcePackage;
        adapterCertificate.GliderCutsceneSourcePackage =
            "/Game/Characters/Minifig/Batman/BP_Batman_GrayGhost_Cutscene";
        var rejectsTamperedCertificateSource =
            !GliderService.IsDeclaredPairedCapeAdapterValid(
                certifiedNightwingCapePair,
                AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
                requireResolvedComponents: true,
                out _) &&
            !GliderService.TryGetAuthoredPairedCapeShell(
                certifiedNightwingCapePair,
                out _,
                out _,
                out _);
        adapterCertificate.GliderCutsceneSourcePackage = expectedCertificateSource;

        var expectedCertificateAnimClass = adapterCertificate.PairedAnimClassObjectName;
        adapterCertificate.PairedAnimClassObjectName = "ABP_Cape_Glide_Imposter_C";
        var rejectsTamperedCertificateAnimClass =
            !GliderService.IsDeclaredPairedCapeAdapterValid(
                certifiedNightwingCapePair,
                AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
                requireResolvedComponents: true,
                out _) &&
            !GliderService.TryGetAuthoredPairedCapeShell(
                certifiedNightwingCapePair,
                out _,
                out _,
                out _);
        adapterCertificate.PairedAnimClassObjectName = expectedCertificateAnimClass;

        var expectedProjectGlideMas = certifiedNightwingCapePair.GliderAnimMas;
        certifiedNightwingCapePair.GliderAnimMas =
            "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Nightwing";
        var rejectsGameplayDonorGlideFallback =
            !GliderService.IsDeclaredPairedCapeAdapterValid(
                certifiedNightwingCapePair,
                AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
                requireResolvedComponents: true,
                out _);
        certifiedNightwingCapePair.GliderAnimMas = expectedProjectGlideMas;

        var expectedCertificateGlideLas = adapterCertificate.GlideAnimLasPackage;
        adapterCertificate.GlideAnimLasPackage =
            "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Nightwing";
        var rejectsTamperedGlideAnimationCertificate =
            !GliderService.IsDeclaredPairedCapeAdapterValid(
                certifiedNightwingCapePair,
                AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
                requireResolvedComponents: true,
                out _);
        adapterCertificate.GlideAnimLasPackage = expectedCertificateGlideLas;

        certifiedCosmeticCape.PreferDonorComponentShell = true;
        var rejectsSyntheticDonorShell = !GliderService.IsDeclaredPairedCapeAdapterValid(
            certifiedNightwingCapePair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            requireResolvedComponents: true,
            out _);
        certifiedCosmeticCape.PreferDonorComponentShell = false;
        Check(
            rejectsWrongAdapterAnimClass &&
            rejectsStaleAdapterGraftId &&
            rejectsChangedAdapterSource &&
            adapterRecoversAfterRestoringIdentity &&
            rejectsDuplicateActiveGlider &&
            duplicateShellLookupRejectedWithoutThrow &&
            rejectsTamperedCertificateSource &&
            rejectsTamperedCertificateAnimClass &&
            rejectsGameplayDonorGlideFallback &&
            rejectsTamperedGlideAnimationCertificate &&
            rejectsSyntheticDonorShell,
            "paired-cape certification and shell lookup fail closed on changed sources, glide blocks, certificates, shell mode, or duplicate graft identities",
            failures,
            output);

        var visualFixture = CreateVisualOverlayRegressionFixture();
        var selectsCompatibleScaffold = PairedCapeVisualOverlayService.TrySelectCompatibleScaffoldForTest(
            visualFixture.Index,
            visualFixture.OverlayGrafts,
            visualFixture.CosmeticCape,
            visualFixture.GlideCape,
            "/Game/Characters/Minifig/Batman/BP_Batman_AnimatedSeries_Playable",
            "/Game/Characters/Minifig/Batman/BP_Batman_AnimatedSeries_Cutscene",
            out var selectedVisualPlayableShell,
            out var selectedVisualCutsceneShell);
        var exactOverlayCertificateValid = PairedCapeVisualOverlayService.ValidateDeclaration(
            visualFixture.Project,
            visualFixture.Index,
            visualFixture.Project.PairedCapeAdapter!,
            out _,
            visualFixture.IdentityMaterials);
        var overlayHead = visualFixture.Project.PairedCapeAdapter!.VisualOverlay!.ComponentGrafts
            .Single(graft => graft.Slot.Equals("Head", StringComparison.OrdinalIgnoreCase));
        var expectedHeadMesh = overlayHead.Playable!.MeshObjectPath;
        overlayHead.Playable.MeshObjectPath =
            "/Game/Characters/Heads/Batman/SK_Head_Batman.SK_Head_Batman";
        var rejectsTamperedHeadRecipe = !PairedCapeVisualOverlayService.ValidateDeclaration(
            visualFixture.Project,
            visualFixture.Index,
            visualFixture.Project.PairedCapeAdapter,
            out _,
            visualFixture.IdentityMaterials);
        overlayHead.Playable.MeshObjectPath = expectedHeadMesh;
        var overlayFace = visualFixture.Project.PairedCapeAdapter.VisualOverlay.ComponentGrafts
            .Single(graft => graft.Slot.Equals("Face", StringComparison.OrdinalIgnoreCase));
        var expectedFaceAnim = overlayFace.Playable!.AnimClassObjectPath;
        overlayFace.Playable.AnimClassObjectPath =
            "/Game/Characters/Heads/Faces/ABP_LEGOface_Batman.ABP_LEGOface_Batman_C";
        var rejectsTamperedFaceAnim = !PairedCapeVisualOverlayService.ValidateDeclaration(
            visualFixture.Project,
            visualFixture.Index,
            visualFixture.Project.PairedCapeAdapter,
            out _,
            visualFixture.IdentityMaterials);
        overlayFace.Playable.AnimClassObjectPath = expectedFaceAnim;
        var expectedBodyMaterial = visualFixture.Project.PairedCapeAdapter.VisualOverlay.PlayableBodyMaterialPackage;
        visualFixture.Project.PairedCapeAdapter.VisualOverlay.PlayableBodyMaterialPackage =
            "/Game/Characters/Minifig/Batman/Materials/MI_Batman_AnimatedSeries";
        var rejectsTamperedIdentityMaterial = !PairedCapeVisualOverlayService.ValidateDeclaration(
            visualFixture.Project,
            visualFixture.Index,
            visualFixture.Project.PairedCapeAdapter,
            out _,
            visualFixture.IdentityMaterials);
        visualFixture.Project.PairedCapeAdapter.VisualOverlay.PlayableBodyMaterialPackage = expectedBodyMaterial;
        var expectedScaffold = visualFixture.Project.PairedCapeAdapter.AuthoredShellPlayablePackage;
        visualFixture.Project.PairedCapeAdapter.AuthoredShellPlayablePackage =
            "/Game/Characters/Minifig/Batman/BP_Batman_AnimatedSeries_Playable";
        var rejectsIncompatiblePreferredScaffold = !PairedCapeVisualOverlayService.ValidateDeclaration(
            visualFixture.Project,
            visualFixture.Index,
            visualFixture.Project.PairedCapeAdapter,
            out _,
            visualFixture.IdentityMaterials);
        visualFixture.Project.PairedCapeAdapter.AuthoredShellPlayablePackage = expectedScaffold;
        var expectedOverlay = visualFixture.Project.PairedCapeAdapter.VisualOverlay;
        visualFixture.Project.PairedCapeAdapter.VisualOverlay = null;
        var packageOnlyRealProjectCannotBypassOverlay = !PairedCapeVisualOverlayService.ValidateDeclaration(
            visualFixture.Project,
            visualFixture.Index,
            visualFixture.Project.PairedCapeAdapter,
            out _,
            visualFixture.IdentityMaterials);
        visualFixture.Project.PairedCapeAdapter.VisualOverlay = expectedOverlay;
        var exactObjectPackageIdentityRejectsSameStem =
            StageValidationService.ObjectIdentityMatchesForTest(
                "MI_FACE_Nightwing",
                "/Game/Characters/Heads/Faces/MI_FACE_Nightwing",
                "/Game/Characters/Heads/Faces/MI_FACE_Nightwing.MI_FACE_Nightwing") &&
            !StageValidationService.ObjectIdentityMatchesForTest(
                "MI_FACE_Nightwing",
                "/Game/Imposters/MI_FACE_Nightwing",
                "/Game/Characters/Heads/Faces/MI_FACE_Nightwing.MI_FACE_Nightwing");
        var restoredNightwingFaceTags = PartGraftService.ComponentTagsForExistingFieldRepointForTest(
            ["TtCharacterAsset.Face"],
            ["TtCharacterAsset.Face", "FLS"],
            restoreExistingFieldRecipe: true);
        var ordinaryRepointKeepsScaffoldTags = PartGraftService.ComponentTagsForExistingFieldRepointForTest(
            ["TtCharacterAsset.Face"],
            ["TtCharacterAsset.Face", "FLS"],
            restoreExistingFieldRecipe: false);
        var overlayCanRestoreBothExistingFields = PartGraftService.CanRestoreExistingFieldRecipeForTest(
            playableRequested: true,
            playableExists: true,
            playableCanRepoint: true,
            cutsceneRequested: true,
            cutsceneExists: true,
            cutsceneCanRepoint: true);
        var overlayRejectsMissingRoleField = !PartGraftService.CanRestoreExistingFieldRecipeForTest(
            playableRequested: true,
            playableExists: true,
            playableCanRepoint: true,
            cutsceneRequested: true,
            cutsceneExists: false,
            cutsceneCanRepoint: false);
        var overlayRejectsIncompatibleRoleField = !PartGraftService.CanRestoreExistingFieldRecipeForTest(
            playableRequested: true,
            playableExists: true,
            playableCanRepoint: false,
            cutsceneRequested: true,
            cutsceneExists: true,
            cutsceneCanRepoint: true);
        Check(
            selectsCompatibleScaffold &&
            selectedVisualPlayableShell.EndsWith("BP_Batman_GrayGhost_Playable", StringComparison.OrdinalIgnoreCase) &&
            selectedVisualCutsceneShell.EndsWith("BP_Batman_GrayGhost_Cutscene", StringComparison.OrdinalIgnoreCase) &&
            exactOverlayCertificateValid &&
            rejectsTamperedHeadRecipe &&
            rejectsTamperedFaceAnim &&
            rejectsTamperedIdentityMaterial &&
            rejectsIncompatiblePreferredScaffold &&
            packageOnlyRealProjectCannotBypassOverlay &&
            exactObjectPackageIdentityRejectsSameStem &&
            restoredNightwingFaceTags.SequenceEqual(
                new[] { "TtCharacterAsset.Face", "FLS" },
                StringComparer.OrdinalIgnoreCase) &&
            ordinaryRepointKeepsScaffoldTags.SequenceEqual(
                new[] { "TtCharacterAsset.Face" },
                StringComparer.OrdinalIgnoreCase),
            "Nightwing's static Head rejects the Batman Animated shell, selects Gray Ghost, and certifies exact Head/Face/AnimClass/body/face identities",
            failures,
            output);
        Check(
            overlayCanRestoreBothExistingFields &&
            overlayRejectsMissingRoleField &&
            overlayRejectsIncompatibleRoleField,
            "automatic visual overlays require both compatible live role fields and cannot enter the synthetic component ADD path",
            failures,
            output);

        var certificateShellProject = CreateCertifiedNightwingCapeAdapterProject();
        var certificateShellConfigured = GliderService.TryConfigurePairedCapeAdapter(
            certificateShellProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Cape",
            out _);
        certificateShellProject.PairedCapeAdapter!.AuthoredShellPlayablePackage =
            "/Game/Characters/Minifig/Batman/BP_Batman_GrayGhost_Playable";
        certificateShellProject.PairedCapeAdapter.AuthoredShellCutscenePackage =
            "/Game/Characters/Minifig/Batman/BP_Batman_GrayGhost_Cutscene";
        var shellLookupUsesCertificate = GliderService.TryGetAuthoredPairedCapeShell(
            certificateShellProject,
            out var certificatePlayableShell,
            out var certificateCutsceneShell,
            out _) &&
            certificatePlayableShell.EndsWith("BP_Batman_GrayGhost_Playable", StringComparison.OrdinalIgnoreCase) &&
            certificateCutsceneShell.EndsWith("BP_Batman_GrayGhost_Cutscene", StringComparison.OrdinalIgnoreCase);
        Check(
            certificateShellConfigured && shellLookupUsesCertificate,
            "paired-cape shell lookup returns the certified compatible scaffold rather than the cosmetic donor",
            failures,
            output);

        var coexistenceProject = CreateCertifiedNightwingCapeAdapterProject();
        coexistenceProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Head",
            ResolvedComponent = "Head_2",
            InstanceId = "user-hair"
        });
        var headTwoDoesNotSuppressBaseHead =
            !StageValidationService.HasLaterUserPartOverrideForTest(coexistenceProject, "Head");
        coexistenceProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Face",
            ResolvedComponent = "Face",
            InstanceId = "user-face"
        });
        var exactFaceReplacementWins =
            StageValidationService.HasLaterUserPartOverrideForTest(coexistenceProject, "Face");
        coexistenceProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Body",
            ResolvedComponent = "",
            InstanceId = "legacy-user-body"
        });
        var unresolvedLegacyBodyReplacementWins =
            StageValidationService.HasLaterUserPartOverrideForTest(coexistenceProject, "CharacterMesh0");
        Check(
            headTwoDoesNotSuppressBaseHead && exactFaceReplacementWins && unresolvedLegacyBodyReplacementWins,
            "visual-overlay validation distinguishes a coexisting Head_2 attachment from exact Face/body field overrides",
            failures,
            output);

        var baseChangeProject = CreateCertifiedNightwingCapeAdapterProject();
        var baseChangeAdapterConfigured = GliderService.TryConfigurePairedCapeAdapter(
            baseChangeProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Cape",
            out _);
        baseChangeProject.PartGrafts.Single(graft => !graft.IsGlider).ResolvedComponent = "Cape";
        baseChangeProject.PartGrafts.Single(graft => graft.IsGlider).ResolvedComponent = "Torso";
        GliderService.RefreshPairedCapeAdapterResolvedComponents(baseChangeProject);
        baseChangeProject.GliderType = "native:Batman Animated glide cape";
        baseChangeProject.GliderMaterial = "/Game/Regression/Materials/MI_Glide";
        baseChangeProject.GliderGrafted = true;
        baseChangeProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Head",
            InstanceId = "unrelated-user-head",
            Playable = new SavedPartGraftDonor { Slot = "Head" }
        });
        var removedAdapterFields = MainForm.RemovePairedCapeAdapterAtomicallyForTest(baseChangeProject);
        Check(
            baseChangeAdapterConfigured &&
            baseChangeProject.PairedCapeAdapter is null &&
            baseChangeProject.PartGrafts.Count == 1 &&
            baseChangeProject.PartGrafts[0].InstanceId == "unrelated-user-head" &&
            string.IsNullOrWhiteSpace(baseChangeProject.GliderType) &&
            string.IsNullOrWhiteSpace(baseChangeProject.GliderMaterial) &&
            string.IsNullOrWhiteSpace(baseChangeProject.GliderAnimLas) &&
            string.IsNullOrWhiteSpace(baseChangeProject.GliderAnimMas) &&
            !baseChangeProject.GliderGrafted &&
            !baseChangeProject.GliderAutoEnabledCustomArchetype &&
            !baseChangeProject.UseCustomArchetype &&
            removedAdapterFields.Contains("Cape", StringComparer.OrdinalIgnoreCase) &&
            removedAdapterFields.Contains("Torso", StringComparer.OrdinalIgnoreCase),
            "changing a visual/base identity removes the bound Cape/Torso pair and all adapter-derived glider state atomically",
            failures,
            output);

        var staleIdFallbackProject = CreateCertifiedNightwingCapeAdapterProject();
        var staleIdFallbackConfigured = GliderService.TryConfigurePairedCapeAdapter(
            staleIdFallbackProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Cape",
            out _);
        staleIdFallbackProject.PairedCapeAdapter!.CosmeticCapeGraftInstanceId = "retired-cosmetic-id";
        staleIdFallbackProject.PairedCapeAdapter.GlideCapeGraftInstanceId = "retired-glider-id";
        MainForm.RemovePairedCapeAdapterAtomicallyForTest(staleIdFallbackProject);
        var exactSourceFallbackRemovedPair =
            staleIdFallbackConfigured &&
            staleIdFallbackProject.PairedCapeAdapter is null &&
            staleIdFallbackProject.PartGrafts.Count == 0;

        var corruptBoundIdProject = CreateCertifiedNightwingCapeAdapterProject();
        var corruptBoundIdConfigured = GliderService.TryConfigurePairedCapeAdapter(
            corruptBoundIdProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Cape",
            out _);
        var corruptBoundAdapter = corruptBoundIdProject.PairedCapeAdapter!;
        var corruptBoundCosmetic = corruptBoundIdProject.PartGrafts.Single(graft => !graft.IsGlider);
        var corruptCosmeticId = corruptBoundAdapter.CosmeticCapeGraftInstanceId;
        corruptBoundCosmetic.InstanceId = "real-cosmetic-with-stale-id";
        corruptBoundIdProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Head",
            InstanceId = corruptCosmeticId,
            Playable = new SavedPartGraftDonor
            {
                SourcePackagePath = "/Game/Regression/Corrupt/BP_UnrelatedHead_Playable",
                Context = "playable",
                Slot = "Head"
            },
            Cutscene = new SavedPartGraftDonor
            {
                SourcePackagePath = "/Game/Regression/Corrupt/BP_UnrelatedHead_Cutscene",
                Context = "cutscene",
                Slot = "Head"
            }
        });
        MainForm.RemovePairedCapeAdapterAtomicallyForTest(corruptBoundIdProject);
        var corruptIdCannotRedirectRemoval =
            corruptBoundIdConfigured &&
            corruptBoundIdProject.PairedCapeAdapter is null &&
            corruptBoundIdProject.PartGrafts.Count == 1 &&
            string.Equals(
                corruptBoundIdProject.PartGrafts[0].InstanceId,
                corruptCosmeticId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(corruptBoundIdProject.PartGrafts[0].Slot, "Head", StringComparison.OrdinalIgnoreCase);

        var corruptIdNoFallbackProject = CreateCertifiedNightwingCapeAdapterProject();
        var corruptIdNoFallbackConfigured = GliderService.TryConfigurePairedCapeAdapter(
            corruptIdNoFallbackProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Cape",
            out _);
        var corruptNoFallbackAdapter = corruptIdNoFallbackProject.PairedCapeAdapter!;
        var corruptNoFallbackCosmetic = corruptIdNoFallbackProject.PartGrafts.Single(graft => !graft.IsGlider);
        var corruptNoFallbackBoundId = corruptNoFallbackAdapter.CosmeticCapeGraftInstanceId;
        corruptNoFallbackCosmetic.InstanceId = "real-cosmetic-with-invalid-source";
        corruptNoFallbackCosmetic.Playable!.SourcePackagePath =
            "/Game/Regression/Corrupt/BP_Cape_WrongSource_Playable";
        corruptIdNoFallbackProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Head",
            InstanceId = corruptNoFallbackBoundId,
            Playable = new SavedPartGraftDonor
            {
                SourcePackagePath = "/Game/Regression/Corrupt/BP_UnrelatedHead_Playable",
                Context = "playable",
                Slot = "Head"
            },
            Cutscene = new SavedPartGraftDonor
            {
                SourcePackagePath = "/Game/Regression/Corrupt/BP_UnrelatedHead_Cutscene",
                Context = "cutscene",
                Slot = "Head"
            }
        });
        var corruptNoFallbackAdapterBefore = corruptIdNoFallbackProject.PairedCapeAdapter;
        var corruptNoFallbackGraftsBefore = corruptIdNoFallbackProject.PartGrafts.ToList();
        var corruptNoFallbackLasBefore = corruptIdNoFallbackProject.GliderAnimLas;
        var corruptNoFallbackFailedClosed = false;
        try
        {
            MainForm.RemovePairedCapeAdapterAtomicallyForTest(corruptIdNoFallbackProject);
        }
        catch (InvalidOperationException)
        {
            corruptNoFallbackFailedClosed = true;
        }
        var corruptIdWithoutRecipeFallbackPreservesState =
            corruptIdNoFallbackConfigured &&
            corruptNoFallbackFailedClosed &&
            ReferenceEquals(corruptIdNoFallbackProject.PairedCapeAdapter, corruptNoFallbackAdapterBefore) &&
            corruptIdNoFallbackProject.PartGrafts.Count == corruptNoFallbackGraftsBefore.Count &&
            corruptNoFallbackGraftsBefore.All(graft => corruptIdNoFallbackProject.PartGrafts.Contains(graft)) &&
            string.Equals(
                corruptIdNoFallbackProject.GliderAnimLas,
                corruptNoFallbackLasBefore,
                StringComparison.OrdinalIgnoreCase);

        var ambiguousStaleIdProject = CreateCertifiedNightwingCapeAdapterProject();
        var ambiguousStaleIdConfigured = GliderService.TryConfigurePairedCapeAdapter(
            ambiguousStaleIdProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Cape",
            out _);
        var ambiguousCosmetic = ambiguousStaleIdProject.PartGrafts.Single(graft => !graft.IsGlider);
        ambiguousStaleIdProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Cape",
            InstanceId = "duplicate-cosmetic-source",
            IsGlider = false,
            Playable = ambiguousCosmetic.Playable,
            Cutscene = ambiguousCosmetic.Cutscene,
        });
        ambiguousStaleIdProject.PairedCapeAdapter!.CosmeticCapeGraftInstanceId = "missing-cosmetic-id";
        var ambiguousAdapterBefore = ambiguousStaleIdProject.PairedCapeAdapter;
        var ambiguousCountBefore = ambiguousStaleIdProject.PartGrafts.Count;
        var ambiguousLasBefore = ambiguousStaleIdProject.GliderAnimLas;
        var ambiguousRemovalFailedClosed = false;
        try
        {
            MainForm.RemovePairedCapeAdapterAtomicallyForTest(ambiguousStaleIdProject);
        }
        catch (InvalidOperationException)
        {
            ambiguousRemovalFailedClosed = true;
        }
        Check(
            exactSourceFallbackRemovedPair &&
            corruptIdCannotRedirectRemoval &&
            corruptIdWithoutRecipeFallbackPreservesState &&
            ambiguousStaleIdConfigured &&
            ambiguousRemovalFailedClosed &&
            ReferenceEquals(ambiguousStaleIdProject.PairedCapeAdapter, ambiguousAdapterBefore) &&
            ambiguousStaleIdProject.PartGrafts.Count == ambiguousCountBefore &&
            ambiguousStaleIdProject.GliderAnimLas == ambiguousLasBefore,
            "atomic adapter removal ignores corrupt IDs, resolves the exact source/slot/role pair, and preserves all state when no unique pair exists",
            failures,
            output);
        var additiveCapeWithGraftedGlider = new NativeSuitProject
        {
            PawnTag = "Pawns.Playable.Batcomputer.CustomCapeGliderRegression",
            CustomStaticMeshes = [new CustomStaticMeshImport { Target = "Cape" }],
            PartGrafts = [new SavedPartGraft { IsGlider = true }]
        };
        var additiveCapeFindings = new StageValidationService(Path.GetTempPath(), null)
            .Validate(additiveCapeWithGraftedGlider);
        Check(
            additiveCapeFindings.Any(finding =>
                finding.Severity == "ERROR" &&
                finding.Message.Contains("custom static mesh attached to Cape", StringComparison.Ordinal)),
            "release validation blocks an additive custom Cape combined with a grafted glider",
            failures,
            output);
        var legacyDoubleCapeProject = new NativeSuitProject
        {
            PawnTag = "Pawns.Playable.Batcomputer.LegacyDoubleCapeRegression",
            UseCustomArchetype = true,
            PartGrafts =
            [
                new SavedPartGraft
                {
                    Slot = "Cape",
                    Playable = new SavedPartGraftDonor
                    {
                        Slot = "Cape",
                        ComponentTags = ["TtCharacterAsset.Cape", "Cape"]
                    }
                },
                new SavedPartGraft
                {
                    Slot = "Torso",
                    IsGlider = true,
                    // Deliberately omits the new AnimClass fields to exercise the saved-project
                    // fallback used by beta-era recipes.
                    Playable = new SavedPartGraftDonor
                    {
                        MeshObjectPath = "/Game/Models/Gadgets/GA_Wingsuit_CatWoman/SK_GA_Wingsuit_CatWoman.SK_GA_Wingsuit_CatWoman"
                    }
                }
            ]
        };
        var legacyDoubleCapeFindings = new StageValidationService(Path.GetTempPath(), null)
            .Validate(legacyDoubleCapeProject);
        Check(
            legacyDoubleCapeFindings.Any(finding =>
                finding.Severity == "ERROR" &&
                finding.Message.Contains("animation blueprint is glide-only", StringComparison.Ordinal)),
            "release validation package-blocks legacy saved wingsuit projects that retain a regular Cape",
            failures,
            output);
        legacyDoubleCapeProject.Requirements.Add(new NativeSuitRequirement
        {
            Kind = "remove-component",
            TargetComponent = "Cape:0"
        });
        var removedLegacyCapeFindings = new StageValidationService(Path.GetTempPath(), null)
            .Validate(legacyDoubleCapeProject);
        Check(
            !removedLegacyCapeFindings.Any(finding =>
                finding.Message.Contains("animation blueprint is glide-only", StringComparison.Ordinal)),
            "release validation accepts the legacy glide-only project once its native Cape is explicitly removed",
            failures,
            output);
        var gliderWithoutEquipment = new NativeSuitProject
        {
            PawnTag = "Pawns.Playable.Batcomputer.ReleaseRegression",
            GliderAnimLas = "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Batman",
            UseCustomArchetype = false,
            PartGrafts = [new SavedPartGraft { IsGlider = true }]
        };
        var gliderFindings = new StageValidationService(Path.GetTempPath(), null)
            .Validate(gliderWithoutEquipment);
        Check(
            gliderWithoutEquipment.EquipmentSlots.Count == 0 &&
            gliderFindings.Any(finding =>
                finding.Severity == "ERROR" &&
                finding.Message.Contains("custom archetype is off", StringComparison.Ordinal)),
            "glider safety remains package-blocking when the project has no equipment",
            failures,
            output);
        var unresolvedEquipmentChange = new EquipmentSlotChange
        {
            Slot = 1,
            Gadget = "__BatcomputerMissingEquipmentRegression__"
        };
        var etaLessEquipment = new GameDataEquipment { Name = "RegressionNoEta" };
        var resolvedEquipment = new GameDataEquipment
        {
            Name = "RegressionResolved",
            EtaPackage = "/Game/Regression/DA_ETA_RegressionResolved"
        };
        var unresolvedEquipmentProject = new NativeSuitProject
        {
            PawnTag = "Pawns.Playable.Batcomputer.UnresolvedEquipmentRegression",
            EquipmentSlots = [unresolvedEquipmentChange]
        };
        var unresolvedEquipmentFindings = new StageValidationService(Path.GetTempPath(), null)
            .Validate(unresolvedEquipmentProject);
        Check(
            EquipmentDependencyService.SavedChangeResolutionError(
                unresolvedEquipmentChange,
                equipment: null) is { } unknownEquipmentError &&
            unknownEquipmentError.Contains("not present", StringComparison.OrdinalIgnoreCase) &&
            EquipmentDependencyService.SavedChangeResolutionError(
                unresolvedEquipmentChange,
                etaLessEquipment) is { } missingEtaError &&
            missingEtaError.Contains("no DA_ETA", StringComparison.Ordinal) &&
            EquipmentDependencyService.SavedChangeResolutionError(
                unresolvedEquipmentChange,
                resolvedEquipment) is null &&
            unresolvedEquipmentFindings.Any(finding =>
                finding.Severity == "ERROR" &&
                finding.Message.Contains("not present in the active equipment catalog", StringComparison.Ordinal)),
            "unresolved or ETA-less saved equipment blocks release instead of being omitted",
            failures,
            output);

        var trioRegressionRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-release-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(trioRegressionRoot);
            const string regressionPackageName = "FreshRegression_P";
            foreach (var extension in new[] { ".pak", ".ucas", ".utoc" })
            {
                File.WriteAllText(
                    Path.Combine(trioRegressionRoot, regressionPackageName + extension),
                    "fresh-" + extension);
            }

            var expectedTrioPaths = new[] { ".pak", ".ucas", ".utoc" }
                .Select(extension => Path.Combine(trioRegressionRoot, regressionPackageName + extension))
                .ToList();
            var acceptsCompleteNonEmptyTrio =
                BuildManifestService.FindMissingOrEmptyFiles(expectedTrioPaths).Count == 0;
            File.WriteAllText(Path.Combine(trioRegressionRoot, regressionPackageName + ".ucas"), "");
            File.Delete(Path.Combine(trioRegressionRoot, regressionPackageName + ".utoc"));
            var incompleteOutputs = BuildManifestService.FindMissingOrEmptyFiles(expectedTrioPaths);
            var rejectsEmptyOrMissingTrio =
                incompleteOutputs.Contains(
                    Path.Combine(trioRegressionRoot, regressionPackageName + ".ucas"),
                    StringComparer.OrdinalIgnoreCase) &&
                incompleteOutputs.Contains(
                    Path.Combine(trioRegressionRoot, regressionPackageName + ".utoc"),
                    StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(Path.Combine(trioRegressionRoot, regressionPackageName + ".ucas"), "fresh-.ucas");
            File.WriteAllText(Path.Combine(trioRegressionRoot, regressionPackageName + ".utoc"), "fresh-.utoc");
            Check(
                acceptsCompleteNonEmptyTrio && rejectsEmptyOrMissingTrio,
                "release packaging rejects a missing or empty IoStore trio",
                failures,
                output);

            var manifestService = new BuildManifestService();
            var (freshManifest, _) = manifestService.Write(
                "fresh-build-id",
                trioRegressionRoot,
                "",
                trioRegressionRoot,
                regressionPackageName,
                "fresh_slot",
                "Fresh suit",
                new Dictionary<string, string>(),
                Array.Empty<string>(),
                Array.Empty<string>());
            var acceptsFresh = manifestService.VerifyInstallableTrio(
                freshManifest,
                "fresh-build-id",
                "fresh_slot",
                regressionPackageName,
                trioRegressionRoot,
                out _);
            var rejectsWrongBuild = !manifestService.VerifyInstallableTrio(
                freshManifest,
                "older-build-id",
                "fresh_slot",
                regressionPackageName,
                trioRegressionRoot,
                out _);

            var transactionalDestination = Path.Combine(trioRegressionRoot, "transactional-install");
            Directory.CreateDirectory(transactionalDestination);
            File.WriteAllText(
                Path.Combine(transactionalDestination, regressionPackageName + ".pak"),
                "old-pak");
            // Deliberately leave the prior .ucas absent so rollback must restore absence as well
            // as the bytes of destinations that existed before the transaction.
            File.WriteAllText(
                Path.Combine(transactionalDestination, regressionPackageName + ".utoc"),
                "old-utoc");

            var installFiles = freshManifest.TrioFiles
                .Select(entry => new TrioInstallTransactionService.FileSpec(
                    Path.Combine(trioRegressionRoot, entry.File),
                    entry.File,
                    entry.Sha256,
                    entry.Size))
                .ToList();
            var installPlan = TrioInstallTransactionService.BuildPlanForTest(
                installFiles,
                transactionalDestination,
                "regressiontransaction");
            var destinationRoot = Path.GetFullPath(transactionalDestination);
            var planUsesDestinationSideArtifacts = installPlan.All(entry =>
                FileSystemPathUtil.IsWithinDirectory(entry.StagedPath, destinationRoot) &&
                FileSystemPathUtil.IsWithinDirectory(entry.BackupPath, destinationRoot));

            var transactionService = new TrioInstallTransactionService();
            var rollbackResult = transactionService.InstallForTest(
                installFiles,
                transactionalDestination,
                failBeforeCommitIndex: 2);
            var rollbackRestoredEveryPriorState =
                !rollbackResult.Success &&
                rollbackResult.DestinationConsistent &&
                File.ReadAllText(Path.Combine(transactionalDestination, regressionPackageName + ".pak")) == "old-pak" &&
                !File.Exists(Path.Combine(transactionalDestination, regressionPackageName + ".ucas")) &&
                File.ReadAllText(Path.Combine(transactionalDestination, regressionPackageName + ".utoc")) == "old-utoc" &&
                !Directory.EnumerateFiles(transactionalDestination).Any(path =>
                    path.EndsWith(".installing", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
            var commitResult = transactionService.Install(installFiles, transactionalDestination);
            var committedCompleteFreshTrio = commitResult.Success &&
                new[] { ".pak", ".ucas", ".utoc" }.All(extension =>
                    File.ReadAllText(Path.Combine(transactionalDestination, regressionPackageName + extension)) ==
                    "fresh-" + extension);
            Check(
                planUsesDestinationSideArtifacts &&
                rollbackRestoredEveryPriorState &&
                committedCompleteFreshTrio,
                "trio install is destination-staged and restores every prior state after a mid-commit failure",
                failures,
                output);

            File.AppendAllText(Path.Combine(trioRegressionRoot, regressionPackageName + ".pak"), "tampered");
            var rejectsChangedTrio = !manifestService.VerifyInstallableTrio(
                freshManifest,
                "fresh-build-id",
                "fresh_slot",
                regressionPackageName,
                trioRegressionRoot,
                out _);
            Check(
                acceptsFresh && rejectsWrongBuild && rejectsChangedTrio,
                "automatic install accepts only the exact trio certified by the current build ID",
                failures,
                output);
        }
        catch (Exception ex)
        {
            output.WriteLine("FAIL: automatic install trio verification threw: " + ex.Message);
            failures.Add("automatic install accepts only the exact trio certified by the current build ID");
            failures.Add("trio install is destination-staged and restores every prior state after a mid-commit failure");
            failures.Add("release packaging rejects a missing or empty IoStore trio");
        }
        finally
        {
            try
            {
                if (Directory.Exists(trioRegressionRoot))
                {
                    Directory.Delete(trioRegressionRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of the uniquely named regression folder.
            }
        }

        var singleMonitor = MainForm.ConstrainWindowBoundsForTest(
            new Rectangle(-1000, -400, 5200, 2600),
            new Rectangle(0, 0, 1920, 1080),
            new Size(1440, 960),
            new Size(1800, 1000),
            recenter: true,
            edgeGap: 12);
        Check(
            new Rectangle(0, 0, 1920, 1080).Contains(singleMonitor) &&
            singleMonitor.Width <= 1800 && singleMonitor.Height <= 1000,
            "oversized startup bounds fit one monitor",
            failures,
            output);
        var spannedDesktop = MainForm.ConstrainWindowBoundsForTest(
            new Rectangle(0, 0, 5000, 1800),
            new Rectangle(0, 0, 3840, 1080),
            new Size(1440, 960),
            new Size(1800, 1000),
            recenter: true,
            edgeGap: 12);
        Check(
            spannedDesktop.Width <= 1800 && spannedDesktop.Height <= 1000,
            "combined-monitor work areas cannot create a two-screen window",
            failures,
            output);
        var highDpiDesktop = MainForm.ConstrainWindowBoundsForTest(
            new Rectangle(80, 80, 3200, 1700),
            new Rectangle(0, 0, 3840, 2160),
            new Size(1920, 1280),
            new Size(3600, 2000),
            recenter: true,
            edgeGap: 24);
        Check(
            highDpiDesktop.Width >= 1920 && highDpiDesktop.Height >= 1280,
            "startup caps remain DPI-scaled above the logical minimum",
            failures,
            output);

        Check(
            AdaptiveWindowManager.ResizableBorderStyleForTest(FormBorderStyle.FixedDialog) == FormBorderStyle.Sizable &&
            AdaptiveWindowManager.ResizableBorderStyleForTest(FormBorderStyle.FixedSingle) == FormBorderStyle.Sizable &&
            AdaptiveWindowManager.ResizableBorderStyleForTest(FormBorderStyle.FixedToolWindow) == FormBorderStyle.SizableToolWindow,
            "fixed app dialogs are upgraded to resizable window chrome",
            failures,
            output);
        var compactWindow = AdaptiveWindowManager.ConstrainWindowBoundsForTest(
            new Rectangle(-200, -100, 1500, 1100),
            new Rectangle(0, 0, 800, 600),
            new Size(920, 700),
            edgeGap: 12);
        Check(
            new Rectangle(0, 0, 800, 600).Contains(compactWindow.Bounds) &&
            compactWindow.MinimumSize.Width <= compactWindow.Bounds.Width &&
            compactWindow.MinimumSize.Height <= compactWindow.Bounds.Height,
            "resizable windows lower oversized minimums to fit a small display",
            failures,
            output);

        output.WriteLine(failures.Count == 0
            ? "release regressions: PASS"
            : $"release regressions: FAIL ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    private static NativeSuitProject CreateCertifiedNightwingCapeAdapterProject()
    {
        const string nightwingPlayable =
            "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Default_Playable";
        return new NativeSuitProject
        {
            AllowSyntheticPairedCapeVisualOverlayFixture = true,
            PlayableTemplate = new TemplateRecord
            {
                PackagePath = nightwingPlayable,
                Stem = "BP_Nightwing_Default_Playable",
                Character = "Nightwing",
                Role = "playable"
            },
            BaseProfile = new SuitBaseProfile
            {
                VisualBasePackage =
                    "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Default_Cutscene",
                VisualBaseKind = "cutscene",
                VisualFamily = "Nightwing",
                GameplayDonorPackage = nightwingPlayable,
                GameplayFamily = "Nightwing",
                Eligibility = "ready"
            },
            // Prove that certification replaces a glide-only donor's wingsuit pose with the exact
            // authored Cape + Torso donor's traversal/glide blocks while retaining Nightwing's
            // general gameplay graph.
            UseCustomArchetype = true,
            GliderAutoEnabledCustomArchetype = true,
            GliderAnimLas = "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Batman",
            GliderAnimMas = "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Batman",
            PartGrafts =
            [
                new SavedPartGraft
                {
                    Slot = "Cape",
                    Label = "Batman Animated Series native cosmetic cape",
                    InstanceId = "nightwing-adapter-cosmetic-cape",
                    OccupancyGroup = "cape.cosmetic",
                    ResolvedComponent = "Cape_2",
                    Playable = AnimatedSeriesCapeDonor("playable", isGlider: false),
                    Cutscene = AnimatedSeriesCapeDonor("cutscene", isGlider: false)
                },
                new SavedPartGraft
                {
                    Slot = "Cape",
                    Label = "Batman Animated Series paired glide cape",
                    IsGlider = true,
                    InstanceId = "nightwing-adapter-glide-cape",
                    OccupancyGroup = "glider.primary",
                    ResolvedComponent = "Cape",
                    Playable = AnimatedSeriesCapeDonor("playable", isGlider: true),
                    Cutscene = AnimatedSeriesCapeDonor("cutscene", isGlider: true)
                }
            ]
        };
    }

    private static SavedPartGraftDonor AnimatedSeriesCapeDonor(string context, bool isGlider)
    {
        var playable = context.Equals("playable", StringComparison.OrdinalIgnoreCase);
        var source = playable
            ? "/Game/Characters/Minifig/Batman/BP_Batman_AnimatedSeries_Playable"
            : "/Game/Characters/Minifig/Batman/BP_Batman_AnimatedSeries_Cutscene";
        if (isGlider)
        {
            return new SavedPartGraftDonor
            {
                SourcePackagePath = source,
                Context = playable ? "playable" : "cutscene",
                Slot = "Torso",
                Stem = playable
                    ? "BP_Batman_AnimatedSeries_Playable"
                    : "BP_Batman_AnimatedSeries_Cutscene",
                MeshKind = "SkeletalMesh",
                SemanticKind = "Torso",
                MeshObjectPath =
                    "/Game/Characters/Attachments/Cape/SK_CAPE_Glide.SK_CAPE_Glide",
                AnimClassObjectName = "ABP_Cape_Glide_C",
                AnimClassPackagePath = "/Game/Characters/Attachments/Cape/ABP_Cape_Glide",
                AnimClassObjectPath =
                    "/Game/Characters/Attachments/Cape/ABP_Cape_Glide.ABP_Cape_Glide_C",
                TemplatePackagePath = source,
                TemplateSlot = "Torso",
                TemplateComponentClass = playable
                    ? "SkeletalMeshComponentBudgeted"
                    : "SkeletalMeshComponent",
                ParentComponentOrVariableName = playable ? "CharacterMesh0" : "Mesh (CharacterMesh0)",
                AttachSocket = "Chest_Socket",
                Materials = playable
                    ?
                    [
                        MaterialRef(
                            "MI_CAPE_Spiked_Glide_BatmanAnimatedSeries",
                            "/Game/Characters/Attachments/Cape/Spiked/MI_CAPE_Spiked_Glide_BatmanAnimatedSeries"),
                        MaterialRef(
                            "MI_CAPE_Spiked_Glide_BatmanAnimatedSeries_LOD1",
                            "/Game/Characters/Attachments/Cape/Spiked/MI_CAPE_Spiked_Glide_BatmanAnimatedSeries_LOD1")
                    ]
                    :
                    [
                        MaterialRef(
                            "MI_Cape_Glide_Batman_AnimatedSeries_LOD0_CUT",
                            "/Game/Characters/Attachments/Cape/Spiked/MI_Cape_Glide_Batman_AnimatedSeries_LOD0_CUT"),
                        MaterialRef(
                            "MI_Cape_Glide_Batman_AnimatedSeries_LOD1_CUT",
                            "/Game/Characters/Attachments/Cape/Spiked/MI_Cape_Glide_Batman_AnimatedSeries_LOD1_CUT")
                    ],
                ComponentTags = ["TtCharacterAsset.Torso", "Glider"]
            };
        }

        var lod0Name = playable
            ? "MI_Cape_Batman_AnimatedSeries_LOD0"
            : "MI_Cape_Batman_AnimatedSeriesLOD0_CUT";
        var lod0Package =
            "/Game/Characters/Attachments/Cape/TwoHole_Spiked/Materials/" + lod0Name;
        var lod1Name = playable
            ? "MI_Cape_Batman_AnimatedSeries_LOD1"
            : "MI_Cape_Batman_AnimatedSeries_LOD1_CUT";
        var lod1Package =
            "/Game/Characters/Attachments/Cape/TwoHole_Spiked/Materials/" + lod1Name;
        return new SavedPartGraftDonor
        {
            SourcePackagePath = source,
            Context = playable ? "playable" : "cutscene",
            Slot = "Cape",
            Stem = playable
                ? "BP_Batman_AnimatedSeries_Playable"
                : "BP_Batman_AnimatedSeries_Cutscene",
            MeshKind = "SkeletalMesh",
            SemanticKind = "Cape",
            MeshObjectPath = playable
                ? "/Game/Characters/Attachments/Cape/TwoHole_Spiked/SK_CAPE_TwoHole_Spiked.SK_CAPE_TwoHole_Spiked"
                : "/Game/Characters/Attachments/Cape/TwoHole_Spiked/SK_CAPE_TwoHole_Spiked_Advanced.SK_CAPE_TwoHole_Spiked_Advanced",
            TemplatePackagePath = source,
            TemplateSlot = "Cape",
            TemplateComponentClass = playable
                ? "SkeletalMeshComponentBudgeted"
                : "SkeletalMeshComponent",
            ParentComponentOrVariableName = playable ? "CharacterMesh0" : "Mesh (CharacterMesh0)",
            AttachSocket = "Root",
            Materials =
            [
                MaterialRef(lod0Name, lod0Package),
                MaterialRef(playable ? lod1Name : lod0Name, playable ? lod1Package : lod0Package),
                MaterialRef(lod1Name, lod1Package)
            ],
            ComponentTags = ["TtCharacterAsset.Cape", "Cape"]
        };
    }

    private sealed record VisualOverlayRegressionFixture(
        NativeSuitProject Project,
        NativeSuitPartIndex Index,
        SavedPartGraft CosmeticCape,
        SavedPartGraft GlideCape,
        IReadOnlyList<SavedPartGraft> OverlayGrafts,
        PairedCapeVisualOverlayService.IdentityMaterials IdentityMaterials);

    private static VisualOverlayRegressionFixture CreateVisualOverlayRegressionFixture()
    {
        const string nightwingPlayable =
            "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Default_Playable";
        const string nightwingCutscene =
            "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Default_Cutscene";
        const string animatedPlayable =
            "/Game/Characters/Minifig/Batman/BP_Batman_AnimatedSeries_Playable";
        const string animatedCutscene =
            "/Game/Characters/Minifig/Batman/BP_Batman_AnimatedSeries_Cutscene";
        const string grayGhostPlayable =
            "/Game/Characters/Minifig/Batman/BP_Batman_GrayGhost_Playable";
        const string grayGhostCutscene =
            "/Game/Characters/Minifig/Batman/BP_Batman_GrayGhost_Cutscene";

        var index = new NativeSuitPartIndex
        {
            Parts =
            [
                VisualOverlayIndexPart(nightwingPlayable, "playable", "Head", "StaticMeshComponentBudgeted",
                    "StaticMesh", "/Game/Characters/Attachments/Hair/SM_HAIR_ShortWavyPartRight.SM_HAIR_ShortWavyPartRight",
                    animObject: "", animPackage: ""),
                VisualOverlayIndexPart(nightwingCutscene, "cutscene", "Head", "StaticMeshComponent",
                    "StaticMesh", "/Game/Characters/Attachments/Hair/SM_HAIR_ShortWavyPartRight.SM_HAIR_ShortWavyPartRight",
                    animObject: "", animPackage: ""),
                VisualOverlayIndexPart(nightwingPlayable, "playable", "Face", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", "/Game/Characters/Heads/Faces/SK_FACE_Superhero.SK_FACE_Superhero",
                    animObject: "ABP_LEGOface_Superhero_C", animPackage: "/Game/Characters/Heads/Faces/ABP_LEGOface_Superhero"),
                VisualOverlayIndexPart(nightwingCutscene, "cutscene", "Face", "SkeletalMeshComponent",
                    "SkeletalMesh", "/Game/Characters/Heads/Faces/SK_FACE_Superhero.SK_FACE_Superhero",
                    animObject: "ABP_LEGOface_Superhero_C", animPackage: "/Game/Characters/Heads/Faces/ABP_LEGOface_Superhero"),

                // The cosmetic donor is the preferred shell, but its skeletal Head cannot host
                // Nightwing's native static hair field without changing the reflected schema.
                VisualOverlayIndexPart(animatedPlayable, "playable", "Head", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", "/Game/Characters/Heads/Batman/SK_Head_Batman.SK_Head_Batman"),
                VisualOverlayIndexPart(animatedCutscene, "cutscene", "Head", "SkeletalMeshComponent",
                    "SkeletalMesh", "/Game/Characters/Heads/Batman/SK_Head_Batman.SK_Head_Batman"),
                VisualOverlayIndexPart(animatedPlayable, "playable", "Face", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", "/Game/Characters/Heads/Faces/SK_FACE_Batman.SK_FACE_Batman",
                    animObject: "ABP_LEGOface_Batman_C", animPackage: "/Game/Characters/Heads/Faces/ABP_LEGOface_Batman"),
                VisualOverlayIndexPart(animatedCutscene, "cutscene", "Face", "SkeletalMeshComponent",
                    "SkeletalMesh", "/Game/Characters/Heads/Faces/SK_FACE_Batman.SK_FACE_Batman",
                    animObject: "ABP_LEGOface_Batman_C", animPackage: "/Game/Characters/Heads/Faces/ABP_LEGOface_Batman"),
                VisualOverlayIndexPart(animatedPlayable, "playable", "Cape", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", AnimatedSeriesCapeDonor("playable", false).MeshObjectPath),
                VisualOverlayIndexPart(animatedCutscene, "cutscene", "Cape", "SkeletalMeshComponent",
                    "SkeletalMesh", AnimatedSeriesCapeDonor("cutscene", false).MeshObjectPath),
                VisualOverlayIndexPart(animatedPlayable, "playable", "Torso", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", AnimatedSeriesCapeDonor("playable", true).MeshObjectPath,
                    animObject: "ABP_Cape_Glide_C", animPackage: "/Game/Characters/Attachments/Cape/ABP_Cape_Glide"),
                VisualOverlayIndexPart(animatedCutscene, "cutscene", "Torso", "SkeletalMeshComponent",
                    "SkeletalMesh", AnimatedSeriesCapeDonor("cutscene", true).MeshObjectPath,
                    animObject: "ABP_Cape_Glide_C", animPackage: "/Game/Characters/Attachments/Cape/ABP_Cape_Glide"),

                // Gray Ghost owns the same authored Cape/Torso field classes and a static Head,
                // making it the first safe Batman scaffold for the Nightwing overlay.
                VisualOverlayIndexPart(grayGhostPlayable, "playable", "Head", "StaticMeshComponentBudgeted",
                    "StaticMesh", "/Game/Characters/Attachments/Hair/SM_HAIR_GrayGhost.SM_HAIR_GrayGhost"),
                VisualOverlayIndexPart(grayGhostCutscene, "cutscene", "Head", "StaticMeshComponent",
                    "StaticMesh", "/Game/Characters/Attachments/Hair/SM_HAIR_GrayGhost.SM_HAIR_GrayGhost"),
                VisualOverlayIndexPart(grayGhostPlayable, "playable", "Face", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", "/Game/Characters/Heads/Faces/SK_FACE_Batman.SK_FACE_Batman",
                    animObject: "ABP_LEGOface_Batman_C", animPackage: "/Game/Characters/Heads/Faces/ABP_LEGOface_Batman"),
                VisualOverlayIndexPart(grayGhostCutscene, "cutscene", "Face", "SkeletalMeshComponent",
                    "SkeletalMesh", "/Game/Characters/Heads/Faces/SK_FACE_Batman.SK_FACE_Batman",
                    animObject: "ABP_LEGOface_Batman_C", animPackage: "/Game/Characters/Heads/Faces/ABP_LEGOface_Batman"),
                VisualOverlayIndexPart(grayGhostPlayable, "playable", "Cape", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", "/Game/Characters/Attachments/Cape/SK_CAPE_GrayGhost.SK_CAPE_GrayGhost"),
                VisualOverlayIndexPart(grayGhostCutscene, "cutscene", "Cape", "SkeletalMeshComponent",
                    "SkeletalMesh", "/Game/Characters/Attachments/Cape/SK_CAPE_GrayGhost.SK_CAPE_GrayGhost"),
                VisualOverlayIndexPart(grayGhostPlayable, "playable", "Torso", "SkeletalMeshComponentBudgeted",
                    "SkeletalMesh", "/Game/Characters/Attachments/Cape/SK_CAPE_Glide.SK_CAPE_Glide",
                    animObject: "ABP_Cape_Glide_C", animPackage: "/Game/Characters/Attachments/Cape/ABP_Cape_Glide"),
                VisualOverlayIndexPart(grayGhostCutscene, "cutscene", "Torso", "SkeletalMeshComponent",
                    "SkeletalMesh", "/Game/Characters/Attachments/Cape/SK_CAPE_Glide.SK_CAPE_Glide",
                    animObject: "ABP_Cape_Glide_C", animPackage: "/Game/Characters/Attachments/Cape/ABP_Cape_Glide"),
            ]
        };

        var overlayGrafts = new[]
        {
            VisualOverlayGraft(index, nightwingPlayable, nightwingCutscene, "Face"),
            VisualOverlayGraft(index, nightwingPlayable, nightwingCutscene, "Head"),
        };
        var project = CreateCertifiedNightwingCapeAdapterProject();
        project.AllowSyntheticPairedCapeVisualOverlayFixture = false;
        var cosmetic = project.PartGrafts.Single(graft => !graft.IsGlider);
        var glider = project.PartGrafts.Single(graft => graft.IsGlider);
        var identity = new PairedCapeVisualOverlayService.IdentityMaterials(
            "/Game/Characters/Minifig/Nightwing/Materials/MI_Nightwing",
            "/Game/Characters/Minifig/Nightwing/Materials/MI_Nightwing_CUT",
            "/Game/Characters/Heads/Faces/MI_FACE_Nightwing",
            "/Game/Characters/Heads/Faces/MI_FACE_Nightwing_CUT");
        project.PairedCapeAdapter = new PairedCapeAdapterProfile
        {
            SchemaVersion = GliderService.PairedCapeAdapterSchemaVersion,
            AdapterId = "visual-overlay-regression",
            GameplayDonorPackage = nightwingPlayable,
            NativeGliderComponent = "Cape",
            AuthoredShellPlayablePackage = grayGhostPlayable,
            AuthoredShellCutscenePackage = grayGhostCutscene,
            CosmeticCapeGraftInstanceId = cosmetic.InstanceId,
            GlideCapeGraftInstanceId = glider.InstanceId,
            CosmeticPlayableSourcePackage = cosmetic.Playable!.SourcePackagePath,
            CosmeticCutsceneSourcePackage = cosmetic.Cutscene!.SourcePackagePath,
            GliderPlayableSourcePackage = glider.Playable!.SourcePackagePath,
            GliderCutsceneSourcePackage = glider.Cutscene!.SourcePackagePath,
            PairedAnimClassObjectName = "ABP_Cape_Glide_C",
            GlideAnimLasPackage = project.GliderAnimLas,
            GlideAnimMasPackage = project.GliderAnimMas,
            ResolvedCosmeticComponent = "Cape",
            ResolvedGliderComponent = "Torso",
            VisualOverlay = new PairedCapeVisualOverlayProfile
            {
                VisualPlayableSourcePackage = nightwingPlayable,
                VisualCutsceneSourcePackage = nightwingCutscene,
                ComponentGrafts = overlayGrafts.ToList(),
                PlayableBodyMaterialPackage = identity.PlayableBody,
                CutsceneBodyMaterialPackage = identity.CutsceneBody,
                PlayableFaceMaterialPackage = identity.PlayableFace,
                CutsceneFaceMaterialPackage = identity.CutsceneFace,
            }
        };
        return new(project, index, cosmetic, glider, overlayGrafts, identity);
    }

    private static SavedPartGraft VisualOverlayGraft(
        NativeSuitPartIndex index,
        string playablePackage,
        string cutscenePackage,
        string slot)
    {
        var playable = index.Parts.Single(part =>
            part.SourcePackagePath == playablePackage && part.Context == "playable" && part.Slot == slot);
        var cutscene = index.Parts.Single(part =>
            part.SourcePackagePath == cutscenePackage && part.Context == "cutscene" && part.Slot == slot);
        return new SavedPartGraft
        {
            Slot = slot,
            Label = "paired-cape visual base " + slot,
            InstanceId = "paired-cape-overlay-" + slot.ToLowerInvariant(),
            OccupancyGroup = "paired-cape.visual." + slot.ToLowerInvariant(),
            Playable = VisualOverlayDonor(playable),
            Cutscene = VisualOverlayDonor(cutscene),
        };
    }

    private static SavedPartGraftDonor VisualOverlayDonor(NativeSuitPartRecord part) => new()
    {
        SourcePackagePath = part.SourcePackagePath,
        Slot = part.Slot,
        Context = part.Context,
        MeshObjectPath = part.MeshObjectPath,
        AnimClassObjectName = part.AnimClassObjectName,
        AnimClassPackagePath = part.AnimClassPackagePath,
        AnimClassObjectPath = part.AnimClassObjectPath,
        Stem = part.Stem,
        MeshKind = part.MeshKind,
        SemanticKind = part.SemanticKind,
        TemplatePackagePath = part.TemplatePackagePath,
        TemplateSlot = part.TemplateSlot,
        TemplateComponentClass = part.TemplateComponentClass,
        ParentComponentOrVariableName = part.ParentComponentOrVariableName,
        AttachSocket = part.AttachSocket,
        Materials = part.Materials.Select(material => MaterialRef(material.ObjectName, material.PackagePath)).ToList(),
        ComponentTags = part.ComponentTags.ToList(),
    };

    private static NativeSuitPartRecord VisualOverlayIndexPart(
        string package,
        string context,
        string slot,
        string componentClass,
        string meshKind,
        string meshObjectPath,
        string animObject = "",
        string animPackage = "")
    {
        var meshObject = meshObjectPath[(meshObjectPath.LastIndexOf('.') + 1)..];
        var nightwingFace = package.Contains("/Nightwing/", StringComparison.OrdinalIgnoreCase) &&
                            slot.Equals("Face", StringComparison.OrdinalIgnoreCase);
        var materialName = nightwingFace
            ? context == "playable" ? "MI_FACE_Nightwing" : "MI_FACE_Nightwing_CUT"
            : "MI_" + slot + "_" + UnrealPathUtil.AssetName(package);
        var materialPackage = nightwingFace
            ? "/Game/Characters/Heads/Faces/" + materialName
            : "/Game/Regression/Materials/" + materialName;
        var tags = slot.Equals("Cape", StringComparison.OrdinalIgnoreCase)
            ? new List<string> { "TtCharacterAsset.Cape", "Cape" }
            : slot.Equals("Torso", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { "TtCharacterAsset.Torso", "Glider" }
                : new List<string> { "TtCharacterAsset." + slot };
        return new NativeSuitPartRecord
        {
            SourcePackagePath = package,
            CharacterFolder = package.Contains("/Nightwing/", StringComparison.OrdinalIgnoreCase) ? "Nightwing" : "Batman",
            Stem = UnrealPathUtil.AssetName(package),
            Context = context,
            Slot = slot,
            ComponentClass = componentClass,
            MeshKind = meshKind,
            MeshObjectName = meshObject,
            MeshPackagePath = meshObjectPath[..meshObjectPath.LastIndexOf('.')],
            MeshObjectPath = meshObjectPath,
            AnimClassObjectName = animObject,
            AnimClassPackagePath = animPackage,
            AnimClassObjectPath = string.IsNullOrWhiteSpace(animObject) ? "" : animPackage + "." + animObject,
            Materials = [MaterialRef(materialName, materialPackage)],
            ComponentTags = tags,
            SemanticKind = slot,
            TemplatePackagePath = package,
            TemplateSlot = slot,
            TemplateComponentClass = componentClass,
            ParentComponentOrVariableName = context == "playable" ? "CharacterMesh0" : "Mesh (CharacterMesh0)",
            AttachSocket = slot.Equals("Head", StringComparison.OrdinalIgnoreCase)
                ? "HeadStud_Attach_Socket"
                : slot.Equals("Face", StringComparison.OrdinalIgnoreCase) ? "Head_Socket" : "Root",
        };
    }

    private static NativeSuitObjectRef MaterialRef(string objectName, string packagePath) => new()
    {
        ObjectName = objectName,
        PackagePath = packagePath,
        ObjectPath = packagePath + "." + objectName,
        ClassName = "MaterialInstanceConstant"
    };

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
