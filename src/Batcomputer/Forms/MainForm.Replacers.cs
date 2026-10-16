namespace Batcomputer;

public sealed partial class MainForm
{
    internal async Task PrepareReplacerFlowAuditAsync(string root, string projectPath)
    {
        _batchMode = true; _projectRootText.Text = Path.GetFullPath(root);
        _projectService = new SuitProjectService(_projectRootText.Text);
        var project = _projectService.LoadProject(projectPath) ?? throw new InvalidDataException("Missing audit project.");
        LoadProjectIntoUi(project);
        await AwaitLoadedProjectStageRestoresBeforeEditAsync("audit the replacer editor");
        SelectWorkspaceFolder(WorkspaceFolder.Replacers, refresh: false);
        SelectComboValue(_toyboxCategoryCombo, "Materials"); RefreshToyboxTiles();
        if (!_yourCharacter.Visible || _toyboxWorkspaceSplit?.Visible != true || _workspaceFolder != WorkspaceFolder.Replacers)
            throw new InvalidDataException("The replacer is not using the shared suit workspace.");
    }

    private static bool IsReplacementOnlyBuild(string root)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "mod.json")));
        var value = json.RootElement;
        return value.TryGetProperty("replacements", out var replacements) && replacements.GetArrayLength() > 0 &&
            value.GetProperty("suits").GetArrayLength() == 0 && value.GetProperty("vehicles").GetArrayLength() == 0;
    }
    private void RefreshReplacerTiles()
    {
        var current = CharacterReplacerProjectService.IsReplacer(_currentProject) ? _currentProject : null;
        var tiles = new List<VirtualTilePanel.Tile>
        {
            new() { Title = "＋ New replacer", Subtitle = "choose the original character", Accent = Theme.Base, Dashed = true, OnClick = () => _ = CreateReplacerAsync() },
        };
        if (current is not null)
        {
            tiles.Add(new() { Section = "CURRENT REPLACER", Title = "Edit appearance", Subtitle = "materials, faces, textures and parts", Accent = Theme.Materials, OnClick = () => SelectComboValue(_toyboxCategoryCombo, "Materials") });
            tiles.Add(new() { Section = "CURRENT REPLACER", Title = "View in 3D", Subtitle = "inspect current appearance", Accent = Theme.Base, OnClick = ViewCurrentSuitIn3D });
            tiles.Add(new() { Section = "CURRENT REPLACER", Title = "Build mod", Subtitle = "add to a mod and build normally", Accent = Theme.Good, OnClick = () => _ = BuildModForCurrentSuitAsync() });
        }
        var service = new SuitProjectService(_projectRootText.Text.Trim());
        foreach (var summary in service.ListProjects())
        {
            NativeSuitProject? project;
            try { project = service.LoadProject(summary.Path); } catch { continue; }
            if (!CharacterReplacerProjectService.IsReplacer(project)) continue;
            var path = summary.Path;
            tiles.Add(new() { Section = "YOUR REPLACERS", Title = summary.DisplayName, Subtitle = UnrealPathUtil.AssetName(project!.ReplacementTarget), Accent = Theme.Base, OnClick = () => OpenRecentProject(path) });
        }
        ShowVirtualTiles(tiles, hero: new VirtualTilePanel.HeroModel { Overline = "REPLACERS", Title = current?.DisplayName ?? "Replace a base-game character",
            Subtitle = current is null ? "Select a base, edit it with the suit tools, then add it to a mod and build. New component attachments are not supported yet." : "Overrides " + current.ReplacementTarget + " · visuals only; original behavior retained. New component attachments are not supported yet.", ThumbAccent = Theme.Base });
    }

    private async Task CreateReplacerAsync()
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Creating a replacer")) return;
        using var picker = new BaseCharacterPicker(false) { Text = "Select the base-game character to replace" };
        if (picker.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedVisualPackage)) return;
        var name = PromptForText("New replacer", "Name this visual replacement", BaseEligibilityService.CharacterStem(picker.SelectedVisualPackage), "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var service = new SuitProjectService(_projectRootText.Text.Trim());
            var project = CharacterReplacerProjectService.Create(picker.SelectedVisualPackage, name,
                AppSettings.Current.EffectiveExtractedContentRoot(), "replacer_" + Guid.NewGuid().ToString("N")[..12]);
            await Task.Run(() =>
            {
                var result = new UAssetPatchService(service.ProjectRoot).CreateNameMapPatchedStage(project);
                if (result.Status != "created") throw new InvalidDataException("Could not prepare replacement working copies: " + result.Status);
                service.SaveProject(project);
            });
            _projectService = service; LoadProjectIntoUi(project);
            SelectComboValue(_toyboxCategoryCombo, "Materials");
            AppendLog("Replacer created. Use the usual visual tools, then Add to mod / Build mod. Only the selected original Blueprint is overridden.");
        }
        catch (Exception ex) { Dialog.Error(this, "Could not create replacer", ex.Message); }
    }

    private async Task<string> PrepareReplacerForModAsync(NativeSuitProject authoring, SuitProjectService service, string output)
    {
        CharacterReplacerProjectService.Validate(authoring);
        var project = CloneProjectForPackagePreparation(authoring);
        var stage = await CreatePackagePreparationStageAsync(project, service.ProjectRoot);
        try
        {
            foreach (var assignment in project.MaterialAssignments) assignment.Context = "both";
            if (!StageGeneratedTexturesIntoContentRoot(project, stage.ContentRoot, out var error, persistProjectChanges: false)) throw new InvalidDataException(error);
            StageGeneratedMaterialsIntoContentRoot(project, stage.ContentRoot);
            RequireCompleteDeclarativeReplay(await ApplySavedMaterials(project, logIfNone: false, projectRoot: service.ProjectRoot, stageContentRootOverride: stage.ContentRoot), "Replacement materials");
            return await Task.Run(() => CharacterReplacerProjectService.Export(project, stage.ContentRoot,
                AppSettings.Current.EffectiveExtractedContentRoot(), AppSettings.Current.EffectiveUsmapPath()!, output));
        }
        finally { await CleanupPackagePreparationStageAsync(stage); }
    }
}
