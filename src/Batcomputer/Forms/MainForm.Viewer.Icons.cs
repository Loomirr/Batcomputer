namespace Batcomputer;

public sealed partial class MainForm
{
    private bool _viewerIconsApplying;
    private async Task ApplyViewerCharacterIconsAsync(PreviewCharacterIconsRequestedEventArgs request)
    {
        var viewer = _viewer; var viewedProject = _viewerProject; var generation = _viewerLoadGeneration;
        if (viewer is null) return;
        if (_viewerIconsApplying) { await viewer.NotifySuitIconApplyAsync(false, "An icon assignment is already running."); return; }
        _viewerIconsApplying = true;
        try
        {
            CharacterIconAssignmentService.Validate(request.Icons);
            if (viewedProject is null || string.IsNullOrWhiteSpace(viewedProject.SlotId) ||
                request.LayoutKey != ViewerLayoutService.SuitKey(viewedProject))
                throw new InvalidOperationException("Open a saved suit in the viewer before assigning icons.");
            var project = ResolveViewerProjectForEdit(viewedProject, _currentProject)!;
            var roles = string.Join(", ", request.Icons.Keys);
            if (!Dialog.Confirm(this, "Assign character icons?", $"Create and assign {roles} for {project.DisplayName}?\n\n" +
                "Existing textures are kept. All requested icons must cook successfully before any are assigned. " +
                "This saves the project only; it does not build or install a mod.", confirmText: "Create and assign"))
            { await viewer.NotifySuitIconApplyAsync(false, "Cancelled; your icons are unchanged."); return; }
            if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("assign character icons"))
                throw new InvalidOperationException("Wait for the saved project to finish restoring.");
            void CheckViewedProject()
            {
                if (generation != _viewerLoadGeneration || !ReferenceEquals(viewedProject, _viewerProject) ||
                    !ReferenceEquals(project, ResolveViewerProjectForEdit(viewedProject, _currentProject)))
                    throw new InvalidOperationException("The viewed suit changed. Nothing was assigned; reopen the studio.");
            }
            CheckViewedProject();
            if (ReferenceEquals(project, _currentProject)) ReadFieldsIntoProject(project);
            var slotId = project.SlotId;
            var modFolder = CharacterIconAssignmentService.TextureModFolder(project);
            var projectRoot = AppSettings.Current.EffectiveProjectRoot();
            if (!await EnsureTextureCookTemplatesAsync(projectRoot)) throw new InvalidOperationException("Refresh game assets to prepare the native icon templates.");
            CheckViewedProject();
            var id = Guid.NewGuid().ToString("N"); var slotIndex = NextTextureSlotIndex(project);
            var entries = new Dictionary<string, GeneratedTextureEntry>(StringComparer.Ordinal);
            foreach (var (role, png) in request.Icons)
            {
                var size = CharacterIconAssignmentService.Size(role);
                var template = TextureCookTemplateService.TemplateJsonPath(projectRoot, size == 256
                    ? TextureCookTemplateService.NativeSuitIconTemplateFolder : TextureCookTemplateService.NativeCharacterIconTemplateFolder);
                var name = $"IconStudio_{role}_{id}";
                var kind = size == 256 ? "Suit selector icon" : "Character icon";
                var package = TexturePackagePathFromUserName(template, name, slotIndex, modFolder, project.SlotId, kind);
                var output = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureImports", MakeSafePackageBaseName(project.SlotId), name);
                _viewerStatus!.Text = $"{project.DisplayName}: cooking {role} icon…";
                var cooked = await Task.Run(() => SuitIconDryRunService.CookToPackage(projectRoot, png, output, package, size));
                CheckViewedProject();
                var preset = new TextureCookPreset(size == 256 ? NativeUimdIconCookProfile : NativeCharacterIconCookProfile,
                    $"Native {size}px BC7 icon", template, size, size, "PF_BC7", TextureProfileSafety.Verified, "Verified native UI icon layout.");
                entries.Add(role, BuildTextureEntryFromSummary(output, cooked.SourcePng, template, DefaultTextureSourceRawRoot(projectRoot),
                    package, MakeSafePackageBaseName($"Texture_{name}_{slotIndex:00000}_P"), "Character icon studio", kind, preset));
                slotIndex++;
            }
            CheckViewedProject();
            // Cooking yields to the UI. Keep any edits made while it ran instead of replacing
            // them with the initial field snapshot when the freshly assigned icons are shown.
            if (ReferenceEquals(project, _currentProject)) ReadFieldsIntoProject(project);
            if (project.SlotId != slotId) throw new InvalidOperationException("The suit identity changed while cooking. No icons were assigned.");
            CharacterIconAssignmentService.AssignAndSave(project, entries, () => new SuitProjectService(projectRoot).SaveProject(project));
            foreach (var (role, entry) in entries) AppendLog($"Studio {role} icon assigned: {entry.PackagePath}");
            if (ReferenceEquals(project, _currentProject)) { ApplyProjectToFields(project); RefreshToyboxTiles(); }
            _viewerStatus!.Text = $"{project.DisplayName}: {entries.Count} icon(s) saved. Rebuild the mod to use them in-game.";
            await viewer.NotifySuitIconApplyAsync(true, $"{entries.Count} icon(s) created and assigned. Project saved; rebuild the mod for in-game use.");
        }
        catch (Exception ex)
        {
            AppendLog("Character icons were not assigned: " + ex.Message);
            if (generation == _viewerLoadGeneration) await viewer.NotifySuitIconApplyAsync(false, "Icons were not assigned: " + ex.Message);
        }
        finally { _viewerIconsApplying = false; }
    }
}
