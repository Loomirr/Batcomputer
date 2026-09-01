namespace Batcomputer;

/// <summary>Fast, asset-free guards for managed custom-animation staging.</summary>
internal static class AnimationImportRegressionChecks
{
    public static void Run(List<string> failures, TextWriter output)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-animation-import-regression-" + Guid.NewGuid().ToString("N"));
        var previousExtractedRoot = AppSettings.Current.ExtractedContentRoot;
        try
        {
            Directory.CreateDirectory(root);
            var service = new AnimLibraryService(root);
            var firstPrimary = WriteCache(service, "first", "A_Test.uasset", [1, 2, 3]);
            var firstSupport = WriteCache(service, "first", "Rig/Test_SKEL.uasset", [4, 5, 6]);
            var secondPrimary = WriteCache(service, "second", "A_Other.uasset", [7, 8, 9]);
            var secondSupport = WriteCache(service, "second", "Rig/Test_SKEL.uasset", [4, 5, 7]);

            var first = HealthyEntry(
                "First",
                "/Game/Mods/AnimRegression/A_Test",
                firstPrimary,
                "/Game/Mods/AnimRegression/Rig/Test_SKEL",
                firstSupport);
            var second = HealthyEntry(
                "Second",
                "/Game/Mods/AnimRegression/A_Other",
                secondPrimary,
                "/Game/Mods/AnimRegression/Rig/Test_SKEL",
                secondSupport);

            Check(
                service.ValidateStagingSet([first, second]).Count == 1,
                "custom animations with different bytes for one shared rig package block before staging",
                failures,
                output);

            File.WriteAllBytes(
                Path.Combine(service.LibraryRoot, secondSupport.Replace('/', Path.DirectorySeparatorChar)),
                [4, 5, 6]);
            Check(
                service.ValidateStagingSet([first, second]).Count == 0,
                "custom animations may share one rig package only when its cooked bytes are identical",
                failures,
                output);

            var stage = Path.Combine(root, "Stage", "Content");
            var staged = service.StageInto(first, stage);
            var exactPaths = staged == 2 &&
                             File.Exists(Path.Combine(stage, "Mods", "AnimRegression", "A_Test.uasset")) &&
                             File.Exists(Path.Combine(stage, "Mods", "AnimRegression", "Rig", "Test_SKEL.uasset"));
            Check(
                exactPaths,
                "managed animation primary and support packages stage at their exact /Game paths",
                failures,
                output);

            File.WriteAllBytes(
                Path.Combine(service.LibraryRoot, firstPrimary.Replace('/', Path.DirectorySeparatorChar)),
                [9, 9, 9]);
            var collisionBlocked = false;
            try
            {
                service.StageInto(first, stage);
            }
            catch (InvalidOperationException ex)
            {
                collisionBlocked = ex.Message.Contains("collision", StringComparison.OrdinalIgnoreCase);
            }
            Check(
                collisionBlocked,
                "animation staging blocks a different package already present at the destination path",
                failures,
                output);

            var extracted = Path.Combine(root, "Extracted", "LEGOBatmanLotDK", "Content");
            var baseAsset = Path.Combine(extracted, "Animation", "LEGOfig", "Batman", "Movement", "A_Idle_Batman.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(baseAsset)!);
            File.WriteAllBytes(baseAsset, [1]);
            AppSettings.Current.ExtractedContentRoot = extracted;
            var baseCollision = HealthyEntry(
                "Base collision",
                "/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman",
                firstPrimary,
                "/Game/Mods/AnimRegression/Rig/Test_SKEL",
                firstSupport);
            Check(
                service.StageInto(baseCollision, Path.Combine(root, "BaseCollisionStage")) == 0 &&
                !baseCollision.IsAvailable &&
                baseCollision.HealthIssues.Any(issue => issue.Contains("base-game", StringComparison.OrdinalIgnoreCase)),
                "managed custom animations cannot overwrite an extracted base-game package path",
                failures,
                output);

            AppSettings.Current.ExtractedContentRoot = previousExtractedRoot;
            var legacy = new AnimLibraryEntry
            {
                Id = "legacy",
                Name = "Unsafe legacy",
                SourceMode = "preserve-path",
                PackagePath = "/Game/Mods/AnimRegression/A_Legacy",
                AssetClass = "AnimSequence",
                Skeleton = "/Game/Animation/Custom/Custom_SKEL",
                Inspected = true,
                CachedFiles = [firstPrimary],
            };
            var legacyLibrary = new AnimLibrary { SchemaVersion = 1, Entries = [legacy] };
            Check(
                service.ReferencedShippable(legacyLibrary, [legacy.PackagePath]).Count == 0 &&
                !legacy.IsAvailable,
                "legacy single-package custom-skeleton animation caches are quarantined until re-imported",
                failures,
                output);

            Check(
                MainForm.TryPackageIdFromIoChunkId("5254228ea9a6b2f500000001", out var packageId) &&
                packageId == 17704396332311139410UL,
                "animation manifests decode IoStore package identities before checking installed game and DLC collisions",
                failures,
                output);

            RunContainerSelectionChecks(root, failures, output);
            RunMontageLibraryChecks(service, failures, output);
            RunExplorerSnapshotChecks(failures, output);
            RunReplacementCatalogChecks(root, service, failures, output);
        }
        catch (Exception ex)
        {
            Check(false, $"custom animation regression fixture completed ({ex.Message})", failures, output);
        }
        finally
        {
            AppSettings.Current.ExtractedContentRoot = previousExtractedRoot;
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort test cleanup */ }
        }
    }

    private static void RunMontageLibraryChecks(
        AnimLibraryService service,
        List<string> failures,
        TextWriter output)
    {
        var montagePrimary = WriteCache(service, "montage", "AM_Test.uasset", [31, 32, 33]);
        var montageSupport = WriteCache(service, "montage", "Rig/Montage_SKEL.uasset", [34, 35, 36]);
        var montage = HealthyEntry(
            "Montage",
            "/Game/Mods/AnimRegression/AM_Test",
            montagePrimary,
            "/Game/Mods/AnimRegression/Rig/Montage_SKEL",
            montageSupport,
            "/Script/Engine.AnimMontage");
        var montageLibrary = new AnimLibrary { Entries = [montage] };
        Check(
            service.ReferencedShippable(montageLibrary, [montage.PackagePath]).Count == 1 &&
            montage.IsAvailable &&
            !montage.HealthStatus.Equals("quarantined", StringComparison.OrdinalIgnoreCase),
            "managed animation health accepts an exact cooked AnimMontage class",
            failures,
            output);

        var lookalikePrimary = WriteCache(service, "lookalike", "AM_Lookalike.uasset", [41, 42, 43]);
        var lookalikeSupport = WriteCache(service, "lookalike", "Rig/Lookalike_SKEL.uasset", [44, 45, 46]);
        var lookalike = HealthyEntry(
            "Lookalike",
            "/Game/Mods/AnimRegression/AM_Lookalike",
            lookalikePrimary,
            "/Game/Mods/AnimRegression/Rig/Lookalike_SKEL",
            lookalikeSupport,
            "AnimMontagePreview");
        var lookalikeLibrary = new AnimLibrary { Entries = [lookalike] };
        Check(
            service.ReferencedShippable(lookalikeLibrary, [lookalike.PackagePath]).Count == 0 &&
            !lookalike.IsAvailable &&
            lookalike.HealthIssues.Any(issue =>
                issue.Contains("AnimSequence or AnimMontage", StringComparison.OrdinalIgnoreCase)),
            "managed animation health rejects class-name lookalikes instead of substring-matching them",
            failures,
            output);

        const string montagePath = "/Game/Mods/AnimRegression/AM_Banana";
        const string clipPath = "/Game/Mods/AnimRegression/A_Banana_Throw";
        const string siblingClipPath = "/Game/Mods/AnimRegression/A_Unrelated";
        const string siblingMontagePath = "/Game/Mods/AnimRegression/AM_Unrelated";
        const string skeletonPath = "/Game/Mods/AnimRegression/Rig/Banana_SKEL";
        const string meshPath = "/Game/Mods/AnimRegression/Rig/Banana_SK";
        const string physicsPath = "/Game/Mods/AnimRegression/Rig/Banana_PhysicsAsset";
        const string gameCurvePath = "/Game/Animation/Shared/Curve_Game";
        var graph = new List<AnimationImportSupportNode>
        {
            new(montagePath, "AnimMontage", [clipPath]),
            new(clipPath, "AnimSequence", [skeletonPath, gameCurvePath]),
            new(siblingClipPath, "AnimSequence", [skeletonPath]),
            new(siblingMontagePath, "AnimMontage", [clipPath]),
            new(skeletonPath, "Skeleton", []),
            new(meshPath, "SkeletalMesh", [skeletonPath, physicsPath]),
            new(physicsPath, "PhysicsAsset", []),
            new(gameCurvePath, "CurveFloat", [], IsProvidedByGame: true),
        };
        var montageClosure = AnimLibraryService.SelectSupportPackagePaths(montagePath, graph);
        Check(
            montageClosure.SetEquals([clipPath, skeletonPath, meshPath, physicsPath]) &&
            !montageClosure.Contains(siblingClipPath) &&
            !montageClosure.Contains(siblingMontagePath) &&
            !montageClosure.Contains(gameCurvePath),
            "montage support keeps its directed sequence and rig closure without unrelated animation siblings",
            failures,
            output);

        var sequenceClosure = AnimLibraryService.SelectSupportPackagePaths(clipPath, graph);
        Check(
            sequenceClosure.SetEquals([skeletonPath, meshPath, physicsPath]) &&
            !sequenceClosure.Contains(montagePath) &&
            !sequenceClosure.Contains(siblingClipPath) &&
            !sequenceClosure.Contains(siblingMontagePath),
            "sequence support reverse-walk excludes montage and sequence primaries sharing its rig",
            failures,
            output);
    }

    private static void RunContainerSelectionChecks(
        string root,
        List<string> failures,
        TextWriter output)
    {
        var fixtureRoot = Path.Combine(root, "ContainerSelection");
        Directory.CreateDirectory(fixtureRoot);

        var completeBase = Path.Combine(fixtureRoot, "CompleteAnimation");
        File.WriteAllBytes(completeBase + ".utoc", [1]);
        File.WriteAllBytes(completeBase + ".ucas", [2]);
        File.WriteAllBytes(completeBase + ".pak", [3]);

        var resolvedSelections = new List<AnimationContainerSelectionService.Selection>();
        var completeErrors = new List<string>();
        foreach (var extension in new[] { ".utoc", ".ucas", ".pak" })
        {
            if (AnimationContainerSelectionService.TryResolve(
                    completeBase + extension,
                    out var selection,
                    out var error) &&
                selection is not null)
            {
                resolvedSelections.Add(selection);
            }
            else
            {
                completeErrors.Add(error);
            }
        }

        Check(
            resolvedSelections.Count == 3 &&
            completeErrors.Count == 0 &&
            resolvedSelections.All(selection =>
                selection.BasePath.Equals(completeBase, StringComparison.OrdinalIgnoreCase) &&
                selection.UtocPath.Equals(completeBase + ".utoc", StringComparison.OrdinalIgnoreCase) &&
                selection.UcasPath.Equals(completeBase + ".ucas", StringComparison.OrdinalIgnoreCase) &&
                selection.PakPath?.Equals(completeBase + ".pak", StringComparison.OrdinalIgnoreCase) == true &&
                selection.Files.Count == 3 &&
                selection.DisplayName == "CompleteAnimation"),
            "custom animation import accepts any .utoc, .ucas, or .pak sibling and resolves one complete trio",
            failures,
            output);

        var optionalBase = Path.Combine(fixtureRoot, "PakOptional");
        File.WriteAllBytes(optionalBase + ".utoc", [4]);
        File.WriteAllBytes(optionalBase + ".ucas", [5]);
        var optionalResolved = AnimationContainerSelectionService.TryResolve(
            optionalBase + ".ucas",
            out var optionalSelection,
            out _);
        Check(
            optionalResolved &&
            optionalSelection is not null &&
            optionalSelection.PakPath is null &&
            optionalSelection.Files.SequenceEqual(
                [optionalBase + ".utoc", optionalBase + ".ucas"],
                StringComparer.OrdinalIgnoreCase),
            "custom animation import treats the .pak sibling as optional when the required IoStore pair is complete",
            failures,
            output);

        var incompleteBase = Path.Combine(fixtureRoot, "MissingUtoc");
        File.WriteAllBytes(incompleteBase + ".ucas", [6]);
        var incompleteResolved = AnimationContainerSelectionService.TryResolve(
            incompleteBase + ".ucas",
            out var incompleteSelection,
            out var incompleteError);
        Check(
            !incompleteResolved &&
            incompleteSelection is null &&
            incompleteError.Contains("MissingUtoc.utoc", StringComparison.OrdinalIgnoreCase),
            "custom animation import rejects a selected container when a required sibling is missing",
            failures,
            output);

        var unsupportedPath = Path.Combine(fixtureRoot, "NotAContainer.zip");
        File.WriteAllBytes(unsupportedPath, [7]);
        var unsupportedResolved = AnimationContainerSelectionService.TryResolve(
            unsupportedPath,
            out var unsupportedSelection,
            out var unsupportedError);
        var absentResolved = AnimationContainerSelectionService.TryResolve(
            Path.Combine(fixtureRoot, "Absent.utoc"),
            out var absentSelection,
            out var absentError);
        var emptyResolved = AnimationContainerSelectionService.TryResolve(
            " ",
            out var emptySelection,
            out var emptyError);
        Check(
            !unsupportedResolved && unsupportedSelection is null &&
            unsupportedError.Contains(".utoc", StringComparison.OrdinalIgnoreCase) &&
            !absentResolved && absentSelection is null &&
            absentError.Contains("no longer exists", StringComparison.OrdinalIgnoreCase) &&
            !emptyResolved && emptySelection is null &&
            emptyError.Contains("Choose", StringComparison.OrdinalIgnoreCase),
            "custom animation import rejects unsupported, missing, and empty selections before retoc runs",
            failures,
            output);
    }

    private static void RunExplorerSnapshotChecks(List<string> failures, TextWriter output)
    {
        var project = new NativeSuitProject
        {
            SlotId = "animation_explorer_fixture",
            DisplayName = "Animation Explorer Fixture",
            AnimationOverrides =
            [
                new AnimSetOverride
                {
                    Category = "Glide",
                    Kind = "Layer",
                    DonorSet = "LAS_Traversal_Nightwing",
                    ReplacementSet = "LAS_Traversal_Batman",
                    ReplacementPackage = "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Batman",
                },
            ],
            LocomotionOverrides =
            [
                new AnimSequenceOverride
                {
                    DonorSequence = "A_Idle_Nightwing",
                    DonorSequencePackage = "/Game/Animation/LEGOfig/Nightwing/Movement/A_Idle_Nightwing",
                    ReplacementSequence = "A_Idle_Flash",
                    ReplacementPackage = "/Game/Mods/Animations/A_Idle_Flash",
                },
            ],
        };

        var ready = new AnimLibraryEntry
        {
            Id = "ready-flash",
            Name = "Flash loop",
            SourceMode = "preserve-path",
            PackagePath = "/Game/Mods/Animations/A_Flash_Loop",
            AssetClass = "AnimSequence",
            Skeleton = "/Game/Mods/Animations/Rig/LEGOfig_SKEL",
            Inspected = true,
            HealthStatus = "healthy",
            IsAvailable = true,
            CachedFiles = ["Cache/ready-flash/A_Flash_Loop.uasset"],
            Dependencies = ["/Game/Animation/Shared/Curve_Flash"],
            SupportPackages =
            [
                new AnimLibraryCachedPackage
                {
                    PackagePath = "/Game/Mods/Animations/Rig/LEGOfig_SKEL",
                    AssetClass = "Skeleton",
                    Inspected = true,
                    Dependencies = ["/Game/Mods/Animations/Rig/LEGOfig_PhysicsAsset"],
                },
                new AnimLibraryCachedPackage
                {
                    PackagePath = "/Game/Mods/Animations/Rig/LEGOfig_PhysicsAsset",
                    AssetClass = "PhysicsAsset",
                    Inspected = true,
                },
            ],
        };
        var quarantined = new AnimLibraryEntry
        {
            Id = "broken-sequence",
            Name = "Broken sequence",
            SourceMode = "preserve-path",
            PackagePath = "/Game/Mods/Animations/A_Broken",
            AssetClass = "AnimSequence",
            Inspected = true,
            HealthStatus = "quarantined",
            IsAvailable = false,
            UnresolvedImports = ["/Engine/UnknownPackage"],
            HealthIssues = ["Contains an unresolved UnknownPackage import."],
        };
        var nonSequence = new AnimLibraryEntry
        {
            Id = "montage",
            Name = "Imported montage",
            SourceMode = "preserve-path",
            PackagePath = "/Game/Mods/Animations/AM_Flash",
            AssetClass = "AnimMontage",
            Inspected = true,
            HealthStatus = "healthy",
            IsAvailable = true,
        };
        var external = new AnimLibraryEntry
        {
            Id = "external-sequence",
            Name = "External sequence",
            SourceMode = "external",
            PackagePath = "/Game/AnotherMod/A_External",
            AssetClass = "AnimSequence",
            Inspected = true,
            HealthStatus = "external",
            IsAvailable = true,
        };
        var library = new AnimLibrary { Entries = [quarantined, nonSequence, external, ready] };

        var snapshot = AnimationExplorerSnapshotBuilder.Build(project, library);
        var currentRoot = snapshot.Roots.SingleOrDefault(node => node.Title == "Current suit");
        var importedRoot = snapshot.Roots.SingleOrDefault(node => node.Title == "Imported animations");
        var importedEntries = importedRoot?.Children
            .Where(node => node.Kind == AnimationExplorerNodeKind.ImportedAnimation)
            .ToList() ?? [];
        var readyNode = importedEntries.SingleOrDefault(node => node.EntryId == ready.Id);
        var quarantinedNode = importedEntries.SingleOrDefault(node => node.EntryId == quarantined.Id);
        var nonSequenceNode = importedEntries.SingleOrDefault(node => node.EntryId == nonSequence.Id);
        var externalNode = importedEntries.SingleOrDefault(node => node.EntryId == external.Id);

        Check(
            snapshot.Roots.Count == 2 &&
            snapshot.ImportedCount == 4 &&
            snapshot.HealthyImportedCount == 1 &&
            currentRoot is not null &&
            Flatten(currentRoot).Any(node =>
                node.Kind == AnimationExplorerNodeKind.Override &&
                node.Title == "Glide" &&
                node.Value == "LAS_Traversal_Batman") &&
            Flatten(currentRoot).Any(node =>
                node.Kind == AnimationExplorerNodeKind.Override &&
                node.Title == "Idle" &&
                node.Value == "A_Idle_Flash"),
            "animation explorer snapshot includes current family/set and idle-walk-run sequence overrides",
            failures,
            output);

        Check(
            readyNode is { CanApply: true } &&
            quarantinedNode is { CanApply: false } &&
            nonSequenceNode is { CanApply: false } &&
            externalNode is { CanApply: false } &&
            Flatten(readyNode).Any(node =>
                node.Kind == AnimationExplorerNodeKind.Rig &&
                node.Value == ready.Skeleton) &&
            Flatten(readyNode).Any(node =>
                node.Kind == AnimationExplorerNodeKind.SupportPackage &&
                node.Value.EndsWith("LEGOfig_PhysicsAsset", StringComparison.Ordinal)) &&
            Flatten(readyNode).Any(node =>
                node.Kind == AnimationExplorerNodeKind.Dependency &&
                node.Value == "/Game/Animation/Shared/Curve_Flash") &&
            Flatten(quarantinedNode!).Any(node =>
                node.Kind == AnimationExplorerNodeKind.Warning &&
                node.Value.Contains("UnknownPackage", StringComparison.OrdinalIgnoreCase)),
            "animation explorer exposes rig/support/dependency health and applies only healthy supported animation entries",
            failures,
            output);

        var supportSearch = AnimationExplorerSnapshotBuilder.Build(project, library, "PhysicsAsset");
        Check(
            supportSearch.Roots.Count == 1 &&
            supportSearch.Roots[0].Title == "Imported animations" &&
            supportSearch.Roots[0].Children.Count == 1 &&
            supportSearch.Roots[0].Children[0].EntryId == ready.Id &&
            Flatten(supportSearch.Roots[0]).Any(node =>
                node.Kind == AnimationExplorerNodeKind.SupportPackage &&
                node.Value.EndsWith("LEGOfig_PhysicsAsset", StringComparison.Ordinal)),
            "animation explorer search keeps a matching support package and its imported-entry ancestor path",
            failures,
            output);

        var currentSearch = AnimationExplorerSnapshotBuilder.Build(project, library, "Idle");
        Check(
            currentSearch.Roots.Count == 1 &&
            currentSearch.Roots[0].Title == "Current suit" &&
            Flatten(currentSearch.Roots[0]).Any(node =>
                node.Kind == AnimationExplorerNodeKind.Override &&
                node.Title == "Idle"),
            "animation explorer search can isolate the current suit's sequence overrides",
            failures,
            output);
    }

    private static IEnumerable<AnimationExplorerNode> Flatten(AnimationExplorerNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RunReplacementCatalogChecks(
        string workspaceRoot,
        AnimLibraryService service,
        List<string> failures,
        TextWriter output)
    {
        var sequenceFile = WriteCache(service, "catalog-sequence", "A_Custom.uasset", [21, 22, 23]);
        var sequence = new AnimLibraryEntry
        {
            Id = "catalog-sequence",
            Name = "Custom sequence",
            SourceMode = "preserve-path",
            PackagePath = "/Game/Mods/AnimRegression/A_Custom",
            AssetClass = "AnimSequence",
            Skeleton = "/Game/Characters/LEGOfig/SKEL_LEGOfig",
            Inspected = true,
            CachedFiles = [sequenceFile],
            HealthStatus = "healthy",
            IsAvailable = true,
        };
        var montage = new AnimLibraryEntry
        {
            Id = "catalog-montage",
            Name = "Custom montage",
            SourceMode = "preserve-path",
            PackagePath = "/Game/Mods/AnimRegression/AM_Custom",
            AssetClass = "AnimMontage",
            Inspected = true,
            CachedFiles = ["Cache/catalog-montage/AM_Custom.uasset"],
            HealthStatus = "healthy",
            IsAvailable = true,
        };
        var quarantined = new AnimLibraryEntry
        {
            Id = "catalog-quarantined",
            Name = "Broken sequence",
            SourceMode = "preserve-path",
            PackagePath = "/Game/Mods/AnimRegression/A_Broken",
            AssetClass = "AnimSequence",
            Inspected = true,
            HealthStatus = "quarantined",
            IsAvailable = false,
            HealthIssues = ["Primary cooked package is missing."],
        };
        var external = new AnimLibraryEntry
        {
            Id = "catalog-external",
            Name = "External sequence",
            SourceMode = "external",
            PackagePath = "/Game/AnotherMod/A_External",
            AssetClass = "AnimSequence",
            Inspected = true,
            HealthStatus = "external",
            IsAvailable = true,
        };
        var library = new AnimLibrary { Entries = [sequence, montage, quarantined, external] };
        var target = new CharacterAnimationTargetSnapshot(
            "catalog-target",
            CharacterAnimationReferenceKind.LocomotionSequence,
            "/Game/Animation/LEGOfig/Batman/Movement/ABP_Core_Batman",
            "AnimBlueprintGeneratedClass",
            "/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman",
            "/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman",
            "A_Idle_Batman",
            "A_Idle_Batman",
            "AnimSequence",
            "AnimSequence",
            -1,
            -1,
            -1,
            0,
            false,
            "");

        var imported = AnimationReplacementCatalogService.Build(target, library)
            .Where(candidate => candidate.Source.Equals("Imported", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(candidate => candidate.LibraryEntry?.Id ?? "", StringComparer.OrdinalIgnoreCase);
        Check(
            imported.Count == 4 &&
            imported[sequence.Id].CanSelect &&
            !imported[montage.Id].CanSelect &&
            imported[montage.Id].IncompatibilityReason.Contains("requires AnimSequence", StringComparison.OrdinalIgnoreCase) &&
            !imported[quarantined.Id].CanSelect &&
            imported[quarantined.Id].IncompatibilityReason.Contains("missing", StringComparison.OrdinalIgnoreCase) &&
            !imported[external.Id].CanSelect &&
            imported[external.Id].IncompatibilityReason.Contains("not owned", StringComparison.OrdinalIgnoreCase),
            "replacement picker keeps every tool-wide import visible while disabling incompatible or unavailable entries",
            failures,
            output);

        service.Save(library);
        var anotherSuitView = new AnimLibraryService(workspaceRoot).Load();
        Check(
            anotherSuitView.Entries.Count == library.Entries.Count &&
            anotherSuitView.Entries.Any(entry => entry.Id == sequence.Id),
            "animation imports persist in one workspace library shared by every suit",
            failures,
            output);
    }

    private static string WriteCache(
        AnimLibraryService service,
        string entryId,
        string relativePath,
        byte[] bytes)
    {
        var relative = Path.Combine("Cache", entryId, relativePath).Replace('\\', '/');
        var full = Path.Combine(service.LibraryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return relative;
    }

    private static AnimLibraryEntry HealthyEntry(
        string name,
        string packagePath,
        string primaryFile,
        string supportPath,
        string supportFile,
        string assetClass = "AnimSequence") => new()
    {
        Id = name.Replace(" ", "", StringComparison.Ordinal),
        Name = name,
        SourceMode = "preserve-path",
        PackagePath = packagePath,
        AssetClass = assetClass,
        Inspected = true,
        CachedFiles = [primaryFile],
        HealthStatus = "healthy",
        IsAvailable = true,
        SupportPackages =
        [
            new AnimLibraryCachedPackage
            {
                PackagePath = supportPath,
                AssetClass = "Skeleton",
                Inspected = true,
                CachedFiles = [supportFile],
            },
        ],
    };

    private static void Check(bool condition, string name, List<string> failures, TextWriter output)
    {
        if (condition)
        {
            output.WriteLine($"PASS: {name}");
            return;
        }
        failures.Add(name);
        output.WriteLine($"FAIL: {name}");
    }
}
