namespace Batcomputer;

public sealed partial class MainForm
{
    private async Task OpenSkinnedMeshWorkshopAsync(SkinnedMeshImport? existing)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("edit skinned meshes")) return;
        var project = _currentProject;
        if (project?.PlayableTemplate is null || project.CutsceneTemplate is null)
        { Dialog.Warn(this, "Skinned meshes", "Open a suit and choose its base first."); return; }
        var projectRoot = _projectRootText.Text.Trim();
        var directory = new SuitProjectService(projectRoot).ProjectOutputDirectory(project);
        try
        {
            if (existing is null) await RebuildGraftStageFromDeclarativeAsync();
            if (!ReferenceEquals(project, _currentProject)) return;
            var content = Path.Combine(directory, "GraftedPartStage/LEGOBatmanLotDK/Content");
            var targets = existing is not null
                ? (IReadOnlyList<SkinnedMeshStageService.Target>)[new(existing.Component, existing.DonorMeshPackage)]
                : await Task.Run(() => SkinnedMeshStageService.Targets(content, project));
            if (targets.Count == 0) { Dialog.Warn(this, "Skinned meshes", "No compatible skeletal component exists in both character roles. Add the native part first. Static-only slots, facial rigs and cloth capes cannot be converted here."); return; }
            var recipe = existing?.Clone() ?? new SkinnedMeshImport { Component = targets[0].Component, DonorMeshPackage = targets[0].DonorMesh };
            if (string.IsNullOrEmpty(recipe.MeshPackage))
            {
                var target = project.TargetPackages.Playable.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var mod = target[Array.IndexOf(target, "Mods") + 1];
                var suit = new string(project.SlotId.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
                recipe.MeshPackage = $"/Game/Mods/{mod}/Skinned/S_{suit}/M_{recipe.Id}/SK_Custom";
            }
            var components = _characterSlots.Select(s => s.Component).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            using var workshop = new SkinnedMeshWorkshopForm(directory, targets, components, recipe, existing is not null);
            var answer = workshop.ShowDialog(this);
            if (!ReferenceEquals(project, _currentProject)) return;
            if (!workshop.RemoveRequested && (answer != DialogResult.OK || workshop.Result is null)) return;
            var previous = project.SkinnedMeshes.ToList(); var previousMaterials = project.MaterialAssignments.ToList();
            if (existing is not null) project.SkinnedMeshes.Remove(existing);
            if (!workshop.RemoveRequested)
            {
                var result = workshop.Result!;
                project.SkinnedMeshes.RemoveAll(m => m.Component.Equals(result.Component, StringComparison.OrdinalIgnoreCase));
                project.SkinnedMeshes.Add(result);
                // Preserve role-specific inspector overrides unless the workshop actually changes
                // that slot or its meaning. Reopening to rename a mesh must not erase CUT materials.
                project.MaterialAssignments.RemoveAll(m => m.Component.Split(':')[0].Equals(result.Component, StringComparison.OrdinalIgnoreCase) &&
                    (existing is null || !existing.Materials.Any(old => old.Slot == m.Slot &&
                        result.Materials.Any(next => next.Slot == m.Slot && next.SourceMaterialName == old.SourceMaterialName && next.MaterialPath == old.MaterialPath))));
            }
            try { await RebuildGraftStageFromDeclarativeAsync(); }
            catch { project.SkinnedMeshes = previous; project.MaterialAssignments = previousMaterials; throw; }
            RecordChange("Parts", recipe.Name, workshop.RemoveRequested ? "Restored native skinned component" : "Validated existing-rig FBX; playable + cutscene", "staged");
            RefreshToyboxTiles(); RefreshInspector(); _session.RaiseChanged();
        }
        catch (Exception ex) { AppendLog("Skinned mesh workshop: " + ex); Dialog.Error(this, "Skinned mesh could not be applied", ex.Message); }
    }
}
