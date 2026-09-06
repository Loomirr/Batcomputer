namespace Batcomputer;

internal static class StageTransactionRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var root = Path.Combine(Path.GetTempPath(), "Batcomputer-stage-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new SuitProjectService(root);
            var project = new NativeSuitProject { SlotId = "transaction", DisplayName = "Original suit", PawnTag = "Pawns.Playable.CatWoman.Test" };
            service.SaveProject(project);
            var before = service.CaptureProjectFileRollback(project.SlotId);
            var generation = service.CaptureProjectSaveGeneration(project.SlotId);
            project.PartGrafts.Add(new SavedPartGraft { Label = "Torso test", Slot = "Torso2" });
            MainForm.RecordProjectChange(project, "Parts", "Torso2", "graft torso");
            MainForm.RecordProjectChange(project, "Gliders", "Glide visual", "restored donor default");
            MainForm.RecordProjectChange(project, "Parts", "Torso2", "graft torso");
            results.Add((project.Changes.Count == 2 && service.CaptureProjectSaveGeneration(project.SlotId) == generation &&
                File.ReadAllText(before.Path) == before.Contents,
                "part/glider transaction journaling neither saves early nor supersedes rollback ownership; duplicate entries collapse"));
            var restored = service.TryRestoreProjectFile(before);
            results.Add((restored.Restored && !restored.Superseded && service.LoadProject(before.Path)!.PartGrafts.Count == 0,
                "a failed graft can restore the previous saved recipe after journaling without a false newer-edit conflict"));

            var capture = service.CaptureProjectSaveIfCurrent(project, generation);
            results.Add((capture.Snapshot is not null && service.CommitProjectSave(capture.Snapshot).Written &&
                service.LoadProject(before.Path)!.PartGrafts.Count == 1,
                "a successful graft commits its recipe and journal in a single owned save"));

            var owner = service.CaptureProjectFileRollback(project.SlotId);
            var loaded = service.LoadProject(owner.Path)!;
            MainForm.RequirePackageProjectMatchesSavedOwner(loaded, owner);
            results.Add((service.RunIfProjectFileSnapshotStillCurrent(owner, () => { }),
                "package recovery accepts the exact migrated saved recipe without saving it again"));
            loaded.PawnTag = "Pawns.Playable.Catwoman.Test";
            MainForm.RequirePackageProjectMatchesSavedOwner(loaded, owner);
            results.Add((true, "package recovery permits identity-preserving native PawnTag spelling canonicalization"));
            loaded.PawnTag = "Pawns.Playable.Batman.Test";
            bool changedTagRejected = false;
            try { MainForm.RequirePackageProjectMatchesSavedOwner(loaded, owner); }
            catch (InvalidOperationException) { changedTagRejected = true; }
            results.Add((changedTagRejected, "package recovery does not treat a different character owner as spelling canonicalization"));
            loaded.PawnTag = project.PawnTag;
            loaded.DisplayName = "Newer unsaved edit";
            bool rejected = false;
            try { MainForm.RequirePackageProjectMatchesSavedOwner(loaded, owner); }
            catch (InvalidOperationException) { rejected = true; }
            results.Add((rejected, "package recovery rejects stale or unsaved recipes before modifying any stage"));

            var staleGeneration = service.CaptureProjectSaveGeneration(project.SlotId);
            service.SaveProject(loaded);
            bool certified = false;
            results.Add((!service.RunIfProjectFileSnapshotStillCurrent(owner, () => certified = true) && !certified &&
                service.CaptureProjectSaveIfCurrent(project, staleGeneration).Snapshot is null &&
                service.TryRestoreProjectFile(owner).Superseded,
                "a real newer save still blocks stale certification, commit and rollback"));

            var message = MainForm.StageRecoveryFailureMessage("Example suit", "Part 'Example hip' failed: missing donor.");
            results.Add((message.Contains("Example hip") && message.Contains("Open 'Example suit'") &&
                message.Contains("Build Mod") && message.Contains("Waiting alone will not") && message.Contains("No partial package"),
                "failed automatic stage repair names the cause and gives actionable recovery instead of advising an unexplained wait"));
        }
        catch (Exception ex) { results.Add((false, "stage transaction regression: " + ex)); }
        finally { Directory.Delete(root, recursive: true); }
        return results;
    }
}
