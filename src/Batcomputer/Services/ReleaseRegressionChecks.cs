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
            GameAssetRefreshService.AllCharacterFilters.Contains(
                GameAssetRefreshService.AdditionalContentFilter,
                StringComparer.OrdinalIgnoreCase) &&
            GameAssetRefreshService.DeveloperResearchFilters.Contains(
                GameAssetRefreshService.AdditionalContentFilter,
                StringComparer.OrdinalIgnoreCase) &&
            GameAssetRefreshService.AllCharacterFilters.Contains(
                GameAssetRefreshService.GameFeatureContentFilter,
                StringComparer.OrdinalIgnoreCase) &&
            GameAssetRefreshService.DeveloperResearchFilters.Contains(
                GameAssetRefreshService.GameFeatureContentFilter,
                StringComparer.OrdinalIgnoreCase) &&
            GameAssetRefreshService.DlcRootForPaksRoot(
                    Path.Combine("C:", "Games", "LEGOBatmanLotDK", "Content", "Paks"))
                .Equals(
                    Path.Combine("C:", "Games", "LEGOBatmanLotDK", "Content", "DLC"),
                    StringComparison.OrdinalIgnoreCase),
            "first-time and full refresh mount Content/DLC and extract AdditionalContent plus Game Feature DLC",
            failures,
            output);

        var partialDlcDumpRejected = false;
        try
        {
            GameAssetRefreshService.EnsureDlcCharacterCoverageForActivation(
                new GameAssetRefreshService.Result
                {
                    DlcContainersMounted = 4,
                    AdditionalContentAssets = 12,
                    GameFeatureAssets = 7,
                    DlcPlayableAssets = 0,
                    DlcCutsceneAssets = 0,
                });
        }
        catch (InvalidDataException ex)
        {
            partialDlcDumpRejected = ex.Message.Contains(
                "previous extracted dump active",
                StringComparison.OrdinalIgnoreCase);
        }

        var completeDlcDumpAccepted = true;
        var noDlcDumpAccepted = true;
        try
        {
            GameAssetRefreshService.EnsureDlcCharacterCoverageForActivation(
                new GameAssetRefreshService.Result
                {
                    DlcContainersMounted = 4,
                    AdditionalContentAssets = 12,
                    GameFeatureAssets = 20,
                    DlcPlayableAssets = 3,
                    DlcCutsceneAssets = 3,
                });
        }
        catch
        {
            completeDlcDumpAccepted = false;
        }
        try
        {
            GameAssetRefreshService.EnsureDlcCharacterCoverageForActivation(
                new GameAssetRefreshService.Result
                {
                    DlcContainersMounted = 0,
                    AdditionalContentAssets = 0,
                    DlcPlayableAssets = 0,
                    DlcCutsceneAssets = 0,
                });
        }
        catch
        {
            noDlcDumpAccepted = false;
        }
        Check(
            partialDlcDumpRejected && completeDlcDumpAccepted && noDlcDumpAccepted,
            "refresh rejects Batcave-only partial DLC extraction before replacing the active dump",
            failures,
            output);

        var allModTileSummaries = MainForm.ModTileSummaries(
            Enumerable.Range(1, 9).Select(index => new ModProjectService.ModSummary(
                "Mod" + index,
                "Mod " + index,
                Path.Combine("mods", "Mod" + index + ".native-suit-mod-project.json"),
                DateTime.UtcNow.AddMinutes(-index),
                "",
                index)));
        Check(
            allModTileSummaries.Count == 9 &&
            allModTileSummaries[0].ModId == "Mod1" &&
            allModTileSummaries[^1].ModId == "Mod9",
            "Home and Build mod tile projections retain every saved mod instead of hiding older entries",
            failures,
            output);

        var allSavedSuitSummaries = Enumerable.Range(1, 14)
            .Select(index => new SuitProjectService.ProjectSummary(
                "suit" + index,
                "Suit " + index,
                Path.Combine("suits", $"suit{index}.native-suit-project.json"),
                DateTime.UtcNow.AddMinutes(-index),
                "",
                $"/Game/Mods/Suit{index}/BP_Suit{index}_Playable"))
            .ToList();
        var projectedModWorkspaceSuits = MainForm.ModWorkspaceSuitTileSummaries(
            allSavedSuitSummaries,
            new[] { allSavedSuitSummaries[4], allSavedSuitSummaries[1] });
        Check(
            projectedModWorkspaceSuits.Count == 14 &&
            projectedModWorkspaceSuits[0].SlotId == "suit5" &&
            projectedModWorkspaceSuits[1].SlotId == "suit2" &&
            projectedModWorkspaceSuits.Select(summary => summary.SlotId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 14,
            "the active mod workspace shows every saved suit while keeping included suits first",
            failures,
            output);
        Check(
            MainForm.EnabledModSuitCount(
            [
                new ModSuitEntry { SuitId = "enabled", Enabled = true },
                new ModSuitEntry { SuitId = "disabled-one", Enabled = false },
                new ModSuitEntry { SuitId = "disabled-two", Enabled = false },
            ]) == 1 &&
            MainForm.EnabledModSuitCount(
            [
                new ModSuitEntry { SuitId = "disabled-only", Enabled = false },
            ]) == 0,
            "disabled suits remain visible but do not make an otherwise empty mod buildable",
            failures,
            output);

        var iconPropertyTargets = UimdGenService.IconPropertyTargets(new UimdGenService.IconTargets(
            "/Game/Mods/IconProof/Textures/T_Menu",
            "/Game/Mods/IconProof/Textures/T_Suit",
            "/Game/Mods/IconProof/Textures/T_Left",
            "/Game/Mods/IconProof/Textures/T_Right"));
        Check(
            iconPropertyTargets.Count == 4 &&
            iconPropertyTargets.Any(target => target.Role == "menu" && target.PropertyName == "MenuIcon" && target.TargetPath.EndsWith("T_Menu", StringComparison.Ordinal)) &&
            iconPropertyTargets.Any(target => target.Role == "suit" && target.PropertyName == "SuitIcon" && target.TargetPath.EndsWith("T_Suit", StringComparison.Ordinal)) &&
            iconPropertyTargets.Any(target => target.Role == "left" && target.PropertyName == "RightFacing" && target.TargetPath.EndsWith("T_Left", StringComparison.Ordinal)) &&
            iconPropertyTargets.Any(target => target.Role == "right" && target.PropertyName == "LeftFacing" && target.TargetPath.EndsWith("T_Right", StringComparison.Ordinal)),
            "generated UIMDs patch every selected icon by property role even when donor icon discovery is unavailable",
            failures,
            output);

        var legacyPortrait = new GeneratedTextureEntry
        {
            DisplayName = "Legacy portrait",
            Kind = "UI artwork",
            CookProfile = "ui-suit-256-bc7",
            CookWidth = 256,
            CookHeight = 256,
            PackagePath = "/Game/Mods/IconProof/Textures/T_LegacyPortrait",
        };
        var legacySuitIcon = new GeneratedTextureEntry
        {
            DisplayName = "Legacy suit icon",
            Kind = "UI artwork",
            CookProfile = "ui-character-512-bc7",
            CookWidth = 512,
            CookHeight = 512,
            PackagePath = "/Game/Mods/IconProof/Textures/T_LegacySuit",
        };
        var legacyIconProject = new NativeSuitProject
        {
            IconMenu = legacyPortrait.PackagePath,
            IconSuit = legacySuitIcon.PackagePath,
            GeneratedTextures = [legacyPortrait, legacySuitIcon],
        };
        var portraitReimport = MainForm.GeneratedUimdIconRecipeRequirementForTest(
            legacyIconProject,
            legacyPortrait);
        var suitReimport = MainForm.GeneratedUimdIconRecipeRequirementForTest(
            legacyIconProject,
            legacySuitIcon);
        Check(
            portraitReimport is { Kind: "Character icon", CookProfile: "ui-character-512-bc7", Size: 512 } &&
            suitReimport is { Kind: "Suit selector icon", CookProfile: "ui-suit-256-bc7", Size: 256 },
            "manual icon reimport follows its UIMD role, upgrading portraits to 512px while keeping the suit tile 256px",
            failures,
            output);

        var sharedReleaseIcon = "/Game/Mods/IconProof/Textures/T_SharedReleaseIcon";
        var releaseIconPackages = ModReleaseValidationService.ReleaseGeneratedTexturePackages(
        [
            new ModReleaseValidationService.SuitInput(
                new ModSuitEntry { SuitId = "owner", Enabled = true },
                "owner.json",
                new NativeSuitProject
                {
                    SlotId = "owner",
                    GeneratedTextures =
                    [
                        new GeneratedTextureEntry { PackagePath = sharedReleaseIcon },
                    ],
                }),
            new ModReleaseValidationService.SuitInput(
                new ModSuitEntry { SuitId = "disabled-owner", Enabled = false },
                "disabled.json",
                new NativeSuitProject
                {
                    SlotId = "disabled-owner",
                    GeneratedTextures =
                    [
                        new GeneratedTextureEntry { PackagePath = "/Game/Mods/IconProof/Textures/T_Disabled" },
                    ],
                }),
        ]);
        Check(
            releaseIconPackages.Contains(sharedReleaseIcon) &&
            !releaseIconPackages.Contains("/Game/Mods/IconProof/Textures/T_Disabled"),
            "icon preflight accepts assets generated by another enabled suit in the same release but not disabled content",
            failures,
            output);

        var syntheticGamePaks = Path.Combine(
            "C:\\",
            "Games",
            "LEGOBatmanLotDK",
            "Content",
            "Paks");
        var syntheticSameVolumeOutput = Path.Combine("C:\\", "Batcomputer", "Extracts", "Current");
        var syntheticOtherVolumeOutput = Path.Combine("D:\\", "Batcomputer", "Extracts", "Current");
        var sameVolumeMountRoot = GameAssetRefreshService.ResolveCombinedContainerMountRoot(
            syntheticGamePaks,
            syntheticSameVolumeOutput,
            "regression");
        var crossVolumeMountRoot = GameAssetRefreshService.ResolveCombinedContainerMountRoot(
            syntheticGamePaks,
            syntheticOtherVolumeOutput,
            "regression");
        Check(
            sameVolumeMountRoot.Equals(
                Path.Combine(Path.GetFullPath(syntheticSameVolumeOutput), ".retoc-base-and-dlc-input"),
                StringComparison.OrdinalIgnoreCase) &&
            crossVolumeMountRoot.Equals(
                Path.Combine(
                    Path.GetFullPath(Path.Combine("C:\\", "Games", "LEGOBatmanLotDK", "Content")),
                    ".batcomputer-retoc-base-and-dlc-input-regression"),
                StringComparison.OrdinalIgnoreCase),
            "cross-volume DLC refresh keeps the legacy same-volume mount but places hard links on the game volume when needed",
            failures,
            output);

        var dlcMountFixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-dlc-mount-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseContainers = Path.Combine(dlcMountFixtureRoot, "Paks");
            var dlcContainers = Path.Combine(dlcMountFixtureRoot, "DLC");
            var outputRoot = Path.Combine(dlcMountFixtureRoot, "Output");
            Directory.CreateDirectory(baseContainers);
            Directory.CreateDirectory(dlcContainers);
            Directory.CreateDirectory(outputRoot);
            foreach (var extension in new[] { ".utoc", ".ucas", ".pak" })
            {
                File.WriteAllText(Path.Combine(baseContainers, "pakchunk0-Windows" + extension), "base-" + extension);
                File.WriteAllText(Path.Combine(dlcContainers, "pakchunk101-Windows" + extension), "dlc-" + extension);
            }

            var mount = GameAssetRefreshService.CreateCombinedContainerMount(
                baseContainers,
                dlcContainers,
                outputRoot);
            var mountWorks = new[]
                {
                    "pakchunk0-Windows.utoc",
                    "pakchunk0-Windows.ucas",
                    "pakchunk0-Windows.pak",
                    "pakchunk101-Windows.utoc",
                    "pakchunk101-Windows.ucas",
                    "pakchunk101-Windows.pak",
                }.All(name => File.Exists(Path.Combine(mount, name))) &&
                File.ReadAllText(Path.Combine(mount, "pakchunk0-Windows.utoc")) == "base-.utoc" &&
                File.ReadAllText(Path.Combine(mount, "pakchunk101-Windows.utoc")) == "dlc-.utoc";
            GameAssetRefreshService.TryDeleteCombinedContainerMount(mount);

            var markerlessLookalike = Path.Combine(outputRoot, ".retoc-base-and-dlc-input");
            Directory.CreateDirectory(markerlessLookalike);
            var markerlessFile = Path.Combine(markerlessLookalike, "do-not-delete.txt");
            File.WriteAllText(markerlessFile, "not owned by Batcomputer's mount creator");
            GameAssetRefreshService.TryDeleteCombinedContainerMount(markerlessLookalike);
            var markerlessLookalikePreserved = Directory.Exists(markerlessLookalike) &&
                                               File.Exists(markerlessFile);
            Directory.Delete(markerlessLookalike, recursive: true);
            Check(
                mountWorks &&
                !Directory.Exists(mount) &&
                markerlessLookalikePreserved &&
                File.Exists(Path.Combine(baseContainers, "pakchunk0-Windows.utoc")) &&
                File.Exists(Path.Combine(dlcContainers, "pakchunk101-Windows.utoc")),
                "the owned disposable retoc input cleans up exactly while preserving sources and markerless lookalike folders",
                failures,
                output);
        }
        catch (Exception ex)
        {
            Check(
                false,
                "the owned disposable retoc input cleans up exactly while preserving sources and markerless lookalike folders (" + ex.Message + ")",
                failures,
                output);
        }
        finally
        {
            try { Directory.Delete(dlcMountFixtureRoot, recursive: true); } catch { /* best effort */ }
        }

        var characterRootFixture = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-character-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gameRoot = Path.Combine(characterRootFixture, "LEGOBatmanLotDK");
            var contentRoot = Path.Combine(gameRoot, "Content");
            var baseCharacters = Path.Combine(contentRoot, "Characters");
            var dlcCharacters = Path.Combine(
                contentRoot,
                "AdditionalContent",
                "DlcPack",
                "Content",
                "Characters");
            var pluginCharacters = Path.Combine(
                gameRoot,
                "Plugins",
                "GameFeatures",
                "DLC_BeyondPack",
                "Content",
                "Characters");
            var unrelatedCharacters = Path.Combine(
                contentRoot,
                "UnrelatedLargeTree",
                "Characters");
            Directory.CreateDirectory(baseCharacters);
            Directory.CreateDirectory(dlcCharacters);
            Directory.CreateDirectory(pluginCharacters);
            Directory.CreateDirectory(unrelatedCharacters);

            var basePlayable = Path.Combine(baseCharacters, "Minifig", "Batman", "BP_Batman_Base_Playable.uasset");
            var pluginPlayable = Path.Combine(pluginCharacters, "Minifig", "Batman", "BP_Batman_Beyond_Playable.uasset");
            var pluginCutscene = Path.Combine(pluginCharacters, "Minifig", "Batman", "BP_Batman_Beyond_Cutscene.uasset");
            var pluginDcmd = Path.Combine(pluginCharacters, "Minifig", "Batman", "DA_DCMD_Batman_Beyond_Playable.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(basePlayable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPlayable)!);
            File.WriteAllText(basePlayable, "base");
            File.WriteAllText(pluginPlayable, "plugin-playable");
            File.WriteAllText(pluginCutscene, "plugin-cutscene");
            File.WriteAllText(pluginDcmd, "plugin-dcmd");

            var dlcBlueprintCounts = GameAssetRefreshService.CountDlcCharacterBlueprintsForTest(
                contentRoot,
                [pluginPlayable, pluginCutscene, pluginDcmd]);

            Check(
                CharacterContentRootService.Enumerate(contentRoot)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals([baseCharacters, dlcCharacters, pluginCharacters]) &&
                string.Equals(
                    ExtractedPackagePathService.PackagePathFromFile(contentRoot, pluginPlayable),
                    "/DLC_BeyondPack/Characters/Minifig/Batman/BP_Batman_Beyond_Playable",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    ExtractedPackagePathService.ResolvePackageUasset(
                        contentRoot,
                        "/DLC_BeyondPack/Characters/Minifig/Batman/BP_Batman_Beyond_Playable"),
                    pluginPlayable,
                    StringComparison.OrdinalIgnoreCase) &&
                BaseEligibilityService.IsGameplayDonorPackage(
                    "/DLC_BeyondPack/Characters/Minifig/Batman/BP_Batman_Beyond_Playable") &&
                MainForm.TemplateFromUassetForTest(pluginPlayable, "playable", contentRoot)?.PackagePath ==
                    "/DLC_BeyondPack/Characters/Minifig/Batman/BP_Batman_Beyond_Playable" &&
                ExtractedPackagePathService.ResolvePackageBase(
                    contentRoot,
                    "/DLC_BeyondPack/../DLC_Missing/Characters/BP_Escape") is null &&
                ExtractedPackagePathService.ResolvePackageBase(
                    contentRoot,
                    "/DLC_NotInstalled/Characters/BP_Missing") is null &&
                BaseCharacterPicker.EnumerateExtractedVisualPackages(contentRoot, playablesOnly: true)
                    .Contains(
                        "/DLC_BeyondPack/Characters/Minifig/Batman/BP_Batman_Beyond_Playable",
                        StringComparer.OrdinalIgnoreCase) &&
                dlcBlueprintCounts == (Playables: 1, Cutscenes: 1) &&
                string.Equals(
                    GameAssetRefreshService.FindContentRootForTest(characterRootFixture),
                    contentRoot,
                    StringComparison.OrdinalIgnoreCase),
                "character discovery and package resolution include real Game Feature DLC mounts without scanning unrelated folders",
                failures,
                output);
        }
        catch (Exception ex)
        {
            Check(
                false,
                "character discovery includes base, AdditionalContent, and Game Feature roots (" + ex.Message + ")",
                failures,
                output);
        }
        finally
        {
            try { Directory.Delete(characterRootFixture, recursive: true); } catch { /* best effort */ }
        }

        Check(
            new[]
            {
                GameAssetRefreshService.AllCharacterFilters,
                GameAssetRefreshService.DeveloperResearchFilters,
            }.All(filters => GameAssetRefreshService.FiltersRecursivelyCover(
                filters,
                GameAssetRefreshService.CharacterMaterialsFilter)),
            "first-time and full refresh recursively extract Content/Characters/Materials",
            failures,
            output);

        var materialCatalogRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-extracted-material-catalog-" + Guid.NewGuid().ToString("N"));
        var priorCatalogSettings = AppSettings.Current;
        try
        {
            var contentA = Path.Combine(materialCatalogRoot, "A", "Content");
            var contentB = Path.Combine(materialCatalogRoot, "B", "Content");
            var deepCowl = Path.Combine(
                contentA,
                "Characters",
                "Materials",
                "MI_Instances",
                "EoM",
                "Controller",
                "MI_BatmanCowlEyes_Hollow.uasset");
            var attachmentCowl = Path.Combine(
                contentA,
                "Characters",
                "Attachments",
                "HAT",
                "BatmanCowl_MoldedEyes",
                "Materials",
                "MI_BatmanCowlEyes_Molded_Black.uasset");
            var faceMi = Path.Combine(
                contentA,
                "Characters",
                "Attachments",
                "Face",
                "RegressionFace",
                "MI_FACE_Regression.uasset");
            var topLevelMi = Path.Combine(
                contentA,
                "Characters",
                "Materials",
                "MI_RegressionOnlyA_D71A4F9E.uasset");
            var wingsuitMi = Path.Combine(contentA, "Models", "Gadgets", "MI_DECAL_Wingsuit_Test.uasset");
            var pluginMi = Path.Combine(
                materialCatalogRoot,
                "A",
                "Plugins",
                "GameFeatures",
                "DLC_BeyondPack",
                "Content",
                "Characters",
                "Materials",
                "MI_Beyond_Test.uasset");
            var masterMaterial = Path.Combine(contentA, "Characters", "Materials", "M_Master.uasset");
            var wrongExtension = Path.Combine(contentA, "Characters", "Materials", "MI_NotCooked.txt");
            var replacementMi = Path.Combine(contentB, "Characters", "Materials", "MI_Replacement.uasset");
            foreach (var path in new[]
                     {
                         deepCowl,
                         attachmentCowl,
                         faceMi,
                          topLevelMi,
                          wingsuitMi,
                          pluginMi,
                         masterMaterial,
                         wrongExtension,
                         replacementMi,
                     })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[] { 0x42 });
            }

            ExtractedMaterialCatalogService.Invalidate();
            var extractedA = ExtractedMaterialCatalogService.ExtractedForRoot(contentA);
            var afterCacheMi = Path.Combine(
                contentA,
                "Characters",
                "Materials",
                "MI_AfterCacheRefresh.uasset");
            File.WriteAllBytes(afterCacheMi, new byte[] { 0x43 });
            var cachedA = ExtractedMaterialCatalogService.ExtractedForRoot(contentA);
            ExtractedMaterialCatalogService.Invalidate();
            var refreshedA = ExtractedMaterialCatalogService.ExtractedForRoot(contentA);
            var extractedB = ExtractedMaterialCatalogService.ExtractedForRoot(contentB);
            var shipped = new[]
            {
                new GameDataAsset
                {
                    Path = "/game/characters/materials/mi_instances/eom/controller/mi_batmancowleyes_hollow",
                    Class = "MaterialInstanceConstant",
                },
                new GameDataAsset
                {
                    Path = "/Game/Characters/Attachments/HAT/MI_ShippedOnly",
                    Class = "MaterialInstanceConstant",
                },
            };
            var merged = ExtractedMaterialCatalogService.MergeForRegression(shipped, extractedA);
            var missingRootFallback = ExtractedMaterialCatalogService.MergeForRegression(
                shipped,
                ExtractedMaterialCatalogService.ExtractedForRoot(Path.Combine(materialCatalogRoot, "missing")));

            AppSettings.Current = new AppSettings { ExtractedContentRoot = contentA };
            ExtractedMaterialCatalogService.Invalidate();
            var centralizedA = GameDataService.Instance.AssetsOfClass("MaterialInstanceConstant").ToList();
            var centralizedFaces = AttachmentCatalogService.FaceMaterials("RegressionFace").ToList();
            AppSettings.Current = new AppSettings { ExtractedContentRoot = contentB };
            var centralizedB = GameDataService.Instance.AssetsOfClass("MaterialInstanceConstant").ToList();

            var deepPackage = "/Game/Characters/Materials/MI_Instances/EoM/Controller/MI_BatmanCowlEyes_Hollow";
            var attachmentPackage = "/Game/Characters/Attachments/HAT/BatmanCowl_MoldedEyes/Materials/MI_BatmanCowlEyes_Molded_Black";
            var rootAOnlyPackage = "/Game/Characters/Materials/MI_RegressionOnlyA_D71A4F9E";
            var catalogOk =
                extractedA.Count == 6 &&
                cachedA.Count == 6 &&
                refreshedA.Count == 7 &&
                refreshedA.Any(asset => asset.Path.EndsWith(
                    "/MI_AfterCacheRefresh",
                    StringComparison.OrdinalIgnoreCase)) &&
                extractedA.Any(asset => asset.Path.Equals(deepPackage, StringComparison.OrdinalIgnoreCase)) &&
                extractedA.Any(asset => asset.Path.Equals(attachmentPackage, StringComparison.OrdinalIgnoreCase)) &&
                extractedA.Any(asset => asset.Path.Equals(
                    "/Game/Models/Gadgets/MI_DECAL_Wingsuit_Test",
                    StringComparison.OrdinalIgnoreCase)) &&
                extractedA.Any(asset => asset.Path.Equals(
                    "/DLC_BeyondPack/Characters/Materials/MI_Beyond_Test",
                    StringComparison.OrdinalIgnoreCase)) &&
                extractedA.All(asset => !asset.Path.EndsWith("M_Master", StringComparison.OrdinalIgnoreCase) &&
                                        !asset.Path.EndsWith("MI_NotCooked", StringComparison.OrdinalIgnoreCase)) &&
                extractedB.Count == 1 &&
                extractedB[0].Path.EndsWith("/MI_Replacement", StringComparison.OrdinalIgnoreCase) &&
                merged.Count == 7 &&
                merged.Count(asset => asset.Path.Equals(deepPackage, StringComparison.OrdinalIgnoreCase)) == 1 &&
                merged.Count(asset => asset.Path.Contains("BatmanCowl", StringComparison.OrdinalIgnoreCase)) == 2 &&
                merged.Select(asset => MainForm.MaterialGroupFolder(asset.Path))
                    .Contains("Characters/Materials", StringComparer.OrdinalIgnoreCase) &&
                merged.Select(asset => MainForm.MaterialGroupFolder(asset.Path))
                    .Contains("DLC_BeyondPack/Characters", StringComparer.OrdinalIgnoreCase) &&
                MaterialCatalogPicker.MatchesCharacter(deepPackage, "Batman") &&
                MaterialCatalogPicker.MatchesCharacter(attachmentPackage, "Batman") &&
                missingRootFallback.Count == shipped.Length &&
                centralizedA.Any(asset => asset.Path.Equals(deepPackage, StringComparison.OrdinalIgnoreCase)) &&
                centralizedA.Any(asset => asset.Path.Equals(rootAOnlyPackage, StringComparison.OrdinalIgnoreCase)) &&
                centralizedA.Any(asset => asset.Path.EndsWith(
                    "/MI_DECAL_Wingsuit_Test",
                    StringComparison.OrdinalIgnoreCase)) &&
                centralizedA.Any(asset => asset.Path.Equals(
                    "/DLC_BeyondPack/Characters/Materials/MI_Beyond_Test",
                    StringComparison.OrdinalIgnoreCase)) &&
                centralizedFaces.Count == 1 &&
                centralizedFaces[0].Path.EndsWith("/MI_FACE_Regression", StringComparison.OrdinalIgnoreCase) &&
                centralizedB.Any(asset => asset.Path.EndsWith("/MI_Replacement", StringComparison.OrdinalIgnoreCase)) &&
                centralizedB.All(asset => !asset.Path.Equals(rootAOnlyPackage, StringComparison.OrdinalIgnoreCase));
            Check(
                catalogOk,
                "every material browser merges base-game and Game Feature MIs, deduplicates paths, and follows root changes",
                failures,
                output);
        }
        catch (Exception ex)
        {
            Check(
                false,
                $"every material browser merges base-game and Game Feature MIs, deduplicates paths, and follows root changes ({ex.Message})",
                failures,
                output);
        }
        finally
        {
            AppSettings.Current = priorCatalogSettings;
            ExtractedMaterialCatalogService.Invalidate();
            try { Directory.Delete(materialCatalogRoot, recursive: true); } catch { /* best effort */ }
        }

        var projectFaceLoaderCalls = 0;
        var workspaceFaceLoaderCalls = 0;
        var partFaceLoaderCalls = 0;
        var projectFaceEnumerations = 0;
        var workspaceFaceEnumerations = 0;
        var partFaceEnumerations = 0;
        const string faceMaterialA = "/Game/Mods/Test/MI_FACE_A";
        const string faceMaterialB = "/Game/Mods/Test/MI_FACE_B";
        const string faceMaterialC = "/Game/Characters/Attachments/Face/Test/MI_FACE_C";
        const string faceMaterialD = "/Game/Mods/Test/MI_FACE_D";
        var projectFaceMaterials = new[]
        {
            new GeneratedMaterialEntry
            {
                PackagePath = faceMaterialA,
                CompatibleFaceMeshPackagePaths = ["/Game/Faces/SK_Current"],
            },
            new GeneratedMaterialEntry
            {
                PackagePath = faceMaterialD,
                CompatibleFaceMeshPackagePaths = [],
            },
        };
        var workspaceFaceMaterials = new[]
        {
            new GeneratedMaterialEntry
            {
                PackagePath = faceMaterialA.ToLowerInvariant(),
                CompatibleFaceMeshPackagePaths = ["/Game/Faces/SK_WorkspaceA"],
            },
            new GeneratedMaterialEntry
            {
                PackagePath = faceMaterialB,
                CompatibleFaceMeshPackagePaths = ["/Game/Faces/SK_WorkspaceB"],
            },
            new GeneratedMaterialEntry
            {
                PackagePath = faceMaterialD,
                CompatibleFaceMeshPackagePaths = ["/Game/Faces/SK_WorkspaceD"],
            },
        };
        var indexedFaceParts = new[]
        {
            new NativeSuitPartRecord
            {
                Slot = "Face",
                SemanticKind = "Face",
                MeshPackagePath = "/Game/Faces/SK_IndexA",
                Materials = [new NativeSuitObjectRef { ObjectPath = faceMaterialA + ".MI_FACE_A" }],
            },
            new NativeSuitPartRecord
            {
                Slot = "Face",
                SemanticKind = "Face",
                MeshPackagePath = "/Game/Faces/SK_IndexC2",
                Materials = [new NativeSuitObjectRef { PackagePath = faceMaterialC.ToLowerInvariant() }],
            },
            new NativeSuitPartRecord
            {
                Slot = "Face",
                SemanticKind = "Face",
                MeshPackagePath = "/Game/Faces/SK_IndexC1",
                Materials = [new NativeSuitObjectRef { ObjectPath = faceMaterialC + ".MI_FACE_C" }],
            },
            new NativeSuitPartRecord
            {
                Slot = "Torso",
                SemanticKind = "Body",
                MeshPackagePath = "/Game/Faces/SK_MustBeIgnored",
                Materials = [new NativeSuitObjectRef { PackagePath = faceMaterialC }],
            },
        };

        IEnumerable<T> CountFaceEnumeration<T>(IEnumerable<T> source, Action increment)
        {
            increment();
            foreach (var item in source)
            {
                yield return item;
            }
        }

        var faceLookup = FaceMaterialCompatibilityLookup.Build(
            projectMaterialLoader: () =>
            {
                projectFaceLoaderCalls++;
                return CountFaceEnumeration(projectFaceMaterials, () => projectFaceEnumerations++);
            },
            workspaceMaterialLoader: () =>
            {
                workspaceFaceLoaderCalls++;
                return CountFaceEnumeration(workspaceFaceMaterials, () => workspaceFaceEnumerations++);
            },
            partLoader: () =>
            {
                partFaceLoaderCalls++;
                return CountFaceEnumeration(indexedFaceParts, () => partFaceEnumerations++);
            });
        for (var lookupIteration = 0; lookupIteration < 100; lookupIteration++)
        {
            _ = faceLookup.Resolve(faceMaterialA);
            _ = faceLookup.Resolve(faceMaterialB);
            _ = faceLookup.Resolve(faceMaterialC);
            _ = faceLookup.Resolve(faceMaterialD);
        }
        Check(
            faceLookup.Resolve(faceMaterialA).SequenceEqual(
                new[] { "/Game/Faces/SK_Current" },
                StringComparer.OrdinalIgnoreCase) &&
            faceLookup.Resolve(faceMaterialB).SequenceEqual(
                new[] { "/Game/Faces/SK_WorkspaceB" },
                StringComparer.OrdinalIgnoreCase) &&
            faceLookup.Resolve(faceMaterialC).SequenceEqual(
                new[] { "/Game/Faces/SK_IndexC1", "/Game/Faces/SK_IndexC2" },
                StringComparer.OrdinalIgnoreCase) &&
            faceLookup.Resolve(faceMaterialD).SequenceEqual(
                new[] { "/Game/Faces/SK_WorkspaceD" },
                StringComparer.OrdinalIgnoreCase) &&
            projectFaceLoaderCalls == 1 && workspaceFaceLoaderCalls == 1 && partFaceLoaderCalls == 1 &&
            projectFaceEnumerations == 1 && workspaceFaceEnumerations == 1 && partFaceEnumerations == 1,
            "face compatibility snapshots each metadata source once and preserves project/workspace/index priority",
            failures,
            output);

        Check(
            UnrealPathUtil.MeshIdentityMatches(
                "/Game/Characters/Attachments/LEGOface/SK_LEGOface",
                "SK_LEGOface") &&
            UnrealPathUtil.MeshIdentityMatches(
                "SK_LEGOface",
                "/Game/Characters/Attachments/LEGOface/SK_LEGOface.SK_LEGOface") &&
            !UnrealPathUtil.MeshIdentityMatches(
                "/Game/Characters/Attachments/LEGOface/SK_LEGOface",
                "SK_LEGOface_Joker89") &&
            !UnrealPathUtil.MeshIdentityMatches(
                "/Game/Characters/Attachments/LEGOface/SK_LEGOface",
                "SK_LEGOface_Superhero") &&
            !UnrealPathUtil.MeshIdentityMatches(
                "/Game/Characters/Attachments/LEGOface/SK_LEGOface",
                "/Game/Legacy/Faces/SK_LEGOface"),
            "face mesh identity accepts inspector short names without collapsing distinct rigs or qualified packages",
            failures,
            output);

        Check(
            ModelPreviewService.FaceMaterialEditorContractForTest(),
            "the 3D viewer Material editor exposes live face layers and their nested texture roles without removing shader base maps",
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

        var textureMipRecipeRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-texture-mip-recipe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var generated = Path.Combine(textureMipRecipeRoot, "Generated");
            var worldFolder = Path.Combine(generated, "TextureStandaloneTemplate_DroneControlBGRA8");
            var worldBase = Path.Combine(worldFolder, "T_GA_DroneControl_BatGirl_AO");
            Directory.CreateDirectory(worldFolder);
            CreateSizedTextureFixture(worldBase + ".uasset", 1348);
            CreateSizedTextureFixture(worldBase + ".uexp", 22165, packageFooter: true);
            CreateSizedTextureFixture(worldBase + ".ubulk", 22347776);

            var worldJson = TextureCookTemplateService.TemplateJsonPath(
                textureMipRecipeRoot,
                "TextureStandaloneTemplate_DroneControlBGRA8");
            var rejectedFakeDonor = TextureCookTemplateService.NormalizeCoreTemplates(textureMipRecipeRoot) == 0;
            var wroteCanonicalRecipe = TextureCookTemplateService.WriteCanonicalTemplateForRegression(
                "TextureStandaloneTemplate_DroneControlBGRA8",
                worldJson);
            using var worldDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(worldJson));
            var worldRoot = worldDocument.RootElement;
            var worldMips = worldRoot.GetProperty("Mips").EnumerateArray().ToArray();
            var worldInline = worldMips.Skip(5).ToArray();
            var worldLayoutOk =
                rejectedFakeDonor &&
                wroteCanonicalRecipe &&
                worldRoot.GetProperty("InlinePayloadOffsetBias").GetInt32() == 0 &&
                worldMips.Length == 12 &&
                worldMips[0].GetProperty("SizeX").GetInt32() == 2048 &&
                worldMips[^1].GetProperty("SizeX").GetInt32() == 1 &&
                worldMips.Take(5).All(mip => mip.GetProperty("BulkData").GetProperty("BulkDataFlags").GetString()!
                    .Contains("PayloadInSep", StringComparison.OrdinalIgnoreCase)) &&
                worldInline.All(mip => mip.GetProperty("BulkData").GetProperty("BulkDataFlags").GetString()!
                    .Contains("ForceInlinePayload", StringComparison.OrdinalIgnoreCase)) &&
                worldInline[0].GetProperty("BulkData").GetProperty("OffsetInFile").GetInt32() == 197;
            for (var i = 1; i < worldInline.Length && worldLayoutOk; i++)
            {
                var priorBulk = worldInline[i - 1].GetProperty("BulkData");
                var currentBulk = worldInline[i].GetProperty("BulkData");
                worldLayoutOk = currentBulk.GetProperty("OffsetInFile").GetInt32() ==
                    priorBulk.GetProperty("OffsetInFile").GetInt32() +
                    priorBulk.GetProperty("SizeOnDisk").GetInt32() + 0x10;
            }
            Check(
                worldLayoutOk,
                "world Texture2D donors require exact identity and canonical recipes cover the complete 12-mip chain",
                failures,
                output);

            var syntheticWorldPayload = Enumerable.Repeat((byte)0xCC, 22165).ToArray();
            syntheticWorldPayload[^4] = 0xC1;
            syntheticWorldPayload[^3] = 0x83;
            syntheticWorldPayload[^2] = 0x2A;
            syntheticWorldPayload[^1] = 0x9E;
            var rewrittenWorldPayload = TextureCookService.RewriteInlineMipsForRegression(
                worldJson,
                syntheticWorldPayload);
            var worldWriterOk = true;
            for (var i = 0; i < worldInline.Length && worldWriterOk; i++)
            {
                var bulk = worldInline[i].GetProperty("BulkData");
                var offset = bulk.GetProperty("OffsetInFile").GetInt32();
                var size = bulk.GetProperty("SizeOnDisk").GetInt32();
                var expectedFill = (byte)(6 + i);
                worldWriterOk = Enumerable.Range(offset, size).All(index =>
                    rewrittenWorldPayload[index] == expectedFill);
                if (i + 1 < worldInline.Length)
                {
                    worldWriterOk = worldWriterOk && Enumerable.Range(offset + size, 0x10).All(index =>
                        rewrittenWorldPayload[index] == 0xCC);
                }
            }
            var worldLastBulk = worldInline[^1].GetProperty("BulkData");
            var worldFinalPayloadEnd =
                worldLastBulk.GetProperty("OffsetInFile").GetInt32() +
                worldLastBulk.GetProperty("SizeOnDisk").GetInt32();
            worldWriterOk = worldWriterOk &&
                Enumerable.Range(worldFinalPayloadEnd, rewrittenWorldPayload.Length - 4 - worldFinalPayloadEnd)
                    .All(index => rewrittenWorldPayload[index] == 0xCC) &&
                rewrittenWorldPayload[^4..].SequenceEqual(new byte[] { 0xC1, 0x83, 0x2A, 0x9E });
            Check(
                worldWriterOk,
                "mixed world Texture2D writes every bias-zero inline mip without touching record gaps or the split-export tail",
                failures,
                output);

            var mmrJson = TextureCookTemplateService.TemplateJsonPath(
                textureMipRecipeRoot,
                TextureCookTemplateService.NativeMmrTemplateFolder);
            var wroteNativeMmrRecipe = TextureCookTemplateService.WriteCanonicalTemplateForRegression(
                TextureCookTemplateService.NativeMmrTemplateFolder,
                mmrJson);
            using var mmrDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mmrJson));
            var mmrRoot = mmrDocument.RootElement;
            var mmrMips = mmrRoot.GetProperty("Mips").EnumerateArray().ToArray();
            var mmrLastBulk = mmrMips[^1].GetProperty("BulkData");
            var mmrLayoutOk =
                wroteNativeMmrRecipe &&
                TextureCookTemplateService.RequiredPackageExtensionsForRegression(
                    TextureCookTemplateService.NativeMmrTemplateFolder)
                    .SequenceEqual(new[] { ".uasset", ".uexp" }) &&
                mmrRoot.GetProperty("Package").GetString() ==
                    "/Game/Characters/Textures/EoM/T_TPAGE_OswaldCobblepot_DIST_MMR" &&
                mmrRoot.GetProperty("PixelFormat").GetString() == "PF_DXT1" &&
                mmrRoot.GetProperty("SizeX").GetInt32() == 2048 &&
                mmrRoot.GetProperty("InlinePayloadOffsetBias").GetInt32() == 0 &&
                mmrMips.Length == 12 &&
                mmrMips.All(mip => mip.GetProperty("BulkData").GetProperty("BulkDataFlags").GetString()!
                    .Contains("ForceInlinePayload", StringComparison.OrdinalIgnoreCase)) &&
                mmrMips[0].GetProperty("BulkData").GetProperty("SizeOnDisk").GetInt32() == 2_097_152 &&
                mmrMips[0].GetProperty("BulkData").GetProperty("OffsetInFile").GetInt32() == 119 &&
                mmrMips[^1].GetProperty("SizeX").GetInt32() == 1 &&
                mmrLastBulk.GetProperty("SizeOnDisk").GetInt32() == 8 &&
                mmrLastBulk.GetProperty("OffsetInFile").GetInt32() +
                    mmrLastBulk.GetProperty("SizeOnDisk").GetInt32() == 2_796_539 - 28;
            for (var i = 1; i < mmrMips.Length && mmrLayoutOk; i++)
            {
                var priorBulk = mmrMips[i - 1].GetProperty("BulkData");
                var currentBulk = mmrMips[i].GetProperty("BulkData");
                mmrLayoutOk = currentBulk.GetProperty("OffsetInFile").GetInt32() ==
                    priorBulk.GetProperty("OffsetInFile").GetInt32() +
                    priorBulk.GetProperty("SizeOnDisk").GetInt32() + 0x10;
            }
            Check(
                mmrLayoutOk,
                "native MMR profile keeps the donor's full 2K PF_DXT1 inline mip layout",
                failures,
                output);
            Check(
                MainForm.TextureProfileIsVerifiedForRegression(
                    MainForm.NativeMmrCookProfile,
                    "Roughness/spec mask") &&
                !MainForm.TextureProfileIsVerifiedForRegression(
                    "mask-2k-bgra8",
                    "Roughness/spec mask") &&
                !MainForm.TextureProfileIsVerifiedForRegression(
                    "packed-2k-dxt5-legacy",
                    "Roughness/spec mask"),
                "native MMR is the verified packed-map profile while legacy donor routes stay experimental",
                failures,
                output);
            Check(
                MainForm.GuessTextureImportKind("CowlMMR") == "Roughness/spec mask" &&
                MainForm.GuessTextureImportKind("T_Body_ORM") == "Roughness/spec mask" &&
                MainForm.GuessTextureImportKind("T_Body_DNRM") == "Normal map" &&
                MainForm.GuessTextureImportKind("T_Body_NRM") == "Normal map" &&
                MainForm.GuessTextureImportKind("T_Body_ColorMask") == "Color mask" &&
                MainForm.GuessTextureImportKind("T_Body_ColourMask") == "Color mask" &&
                MainForm.GuessTextureImportKind("T_Hair_CT") == "CT map" &&
                MainForm.GuessTextureImportKind("T_Hair_CTUV") == "CT map" &&
                MainForm.GuessTextureImportKind("T_Hair_RAO") == "RAO map" &&
                MainForm.GuessTextureImportKind("T_Body_BC") == "Character texture" &&
                MainForm.GuessTextureImportKind("T_RightArm_MMR") == "Roughness/spec mask" &&
                MainForm.GuessTextureImportKind("T_Face_Left_BC") == "Character texture" &&
                MainForm.GuessTextureImportKind("Uniform") == "Character texture" &&
                MainForm.GuessTextureImportKind("Storm") == "Character texture" &&
                MainForm.TextureKindForCookProfileChange(
                    "Character texture",
                    "CowlMMR",
                    "C:/legacy/CowlMMR.png",
                    "/Game/Mods/Test/CowlMMR") == "Roughness/spec mask" &&
                MainForm.TextureKindForCookProfileChange(
                    "Character texture",
                    "StormBody",
                    "C:/legacy/UniformBase.png") == "Character texture",
                "common BC/MMR/DNRM/NRM/ColorMask/CT/RAO filename suffixes select the intended texture use without matching ordinary names",
                failures,
                output);

            var mmrCookTemplateFolder = Path.Combine(generated, "MmrCookFixture");
            var mmrCookJson = Path.Combine(mmrCookTemplateFolder, "T_MmrFixture.json");
            Directory.CreateDirectory(mmrCookTemplateFolder);
            TextureCookTemplateService.WriteCanonicalTemplateForRegression(
                TextureCookTemplateService.NativeMmrTemplateFolder,
                mmrCookJson);
            var mmrCookTemplateBase = Path.Combine(mmrCookTemplateFolder, "T_MmrFixture");
            CreateSizedTextureFixture(mmrCookTemplateBase + ".uasset", 1326);
            CreateSizedTextureFixture(mmrCookTemplateBase + ".uexp", 2_796_539, packageFooter: true);
            var mmrSourcePng = Path.Combine(textureMipRecipeRoot, "mmr-regression-source.png");
            using (var bitmap = new System.Drawing.Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(255, 0, 127, 255));
                bitmap.Save(mmrSourcePng, System.Drawing.Imaging.ImageFormat.Png);
            }

            var mmrCookedRoot = Path.Combine(textureMipRecipeRoot, "MmrCookedContent");
            var mmrCookResult = new TextureCookService(textureMipRecipeRoot).Cook(new TextureCookService.Request
            {
                SourceImagePath = mmrSourcePng,
                TemplateJsonPath = mmrCookJson,
                OutputContentRoot = mmrCookedRoot,
                OutputPackagePath = mmrRoot.GetProperty("Package").GetString()!,
                WriteInlineMips = true,
            });
            var mmrCookedBase = Path.Combine(
                mmrCookedRoot,
                "Characters",
                "Textures",
                "EoM",
                "T_TPAGE_OswaldCobblepot_DIST_MMR");
            Check(
                mmrCookResult.Status.Equals("created", StringComparison.OrdinalIgnoreCase) &&
                mmrCookResult.PixelFormat == "PF_DXT1" &&
                mmrCookResult.MipCount == 12 &&
                mmrCookResult.ExternalMipCount == 0 &&
                mmrCookResult.InlineMipCount == 12 &&
                string.IsNullOrWhiteSpace(mmrCookResult.OutputUbulk) &&
                File.Exists(mmrCookedBase + ".uasset") &&
                File.Exists(mmrCookedBase + ".uexp") &&
                !File.Exists(mmrCookedBase + ".ubulk") &&
                MainForm.TextureCookReportOutputMatchesFiles(
                    mmrCookedBase + ".texture-cook-report.json",
                    mmrCookedBase,
                    mmrCookJson),
                "native MMR recipes cook and hash a complete all-inline output without optional bulk files",
                failures,
                output);

            var uiFolder = Path.Combine(generated, TextureCookTemplateService.NativeSuitIconTemplateFolder);
            var uiBase = Path.Combine(uiFolder, "T_SuitIcon_NULL_BCA");
            Directory.CreateDirectory(uiFolder);
            CreateSizedTextureFixture(uiBase + ".uasset", 1616);
            CreateSizedTextureFixture(uiBase + ".uexp", 87708, packageFooter: true);
            var uiNormalized = TextureCookTemplateService.NormalizeNativeSuitIconTemplate(textureMipRecipeRoot);
            var uiJson = TextureCookTemplateService.TemplateJsonPath(
                textureMipRecipeRoot,
                TextureCookTemplateService.NativeSuitIconTemplateFolder);
            using var uiDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(uiJson));
            var uiRoot = uiDocument.RootElement;
            var uiMips = uiRoot.GetProperty("Mips").EnumerateArray().ToArray();
            var uiLast = uiMips[^1].GetProperty("BulkData");
            Check(
                uiNormalized &&
                uiRoot.GetProperty("InlinePayloadOffsetBias").GetInt32() == 0x11 &&
                uiMips.Length == 9 &&
                uiMips[0].GetProperty("BulkData").GetProperty("OffsetInFile").GetInt32() == 0x7F &&
                0x11L + uiLast.GetProperty("OffsetInFile").GetInt64() + uiLast.GetProperty("SizeOnDisk").GetInt64() == 87708 - 28,
                "native suit-icon recipes keep their explicit +0x11 inline payload bias and package footer",
                failures,
                output);

            var characterUiFolder = Path.Combine(generated, TextureCookTemplateService.NativeCharacterIconTemplateFolder);
            var characterUiBase = Path.Combine(characterUiFolder, "T_UI_IconChar_Batman_TheBatman2025_Menu_BCA");
            Directory.CreateDirectory(characterUiFolder);
            CreateSizedTextureFixture(characterUiBase + ".uasset", 1260);
            CreateSizedTextureFixture(characterUiBase + ".uexp", 349841, packageFooter: true);
            var characterUiNormalized = TextureCookTemplateService.NormalizeNativeCharacterIconTemplate(textureMipRecipeRoot);
            var characterUiJson = TextureCookTemplateService.TemplateJsonPath(
                textureMipRecipeRoot,
                TextureCookTemplateService.NativeCharacterIconTemplateFolder);
            using var characterUiDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(characterUiJson));
            var characterUiRoot = characterUiDocument.RootElement;
            var characterUiMips = characterUiRoot.GetProperty("Mips").EnumerateArray().ToArray();
            var characterUiLast = characterUiMips[^1].GetProperty("BulkData");
            Check(
                characterUiNormalized &&
                characterUiRoot.GetProperty("SizeX").GetInt32() == 512 &&
                characterUiRoot.GetProperty("PixelFormat").GetString() == "PF_BC7" &&
                characterUiRoot.GetProperty("InlinePayloadOffsetBias").GetInt32() == 0x11 &&
                characterUiMips.Length == 10 &&
                characterUiMips[0].GetProperty("BulkData").GetProperty("OffsetInFile").GetInt32() == 0x64 &&
                0x11L + characterUiLast.GetProperty("OffsetInFile").GetInt64() + characterUiLast.GetProperty("SizeOnDisk").GetInt64() == 349841 - 28,
                "native character-icon recipes keep the distinct 512px BC7 inline-mip layout used by UIMD portraits",
                failures,
                output);

            var sourcePng = Path.Combine(textureMipRecipeRoot, "texture-regression-source.png");
            using (var bitmap = new System.Drawing.Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(255, 12, 34, 56));
                graphics.FillRectangle(System.Drawing.Brushes.Gold, 0, 0, 128, 128);
                graphics.FillRectangle(System.Drawing.Brushes.MediumPurple, 128, 128, 128, 128);
                bitmap.Save(sourcePng, System.Drawing.Imaging.ImageFormat.Png);
            }

            var cookedRoot = Path.Combine(textureMipRecipeRoot, "CookedContent");
            var cookResult = new TextureCookService(textureMipRecipeRoot).Cook(new TextureCookService.Request
            {
                SourceImagePath = sourcePng,
                TemplateJsonPath = uiJson,
                OutputContentRoot = cookedRoot,
                OutputPackagePath = "/Game/UI/Icons/Suits/T_SuitIcon_NULL_BCA",
                WriteInlineMips = true,
                Bc7Quality = "fast",
            });
            var cookedBase = Path.Combine(cookedRoot, "UI", "Icons", "Suits", "T_SuitIcon_NULL_BCA");
            var cookedReport = cookedBase + ".texture-cook-report.json";
            var cookedUexpPath = cookedBase + ".uexp";
            var uiPayloadLayoutPreserved = false;
            if (File.Exists(cookedUexpPath))
            {
                var cookedUexp = File.ReadAllBytes(cookedUexpPath);
                var uiBias = uiRoot.GetProperty("InlinePayloadOffsetBias").GetInt32();
                uiPayloadLayoutPreserved = true;
                for (var i = 0; i < uiMips.Length && uiPayloadLayoutPreserved; i++)
                {
                    var bulk = uiMips[i].GetProperty("BulkData");
                    var offset = uiBias + bulk.GetProperty("OffsetInFile").GetInt32();
                    var size = bulk.GetProperty("SizeOnDisk").GetInt32();
                    uiPayloadLayoutPreserved = Enumerable.Range(offset, size).Any(index => cookedUexp[index] != 0);
                    if (i + 1 < uiMips.Length)
                    {
                        uiPayloadLayoutPreserved = uiPayloadLayoutPreserved &&
                            Enumerable.Range(offset + size, 0x10).All(index => cookedUexp[index] == 0);
                    }
                }
                var uiFinalPayloadEnd =
                    uiBias + uiLast.GetProperty("OffsetInFile").GetInt32() + uiLast.GetProperty("SizeOnDisk").GetInt32();
                uiPayloadLayoutPreserved = uiPayloadLayoutPreserved &&
                    Enumerable.Range(uiFinalPayloadEnd, cookedUexp.Length - 4 - uiFinalPayloadEnd)
                        .All(index => cookedUexp[index] == 0) &&
                    cookedUexp[^4..].SequenceEqual(new byte[] { 0xC1, 0x83, 0x2A, 0x9E });
            }
            var completeCookAccepted =
                cookResult.Status.Equals("created", StringComparison.OrdinalIgnoreCase) &&
                uiPayloadLayoutPreserved &&
                MainForm.TextureCookReportOutputMatchesFiles(cookedReport, cookedBase, uiJson);
            var tamperedCookRejected = false;
            if (File.Exists(cookedBase + ".uexp"))
            {
                var tamperedUexp = File.ReadAllBytes(cookedUexpPath);
                if (tamperedUexp.Length > 0x90)
                {
                    tamperedUexp[0x90] ^= 0xFF;
                    File.WriteAllBytes(cookedUexpPath, tamperedUexp);
                    tamperedCookRejected =
                        !MainForm.TextureCookReportOutputMatchesFiles(cookedReport, cookedBase, uiJson);
                }
            }
            var bulkRecipe = new GeneratedTextureEntry
            {
                DisplayName = "Bulk texture fixture",
                SourcePng = sourcePng,
                TemplateJson = uiJson,
                OutputRoot = textureMipRecipeRoot,
                PackagePath = "/Game/Mods/Fixture/Textures/T_Bulk",
            };
            var completeBulkRecipeAccepted =
                MainForm.GeneratedTextureReimportPreflightError(bulkRecipe) is null;
            bulkRecipe.SourcePng = Path.Combine(textureMipRecipeRoot, "missing-source.png");
            var missingBulkSourceRejected =
                MainForm.GeneratedTextureReimportPreflightError(bulkRecipe)?.Contains("source PNG", StringComparison.OrdinalIgnoreCase) == true;
            bulkRecipe.SourcePng = sourcePng;
            bulkRecipe.TemplateJson = Path.Combine(textureMipRecipeRoot, "missing-template.json");
            var missingBulkTemplateRejected =
                MainForm.GeneratedTextureReimportPreflightError(bulkRecipe)?.Contains("template", StringComparison.OrdinalIgnoreCase) == true;
            Check(
                completeCookAccepted &&
                tamperedCookRejected &&
                completeBulkRecipeAccepted &&
                missingBulkSourceRejected &&
                missingBulkTemplateRejected,
                "texture cooks write every inline mip atomically, bulk reimport preflights every saved recipe, and staging rejects payloads changed after the cook report",
                failures,
                output);
        }
        finally
        {
            if (Directory.Exists(textureMipRecipeRoot))
            {
                Directory.Delete(textureMipRecipeRoot, recursive: true);
            }
        }

        var movedExtractRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-template-path-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var activeContent = Path.Combine(movedExtractRoot, "Current", "Content");
            var activeBase = Path.Combine(
                activeContent,
                "Characters",
                "Minifig",
                "Batman",
                "BP_Batman_1989_Playable");
            Directory.CreateDirectory(Path.GetDirectoryName(activeBase)!);
            File.WriteAllText(activeBase + ".uasset", "current-uasset");
            File.WriteAllText(activeBase + ".uexp", "current-uexp");
            var pluginBase = Path.Combine(
                movedExtractRoot,
                "Current",
                "Plugins",
                "GameFeatures",
                "DLC_BeyondPack",
                "Content",
                "Characters",
                "Minifig",
                "Batman",
                "BP_Batman_Beyond_Playable");
            Directory.CreateDirectory(Path.GetDirectoryName(pluginBase)!);
            File.WriteAllText(pluginBase + ".uasset", "plugin-uasset");
            File.WriteAllText(pluginBase + ".uexp", "plugin-uexp");
            var staleTemplate = new TemplateRecord
            {
                PackagePath = "/Game/Characters/Minifig/Batman/BP_Batman_1989_Playable",
                ContentRelative = "Characters/Minifig/Batman/BP_Batman_1989_Playable",
                Uasset = Path.Combine(movedExtractRoot, "Retired", "Content", "Characters", "Minifig", "Batman", "BP_Batman_1989_Playable.uasset"),
                Uexp = Path.Combine(movedExtractRoot, "Retired", "Content", "Characters", "Minifig", "Batman", "BP_Batman_1989_Playable.uexp"),
                Role = "playable",
            };
            var stalePluginTemplate = new TemplateRecord
            {
                PackagePath = "/DLC_BeyondPack/Characters/Minifig/Batman/BP_Batman_Beyond_Playable",
                ContentRelative = "Characters/Minifig/Batman/BP_Batman_Beyond_Playable",
                Uasset = Path.Combine(movedExtractRoot, "Retired", "Plugins", "DLC_BeyondPack", "BP_Batman_Beyond_Playable.uasset"),
                Role = "playable",
            };
            var missingPluginWithBaseCollision = new TemplateRecord
            {
                PackagePath = "/DLC_NotInstalled/Characters/Minifig/Batman/BP_Batman_1989_Playable",
                ContentRelative = "Characters/Minifig/Batman/BP_Batman_1989_Playable",
                Uasset = Path.Combine(movedExtractRoot, "Retired", "Plugins", "DLC_NotInstalled", "BP_Batman_1989_Playable.uasset"),
                Role = "playable",
            };
            Check(
                SuitProjectService.RefreshTemplateSourceForTest(staleTemplate, activeContent) &&
                staleTemplate.Uasset.Equals(activeBase + ".uasset", StringComparison.OrdinalIgnoreCase) &&
                staleTemplate.Uexp?.Equals(activeBase + ".uexp", StringComparison.OrdinalIgnoreCase) == true &&
                staleTemplate.PackagePath == "/Game/Characters/Minifig/Batman/BP_Batman_1989_Playable" &&
                SuitProjectService.RefreshTemplateSourceForTest(stalePluginTemplate, activeContent) &&
                stalePluginTemplate.Uasset.Equals(pluginBase + ".uasset", StringComparison.OrdinalIgnoreCase) &&
                stalePluginTemplate.Uexp?.Equals(pluginBase + ".uexp", StringComparison.OrdinalIgnoreCase) == true &&
                stalePluginTemplate.PackagePath == "/DLC_BeyondPack/Characters/Minifig/Batman/BP_Batman_Beyond_Playable" &&
                !SuitProjectService.RefreshTemplateSourceForTest(missingPluginWithBaseCollision, activeContent) &&
                missingPluginWithBaseCollision.PackagePath ==
                    "/DLC_NotInstalled/Characters/Minifig/Batman/BP_Batman_1989_Playable",
                "saved suits relocate retired base-game and Game Feature templates by exact package identity without crossing mounts",
                failures,
                output);
        }
        finally
        {
            if (Directory.Exists(movedExtractRoot))
            {
                Directory.Delete(movedExtractRoot, recursive: true);
            }
        }

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
            var dlcBatgirlRoot = Path.Combine(
                contentRoot,
                "AdditionalContent",
                "DLC_Arkham",
                "Characters",
                "Minifig",
                "Batgirl");
            Directory.CreateDirectory(dlcBatgirlRoot);
            File.WriteAllBytes(
                Path.Combine(dlcBatgirlRoot, "BP_BatGirl_Arkhamverse_Playable.uasset"),
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
                visualAssets.Count == 4 &&
                visualAssets.Any(asset => asset.Path.Equals(
                    "/Game/AdditionalContent/DLC_Arkham/Characters/Minifig/Batgirl/BP_BatGirl_Arkhamverse_Playable",
                    StringComparison.OrdinalIgnoreCase)) &&
                gameplayAssets.Count == 1 &&
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
                "extracted Smallfig and AdditionalContent DLC Blueprints appear as selectable visual/gameplay bases",
                failures,
                output);

            var indexedBlueprints = PartIndexService.EnumerateCharacterBlueprintsForTest(contentRoot);
            Check(
                indexedBlueprints.Count == 4 &&
                indexedBlueprints.Any(path => Path.GetFileNameWithoutExtension(path)
                    .Equals("BP_Batmite_Quest", StringComparison.OrdinalIgnoreCase)) &&
                indexedBlueprints.Any(path => Path.GetFileNameWithoutExtension(path)
                    .Equals("BP_BatGirl_Arkhamverse_Playable", StringComparison.OrdinalIgnoreCase)) &&
                !PartIndexService.IsCurrentIndexForTest(new NativeSuitPartIndex { SchemaVersion = 4 }) &&
                PartIndexService.IsCurrentIndexForTest(new NativeSuitPartIndex()),
                "the native part index scans Smallfig and AdditionalContent DLC character Blueprints",
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
                GameDataService.Instance.FamilyForBasePath(
                    "/DLC_BeyondPack/Characters/Smallfig/Robin/BP_RobinDickGrayson_Beyond_Playable")?.Name
                    .Equals("Robin_DickGrayson", StringComparison.OrdinalIgnoreCase) == true &&
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
            PartGraftService.RejectsCookedClassFieldMutationForTest(),
            "an appended SCS component cannot mutate an opaque cooked CDO's reflected class-field schema",
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
            PartGraftService.RebindsCrossAssetDonorNamesForTest(),
            "cross-package component grafts rebind nested collision names and cutscene owner names into the target name map",
            failures,
            output);
        Check(
            StageValidationService.RejectsMalformedCustomStaticMetadataForTest(),
            "final stage validation rejects malformed custom-static BodyInstance and cutscene parent-owner metadata",
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
        var invalidWin64Message = RegistryPluginService.DescribeWriterBuildFailureForTest(
            6,
            "Platform Win64 is not a valid platform to build. Check that the SDK is installed properly and that you have the necessary platform support files.");
        Check(
            invalidWin64Message.Contains("unrelated to the .usmap", StringComparison.OrdinalIgnoreCase) &&
            invalidWin64Message.Contains("Game development with C++", StringComparison.Ordinal) &&
            invalidWin64Message.Contains("Windows 10 or 11 SDK", StringComparison.Ordinal),
            "registry writer explains an unavailable Win64 SDK instead of reporting a generic exit code",
            failures,
            output);
        Check(
            AppSettings.PortableLayoutIssues().Count == 0,
            "portable layout requires the complete bundled registry writer prebuilt",
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
        var inspectorRemovalMesh = new CustomStaticMeshImport
        {
            Id = "inspector-removal",
            DisplayName = "Inspector removal fixture",
            ResolvedComponent = "CustomMesh_InspectorRemoval",
        };
        var inspectorRemovalProject = new NativeSuitProject
        {
            CustomStaticMeshes = [inspectorRemovalMesh],
        };
        Check(
            ReferenceEquals(
                MainForm.FindCustomStaticMeshForComponent(
                    inspectorRemovalProject,
                    "CustomMesh_InspectorRemoval:1"),
                inspectorRemovalMesh) &&
            MainForm.FindCustomStaticMeshForComponent(
                inspectorRemovalProject,
                "CharacterMesh0") is null,
            "inspector component removal routes project-owned OBJ meshes through their owned cleanup path",
            failures,
            output);
        Check(
            StageValidationService.CustomStaticMeshDeclarationIdentityRegressionPasses(),
            "custom mesh declarations reject normalized duplicate IDs, resolved components, and mesh packages",
            failures,
            output);
        Check(
            StageValidationService.CustomStaticMeshSourceMaterialNameRegressionPasses(),
            "custom mesh declarations require nonempty unique source OBJ material names",
            failures,
            output);
        Check(
            StageValidationService.CustomStaticComponentTemplateBindingRegressionPasses(),
            "custom mesh validation binds each live SCS node to its exact declared component-template export",
            failures,
            output);
        Check(
            StageValidationService.ValidationProjectRootResolutionRegressionPasses(),
            "stage-validation CLI derives canonical workspace roots and honors explicit archived-project roots",
            failures,
            output);
        var combinedStageFailure = MainForm.CombinedStageValidationFailure(
            new InvalidOperationException("combined-stage regression fixture"));
        Check(
            combinedStageFailure.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase) &&
            combinedStageFailure.Message.Contains("packaging is blocked", StringComparison.OrdinalIgnoreCase) &&
            combinedStageFailure.Message.Contains("combined-stage regression fixture", StringComparison.Ordinal),
            "combined-mod packaging treats an unexpected structural-validation failure as a blocking error",
            failures,
            output);
        var customSourcePathProject = new NativeSuitProject { SlotId = "custom-source-fixture" };
        Check(
            StageValidationService.ProjectOwnedCustomMeshSourcePathIsSafeForTest(
                Path.GetTempPath(),
                customSourcePathProject,
                Path.Combine("ImportedMeshes", "fixture.obj")) &&
            !StageValidationService.ProjectOwnedCustomMeshSourcePathIsSafeForTest(
                Path.GetTempPath(),
                customSourcePathProject,
                Path.Combine("..", "..", "outside.obj")),
            "custom mesh OBJ declarations stay inside their SuitProjectService-owned output directory",
            failures,
            output);

        var customPackageFixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-custom-package-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string customPackage = "/Game/Mods/Fixture/Meshes/SM_Custom_Fixture";
            var customPackageBase = Path.Combine(
                customPackageFixtureRoot,
                "Mods",
                "Fixture",
                "Meshes",
                "SM_Custom_Fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(customPackageBase)!);
            File.WriteAllBytes(customPackageBase + ".uasset", [1]);
            File.WriteAllBytes(customPackageBase + ".uexp", [2]);
            File.WriteAllBytes(customPackageBase + ".ubulk", [3]);
            var completeCustomPackage = StageValidationService.RequiredPackageFilesAreNonEmptyForTest(
                customPackageFixtureRoot,
                customPackage,
                ".uasset",
                ".uexp",
                ".ubulk");
            File.WriteAllBytes(customPackageBase + ".ubulk", []);
            var emptyPayloadRejected = !StageValidationService.RequiredPackageFilesAreNonEmptyForTest(
                customPackageFixtureRoot,
                customPackage,
                ".uasset",
                ".uexp",
                ".ubulk");
            Check(
                completeCustomPackage && emptyPayloadRejected,
                "custom mesh package validation requires a nonempty uasset, uexp, and ubulk trio",
                failures,
                output);
        }
        catch
        {
            failures.Add("custom mesh package validation requires a nonempty uasset, uexp, and ubulk trio");
        }
        finally
        {
            try { Directory.Delete(customPackageFixtureRoot, recursive: true); } catch { /* best effort */ }
        }

        var contextualCustomMaterialProject = new NativeSuitProject
        {
            MaterialAssignments =
            [
                new SavedMaterialAssignment
                {
                    Component = "CustomMesh_Fixture",
                    Slot = 0,
                    Context = "both",
                    MiPackagePath = "/Game/Mods/Fixture/Materials/MI_Both",
                },
                new SavedMaterialAssignment
                {
                    Component = "CustomMesh_Fixture",
                    Slot = 0,
                    Context = "playable",
                    MiPackagePath = "/Game/Mods/Fixture/Materials/MI_Playable",
                },
                new SavedMaterialAssignment
                {
                    Component = "CustomMesh_Fixture",
                    Slot = 0,
                    Context = "cutscene",
                    MiPackagePath = "/Game/Mods/Fixture/Materials/MI_Cutscene",
                },
            ],
        };
        Check(
            StageValidationService.EffectiveCustomStaticMeshMaterialForTest(
                contextualCustomMaterialProject,
                "playable",
                "CustomMesh_Fixture",
                "/Game/Base/MI_Fallback") == "/Game/Mods/Fixture/Materials/MI_Playable" &&
            StageValidationService.EffectiveCustomStaticMeshMaterialForTest(
                contextualCustomMaterialProject,
                "cutscene",
                "CustomMesh_Fixture",
                "/Game/Base/MI_Fallback") == "/Game/Mods/Fixture/Materials/MI_Cutscene" &&
            StageValidationService.EffectiveCustomStaticMeshMaterialForTest(
                contextualCustomMaterialProject,
                "playable",
                "CustomMesh_Unassigned",
                "/Game/Base/MI_Fallback") == "/Game/Base/MI_Fallback",
            "custom mesh validation resolves the final role-specific slot-zero material with a declaration fallback",
            failures,
            output);
        Check(
            StaticMeshObjProbeService.PayloadMetadataRegressionPasses(),
            "custom OBJ payloads update section vertex ranges and inline buffer-size summaries",
            failures,
            output);
        Check(
            StaticMeshObjProbeService.MultiMaterialObjRegressionPasses(),
            "custom OBJ usemtl sections keep source-name bindings and emit matching cooked/preview slots",
            failures,
            output);
        Check(
            StaticMeshObjProbeService.MultiMaterialPackageValidationRegressionPasses(),
            "custom OBJ package validation rejects malformed LOD sections, bounds, weighted samplers, and StaticMaterials metadata",
            failures,
            output);

        const string assignmentBlack = "/Game/Mods/SlotFlow/Materials/MI_Black";
        const string assignmentMetal = "/Game/Mods/SlotFlow/Materials/MI_Metal";
        const string assignmentTrim = "/Game/Mods/SlotFlow/Materials/MI_Trim";
        var priorAssignmentSlots = new List<CustomStaticMeshMaterialSlot>
        {
            new() { Slot = 0, SourceMaterialName = "Black", StableSlotName = "BC_SLOT_000_Black", MaterialPath = assignmentBlack },
            new() { Slot = 1, SourceMaterialName = "Metal", StableSlotName = "BC_SLOT_001_Metal", MaterialPath = assignmentMetal },
            new() { Slot = 2, SourceMaterialName = "Trim", StableSlotName = "BC_SLOT_002_Trim", MaterialPath = assignmentTrim },
        };
        var compactedAssignmentSlots = StaticMeshObjProbeService.ReconcileMaterialSlots(
            priorAssignmentSlots,
            ["Trim", "Metal"]);
        var assignmentRemapImport = new CustomStaticMeshImport
        {
            Id = "assignment-remap",
            ResolvedComponent = "CustomMesh_AssignmentRemap",
            MaterialSlots = priorAssignmentSlots,
            MaterialPath = assignmentBlack,
        };
        var assignmentRemapProject = new NativeSuitProject
        {
            MaterialAssignments =
            [
                new SavedMaterialAssignment { Component = "CustomMesh_AssignmentRemap", Slot = 0, Context = "both", MiPackagePath = assignmentBlack },
                new SavedMaterialAssignment { Component = "CustomMesh_AssignmentRemap", Slot = 1, Context = "playable", MiPackagePath = assignmentMetal },
                new SavedMaterialAssignment { Component = "CustomMesh_AssignmentRemap", Slot = 2, Context = "cutscene", MiPackagePath = assignmentTrim },
                new SavedMaterialAssignment { Component = "CharacterMesh0", Slot = 0, Context = "both", MiPackagePath = "/Game/Mods/SlotFlow/Materials/MI_Body" },
            ],
        };
        CustomStaticMeshImportService.RewriteCustomMaterialAssignments(
            assignmentRemapProject,
            assignmentRemapImport,
            "CustomMesh_AssignmentRemap",
            "CustomMesh_AssignmentRemap_Rebuilt",
            priorAssignmentSlots,
            compactedAssignmentSlots);
        var remappedCustomAssignments = assignmentRemapProject.MaterialAssignments
            .Where(assignment => assignment.Component.Equals(
                "CustomMesh_AssignmentRemap_Rebuilt",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(assignment => assignment.Slot)
            .ToList();
        Check(
            compactedAssignmentSlots.Select(slot => slot.SourceMaterialName).SequenceEqual(["Metal", "Trim"], StringComparer.Ordinal) &&
            remappedCustomAssignments.Count == 2 &&
            remappedCustomAssignments[0].Slot == 0 &&
            remappedCustomAssignments[0].Context.Equals("playable", StringComparison.OrdinalIgnoreCase) &&
            remappedCustomAssignments[0].MiPackagePath == assignmentMetal &&
            remappedCustomAssignments[1].Slot == 1 &&
            remappedCustomAssignments[1].Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase) &&
            remappedCustomAssignments[1].MiPackagePath == assignmentTrim &&
            assignmentRemapProject.MaterialAssignments.Count(assignment =>
                assignment.Component.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase)) == 1 &&
            assignmentRemapProject.MaterialAssignments.All(assignment =>
                assignment.MiPackagePath != assignmentBlack),
            "custom mesh material assignments follow source names through usemtl reordering, removal, and slot compaction",
            failures,
            output);

        const string graftPlayable = "/Game/Characters/Minifig/Alfred/BP_Alfred_Casual_Playable";
        const string graftCutscene = "/Game/Characters/Minifig/Alfred/BP_Alfred_Casual_Cutscene";
        var multiMaterialDonorIndex = new NativeSuitPartIndex
        {
            Parts =
            [
                new NativeSuitPartRecord
                {
                    SourcePackagePath = graftPlayable,
                    Context = "playable",
                    Slot = "Head",
                    MeshKind = "StaticMesh",
                    ComponentClass = "StaticMeshComponent",
                    AttachSocket = "HeadStud_Attach_Socket",
                },
                new NativeSuitPartRecord
                {
                    SourcePackagePath = graftCutscene,
                    Context = "cutscene",
                    Slot = "Head",
                    MeshKind = "StaticMesh",
                    ComponentClass = "StaticMeshComponent",
                    AttachSocket = "HeadStud_Attach_Socket",
                },
            ],
        };
        var multiMaterialAttachment = CustomStaticMeshImportService.ResolveAttachmentSlot("Head");
        var playableMultiMaterialPart = CustomStaticMeshImportService.CreateStaticAttachmentPart(
            multiMaterialDonorIndex,
            "playable",
            graftPlayable,
            multiMaterialAttachment,
            "/Game/Mods/SlotFlow/Meshes/SM_Custom_Multi",
            "SM_Custom_Multi",
            compactedAssignmentSlots);
        var cutsceneMultiMaterialPart = CustomStaticMeshImportService.CreateStaticAttachmentPart(
            multiMaterialDonorIndex,
            "cutscene",
            graftCutscene,
            multiMaterialAttachment,
            "/Game/Mods/SlotFlow/Meshes/SM_Custom_Multi",
            "SM_Custom_Multi",
            compactedAssignmentSlots);
        var expectedGraftMaterials = new[] { assignmentMetal, assignmentTrim };
        Check(
            playableMultiMaterialPart.Materials.Select(material => material.PackagePath)
                .SequenceEqual(expectedGraftMaterials, StringComparer.OrdinalIgnoreCase) &&
            cutsceneMultiMaterialPart.Materials.Select(material => material.PackagePath)
                .SequenceEqual(expectedGraftMaterials, StringComparer.OrdinalIgnoreCase),
            "custom mesh playable and cutscene graft recipes carry every active OBJ material slot in order",
            failures,
            output);

        const string legacyCustomMaterialJson =
            "{\"customStaticMeshes\":[{\"id\":\"legacy-cowl\",\"materialPath\":\"/Game/Mods/Legacy/Materials/MI_Cowl\"}]}";
        var legacyCustomMaterialProject = System.Text.Json.JsonSerializer.Deserialize<NativeSuitProject>(
            legacyCustomMaterialJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var legacyCustomMesh = legacyCustomMaterialProject?.CustomStaticMeshes.SingleOrDefault();
        var legacyEffectiveSlots = legacyCustomMesh is null
            ? Array.Empty<CustomStaticMeshMaterialSlot>()
            : StaticMeshObjProbeService.EffectiveMaterialSlots(legacyCustomMesh).ToArray();
        Check(
            legacyCustomMesh is not null &&
            legacyCustomMesh.MaterialSlots.Count == 0 &&
            legacyEffectiveSlots.Length == 1 &&
            legacyEffectiveSlots[0].Slot == 0 &&
            legacyEffectiveSlots[0].SourceMaterialName == "Default" &&
            legacyEffectiveSlots[0].MaterialPath == "/Game/Mods/Legacy/Materials/MI_Cowl",
            "legacy custom-mesh JSON without MaterialSlots synthesizes its saved MaterialPath as slot zero",
            failures,
            output);

        const string releaseSlot0 = "/Game/Mods/SlotFlow/Materials/MI_ReleaseBlack";
        const string releaseSlot1 = "/Game/Mods/SlotFlow/Materials/MI_ReleaseMetal";
        const string renamedReleaseSlot1 = "/Game/Mods/SlotFlow/Materials/MI_ReleaseMetalRenamed";
        var multiMaterialReleaseMesh = new CustomStaticMeshImport
        {
            Id = "release-multi",
            MaterialPath = releaseSlot0,
            MaterialSlots =
            [
                new CustomStaticMeshMaterialSlot { Slot = 0, SourceMaterialName = "Black", MaterialPath = releaseSlot0 },
                new CustomStaticMeshMaterialSlot { Slot = 1, SourceMaterialName = "Metal", MaterialPath = releaseSlot1 },
            ],
        };
        var multiMaterialReleaseProject = new NativeSuitProject
        {
            CustomStaticMeshes = [multiMaterialReleaseMesh],
        };
        var enumeratedReleaseMaterials = MainForm.AssignedModMaterialPackagesForRelease(multiMaterialReleaseProject);
        var slotOneReferencesBeforeRename = MainForm.CountCustomStaticMeshMaterialReferences(
            multiMaterialReleaseProject,
            releaseSlot1.ToLowerInvariant());
        var renamedSlotOneReferences = MainForm.ReplaceCustomStaticMeshMaterialReferences(
            multiMaterialReleaseProject,
            releaseSlot1.ToLowerInvariant(),
            renamedReleaseSlot1);
        var slotOneReferencesAfterRename = MainForm.CountCustomStaticMeshMaterialReferences(
            multiMaterialReleaseProject,
            renamedReleaseSlot1);
        var resetDeletedSlotOneReferences = MainForm.ReplaceCustomStaticMeshMaterialReferences(
            multiMaterialReleaseProject,
            renamedReleaseSlot1,
            CustomStaticMeshImportService.DefaultMaterialPackagePath);
        Check(
            enumeratedReleaseMaterials.SequenceEqual([releaseSlot0, releaseSlot1], StringComparer.OrdinalIgnoreCase) &&
            slotOneReferencesBeforeRename == 1 &&
            renamedSlotOneReferences == 1 &&
            slotOneReferencesAfterRename == 1 &&
            resetDeletedSlotOneReferences == 1 &&
            multiMaterialReleaseMesh.MaterialPath == releaseSlot0 &&
            multiMaterialReleaseMesh.MaterialSlots.Single(slot => slot.Slot == 1).MaterialPath ==
                CustomStaticMeshImportService.DefaultMaterialPackagePath,
            "release enumeration plus rename/delete reference helpers include custom-mesh material slot one",
            failures,
            output);

        var riskySurface = new MaterialSurfaceDiagnosticService.MmrStats(
            1000, 255, 24, 8, 32, 55d, 12d, 70d);
        var safeSurface = new MaterialSurfaceDiagnosticService.MmrStats(
            1000, 0, 76, 64, 89, 0d, 0d, 0d);
        var riskySurfaceMessages = MaterialSurfaceDiagnosticService.RiskMessages(
            riskySurface,
            expectUnusedGreen: true);
        var specializedDonorMessages = MaterialSurfaceDiagnosticService.RiskMessages(riskySurface);
        Check(
            riskySurfaceMessages.Any(message => message.Contains("green channel", StringComparison.OrdinalIgnoreCase)) &&
            riskySurfaceMessages.Any(message => message.Contains("fully metallic", StringComparison.OrdinalIgnoreCase)) &&
            riskySurfaceMessages.Any(message => message.Contains("roughness", StringComparison.OrdinalIgnoreCase)) &&
            specializedDonorMessages.All(message => !message.Contains("green channel", StringComparison.OrdinalIgnoreCase)) &&
            specializedDonorMessages.Any(message => message.Contains("fully metallic", StringComparison.OrdinalIgnoreCase)) &&
            MaterialSurfaceDiagnosticService.RiskMessages(safeSurface).Count == 0,
            "MMR authoring diagnostics scope green-channel packing to verified donor families while retaining metal and gloss warnings",
            failures,
            output);

        var nativePlasticCowlRecipe = new MaterialTemplateCatalogService().Recipes()
            .SingleOrDefault(recipe => recipe.Id.Equals(
                "accessory.textured-cowl.native-plastic",
                StringComparison.OrdinalIgnoreCase));
        Check(
            nativePlasticCowlRecipe is not null &&
            nativePlasticCowlRecipe.ExpectsUnusedMmrGreen &&
            nativePlasticCowlRecipe.Outputs.Count == 2 &&
            new[] { "RAO", "CT", "NRM", "ColourMask" }.All(parameter =>
                nativePlasticCowlRecipe.DefaultTextureOverrides.ContainsKey(parameter)) &&
            nativePlasticCowlRecipe.Outputs.Any(candidate =>
                candidate.Role.Equals("gameplay", StringComparison.OrdinalIgnoreCase) &&
                candidate.DonorPackagePath.Equals(
                    "/Game/Characters/Attachments/Hat/BatmanCowl_MoldedEyes/Materials/MI_HAT_BatmanBraveAndTheBold_EOM",
                    StringComparison.OrdinalIgnoreCase)) &&
            nativePlasticCowlRecipe.Outputs.Any(candidate =>
                candidate.Role.Equals("cutscene", StringComparison.OrdinalIgnoreCase) &&
                candidate.DonorPackagePath.Equals(
                    "/Game/Characters/Attachments/Hat/BatmanCowl_MoldedEyes/Materials/MI_HAT_BatmanBraveAndTheBold_CUT",
                    StringComparison.OrdinalIgnoreCase)),
            "the native-plastic custom-cowl template keeps matched role donors and neutralizes donor-mesh maps",
            failures,
            output);

        var inheritedDuplicateNormals = new[]
        {
            new MaterialGenService.TextureParam
            {
                Name = "DNRM",
                ObjectPath = "/Game/Mods/Fixture/T_CustomNormal.T_CustomNormal",
            },
            new MaterialGenService.TextureParam
            {
                Name = "NRM",
                ObjectPath = "/Game/Mods/Fixture/T_CustomNormal.T_CustomNormal",
            },
        };
        var inheritedNormalDuplicates = MaterialWizard.FindDuplicatedEffectiveNormalParameters(
            inheritedDuplicateNormals,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var correctedNormalDuplicates = MaterialWizard.FindDuplicatedEffectiveNormalParameters(
            inheritedDuplicateNormals,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NRM"] = "/Game/Characters/Textures/Shared/EoM/T_Dummy_Norm.T_Dummy_Norm",
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var clearedNormalDuplicates = MaterialWizard.FindDuplicatedEffectiveNormalParameters(
            inheritedDuplicateNormals,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(new[] { "NRM" }, StringComparer.OrdinalIgnoreCase));
        Check(
            inheritedNormalDuplicates.Count == 1 &&
            inheritedNormalDuplicates[0].Contains("DNRM", StringComparer.OrdinalIgnoreCase) &&
            inheritedNormalDuplicates[0].Contains("NRM", StringComparer.OrdinalIgnoreCase) &&
            correctedNormalDuplicates.Count == 0 &&
            clearedNormalDuplicates.Count == 0,
            "material surface checks compare effective inherited normals and respect overrides or explicit clears",
            failures,
            output);

        const string materialPairGroup = "material-pair-regression";
        var materialPair = new List<GeneratedMaterialEntry>
        {
            new() { PackagePath = "/Game/Mods/Pair/MI_Cowl_LOD0", TemplateGroupId = materialPairGroup, TemplateOutputRole = "gameplay LOD0" },
            new() { PackagePath = "/Game/Mods/Pair/MI_Cowl_LOD0_CUT", TemplateGroupId = materialPairGroup, TemplateOutputRole = "cutscene LOD0" },
            new() { PackagePath = "/Game/Mods/Pair/MI_Cowl_LOD1", TemplateGroupId = materialPairGroup, TemplateOutputRole = "gameplay LOD1" },
            new() { PackagePath = "/Game/Mods/Pair/MI_Cowl_LOD1_CUT", TemplateGroupId = materialPairGroup, TemplateOutputRole = "cutscene LOD1" },
        };
        var resolvedLod0Pair = MainForm.ResolveTemplateMaterialAssignments(
            materialPair,
            "/Game/Mods/Pair/MI_Cowl_LOD0",
            "both");
        var resolvedLod1Pair = MainForm.ResolveTemplateMaterialAssignments(
            materialPair,
            "/Game/Mods/Pair/MI_Cowl_LOD1_CUT",
            "both");
        var incompletePair = MainForm.ResolveTemplateMaterialAssignments(
            materialPair.Where(entry => !entry.TemplateOutputRole.Equals("cutscene LOD0", StringComparison.OrdinalIgnoreCase)),
            "/Game/Mods/Pair/MI_Cowl_LOD0",
            "both");
        var duplicatePair = MainForm.ResolveTemplateMaterialAssignments(
            materialPair.Append(new GeneratedMaterialEntry
            {
                PackagePath = "/Game/Mods/Pair/MI_Cowl_LOD0_Duplicate",
                TemplateGroupId = materialPairGroup,
                TemplateOutputRole = "gameplay LOD0",
            }),
            "/Game/Mods/Pair/MI_Cowl_LOD0",
            "both");
        Check(
            resolvedLod0Pair.Assignments.Count == 2 &&
            resolvedLod0Pair.Assignments.Any(assignment =>
                assignment.Context.Equals("playable", StringComparison.OrdinalIgnoreCase) &&
                assignment.PackagePath.EndsWith("MI_Cowl_LOD0", StringComparison.OrdinalIgnoreCase)) &&
            resolvedLod0Pair.Assignments.Any(assignment =>
                assignment.Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase) &&
                assignment.PackagePath.EndsWith("MI_Cowl_LOD0_CUT", StringComparison.OrdinalIgnoreCase)) &&
            resolvedLod1Pair.Assignments.Count == 2 &&
            resolvedLod1Pair.Assignments.All(assignment =>
                assignment.PackagePath.Contains("LOD1", StringComparison.OrdinalIgnoreCase)) &&
            incompletePair.Assignments.Count == 0 && !string.IsNullOrWhiteSpace(incompletePair.Warning) &&
            duplicatePair.Assignments.Count == 0 && !string.IsNullOrWhiteSpace(duplicatePair.Warning),
            "paired material outputs route exact gameplay/cutscene and LOD siblings while incomplete or ambiguous groups fail closed",
            failures,
            output);

        const string customBaselineMaterial = "/Game/Mods/CustomFlow/Materials/MI_CustomCowl";
        const string customMeshPackage = "/Game/Mods/CustomFlow/Meshes/SM_Custom_Cowl";
        var customMaterialFlowProject = new NativeSuitProject
        {
            TargetPackages = new TargetPackages
            {
                Playable = "/Game/Mods/CustomFlow/Characters/BP_CustomFlow_Playable",
            },
            GeneratedMaterials =
            [
                new GeneratedMaterialEntry { PackagePath = customBaselineMaterial },
            ],
            CustomStaticMeshes =
            [
                new CustomStaticMeshImport
                {
                    Id = "custom-cowl",
                    ResolvedComponent = "CustomMesh_CustomCowl",
                    MeshPackagePath = customMeshPackage,
                    MaterialPath = customBaselineMaterial,
                },
            ],
        };
        var referencedCustomBaseline = MainForm.ReferencedGeneratedMaterialPackagesForRelease(
            customMaterialFlowProject);
        var requiredCustomBaseline = MainForm.AssignedModMaterialPackagesForRelease(
            customMaterialFlowProject);
        var renamedCustomBaseline = MainForm.ReplaceCustomStaticMeshMaterialReferences(
            customMaterialFlowProject,
            customBaselineMaterial.ToLowerInvariant(),
            "/Game/Mods/CustomFlow/Materials/MI_RenamedCowl");
        var capturedCustomTransform = MainForm.CaptureViewerCustomMeshTransform(
            new CustomStaticMeshImport
            {
                Scale = 215f,
                OffsetX = 12f,
                OffsetY = -8f,
                OffsetZ = 4f,
                RotationPitch = 15f,
                RotationYaw = 25f,
                RotationRoll = 35f,
            });
        customMaterialFlowProject.CustomStaticMeshes[0].MaterialPath = customMeshPackage;
        var materialMeshCollision = MainForm.MaterialCustomMeshPackageCollisions(customMaterialFlowProject);
        Check(
            referencedCustomBaseline.SequenceEqual([customBaselineMaterial], StringComparer.OrdinalIgnoreCase) &&
            requiredCustomBaseline.SequenceEqual([customBaselineMaterial], StringComparer.OrdinalIgnoreCase) &&
            renamedCustomBaseline == 1 &&
            capturedCustomTransform.Scale == 215f &&
            capturedCustomTransform.OffsetX == 12f &&
            capturedCustomTransform.OffsetY == -8f &&
            capturedCustomTransform.OffsetZ == 4f &&
            capturedCustomTransform.RotationPitch == 15f &&
            capturedCustomTransform.RotationYaw == 25f &&
            capturedCustomTransform.RotationRoll == 35f &&
            materialMeshCollision.SequenceEqual([customMeshPackage], StringComparer.OrdinalIgnoreCase),
            "custom mesh material baselines survive release ownership checks and rename, while package collisions and viewer transform drift are detectable",
            failures,
            output);

        const string closureRoot = "/Game/Mods/Shared/Materials/MI_Shared";
        const string closureParent = "/Game/Mods/Shared/Materials/MI_Parent";
        const string closureTexture = "/Game/Mods/Shared/Textures/T_Body_BC";
        const string closureParentTexture = "/Game/Mods/Shared/Textures/T_Parent_MMR";
        var closureGraph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [closureRoot] = [closureParent, closureTexture, "/Game/Characters/Shared/T_Game"],
            [closureParent] = [closureParentTexture, closureRoot],
            [closureTexture] = [],
            [closureParentTexture] = [],
        };
        var materialClosure = ToolMaterialLibraryService.WalkModLocalMaterialDependencyClosure(
            closureRoot,
            package => closureGraph.TryGetValue(package, out var dependencies)
                ? dependencies
                : Array.Empty<string>());
        var reachableImportPackages = ToolMaterialLibraryService.ReachableImportPackagesForTest(
            new (string ObjectName, int OuterImportIndex)[]
            {
                ("/Game/Mods/Electric/T_Body_BC", -1),
                ("T_Body_BC", 0),
                ("/Game/Mods/Electric/Textures/T_Body_BC", -1),
                ("T_Body_BC", 2),
                ("/Game/Mods/Electric/Materials/MI_Parent", -1),
                ("MI_Parent", 4),
            },
            new[] { 3, 5 });
        var escapingDependencyRejected = false;
        try
        {
            ToolMaterialLibraryService.WalkModLocalMaterialDependencyClosure(
                closureRoot,
                package => package.Equals(closureRoot, StringComparison.OrdinalIgnoreCase)
                    ? ["/Game/Mods/../Escaped/T_Bad"]
                    : Array.Empty<string>());
        }
        catch (InvalidOperationException)
        {
            escapingDependencyRejected = true;
        }

        var closureFilesRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-material-closure-regression-" + Guid.NewGuid().ToString("N"));
        var completeClosureFilesAccepted = false;
        var missingClosureFilesRejected = false;
        var emptyOptionalBulkRejected = false;
        try
        {
            Directory.CreateDirectory(closureFilesRoot);
            var packageBase = Path.Combine(closureFilesRoot, "MI_Fixture");
            File.WriteAllBytes(packageBase + ".uasset", [1]);
            File.WriteAllBytes(packageBase + ".uexp", [2]);
            completeClosureFilesAccepted = ToolMaterialLibraryService.ClosurePackageFilesAreCompleteForTest(packageBase);
            File.Delete(packageBase + ".uexp");
            missingClosureFilesRejected = !ToolMaterialLibraryService.ClosurePackageFilesAreCompleteForTest(packageBase);
            File.WriteAllBytes(packageBase + ".uexp", [2]);
            File.WriteAllBytes(packageBase + ".ubulk", []);
            emptyOptionalBulkRejected = !ToolMaterialLibraryService.ClosurePackageFilesAreCompleteForTest(packageBase);
        }
        finally
        {
            try { Directory.Delete(closureFilesRoot, recursive: true); } catch { /* best effort */ }
        }
        Check(
            materialClosure.Count == 4 &&
            materialClosure.Contains(closureRoot, StringComparer.OrdinalIgnoreCase) &&
            materialClosure.Contains(closureParent, StringComparer.OrdinalIgnoreCase) &&
            materialClosure.Contains(closureTexture, StringComparer.OrdinalIgnoreCase) &&
            materialClosure.Contains(closureParentTexture, StringComparer.OrdinalIgnoreCase) &&
            !materialClosure.Contains("/Game/Characters/Shared/T_Game", StringComparer.OrdinalIgnoreCase) &&
            reachableImportPackages.SequenceEqual(
                new[]
                {
                    "/Game/Mods/Electric/Materials/MI_Parent",
                    "/Game/Mods/Electric/Textures/T_Body_BC",
                },
                StringComparer.OrdinalIgnoreCase) &&
            !reachableImportPackages.Contains(
                "/Game/Mods/Electric/T_Body_BC",
                StringComparer.OrdinalIgnoreCase) &&
            escapingDependencyRejected &&
            completeClosureFilesAccepted &&
            missingClosureFilesRejected &&
            emptyOptionalBulkRejected,
            "shared tool materials follow only reachable live imports, carry their cycle-safe mod-local closure, and reject escaping or incomplete packages",
            failures,
            output);

        var libraryRepairRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-material-library-repair-" + Guid.NewGuid().ToString("N"));
        var libraryRepairRollbackPassed = false;
        var libraryRepairCommitPassed = false;
        var incompleteAtomicPromotionRejected = false;
        try
        {
            const string repairPackage = "/Game/Mods/Fixture/MI_Repair";
            var repairLibrary = new ToolMaterialLibraryService(libraryRepairRoot);
            var archivedBase = Path.Combine(
                repairLibrary.ContentRoot,
                repairPackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(archivedBase)!);
            File.WriteAllText(archivedBase + ".uasset", "old-uasset");
            File.WriteAllText(archivedBase + ".uexp", "old-uexp");
            File.WriteAllText(archivedBase + ".ubulk", "old-ubulk");
            Directory.CreateDirectory(repairLibrary.CatalogRoot);
            File.WriteAllText(repairLibrary.CatalogPath, "old-catalog");

            var candidateBase = Path.Combine(libraryRepairRoot, "candidate", "MI_Repair");
            Directory.CreateDirectory(Path.GetDirectoryName(candidateBase)!);
            File.WriteAllText(candidateBase + ".uasset", "new-uasset");
            File.WriteAllText(candidateBase + ".uexp", "new-uexp");
            File.WriteAllText(candidateBase + ".ubulk", "new-ubulk");
            var incompleteBase = Path.Combine(libraryRepairRoot, "incomplete", "MI_Repair");
            Directory.CreateDirectory(Path.GetDirectoryName(incompleteBase)!);
            File.WriteAllText(incompleteBase + ".uasset", "partial-uasset");

            using (repairLibrary.BeginRepairSnapshotForTest([repairPackage]))
            {
                try
                {
                    ToolMaterialLibraryService.ReplacePackageFilesAtomicallyForTest(
                        incompleteBase,
                        archivedBase);
                }
                catch (InvalidOperationException)
                {
                    incompleteAtomicPromotionRejected =
                        File.ReadAllText(archivedBase + ".uasset") == "old-uasset" &&
                        File.ReadAllText(archivedBase + ".uexp") == "old-uexp" &&
                        File.ReadAllText(archivedBase + ".ubulk") == "old-ubulk";
                }

                ToolMaterialLibraryService.ReplacePackageFilesAtomicallyForTest(candidateBase, archivedBase);
                File.WriteAllText(repairLibrary.CatalogPath, "new-catalog");
            }
            libraryRepairRollbackPassed =
                File.ReadAllText(archivedBase + ".uasset") == "old-uasset" &&
                File.ReadAllText(archivedBase + ".uexp") == "old-uexp" &&
                File.ReadAllText(archivedBase + ".ubulk") == "old-ubulk" &&
                File.ReadAllText(repairLibrary.CatalogPath) == "old-catalog";

            using (var committedRepair = repairLibrary.BeginRepairSnapshotForTest([repairPackage]))
            {
                ToolMaterialLibraryService.ReplacePackageFilesAtomicallyForTest(candidateBase, archivedBase);
                File.WriteAllText(repairLibrary.CatalogPath, "committed-catalog");
                committedRepair.Commit();
            }
            libraryRepairCommitPassed =
                File.ReadAllText(archivedBase + ".uasset") == "new-uasset" &&
                File.ReadAllText(archivedBase + ".uexp") == "new-uexp" &&
                File.ReadAllText(archivedBase + ".ubulk") == "new-ubulk" &&
                File.ReadAllText(repairLibrary.CatalogPath) == "committed-catalog";
        }
        finally
        {
            try { Directory.Delete(libraryRepairRoot, recursive: true); } catch { /* best effort */ }
        }
        Check(
            incompleteAtomicPromotionRejected &&
            libraryRepairRollbackPassed &&
            libraryRepairCommitPassed,
            "material repair atomically promotes complete package trios and rolls its shared catalog/archive back until the suit commits",
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
        var lockViolation = new IOException("locked range", unchecked((int)0x80070021));
        Check(
            FileLockUtil.IsTransient(new InvalidOperationException("wrapped", sharingViolation)) &&
            FileLockUtil.IsTransient(new InvalidOperationException(
                "wrapped",
                new TransientFileLockException("structured lock"))) &&
            FileLockUtil.IsTransient(new AggregateException(
                new FileNotFoundException("first deterministic branch"),
                new InvalidOperationException("second lock branch", lockViolation))) &&
            !FileLockUtil.IsTransient(new AggregateException(
                new FileNotFoundException("missing"),
                new InvalidDataException("bad data"))) &&
            !FileLockUtil.IsTransient(new FileNotFoundException("missing")) &&
            !FileLockUtil.IsTransient(new IOException("sharing violation text without a sharing-violation code")),
            "only transient sharing violations enter the bounded file-lock retry path",
            failures,
            output);

        var successfulRetryAttempts = 0;
        var successfulRetryDelays = new List<int>();
        string? successfulRetryResult = null;
        Exception? successfulRetryFailure = null;
        try
        {
            successfulRetryResult = MainForm.RunFileLockRetryPolicyAsync(
                    () =>
                    {
                        successfulRetryAttempts++;
                        if (successfulRetryAttempts < 3)
                        {
                            throw new IOException("deterministic sharing violation", unchecked((int)0x80070020));
                        }
                        return "ready";
                    },
                    "exercise the deterministic retry fixture",
                    delayAsync: delay =>
                    {
                        successfulRetryDelays.Add(delay);
                        return Task.CompletedTask;
                    })
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            successfulRetryFailure = ex;
        }
        Check(
            successfulRetryFailure is null &&
            successfulRetryResult == "ready" &&
            successfulRetryAttempts == 3 &&
            successfulRetryDelays.SequenceEqual([150, 300]),
            "transient file locks retry deterministically and return the eventual successful result",
            failures,
            output);

        var exhaustedRetryAttempts = 0;
        var exhaustedRetryDelays = new List<int>();
        TransientFileLockException? exhaustedRetryFailure = null;
        Exception? unexpectedExhaustedFailure = null;
        try
        {
            MainForm.RunFileLockRetryPolicyAsync<int>(
                    () =>
                    {
                        exhaustedRetryAttempts++;
                        throw new IOException("persistent sharing violation", unchecked((int)0x80070020));
                    },
                    "exercise retry exhaustion",
                    delayAsync: delay =>
                    {
                        exhaustedRetryDelays.Add(delay);
                        return Task.CompletedTask;
                    })
                .GetAwaiter()
                .GetResult();
        }
        catch (TransientFileLockException ex)
        {
            exhaustedRetryFailure = ex;
        }
        catch (Exception ex)
        {
            unexpectedExhaustedFailure = ex;
        }

        var nonTransientRetryAttempts = 0;
        var nonTransientRetryDelays = new List<int>();
        Exception? nonTransientRetryFailure = null;
        try
        {
            MainForm.RunFileLockRetryPolicyAsync<int>(
                    () =>
                    {
                        nonTransientRetryAttempts++;
                        throw new InvalidDataException("deterministic parse failure");
                    },
                    "exercise a non-transient failure",
                    delayAsync: delay =>
                    {
                        nonTransientRetryDelays.Add(delay);
                        return Task.CompletedTask;
                    })
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            nonTransientRetryFailure = ex;
        }
        Check(
            unexpectedExhaustedFailure is null &&
            exhaustedRetryFailure is not null &&
            FileLockUtil.IsTransient(exhaustedRetryFailure.InnerException) &&
            exhaustedRetryAttempts == 6 &&
            exhaustedRetryDelays.SequenceEqual([150, 300, 600, 1000, 1500]) &&
            nonTransientRetryFailure is InvalidDataException &&
            nonTransientRetryAttempts == 1 &&
            nonTransientRetryDelays.Count == 0,
            "file-lock retry exhaustion is bounded and non-transient failures are never retried",
            failures,
            output);

        var structuredRetryAttempts = 0;
        var structuredRetryDelays = new List<int>();
        var structuredRetryResult = MainForm.RunStructuredFileLockRetryPolicyAsync(
                () => ++structuredRetryAttempts,
                result => result < 3,
                "exercise a structured lock result",
                delayAsync: delay =>
                {
                    structuredRetryDelays.Add(delay);
                    return Task.CompletedTask;
                })
            .GetAwaiter()
            .GetResult();
        Check(
            structuredRetryResult == 3 &&
            structuredRetryAttempts == 3 &&
            structuredRetryDelays.SequenceEqual([150, 300]),
            "structured stage writers use the same asynchronous transient-lock retry schedule",
            failures,
            output);

        TransientFileLockException? packagePatchLock = null;
        try
        {
            UAssetPatchService.ExecutePackagePatchOperationForTest(
                () => throw new IOException("package sharing violation", unchecked((int)0x80070020)));
        }
        catch (TransientFileLockException ex)
        {
            packagePatchLock = ex;
        }
        var ordinaryPackageFailure = UAssetPatchService.ExecutePackagePatchOperationForTest(
            () => throw new InvalidDataException("package parse fixture"));
        Check(
            packagePatchLock is not null &&
            FileLockUtil.IsTransient(packagePatchLock.InnerException) &&
            !ordinaryPackageFailure.Success &&
            ordinaryPackageFailure.Error?.Contains("package parse fixture", StringComparison.Ordinal) == true,
            "name-map package writes expose transient locks to the retry policy without losing ordinary structured errors",
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

        var pairedCapeMaterialOverrideProject = CreateCertifiedNightwingCapeAdapterProject();
        pairedCapeMaterialOverrideProject.MaterialAssignments =
        [
            new SavedMaterialAssignment
            {
                Component = "Cape",
                Slot = 0,
                Context = "both",
                MiPackagePath = "/Game/Mods/MaterialProof/Materials/MI_Cape_Custom",
            },
            new SavedMaterialAssignment
            {
                Component = "Torso",
                Slot = 0,
                Context = "playable",
                MiPackagePath = "/Game/Mods/MaterialProof/Materials/MI_Glider_Gameplay",
            },
            new SavedMaterialAssignment
            {
                Component = "Torso",
                Slot = 0,
                Context = "cutscene",
                MiPackagePath = "/Game/Mods/MaterialProof/Materials/MI_Glider_Cutscene",
            },
        ];
        Check(
            StageValidationService.FinalMaterialPackageForTest(
                pairedCapeMaterialOverrideProject,
                "playable",
                "Cape",
                0,
                "/Game/Donor/MI_Cape") == "/Game/Mods/MaterialProof/Materials/MI_Cape_Custom" &&
            StageValidationService.FinalMaterialPackageForTest(
                pairedCapeMaterialOverrideProject,
                "cutscene",
                "Cape",
                0,
                "/Game/Donor/MI_Cape") == "/Game/Mods/MaterialProof/Materials/MI_Cape_Custom" &&
            StageValidationService.FinalMaterialPackageForTest(
                pairedCapeMaterialOverrideProject,
                "playable",
                "Torso",
                0,
                "/Game/Donor/MI_Glider") == "/Game/Mods/MaterialProof/Materials/MI_Glider_Gameplay" &&
            StageValidationService.FinalMaterialPackageForTest(
                pairedCapeMaterialOverrideProject,
                "cutscene",
                "Torso",
                0,
                "/Game/Donor/MI_Glider") == "/Game/Mods/MaterialProof/Materials/MI_Glider_Cutscene" &&
            StageValidationService.FinalMaterialPackageForTest(
                pairedCapeMaterialOverrideProject,
                "playable",
                "Torso",
                1,
                "/Game/Donor/MI_Glider_LOD1") == "/Game/Donor/MI_Glider_LOD1",
            "paired-cape build validation accepts declared Cape/glider materials per runtime role while untouched slots retain donor identity",
            failures,
            output);

        const string autoPairPlayablePackage =
            "/Game/Characters/Minifig/Batman/BP_Batman_AutoPair_Playable";
        const string autoPairCutscenePackage =
            "/Game/Characters/Minifig/Batman/BP_Batman_AutoPair_Cutscene";
        var autoPairPlayableGlider = new NativeSuitPartRecord
        {
            Context = "playable",
            SourcePackagePath = autoPairPlayablePackage,
            Slot = "Torso",
            MeshObjectName = "SK_CAPE_Glide",
            MeshPackagePath = "/Game/Characters/Attachments/Cape/SK_CAPE_Glide",
            MeshObjectPath = "/Game/Characters/Attachments/Cape/SK_CAPE_Glide.SK_CAPE_Glide",
            AnimClassObjectName = "ABP_Cape_Glide_C",
            ComponentTags = ["Glider"],
        };
        var autoPairCutsceneGlider = new NativeSuitPartRecord
        {
            Context = "cutscene",
            SourcePackagePath = autoPairCutscenePackage,
            Slot = "Torso",
            MeshObjectName = "SK_CAPE_Glide",
            MeshPackagePath = "/Game/Characters/Attachments/Cape/SK_CAPE_Glide",
            MeshObjectPath = "/Game/Characters/Attachments/Cape/SK_CAPE_Glide.SK_CAPE_Glide",
            AnimClassObjectName = "ABP_Cape_Glide_C",
            ComponentTags = ["Glider"],
        };
        var autoPairPlayableCape = new NativeSuitPartRecord
        {
            Context = "playable",
            SourcePackagePath = autoPairPlayablePackage,
            Slot = "Cape",
            MeshObjectName = "SK_CAPE_Spiked",
            MeshPackagePath = "/Game/Characters/Attachments/Cape/SK_CAPE_Spiked",
            MeshObjectPath = "/Game/Characters/Attachments/Cape/SK_CAPE_Spiked.SK_CAPE_Spiked",
            ComponentTags = ["TtCharacterAsset.Cape"],
        };
        var autoPairCutsceneCape = new NativeSuitPartRecord
        {
            Context = "cutscene",
            SourcePackagePath = autoPairCutscenePackage,
            Slot = "Cape",
            MeshObjectName = "SK_CAPE_Spiked_Advanced",
            MeshPackagePath = "/Game/Characters/Attachments/Cape/SK_CAPE_Spiked_Advanced",
            MeshObjectPath = "/Game/Characters/Attachments/Cape/SK_CAPE_Spiked_Advanced.SK_CAPE_Spiked_Advanced",
            ComponentTags = ["TtCharacterAsset.Cape"],
        };
        var autoPairIndex = new NativeSuitPartIndex
        {
            Parts =
            [
                autoPairPlayableGlider,
                autoPairCutsceneGlider,
                autoPairPlayableCape,
                autoPairCutsceneCape,
                new NativeSuitPartRecord
                {
                    Context = "playable",
                    SourcePackagePath = "/Game/Characters/Minifig/Batman/BP_Batman_Decoy_Playable",
                    Slot = "Cape",
                    MeshObjectName = "SK_CAPE_Decoy",
                    MeshPackagePath = "/Game/Characters/Attachments/Cape/SK_CAPE_Decoy",
                    ComponentTags = ["Cape"],
                }
            ]
        };
        var exactAutoPairFound = MainForm.TryFindMatchingCosmeticCapeForGliderForTest(
            autoPairIndex,
            autoPairPlayableGlider,
            autoPairCutsceneGlider,
            out var resolvedAutoPairPlayableCape,
            out var resolvedAutoPairCutsceneCape,
            out _);
        var incompleteAutoPairRejected = !MainForm.TryFindMatchingCosmeticCapeForGliderForTest(
            autoPairIndex,
            autoPairPlayableGlider,
            null,
            out _,
            out _,
            out _);
        Check(
            exactAutoPairFound &&
            ReferenceEquals(resolvedAutoPairPlayableCape, autoPairPlayableCape) &&
            ReferenceEquals(resolvedAutoPairCutsceneCape, autoPairCutsceneCape) &&
            incompleteAutoPairRejected,
            "selecting a paired-capable glide cape can atomically recover its exact Cape plus Torso donor pair",
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

        var genericCapeLessPair = CreateCertifiedNightwingCapeAdapterProject();
        const string genericCapeLessPlayable =
            "/Game/Characters/Minifig/Catwoman/BP_Catwoman_Default_Playable";
        genericCapeLessPair.PlayableTemplate!.PackagePath = genericCapeLessPlayable;
        genericCapeLessPair.BaseProfile!.GameplayDonorPackage = genericCapeLessPlayable;
        genericCapeLessPair.BaseProfile.GameplayFamily = "Catwoman";
        genericCapeLessPair.BaseProfile.VisualBasePackage =
            "/Game/Characters/Minifig/Catwoman/BP_Catwoman_Default_Cutscene";
        genericCapeLessPair.BaseProfile.VisualFamily = "Catwoman";
        var genericCapeLessConfigured = GliderService.TryConfigurePairedCapeAdapter(
            genericCapeLessPair,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            nativeGliderComponent: "Cape",
            out _);
        foreach (var graft in genericCapeLessPair.PartGrafts)
        {
            graft.ResolvedComponent = graft.Slot;
        }
        GliderService.RefreshPairedCapeAdapterResolvedComponents(genericCapeLessPair);
        Check(
            genericCapeLessConfigured &&
            genericCapeLessPair.PairedCapeAdapter is not null &&
            genericCapeLessPair.PairedCapeAdapter.GameplayDonorPackage.Equals(
                genericCapeLessPlayable,
                StringComparison.OrdinalIgnoreCase) &&
            GliderService.IsDeclaredPairedCapeAdapterValid(
                genericCapeLessPair,
                AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
                requireResolvedComponents: true,
                out _),
            "the paired-cape adapter is driven by the cape-less glide contract rather than a Nightwing identity",
            failures,
            output);

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
        const string dlcCertifiedMas = "/DLC_BeyondPack/Animation/MontageAnimSets/MAS_Glide_Beyond";
        var dlcParentsRemainExactContentPackages =
            StageValidationService.PairedCapeAnimationParentsAreSafeForTest(
                [
                    "/DLC_BeyondPack/Animation/MontageAnimSets/MAS_Glide_Native",
                    "/Game/Animation/MontageAnimSets/Character/MAS_Playable"
                ],
                [
                    dlcCertifiedMas,
                    "/Game/Animation/MontageAnimSets/Character/MAS_Playable"
                ],
                dlcCertifiedMas,
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
            unresolvedParentEntryRejected &&
            dlcParentsRemainExactContentPackages,
            "paired-cape MAS/LAS clones retain every non-glide parent across game/DLC mounts while replacing each native glide category with exactly one certified package (duplicates and same-stem aliases rejected)",
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
        var exactDlcBehaviorBridgeAccepted =
            StageValidationService.BehaviorBridgeReferencesAreSafeForTest(
                [("/DLC_BeyondPack/Characters/DA_DPRD_Beyond", "DA_DPRD_Beyond")],
                authoredBatmanDprd,
                "/DLC_BeyondPack/Characters/DA_DPRD_Beyond");
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
            exactDlcBehaviorBridgeAccepted &&
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

        var shellRemovalRepairProject = CreateCertifiedNightwingCapeAdapterProject();
        shellRemovalRepairProject.Requirements =
        [
            new NativeSuitRequirement { Kind = "remove-component", TargetComponent = "Head:0" },
            new NativeSuitRequirement { Kind = "remove-component", TargetComponent = "Torso:0" },
            new NativeSuitRequirement { Kind = "remove-component", TargetComponent = "TtCharacterAssetMinion:0" },
            new NativeSuitRequirement { Kind = "remove-component", TargetComponent = "UnrelatedAttachment:0" },
        ];
        var repairedShellRemovals = MainForm.RemoveUnsafePairedCapeRemovalRulesForTest(
            shellRemovalRepairProject,
            ["Cape", "Torso"]);
        var shellRemovalWithIndependentHairProject = CreateCertifiedNightwingCapeAdapterProject();
        var shellRemovalWithIndependentHairAdapterConfigured = GliderService.TryConfigurePairedCapeAdapter(
            shellRemovalWithIndependentHairProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Torso",
            out _);
        shellRemovalWithIndependentHairProject.PartGrafts.Add(new SavedPartGraft
        {
            Slot = "Head",
            ResolvedComponent = "Head",
            InstanceId = "user-hair",
            OccupancyGroup = "head.scalp_hair"
        });
        var independentHairCanBeRemoved = !MainForm.IsPairedCapeShellRemovalBlockedForTest(
            shellRemovalWithIndependentHairProject,
            "Head",
            ["Head", "Face", "Cape", "Torso"]);
        var nativeShellHeadProject = CreateCertifiedNightwingCapeAdapterProject();
        var nativeShellHeadAdapterConfigured = GliderService.TryConfigurePairedCapeAdapter(
            nativeShellHeadProject,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            "Torso",
            out _);
        var nativeShellHeadCanBeHidden = !MainForm.IsPairedCapeShellRemovalBlockedForTest(
            nativeShellHeadProject,
            "Head",
            ["Head", "Face", "Cape", "Torso"]);
        var nativeShellHeadUsesPreserveNodeHide =
            MainForm.ShouldPreservePairedCapeShellNodeForVisualHideForTest(
                nativeShellHeadProject,
                "Head",
                ["Head", "Face", "Cape", "Torso"]) &&
            !MainForm.ShouldPreservePairedCapeShellNodeForVisualHideForTest(
                nativeShellHeadProject,
                "Cape",
                ["Head", "Face", "Cape", "Torso"]);
        var exactCapeFieldRemainsAtomic = MainForm.IsPairedCapeShellRemovalBlockedForTest(
            nativeShellHeadProject,
            "Cape",
            ["Head", "Face", "Cape", "Torso"]);
        Check(
            repairedShellRemovals.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["Torso"]) &&
            shellRemovalRepairProject.Requirements.Count == 3 &&
            shellRemovalRepairProject.Requirements.Any(requirement =>
                requirement.TargetComponent == "Head:0") &&
            shellRemovalRepairProject.Requirements.Any(requirement =>
                requirement.TargetComponent == "TtCharacterAssetMinion:0") &&
            shellRemovalRepairProject.Requirements.Any(requirement =>
                requirement.TargetComponent == "UnrelatedAttachment:0") &&
            StageValidationService.AuthoredShellLiveComponentsRemainForTest(
                ["Face", "Head", "Cape", "Torso"],
                ["Head_2", "Torso", "Cape", "Head", "Face"]) &&
            !StageValidationService.AuthoredShellLiveComponentsRemainForTest(
                ["Face", "Head", "Cape", "Torso"],
                ["Head_2", "Cape", "Head", "Face"]) &&
            independentHairCanBeRemoved &&
            nativeShellHeadCanBeHidden &&
            nativeShellHeadUsesPreserveNodeHide &&
            exactCapeFieldRemainsAtomic &&
            ComponentRemoveService.IsVisualMeshProperty("StaticMesh") &&
            ComponentRemoveService.IsVisualMeshProperty("SkeletalMesh") &&
            ComponentRemoveService.IsVisualMeshProperty("SkinnedAsset") &&
            !ComponentRemoveService.IsVisualMeshProperty("OverrideMaterials") &&
            shellRemovalWithIndependentHairAdapterConfigured &&
            nativeShellHeadAdapterConfigured,
            "paired-cape removal keeps Cape/Torso atomic while ordinary shell Head visuals use a preserve-node hide",
            failures,
            output);

        var staleMaterialTargetProject = new NativeSuitProject
        {
            PartGrafts =
            [
                new SavedPartGraft
                {
                    Slot = "Cape",
                    ResolvedComponent = "Cape",
                    IsGlider = false,
                }
            ],
            MaterialAssignments =
            [
                new SavedMaterialAssignment { Component = "Cape_2", Slot = 0, MiPackagePath = "/Game/Test/MI_Cape0" },
                new SavedMaterialAssignment { Component = "Cape_2", Slot = 1, MiPackagePath = "/Game/Test/MI_Cape1" },
                new SavedMaterialAssignment { Component = "CharacterMesh0", Slot = 0, MiPackagePath = "/Game/Test/MI_Body" },
            ]
        };
        var recoveredMaterialTargets = MainForm.ReconcileResolvedPartMaterialAssignmentsForTest(
            staleMaterialTargetProject,
            Array.Empty<KeyValuePair<string, string>>());
        Check(
            recoveredMaterialTargets.Count == 1 &&
            staleMaterialTargetProject.MaterialAssignments.Count(assignment =>
                assignment.Component.Equals("Cape", StringComparison.OrdinalIgnoreCase)) == 2 &&
            staleMaterialTargetProject.MaterialAssignments.Any(assignment =>
                assignment.Component.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase)) &&
            staleMaterialTargetProject.MaterialAssignments.All(assignment =>
                !assignment.Component.Equals("Cape_2", StringComparison.OrdinalIgnoreCase)),
            "legacy suffixed part material targets recover to one unambiguous current graft without guessing core fields",
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
        baseChangeProject.MaterialAssignments =
        [
            new SavedMaterialAssignment { Component = "Cape", Slot = 0, MiPackagePath = "/Game/Test/MI_Cape" },
            new SavedMaterialAssignment { Component = "Torso", Slot = 0, MiPackagePath = "/Game/Test/MI_Torso" },
            new SavedMaterialAssignment { Component = "CharacterMesh0", Slot = 0, MiPackagePath = "/Game/Test/MI_Body" },
        ];
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
            baseChangeProject.MaterialAssignments.Count == 1 &&
            baseChangeProject.MaterialAssignments[0].Component == "CharacterMesh0" &&
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

        var nativeBodies = NativeBodyProfileService.Catalog();
        Check(
            nativeBodies.Count == 9 &&
            nativeBodies.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 9 &&
            nativeBodies.Count(profile => profile.Id.Contains("armless", StringComparison.OrdinalIgnoreCase)) == 2,
            "native body catalog exposes the exact shipped Minifig/Smallfig variants",
            failures,
            output);
        Check(
            nativeBodies.All(profile =>
                !profile.Id.Contains("08-armless", StringComparison.OrdinalIgnoreCase) &&
                !profile.Id.Contains("08-headless", StringComparison.OrdinalIgnoreCase) &&
                !profile.Id.Contains("08-no-", StringComparison.OrdinalIgnoreCase)) &&
            nativeBodies.Single(profile => profile.Id == "minifig-headless").HeadPolicy ==
                NativeBodyProfileService.IntentionallyAbsentHeadPolicy &&
            nativeBodies.Single(profile => profile.Id == "smallfig-armless").GeometryFamily == "Smallfig",
            "body profiles do not synthesize unsupported reduced 08 combinations and preserve head policy",
            failures,
            output);
        var bodyDeclaration = new NativeSuitProject
        {
            BodyProfile = NativeBodyProfileService.Find("minifig-no-upper-body"),
        };
        var bodyRoundTrip = System.Text.Json.JsonSerializer.Deserialize<NativeSuitProject>(
            System.Text.Json.JsonSerializer.Serialize(bodyDeclaration));
        Check(
            MainForm.ProjectRequiresCompletedGraftStage(bodyDeclaration) &&
            bodyRoundTrip?.BodyProfile?.MeshPackagePath ==
                "/Game/Characters/LEGOfig/SK_LEGOFig_Minifig_NoUpperBody" &&
            bodyRoundTrip.BodyProfile.MissingRegions.Contains("Torso", StringComparer.OrdinalIgnoreCase),
            "native body declaration persists and requires a certified declarative stage",
            failures,
            output);
        var reducedBody = NativeBodyProfileService.Find("minifig-armless");
        var ordinaryBody = NativeBodyProfileService.Find("minifig-standard");
        Check(
            NativeBodyProfileService.SelectAfterBaseChange(reducedBody, ordinaryBody, baseIdentityChanged: false)?.Id ==
                "minifig-armless" &&
            NativeBodyProfileService.SelectAfterBaseChange(reducedBody, ordinaryBody, baseIdentityChanged: true)?.Id ==
                "minifig-standard",
            "reselecting the same base preserves an explicit native body while a real base change follows the new base",
            failures,
            output);

        var newerMaterial = new GeneratedMaterialEntry
        {
            PackagePath = "/Game/Mods/Shared/Materials/MI_Shared",
            DisplayName = "New metadata",
            CreatedUtc = "2026-01-02T00:00:00Z",
            CompatibleFaceMeshPackagePaths = ["/Game/Characters/Attachments/LEGOface/SK_LEGOface"],
        };
        var staleMaterial = new GeneratedMaterialEntry
        {
            PackagePath = newerMaterial.PackagePath,
            DisplayName = "Old metadata",
            CreatedUtc = "2026-01-01T00:00:00Z",
        };
        Check(
            !ToolMaterialLibraryService.PreferMigratedEntry(staleMaterial, newerMaterial) &&
            ToolMaterialLibraryService.PreferMigratedEntry(newerMaterial, staleMaterial),
            "older saved suits cannot replace newer workspace material metadata during migration",
            failures,
            output);

        var materialReferenceRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-material-library-regression-" + Guid.NewGuid().ToString("N"));
        var sharedReferencesDetected = false;
        var sharedLibraryRoundTrip = false;
        var legacyMaterialWasAdopted = false;
        var malformedLegacyMaterialWasRejected = false;
        var incompleteReleaseMaterialWasRejected = false;
        var unsafeMaterialPathRejected = false;
        var metadataSnapshotRecoveredSavedSuit = false;
        var previousSettings = AppSettings.Current;
        try
        {
            var exportRoot = Path.Combine(materialReferenceRoot, "ExportContent");
            var packageBase = Path.Combine(exportRoot, "Mods", "Shared", "Materials", "MI_Shared");
            Directory.CreateDirectory(Path.GetDirectoryName(packageBase)!);
            File.WriteAllText(packageBase + ".uasset", "cooked-uasset");
            File.WriteAllText(packageBase + ".uexp", "cooked-uexp");
            AppSettings.Current = new AppSettings
            {
                ProjectRoot = materialReferenceRoot,
                ExportContentRoot = exportRoot,
            };
            var projects = new SuitProjectService(materialReferenceRoot);
            projects.SaveProject(new NativeSuitProject
            {
                SlotId = "material_origin",
                DisplayName = "Material origin",
                GeneratedMaterials = [newerMaterial],
            });
            projects.SaveProject(new NativeSuitProject
            {
                SlotId = "material_consumer",
                DisplayName = "Material consumer",
                MaterialAssignments =
                [
                    new SavedMaterialAssignment
                    {
                        Component = "CharacterMesh0",
                        Slot = 0,
                        Context = "both",
                        MiPackagePath = newerMaterial.PackagePath,
                    },
                ],
            });
            var library = new ToolMaterialLibraryService(materialReferenceRoot);
            metadataSnapshotRecoveredSavedSuit = library.LoadMetadataSnapshot().Any(material =>
                material.PackagePath.Equals(newerMaterial.PackagePath, StringComparison.OrdinalIgnoreCase) &&
                material.CompatibleFaceMeshPackagePaths.SequenceEqual(
                    newerMaterial.CompatibleFaceMeshPackagePaths,
                    StringComparer.OrdinalIgnoreCase));
            library.Register([newerMaterial]);
            var references = library.FindReferencingSuits(
                newerMaterial.PackagePath,
                exceptSlotId: "material_origin");
            sharedReferencesDetected =
                references.Count == 1 && references[0].Equals("Material consumer", StringComparison.Ordinal);
            var importedProject = new NativeSuitProject { SlotId = "material_import" };
            var packageStage = Path.Combine(materialReferenceRoot, "PackageStage");
            var copied = library.CopyPackageToContentRoot(newerMaterial.PackagePath, packageStage);
            var stagedSharedUasset = Path.Combine(packageStage, "Mods", "Shared", "Materials", "MI_Shared.uasset");
            File.WriteAllText(stagedSharedUasset, "fresh-certified-stage-uasset");
            var refusedOverwrite = library.CopyPackageToContentRoot(newerMaterial.PackagePath, packageStage);
            sharedLibraryRoundTrip =
                library.LoadAvailable().Any(material => material.PackagePath.Equals(
                    newerMaterial.PackagePath,
                    StringComparison.OrdinalIgnoreCase)) &&
                library.ImportIntoProject(importedProject, newerMaterial.PackagePath) &&
                importedProject.GeneratedMaterials.Count == 1 &&
                copied.Count == 2 &&
                refusedOverwrite.Count == 0 &&
                File.ReadAllText(stagedSharedUasset) == "fresh-certified-stage-uasset" &&
                File.Exists(Path.Combine(packageStage, "Mods", "Shared", "Materials", "MI_Shared.uexp"));

            const string legacyPackage = "/Game/Mods/Legacy/MI_LegacyBody";
            var legacyProject = new NativeSuitProject
            {
                SlotId = "legacy_material_origin",
                DisplayName = "Legacy material origin",
                MaterialAssignments =
                [
                    new SavedMaterialAssignment
                    {
                        Component = "CharacterMesh0",
                        Slot = 0,
                        Context = "both",
                        MiPackagePath = legacyPackage,
                    },
                ],
            };
            projects.SaveProject(legacyProject);
            var legacyStageRoot = Path.Combine(
                projects.ProjectOutputDirectory(legacyProject),
                "IoStore",
                "Stage",
                "LEGOBatmanLotDK",
                "Content");
            var legacyStageBase = Path.Combine(legacyStageRoot, "Mods", "Legacy", "MI_LegacyBody");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyStageBase)!);
            File.WriteAllText(legacyStageBase + ".uasset", "legacy-uasset");
            File.WriteAllText(legacyStageBase + ".uexp", "legacy-uexp");

            var adopted = library.LoadAvailable().Any(material => material.PackagePath.Equals(
                legacyPackage,
                StringComparison.OrdinalIgnoreCase));
            Directory.Delete(projects.ProjectOutputDirectory(legacyProject), recursive: true);
            var legacyPackageStage = Path.Combine(materialReferenceRoot, "LegacyPackageStage");
            try
            {
                MainForm.StageReferencedToolMaterialsForRelease(
                    legacyProject,
                    library,
                    legacyPackageStage);
            }
            catch (InvalidOperationException ex)
            {
                malformedLegacyMaterialWasRejected =
                    ex.Message.Contains(legacyPackage, StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains("Re-cook", StringComparison.OrdinalIgnoreCase);
            }
            legacyMaterialWasAdopted =
                adopted &&
                library.LoadAvailable().Any(material => material.PackagePath.Equals(
                    legacyPackage,
                    StringComparison.OrdinalIgnoreCase));
            malformedLegacyMaterialWasRejected =
                malformedLegacyMaterialWasRejected &&
                legacyProject.GeneratedMaterials.Count == 0 &&
                MainForm.ReferencedGeneratedMaterialPackagesForRelease(
                        legacyProject,
                        library.LoadAvailable().Select(material => material.PackagePath))
                    .SequenceEqual([legacyPackage], StringComparer.OrdinalIgnoreCase);

            const string incompletePackage = "/Game/Mods/Legacy/MI_Incomplete";
            var incompleteProject = new NativeSuitProject
            {
                GeneratedMaterials = [],
                MaterialAssignments =
                [
                    new SavedMaterialAssignment
                    {
                        Component = "Head",
                        Slot = 0,
                        Context = "both",
                        MiPackagePath = incompletePackage,
                    },
                ],
            };
            try
            {
                MainForm.StageReferencedToolMaterialsForRelease(
                    incompleteProject,
                    library,
                    Path.Combine(materialReferenceRoot, "IncompletePackageStage"));
            }
            catch (InvalidOperationException ex)
            {
                incompleteReleaseMaterialWasRejected =
                    ex.Message.Contains(incompletePackage, StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains(".uasset", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains(".uexp", StringComparison.OrdinalIgnoreCase);
            }

            var unsafeMaterial = new GeneratedMaterialEntry
            {
                PackagePath = "/Game/Mods/../../Outside/MI_Unsafe",
                DisplayName = "Unsafe material",
            };
            library.Register([unsafeMaterial]);
            var unsafeStage = Path.Combine(materialReferenceRoot, "UnsafePackageStage");
            unsafeMaterialPathRejected =
                !File.ReadAllText(library.CatalogPath).Contains("MI_Unsafe", StringComparison.OrdinalIgnoreCase) &&
                library.CopyPackageToContentRoot(unsafeMaterial.PackagePath, unsafeStage).Count == 0 &&
                !Directory.Exists(Path.Combine(materialReferenceRoot, "Outside"));
        }
        finally
        {
            AppSettings.Current = previousSettings;
            try
            {
                if (Directory.Exists(materialReferenceRoot))
                {
                    Directory.Delete(materialReferenceRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of the unique regression directory.
            }
        }
        Check(
            sharedReferencesDetected,
            "shared material deletion and rename protection detects other saved suits",
            failures,
            output);
        Check(
            sharedLibraryRoundTrip,
            "a tool-created material can be discovered, imported, and staged by another suit without overwriting a fresh certified-stage package",
            failures,
            output);
        Check(
            metadataSnapshotRecoveredSavedSuit,
            "read-only material browser metadata recovers saved-suit compatibility without a catalog repair pass",
            failures,
            output);
        Check(
            legacyMaterialWasAdopted,
            "legacy suit assignments are adopted into the durable workspace material library",
            failures,
            output);
        Check(
            malformedLegacyMaterialWasRejected && incompleteReleaseMaterialWasRejected,
            "legacy assignment-only materials are adopted but malformed or incomplete cooked packages fail closed",
            failures,
            output);
        Check(
            unsafeMaterialPathRejected,
            "workspace material paths cannot escape their content roots",
            failures,
            output);

        var generatedDependencyRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-generated-material-dependency-" + Guid.NewGuid().ToString("N"));
        var generatedDependencyResolved = false;
        var movedDependencyResolved = false;
        var freshRecookPreferred = false;
        var incompleteCurrentCookRejected = false;
        var staleArchiveBulkRemoved = false;
        var outsideWorkspaceOutputRejected = false;
        var conflictingDuplicateOwnerRejected = false;
        var missingRequiredBulkRejected = false;
        var tamperedCookReportRejected = false;
        try
        {
            var generatedRoot = Path.Combine(generatedDependencyRoot, "Generated");
            Directory.CreateDirectory(generatedRoot);
            const string liveTexturePackage = "/Game/Mods/Steve/Textures/T_Steve_Head";
            const string movedTexturePackage = "/Game/Mods/Steve/Textures/T_Steve_Moved";
            const string outsideTexturePackage = "/Game/Mods/Steve/Textures/T_Steve_Outside";
            const string missingBulkPackage = "/Game/Mods/Steve/Textures/T_Steve_MissingBulk";
            const string tamperedReportPackage = "/Game/Mods/Steve/Textures/T_Steve_Tampered";
            var templateRoot = Path.Combine(generatedRoot, "RegressionTextureTemplates");
            var liveTextureOutput = Path.Combine(generatedRoot, "TextureImports", "steve", "T_Steve_Head_00001");
            var movedTextureOutput = Path.Combine(generatedRoot, "TextureImports", "steve", "T_Steve_Moved_00002");
            var missingBulkOutput = Path.Combine(generatedRoot, "TextureImports", "steve", "T_Steve_MissingBulk_00003");
            var tamperedReportOutput = Path.Combine(generatedRoot, "TextureImports", "steve", "T_Steve_Tampered_00004");
            var liveTexture = CreateGeneratedTextureCookFixture(
                liveTextureOutput,
                templateRoot,
                liveTexturePackage,
                "live",
                externalMips: false,
                includeUnexpectedBulk: true);
            var movedTexture = CreateGeneratedTextureCookFixture(
                movedTextureOutput,
                templateRoot,
                movedTexturePackage,
                "moved",
                externalMips: false);
            var missingBulkTexture = CreateGeneratedTextureCookFixture(
                missingBulkOutput,
                templateRoot,
                missingBulkPackage,
                "missing-bulk",
                externalMips: true,
                includeRequiredBulk: false);
            var tamperedReportTexture = CreateGeneratedTextureCookFixture(
                tamperedReportOutput,
                templateRoot,
                tamperedReportPackage,
                "tampered-report",
                externalMips: false);
            var tamperedReportBase = GeneratedTextureFixturePackageBase(
                tamperedReportOutput,
                tamperedReportPackage);
            File.AppendAllText(tamperedReportBase + ".uasset", "-changed-after-report");

            var outsideTextureOutput = Path.Combine(
                generatedDependencyRoot,
                "UntrustedCookOutput",
                "steve",
                "T_Steve_Outside_00003");
            var outsideTextureBase = Path.Combine(
                outsideTextureOutput,
                "Cooked",
                "LEGOBatmanLotDK",
                "Content",
                outsideTexturePackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outsideTextureBase)!);
            File.WriteAllText(outsideTextureBase + ".uasset", "outside-uasset");
            File.WriteAllText(outsideTextureBase + ".uexp", "outside-uexp");
            var outsideTexture = CreateGeneratedTextureCookFixture(
                outsideTextureOutput,
                templateRoot,
                outsideTexturePackage,
                "outside",
                externalMips: false);

            var movedSavedOutput = Path.Combine(
                Path.GetPathRoot(generatedDependencyRoot) ?? "C:\\",
                "RetiredWorkspace",
                "Generated",
                "TextureImports",
                "steve",
                "T_Steve_Moved_00002");
            var dependencyProject = new NativeSuitProject
            {
                SlotId = "steve_dependency_fixture",
                GeneratedTextures =
                [
                    liveTexture,
                    new GeneratedTextureEntry
                    {
                        DisplayName = movedTexture.DisplayName,
                        Kind = movedTexture.Kind,
                        CookProfile = movedTexture.CookProfile,
                        CookWidth = movedTexture.CookWidth,
                        CookHeight = movedTexture.CookHeight,
                        CookPixelFormat = movedTexture.CookPixelFormat,
                        PackagePath = movedTexture.PackagePath,
                        ObjectPath = movedTexture.ObjectPath,
                        TemplateJson = movedTexture.TemplateJson,
                        OutputRoot = movedSavedOutput,
                    },
                    outsideTexture,
                    missingBulkTexture,
                    tamperedReportTexture,
                ],
            };
            new SuitProjectService(generatedDependencyRoot).SaveProject(dependencyProject);
            var dependencyLibrary = new ToolMaterialLibraryService(generatedDependencyRoot);
            var resolvedLiveUasset = dependencyLibrary.ResolvePackageUasset(liveTexturePackage);
            var resolvedMovedUasset = dependencyLibrary.ResolvePackageUasset(movedTexturePackage);
            generatedDependencyResolved =
                !string.IsNullOrWhiteSpace(resolvedLiveUasset) &&
                File.ReadAllText(resolvedLiveUasset) == "live-uasset" &&
                File.ReadAllText(Path.ChangeExtension(resolvedLiveUasset, ".uexp")) == "live-uexp";
            movedDependencyResolved =
                !string.IsNullOrWhiteSpace(resolvedMovedUasset) &&
                File.ReadAllText(resolvedMovedUasset) == "moved-uasset" &&
                File.ReadAllText(Path.ChangeExtension(resolvedMovedUasset, ".uexp")) == "moved-uexp";

            // ResolvePackageUasset archived the first cook. Mutate the current recipe output and
            // prove the next lookup uses the recook instead of that older complete archive.
            var liveTextureBase = Path.Combine(
                liveTextureOutput,
                "Cooked",
                "LEGOBatmanLotDK",
                "Content",
                liveTexturePackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(liveTextureBase + ".uasset", "live-v2-uasset");
            File.WriteAllText(liveTextureBase + ".uexp", "live-v2-uexp");
            File.Delete(liveTextureBase + ".ubulk");
            WriteGeneratedTextureCookReport(liveTexture, liveTextureBase, externalMips: false);
            var resolvedFreshUasset = dependencyLibrary.ResolvePackageUasset(liveTexturePackage);
            freshRecookPreferred =
                !string.IsNullOrWhiteSpace(resolvedFreshUasset) &&
                File.ReadAllText(resolvedFreshUasset) == "live-v2-uasset" &&
                File.ReadAllText(Path.ChangeExtension(resolvedFreshUasset, ".uexp")) == "live-v2-uexp";

            // A saved recipe is authoritative even while its current cook is damaged. The older
            // complete workspace archive must not silently win, because that would package stale
            // texture bytes after a failed recook.
            File.Delete(liveTextureBase + ".uexp");
            try
            {
                _ = dependencyLibrary.ResolvePackageUasset(liveTexturePackage);
            }
            catch (InvalidOperationException ex)
            {
                incompleteCurrentCookRejected =
                    ex.Message.Contains("saved recipe", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase);
            }
            File.WriteAllText(liveTextureBase + ".uexp", "live-v2-uexp");

            try
            {
                _ = dependencyLibrary.ResolvePackageUasset(missingBulkPackage);
            }
            catch (InvalidOperationException ex)
            {
                missingRequiredBulkRejected =
                    ex.Message.Contains("saved recipe", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains(".ubulk", StringComparison.OrdinalIgnoreCase);
            }
            try
            {
                _ = dependencyLibrary.ResolvePackageUasset(tamperedReportPackage);
            }
            catch (InvalidOperationException ex)
            {
                tamperedCookReportRejected =
                    ex.Message.Contains("saved recipe", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase);
            }

            // An in-place material-library refresh must not leave an old external mip payload
            // beside a newer cook that moved all payload inline.
            dependencyLibrary.Register(
            [
                new GeneratedMaterialEntry
                {
                    DisplayName = "Texture refresh fixture",
                    Kind = "Material",
                    PackagePath = liveTexturePackage,
                },
            ]);
            var archivedLiveBase = Path.Combine(
                dependencyLibrary.ContentRoot,
                liveTexturePackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
            staleArchiveBulkRemoved = !File.Exists(archivedLiveBase + ".ubulk");
            try
            {
                outsideWorkspaceOutputRejected =
                    dependencyLibrary.ResolvePackageUasset(outsideTexturePackage) is null;
            }
            catch (InvalidOperationException ex)
            {
                outsideWorkspaceOutputRejected =
                    ex.Message.Contains("saved recipe", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase);
            }

            // Two saved recipes may only share a package identity when their cooked triplets are
            // byte-for-byte identical. Different owners must fail closed instead of depending on
            // project enumeration order.
            var duplicateTextureOutput = Path.Combine(
                generatedRoot,
                "TextureImports",
                "steve-duplicate",
                "T_Steve_Head_00004");
            var duplicateTexture = CreateGeneratedTextureCookFixture(
                duplicateTextureOutput,
                templateRoot,
                liveTexturePackage,
                "conflicting",
                externalMips: false);
            new SuitProjectService(generatedDependencyRoot).SaveProject(new NativeSuitProject
            {
                SlotId = "steve_dependency_duplicate_fixture",
                GeneratedTextures =
                [
                    duplicateTexture,
                ],
            });
            try
            {
                _ = dependencyLibrary.ResolvePackageUasset(liveTexturePackage);
            }
            catch (InvalidOperationException ex)
            {
                conflictingDuplicateOwnerRejected =
                    ex.Message.Contains("multiple saved recipes", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains(liveTexturePackage, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { Directory.Delete(generatedDependencyRoot, recursive: true); } catch { /* best effort */ }
        }
        Check(
            generatedDependencyResolved &&
            movedDependencyResolved &&
            freshRecookPreferred &&
            incompleteCurrentCookRejected &&
            staleArchiveBulkRemoved &&
            outsideWorkspaceOutputRejected &&
            conflictingDuplicateOwnerRejected &&
            missingRequiredBulkRejected &&
            tamperedCookReportRejected,
            "material dependencies prefer verified fresh workspace cooks, reject stale fallback, missing external mips, tampered cook reports, outside roots and conflicting owners, and remove stale bulk data",
            failures,
            output);

        var headlessProjectRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-headless-membership-" + Guid.NewGuid().ToString("N"));
        var headlessSuitPath = Path.Combine(
            headlessProjectRoot,
            "Generated",
            "NativeSuitGuiProjects",
            "fixture.native-suit-project.json");
        var headlessModService = new ModProjectService(headlessProjectRoot);
        var headlessMod = new NativeSuitModProject
        {
            Suits =
            [
                new ModSuitEntry
                {
                    SuitProjectPath = Path.GetRelativePath(headlessProjectRoot, headlessSuitPath),
                    SuitId = "fixture_suit",
                    Enabled = true,
                },
            ],
        };
        var headlessExactMatch = MainForm.HeadlessModContainsEnabledSuit(
            headlessModService,
            headlessMod,
            headlessSuitPath);
        headlessMod.Suits[0].SuitId = "";
        var headlessBlankCachedIdAccepted = MainForm.HeadlessModContainsEnabledSuit(
            headlessModService,
            headlessMod,
            headlessSuitPath);
        headlessMod.Suits[0].SuitId = "stale_legacy_identity";
        var headlessStaleCachedIdAccepted = MainForm.HeadlessModContainsEnabledSuit(
            headlessModService,
            headlessMod,
            headlessSuitPath);
        var headlessWrongPathRejected = !MainForm.HeadlessModContainsEnabledSuit(
            headlessModService,
            headlessMod,
            Path.Combine(headlessProjectRoot, "Generated", "NativeSuitGuiProjects", "other.native-suit-project.json"));
        headlessMod.Suits[0].Enabled = false;
        var headlessDisabledRejected = !MainForm.HeadlessModContainsEnabledSuit(
            headlessModService,
            headlessMod,
            headlessSuitPath);
        Check(
            headlessExactMatch &&
            headlessBlankCachedIdAccepted &&
            headlessStaleCachedIdAccepted &&
            headlessWrongPathRejected &&
            headlessDisabledRejected,
            "headless acceptance builds require the exact enabled suit path while accepting blank or stale legacy cached IDs",
            failures,
            output);

        TextureCookRegressionChecks.Run(failures, output);
        AnimationImportRegressionChecks.Run(failures, output);
        CharacterAnimationGraphRegressionChecks.Run(failures, output);

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

    private static GeneratedTextureEntry CreateGeneratedTextureCookFixture(
        string outputRoot,
        string templateRoot,
        string packagePath,
        string marker,
        bool externalMips,
        bool includeRequiredBulk = true,
        bool includeUnexpectedBulk = false)
    {
        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        Directory.CreateDirectory(templateRoot);
        var templateStem = UnrealPathUtil.AssetName(packagePath) +
                           (externalMips ? "_External" : "_Inline");
        var templateBase = Path.Combine(templateRoot, templateStem);
        var templateJson = templateBase + ".json";
        var templatePackage = "/Game/Regression/TextureTemplates/" + templateStem;
        var flags = externalMips
            ? "BULKDATA_PayloadInSeperateFile"
            : "BULKDATA_ForceInlinePayload";
        File.WriteAllText(
            templateJson,
            System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new
                {
                    Type = "Texture2D",
                    Name = templateStem,
                    Package = templatePackage,
                    SizeX = 4,
                    SizeY = 4,
                    PixelFormat = "PF_DXT1",
                    Mips = new object[]
                    {
                        new
                        {
                            SizeX = 4,
                            SizeY = 4,
                            BulkData = new
                            {
                                ElementCount = 8,
                                SizeOnDisk = 8,
                                OffsetInFile = 0,
                                BulkDataFlags = flags,
                            },
                        },
                    },
                },
            }));
        File.WriteAllText(templateBase + ".uasset", "template-uasset-" + templateStem);
        File.WriteAllText(templateBase + ".uexp", "template-uexp-" + templateStem);

        var packageBase = GeneratedTextureFixturePackageBase(outputRoot, packagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(packageBase)!);
        File.WriteAllText(packageBase + ".uasset", marker + "-uasset");
        File.WriteAllText(packageBase + ".uexp", marker + "-uexp");
        if (externalMips || includeUnexpectedBulk)
        {
            File.WriteAllText(packageBase + ".ubulk", marker + "-ubulk");
        }

        var texture = new GeneratedTextureEntry
        {
            DisplayName = marker,
            Kind = "Character texture",
            CookProfile = "release-regression",
            CookWidth = 4,
            CookHeight = 4,
            CookPixelFormat = "PF_DXT1",
            PackagePath = packagePath,
            ObjectPath = packagePath + "." + UnrealPathUtil.AssetName(packagePath),
            TemplateJson = templateJson,
            OutputRoot = outputRoot,
        };
        WriteGeneratedTextureCookReport(texture, packageBase, externalMips);
        if (externalMips && !includeRequiredBulk)
        {
            File.Delete(packageBase + ".ubulk");
        }
        return texture;
    }

    private static string GeneratedTextureFixturePackageBase(string outputRoot, string packagePath) =>
        Path.Combine(
            outputRoot,
            "Cooked",
            "LEGOBatmanLotDK",
            "Content",
            UnrealPathUtil.NormalizePackagePath(packagePath)["/Game/".Length..]
                .Replace('/', Path.DirectorySeparatorChar));

    private static void WriteGeneratedTextureCookReport(
        GeneratedTextureEntry texture,
        string packageBase,
        bool externalMips)
    {
        var templatePackage = ReadRegressionTextureTemplatePackage(texture.TemplateJson);
        var (uassetBytes, uassetSha256) = GeneratedTextureFixtureIntegrity(packageBase + ".uasset");
        var (uexpBytes, uexpSha256) = GeneratedTextureFixtureIntegrity(packageBase + ".uexp");
        var (ubulkBytes, ubulkSha256) = externalMips
            ? GeneratedTextureFixtureIntegrity(packageBase + ".ubulk")
            : (0L, "");
        File.WriteAllText(
            packageBase + ".texture-cook-report.json",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Status = "created",
                TemplatePackagePath = templatePackage,
                OutputPackagePath = UnrealPathUtil.NormalizePackagePath(texture.PackagePath),
                Width = texture.CookWidth,
                Height = texture.CookHeight,
                PixelFormat = texture.CookPixelFormat,
                MipCount = 1,
                ExternalMipCount = externalMips ? 1 : 0,
                InlineMipCount = externalMips ? 0 : 1,
                RecipeFingerprint = TextureCookService.RecipeFingerprintFor(texture.TemplateJson),
                OutputUassetBytes = uassetBytes,
                OutputUassetSha256 = uassetSha256,
                OutputUexpBytes = uexpBytes,
                OutputUexpSha256 = uexpSha256,
                OutputUbulkBytes = ubulkBytes,
                OutputUbulkSha256 = ubulkSha256,
                EncoderVersion = TextureCookService.CurrentEncoderVersion,
            }));
    }

    private static string ReadRegressionTextureTemplatePackage(string templateJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(templateJson));
        var root = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().First()
            : doc.RootElement;
        return UnrealPathUtil.NormalizePackagePath(root.GetProperty("Package").GetString());
    }

    private static (long Bytes, string Sha256) GeneratedTextureFixtureIntegrity(string path)
    {
        using var stream = File.OpenRead(path);
        return (
            stream.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
    }

    private static void CreateSizedTextureFixture(string path, long length, bool packageFooter = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
        if (packageFooter)
        {
            stream.Seek(-4, SeekOrigin.End);
            stream.Write([0xC1, 0x83, 0x2A, 0x9E]);
        }
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
