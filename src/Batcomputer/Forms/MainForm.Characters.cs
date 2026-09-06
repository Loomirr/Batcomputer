namespace Batcomputer;

public sealed partial class MainForm
{
    private bool _creatingCharacterRecipe;
    private void RefreshCharacterWorkspaceTiles()
    {
        var current = CustomCharacterProjectService.IsCharacter(_currentProject) ? _currentProject : null;
        var tiles = new List<VirtualTilePanel.Tile>
        {
            new() { Section = "CHARACTERS", Title = "＋ New character", Subtitle = "own roster entry · start from a native base", Accent = Theme.Materials,
                Dashed = true, OnClick = () => _ = CreateCharacterAsync() },
            new() { Section = "CHARACTERS", Title = "Copy a saved suit", Subtitle = "new character from an existing design", Accent = Theme.Materials,
                OnClick = () => _ = CreateCharacterFromSavedSuitAsync() }
        };
        if (current is not null)
        {
            tiles.Add(new() { Section = "CURRENT CHARACTER", Title = "Character identity", Subtitle = current.PawnTag, Accent = Theme.Materials, OnClick = EditCustomCharacterIdentity });
            tiles.Add(new() { Section = "CURRENT CHARACTER", Title = "＋ Add a suit", Subtitle = "new variant for this character", Accent = Theme.Base, OnClick = () => _ = CreateCharacterSuitAsync(current) });
            tiles.Add(new() { Section = "CURRENT CHARACTER", Title = "Add to a mod / Build", Subtitle = "includes default suit + roster + unlock data", Accent = Theme.Gold,
                OnClick = () => _ = BuildModForCurrentSuitAsync() });
            tiles.Add(new() { Section = "CURRENT CHARACTER", Title = "Visual / gameplay base", Subtitle = "same authoring categories as suits", Accent = Theme.Base, OnClick = OpenBaseWizard });
        }
        var service = new SuitProjectService(_projectRootText.Text.Trim());
        foreach (var summary in service.ListProjects().Where(project => project.IsCharacter))
        {
            var captured = summary;
            tiles.Add(new() { Section = "YOUR CHARACTERS", Title = captured.DisplayName, Subtitle = $"{captured.CharacterId} · saved {captured.Modified:MMM d}",
                Accent = Theme.Materials, Image = LoadSuitCoverImage(captured), OnClick = () => OpenRecentProject(captured.Path) });
        }
        ShowVirtualTiles(tiles, hero: new VirtualTilePanel.HeroModel
        {
            Overline = "CHARACTER WORKSPACE", Title = current?.DisplayName ?? "Your own playable characters",
            Subtitle = "Independent character group and default suit. Customize parts, materials, faces, animations, abilities and equipment with the categories on the left. Scripted story cutscenes are not supported.",
            ThumbAccent = Theme.Materials
        });
    }

    private async Task CreateCharacterFromSavedSuitAsync()
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("copy a saved suit")) return;
        var service = new SuitProjectService(_projectRootText.Text.Trim());
        using var picker = new LoadSuitDialog(service.ListProjects().Where(project => !project.IsCharacter).ToArray(),
            libraryTitle: "Copy a saved design", libraryDescription: "Choose a suit to copy into a new character. The original stays unchanged.", actionText: "Copy design");
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedPath is null) return;
        try { await CreateCharacterAsync(service.LoadProject(picker.SelectedPath)); }
        catch (Exception ex) { Dialog.Error(this, "Could not copy the saved suit", ex.Message); }
    }

    private async Task CreateCharacterAsync(NativeSuitProject? source = null)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("create a character")) return;
        using var dialog = new CharacterIdentityDialog("New character", "Character ID (permanent; e.g. Ragman)",
            source?.DisplayName ?? "", description: source?.Description ?? "",
            note: "Creates an independent roster entry, unlocked by default. The default suit uses CharacterID.CharacterID. Existing suits and native gameplay donors remain unchanged. Story cutscenes are not supported.");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await SaveNewCharacterRecipeAsync(source, dialog.DisplayNameValue, dialog.TechnicalId, dialog.TechnicalId, "", dialog.DescriptionValue);
    }

    private async Task ChooseCustomCharacterBaseAsync()
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("choose a custom character")) return;
        var service = new SuitProjectService(_projectRootText.Text.Trim());
        using var picker = new LoadSuitDialog(service.ListProjects().Where(project => project.IsCharacter).ToArray(),
            libraryTitle: "Your characters", libraryDescription: "Choose a saved character to create a new, independently editable suit. Its default is included when building.");
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedPath is null) return;
        try
        {
            var owner = service.LoadProject(picker.SelectedPath) ?? throw new InvalidDataException("Character project could not be read.");
            await CreateCharacterSuitAsync(owner);
        }
        catch (Exception ex) { Dialog.Error(this, "Could not create character suit", ex.Message); }
    }

    private async Task CreateCharacterSuitAsync(NativeSuitProject owner)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("create a character suit")) return;
        if (owner.CustomCharacter is not { IsDefinition: true } identity || !BaseEligibilityService.Evaluate(owner).IsReady)
        { Dialog.Warn(this, "Choose a character base first", "Set and save this character's native base before creating its additional suits."); return; }
        using var dialog = new CharacterIdentityDialog("New suit for " + owner.DisplayName, "Suit ID (permanent; e.g. Unmasked)",
            note: "This copies the current design into a separate suit. Edit its appearance and abilities independently.", ownerId: identity.CharacterId);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await SaveNewCharacterRecipeAsync(owner, dialog.DisplayNameValue, identity.CharacterId, dialog.TechnicalId, owner.SlotId, dialog.DescriptionValue);
    }

    private async Task CreateSuitFromSavedCharacterPathAsync(string path)
    {
        try
        {
            var owner = new SuitProjectService(_projectRootText.Text.Trim()).LoadProject(path)
                ?? throw new InvalidDataException("The saved character could not be read.");
            await CreateCharacterSuitAsync(owner);
        }
        catch (Exception ex) { Dialog.Error(this, "Character base", ex.Message); }
    }

    private async Task SaveNewCharacterRecipeAsync(NativeSuitProject? source, string name, string ownerId, string variantId, string definitionId, string description)
    {
        if (_creatingCharacterRecipe) { Dialog.Info(this, "Character setup", "Another character is still being prepared. Please wait for it to finish."); return; }
        _creatingCharacterRecipe = true;
        source = source is null ? null : CloneProjectForPackagePreparation(source);
        var service = new SuitProjectService(_projectRootText.Text.Trim());
        NativeSuitProject? candidate = null;
        var ownsNewOutputs = false;
        ProgressDialog? progress = null;
        try
        {
            candidate = CustomCharacterProjectService.CreateRecipe(source, name, ownerId, variantId, definitionId);
            candidate.Description = description;
            if (File.Exists(service.ProjectPathForSlot(candidate.SlotId)) || Directory.Exists(service.ProjectOutputDirectory(candidate)))
                throw new InvalidDataException("That character/suit ID already has a saved project or output. Choose another ID; no existing project will be overwritten.");
            CustomCharacterProjectService.RequireAvailableIdentity(candidate, service);
            ownsNewOutputs = true;
            if (!_batchMode) progress = new ProgressDialog(this, "Preparing your character");
            var updates = new Progress<string>(message => { if (progress is { IsDisposed: false }) progress.Report(message); });
            if (source is not null)
            {
                NormalizeGeneratedUimdIconRecipes(candidate);
                await Task.Run(() =>
                {
                    CustomCharacterProjectService.CopyAuthoringSources(source, candidate, service);
                    CustomCharacterProjectService.CopyVisualAssets(source, candidate, service, message => { AppendLog(message); ((IProgress<string>)updates).Report(message); });
                });
            }
            service.SaveProject(candidate);
            AppendLog($"Created {(CustomCharacterProjectService.IsCharacter(candidate) ? "character" : "character suit")} '{name}': {candidate.PawnTag} (unlocked by default). Source project unchanged.");
            if (BaseEligibilityService.Evaluate(candidate).IsReady)
            {
                progress?.Report("Preparing the character's playable and companion assets…");
                await RebuildGraftStageFromDeclarativeAsync(candidate, service.ProjectRoot, persistProject: true);
            }
        }
        catch (Exception ex)
        {
            progress?.Dispose(); progress = null;
            AppendLog("Character creation: " + ex.Message);
            if (candidate is not null && ownsNewOutputs)
            {
                try
                {
                    var recovery = CustomCharacterProjectService.ArchiveFailedCreation(candidate, service);
                    if (recovery is not null) AppendLog("Incomplete new-character outputs were archived (recoverable) at " + recovery + ". The ID can be retried.");
                }
                catch (Exception recoveryError) { AppendLog("New-character recovery warning: " + recoveryError.Message); }
            }
            Dialog.Error(this, "Character setup needs attention", ex.Message +
                (candidate is not null && File.Exists(service.ProjectPathForSlot(candidate.SlotId)) ? "\n\nThe new project is saved. Reopen it to repair the source or base; the original project was not changed." : ""));
            return;
        }
        finally { progress?.Dispose(); _creatingCharacterRecipe = false; }
        _projectService = service;
        LoadProjectIntoUi(candidate);
        SelectComboValue(_toyboxCategoryCombo, BaseEligibilityService.Evaluate(candidate).IsReady ? "Home" : "Base");
        RefreshToyboxTiles();
    }

    private void EditCustomCharacterIdentity()
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("editing character identity") || _currentProject?.CustomCharacter is not { } identity) return;
        using var dialog = new CharacterIdentityDialog(identity.IsDefinition ? "Character identity" : "Character suit identity",
            identity.IsDefinition ? "Character ID (fixed)" : "Suit ID (fixed)", _currentProject.DisplayName,
            identity.IsDefinition ? identity.CharacterId : identity.VariantId, _currentProject.Description, lockedId: true,
            note: $"{_currentProject.ProgressTag}\nOwner and IDs are fixed to keep saves and child suits valid. Story cutscenes are not supported.",
            ownerId: identity.IsDefinition ? null : identity.CharacterId);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _currentProject.DisplayName = dialog.DisplayNameValue; _currentProject.Description = dialog.DescriptionValue;
        CustomCharacterProjectService.ApplyIdentity(_currentProject);
        _suitNameText.Text = _currentProject.DisplayName; _descriptionText.Text = _currentProject.Description;
        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); }
        catch (Exception ex) { Dialog.Error(this, "Identity was not saved", ex.Message); return; }
        RefreshToyboxTiles(); RefreshInspector();
    }
}
