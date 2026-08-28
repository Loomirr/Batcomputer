using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using PropertyData = UAssetAPI.PropertyTypes.Objects.PropertyData;

namespace Batcomputer;

public sealed class PartGraftService
{
    internal const string CanonicalStaticCollisionProfile = "NoCollision";
    internal const string CanonicalCutsceneParentOwnerClass = "BP_CutsceneMinifigCharacter_C";
    internal static readonly IReadOnlyList<string> CanonicalStaticCollisionChannels =
    [
        "WorldStatic",
        "WorldDynamic",
        "Pawn",
        "Visibility",
        "Camera",
        "PhysicsBody",
        "Vehicle",
        "Destructible",
        "EngineTraceChannel2",
        "EngineTraceChannel3",
        "EngineTraceChannel4",
        "EngineTraceChannel5",
        "EngineTraceChannel6",
        "GameTraceChannel18",
    ];

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ProjectRoot { get; }
    public string GuiOutputRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitGuiProjects");

    public PartGraftService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public PartGraftBatchResult CreateTorso2GraftedStage(NativeSuitProject project)
    {
        var partIndexService = new PartIndexService(ProjectRoot);
        var partIndex = partIndexService.LoadPartIndex();
        if (partIndex is null)
        {
            partIndex = partIndexService.BuildPartIndex();
        }

        var playablePart = FindAbsoluteTorso2(partIndex, "playable");
        var cutscenePart = FindAbsoluteTorso2(partIndex, "cutscene");

        return CreateSelectedPartGraftedStage(
            project,
            playablePart,
            cutscenePart,
            targetSlot: "Torso2",
            cloneSlot: "Face",
            attachSocket: "Chest_Socket",
            preferDonorComponentShell: false,
            restoreExistingFieldRecipe: false,
            stageName: "GraftedTorso2Stage",
            reportFileName: "torso2-graft-report.json");
    }

    public PartGraftBatchResult CreateSelectedPartGraftedStage(
        NativeSuitProject project,
        NativeSuitPartRecord? playablePart,
        NativeSuitPartRecord? cutscenePart,
        string targetSlot,
        string cloneSlot,
        string attachSocket,
        bool preferDonorComponentShell = false,
        bool restoreExistingFieldRecipe = false)
    {
        return CreateSelectedPartGraftedStage(
            project,
            playablePart,
            cutscenePart,
            targetSlot,
            cloneSlot,
            attachSocket,
            preferDonorComponentShell,
            restoreExistingFieldRecipe,
            stageName: "GraftedPartStage",
            reportFileName: "selected-part-graft-report.json");
    }

    private PartGraftBatchResult CreateSelectedPartGraftedStage(
        NativeSuitProject project,
        NativeSuitPartRecord? playablePart,
        NativeSuitPartRecord? cutscenePart,
        string targetSlot,
        string cloneSlot,
        string attachSocket,
        bool preferDonorComponentShell,
        bool restoreExistingFieldRecipe,
        string stageName,
        string reportFileName)
    {
        // Catalog entries contain requested mesh/material paths but may not come from
        // a character BP. Resolve each role against the extracted BP index so every
        // graft has a real native component-shell recipe.
        playablePart = ResolveTemplateRecipe(playablePart, "playable");
        cutscenePart = ResolveTemplateRecipe(cutscenePart, "cutscene");

        var graftedContentRoot = Path.Combine(GuiOutputRoot, project.SlotId, stageName, "LEGOBatmanLotDK", "Content");
        string patchedContentRoot;

        if (Directory.Exists(graftedContentRoot))
        {
            // Additive builder behavior: keep prior grafts/removals/material edits
            // and append the newly dragged part into the current packageable stage.
            patchedContentRoot = graftedContentRoot;
        }
        else
        {
            patchedContentRoot =
                ResolveBestExistingContentRoot(project.SlotId, graftedContentRoot)
                ?? new UAssetPatchService(ProjectRoot).CreateNameMapPatchedStage(project).PatchedContentRoot;
            CopyDirectory(patchedContentRoot, graftedContentRoot);
        }

        var partIndexPath = new PartIndexService(ProjectRoot).PartIndexPath;
        var batch = new PartGraftBatchResult
        {
            Status = "created",
            CreatedUtc = DateTime.UtcNow,
            PatchedContentRoot = patchedContentRoot,
            GraftedContentRoot = graftedContentRoot,
            PartIndexPath = partIndexPath
        };

        var mappingsPath = FindDefaultMappingsPath();
        var packageSnapshots = CapturePackageSnapshots(
            graftedContentRoot,
            project,
            includePlayable: playablePart is not null,
            includeCutscene: cutscenePart is not null);

        // If a component with this exact slot already exists (native, like Cape, or a
        // prior graft), REPLACE it - repoint its mesh/anim/materials - instead of
        // adding a numbered duplicate (Head_2 / Cape_2). Swapping a skeletal mesh
        // also swaps all its built-in LODs at once. Only genuinely-new slots are added.
        var componentService = new ComponentRemoveService(ProjectRoot);
        var playableSlotExists = playablePart is not null && componentService
            .ListScsComponentNames(project.SlotId, project.TargetPackages.Playable, targetSlot)
            .Any(name => name.Equals(targetSlot, StringComparison.OrdinalIgnoreCase));
        var cutsceneSlotExists = cutscenePart is not null && componentService
            .ListScsComponentNames(project.SlotId, project.TargetPackages.Cutscene, targetSlot)
            .Any(name => name.Equals(targetSlot, StringComparison.OrdinalIgnoreCase));
        // Repoint (replace-in-place) is only valid when the existing slot component is the
        // SAME mesh kind as the incoming part (skeletal cape → skeletal wingsuit). A STATIC
        // hair/hat landing on a slot whose existing component is SKELETAL (e.g. Batman's
        // skeletal "Head" cowl) must NOT replace it - that hits the banned cross-kind
        // conversion. Fall through to the ADD path (new static component on a peg) instead.
        var playableCanRepoint = !playableSlotExists || playablePart is null ||
            ExistingSlotCanBeRepointedLive(
                graftedContentRoot,
                project.TargetPackages.Playable,
                targetSlot,
                playablePart,
                mappingsPath);
        var cutsceneCanRepoint = !cutsceneSlotExists || cutscenePart is null ||
            ExistingSlotCanBeRepointedLive(
                graftedContentRoot,
                project.TargetPackages.Cutscene,
                targetSlot,
                cutscenePart,
                mappingsPath);

        if (!CanRestoreExistingFieldRecipe(
                restoreExistingFieldRecipe,
                playablePart is not null,
                playableSlotExists,
                playableCanRepoint,
                cutscenePart is not null,
                cutsceneSlotExists,
                cutsceneCanRepoint))
        {
            var rejectedRoles = new List<string>();
            if (playablePart is not null && (!playableSlotExists || !playableCanRepoint))
            {
                rejectedRoles.Add("playable");
            }
            if (cutscenePart is not null && (!cutsceneSlotExists || !cutsceneCanRepoint))
            {
                rejectedRoles.Add("cutscene");
            }
            throw new InvalidOperationException(
                $"The certified paired-cape visual overlay cannot restore existing field '{targetSlot}' for {string.Join(" and ", rejectedRoles)}: " +
                "the live authored field is missing or has an incompatible component class. Batcomputer refused to append a replacement reflected field.");
        }

        // A retry can arrive after the playable write succeeded but the cutscene package was
        // locked. In that state only the playable owns the requested slot. Repoint the role that
        // already exists and ADD the missing role under the same name; deciding from playable
        // alone would incorrectly try to repoint a cutscene component that is not there.
        if ((playableSlotExists || cutsceneSlotExists) && playableCanRepoint && cutsceneCanRepoint)
        {
            if (playableSlotExists || cutsceneSlotExists)
            {
                var repoint = RepointComponentsToParts(
                    project,
                    targetSlot,
                    playablePart,
                    cutscenePart,
                    applyToPlayable: playableSlotExists,
                    applyToCutscene: cutsceneSlotExists,
                    restoreExistingFieldRecipe: restoreExistingFieldRecipe);
                batch.PackageResults.AddRange(repoint.PackageResults);
            }

            if (playablePart is not null && !playableSlotExists)
            {
                batch.PackageResults.Add(ApplyPartGraftToPackage(
                    "playable",
                    graftedContentRoot,
                    project.TargetPackages.Playable,
                    playablePart,
                    targetSlot,
                    cloneSlot,
                    attachSocket,
                    preferDonorComponentShell,
                    mappingsPath));
            }
            if (cutscenePart is not null && !cutsceneSlotExists)
            {
                batch.PackageResults.Add(ApplyPartGraftToPackage(
                    "cutscene",
                    graftedContentRoot,
                    project.TargetPackages.Cutscene,
                    cutscenePart,
                    targetSlot,
                    cloneSlot,
                    attachSocket,
                    preferDonorComponentShell,
                    mappingsPath));
            }

            RollBackTargetPackagesAfterTransientFailure(batch, packageSnapshots);
            SetBatchStatus(batch);
            var repointReport = Path.Combine(GuiOutputRoot, project.SlotId, reportFileName);
            batch.ReportPath = TryWriteBatchReport(repointReport, batch);
            return batch;
        }

        // Slot names that a saved remove-component rule is meant to hide are RESERVED for that base
        // component. Without this the stage rebuild eats its own removal: the rebuild applies the
        // removal first (freeing e.g. "Head"), the replayed hair graft then claims the now-free
        // "Head", and the "don't strip what we just grafted" guard deletes the cowl's removal - so
        // the next rebuild brings the cowl back with the hair on top of it.
        var reservedSlots = (project.Requirements ?? new List<NativeSuitRequirement>())
            .Where(r => r.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase))
            .Select(r =>
            {
                var t = (r.TargetComponent ?? string.Empty).Trim();
                var colon = t.LastIndexOf(':');
                return colon > 0 ? t[..colon] : t;
            })
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var actualTargetSlot = MakeUniqueTargetSlotAcrossPackages(
            graftedContentRoot,
            new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene },
            targetSlot,
            mappingsPath,
            reservedSlots);

        if (playablePart is not null)
        {
            batch.PackageResults.Add(ApplyPartGraftToPackage(
                role: "playable",
                contentRoot: graftedContentRoot,
                targetPackagePath: project.TargetPackages.Playable,
                donorPart: playablePart,
                targetSlot: actualTargetSlot,
                cloneSlot: cloneSlot,
                attachSocket: attachSocket,
                preferDonorComponentShell: preferDonorComponentShell,
                mappingsPath: mappingsPath));
        }

        if (cutscenePart is not null)
        {
            batch.PackageResults.Add(ApplyPartGraftToPackage(
                role: "cutscene",
                contentRoot: graftedContentRoot,
                targetPackagePath: project.TargetPackages.Cutscene,
                donorPart: cutscenePart,
                targetSlot: actualTargetSlot,
                cloneSlot: cloneSlot,
                attachSocket: attachSocket,
                preferDonorComponentShell: preferDonorComponentShell,
                mappingsPath: mappingsPath));
        }

        RollBackTargetPackagesAfterTransientFailure(batch, packageSnapshots);
        SetBatchStatus(batch);

        var reportPath = Path.Combine(GuiOutputRoot, project.SlotId, reportFileName);
        batch.ReportPath = TryWriteBatchReport(reportPath, batch);
        return batch;
    }

    private static bool CanRestoreExistingFieldRecipe(
        bool restoreExistingFieldRecipe,
        bool playableRequested,
        bool playableExists,
        bool playableCanRepoint,
        bool cutsceneRequested,
        bool cutsceneExists,
        bool cutsceneCanRepoint) =>
        !restoreExistingFieldRecipe ||
        ((!playableRequested || playableExists && playableCanRepoint) &&
         (!cutsceneRequested || cutsceneExists && cutsceneCanRepoint));

    internal static bool CanRestoreExistingFieldRecipeForTest(
        bool playableRequested,
        bool playableExists,
        bool playableCanRepoint,
        bool cutsceneRequested,
        bool cutsceneExists,
        bool cutsceneCanRepoint) =>
        CanRestoreExistingFieldRecipe(
            restoreExistingFieldRecipe: true,
            playableRequested,
            playableExists,
            playableCanRepoint,
            cutsceneRequested,
            cutsceneExists,
            cutsceneCanRepoint);

    private static string TryWriteBatchReport(string reportPath, PartGraftBatchResult batch)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(batch, ReportJsonOptions));
            return reportPath;
        }
        catch (Exception ex)
        {
            // The report is diagnostic only. Package writes have already succeeded or been
            // rolled back; failing the whole operation here would retry a completed graft and
            // could allocate a second suffixed component name.
            Console.Error.WriteLine($"Part graft report could not be written: {ex.Message}");
            return "";
        }
    }

    private static void SetBatchStatus(PartGraftBatchResult batch)
    {
        var playableResult = batch.PackageResults.FirstOrDefault(result =>
            result.Role.Equals("playable", StringComparison.OrdinalIgnoreCase));
        if (batch.PackageResults.Count == 0)
        {
            batch.Status = "no-parts-selected";
        }
        else if (batch.PackageResults.All(result => result.Success))
        {
            batch.Status = "created";
        }
        else if (playableResult?.Success == true)
        {
            batch.Status = "gameplay-test-ready-cutscene-pending";
        }
        else
        {
            batch.Status = "partial-failure";
        }
    }

    internal static bool ShouldRollbackTransientBatchForTest(
        IEnumerable<PartGraftPackageResult> packageResults)
    {
        return packageResults.Any(result => result.TransientFileLock);
    }

    internal static IReadOnlyList<string> GetTransientBatchRollbackRolesForTest(
        IEnumerable<PartGraftPackageResult> packageResults,
        IEnumerable<string> targetedRoles)
    {
        if (!ShouldRollbackTransientBatchForTest(packageResults))
        {
            return Array.Empty<string>();
        }

        return targetedRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class PackageSnapshot
    {
        private static readonly string[] PackageExtensions =
            [".uasset", ".uexp", ".ubulk", ".uptnl", ".m.ubulk"];

        public required string Role { get; init; }
        public required Dictionary<string, byte[]?> Files { get; init; }

        public void Restore()
        {
            foreach (var (path, bytes) in Files)
            {
                if (bytes is null)
                {
                    File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                }
            }
        }

        public static PackageSnapshot Capture(string role, string packageBasePath)
        {
            return new PackageSnapshot
            {
                Role = role,
                Files = PackageExtensions.ToDictionary(
                    extension => packageBasePath + extension,
                    extension =>
                    {
                        var path = packageBasePath + extension;
                        return File.Exists(path) ? File.ReadAllBytes(path) : null;
                    },
                    StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    private static IReadOnlyDictionary<string, PackageSnapshot> CapturePackageSnapshots(
        string contentRoot,
        NativeSuitProject project,
        bool includePlayable,
        bool includeCutscene)
    {
        var snapshots = new Dictionary<string, PackageSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (includePlayable && !string.IsNullOrWhiteSpace(project.TargetPackages.Playable))
        {
            snapshots["playable"] = PackageSnapshot.Capture(
                "playable",
                PackagePathToBasePath(contentRoot, project.TargetPackages.Playable));
        }
        if (includeCutscene && !string.IsNullOrWhiteSpace(project.TargetPackages.Cutscene))
        {
            snapshots["cutscene"] = PackageSnapshot.Capture(
                "cutscene",
                PackagePathToBasePath(contentRoot, project.TargetPackages.Cutscene));
        }
        return snapshots;
    }

    private static void RollBackTargetPackagesAfterTransientFailure(
        PartGraftBatchResult batch,
        IReadOnlyDictionary<string, PackageSnapshot> packageSnapshots)
    {
        var rollbackRoles = GetTransientBatchRollbackRolesForTest(
            batch.PackageResults,
            packageSnapshots.Keys);
        if (rollbackRoles.Count == 0)
        {
            return;
        }

        var restoredRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restoreFailures = new List<(string Role, Exception Error)>();
        foreach (var role in rollbackRoles)
        {
            if (!packageSnapshots.TryGetValue(role, out var snapshot))
            {
                restoreFailures.Add((role, new InvalidOperationException(
                    $"Could not roll back the partial {role} graft because its package snapshot is missing.")));
                continue;
            }

            const int restoreAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    snapshot.Restore();
                    restoredRoles.Add(role);
                    break;
                }
                catch (Exception ex) when (attempt < restoreAttempts && FileLockUtil.IsTransient(ex))
                {
                    Thread.Sleep(180 * attempt);
                }
                catch (Exception ex)
                {
                    // Keep restoring the other targets before surfacing the failure. A lock on
                    // one package must never prevent its successfully-written sibling from being
                    // returned to the same pre-graft snapshot.
                    restoreFailures.Add((role, ex));
                    break;
                }
            }
        }

        foreach (var result in batch.PackageResults.Where(result => restoredRoles.Contains(result.Role)))
        {
            if (result.Success)
            {
                result.Success = false;
                result.Error =
                    "This package write succeeded, but the whole graft batch was rolled back because a required role was temporarily locked.";
            }
            else if (result.TransientFileLock)
            {
                result.Error = string.IsNullOrWhiteSpace(result.Error)
                    ? "The transiently locked package was restored to its pre-graft snapshot."
                    : result.Error + Environment.NewLine +
                      "Any partial package write was restored to its pre-graft snapshot.";
            }
        }

        if (restoreFailures.Count == 0)
        {
            return;
        }

        var failedRoles = string.Join(", ", restoreFailures.Select(failure => failure.Role));
        var transientRestoreFailure = restoreFailures.FirstOrDefault(failure =>
            FileLockUtil.IsTransient(failure.Error));
        if (transientRestoreFailure.Error is not null)
        {
            throw new TransientFileLockException(
                $"The {failedRoles} package snapshot(s) could not be restored after a transient graft failure.",
                transientRestoreFailure.Error);
        }

        throw new InvalidOperationException(
            $"The {failedRoles} package snapshot(s) could not be restored after a partial graft.",
            restoreFailures[0].Error);
    }

    /// <summary>
    /// Repoints an EXISTING component (by variable name, e.g. "Cape") to a part's
    /// mesh/anim/materials/tags - no new component. Used to turn a natively-caped
    /// base's glide visual into a wingsuit: the character's proven glide-visibility
    /// wiring (GE_ShowGlider → Visible.Glider ABPTag → cape ABP) stays intact, we
    /// only swap what the cape shows. Edits the current staged playable + cutscene.
    /// </summary>
    /// <summary>
    /// Returns true when the staged playable's existing component for <paramref name="slot"/>
    /// is the SAME mesh kind (static vs skeletal) as the incoming part. Used to decide
    /// whether a drop should repoint-in-place (same kind) or add a new component (cross
    /// kind, e.g. static hair onto a base whose "Head" is a skeletal cowl). Fails open
    /// (returns true) only when the component can't be inspected, preserving old behaviour.
    /// </summary>
    private static bool ExistingSlotCanBeRepointedLive(string contentRoot, string packagePath, string slot, NativeSuitPartRecord part, string? mappingsPath)
    {
        try
        {
            var targetBase = PackagePathToBasePath(contentRoot, packagePath);
            var inputUasset = targetBase + ".uasset";
            if (!File.Exists(inputUasset))
            {
                return true;
            }
            var mappings = string.IsNullOrWhiteSpace(mappingsPath) ? null : MappingsCache.Load(mappingsPath);
            var asset = new UAsset(inputUasset, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            var exportName = slot + "_GEN_VARIABLE";
            var comp = asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.ObjectName.ToString().Equals(exportName, StringComparison.OrdinalIgnoreCase));
            if (comp is null)
            {
                return true;
            }
            var compClass = comp.GetExportClassType().Value?.ToString() ?? "";
            var compIsStatic = compClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase);
            var wantStatic = part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase);
            var componentTags = FindPropertyLive<ArrayPropertyData>(comp.Data, "ComponentTags")?.Value?
                .OfType<NamePropertyData>()
                .Select(tag => tag.Value.ToString())
                .ToArray()
                ?? Array.Empty<string>();
            return CanRepointExistingComponentForTest(
                compIsStatic,
                wantStatic,
                ComponentLooksLikeGlider(slot, componentTags),
                GliderService.IsNativeGliderPart(part));
        }
        catch
        {
            return true;
        }
    }

    internal static bool CanRepointExistingComponentForTest(
        bool existingIsStatic,
        bool incomingIsStatic,
        bool existingIsGlider,
        bool incomingIsGlider) =>
        existingIsStatic == incomingIsStatic && existingIsGlider == incomingIsGlider;

    private static bool ComponentLooksLikeGlider(string slot, IEnumerable<string> tags)
    {
        if (tags.Any(tag =>
                tag.Equals("Glider", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("GlideCape", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return slot.Contains("Glide", StringComparison.OrdinalIgnoreCase) ||
               slot.Contains("Glider", StringComparison.OrdinalIgnoreCase) ||
               slot.Contains("Wingsuit", StringComparison.OrdinalIgnoreCase);
    }

    public PartGraftBatchResult RepointComponentToPart(NativeSuitProject project, string componentName, NativeSuitPartRecord part)
        => RepointComponentsToParts(project, componentName, part, part);

    private PartGraftBatchResult RepointComponentsToParts(
        NativeSuitProject project,
        string componentName,
        NativeSuitPartRecord? playablePart,
        NativeSuitPartRecord? cutscenePart,
        bool applyToPlayable = true,
        bool applyToCutscene = true,
        bool restoreExistingFieldRecipe = false)
    {
        var batch = new PartGraftBatchResult { Status = "created", CreatedUtc = DateTime.UtcNow };
        var graftedContentRoot = Path.Combine(GuiOutputRoot, project.SlotId, "GraftedPartStage", "LEGOBatmanLotDK", "Content");
        var contentRoot = Directory.Exists(graftedContentRoot)
            ? graftedContentRoot
            : ResolveBestExistingContentRoot(project.SlotId)
              ?? new UAssetPatchService(ProjectRoot).CreateNameMapPatchedStage(project).PatchedContentRoot;
        batch.GraftedContentRoot = contentRoot;
        batch.PatchedContentRoot = contentRoot;
        var mappingsPath = FindDefaultMappingsPath();
        var exportName = componentName + "_GEN_VARIABLE";

        foreach (var (role, pkg, enabled) in new[]
                 {
                     (role: "playable", pkg: project.TargetPackages.Playable, enabled: applyToPlayable),
                     (role: "cutscene", pkg: project.TargetPackages.Cutscene, enabled: applyToCutscene),
                 }.Where(entry => entry.enabled))
        {
            var pr = new PartGraftPackageResult { Role = role, TargetPackagePath = pkg, TargetSlot = componentName };
            try
            {
                var part = role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                    ? playablePart
                    : cutscenePart;
                if (part is null)
                {
                    throw new InvalidOperationException($"No {role} donor recipe was available for '{componentName}'.");
                }

                var targetBase = PackagePathToBasePath(contentRoot, pkg);
                pr.InputUasset = targetBase + ".uasset";
                pr.OutputUasset = targetBase + ".uasset";
                if (!File.Exists(pr.InputUasset))
                {
                    throw new FileNotFoundException("Target asset not found.", pr.InputUasset);
                }
                var mappings = string.IsNullOrWhiteSpace(mappingsPath) ? null : MappingsCache.Load(mappingsPath);
                var asset = new UAsset(pr.InputUasset, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);

                var comp = asset.Exports.OfType<NormalExport>()
                    .FirstOrDefault(e => e.ObjectName.ToString().Equals(exportName, StringComparison.OrdinalIgnoreCase));
                if (comp is null)
                {
                    pr.Success = false;
                    pr.Error = $"Component '{componentName}' not found in {role}.";
                    batch.PackageResults.Add(pr);
                    continue;
                }

                var meshImport = EnsureObjectImportLive(asset, part.MeshPackagePath, part.MeshObjectName, "/Script/Engine", part.MeshKind);
                var animImport = FPackageIndex.FromRawIndex(0);
                if (!string.IsNullOrWhiteSpace(part.AnimClassObjectName) && !string.IsNullOrWhiteSpace(part.AnimClassPackagePath))
                {
                    animImport = EnsureObjectImportLive(asset, part.AnimClassPackagePath, part.AnimClassObjectName, "/Script/Engine", "AnimBlueprintGeneratedClass");
                }
                var materialImports = new List<FPackageIndex>();
                foreach (var m in part.Materials)
                {
                    var cn = string.IsNullOrWhiteSpace(m.ClassName) ? "MaterialInstanceConstant" : m.ClassName;
                    var mi = EnsureObjectImportLive(asset, m.PackagePath, m.ObjectName, "/Script/Engine", cn);
                    if (!mi.IsNull()) materialImports.Add(mi);
                }

                // Preserve the existing component's tags (they already include the
                // "Glider" tag + the character's slot tag that the glide-visibility
                // wiring keys on) - only swap mesh/anim/material, not the tags.
                var existingTags = comp.Data.OfType<ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString().Equals("ComponentTags", StringComparison.OrdinalIgnoreCase))
                    ?.Value?.OfType<NamePropertyData>().Select(t => t.Value.ToString()).ToList();
                // Only repoint when the existing component's class matches the part's
                // mesh kind. We do NOT convert component classes (corruption risk):
                // replacing a skeletal component with a static mesh (or vice versa)
                // fails cleanly instead.
                var compClass = comp.GetExportClassType().Value?.ToString() ?? "";
                var compIsStatic = compClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase);
                var wantStatic = part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase);
                if (wantStatic != compIsStatic)
                {
                    pr.Success = false;
                    pr.Error = $"Component '{componentName}' is {(compIsStatic ? "static" : "skeletal")} but the part is {(wantStatic ? "static" : "skeletal")} — can't replace across mesh kinds (conversion disabled). Use a matching-kind part.";
                    batch.PackageResults.Add(pr);
                    continue;
                }

                SetComponentTemplateDataLive(
                    asset,
                    comp,
                    part,
                    meshImport,
                    animImport,
                    materialImports,
                    ComponentTagsForExistingFieldRepoint(existingTags, part.ComponentTags, restoreExistingFieldRecipe));
                comp.CreateBeforeSerializationDependencies = BuildCreateBeforeSerializationDependenciesLive(meshImport, animImport, materialImports);

                // For a GLIDER repoint, also move the SCS node's attach socket to the part's
                // socket. Different glide visuals attach differently - a wingsuit at Root, a
                // cape-glide at Chest_Socket - so keeping the old component's socket would put
                // the swapped glider in the wrong place. Scoped to glider parts (tag "Glider")
                // so ordinary component repoints (head/torso/etc.) are untouched. Only applied
                // when the node actually has an AttachToName (root nodes don't).
                var isGliderRepoint = GliderService.IsNativeGliderPart(part);
                if ((isGliderRepoint || restoreExistingFieldRecipe) &&
                    !string.IsNullOrWhiteSpace(part.AttachSocket))
                {
                    var nodeIndex = FindScsNodeBySlotLive(asset, componentName);
                    if (nodeIndex != 0 && asset.Exports[nodeIndex - 1] is NormalExport scsNode)
                    {
                        if (restoreExistingFieldRecipe)
                        {
                            SetNamePropertyValueLive(asset, scsNode.Data, "AttachToName", part.AttachSocket);
                            if (string.IsNullOrWhiteSpace(part.ParentComponentOrVariableName))
                            {
                                throw new InvalidOperationException(
                                    $"The certified visual-base recipe for '{componentName}' has no SCS parent identity.");
                            }
                            SetNamePropertyValueLive(
                                asset,
                                scsNode.Data,
                                "ParentComponentOrVariableName",
                                part.ParentComponentOrVariableName);
                        }
                        else if (FindPropertyLive<NamePropertyData>(scsNode.Data, "AttachToName") is not null)
                        {
                            SetNamePropertyValueLive(asset, scsNode.Data, "AttachToName", part.AttachSocket);
                        }
                    }
                    else if (restoreExistingFieldRecipe)
                    {
                        throw new InvalidOperationException(
                            $"The certified visual-base field '{componentName}' is not linked to a live SCS node.");
                    }
                }

                if (role.Equals("cutscene", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
                }
                asset.Write(pr.OutputUasset);
                pr.Success = true;
                pr.DonorMeshObjectPath = part.MeshObjectPath;
            }
            catch (Exception ex)
            {
                pr.Success = false;
                pr.Error = ex.ToString();
                pr.TransientFileLock = FileLockUtil.IsTransient(ex);
            }
            batch.PackageResults.Add(pr);
        }

        batch.Status = batch.PackageResults.Any(p => p.Success) ? "created" : "partial-failure";
        return batch;
    }

    private static IReadOnlyCollection<string>? ComponentTagsForExistingFieldRepoint(
        IReadOnlyCollection<string>? existingTags,
        IReadOnlyCollection<string>? donorTags,
        bool restoreExistingFieldRecipe) =>
        restoreExistingFieldRecipe ? donorTags ?? [] : existingTags;

    internal static IReadOnlyList<string> ComponentTagsForExistingFieldRepointForTest(
        IReadOnlyCollection<string>? existingTags,
        IReadOnlyCollection<string>? donorTags,
        bool restoreExistingFieldRecipe) =>
        ComponentTagsForExistingFieldRepoint(existingTags, donorTags, restoreExistingFieldRecipe)?.ToList() ?? [];

    private NativeSuitPartRecord? ResolveTemplateRecipe(NativeSuitPartRecord? part, string role)
    {
        if (part is null)
        {
            return null;
        }

        var index = new PartIndexService(ProjectRoot).LoadPartIndex();
        if (index is null)
        {
            return part;
        }

        var candidates = index.Parts
            .Where(candidate => candidate.HasMesh &&
                (!string.IsNullOrWhiteSpace(part.MeshObjectPath) &&
                 candidate.MeshObjectPath.Equals(part.MeshObjectPath, StringComparison.OrdinalIgnoreCase) ||
                 !string.IsNullOrWhiteSpace(part.MeshPackagePath) &&
                 candidate.MeshPackagePath.Equals(part.MeshPackagePath, StringComparison.OrdinalIgnoreCase) &&
                 candidate.MeshObjectName.Equals(part.MeshObjectName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Shared meshes (especially SK_CAPE_Glide) appear on many character BPs. Preserve the
        // exact selected donor whenever it is present instead of silently taking the first row for
        // that mesh/context; certified cape pairs depend on their component shell coming from the
        // same playable/cutscene package as the saved recipe.
        var template = candidates.FirstOrDefault(candidate =>
                candidate.Context.Equals(role, StringComparison.OrdinalIgnoreCase) &&
                candidate.SourcePackagePath.Equals(
                    part.SourcePackagePath,
                    StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(candidate =>
                candidate.Context.Equals(role, StringComparison.OrdinalIgnoreCase) &&
                candidate.SourcePackagePath.Equals(
                    part.TemplatePackagePath,
                    StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(candidate =>
                candidate.Context.Equals(role, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();

        if (template is null)
        {
            // A part can still be valid if it came directly from the index produced by
            // an older version of the tool. Keep it usable and let normal compatibility
            // checks decide whether a target shell can accept it.
            return part;
        }

        var resolved = PartRecipeService.Clone(part);
        resolved.TemplatePackagePath = template.SourcePackagePath;
        resolved.TemplateUasset = template.SourceUasset;
        resolved.TemplateSlot = template.Slot;
        resolved.TemplateComponentClass = template.ComponentClass;
        resolved.ComponentClass = template.ComponentClass;
        resolved.ParentComponentOrVariableName = template.ParentComponentOrVariableName;
        resolved.AttachSocket = template.AttachSocket;
        resolved.ComponentTags = template.ComponentTags.ToList();
        resolved.SemanticKind = string.IsNullOrWhiteSpace(part.SemanticKind)
            ? template.SemanticKind
            : part.SemanticKind;
        if (string.IsNullOrWhiteSpace(resolved.AnimClassObjectPath))
        {
            resolved.AnimClassObjectName = template.AnimClassObjectName;
            resolved.AnimClassPackagePath = template.AnimClassPackagePath;
            resolved.AnimClassObjectPath = template.AnimClassObjectPath;
        }
        resolved.IsSynthesized = false;
        resolved.RecipeKey = PartRecipeService.BuildRecipeKey(resolved);
        return resolved;
    }

    private static NativeSuitPartRecord? FindAbsoluteTorso2(NativeSuitPartIndex partIndex, string context)
    {
        return partIndex.Parts.FirstOrDefault(part =>
            part.Context.Equals(context, StringComparison.OrdinalIgnoreCase) &&
            part.Slot.Equals("Torso2", StringComparison.OrdinalIgnoreCase) &&
            part.SourcePackagePath.Contains("BP_Batman_Absolute", StringComparison.OrdinalIgnoreCase));
    }

    // Clone a donor static template when the base has no matching component.
    private const bool EnableDonorStaticShellGrafts = true;

    private static PartGraftPackageResult ApplyPartGraftToPackage(
        string role,
        string contentRoot,
        string targetPackagePath,
        NativeSuitPartRecord donorPart,
        string targetSlot,
        string cloneSlot,
        string attachSocket,
        bool preferDonorComponentShell,
        string? mappingsPath)
    {
        var result = new PartGraftPackageResult
        {
            Role = role,
            TargetPackagePath = targetPackagePath,
            DonorPackagePath = donorPart.SourcePackagePath,
            DonorMeshObjectPath = donorPart.MeshObjectPath,
            TargetSlot = targetSlot,
            CloneSlot = cloneSlot,
            AttachSocket = attachSocket
        };

        try
        {
            var targetBase = PackagePathToBasePath(contentRoot, targetPackagePath);
            result.InputUasset = targetBase + ".uasset";
            result.OutputUasset = targetBase + ".uasset";

            if (!File.Exists(result.InputUasset))
            {
                throw new FileNotFoundException("Target graft asset was not found.", result.InputUasset);
            }

            var mappings = string.IsNullOrWhiteSpace(mappingsPath) ? null : MappingsCache.Load(mappingsPath);
            var asset = new UAsset(result.InputUasset, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);

            var classExportIndex = FindFirstExportIndexLive(asset, export => export is ClassExport);
            var scsExportIndex = FindFirstExportIndexLive(asset, export => export.ObjectName.ToString().Equals("SimpleConstructionScript_0", StringComparison.OrdinalIgnoreCase));
            if (classExportIndex <= 0 || scsExportIndex <= 0)
            {
                throw new InvalidOperationException("Could not find target ClassExport or SimpleConstructionScript_0 export.");
            }
            var originalClassFieldSchema = CaptureClassFieldSchema(
                (ClassExport)asset.Exports[classExportIndex - 1]);

            // The clone source must match the part's MESH KIND - a StaticMesh part
            // (hair/hat) needs a StaticMeshComponent, a SkeletalMesh part a skeletal
            // one - else the clone won't carry the right mesh property. We do NOT
            // convert component classes (that risked corrupting the asset); if no
            // matching-kind component exists to clone, fail with a clear message.
            NormalExport newComponent;
            NormalExport newNode;
            var usedCrossPackageDonorShell = false;
            var cloneNodeIndex = FindCloneNodeForPartLive(asset, cloneSlot, donorPart);
            if (preferDonorComponentShell)
            {
                // Certified adapters need the authentic native cape shell, not merely another
                // skeletal component from the gameplay donor with mesh/material fields replaced.
                if (!TryBuildComponentShellFromDonorLive(
                        asset,
                        donorPart,
                        mappings,
                        out newComponent,
                        out newNode))
                {
                    throw new InvalidOperationException(
                        $"Could not clone the authentic donor component shell for '{donorPart.Slot}' " +
                        $"from '{donorPart.SourcePackagePath}'. The paired-cape adapter will not " +
                        "fall back to an unrelated local skeletal component.");
                }
                usedCrossPackageDonorShell = true;
            }
            else if (cloneNodeIndex != 0)
            {
                var cloneNode = asset.Exports[cloneNodeIndex - 1] as NormalExport
                    ?? throw new InvalidOperationException($"Clone SCS slot '{cloneSlot}' was not a NormalExport.");
                var cloneTemplateIndex = GetObjectPropertyValueLive(cloneNode.Data, "ComponentTemplate").Index;
                if (cloneTemplateIndex <= 0 || cloneTemplateIndex > asset.Exports.Count)
                {
                    throw new InvalidOperationException($"Clone slot '{cloneSlot}' had invalid ComponentTemplate index {cloneTemplateIndex}.");
                }

                var cloneTemplate = asset.Exports[cloneTemplateIndex - 1] as NormalExport
                    ?? throw new InvalidOperationException($"Clone template for '{cloneSlot}' was not a NormalExport.");
                newComponent = (NormalExport)cloneTemplate.Clone();
                newComponent.Data = DeepCloneProperties(cloneTemplate.Data);
                newNode = (NormalExport)cloneNode.Clone();
                newNode.Data = DeepCloneProperties(cloneNode.Data);
            }
            else if (EnableDonorStaticShellGrafts &&
                     donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
                     TryBuildComponentShellFromDonorLive(asset, donorPart, mappings, out newComponent, out newNode))
            {
                // The donor template supplies the StaticMeshComponent shape the base lacks.
                usedCrossPackageDonorShell = true;
            }
            else
            {
                throw new InvalidOperationException(
                    donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)
                        ? $"This base has no static-mesh component in its own blueprint for '{targetSlot}' (its pegs are inherited, not editable here). The graft needs a matching native static-component recipe from the extracted BP index; rebuild the index or use a base with a native static component if no recipe is found."
                        : $"Could not find a skeletal component to clone for '{targetSlot}' (tried '{cloneSlot}').");
            }

            var beforeImportCount = asset.Imports.Count;
            var meshImport = EnsureObjectImportLive(asset, donorPart.MeshPackagePath, donorPart.MeshObjectName, "/Script/Engine", donorPart.MeshKind);
            var animImport = FPackageIndex.FromRawIndex(0);
            if (!string.IsNullOrWhiteSpace(donorPart.AnimClassObjectName) &&
                !string.IsNullOrWhiteSpace(donorPart.AnimClassPackagePath))
            {
                animImport = EnsureObjectImportLive(asset, donorPart.AnimClassPackagePath, donorPart.AnimClassObjectName, "/Script/Engine", "AnimBlueprintGeneratedClass");
            }

            var materialImports = new List<FPackageIndex>();
            foreach (var material in donorPart.Materials)
            {
                var className = string.IsNullOrWhiteSpace(material.ClassName) ? "MaterialInstanceConstant" : material.ClassName;
                var materialImport = EnsureObjectImportLive(asset, material.PackagePath, material.ObjectName, "/Script/Engine", className);
                if (!materialImport.IsNull())
                {
                    materialImports.Add(materialImport);
                }
            }

            var componentExportIndex = asset.Exports.Count + 1;
            var scsNodeExportIndex = asset.Exports.Count + 2;
            var scsNodeName = NextScsNodeNameLive(asset);

            newComponent.ObjectName = MakeName(asset, targetSlot + "_GEN_VARIABLE");
            newComponent.OuterIndex = FromExportNumber(classExportIndex);
            newComponent.Asset = asset;
            SetComponentTemplateDataLive(asset, newComponent, donorPart, meshImport, animImport, materialImports);
            if (!GliderService.IsNativeGliderPart(donorPart))
            {
                // Cosmetic attachments must not inherit the hidden/deactivated
                // defaults of a glide-only shell selected as a last-resort clone.
                SetBoolPropertyIfPresentLive(newComponent.Data, "bHiddenInGame", false);
                SetBoolPropertyIfPresentLive(newComponent.Data, "bVisible", true);
                SetBoolPropertyIfPresentLive(newComponent.Data, "bAutoActivate", true);
            }
            newComponent.SerializationBeforeSerializationDependencies = new List<FPackageIndex> { FromExportNumber(classExportIndex) };
            newComponent.CreateBeforeSerializationDependencies = BuildCreateBeforeSerializationDependenciesLive(meshImport, animImport, materialImports);
            newComponent.SerializationBeforeCreateDependencies = new List<FPackageIndex> { newComponent.ClassIndex, newComponent.TemplateIndex }.Where(x => !x.IsNull()).ToList();
            newComponent.CreateBeforeCreateDependencies = new List<FPackageIndex> { FromExportNumber(classExportIndex) };

            newNode.ObjectName = MakeName(asset, scsNodeName);
            newNode.OuterIndex = FromExportNumber(scsExportIndex);
            newNode.Asset = asset;
            SetObjectPropertyValueLive(newNode.Data, "ComponentTemplate", FromExportNumber(componentExportIndex));
            SetNamePropertyValueLive(asset, newNode.Data, "AttachToName", ResolveAttachSocket(donorPart, attachSocket));
            SetNamePropertyValueLive(asset, newNode.Data, "ParentComponentOrVariableName", ResolveParentComponent(newNode.Data, donorPart));
            SetNamePropertyValueLive(asset, newNode.Data, "InternalVariableName", targetSlot);
            SetGuidPropertyValueLive(newNode.Data, "VariableGuid", Guid.NewGuid());
            RepairScsNodeComponentDependencyLive(newNode);
            newNode.SerializationBeforeCreateDependencies = new List<FPackageIndex> { newNode.ClassIndex, newNode.TemplateIndex }.Where(x => !x.IsNull()).ToList();
            newNode.CreateBeforeCreateDependencies = new List<FPackageIndex> { FromExportNumber(scsExportIndex) };

            // Do not append a reflected FObjectProperty to the generated class. Cooked gameplay
            // Blueprints retain their CDO as an opaque RawExport; changing the class-field schema
            // without re-authoring that raw unversioned property stream makes AsyncLoading2 read
            // unrelated CDO bytes as an object reference (the Who Laughs crash was export 382/61).
            // The authored SCS node and template are sufficient to construct the attachment, as
            // proven by the original in-game custom-static-mesh acceptance packages.

            asset.Exports.Add(newComponent);
            asset.Exports.Add(newNode);
            asset.DependsMap.Add(Array.Empty<int>());
            asset.DependsMap.Add(Array.Empty<int>());
            result.NewComponentExportIndex = componentExportIndex;
            result.NewScsNodeExportIndex = scsNodeExportIndex;
            result.AddedExports = 2;
            result.AddedImports = asset.Imports.Count - beforeImportCount;

            AddScsRootNodeLive(asset, (NormalExport)asset.Exports[scsExportIndex - 1], scsNodeExportIndex);
            UpdateRootCountsLive(asset);
            if (role.Equals("cutscene", StringComparison.OrdinalIgnoreCase))
            {
                EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
            }

            asset.Write(result.OutputUasset);

            // Round-trip the write: a cross-asset donor graft that produced a malformed
            // component would crash the game on load, so re-parse here and fail loudly
            // instead of shipping a bad asset. (The stage is regenerated on the next graft
            // attempt, so a rejected write is recoverable.)
            try
            {
                var validated = new UAsset(result.OutputUasset, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
                var validatedClass = validated.Exports.OfType<ClassExport>().FirstOrDefault();
                if (validatedClass is null ||
                    !ClassFieldSchemaMatches(validatedClass, originalClassFieldSchema))
                {
                    throw new InvalidOperationException(
                        $"Generated component '{targetSlot}' changed the cooked Blueprint class-field schema. " +
                        "Its opaque class-default-object stream cannot be safely rewritten.");
                }

                var validatedComponent = validated.Exports.OfType<NormalExport>()
                    .FirstOrDefault(export => export.ObjectName.ToString()
                        .Equals(targetSlot + "_GEN_VARIABLE", StringComparison.OrdinalIgnoreCase));
                if (validatedComponent is null)
                {
                    throw new InvalidOperationException(
                        $"Generated component template '{targetSlot}_GEN_VARIABLE' is missing.");
                }
                var validatedAnimClass = FindPropertyLive<ObjectPropertyData>(
                    validatedComponent.Data,
                    "AnimClass")?.Value ?? FPackageIndex.FromRawIndex(0);
                if (string.IsNullOrWhiteSpace(donorPart.AnimClassObjectName) &&
                    !validatedAnimClass.IsNull())
                {
                    throw new InvalidOperationException(
                        $"Generated component '{targetSlot}' retained an unrelated AnimClass even though its donor has none.");
                }

                var validatedNode = validated.Exports.OfType<NormalExport>()
                    .FirstOrDefault(export =>
                        export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase) &&
                        FindPropertyLive<NamePropertyData>(export.Data, "InternalVariableName")?.Value.ToString()
                            .Equals(targetSlot, StringComparison.OrdinalIgnoreCase) == true);
                var validatedTemplateIndex = validatedNode is null
                    ? FPackageIndex.FromRawIndex(0)
                    : GetObjectPropertyValueLive(validatedNode.Data, "ComponentTemplate");
                var validatedComponentIndex = validated.Exports.IndexOf(validatedComponent) + 1;
                if (validatedNode is null ||
                    validatedTemplateIndex.Index != validatedComponentIndex ||
                    !validatedNode.CreateBeforeSerializationDependencies.Any(
                        dependency => dependency.Index == validatedComponentIndex))
                {
                    throw new InvalidOperationException(
                        $"Generated SCS node '{targetSlot}' is not create-before-serialization dependent on its component template.");
                }

                var validatedNodeIndex = validated.Exports.IndexOf(validatedNode) + 1;
                var validatedScs = validated.Exports.OfType<NormalExport>()
                    .FirstOrDefault(export => export.ObjectName.ToString()
                        .Equals("SimpleConstructionScript_0", StringComparison.OrdinalIgnoreCase));
                if (validatedScs is null ||
                    !validatedScs.CreateBeforeSerializationDependencies.Any(
                        dependency => dependency.Index == validatedNodeIndex))
                {
                    throw new InvalidOperationException(
                        $"Generated SimpleConstructionScript is not create-before-serialization dependent on node '{targetSlot}'.");
                }

                if (usedCrossPackageDonorShell &&
                    donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryValidateCanonicalStaticShellBodyInstance(validatedComponent, out var bodyInstanceError))
                    {
                        throw new InvalidOperationException(
                            $"Generated static component '{targetSlot}' has invalid donor-shell collision metadata: {bodyInstanceError}");
                    }

                    if (role.Equals("cutscene", StringComparison.OrdinalIgnoreCase) &&
                        !TryValidateCutsceneParentOwner(validatedNode, out var parentOwnerError))
                    {
                        throw new InvalidOperationException(
                            $"Generated cutscene SCS node '{targetSlot}' has invalid parent-owner metadata: {parentOwnerError}");
                    }
                }
            }
            catch (Exception validateEx)
            {
                throw new InvalidOperationException(
                    "Graft wrote an asset that failed to re-parse — aborting so it can't crash the game on load. " + validateEx.Message,
                    validateEx);
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.ToString();
            result.TransientFileLock = FileLockUtil.IsTransient(ex);
        }

        return result;
    }

    private static int FindScsNodeBySlotLive(UAsset asset, string slot)
    {
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            if (asset.Exports[i] is not NormalExport normal ||
                !normal.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var internalVariableName = FindPropertyLive<NamePropertyData>(normal.Data, "InternalVariableName");
            if (internalVariableName?.Value.ToString().Equals(slot, StringComparison.OrdinalIgnoreCase) == true)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Finds an SCS node whose component template class matches the part's mesh kind
    /// (StaticMeshComponent for StaticMesh parts, a skeletal component otherwise).
    /// Prefers <paramref name="preferredSlot"/> when it matches; else the first
    /// matching node. Returns the 1-based node export index, or 0 if none.
    /// </summary>
    private static int FindCloneNodeForMeshKindLive(UAsset asset, string preferredSlot, string meshKind)
    {
        var wantStatic = meshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase);

        bool TemplateMatches(int nodeIndex)
        {
            if (asset.Exports[nodeIndex - 1] is not NormalExport node)
            {
                return false;
            }
            var templateIdx = GetObjectPropertyValueLive(node.Data, "ComponentTemplate").Index;
            if (templateIdx <= 0 || templateIdx > asset.Exports.Count)
            {
                return false;
            }
            var cls = asset.Exports[templateIdx - 1].GetExportClassType().Value?.ToString() ?? "";
            var isStatic = cls.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase);
            var isSkeletal = cls.Contains("Skeletal", StringComparison.OrdinalIgnoreCase) ||
                             cls.Contains("SkinnedMesh", StringComparison.OrdinalIgnoreCase);
            return wantStatic ? isStatic : isSkeletal;
        }

        var preferred = FindScsNodeBySlotLive(asset, preferredSlot);
        if (preferred != 0 && TemplateMatches(preferred))
        {
            return preferred;
        }
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            if (asset.Exports[i] is NormalExport n &&
                n.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase) &&
                TemplateMatches(i + 1))
            {
                return i + 1;
            }
        }
        return 0;
    }

    /// <summary>
    /// Chooses the closest native component shell for a part recipe. Mesh kind is a
    /// hard boundary; class, semantic family, socket, parent, tags, and preferred slot
    /// then rank the candidates. This prevents a Torso2 from accidentally cloning a
    /// Face shell merely because both happen to be skeletal components.
    /// </summary>
    private static int FindCloneNodeForPartLive(UAsset asset, string preferredSlot, NativeSuitPartRecord donorPart)
    {
        var wantStatic = donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase);
        var desiredClass = string.IsNullOrWhiteSpace(donorPart.TemplateComponentClass)
            ? donorPart.ComponentClass
            : donorPart.TemplateComponentClass;
        var desiredKind = string.IsNullOrWhiteSpace(donorPart.SemanticKind)
            ? PartRecipeService.SemanticKind(donorPart)
            : donorPart.SemanticKind;
        var desiredTags = donorPart.ComponentTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredIsGlider = GliderService.IsNativeGliderPart(donorPart);
        var bestIndex = 0;
        var bestScore = int.MinValue;

        for (var i = 0; i < asset.Exports.Count; i++)
        {
            if (asset.Exports[i] is not NormalExport node ||
                !node.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var templateIndex = GetObjectPropertyValueLive(node.Data, "ComponentTemplate").Index;
            if (templateIndex <= 0 || templateIndex > asset.Exports.Count ||
                asset.Exports[templateIndex - 1] is not NormalExport template)
            {
                continue;
            }

            var componentClass = template.GetExportClassType().Value?.ToString() ?? "";
            var isStatic = componentClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase);
            var isSkeletal = componentClass.Contains("Skeletal", StringComparison.OrdinalIgnoreCase) ||
                             componentClass.Contains("SkinnedMesh", StringComparison.OrdinalIgnoreCase);
            if (wantStatic ? !isStatic : !isSkeletal)
            {
                continue;
            }

            var slot = FindPropertyLive<NamePropertyData>(node.Data, "InternalVariableName")?.Value.ToString() ?? "";
            var attach = FindPropertyLive<NamePropertyData>(node.Data, "AttachToName")?.Value.ToString() ?? "";
            var parent = FindPropertyLive<NamePropertyData>(node.Data, "ParentComponentOrVariableName")?.Value.ToString() ?? "";
            var tags = FindPropertyLive<ArrayPropertyData>(template.Data, "ComponentTags")?.Value?
                .OfType<NamePropertyData>()
                .Select(value => value.Value.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidateIsGlider = ComponentLooksLikeGlider(slot, tags);

            var score = 0;
            if (slot.Equals(preferredSlot, StringComparison.OrdinalIgnoreCase)) score += 80;
            if (slot.Equals(donorPart.TemplateSlot, StringComparison.OrdinalIgnoreCase)) score += 70;
            if (!string.IsNullOrWhiteSpace(desiredClass) &&
                componentClass.Equals(desiredClass, StringComparison.OrdinalIgnoreCase)) score += 100;
            if (!string.IsNullOrWhiteSpace(donorPart.AttachSocket) &&
                attach.Equals(donorPart.AttachSocket, StringComparison.OrdinalIgnoreCase)) score += 35;
            if (!string.IsNullOrWhiteSpace(donorPart.ParentComponentOrVariableName) &&
                parent.Equals(donorPart.ParentComponentOrVariableName, StringComparison.OrdinalIgnoreCase)) score += 15;

            score += desiredTags.Count(tag => tags.Contains(tag)) * 18;
            // A cosmetic cape and a runtime glide visual can both be named Cape,
            // but their visibility/animation wiring is not interchangeable.
            score += candidateIsGlider == desiredIsGlider ? 240 : -400;
            var candidateKind = PartRecipeService.SemanticKind(slot, "", "", tags);
            if (candidateKind.Equals(desiredKind, StringComparison.OrdinalIgnoreCase)) score += 30;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i + 1;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Builds an authentic component SCS node + template from the donor package. Static parts use
    /// this when the target has no static shell; certified paired-cape adapters also use it for an
    /// appended skeletal cosmetic cape so no unrelated face/body/glider shell is repurposed. This
    /// is not class conversion: the donor template's native class and serialized property shape are
    /// retained, while cross-package references are rebased and visual fields are overwritten.
    /// </summary>
    private static bool TryBuildComponentShellFromDonorLive(
        UAsset target,
        NativeSuitPartRecord donorPart,
        Usmap? mappings,
        out NormalExport newComponent,
        out NormalExport newNode)
    {
        newComponent = null!;
        newNode = null!;

        if (string.IsNullOrWhiteSpace(donorPart.SourcePackagePath) &&
            string.IsNullOrWhiteSpace(donorPart.TemplatePackagePath))
        {
            return false;
        }

        string donorUasset;
        try
        {
            var templatePackagePath = string.IsNullOrWhiteSpace(donorPart.TemplatePackagePath)
                ? donorPart.SourcePackagePath
                : donorPart.TemplatePackagePath;
            donorUasset = PackagePathToBasePath(AppSettings.Current.EffectiveExtractedContentRoot(), templatePackagePath) + ".uasset";
        }
        catch
        {
            return false;
        }
        if (!File.Exists(donorUasset))
        {
            return false;
        }

        var donor = new UAsset(donorUasset, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);

        var templateSlot = string.IsNullOrWhiteSpace(donorPart.TemplateSlot)
            ? donorPart.Slot
            : donorPart.TemplateSlot;
        var donorNodeIndex = FindScsNodeBySlotLive(donor, templateSlot);
        if (donorNodeIndex == 0)
        {
            donorNodeIndex = FindCloneNodeForMeshKindLive(donor, templateSlot, donorPart.MeshKind);
        }
        if (donorNodeIndex == 0 || donor.Exports[donorNodeIndex - 1] is not NormalExport donorNode)
        {
            return false;
        }

        var donorTemplateIndex = GetObjectPropertyValueLive(donorNode.Data, "ComponentTemplate").Index;
        if (donorTemplateIndex <= 0 || donorTemplateIndex > donor.Exports.Count ||
            donor.Exports[donorTemplateIndex - 1] is not NormalExport donorTemplate)
        {
            return false;
        }

        // Guard: the donor shell must authentically match the requested mesh kind.
        var donorTemplateClass = donorTemplate.GetExportClassType().Value?.ToString() ?? "";
        var donorIsStatic = donorTemplateClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase);
        var donorIsSkeletal = donorTemplateClass.Contains("Skeletal", StringComparison.OrdinalIgnoreCase) ||
                              donorTemplateClass.Contains("SkinnedMesh", StringComparison.OrdinalIgnoreCase);
        var wantsStatic = donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase);
        if (wantsStatic ? !donorIsStatic : !donorIsSkeletal)
        {
            return false;
        }

        // Copy every donor name into the target's name map. FNames serialize by string
        // lookup against the writing asset, so once all strings exist here the cloned
        // properties resolve correctly regardless of the donor's original indices.
        foreach (var donorName in donor.GetNameMapIndexList())
        {
            var s = donorName.ToString();
            if (!target.ContainsNameReference(new FString(s)))
            {
                target.AddNameReference(new FString(s), false, false);
            }
        }

        newComponent = (NormalExport)donorTemplate.Clone();
        newComponent.Data = DeepClonePropertiesRebased(donorTemplate.Data, target);
        newComponent.Asset = target;
        newComponent.ClassIndex = EnsureImportFromDonorLive(target, donor, donorTemplate.ClassIndex);
        // Preserve the donor's explicit component archetype when present. Some cooked templates
        // serialize TemplateIndex=0 and carry the class CDO only through preload dependencies, so
        // synthesize that exact class CDO import as a fallback (the static-hair hover crash was the
        // original proof that this link is mandatory).
        newComponent.TemplateIndex = EnsureImportFromDonorLive(target, donor, donorTemplate.TemplateIndex);
        if (newComponent.TemplateIndex.IsNull() && donorTemplate.ClassIndex.IsImport())
        {
            var donorClass = donorTemplate.ClassIndex.ToImport(donor);
            var classPackagePath = !donorClass.OuterIndex.IsNull() && donorClass.OuterIndex.IsImport()
                ? donorClass.OuterIndex.ToImport(donor).ObjectName.ToString()
                : "/Script/Engine";
            newComponent.TemplateIndex = EnsureObjectImportLive(
                target,
                classPackagePath,
                "Default__" + donorTemplateClass,
                classPackagePath,
                donorTemplateClass);
        }
        newComponent.SuperIndex = FPackageIndex.FromRawIndex(0);
        // Names are copied above (FNames resolve by string), but a few properties can still
        // carry FPackageIndex OBJECT refs into the DONOR's tables. The caller overwrites
        // StaticMesh/OverrideMaterials/ComponentTags with target imports; AttachParent (if
        // present) points into the donor's export graph and is redundant (attachment is driven
        // by the SCS node), so drop it. Everything else on Thomas's template (BodyInstance,
        // LightingChannels, bools, enums) is KEPT to match the working structure exactly.
        newComponent.Data.RemoveAll(p =>
            p.Name.ToString().Equals("AttachParent", StringComparison.OrdinalIgnoreCase) ||
            (p is ObjectPropertyData op &&
             !op.Name.ToString().Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
             !op.Name.ToString().Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
             !op.Name.ToString().Equals("SkinnedAsset", StringComparison.OrdinalIgnoreCase) &&
             !op.Name.ToString().Equals("AnimClass", StringComparison.OrdinalIgnoreCase)));

        newNode = (NormalExport)donorNode.Clone();
        newNode.Data = DeepClonePropertiesRebased(donorNode.Data, target);
        newNode.Asset = target;
        newNode.ClassIndex = EnsureImportFromDonorLive(target, donor, donorNode.ClassIndex);
        // Preserve the SCS node's archetype link too (mirror the donor).
        newNode.TemplateIndex = EnsureImportFromDonorLive(target, donor, donorNode.TemplateIndex);
        newNode.SuperIndex = FPackageIndex.FromRawIndex(0);
        // The SCS node's ComponentClass ObjectProperty still points at the donor's class import;
        // repoint it to the rebased target component class or the node carries a wrong index.
        var nodeComponentClass = newNode.Data.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name.ToString().Equals("ComponentClass", StringComparison.OrdinalIgnoreCase));
        if (nodeComponentClass is not null)
        {
            nodeComponentClass.Value = newComponent.ClassIndex;
        }

        return true;
    }

    /// <summary>
    /// Recreates in <paramref name="target"/> the import that <paramref name="donorImportIndex"/>
    /// points to in <paramref name="donor"/> (matching object/class/outer-package), returning the
    /// target-relative package index. Used to rebase a cloned donor export's class reference.
    /// </summary>
    private static FPackageIndex EnsureImportFromDonorLive(UAsset target, UAsset donor, FPackageIndex donorImportIndex)
    {
        if (donorImportIndex.IsNull() || !donorImportIndex.IsImport())
        {
            return FPackageIndex.FromRawIndex(0);
        }

        var import = donorImportIndex.ToImport(donor);
        var packagePath = "/Script/Engine";
        if (!import.OuterIndex.IsNull() && import.OuterIndex.IsImport())
        {
            packagePath = import.OuterIndex.ToImport(donor).ObjectName.ToString();
        }

        return EnsureObjectImportLive(
            target,
            packagePath,
            import.ObjectName.ToString(),
            import.ClassPackage.ToString(),
            import.ClassName.ToString());
    }

    private static string MakeUniqueTargetSlotAcrossPackages(
        string contentRoot,
        IEnumerable<string> packagePaths,
        string desiredSlot,
        string? mappingsPath,
        IEnumerable<string>? reservedSlots = null)
    {
        desiredSlot = string.IsNullOrWhiteSpace(desiredSlot)
            ? "Part"
            : desiredSlot.Trim();

        var existingSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Treat removal targets as occupied even though they're absent from the stage right now -
        // the base component they hide owns that name.
        foreach (var reserved in reservedSlots ?? Enumerable.Empty<string>())
        {
            existingSlots.Add(reserved);
        }
        var mappings = string.IsNullOrWhiteSpace(mappingsPath) ? null : MappingsCache.Load(mappingsPath);

        foreach (var packagePath in packagePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var uasset = PackagePathToBasePath(contentRoot, packagePath) + ".uasset";
                if (!File.Exists(uasset))
                {
                    continue;
                }

                var asset = new UAsset(
                    uasset,
                    EngineVersion.VER_UE5_6,
                    mappings,
                    CustomSerializationFlags.SkipPreloadDependencyLoading);

                foreach (var slot in GetScsSlotNamesLive(asset))
                {
                    existingSlots.Add(slot);
                }
            }
            catch
            {
                // Best-effort naming. The actual write path will surface any
                // package-specific load/write error with full context.
            }
        }

        if (!existingSlots.Contains(desiredSlot))
        {
            return desiredSlot;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{desiredSlot}_{i}";
            if (!existingSlots.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{desiredSlot}_{DateTime.UtcNow:HHmmss}";
    }

    private static IEnumerable<string> GetScsSlotNamesLive(UAsset asset)
    {
        // Ignore orphan SCS nodes so name allocation uses live components.
        var linked = GetLinkedScsNodeIndexesLive(asset);

        for (var i = 0; i < asset.Exports.Count; i++)
        {
            if (asset.Exports[i] is not NormalExport export ||
                !export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (linked is not null && !linked.Contains(i + 1))
            {
                continue; // orphan node — not part of the live component graph
            }

            var internalVariableName = FindPropertyLive<NamePropertyData>(export.Data, "InternalVariableName");
            var slot = internalVariableName?.Value.ToString();
            if (!string.IsNullOrWhiteSpace(slot))
            {
                yield return slot;
            }
        }
    }

    /// <summary>
    /// Returns the 1-based export indexes of SCS nodes that are linked into the
    /// SimpleConstructionScript's AllNodes array, or null if that can't be determined
    /// (caller should then treat every SCS_Node export as live).
    /// </summary>
    private static HashSet<int>? GetLinkedScsNodeIndexesLive(UAsset asset)
    {
        var scs = asset.Exports.OfType<NormalExport>()
            .FirstOrDefault(e => e.ObjectName.ToString().Equals("SimpleConstructionScript_0", StringComparison.OrdinalIgnoreCase));
        if (scs is null)
        {
            return null;
        }

        var allNodes = FindPropertyLive<ArrayPropertyData>(scs.Data, "AllNodes");
        if (allNodes?.Value is null)
        {
            return null;
        }

        var indexes = new HashSet<int>();
        foreach (var entry in allNodes.Value.OfType<ObjectPropertyData>())
        {
            if (entry.Value.Index > 0)
            {
                indexes.Add(entry.Value.Index);
            }
        }
        return indexes.Count > 0 ? indexes : null;
    }

    private static List<PropertyData> DeepCloneProperties(IEnumerable<PropertyData> properties)
    {
        return properties.Select(property => (PropertyData)property.Clone()).ToList();
    }

    /// <summary>
    /// UAssetAPI's PropertyData.Clone preserves the donor FName objects, including their numeric
    /// indexes into the donor package's name map. Merely copying the donor strings into the target
    /// name map is not sufficient: on write, an un-rebased nested name is interpreted at the same
    /// numeric index in the target package. That previously turned NoCollision and the collision
    /// response channels into unrelated component tags, sockets, and asset paths. Walk the complete
    /// cloned property graph and recreate every FName against the target package before writing it.
    /// </summary>
    private static List<PropertyData> DeepClonePropertiesRebased(
        IEnumerable<PropertyData> properties,
        UAsset target)
    {
        var source = properties.ToList();
        var cloned = DeepCloneProperties(source);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < source.Count; i++)
        {
            RebindPropertyGraphNames(target, source[i], cloned[i], visited);
        }
        return cloned;
    }

    private static void RebindPropertyGraphNames(
        UAsset target,
        object? source,
        object? clone,
        HashSet<object> visited)
    {
        if (source is null || clone is null)
        {
            return;
        }

        if (source is FName sourceName && clone is FName clonedName)
        {
            RebindNameInPlace(target, sourceName, clonedName);
            return;
        }

        var type = source.GetType();
        if (type != clone.GetType() ||
            type == typeof(string) ||
            type.IsPrimitive ||
            type.IsEnum ||
            type == typeof(decimal) ||
            type == typeof(Guid) ||
            type == typeof(DateTime) ||
            type == typeof(TimeSpan))
        {
            return;
        }

        if (!type.IsValueType && !visited.Add(clone))
        {
            return;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            RebindPropertyGraphNames(
                target,
                type.GetProperty("Key")?.GetValue(source),
                type.GetProperty("Key")?.GetValue(clone),
                visited);
            RebindPropertyGraphNames(
                target,
                type.GetProperty("Value")?.GetValue(source),
                type.GetProperty("Value")?.GetValue(clone),
                visited);
            return;
        }

        if (source is DictionaryEntry sourceEntry && clone is DictionaryEntry clonedEntry)
        {
            RebindPropertyGraphNames(target, sourceEntry.Key, clonedEntry.Key, visited);
            RebindPropertyGraphNames(target, sourceEntry.Value, clonedEntry.Value, visited);
            return;
        }

        if (source is IEnumerable sourceItems && clone is IEnumerable clonedItems && source is not string)
        {
            var sourceEnumerator = sourceItems.GetEnumerator();
            var cloneEnumerator = clonedItems.GetEnumerator();
            try
            {
                while (sourceEnumerator.MoveNext() && cloneEnumerator.MoveNext())
                {
                    RebindPropertyGraphNames(
                        target,
                        sourceEnumerator.Current,
                        cloneEnumerator.Current,
                        visited);
                }
            }
            finally
            {
                (sourceEnumerator as IDisposable)?.Dispose();
                (cloneEnumerator as IDisposable)?.Dispose();
            }
            return;
        }

        // Stay inside UAssetAPI's serialized property graph. This avoids following arbitrary
        // framework objects while still covering PropertyData, AncestryInfo, FPropertyTypeName,
        // delegates, text arguments, gameplay-tag arrays, maps, sets, and nested structs.
        if (type.Assembly != typeof(PropertyData).Assembly)
        {
            return;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (var field in type.GetFields(flags))
        {
            var sourceValue = field.GetValue(source);
            if (field.FieldType == typeof(FName))
            {
                if (sourceValue is FName fieldName)
                {
                    field.SetValue(clone, MakeName(target, fieldName));
                }
                continue;
            }

            if (!ShouldTraverseNameGraph(field.FieldType))
            {
                continue;
            }

            var clonedValue = field.GetValue(clone);
            RebindPropertyGraphNames(target, sourceValue, clonedValue, visited);
            if (field.FieldType.IsValueType && clonedValue is not null)
            {
                field.SetValue(clone, clonedValue);
            }
        }

        foreach (var property in type.GetProperties(flags))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            if (property.PropertyType == typeof(FName))
            {
                if (property.CanWrite && property.GetValue(source) is FName propertyName)
                {
                    property.SetValue(clone, MakeName(target, propertyName));
                }
                continue;
            }

            if (!ShouldTraverseNameGraph(property.PropertyType))
            {
                continue;
            }

            object? sourceValue;
            object? clonedValue;
            try
            {
                sourceValue = property.GetValue(source);
                clonedValue = property.GetValue(clone);
            }
            catch
            {
                continue;
            }

            RebindPropertyGraphNames(target, sourceValue, clonedValue, visited);
            if (property.CanWrite && property.PropertyType.IsValueType && clonedValue is not null)
            {
                property.SetValue(clone, clonedValue);
            }
        }
    }

    private static bool ShouldTraverseNameGraph(Type type) =>
        type != typeof(string) &&
        (typeof(IEnumerable).IsAssignableFrom(type) || type.Assembly == typeof(PropertyData).Assembly);

    private static void RebindNameInPlace(UAsset target, FName source, FName clone)
    {
        var rebound = MakeName(target, source);
        clone.Asset = rebound.Asset;
        clone.Type = rebound.Type;
        clone.Number = rebound.Number;
        clone.Value = rebound.Value;
    }

    internal static bool TryValidateCanonicalStaticShellBodyInstance(
        NormalExport component,
        out string error)
    {
        var bodyInstance = FindPropertyLive<StructPropertyData>(component.Data, "BodyInstance");
        if (bodyInstance is null)
        {
            error = "BodyInstance is missing.";
            return false;
        }

        var objectType = FindPropertyLive<EnumPropertyData>(bodyInstance.Value, "ObjectType")?.Value.ToString() ?? "";
        var collisionEnabled = FindPropertyLive<EnumPropertyData>(bodyInstance.Value, "CollisionEnabled")?.Value.ToString() ?? "";
        var collisionProfile = FindPropertyLive<NamePropertyData>(bodyInstance.Value, "CollisionProfileName")?.Value.ToString() ?? "";
        if (!objectType.Equals("ECC_WorldStatic", StringComparison.Ordinal) ||
            !collisionEnabled.Equals(CanonicalStaticCollisionProfile, StringComparison.Ordinal) ||
            !collisionProfile.Equals(CanonicalStaticCollisionProfile, StringComparison.Ordinal))
        {
            error =
                $"expected ObjectType=ECC_WorldStatic, CollisionEnabled={CanonicalStaticCollisionProfile}, " +
                $"CollisionProfileName={CanonicalStaticCollisionProfile}; got ObjectType='{objectType}', " +
                $"CollisionEnabled='{collisionEnabled}', CollisionProfileName='{collisionProfile}'.";
            return false;
        }

        var collisionResponses = FindPropertyLive<StructPropertyData>(bodyInstance.Value, "CollisionResponses");
        var responseArray = collisionResponses is null
            ? null
            : FindPropertyLive<ArrayPropertyData>(collisionResponses.Value, "ResponseArray");
        var entries = responseArray?.Value?.OfType<StructPropertyData>().ToList() ?? [];
        if (responseArray is null || entries.Count != responseArray.Value.Length)
        {
            error = "CollisionResponses.ResponseArray is missing or contains malformed entries.";
            return false;
        }

        var channels = entries
            .Select(entry => FindPropertyLive<NamePropertyData>(entry.Value, "Channel")?.Value.ToString() ?? "")
            .ToList();
        if (!channels.SequenceEqual(CanonicalStaticCollisionChannels, StringComparer.Ordinal))
        {
            error =
                "collision response channels are not the canonical static-shell set. " +
                $"Expected [{string.Join(", ", CanonicalStaticCollisionChannels)}]; got [{string.Join(", ", channels)}].";
            return false;
        }

        var responses = entries
            .Select(entry => FindPropertyLive<EnumPropertyData>(entry.Value, "Response")?.Value.ToString() ?? "")
            .ToList();
        if (responses.Any(response => !response.Equals("ECR_Ignore", StringComparison.Ordinal)))
        {
            error = "one or more canonical static-shell collision responses are not ECR_Ignore.";
            return false;
        }

        error = "";
        return true;
    }

    internal static bool TryValidateCutsceneParentOwner(NormalExport node, out string error)
    {
        var owner = FindPropertyLive<NamePropertyData>(node.Data, "ParentComponentOwnerClassName")?.Value.ToString() ?? "";
        if (!owner.Equals(CanonicalCutsceneParentOwnerClass, StringComparison.Ordinal))
        {
            error = $"expected '{CanonicalCutsceneParentOwnerClass}', got '{owner}'.";
            return false;
        }

        error = "";
        return true;
    }

    private static FPackageIndex FromExportNumber(int exportNumber)
    {
        return exportNumber <= 0 ? FPackageIndex.FromRawIndex(0) : FPackageIndex.FromExport(exportNumber - 1);
    }

    private static FPackageIndex FromImportNumber(int importNumber)
    {
        return importNumber <= 0 ? FPackageIndex.FromRawIndex(0) : FPackageIndex.FromImport(importNumber - 1);
    }

    private static int FindFirstExportIndexLive(UAsset asset, Func<Export, bool> predicate)
    {
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            if (predicate(asset.Exports[i]))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static string NextScsNodeNameLive(UAsset asset)
    {
        var max = -1;
        foreach (var export in asset.Exports)
        {
            var name = export.ObjectName.ToString();
            if (name.StartsWith("SCS_Node_", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name["SCS_Node_".Length..], out var number))
            {
                max = Math.Max(max, number);
            }
        }

        return "SCS_Node_" + (max + 1);
    }

    private static void SetComponentTemplateDataLive(
        UAsset asset,
        NormalExport component,
        NativeSuitPartRecord donorPart,
        FPackageIndex meshImport,
        FPackageIndex animImport,
        List<FPackageIndex> materialImports,
        IReadOnlyCollection<string>? componentTagsOverride = null)
    {
        var animClass = FindPropertyLive<ObjectPropertyData>(component.Data, "AnimClass");
        if (!animImport.IsNull())
        {
            SetObjectPropertyValueLive(component.Data, "AnimClass", animImport);
        }
        else if (!donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
                 animClass is not null)
        {
            // A closest-shell clone can come from a different skeletal visual family. For example,
            // a regular cape added to Nightwing has no AnimClass of its own and may use the Face
            // component as its only non-glider skeletal shell. Retaining the face AnimBlueprint on
            // the cape mesh is an invalid mesh/animation pairing and can crash during component
            // initialization. The donor's explicit lack of an AnimClass means clear the clone.
            animClass.Value = FPackageIndex.FromRawIndex(0);
        }

        if (donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
        {
            SetObjectPropertyValueLive(component.Data, "StaticMesh", meshImport, asset);
        }
        else
        {
            SetObjectPropertyValueLive(component.Data, "SkeletalMesh", meshImport, asset);
            SetObjectPropertyValueLive(component.Data, "SkinnedAsset", meshImport, asset);
        }

        SetObjectArrayPropertyLive(asset, component.Data, "OverrideMaterials", materialImports);
        SetNameArrayPropertyLive(
            asset,
            component.Data,
            "ComponentTags",
            (componentTagsOverride ?? donorPart.ComponentTags).ToList());
    }

    private static string[] CaptureClassFieldSchema(ClassExport classExport) =>
        classExport.LoadedProperties
            .Select(property =>
                property.GetType().FullName + "|" +
                property.Name + "|" +
                (property is FObjectProperty objectProperty
                    ? objectProperty.PropertyClass.Index.ToString()
                    : ""))
            .ToArray();

    private static bool ClassFieldSchemaMatches(
        ClassExport classExport,
        IReadOnlyList<string> expected)
    {
        var actual = CaptureClassFieldSchema(classExport);
        return actual.Length == expected.Count &&
               actual.SequenceEqual(expected, StringComparer.Ordinal);
    }

    internal static bool RebindsCrossAssetDonorNamesForTest()
    {
        var donor = new UAsset(
            EngineVersion.VER_UE5_6,
            mappings: null,
            CustomSerializationFlags.None);
        donor.ClearNameIndexList();
        var target = new UAsset(
            EngineVersion.VER_UE5_6,
            mappings: null,
            CustomSerializationFlags.None);
        target.ClearNameIndexList();

        // Deliberately occupy the target's early name indexes with the exact kinds of unrelated
        // names that appeared in the crashing Who Laughs package. A raw donor-index clone would
        // resolve to these values after serialization.
        AddNamesLive(
            target,
            "Glider",
            "FLS",
            "TtCharacterAsset.Torso",
            "HeadStud_Attach_Socket",
            "/Game/Characters/Attachments/Hat/TheBatman/ABP_HAT_Batman",
            "BlueprintType",
            "Chest_Socket");

        var sourceBody = CreateCanonicalStaticBodyInstanceForTest(donor);
        var sourceOwner = new NamePropertyData(MakeName(donor, "ParentComponentOwnerClassName"))
        {
            Value = MakeName(donor, CanonicalCutsceneParentOwnerClass)
        };
        var cloned = DeepClonePropertiesRebased([sourceBody, sourceOwner], target);
        var component = new NormalExport { Data = [cloned[0]] };
        var node = new NormalExport { Data = [cloned[1]] };

        var body = FindPropertyLive<StructPropertyData>(component.Data, "BodyInstance");
        var profile = body is null
            ? null
            : FindPropertyLive<NamePropertyData>(body.Value, "CollisionProfileName");
        var collisionResponses = body is null
            ? null
            : FindPropertyLive<StructPropertyData>(body.Value, "CollisionResponses");
        var responseArray = collisionResponses is null
            ? null
            : FindPropertyLive<ArrayPropertyData>(collisionResponses.Value, "ResponseArray");
        var channels = responseArray?.Value?.OfType<StructPropertyData>()
            .Select(entry => FindPropertyLive<NamePropertyData>(entry.Value, "Channel"))
            .Where(channel => channel is not null)
            .Cast<NamePropertyData>()
            .ToList() ?? [];
        var owner = FindPropertyLive<NamePropertyData>(node.Data, "ParentComponentOwnerClassName");

        return TryValidateCanonicalStaticShellBodyInstance(component, out _) &&
               TryValidateCutsceneParentOwner(node, out _) &&
               body is not null && ReferenceEquals(body.Name.Asset, target) &&
               profile is not null && ReferenceEquals(profile.Value.Asset, target) &&
               owner is not null && ReferenceEquals(owner.Value.Asset, target) &&
               channels.Count == CanonicalStaticCollisionChannels.Count &&
               channels.All(channel => ReferenceEquals(channel.Value.Asset, target));
    }

    private static StructPropertyData CreateCanonicalStaticBodyInstanceForTest(UAsset asset)
    {
        EnumPropertyData Enum(string name, string enumType, string value) => new(MakeName(asset, name))
        {
            EnumType = MakeName(asset, enumType),
            InnerType = MakeName(asset, "ByteProperty"),
            Value = MakeName(asset, value)
        };

        var responseEntries = CanonicalStaticCollisionChannels
            .Select((channel, index) => (PropertyData)new StructPropertyData(
                MakeName(asset, index.ToString()),
                MakeName(asset, "ResponseChannel"))
            {
                Value =
                [
                    new NamePropertyData(MakeName(asset, "Channel"))
                    {
                        Value = MakeName(asset, channel)
                    },
                    Enum("Response", "ECollisionResponse", "ECR_Ignore")
                ]
            })
            .ToArray();
        var responseArray = new ArrayPropertyData(MakeName(asset, "ResponseArray"))
        {
            ArrayType = MakeName(asset, "StructProperty"),
            Value = responseEntries
        };
        var collisionResponses = new StructPropertyData(
            MakeName(asset, "CollisionResponses"),
            MakeName(asset, "CollisionResponseContainer"))
        {
            Value = [responseArray]
        };

        return new StructPropertyData(
            MakeName(asset, "BodyInstance"),
            MakeName(asset, "BodyInstance"))
        {
            Value =
            [
                Enum("ObjectType", "ECollisionChannel", "ECC_WorldStatic"),
                Enum("CollisionEnabled", "ECollisionEnabled", CanonicalStaticCollisionProfile),
                new NamePropertyData(MakeName(asset, "CollisionProfileName"))
                {
                    Value = MakeName(asset, CanonicalStaticCollisionProfile)
                },
                collisionResponses
            ]
        };
    }

    internal static bool RejectsCookedClassFieldMutationForTest()
    {
        var asset = new UAsset(
            EngineVersion.VER_UE5_6,
            mappings: null,
            CustomSerializationFlags.None);
        asset.ClearNameIndexList();
        var sourceClass = FromImportNumber(1);
        var classExport = new ClassExport
        {
            LoadedProperties =
            [
                new FObjectProperty
                {
                    Name = MakeName(asset, "Face"),
                    SerializedType = MakeName(asset, "ObjectProperty"),
                    PropertyClass = sourceClass,
                    ElementSize = 8,
                },
            ],
        };
        var originalSchema = CaptureClassFieldSchema(classExport);
        classExport.LoadedProperties = classExport.LoadedProperties.Append(new FObjectProperty
        {
            Name = MakeName(asset, "Hip"),
            SerializedType = MakeName(asset, "ObjectProperty"),
            PropertyClass = FromImportNumber(2),
            ElementSize = 8,
        }).ToArray();

        return !ClassFieldSchemaMatches(classExport, originalSchema);
    }

    internal static bool ClearsStaleAnimClassForDonorWithoutAnimForTest()
    {
        var asset = new UAsset(
            EngineVersion.VER_UE5_6,
            mappings: null,
            CustomSerializationFlags.None);
        asset.ClearNameIndexList();
        var component = new NormalExport
        {
            Data =
            [
                new ObjectPropertyData(MakeName(asset, "AnimClass"))
                {
                    Value = FromImportNumber(5)
                },
                new ObjectPropertyData(MakeName(asset, "SkeletalMesh")),
                new ObjectPropertyData(MakeName(asset, "SkinnedAsset")),
                new ArrayPropertyData(MakeName(asset, "OverrideMaterials")),
                new ArrayPropertyData(MakeName(asset, "ComponentTags")),
            ]
        };
        var donor = new NativeSuitPartRecord { MeshKind = "SkeletalMesh" };

        SetComponentTemplateDataLive(
            asset,
            component,
            donor,
            FromImportNumber(7),
            FPackageIndex.FromRawIndex(0),
            []);

        return FindPropertyLive<ObjectPropertyData>(component.Data, "AnimClass")?.Value.IsNull() == true &&
               BuildCreateBeforeSerializationDependenciesLive(
                   FromImportNumber(7),
                   FPackageIndex.FromRawIndex(0),
                   []).Select(index => index.Index).SequenceEqual([-7]);
    }

    internal static bool AddsScsNodeDependencyInNativeOrderForTest()
    {
        var existing = new[] { 30, 31, 32, 33, 34, 29 }
            .Select(FromExportNumber)
            .ToList();
        var once = AddScsNodeDependencyInNativeOrder(
            existing,
            FromExportNumber(59),
            FromExportNumber(29));
        var twice = AddScsNodeDependencyInNativeOrder(
            once,
            FromExportNumber(59),
            FromExportNumber(29));
        return twice.Select(index => index.Index)
            .SequenceEqual([30, 31, 32, 33, 34, 59, 29]);
    }

    private static void SetBoolPropertyIfPresentLive(List<PropertyData> properties, string propertyName, bool value)
    {
        var property = FindPropertyLive<BoolPropertyData>(properties, propertyName);
        if (property is not null)
        {
            property.Value = value;
        }
    }

    private static FPackageIndex EnsureObjectImportLive(UAsset asset, string packagePath, string objectName, string classPackage, string className)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(objectName))
        {
            return FPackageIndex.FromRawIndex(0);
        }

        var packageImport = EnsurePackageImportLive(asset, packagePath);
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString().Equals(objectName, StringComparison.Ordinal) &&
                import.OuterIndex.Index == packageImport.Index &&
                import.ClassPackage.ToString().Equals(classPackage, StringComparison.Ordinal) &&
                import.ClassName.ToString().Equals(className, StringComparison.Ordinal))
            {
                return FromImportNumber(i + 1);
            }
        }

        AddNamesLive(asset, objectName, classPackage, className);
        return asset.AddImport(new Import(classPackage, className, packageImport, objectName, false, asset));
    }

    private static FPackageIndex EnsurePackageImportLive(UAsset asset, string packagePath)
    {
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString().Equals(packagePath, StringComparison.Ordinal) &&
                import.OuterIndex.IsNull() &&
                import.ClassName.ToString().Equals("Package", StringComparison.Ordinal))
            {
                return FromImportNumber(i + 1);
            }
        }

        AddNamesLive(asset, packagePath, "/Script/CoreUObject", "Package");
        return asset.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), packagePath, false, asset));
    }

    private static List<FPackageIndex> BuildCreateBeforeSerializationDependenciesLive(FPackageIndex meshImport, FPackageIndex animImport, List<FPackageIndex> materialImports)
    {
        var output = new List<FPackageIndex>();
        if (!animImport.IsNull())
        {
            output.Add(animImport);
        }
        if (!meshImport.IsNull())
        {
            output.Add(meshImport);
        }
        output.AddRange(materialImports.Where(index => !index.IsNull()));
        return output
            .GroupBy(index => index.Index)
            .Select(group => group.First())
            .ToList();
    }

    private static void RepairScsNodeComponentDependencyLive(NormalExport scsNode)
    {
        var componentTemplate = GetObjectPropertyValueLive(scsNode.Data, "ComponentTemplate");
        if (componentTemplate.Index <= 0)
        {
            throw new InvalidOperationException(
                $"SCS node '{scsNode.ObjectName}' has no export-backed ComponentTemplate.");
        }

        // Match native cooked SCS nodes exactly: the node cannot serialize until its component
        // template export has been created. Never retain the cloned node's dependency on the old
        // source component after assigning a new ComponentTemplate.
        scsNode.CreateBeforeSerializationDependencies = [componentTemplate];
    }

    private static void AddScsRootNodeLive(UAsset asset, NormalExport scsExport, int scsNodeExportIndex)
    {
        AddObjectIndexToArrayPropertyLive(asset, scsExport.Data, "RootNodes", FromExportNumber(scsNodeExportIndex));
        AddObjectIndexToArrayPropertyLive(asset, scsExport.Data, "AllNodes", FromExportNumber(scsNodeExportIndex));

        var defaultRootIndex = asset.Exports
            .Select((export, index) => (export, index: index + 1))
            .Where(entry => entry.export is NormalExport)
            .Select(entry => (node: (NormalExport)entry.export, entry.index))
            .FirstOrDefault(entry =>
                entry.node.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase) &&
                FindPropertyLive<NamePropertyData>(entry.node.Data, "InternalVariableName")?.Value.ToString()
                    .Equals("DefaultSceneRoot", StringComparison.OrdinalIgnoreCase) == true)
            .index;
        scsExport.CreateBeforeSerializationDependencies =
            AddScsNodeDependencyInNativeOrder(
                scsExport.CreateBeforeSerializationDependencies,
                FromExportNumber(scsNodeExportIndex),
                defaultRootIndex > 0 ? FromExportNumber(defaultRootIndex) : FPackageIndex.FromRawIndex(0));
    }

    private static List<FPackageIndex> AddScsNodeDependencyInNativeOrder(
        IEnumerable<FPackageIndex> existing,
        FPackageIndex newNode,
        FPackageIndex defaultRoot)
    {
        var dependencies = existing
            .GroupBy(index => index.Index)
            .Select(group => group.First())
            .ToList();
        if (dependencies.Any(index => index.Index == newNode.Index))
        {
            return dependencies;
        }

        var rootPosition = defaultRoot.IsNull()
            ? -1
            : dependencies.FindIndex(index => index.Index == defaultRoot.Index);
        if (rootPosition >= 0)
        {
            dependencies.Insert(rootPosition, newNode);
        }
        else
        {
            dependencies.Add(newNode);
        }
        return dependencies;
    }

    private static void AddObjectIndexToArrayPropertyLive(UAsset asset, List<PropertyData> properties, string propertyName, FPackageIndex objectIndex)
    {
        var property = FindPropertyLive<ArrayPropertyData>(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find array property '{propertyName}'.");
        var values = property.Value?.ToList() ?? new List<PropertyData>();
        if (values.OfType<ObjectPropertyData>().Any(item => item.Value.Index == objectIndex.Index))
        {
            return;
        }

        values.Add(new ObjectPropertyData(MakeName(asset, values.Count.ToString()))
        {
            Value = objectIndex
        });
        property.Value = values.ToArray();
    }

    private static void SetObjectPropertyValueLive(List<PropertyData> properties, string propertyName, FPackageIndex objectIndex, UAsset? asset = null)
    {
        var property = FindPropertyLive<ObjectPropertyData>(properties, propertyName);
        if (property is not null)
        {
            property.Value = objectIndex;
            return;
        }
        // Property not serialized on the (possibly default-valued) clone - add it.
        // The unversioned writer keys off the class schema, so this only matters when
        // the component's class actually has the property (ensured by clone-kind match).
        if (asset is null)
        {
            throw new InvalidOperationException($"Could not find object property '{propertyName}'.");
        }
        properties.Add(new ObjectPropertyData(MakeName(asset, propertyName)) { Value = objectIndex });
    }

    private static FPackageIndex GetObjectPropertyValueLive(List<PropertyData> properties, string propertyName)
    {
        var property = FindPropertyLive<ObjectPropertyData>(properties, propertyName);
        return property?.Value ?? FPackageIndex.FromRawIndex(0);
    }

    private static string ResolveAttachSocket(NativeSuitPartRecord donorPart, string fallbackAttachSocket)
    {
        if (!string.IsNullOrWhiteSpace(donorPart.AttachSocket))
        {
            return donorPart.AttachSocket;
        }

        return fallbackAttachSocket;
    }

    private static string ResolveParentComponent(List<PropertyData> clonedNodeData, NativeSuitPartRecord donorPart)
    {
        if (!string.IsNullOrWhiteSpace(donorPart.ParentComponentOrVariableName))
        {
            return donorPart.ParentComponentOrVariableName;
        }

        var property = FindPropertyLive<NamePropertyData>(clonedNodeData, "ParentComponentOrVariableName");
        return property?.Value.ToString() ?? "";
    }

    private static void SetNamePropertyValueLive(UAsset asset, List<PropertyData> properties, string propertyName, string value)
    {
        var property = FindPropertyLive<NamePropertyData>(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find name property '{propertyName}'.");
        property.Value = MakeName(asset, value);
    }

    private static void SetGuidPropertyValueLive(List<PropertyData> properties, string propertyName, Guid value)
    {
        var property = FindPropertyLive<StructPropertyData>(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find guid struct property '{propertyName}'.");
        if (property.Value.Count == 0 || property.Value[0] is not GuidPropertyData guidProperty)
        {
            throw new InvalidOperationException($"Guid property '{propertyName}' had an unexpected shape.");
        }

        guidProperty.Value = value;
    }

    private static void SetObjectArrayPropertyLive(UAsset asset, List<PropertyData> properties, string propertyName, List<FPackageIndex> objectIndexes)
    {
        var property = FindPropertyLive<ArrayPropertyData>(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find object array property '{propertyName}'.");
        var output = objectIndexes
            .Where(index => !index.IsNull())
            .Select((index, i) => (PropertyData)new ObjectPropertyData(MakeName(asset, i.ToString()))
            {
                Value = index
            })
            .ToArray();
        property.Value = output;
    }

    private static void SetNameArrayPropertyLive(UAsset asset, List<PropertyData> properties, string propertyName, List<string> values)
    {
        var property = FindPropertyLive<ArrayPropertyData>(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find name array property '{propertyName}'.");
        var output = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select((value, i) => (PropertyData)new NamePropertyData(MakeName(asset, i.ToString()))
            {
                Value = MakeName(asset, value)
            })
            .ToArray();
        property.Value = output;
    }

    private static T? FindPropertyLive<T>(List<PropertyData> properties, string propertyName)
        where T : PropertyData
    {
        return properties
            .OfType<T>()
            .FirstOrDefault(property => property.Name.ToString().Equals(propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static void UpdateRootCountsLive(UAsset asset)
    {
        foreach (var generation in asset.Generations)
        {
            generation.ExportCount = asset.Exports.Count;
            generation.NameCount = asset.GetNameMapIndexList().Count;
        }
    }

    private static void EnsureMinimalSchema(UAsset asset, string schemaName, string modulePath)
    {
        var mappings = asset.Mappings;
        if (mappings is null || mappings.Schemas.ContainsKey(schemaName))
        {
            return;
        }

        var schema = new UsmapSchema(
            name: schemaName,
            superType: "",
            propCount: 0,
            props: new ConcurrentDictionary<int, UsmapProperty>(),
            isCaseInsensitive: mappings.AreFNamesCaseInsensitive,
            superTypeModulePath: "",
            fromAsset: true)
        {
            ModulePath = modulePath
        };

        mappings.Schemas[schemaName] = schema;
    }

    private static FName MakeName(UAsset asset, string value)
    {
        if (!asset.ContainsNameReference(new FString(value)))
        {
            asset.AddNameReference(new FString(value), false, false);
        }

        return new FName(asset, value, 0);
    }

    private static FName MakeName(UAsset asset, FName source)
    {
        var value = source.Value.ToString();
        if (!asset.ContainsNameReference(new FString(value)))
        {
            asset.AddNameReference(new FString(value), false, false);
        }

        return new FName(asset, new FString(value), source.Number);
    }

    private static void AddNamesLive(UAsset asset, params string?[] names)
    {
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && !asset.ContainsNameReference(new FString(name)))
            {
                asset.AddNameReference(new FString(name), false, false);
            }
        }
    }

    private static void SetComponentTemplateData(JsonObject component, NativeSuitPartRecord donorPart, int meshImport, int animImport, List<int> materialImports)
    {
        var data = RequireArray(component, "Data");
        if (animImport != 0)
        {
            SetObjectPropertyValue(data, "AnimClass", animImport);
        }

        if (donorPart.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
        {
            SetObjectPropertyValue(data, "StaticMesh", meshImport);
        }
        else
        {
            SetObjectPropertyValue(data, "SkeletalMesh", meshImport);
            SetObjectPropertyValue(data, "SkinnedAsset", meshImport);
        }

        SetObjectArrayProperty(data, "OverrideMaterials", materialImports);
        SetNameArrayProperty(data, "ComponentTags", donorPart.ComponentTags);
    }

    private static void AddClassChildProperty(JsonObject classExport, string cloneSlot, string newSlot)
    {
        var loadedProperties = RequireArray(classExport, "LoadedProperties");
        if (loadedProperties.Any(node => GetString(node, "Name").Equals(newSlot, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var clone = loadedProperties
            .Select(node => node?.AsObject())
            .FirstOrDefault(node => node is not null && GetString(node, "Name").Equals(cloneSlot, StringComparison.OrdinalIgnoreCase));
        if (clone is null)
        {
            throw new InvalidOperationException($"Could not find loaded property clone slot '{cloneSlot}'.");
        }

        var newProperty = CloneObject(clone);
        newProperty["Name"] = newSlot;
        loadedProperties.Add(newProperty);
    }

    private static void AddScsRootNode(JsonObject scsExport, int scsNodeExportIndex)
    {
        var data = RequireArray(scsExport, "Data");
        AddObjectIndexToArrayProperty(data, "RootNodes", scsNodeExportIndex);
        AddObjectIndexToArrayProperty(data, "AllNodes", scsNodeExportIndex);
    }

    private static void AddObjectIndexToArrayProperty(JsonArray properties, string propertyName, int objectIndex)
    {
        var property = FindDataProperty(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find array property '{propertyName}'.");
        var values = RequireArray(property, "Value");
        if (values.Any(node => GetInt(node, "Value") == objectIndex))
        {
            return;
        }

        JsonObject newItem;
        if (values.Count > 0)
        {
            newItem = CloneObject(values[values.Count - 1]!.AsObject());
        }
        else
        {
            newItem = new JsonObject
            {
                ["$type"] = "UAssetAPI.PropertyTypes.Objects.ObjectPropertyData, UAssetAPI",
                ["Name"] = "0",
                ["ArrayIndex"] = 0,
                ["PropertyGuid"] = null,
                ["IsZero"] = false,
                ["PropertyTagFlags"] = "None",
                ["PropertyTypeName"] = null,
                ["PropertyTagExtensions"] = "NoExtension"
            };
        }

        newItem["Name"] = values.Count.ToString();
        newItem["Value"] = objectIndex;
        values.Add(newItem);
    }

    private static int EnsureObjectImport(JsonObject root, string packagePath, string objectName, string classPackage, string className)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(objectName))
        {
            return 0;
        }

        var packageImport = EnsurePackageImport(root, packagePath);
        var imports = RequireArray(root, "Imports");
        for (var i = 0; i < imports.Count; i++)
        {
            var import = imports[i]!.AsObject();
            if (GetString(import, "ObjectName").Equals(objectName, StringComparison.Ordinal) &&
                GetInt(import, "OuterIndex") == packageImport &&
                GetString(import, "ClassPackage").Equals(classPackage, StringComparison.Ordinal) &&
                GetString(import, "ClassName").Equals(className, StringComparison.Ordinal))
            {
                return -(i + 1);
            }
        }

        AddNames(RequireArray(root, "NameMap"), objectName, classPackage, className);
        var newImport = new JsonObject
        {
            ["$type"] = "UAssetAPI.Import, UAssetAPI",
            ["ObjectName"] = objectName,
            ["OuterIndex"] = packageImport,
            ["ClassPackage"] = classPackage,
            ["ClassName"] = className,
            ["PackageName"] = null,
            ["bImportOptional"] = false
        };
        imports.Add(newImport);
        return -imports.Count;
    }

    private static int EnsurePackageImport(JsonObject root, string packagePath)
    {
        var imports = RequireArray(root, "Imports");
        for (var i = 0; i < imports.Count; i++)
        {
            var import = imports[i]!.AsObject();
            if (GetString(import, "ObjectName").Equals(packagePath, StringComparison.Ordinal) &&
                GetInt(import, "OuterIndex") == 0 &&
                GetString(import, "ClassName").Equals("Package", StringComparison.Ordinal))
            {
                return -(i + 1);
            }
        }

        AddNames(RequireArray(root, "NameMap"), packagePath, "/Script/CoreUObject", "Package");
        var newImport = new JsonObject
        {
            ["$type"] = "UAssetAPI.Import, UAssetAPI",
            ["ObjectName"] = packagePath,
            ["OuterIndex"] = 0,
            ["ClassPackage"] = "/Script/CoreUObject",
            ["ClassName"] = "Package",
            ["PackageName"] = null,
            ["bImportOptional"] = false
        };
        imports.Add(newImport);
        return -imports.Count;
    }

    private static void UpdateRootCounts(JsonObject root)
    {
        var exports = RequireArray(root, "Exports");
        var nameMap = RequireArray(root, "NameMap");
        if (root["Generations"] is JsonArray generations)
        {
            foreach (var generationNode in generations)
            {
                if (generationNode is JsonObject generation)
                {
                    generation["ExportCount"] = exports.Count;
                    generation["NameCount"] = nameMap.Count;
                }
            }
        }

        root["NamesReferencedFromExportDataCount"] = nameMap.Count;
    }

    private static int FindScsNodeBySlot(JsonArray exports, string slot)
    {
        for (var i = 0; i < exports.Count; i++)
        {
            if (exports[i] is not JsonObject export ||
                !GetString(export, "ObjectName").StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = RequireArray(export, "Data");
            if (GetNamePropertyValue(data, "InternalVariableName").Equals(slot, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static int FindFirstExportIndex(JsonArray exports, Func<JsonObject, bool> predicate)
    {
        for (var i = 0; i < exports.Count; i++)
        {
            if (exports[i] is JsonObject export && predicate(export))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static string NextScsNodeName(JsonArray exports)
    {
        var max = -1;
        foreach (var exportNode in exports)
        {
            if (exportNode is not JsonObject export)
            {
                continue;
            }

            var name = GetString(export, "ObjectName");
            if (!name.StartsWith("SCS_Node_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(name["SCS_Node_".Length..], out var number))
            {
                max = Math.Max(max, number);
            }
        }

        return "SCS_Node_" + (max + 1);
    }

    private static List<int> BuildCreateBeforeSerializationDependencies(int meshImport, int animImport, List<int> materialImports)
    {
        var output = new List<int>();
        if (animImport != 0)
        {
            output.Add(animImport);
        }
        if (meshImport != 0)
        {
            output.Add(meshImport);
        }
        output.AddRange(materialImports.Where(index => index != 0));
        return output.Distinct().ToList();
    }

    private static void SetDependencyArray(JsonObject export, string name, params int[] values)
    {
        SetDependencyArray(export, name, values.AsEnumerable());
    }

    private static void SetDependencyArray(JsonObject export, string name, IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Where(value => value != 0).Distinct())
        {
            array.Add(value);
        }
        export[name] = array;
    }

    private static void SetObjectPropertyValue(JsonArray properties, string propertyName, int objectIndex)
    {
        var property = FindDataProperty(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find object property '{propertyName}'.");
        property["Value"] = objectIndex;
    }

    private static int GetObjectPropertyValue(JsonArray properties, string propertyName)
    {
        var property = FindDataProperty(properties, propertyName);
        return property is null ? 0 : GetInt(property, "Value");
    }

    private static void SetNamePropertyValue(JsonArray properties, string propertyName, string value)
    {
        var property = FindDataProperty(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find name property '{propertyName}'.");
        property["Value"] = value;
    }

    private static string GetNamePropertyValue(JsonArray properties, string propertyName)
    {
        var property = FindDataProperty(properties, propertyName);
        return property is null ? "" : GetString(property, "Value");
    }

    private static void SetGuidPropertyValue(JsonArray properties, string propertyName, Guid value)
    {
        var property = FindDataProperty(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find guid struct property '{propertyName}'.");
        var valueArray = RequireArray(property, "Value");
        if (valueArray.Count == 0 || valueArray[0] is not JsonObject guidProperty)
        {
            throw new InvalidOperationException($"Guid property '{propertyName}' had an unexpected shape.");
        }

        guidProperty["Value"] = "{" + value.ToString().ToUpperInvariant() + "}";
    }

    private static void SetObjectArrayProperty(JsonArray properties, string propertyName, List<int> objectIndexes)
    {
        var property = FindDataProperty(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find object array property '{propertyName}'.");
        var oldValues = RequireArray(property, "Value");
        var template = oldValues.Count > 0
            ? CloneObject(oldValues[0]!.AsObject())
            : new JsonObject
            {
                ["$type"] = "UAssetAPI.PropertyTypes.Objects.ObjectPropertyData, UAssetAPI",
                ["ArrayIndex"] = 0,
                ["PropertyGuid"] = null,
                ["IsZero"] = false,
                ["PropertyTagFlags"] = "None",
                ["PropertyTypeName"] = null,
                ["PropertyTagExtensions"] = "NoExtension"
            };

        var newValues = new JsonArray();
        for (var i = 0; i < objectIndexes.Count; i++)
        {
            var item = CloneObject(template);
            item["Name"] = i.ToString();
            item["Value"] = objectIndexes[i];
            newValues.Add(item);
        }

        property["Value"] = newValues;
    }

    private static void SetNameArrayProperty(JsonArray properties, string propertyName, List<string> values)
    {
        var property = FindDataProperty(properties, propertyName)
            ?? throw new InvalidOperationException($"Could not find name array property '{propertyName}'.");
        var oldValues = RequireArray(property, "Value");
        var template = oldValues.Count > 0
            ? CloneObject(oldValues[0]!.AsObject())
            : new JsonObject
            {
                ["$type"] = "UAssetAPI.PropertyTypes.Objects.NamePropertyData, UAssetAPI",
                ["ArrayIndex"] = 0,
                ["PropertyGuid"] = null,
                ["IsZero"] = false,
                ["PropertyTagFlags"] = "None",
                ["PropertyTypeName"] = null,
                ["PropertyTagExtensions"] = "NoExtension"
            };

        var newValues = new JsonArray();
        for (var i = 0; i < values.Count; i++)
        {
            var item = CloneObject(template);
            item["Name"] = i.ToString();
            item["Value"] = values[i];
            newValues.Add(item);
        }

        property["Value"] = newValues;
    }

    private static JsonObject? FindDataProperty(JsonArray properties, string propertyName)
    {
        foreach (var propertyNode in properties)
        {
            if (propertyNode is JsonObject property &&
                GetString(property, "Name").Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    private static JsonArray RequireArray(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonArray array)
        {
            return array;
        }

        throw new InvalidOperationException($"Expected JSON array '{propertyName}'.");
    }

    private static string GetString(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj || obj[propertyName] is null)
        {
            return "";
        }

        return obj[propertyName]!.GetValueKind() == JsonValueKind.String
            ? obj[propertyName]!.GetValue<string>()
            : obj[propertyName]!.ToJsonString();
    }

    private static int GetInt(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj || obj[propertyName] is null)
        {
            return 0;
        }

        try
        {
            return obj[propertyName]!.GetValue<int>();
        }
        catch
        {
            return 0;
        }
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString())!.AsObject();
    }

    private static void AddNames(JsonArray nameMap, params string?[] names)
    {
        AddNames(nameMap, names.AsEnumerable());
    }

    private static void AddNames(JsonArray nameMap, IEnumerable<string?> names)
    {
        var existing = new HashSet<string>(
            nameMap.Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length > 0),
            StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || existing.Contains(name))
            {
                continue;
            }

            nameMap.Add(name);
            existing.Add(name);
        }
    }

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        return ExtractedPackagePathService.ResolvePackageBase(contentRoot, packagePath)
               ?? throw new InvalidOperationException($"Package is not present in a recognized extracted mount: {packagePath}");
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private string? ResolveBestExistingContentRoot(string slotId, string? excludeContentRoot = null)
    {
        var baseDir = Path.Combine(GuiOutputRoot, slotId);
        var candidates = new[]
        {
            Path.Combine(baseDir, "GraftedPartStage", "LEGOBatmanLotDK", "Content"),
            Path.Combine(baseDir, "GraftedTorso2Stage", "LEGOBatmanLotDK", "Content"),
            Path.Combine(baseDir, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content")
        };

        var excludeFull = string.IsNullOrWhiteSpace(excludeContentRoot)
            ? ""
            : Path.GetFullPath(excludeContentRoot);

        return candidates
            .Where(Directory.Exists)
            .FirstOrDefault(candidate =>
                string.IsNullOrWhiteSpace(excludeFull) ||
                !Path.GetFullPath(candidate).Equals(excludeFull, StringComparison.OrdinalIgnoreCase));
    }

    private string? FindDefaultMappingsPath()
    {
        return AppSettings.Current.EffectiveUsmapPath();
    }
}
