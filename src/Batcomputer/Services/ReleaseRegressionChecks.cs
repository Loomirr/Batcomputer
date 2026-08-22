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
        var savedCapeProject = new NativeSuitProject
        {
            PartGrafts =
            [
                new SavedPartGraft
                {
                    IsGlider = false,
                    Playable = new SavedPartGraftDonor
                    {
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
            recenter: true,
            edgeGap: 12);
        Check(
            spannedDesktop.Width <= 1800 && spannedDesktop.Height <= 1000,
            "combined-monitor work areas cannot create a two-screen window",
            failures,
            output);

        output.WriteLine(failures.Count == 0
            ? "release regressions: PASS"
            : $"release regressions: FAIL ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
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
