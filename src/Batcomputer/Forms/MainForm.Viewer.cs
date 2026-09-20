namespace Batcomputer;

/// <summary>
/// The dedicated 3D workspace: pick a character - your own suit, a playable blueprint, or a cutscene
/// one - and render it with the game's materials, parts and facial expressions.
/// </summary>
public sealed partial class MainForm
{
    private ModelPreviewControl? _viewer;
    private ListBox? _viewerList;
    private SearchBox? _viewerSearch;
    private Label? _viewerStatus;
    private Button? _viewerLoadButton;
    private FlowLayoutPanel? _viewerSources;
    private ThemedDropDown? _viewerFamily;
    private Label? _viewerCount;
    private string _viewerFamilySource = "";
    private string _viewerSource = "My library";
    private List<CharacterCatalogService.Entry> _viewerEntries = new();
    private TableLayoutPanel? _viewerHostLayout;
    private Control? _viewerPanel;
    private int _viewerLoadGeneration;
    private NativeSuitProject? _viewerProject;
    private bool _viewerCustomMeshBakeInProgress;
    private readonly SemaphoreSlim _viewerCustomMeshPlacementGate = new(1, 1);
    private int _viewerCustomMeshPlacementRequest;
    private readonly Dictionary<string, int> _viewerCustomMeshRequests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the viewer once and hosts it in the dedicated full-width workspace.</summary>
    private void ShowViewerPanel()
    {
        if (_viewerHostLayout is null)
        {
            return;
        }
        if (_viewerPanel is null)
        {
            _viewerPanel = CreateViewerPanel();
            _viewerPanel.Dock = DockStyle.Fill;
            _viewerHostLayout.Controls.Add(_viewerPanel, 0, 0);
        }
        _toyboxTileFlow.Visible = false;
        _toyboxTileGrid.Visible = false;
        _viewerPanel.Visible = true;
        _viewerPanel.BringToFront();
        SetViewerWorkspaceExpanded(true);
        _ = _viewer?.ResumeRendererAsync();
        RefreshViewerCustomSuits();
    }

    private void HideViewerPanel()
    {
        _viewerLoadGeneration++;
        if (_viewerLoadButton is not null)
        {
            _viewerLoadButton.Enabled = true;
        }
        _viewer?.ReleaseRenderer();
        if (_viewerPanel is not null)
        {
            _viewerPanel.Visible = false;
        }
        SetViewerWorkspaceExpanded(false);
    }

    /// <summary>Shows or hides the dedicated viewer workspace without reshaping the suit editor.</summary>
    private void SetViewerWorkspaceExpanded(bool expanded)
    {
        if (_viewerWorkspaceHost is not null)
        {
            _viewerWorkspaceHost.Visible = expanded;
        }
    }

    /// <summary>Builds the viewer screen: source list on the left, render on the right.</summary>
    private Control CreateViewerPanel()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.WindowBg,
            Padding = new Padding(10),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 278));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- left: source picker + list ---------------------------------------
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = Theme.PanelBg,
            Padding = new Padding(10),
        };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label { Text = "CHARACTER LIBRARY", AutoSize = true, Font = Theme.Eyebrow,
            ForeColor = Theme.Gold, Margin = new Padding(2, 6, 0, 14) };
        left.Controls.Add(heading, 0, 0);
        // Segmented source switch: three flat buttons that behave as one control.
        _viewerSources = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 30, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0),
        };
        foreach (var name in new[] { "My library", "Playable", "Cutscene" })
        {
            var b = new Button
            {
                Text = name, Width = 79, Height = 26, FlatStyle = FlatStyle.Flat, Font = Theme.Caption,
                BackColor = Theme.PanelBg, ForeColor = Theme.OnDarkMuted, Margin = new Padding(0, 0, 2, 0),
                Tag = name,
            };
            b.FlatAppearance.BorderColor = Theme.PanelBg;
            b.Click += (s2, _) =>
            {
                _viewerSource = (string)((Button)s2!).Tag!;
                SyncViewerSourceButtons();
                RefreshViewerList();
            };
            _viewerSources.Controls.Add(b);
        }
        left.Controls.Add(_viewerSources, 0, 1);
        SyncViewerSourceButtons();

        _viewerSearch = new SearchBox { Dock = DockStyle.Top, Height = 34, Margin = new Padding(0, 10, 0, 8), AccessibleName = "Search characters and suits" };
        _viewerSearch.TextChanged += (_, _) => RefreshViewerList();
        left.Controls.Add(_viewerSearch, 0, 2);
        _viewerFamily = new ThemedDropDown { Dock = DockStyle.Top, Height = 34, Placeholder = "All characters", AccessibleName = "Character filter" };
        _viewerFamily.SelectedIndexChanged += (_, _) => RefreshViewerList();
        left.Controls.Add(_viewerFamily, 0, 3);
        _viewerCount = new Label { Dock = DockStyle.Top, Height = 28, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft };
        left.Controls.Add(_viewerCount, 0, 4);

        _viewerList = new CharacterBrowserList
        {
            Dock = DockStyle.Fill,
            Font = Theme.Body,
            IntegralHeight = false,
        };
        _viewerList.DoubleClick += (_, _) => LoadSelectedViewerCharacter();
        _viewerList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; LoadSelectedViewerCharacter(); } };
        left.Controls.Add(_viewerList, 0, 5);

        _viewerLoadButton = new Button
        {
            Dock = DockStyle.Top, Height = 40, Text = "Open in workshop", FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelBg, ForeColor = Theme.Gold, Font = Theme.Body, Margin = new Padding(0, 6, 0, 0),
        };
        Theme.StyleGoldButton(_viewerLoadButton);
        _viewerLoadButton.Click += (_, _) => LoadSelectedViewerCharacter();
        left.Controls.Add(_viewerLoadButton, 0, 6);

        root.Controls.Add(left, 0, 0);

        // --- right: the render + status ---------------------------------------
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent,
            Padding = new Padding(10, 0, 0, 0),
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _viewer = new ModelPreviewControl { Dock = DockStyle.Fill };
        _viewer.PlacementSaveRequested += (_, args) => SaveViewerPlacement(args);
        right.Controls.Add(_viewer, 0, 0);

        _viewerStatus = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 24, ForeColor = Theme.OnDarkMuted,
            Font = Theme.Caption, TextAlign = ContentAlignment.MiddleLeft, Text = "No character loaded.",
        };
        right.Controls.Add(_viewerStatus, 0, 1);

        root.Controls.Add(right, 1, 0);

        LoadViewerCatalog();
        return root;
    }

    /// <summary>Reads the catalogue (cached after the first pak scan) and fills the list.</summary>
    private void LoadViewerCatalog()
    {
        try
        {
            var settings = AppSettings.Current;
            var paks = settings.EffectiveGamePaksRoot();
            var usmap = settings.EffectiveUsmapPath() ?? string.Empty;
            _viewerEntries = CharacterCatalogService.Load(paks, usmap);
            _viewerEntries.AddRange(CharacterCatalogService.CustomSuits(settings.EffectiveProjectRoot()));
        }
        catch (Exception ex)
        {
            _viewerStatus!.Text = "Could not read the character list: " + ex.Message.Split('\n')[0];
        }
        RefreshViewerList();
    }

    /// <summary>Refreshes only local suit projects; the expensive pak catalogue remains cached.</summary>
    private void RefreshViewerCustomSuits()
    {
        if (_viewerList is null)
        {
            return;
        }

        _viewerEntries.RemoveAll(entry => entry.Origin == CharacterCatalogService.Source.CustomSuit);
        _viewerEntries.AddRange(CharacterCatalogService.CustomSuits(AppSettings.Current.EffectiveProjectRoot()));
        RefreshViewerList();
    }

    private void SyncViewerSourceButtons()
    {
        foreach (var c in _viewerSources?.Controls.OfType<Button>() ?? Enumerable.Empty<Button>())
        {
            var on = string.Equals((string)c.Tag!, _viewerSource, StringComparison.Ordinal);
            c.ForeColor = on ? Theme.Gold : Theme.OnDarkMuted;
            c.BackColor = on ? Theme.CardHi : Theme.PanelBg;
            c.FlatAppearance.BorderColor = on ? Theme.GoldDim : Theme.PanelBg;
        }
    }

    private void RefreshViewerList()
    {
        if (_viewerList is null)
        {
            return;
        }
        var source = _viewerSource switch
        {
            "Playable" => CharacterCatalogService.Source.Playable,
            "Cutscene" => CharacterCatalogService.Source.Cutscene,
            _ => CharacterCatalogService.Source.CustomSuit,
        };
        var needle = _viewerSearch?.Text?.Trim() ?? string.Empty;
        if (_viewerFamily is not null) _viewerFamily.Visible = source != CharacterCatalogService.Source.CustomSuit;
        if (_viewerFamily is not null && _viewerFamilySource != _viewerSource)
        {
            _viewerFamilySource = _viewerSource; // Set before selection events recurse.
            _viewerFamily.Items.Clear();
            _viewerFamily.Items.Add("All characters");
            foreach (var family in _viewerEntries.Where(e => e.Origin == source).Select(e => e.Name.Split(' ', 2)[0]).Distinct().OrderBy(n => n))
                _viewerFamily.Items.Add(family);
            _viewerFamily.SelectedIndex = 0;
        }
        var familyFilter = source != CharacterCatalogService.Source.CustomSuit && _viewerFamily?.SelectedIndex > 0 ? _viewerFamily.SelectedItem?.ToString() : null;
        var selected = _viewerList.SelectedItem as CharacterCatalogService.Entry;
        var matches = _viewerEntries.Where(e => e.Origin == source)
            .Where(e => familyFilter is null || e.Name.Split(' ', 2)[0] == familyFilter)
            .Where(e => needle.Length == 0 || e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                CharacterBrowserList.DisplayName(e.Name).Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.ProjectId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.CharacterId.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();

        _viewerList.BeginUpdate();
        _viewerList.Items.Clear();
        foreach (var e in matches)
        {
            _viewerList.Items.Add(e);
        }
        _viewerList.DisplayMember = nameof(CharacterCatalogService.Entry.Name);
        _viewerList.EndUpdate();

        if (_viewerList.Items.Count > 0)
        {
            var retained = selected is null ? -1 : matches.FindIndex(e => e == selected);
            _viewerList.SelectedIndex = retained >= 0 ? retained : 0;
        }
        else if (source == CharacterCatalogService.Source.CustomSuit)
        {
            _viewerStatus!.Text = "No saved characters or suits yet — create one in Characters or Suits.";
        }
        if (_viewerCount is not null) _viewerCount.Text = matches.Count == 0 ? "No matches · try another filter" :
            source == CharacterCatalogService.Source.CustomSuit
                ? $"{matches.Count(e => e.IsCharacter)} characters · {matches.Count(e => !e.IsCharacter)} suits"
                : $"{matches.Count} entries · double-click to open";
        if (_viewerLoadButton is not null) _viewerLoadButton.Enabled = matches.Count > 0;
    }

    private void LoadSelectedViewerCharacter()
    {
        if (_viewerList?.SelectedItem is not CharacterCatalogService.Entry entry)
        {
            return;
        }
        if (entry.Origin == CharacterCatalogService.Source.CustomSuit)
        {
            var loadedProject = string.IsNullOrWhiteSpace(entry.ProjectPath)
                ? null
                : new SuitProjectService(AppSettings.Current.EffectiveProjectRoot()).LoadProject(entry.ProjectPath);
            if (loadedProject is null)
            {
                _viewerStatus!.Text = $"{entry.Name}: the saved suit project could not be read.";
                _viewer?.ShowMessage("Could not read this suit project.");
                return;
            }
            // The dedicated viewer reads projects from disk, while the editor keeps the selected
            // suit in memory. Reuse that live instance when both represent the same saved slot.
            // Otherwise a viewer bake can update one object and a later part removal can save the
            // stale editor object over it, restoring the custom mesh's old transform.
            var project = ResolveViewerProjectForEdit(loadedProject, _currentProject)!;
            ShowCharacterInViewer(string.Empty, entry.Name, project);
            return;
        }
        ShowCharacterInViewer(
            entry.ObjectPath,
            entry.Name,
            allowBaseGameRedBrickPreview: entry.Origin == CharacterCatalogService.Source.Playable);
    }

    /// <summary>Builds and shows a character; used by the tab and by "View in 3D" on My character.</summary>
    private async void ShowCharacterInViewer(
        string objectPath,
        string label,
        NativeSuitProject? project = null,
        bool allowBaseGameRedBrickPreview = false)
    {
        var loadGeneration = ++_viewerLoadGeneration;
        var viewer = _viewer;
        if (viewer is null)
        {
            return;
        }
        _viewerProject = project;
        if (project is not null && EnsureCrossKindHeadGraftHidesBaseHead(project))
        {
            try
            {
                (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(project);
                AppendLog($"Viewer: saved Head:0 removal for cross-kind head graft on '{project.DisplayName}'.");
            }
            catch (Exception ex)
            {
                AppendLog($"Viewer: could not save cross-kind head removal: {ex.Message}");
            }
        }
        viewer.ShowMessage($"Building {label}…");
        _viewerStatus!.Text = $"Building {label} — decoding meshes, materials and parts…";
        _viewerLoadButton!.Enabled = false;
        try
        {
            var settings = AppSettings.Current;
            var paks = settings.EffectiveGamePaksRoot();
            var usmap = settings.EffectiveUsmapPath() ?? string.Empty;
            var previewDiagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void FlushPreviewDiagnostics()
            {
                while (previewDiagnostics.TryDequeue(out var message))
                {
                    AppendLog("Viewer: " + message);
                }
            }
            string folder;
            if (project is null)
            {
                folder = await Task.Run(() => ModelPreviewService.BuildPreviewCharacter(
                    paks,
                    usmap,
                    objectPath,
                    previewOptions: new ModelPreviewService.CharacterPreviewOptions
                    {
                        RedBrickTints = allowBaseGameRedBrickPreview
                            ? ViewerBaseGameRedBrickPaletteService.LoadPreviewTints()
                            : [],
                    }));
            }
            else
            {
                // Custom-project previews overlay GraftedPartStage and therefore read the same
                // loose packages that a saved-suit restore deletes and rewrites. Hold the shared
                // stage gate across the background build so neither operation can make the other
                // trip UAssetAPI's exclusive file-open behavior. Waiting remains asynchronous.
                await RebuildGate.WaitAsync();
                try
                {
                    // A hidden/replaced viewer may have gone stale while waiting behind a rebuild.
                    if (loadGeneration != _viewerLoadGeneration || _viewerPanel?.Visible != true)
                    {
                        return;
                    }

                    folder = await Task.Run(() => ModelPreviewService.BuildPreviewSuit(
                        paks,
                        usmap,
                        project,
                        settings.EffectiveProjectRoot(),
                        previewDiagnostics.Enqueue,
                        ViewerBaseGameRedBrickPaletteService.LoadPreviewTints()));
                }
                finally
                {
                    RebuildGate.Release();
                }
            }
            FlushPreviewDiagnostics();
            if (loadGeneration != _viewerLoadGeneration || _viewerPanel?.Visible != true)
            {
                return;
            }
            await viewer.ShowFolderAsync(folder);
            _viewerStatus.Text = project is null
                ? $"{label} - drag to orbit, scroll to zoom."
                : $"{label} - drag to orbit, scroll to zoom. Custom mesh preview changes save to the suit automatically; choose Bake to game before testing them in-game.";
        }
        catch (Exception ex)
        {
            var why = ex.Message.Split('\n')[0];
            if (loadGeneration == _viewerLoadGeneration)
            {
                viewer.ShowMessage("Could not build this character.\n\n" + why);
                _viewerStatus.Text = "Failed: " + why;
            }
        }
        finally
        {
            if (loadGeneration == _viewerLoadGeneration)
            {
                _viewerLoadButton!.Enabled = true;
            }
            else
            {
                viewer.TrimInactiveMemory();
            }
        }
    }

    private async void SaveViewerPlacement(PreviewPlacementSaveRequestedEventArgs args)
    {
        var component = args.Component.Trim();
        if (component.Length == 0 || string.IsNullOrWhiteSpace(args.LayoutKey))
        {
            return;
        }

        if (args.CustomMeshTransform is not null)
        {
            if (_viewerCustomMeshBakeInProgress && !args.CustomMeshBakeRequested)
            {
                // Ignore debounced/pagehide drafts emitted after Bake has begun. The bake request
                // already carries the authoritative transform and is rebuilding from that recipe.
                return;
            }
            var viewedProject = _viewerProject;
            if (viewedProject is null ||
                !args.LayoutKey.Equals(
                    ViewerLayoutService.SuitKey(viewedProject),
                    StringComparison.OrdinalIgnoreCase))
            {
                // A retiring WebView can deliver a delayed pagehide/draft message after another
                // character has started loading. Never apply it to the newly selected project.
                return;
            }

            var project = ResolveViewerProjectForEdit(viewedProject, _currentProject)!;
            var customMesh = !string.IsNullOrWhiteSpace(args.CustomMeshId)
                ? project.CustomStaticMeshes.FirstOrDefault(mesh =>
                    mesh.Id.Equals(args.CustomMeshId, StringComparison.OrdinalIgnoreCase))
                : project.CustomStaticMeshes.FirstOrDefault(mesh =>
                    !string.IsNullOrWhiteSpace(mesh.ResolvedComponent) &&
                    mesh.ResolvedComponent.Equals(component, StringComparison.OrdinalIgnoreCase));
            if (customMesh is null)
            {
                _viewerStatus!.Text = "That custom mesh is no longer part of this suit.";
                return;
            }
            var placementRequest = ++_viewerCustomMeshPlacementRequest;
            _viewerCustomMeshRequests[args.LayoutKey + "|" + customMesh.Id] = placementRequest;
            await SaveCustomStaticMeshPlacementAsync(
                project!,
                customMesh,
                args,
                _viewerLoadGeneration,
                placementRequest);
            return;
        }

        var isZero = Math.Abs(args.OffsetX) < 0.00001f &&
                     Math.Abs(args.OffsetY) < 0.00001f &&
                     Math.Abs(args.OffsetZ) < 0.00001f;
        if (!ViewerLayoutService.Save(
                AppSettings.Current.EffectiveProjectRoot(),
                args.LayoutKey,
                component,
                args.OffsetX,
                args.OffsetY,
                args.OffsetZ,
                args.UvChannel))
        {
            _viewerStatus!.Text = "Could not save that viewer alignment.";
            return;
        }

        _viewerStatus!.Text = isZero && args.UvChannel is null
            ? $"{component}: viewer overrides reset."
            : $"{component}: viewer alignment and UV saved.";
    }

    private async Task SaveCustomStaticMeshPlacementAsync(
        NativeSuitProject project,
        CustomStaticMeshImport mesh,
        PreviewPlacementSaveRequestedEventArgs args,
        int expectedViewerGeneration,
        int placementRequest)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("save the custom-mesh placement"))
        {
            if (_viewer is not null && expectedViewerGeneration == _viewerLoadGeneration && args.CustomMeshTransform is { } rejected)
            {
                if (args.CustomMeshBakeRequested) await _viewer.NotifyCustomMeshBakeFailedAsync("The project stage is not ready.");
                else await _viewer.NotifyCustomMeshDraftAsync(args.Component, rejected, "The project stage is not ready.");
            }
            return;
        }

        if (args.CustomMeshTransform is not { } transform)
        {
            return;
        }

        var isBake = args.CustomMeshBakeRequested;
        if (isBake && _viewerCustomMeshBakeInProgress)
        {
            return;
        }

        var workspaceWasEnabled = _mainWorkspaceHost.Enabled;
        if (isBake)
        {
            // Set this before the first await so debounced/pagehide drafts emitted by the WebView
            // after the Bake click are ignored rather than queued behind the authoritative bake.
            _viewerCustomMeshBakeInProgress = true;
            _mainWorkspaceHost.Enabled = false;
        }

        await _viewerCustomMeshPlacementGate.WaitAsync();
        try
        {
            if (!isBake && placementRequest != _viewerCustomMeshRequests.GetValueOrDefault(args.LayoutKey + "|" + mesh.Id))
            {
                return;
            }

            var previousTransform = CaptureViewerCustomMeshTransform(mesh);
            if (!isBake)
            {
                ApplyViewerCustomMeshTransform(mesh, transform);
                try
                {
                    var projectService = _projectService ??= new SuitProjectService(_projectRootText.Text.Trim());
                    // A preview save changes the authoritative declarative recipe, while the current
                    // cooked mesh still has the prior transform. Establish the fail-closed packaging
                    // sentinel before persisting that newer recipe.
                    await MarkDeclarativeStageIncompleteAsync(project, projectService.ProjectRoot);
                    if (placementRequest != _viewerCustomMeshRequests.GetValueOrDefault(args.LayoutKey + "|" + mesh.Id))
                    {
                        // A newer draft or Bake request arrived while the marker was being written.
                        // Leave the fail-closed marker in place and let that latest request own the
                        // persisted recipe instead of briefly saving this stale transform.
                        ApplyViewerCustomMeshTransform(mesh, previousTransform);
                        return;
                    }
                    projectService.SaveProject(project);
                    _viewerProject = project;
                    _viewerStatus!.Text = $"{mesh.DisplayName}: preview transform saved. Use Bake to game before testing it in-game.";
                    if (_viewer is not null && expectedViewerGeneration == _viewerLoadGeneration)
                        await _viewer.NotifyCustomMeshDraftAsync(args.Component, transform);
                }
                catch (Exception ex)
                {
                    // The project JSON still represents the prior transform. Keep the live editor in
                    // lockstep with it; an incomplete marker that was written before a failed save is
                    // intentionally retained so packaging remains blocked.
                    ApplyViewerCustomMeshTransform(mesh, previousTransform);
                    _viewerStatus!.Text = $"Could not save {mesh.DisplayName}: {ex.Message.Split('\n')[0]}";
                    if (_viewer is not null && expectedViewerGeneration == _viewerLoadGeneration)
                        await _viewer.NotifyCustomMeshDraftAsync(args.Component, transform, ex.Message.Split('\n')[0]);
                }
                return;
            }

            ApplyViewerCustomMeshTransform(mesh, transform);
            var projectSaved = false;
            try
            {
                var projectService = _projectService ??= new SuitProjectService(_projectRootText.Text.Trim());
                projectService.SaveProject(project);
                projectSaved = true;
                // Keep subsequent viewer messages and editor actions on the same declarative recipe.
                // This assignment is especially important when the viewer initially loaded a disk
                // clone before ResolveViewerProjectForEdit selected the active editor instance.
                _viewerProject = project;
                _viewerStatus!.Text = $"{mesh.DisplayName}: baking its transform, then rebuilding the custom mesh…";
                await RebuildGraftStageFromDeclarativeAsync(project, projectService.ProjectRoot);
                projectService.SaveProject(project);
                RecordChange(project, "Parts", mesh.DisplayName,
                    $"custom mesh scale {mesh.Scale:0.###}; position {mesh.OffsetX:0.###}, {mesh.OffsetY:0.###}, {mesh.OffsetZ:0.###}; rotation {mesh.RotationPitch:0.###}, {mesh.RotationYaw:0.###}, {mesh.RotationRoll:0.###}",
                    status: "staged");
                if (expectedViewerGeneration == _viewerLoadGeneration &&
                    ReferenceEquals(_viewerProject, project))
                {
                    _viewerStatus.Text = $"{mesh.DisplayName}: transform baked to the suit. Reloading preview…";
                    ShowCharacterInViewer(string.Empty, project.DisplayName, project);
                }
            }
            catch (Exception ex)
            {
                if (!projectSaved)
                {
                    // No declarative intent reached disk and no rebuild marker was established, so
                    // keep the live editor aligned with the still-certified prior stage.
                    ApplyViewerCustomMeshTransform(mesh, previousTransform);
                }
                _viewerStatus!.Text = $"Could not save {mesh.DisplayName}: {ex.Message.Split('\n')[0]}";
                if (_viewer is not null && expectedViewerGeneration == _viewerLoadGeneration)
                    await _viewer.NotifyCustomMeshBakeFailedAsync(ex.Message.Split('\n')[0]);
            }
        }
        finally
        {
            if (isBake)
            {
                _viewerCustomMeshBakeInProgress = false;
                _mainWorkspaceHost.Enabled = workspaceWasEnabled;
            }
            _viewerCustomMeshPlacementGate.Release();
        }
    }

    /// <summary>
    /// Returns the live editor object when the viewer's disk-loaded object represents the same
    /// saved suit. SlotId is the persisted project identity because SuitProjectService derives the
    /// project JSON path and generated-stage directory from it.
    /// </summary>
    internal static NativeSuitProject? ResolveViewerProjectForEdit(
        NativeSuitProject? viewerProject,
        NativeSuitProject? activeProject)
    {
        if (viewerProject is null)
        {
            return null;
        }
        if (activeProject is not null &&
            (ReferenceEquals(viewerProject, activeProject) ||
             (!string.IsNullOrWhiteSpace(viewerProject.SlotId) &&
              viewerProject.SlotId.Equals(activeProject.SlotId, StringComparison.OrdinalIgnoreCase))))
        {
            return activeProject;
        }
        return viewerProject;
    }

    internal static void ApplyViewerCustomMeshTransform(
        CustomStaticMeshImport mesh,
        PreviewCustomMeshTransform transform)
    {
        mesh.Scale = transform.Scale;
        mesh.OffsetX = transform.OffsetX;
        mesh.OffsetY = transform.OffsetY;
        mesh.OffsetZ = transform.OffsetZ;
        mesh.RotationPitch = transform.RotationPitch;
        mesh.RotationYaw = transform.RotationYaw;
        mesh.RotationRoll = transform.RotationRoll;
    }

    internal static PreviewCustomMeshTransform CaptureViewerCustomMeshTransform(CustomStaticMeshImport mesh) =>
        new(
            mesh.Scale,
            mesh.OffsetX,
            mesh.OffsetY,
            mesh.OffsetZ,
            mesh.RotationPitch,
            mesh.RotationYaw,
            mesh.RotationRoll);

    /// <summary>Jumps to the viewer tab and loads the current suit with its saved edits.</summary>
    private void ViewCurrentSuitIn3D()
    {
        SelectWorkspaceFolder(WorkspaceFolder.Viewer);
        if (_currentProject?.PlayableTemplate is null)
        {
            _viewer?.ShowMessage("Pick a base character first, then view the suit in 3D.");
            return;
        }
        ShowCharacterInViewer(string.Empty, _currentProject.DisplayName, _currentProject);
    }
}
