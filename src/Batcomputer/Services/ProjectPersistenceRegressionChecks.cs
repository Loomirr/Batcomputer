namespace Batcomputer;

/// <summary>Deterministic guards for delayed and stale managed project saves.</summary>
internal static class ProjectPersistenceRegressionChecks
{
    public static void Run(ICollection<string> failures, TextWriter output)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-project-persistence-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SuitProjectService(root);
            var project = new NativeSuitProject
            {
                SlotId = "delayed_writer",
                DisplayName = "Older snapshot",
            };
            var older = service.CaptureProjectSave(project);
            project.DisplayName = "Newer snapshot";
            var newer = service.CaptureProjectSave(project);

            var newerCommit = service.CommitProjectSave(newer);
            var delayedOlderCommit = service.CommitProjectSave(older);
            var persisted = service.LoadProject(newer.Path);
            Check(
                newerCommit.Written &&
                delayedOlderCommit.Superseded &&
                persisted?.DisplayName == "Newer snapshot",
                "a delayed older project snapshot cannot overwrite a newer committed save",
                failures,
                output);

            var immutableProject = new NativeSuitProject
            {
                SlotId = "immutable_snapshot",
                DisplayName = "Captured value",
            };
            var immutableSnapshot = service.CaptureProjectSave(immutableProject);
            immutableProject.DisplayName = "Mutation after capture";
            service.CommitProjectSave(immutableSnapshot);
            Check(
                service.LoadProject(immutableSnapshot.Path)?.DisplayName == "Captured value",
                "async project saves serialize an immutable snapshot before the caller can yield",
                failures,
                output);

            var patchPlanProject = new NativeSuitProject
            {
                SlotId = "immutable_patch_plan",
                DisplayName = "Captured plan",
                TargetPackages = new TargetPackages
                {
                    Playable = "/Game/Mods/Test/BP_Captured_Playable",
                    Cutscene = "/Game/Mods/Test/BP_Captured_Cutscene",
                    Dcmd = "/Game/Mods/Test/DA_Captured_DCMD",
                },
            };
            var patchPlanCapture = service.CaptureProjectSave(patchPlanProject);
            var frozenPatchPlanProject = service.MaterializeProjectSaveSnapshot(patchPlanCapture);
            var patchPlan = PatchPlanService.CreatePatchPlan(frozenPatchPlanProject);
            patchPlanProject.DisplayName = "Live editor mutation";
            patchPlanProject.TargetPackages.Playable = "/Game/Mods/Test/BP_Mutated_Playable";
            Check(
                patchPlan.Project.DisplayName == "Captured plan" &&
                patchPlan.Project.TargetPackages.Playable == "/Game/Mods/Test/BP_Captured_Playable" &&
                patchPlan.Steps.Any(step =>
                    step.Target == "/Game/Mods/Test/BP_Captured_Playable"),
                "patch plans are derived from the immutable project save rather than retaining the live editor object",
                failures,
                output);

            var guardedProject = new NativeSuitProject
            {
                SlotId = "stale_context",
                DisplayName = "Must not be written",
            };
            var guardedSnapshot = service.CaptureProjectSave(guardedProject);
            var rejected = service.CommitProjectSave(guardedSnapshot, contextIsCurrent: () => false);
            Check(
                rejected.RejectedByContext && !File.Exists(guardedSnapshot.Path),
                "a save whose suit-selection context changed is rejected before writing its project document",
                failures,
                output);

            var sameService = MainForm.ResolveProjectServiceForRootForTest(service, root);
            var otherRoot = Path.Combine(root, "OtherWorkspace");
            var switchedService = MainForm.ResolveProjectServiceForRootForTest(service, otherRoot);
            var crossRootSnapshotRejected = false;
            try
            {
                switchedService.CommitProjectSave(newer);
            }
            catch (InvalidOperationException)
            {
                crossRootSnapshotRejected = true;
            }
            Check(
                ReferenceEquals(sameService, service) &&
                !ReferenceEquals(switchedService, service) &&
                crossRootSnapshotRejected &&
                Path.GetFullPath(switchedService.ProjectRoot)
                    .Equals(Path.GetFullPath(otherRoot), StringComparison.OrdinalIgnoreCase),
                "changing the project root replaces the cached service and rejects cross-workspace snapshots",
                failures,
                output);

            var contextSnapshot = service.CaptureProjectSave(project);
            var contextMatches = MainForm.ProjectSaveContextMatchesForTest(
                project,
                project,
                expectedSelectionGeneration: 7,
                currentSelectionGeneration: 7,
                service,
                service,
                project.SlotId,
                contextSnapshot.Path);
            var selectionRejected = !MainForm.ProjectSaveContextMatchesForTest(
                project,
                new NativeSuitProject { SlotId = project.SlotId },
                7,
                8,
                service,
                switchedService,
                project.SlotId,
                contextSnapshot.Path);
            var priorSlot = project.SlotId;
            project.SlotId = "renamed_during_save";
            var renamedRejected = !MainForm.ProjectSaveContextMatchesForTest(
                project,
                project,
                7,
                7,
                service,
                service,
                priorSlot,
                contextSnapshot.Path);
            Check(
                contextMatches && selectionRejected && renamedRejected,
                "captured saves are guarded by suit identity, selection generation, workspace service, slot and path",
                failures,
                output);

            var captureGuardProject = new NativeSuitProject
            {
                SlotId = "capture_generation_guard",
                DisplayName = "Initial",
            };
            service.SaveProject(captureGuardProject);
            var startingCaptureGeneration = service.CaptureProjectSaveGeneration(captureGuardProject.SlotId);
            captureGuardProject.DisplayName = "Newer direct save";
            service.SaveProject(captureGuardProject);
            captureGuardProject.DisplayName = "Stale async continuation";
            var staleGuardedCapture = service.CaptureProjectSaveIfCurrent(
                captureGuardProject,
                startingCaptureGeneration,
                contextIsCurrent: () => true);
            var generationAfterRejectedCapture = service.CaptureProjectFileRollback(captureGuardProject.SlotId);
            Check(
                staleGuardedCapture.Snapshot is null &&
                staleGuardedCapture.Superseded &&
                generationAfterRejectedCapture.LastCapturedSequence ==
                generationAfterRejectedCapture.LastCommittedSequence &&
                service.LoadProject(generationAfterRejectedCapture.Path)?.DisplayName == "Newer direct save",
                "a stale editor continuation is rejected before it can publish a save sequence over a newer direct save",
                failures,
                output);

            var rollbackProject = new NativeSuitProject
            {
                SlotId = "owned_rollback",
                DisplayName = "Baseline",
            };
            service.SaveProject(rollbackProject);
            var rollbackBaseline = service.CaptureProjectFileRollback(rollbackProject.SlotId);
            rollbackProject.DisplayName = "Transaction edit";
            var ownedTransactionSave = service.CaptureProjectSave(rollbackProject);
            var ownedTransactionCommit = service.CommitProjectSave(ownedTransactionSave);
            var ownedRestore = service.TryRestoreProjectFile(
                rollbackBaseline,
                ownedTransactionSave,
                ownedSaveWasCommitted: ownedTransactionCommit.Written);
            var markerCertified = false;
            var certificationRan = service.RunIfProjectFileRestoreStillCurrent(
                ownedRestore,
                () => markerCertified = true);
            Check(
                ownedRestore.Restored &&
                certificationRan &&
                markerCertified &&
                service.LoadProject(ownedRestore.Path)?.DisplayName == "Baseline",
                "a stage transaction can roll back exactly the project save it owns and certify that restored generation",
                failures,
                output);

            var pendingProject = new NativeSuitProject
            {
                SlotId = "newer_pending_save",
                DisplayName = "Baseline",
            };
            service.SaveProject(pendingProject);
            var pendingBaseline = service.CaptureProjectFileRollback(pendingProject.SlotId);
            pendingProject.DisplayName = "Older operation";
            var olderOperation = service.CaptureProjectSave(pendingProject);
            pendingProject.DisplayName = "Newer pending operation";
            var newerPendingOperation = service.CaptureProjectSave(pendingProject);
            var staleRollback = service.TryRestoreProjectFile(
                pendingBaseline,
                olderOperation,
                ownedSaveWasCommitted: false);
            var newerPendingCommit = service.CommitProjectSave(newerPendingOperation);
            Check(
                staleRollback.Superseded &&
                !staleRollback.Restored &&
                newerPendingCommit.Written &&
                service.LoadProject(newerPendingOperation.Path)?.DisplayName == "Newer pending operation",
                "an older rollback cannot overwrite or invalidate a newer project save that is captured but still pending",
                failures,
                output);

            var postRollbackSave = service.CaptureProjectSave(new NativeSuitProject
            {
                SlotId = rollbackProject.SlotId,
                DisplayName = "Save after rollback",
            });
            var staleCertificationRan = false;
            var staleCertificationAccepted = service.RunIfProjectFileRestoreStillCurrent(
                ownedRestore,
                () => staleCertificationRan = true);
            var postRollbackCommit = service.CommitProjectSave(postRollbackSave);
            Check(
                !staleCertificationAccepted &&
                !staleCertificationRan &&
                postRollbackCommit.Written,
                "a newer captured save invalidates old stage-rollback certification before it writes a completion marker",
                failures,
                output);

            var certificationProject = new NativeSuitProject
            {
                SlotId = "save_certification_owner",
                DisplayName = "Stage owner",
            };
            var stageOwner = service.CaptureProjectSave(certificationProject);
            var stageOwnerCommit = service.CommitProjectSave(stageOwner);
            var currentCertificationRan = false;
            var currentCertificationAccepted = service.RunIfProjectSaveStillCurrent(
                stageOwner,
                () => currentCertificationRan = true);
            certificationProject.DisplayName = "Newer save before finalize";
            var newerBeforeFinalize = service.CaptureProjectSave(certificationProject);
            var newerBeforeFinalizeCommit = service.CommitProjectSave(newerBeforeFinalize);
            var staleSaveCertificationRan = false;
            var staleSaveCertificationAccepted = service.RunIfProjectSaveStillCurrent(
                stageOwner,
                () => staleSaveCertificationRan = true);
            Check(
                stageOwnerCommit.Written &&
                currentCertificationAccepted &&
                currentCertificationRan &&
                newerBeforeFinalizeCommit.Written &&
                !staleSaveCertificationAccepted &&
                !staleSaveCertificationRan &&
                service.LoadProject(newerBeforeFinalize.Path)?.DisplayName == "Newer save before finalize",
                "stage certification runs for its exact committed save but is rejected when a newer save lands before finalize",
                failures,
                output);

            var loadedReplayProject = new NativeSuitProject
            {
                SlotId = "loaded_replay_owner",
                DisplayName = "Loaded replay baseline",
            };
            service.SaveProject(loadedReplayProject);
            var loadedReplayOwner = service.CaptureProjectFileRollback(loadedReplayProject.SlotId);
            var currentLoadedReplayCertificationRan = false;
            var currentLoadedReplayAccepted = service.RunIfProjectFileSnapshotStillCurrent(
                loadedReplayOwner,
                () => currentLoadedReplayCertificationRan = true,
                contextIsCurrent: () => true);
            loadedReplayProject.DisplayName = "New save while loaded replay waits";
            service.SaveProject(loadedReplayProject);
            var staleLoadedReplayCertificationRan = false;
            var staleLoadedReplayAccepted = service.RunIfProjectFileSnapshotStillCurrent(
                loadedReplayOwner,
                () => staleLoadedReplayCertificationRan = true,
                contextIsCurrent: () => true);
            var changedSelectionCertificationRan = false;
            var changedSelectionAccepted = service.RunIfProjectFileSnapshotStillCurrent(
                service.CaptureProjectFileRollback(loadedReplayProject.SlotId),
                () => changedSelectionCertificationRan = true,
                contextIsCurrent: () => false);
            Check(
                currentLoadedReplayAccepted &&
                currentLoadedReplayCertificationRan &&
                !staleLoadedReplayAccepted &&
                !staleLoadedReplayCertificationRan &&
                !changedSelectionAccepted &&
                !changedSelectionCertificationRan,
                "loaded-project stage certification requires the unchanged project fingerprint, save generations, and active selection",
                failures,
                output);
        }
        catch (Exception ex)
        {
            failures.Add("project persistence regression threw: " + ex.Message);
            output.WriteLine("FAIL: project persistence regression threw: " + ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup only.
            }
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
