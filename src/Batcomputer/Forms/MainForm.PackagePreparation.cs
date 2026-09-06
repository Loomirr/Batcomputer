using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Isolates destructive release-time preparation from the certified authoring stages.
/// </summary>
public sealed partial class MainForm
{
    private sealed class PackagePreparationStage
    {
        public required string RootDirectory { get; init; }
        public required string ContentRoot { get; init; }
        public required string CleanupBoundary { get; init; }
    }

    private static NativeSuitProject CloneProjectForPackagePreparation(NativeSuitProject project) =>
        JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project))
        ?? throw new InvalidOperationException("Could not snapshot the suit for isolated package preparation.");

    /// <summary>
    /// Captures one immutable, certified authoring stage into a unique disposable work tree.
    /// The rebuild gate covers validation plus the complete copy so a concurrent rebuild cannot
    /// produce a mixed-generation snapshot.
    /// </summary>
    private async Task<PackagePreparationStage> CreatePackagePreparationStageAsync(
        NativeSuitProject project,
        string projectRoot)
    {
        var service = new SuitProjectService(projectRoot);
        var savedOwner = service.CaptureProjectFileRollback(project.SlotId);
        RequirePackageProjectMatchesSavedOwner(project, savedOwner);
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("prepare the saved suit for packaging"))
            throw new InvalidOperationException("Build cancelled because another suit was selected while its saved stage was restoring. Select the intended suit and build again.");

        await RebuildGate.WaitAsync();
        try
        {
            if (!service.RunIfProjectFileSnapshotStillCurrent(savedOwner, () => { }))
                throw new InvalidOperationException("This suit changed while the build was waiting. Build again to use its latest saved edits.");

            // Material closure resolution (including stage repair) is deliberately strict about
            // current texture cooks. Repair this saved owner's derived cache BEFORE resolving
            // any materials, not afterwards. Never rewrite the saved recipe or use an archive
            // to mask a missing PNG/template. The rebuild gate serializes cache preparation.
            foreach (var texture in project.GeneratedTextures)
            {
                if (!TryPrepareGeneratedTextureForStaging(texture, out var textureError))
                    throw new InvalidOperationException(
                        $"Texture preparation for '{project.DisplayName}': {textureError} " +
                        "Open this project's Textures category, select that texture and use Reimport image " +
                        "(or Replace image if its source moved), then build again.");
            }

            var sourceContentRoot = CurrentPackageContentRoot(project, projectRoot);
            if (IsIncompleteDeclarativeGraftStage(project, sourceContentRoot, projectRoot) || !Directory.Exists(sourceContentRoot))
            {
                AppendLog($"Build: rebuilding the incomplete saved stage for '{project.DisplayName}' (playable + cutscene). Please wait; no need to reapply a part.");
                await RepairAuthoringStageForPackagingCoreAsync(project, projectRoot, savedOwner);
                sourceContentRoot = CurrentPackageContentRoot(project, projectRoot);
                if (IsIncompleteDeclarativeGraftStage(project, sourceContentRoot, projectRoot))
                    throw new InvalidOperationException(StageRecoveryFailureMessage(project.DisplayName, "The rebuilt stage could not be certified."));
            }
            if (!Directory.Exists(sourceContentRoot))
            {
                throw new DirectoryNotFoundException(
                    "The certified authoring Content root does not exist: " + sourceContentRoot);
            }

            var projectOutput = new SuitProjectService(projectRoot).ProjectOutputDirectory(project);
            var preparationParent = Path.GetFullPath(Path.Combine(projectOutput, "PackagePreparation"));
            var preparationRoot = Path.GetFullPath(Path.Combine(
                preparationParent,
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")));
            EnsurePackagePreparationPath(preparationRoot, preparationParent);
            var contentRoot = Path.Combine(preparationRoot, "LEGOBatmanLotDK", "Content");

            try
            {
                await RunWithFileLockRetryAsync(
                    () =>
                    {
                        Directory.CreateDirectory(contentRoot);
                        CopyDirectoryContents(sourceContentRoot, contentRoot, overwrite: true);
                        return true;
                    },
                    "snapshot the certified authoring stage for package preparation");
                if (!service.RunIfProjectFileSnapshotStillCurrent(savedOwner, () => { }))
                    throw new InvalidOperationException("This suit was saved again during build preparation. Build again to use its latest edits; no partial package was produced.");
            }
            catch
            {
                TryDeletePackagePreparationTree(preparationRoot, preparationParent);
                throw;
            }

            return new PackagePreparationStage
            {
                RootDirectory = preparationRoot,
                ContentRoot = contentRoot,
                CleanupBoundary = preparationParent,
            };
        }
        finally
        {
            RebuildGate.Release();
        }
    }

    internal static void RequirePackageProjectMatchesSavedOwner(
        NativeSuitProject project, SuitProjectService.ProjectFileRollbackSnapshot savedOwner)
    {
        var saved = savedOwner.Existed && savedOwner.Contents is not null
            ? SuitProjectService.LoadProjectContents(savedOwner.Contents)
            : null;
        // Mod preparation canonicalizes donor-owner spelling (CatWoman/Catwoman). Gameplay tags
        // are FNames; permit only this identity-preserving difference, not a different tag/owner.
        if (saved is not null && (saved.PawnTag ?? "").Trim().Equals((project.PawnTag ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            saved.PawnTag = project.PawnTag ?? "";
        if (saved is null || !project.SlotId.Equals(savedOwner.SlotId, StringComparison.Ordinal) ||
            !JsonSerializer.Serialize(saved).Equals(JsonSerializer.Serialize(project), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The build snapshot for '{project.DisplayName}' does not match the current saved suit. Save the suit and build again; if another edit just finished, reopen it first. No partial package was produced.");
    }

    internal static string StageRecoveryFailureMessage(string displayName, string reason) =>
        $"Could not rebuild saved suit '{displayName}' for packaging.\n\n{reason}\n\n" +
        $"Open '{displayName}' from Home. For a part error, reapply or remove the named part in Parts; " +
        "for a material or custom-mesh error, repair/reimport the named asset. For locked files, close the asset viewer using them. " +
        "Then choose Build Mod again; it will retry the saved-stage rebuild automatically. Waiting alone will not repair a failed edit. No partial package was produced.";

    // Caller holds RebuildGate throughout recovery and the subsequent immutable copy. Never save
    // a build-time clone over the editor. Certification requires the original file/generations.
    private async Task RepairAuthoringStageForPackagingCoreAsync(
        NativeSuitProject project, string projectRoot,
        SuitProjectService.ProjectFileRollbackSnapshot savedOwner)
    {
        BaseStageFilesystemSnapshot? backup = null;
        try
        {
            backup = await CaptureBaseStageFilesystemAsync(projectRoot, project.SlotId,
                new[] { "UnpatchedStage", "PatchedNameMapStage", "GraftedPartStage" });
            await RebuildGraftStageCoreAsync(project, projectRoot, persistProject: false,
                certifyWithoutPersist: true, unchangedProjectFileForCertification: savedOwner);
            await DiscardBaseStageFilesystemBackupAsync(backup, logFailure: true);
            AppendLog($"Build: repaired and certified '{project.DisplayName}'; continuing packaging.");
        }
        catch (Exception failure)
        {
            Exception reportedFailure = failure;
            if (backup is not null)
            {
                try
                {
                    await RestoreBaseStageFilesystemAsync(backup);
                    // The entry state was already incomplete; never certify the restored old files.
                    await MarkDeclarativeStageIncompleteAsync(project, projectRoot);
                }
                catch (Exception rollbackFailure)
                {
                    reportedFailure = new AggregateException(
                        $"Stage recovery could not fully restore its backup at {backup.BackupRoot}. " +
                        failure.Message + " " + rollbackFailure.Message, failure, rollbackFailure);
                }
            }
            throw new InvalidOperationException(StageRecoveryFailureMessage(project.DisplayName, reportedFailure.Message), reportedFailure);
        }
    }

    private async Task CleanupPackagePreparationStageAsync(PackagePreparationStage stage)
    {
        try
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    EnsurePackagePreparationPath(stage.RootDirectory, stage.CleanupBoundary);
                    if (Directory.Exists(stage.RootDirectory))
                    {
                        Directory.Delete(stage.RootDirectory, recursive: true);
                    }
                    return true;
                },
                "clean disposable package-preparation stage");
        }
        catch (Exception ex)
        {
            // A retained work copy has no completion marker and is never selected by authoring or
            // packaging root resolution, so cleanup failure is safe and recoverable.
            AppendLog(
                $"  warning: disposable package-preparation files stayed locked and were retained at {stage.RootDirectory}: {ex.Message}");
        }
    }

    private static void TryDeletePackagePreparationTree(string preparationRoot, string preparationParent)
    {
        try
        {
            EnsurePackagePreparationPath(preparationRoot, preparationParent);
            if (Directory.Exists(preparationRoot))
            {
                Directory.Delete(preparationRoot, recursive: true);
            }
        }
        catch
        {
            // The caller reports the original preparation failure. A partial work copy is inert:
            // it has no completion marker and no root selector points at PackagePreparation.
        }
    }

    private static void EnsurePackagePreparationPath(string preparationRoot, string preparationParent)
    {
        var parent = Path.GetFullPath(preparationParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(preparationRoot);
        if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refused to modify a package-preparation directory outside its generated project boundary.");
        }
    }
}
