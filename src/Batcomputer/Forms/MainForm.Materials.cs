using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Material creation, replacement, and slot assignment.
/// </summary>
public sealed partial class MainForm
{
    // Saved-suit loads deliberately keep their stage restore fire-and-forget so the WinForms UI
    // remains responsive. These counters keep stage-backed view refreshes out of the restore
    // window, while the generation prevents an older suit's completion from becoming the final
    // refresh for a newer selection.
    private int _loadedProjectSelectionGeneration;
    private int _activeLoadedProjectStageRestores;
    private int _currentLoadedProjectStageRestoreGeneration;
    private bool _loadedProjectStageRefreshPending;
    private TaskCompletionSource<bool>? _loadedProjectStageRestoresIdle;

    private void LoadProjectIntoUi(NativeSuitProject project)
    {
        var loadGeneration = unchecked(++_loadedProjectSelectionGeneration);
        if (loadGeneration == 0)
        {
            loadGeneration = ++_loadedProjectSelectionGeneration;
        }
        // Every selection invalidates the previous selection's right to perform a final refresh.
        // A restore started below claims this generation after all load-time normalization is done.
        _currentLoadedProjectStageRestoreGeneration = 0;

        _currentProject = project;
        MigratePartGraftInstances(project);
        if (NormalizeGeneratedUimdIconRecipes(project))
        {
            try
            {
                (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(project);
            }
            catch (Exception ex)
            {
                AppendLog($"UIMD icon recipe save warning: {ex.Message}");
            }
        }

        // Set name/mod first (fires DeriveOutputs), then pin the project's own
        // authoritative values so nothing gets recomputed away.
        _modFolderText.Text = ExtractModFolder(project.TargetPackages?.Playable) ?? "";
        _suitNameText.Text = project.DisplayName ?? "";
        _slotIdText.Text = project.SlotId;
        _displayNameText.Text = project.DisplayName ?? "";
        _descriptionText.Text = project.Description ?? "";
        _targetPlayableText.Text = project.TargetPackages?.Playable ?? "";
        _targetCutsceneText.Text = project.TargetPackages?.Cutscene ?? "";
        _targetDcmdText.Text = project.TargetPackages?.Dcmd ?? "";
        _matOutputText.Text = SuggestedMaterialOutputPackage(project.TargetPackages?.Playable);
        _lastAutoMaterialOutputPackage = _matOutputText.Text.Trim();
        _basePlayableText.Text = project.PlayableTemplate?.Uasset ?? "";
        _baseCutsceneText.Text = project.CutsceneTemplate?.Uasset ?? "";
        _baseDcmdText.Text = project.DcmdTemplate?.Uasset ?? "";
        if (!string.IsNullOrWhiteSpace(project.PackageBaseName))
        {
            // Restore the exact pak name last used for this suit so re-exports keep it.
            // Deliberately do NOT touch _lastAutoPackageBaseName: it means "the last name we
            // AUTO-DERIVED". Claiming a saved (possibly custom) name is auto-derived makes
            // DeriveOutputs - which UseAsBase calls - think it's free to re-derive, silently
            // renaming the suit's package trio (e.g. Prototype_P -> MyMod_MySuit_P).
            _packageBaseNameText.Text = project.PackageBaseName;
        }

        AppendLog($"Loaded suit: {project.DisplayName} ({project.SlotId})");

        var loadGliderComponent = ActiveGliderVisualComponent(project);
        if (!string.IsNullOrWhiteSpace(loadGliderComponent) &&
            RemoveSavedRemovalForComponent(project, loadGliderComponent))
        {
            AppendLog($"  cleared stale remove-component rule for active glider component '{loadGliderComponent}'.");
            try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(project); } catch { /* best effort */ }
        }

        var stageRoot = Path.Combine(
            new SuitProjectService(_projectRootText.Text.Trim()).ProjectOutputDirectory(project),
            "PatchedNameMapStage");
        if (!Directory.Exists(stageRoot))
        {
            AppendLog("  note: no staged content for this suit yet — use Base → Set base suit to (re)build the stage before editing materials.");
        }
        else if (ProjectRequiresCompletedGraftStage(project))
        {
            // Every declarative edit uses the same clean, transactional replay. Material-only and
            // removal-only projects must not patch a previously certified stage in place: a role
            // failure there could leave a partial payload behind an old completion marker.
            AppendLog($"  restoring {project.PartGrafts.Count} part(s) + {project.MaterialAssignments.Count} material(s) + saved removals…");
            BeginLoadedProjectStageRestore();
            _currentLoadedProjectStageRestoreGeneration = loadGeneration;
            _ = RestoreLoadedProjectStageAsync(project, _projectRootText.Text.Trim(), loadGeneration);
        }

        SelectComboValue(_toyboxCategoryCombo, "Materials");
        _session.RaiseChanged();
        RefreshToyboxTiles();
        UpdateToyboxChips();
    }

    private bool IsCurrentLoadedProjectStageRestore(NativeSuitProject project, int loadGeneration) =>
        loadGeneration == _loadedProjectSelectionGeneration &&
        loadGeneration == _currentLoadedProjectStageRestoreGeneration &&
        ReferenceEquals(_currentProject, project);

    private bool DeferStageBackedRefreshWhileLoadedProjectRestores()
    {
        if (_activeLoadedProjectStageRestores <= 0)
        {
            return false;
        }

        _loadedProjectStageRefreshPending = true;
        return true;
    }

    private void BeginLoadedProjectStageRestore()
    {
        if (_activeLoadedProjectStageRestores == 0)
        {
            _loadedProjectStageRestoresIdle = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _activeLoadedProjectStageRestores++;
    }

    /// <summary>
    /// User edits must not mutate the live project object while its saved declaration is being
    /// replayed. The replay intentionally yields to the message loop, so merely serializing the
    /// eventual stage write is too late: an edit could otherwise change the object the active
    /// replay is still reading. Wait without blocking WinForms, then reject the action if the user
    /// selected another suit while it was queued.
    /// </summary>
    private async Task<bool> AwaitLoadedProjectStageRestoresBeforeEditAsync(string operation)
    {
        var expectedProject = _currentProject;
        var expectedGeneration = _loadedProjectSelectionGeneration;
        var loggedWait = false;

        while (_activeLoadedProjectStageRestores > 0)
        {
            var idleTask = _loadedProjectStageRestoresIdle?.Task;
            if (idleTask is null)
            {
                break;
            }
            if (!loggedWait)
            {
                AppendLog($"  {operation}: waiting for the saved suit stage restore to finish…");
                loggedWait = true;
            }
            await idleTask;
        }

        if (expectedGeneration == _loadedProjectSelectionGeneration &&
            ReferenceEquals(expectedProject, _currentProject))
        {
            return true;
        }

        AppendLog($"  {operation} stopped because another suit was selected while it was waiting.");
        return false;
    }

    private bool BlockSynchronousEditWhileLoadedProjectRestores(string operation)
    {
        if (_activeLoadedProjectStageRestores <= 0)
        {
            return false;
        }

        AppendLog($"  {operation} is waiting on the saved suit restore; retry when the restore finishes.");
        return true;
    }

    private void CompleteLoadedProjectStageRestore(NativeSuitProject project, int loadGeneration)
    {
        if (_activeLoadedProjectStageRestores > 0)
        {
            _activeLoadedProjectStageRestores--;
        }

        // Only the currently selected project's restore can clear the current generation. Stale
        // completions merely reduce the active count; they never refresh a newer suit early.
        if (IsCurrentLoadedProjectStageRestore(project, loadGeneration))
        {
            _currentLoadedProjectStageRestoreGeneration = 0;
        }

        TaskCompletionSource<bool>? idleSignal = null;
        if (_activeLoadedProjectStageRestores == 0)
        {
            // The active editor project may have changed through New/Delete while this restore was
            // in flight. With no restore left, no generation can retain final-refresh ownership.
            _currentLoadedProjectStageRestoreGeneration = 0;
            idleSignal = _loadedProjectStageRestoresIdle;
            _loadedProjectStageRestoresIdle = null;
        }

        try
        {
            if (_activeLoadedProjectStageRestores != 0 ||
                _currentLoadedProjectStageRestoreGeneration != 0 ||
                !_loadedProjectStageRefreshPending)
            {
                return;
            }

            _loadedProjectStageRefreshPending = false;
            RefreshAllViewsNow();
        }
        finally
        {
            // RunContinuationsAsynchronously keeps a queued edit from re-entering this completion
            // path before the final stage-backed refresh has returned.
            idleSignal?.TrySetResult(true);
        }
    }

    private async Task RestoreLoadedProjectStageAsync(
        NativeSuitProject project,
        string projectRoot,
        int loadGeneration)
    {
        try
        {
            await RebuildGraftStageFromDeclarativeAsync(
                project,
                projectRoot,
                loadedProjectRestore: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Saved suit restore failed for '{project.DisplayName}': {ex.Message}");
            if (IsCurrentLoadedProjectStageRestore(project, loadGeneration))
            {
                Dialog.Error(
                    this,
                    "Saved suit restore incomplete",
                    "Batcomputer kept the saved project, restored the prior generated payload where possible, and blocked packaging because the saved edits could not be replayed completely.\n\n" +
                    ex.Message);
            }
        }
        finally
        {
            CompleteLoadedProjectStageRestore(project, loadGeneration);
        }
    }

    private static string SuggestedMaterialOutputPackage(string? playablePackage)
    {
        var mod = ExtractModFolder(playablePackage);
        var playableName = UnrealPathUtil.AssetName(playablePackage);
        if (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(playableName))
        {
            return "";
        }

        var stem = playableName.StartsWith("BP_", StringComparison.OrdinalIgnoreCase)
            ? playableName[3..]
            : playableName;
        if (stem.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^"_Playable".Length];
        }
        return $"/Game/Mods/{mod}/Materials/MI_{stem}_Body";
    }

    private void OpenMaterialWizard()
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Creating a material"))
        {
            return;
        }

        var mod = ExtractModFolder(_targetPlayableText.Text.Trim());
        if (string.IsNullOrWhiteSpace(mod))
        {
            AppendLog("Create material: choose a base suit first (Base → Set base).");
            SelectComboValue(_toyboxCategoryCombo, "Base");
            return;
        }

        var suggested = $"MI_Batman_{mod}_{_toyboxSlotLabel.Replace(" ", "").Replace("/", "")}";
        using var wiz = new MaterialWizard(
            _projectRootText.Text.Trim(),
            mod,
            suggested,
            _currentProject?.GeneratedTextures,
            CurrentMaterialTemplateTarget());
        if (wiz.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(wiz.ResultMiPackagePath))
        {
            return;
        }
        RegisterGeneratedMaterial(wiz);
        AppendLog($"Created material {wiz.ResultMiPackagePath}");
        SelectComboValue(_toyboxCategoryCombo, wiz.ResultIsFaceMaterial ? "Faces" : "Materials");
        RefreshToyboxTiles();
    }

    /// <summary>
    /// Opens the material wizard seeded with an existing MI as the base. When
    /// <paramref name="editInPlace"/> is true (a material you made) the output
    /// name defaults to the same MI so saving overwrites/edits it; otherwise
    /// (a base-game MI) it suggests a fresh mod-scoped name.
    /// </summary>
    private async void OpenMaterialFromBase(string miGamePath, bool editInPlace)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync(
                editInPlace ? "edit the generated material" : "create a material from the selected base"))
        {
            return;
        }

        var diskPath = ResolveMaterialDiskPath(miGamePath, preferExport: editInPlace);
        if (diskPath is null)
        {
            AppendLog(editInPlace
                ? $"Could not find the .uasset for {miGamePath} (export content root). Set your export root in Settings."
                : $"Could not find the .uasset for {miGamePath}. Extract base-game content first (Settings → extracted content root).");
            return;
        }

        var mod = editInPlace
            ? ExtractModFolder(miGamePath)
            : ExtractModFolder(_targetPlayableText.Text.Trim());
        if (string.IsNullOrWhiteSpace(mod))
        {
            AppendLog("Create material: choose a base suit first (Base → Set base).");
            SelectComboValue(_toyboxCategoryCombo, "Base");
            return;
        }

        var baseStem = Path.GetFileNameWithoutExtension(diskPath);
        var suggested = editInPlace
            ? baseStem
            : $"MI_{mod}_{baseStem.Replace("MI_", "")}_Custom";

        using var wiz = new MaterialWizard(
            _projectRootText.Text.Trim(),
            mod,
            suggested,
            _currentProject?.GeneratedTextures,
            CurrentMaterialTemplateTarget());
        wiz.PrefillBase(diskPath, suggested, editInPlace);
        if (wiz.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(wiz.ResultMiPackagePath))
        {
            return;
        }
        if (editInPlace)
        {
            try
            {
                await RenameGeneratedMaterialAsync(miGamePath, wiz.ResultMiPackagePath);
            }
            catch (Exception ex)
            {
                // The rename transaction restored the prior suit recipe and stage. Still register
                // the newly cooked MI below as an unassigned material so the author's edit is not
                // lost and can be applied after the reported rebuild problem is fixed.
                AppendLog("Material edit was kept separately because its assignment rename could not be rebuilt: " + ex.Message);
                Dialog.Error(
                    this,
                    "Material edit kept separately",
                    "The prior suit and its working stage were restored. The edited material was kept as a separate unassigned material, but it was not substituted into this suit. Fix the reported rebuild issue, then apply the new material again.\n\n" + ex.Message);
            }
        }
        RegisterGeneratedMaterial(wiz);
        AppendLog($"{(editInPlace ? "Edited" : "Created from base")} material {wiz.ResultMiPackagePath}");
        SelectComboValue(_toyboxCategoryCombo, wiz.ResultIsFaceMaterial ? "Faces" : "Materials");
        RefreshToyboxTiles();
    }

    private void RegisterGeneratedMaterial(MaterialWizard wizard)
    {
        EnsureProject();
        if (_currentProject is null || string.IsNullOrWhiteSpace(wizard.ResultMiPackagePath))
        {
            return;
        }

        var results = wizard.ResultGeneratedMaterials.Count > 0
            ? wizard.ResultGeneratedMaterials
            : new List<MaterialWizard.GeneratedMaterialResult>
            {
                new()
                {
                    PackagePath = wizard.ResultMiPackagePath ?? "",
                    SourceMaterialPackagePath = wizard.ResultSourceMaterialPackagePath ?? "",
                    ParentMaterialPath = wizard.ResultParentMaterialPath ?? "",
                    IsFaceMaterial = wizard.ResultIsFaceMaterial,
                },
            };
        _currentProject.GeneratedMaterials ??= new List<GeneratedMaterialEntry>();
        var registered = new List<GeneratedMaterialEntry>();
        foreach (var result in results)
        {
            var package = UnrealPathUtil.NormalizePackagePath(result.PackagePath);
            var source = UnrealPathUtil.NormalizePackagePath(result.SourceMaterialPackagePath);
            var parent = UnrealPathUtil.NormalizePackagePath(result.ParentMaterialPath);
            _currentProject.GeneratedMaterials.RemoveAll(material =>
                UnrealPathUtil.NormalizePackagePath(material.PackagePath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase));
            var faceMeshes = result.IsFaceMaterial
                ? (result.CompatibleFaceMeshPackagePaths.Count > 0
                    ? result.CompatibleFaceMeshPackagePaths
                    : FaceMeshesForMaterial(source).ToList())
                : new List<string>();
            var entry = new GeneratedMaterialEntry
            {
                DisplayName = UnrealPathUtil.AssetName(package),
                Kind = result.IsFaceMaterial ? "Face" : "Material",
                PackagePath = package,
                SourceMaterialPackagePath = source,
                ParentMaterialPath = parent,
                CompatibleFaceMeshPackagePaths = faceMeshes
                    .Select(UnrealPathUtil.NormalizePackagePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                TemplateRecipeId = result.TemplateRecipeId,
                TemplateOutputRole = result.TemplateOutputRole,
                TemplateGroupId = result.TemplateGroupId,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
            };
            _currentProject.GeneratedMaterials.Add(entry);
            registered.Add(entry);
        }
        (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        new ToolMaterialLibraryService(_projectRootText.Text.Trim()).Register(registered);
    }

    private MaterialTemplateCatalogService.Target? CurrentMaterialTemplateTarget()
    {
        if (string.IsNullOrWhiteSpace(_toyboxComponent))
        {
            return null;
        }
        var mesh = _slotDetails.TryGetValue($"{_toyboxComponent}:{_toyboxSlot}", out var detail)
            ? UnrealPathUtil.NormalizePackagePath(detail.Mesh)
            : "";
        return new MaterialTemplateCatalogService.Target(
            _toyboxComponent,
            _toyboxSlotLabel,
            _toyboxSlot,
            mesh);
    }

    /// <summary>Right-click menu for a material tile (drag-only tiles need a menu to expose actions).</summary>
    private ContextMenuStrip BuildMaterialTileMenu(string miGamePath, bool isUserMade)
    {
        var menu = new ContextMenuStrip();
        if (isUserMade)
        {
            menu.Items.Add("Edit this material…", null, (_, _) => OpenMaterialFromBase(miGamePath, editInPlace: true));
            menu.Items.Add("Delete this material…", null, async (_, _) => await DeleteGeneratedMaterialAsync(miGamePath));
        }
        else
        {
            menu.Items.Add("Use as base for a new material…", null, (_, _) => OpenMaterialFromBase(miGamePath, editInPlace: false));
        }
        if (!string.IsNullOrWhiteSpace(ExtractModFolder(_targetPlayableText.Text.Trim())))
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add($"Apply to slot [{_toyboxSlotLabel}]", null, (_, _) => ApplyToyboxMaterial(miGamePath));
        }
        menu.Items.Add("Copy /Game path", null, (_, _) => { try { Clipboard.SetText(miGamePath); } catch { /* clipboard busy */ } });
        return menu;
    }

    private ContextMenuStrip BuildToolMaterialLibraryMenu(string miGamePath)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit shared material…", null, (_, _) => OpenMaterialFromBase(miGamePath, editInPlace: true));
        if (!string.IsNullOrWhiteSpace(ExtractModFolder(_targetPlayableText.Text.Trim())))
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add($"Apply to slot [{_toyboxSlotLabel}]", null, (_, _) => ApplyToyboxMaterial(miGamePath));
        }
        menu.Items.Add("Copy /Game path", null, (_, _) =>
        {
            try { Clipboard.SetText(miGamePath); }
            catch { /* clipboard busy */ }
        });
        return menu;
    }

    /// <summary>Accepts a dropped MATERIAL (rejects parts) and forwards its /Game path.</summary>
    private void WireMaterialOnlyDropTarget(Control control, Control row, Color accent, Action<string> onMaterialDrop)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
        {
            var payload = TryGetToyboxDragPayload(e.Data);
            var ok = payload is not null && payload.Kind.Equals("material", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(payload.MaterialPath);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) row.BackColor = Theme.Tint(accent);
        };
        control.DragOver += (_, e) =>
        {
            var payload = TryGetToyboxDragPayload(e.Data);
            e.Effect = payload is not null && payload.Kind.Equals("material", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(payload.MaterialPath)
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        control.DragLeave += (_, _) => row.BackColor = Theme.CardBg;
        control.DragDrop += (_, e) =>
        {
            row.BackColor = Theme.CardBg;
            var payload = TryGetToyboxDragPayload(e.Data);
            if (payload is not null && payload.Kind.Equals("material", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(payload.MaterialPath))
            {
                onMaterialDrop(payload.MaterialPath!);
            }
        };
    }

    /// <summary>Short, readable material label (drops the MI_/decal prefixes) for tight rows.</summary>
    private static string ShortMaterialName(string materialGamePath)
    {
        var name = UnrealPathUtil.AssetName(materialGamePath);
        foreach (var prefix in new[] { "MI_DECAL_Wingsuit_", "MI_DECAL_", "MI_FACE_", "MI_HAIR_", "MI_HAT_", "MI_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }
        return name;
    }

    /// <summary>
    /// Re-applies every persisted material assignment onto the current staged
    /// assets. Called after the name-map stage is rebuilt (which wipes staged
    /// edits) and on load, so materials stick across sessions and regenerates.
    /// </summary>
    private async Task<DeclarativeReplayOutcome> ApplySavedMaterials(
        NativeSuitProject project,
        bool logIfNone,
        string? stageContentRootOverride = null)
    {
        var outcome = new DeclarativeReplayOutcome();
        if (project.MaterialAssignments.Count == 0)
        {
            if (logIfNone) AppendLog("  no saved material assignments to re-apply.");
            return outcome;
        }

        var slotId = project.SlotId;
        var playablePkg = project.TargetPackages.Playable;
        var cutscenePkg = project.TargetPackages.Cutscene;
        var service = new MaterialReplaceService(_projectRootText.Text.Trim());
        var reapplied = 0;
        foreach (var m in project.MaterialAssignments)
        {
            if (string.IsNullOrWhiteSpace(m.Component) ||
                !ExtractedPackagePathService.IsContentPackagePath(m.MiPackagePath))
            {
                outcome.Failures.Add(
                    $"{m.Component}:{m.Slot}: saved material component/path is invalid ({m.MiPackagePath})");
                continue;
            }

            var context = (m.Context ?? "").Trim();
            var applyToPlayable = context.Equals("both", StringComparison.OrdinalIgnoreCase) ||
                                  context.Equals("playable", StringComparison.OrdinalIgnoreCase);
            var applyToCutscene = context.Equals("both", StringComparison.OrdinalIgnoreCase) ||
                                  context.Equals("cutscene", StringComparison.OrdinalIgnoreCase);
            if (!applyToPlayable && !applyToCutscene)
            {
                outcome.Failures.Add($"{m.Component}:{m.Slot}: saved material context '{m.Context}' is invalid");
                continue;
            }

            var assignment = new MaterialReplaceService.Assignment
            {
                Component = m.Component,
                Slot = m.Slot,
                MiPackagePath = m.MiPackagePath,
                ApplyToPlayable = applyToPlayable,
                ApplyToCutscene = applyToCutscene,
            };
            var result = await RunWithStructuredFileLockRetryAsync(
                () => string.IsNullOrWhiteSpace(stageContentRootOverride)
                    ? service.Apply(slotId, playablePkg, cutscenePkg, assignment)
                    : service.ApplyToContentRoot(
                        stageContentRootOverride,
                        slotId,
                        playablePkg,
                        cutscenePkg,
                        assignment),
                materialResult => materialResult.TransientFileLock ||
                                  materialResult.Files.Any(file => file.TransientFileLock),
                $"re-apply material '{m.Component}:{m.Slot}'");

            var requiredRoles = new[]
            {
                (Role: "playable", Required: applyToPlayable, Package: playablePkg),
                (Role: "cutscene", Required: applyToCutscene, Package: cutscenePkg),
            };
            var assignmentComplete = true;
            foreach (var required in requiredRoles.Where(role => role.Required))
            {
                if (string.IsNullOrWhiteSpace(required.Package))
                {
                    outcome.Failures.Add($"{m.Component}:{m.Slot}/{required.Role}: target package path is empty");
                    assignmentComplete = false;
                    continue;
                }

                var file = result.Files.FirstOrDefault(candidate =>
                    candidate.Role.Equals(required.Role, StringComparison.OrdinalIgnoreCase));
                if (file?.Success == true)
                {
                    continue;
                }

                var error = file?.Error ?? result.Error ?? "no result was returned";
                outcome.Failures.Add($"{m.Component}:{m.Slot}/{required.Role}: {error}");
                outcome.TransientFileLock |= file?.TransientFileLock == true || result.TransientFileLock;
                assignmentComplete = false;
            }

            if (assignmentComplete)
            {
                reapplied++;
            }
        }
        AppendLog($"  re-applied {reapplied}/{project.MaterialAssignments.Count} saved material assignment(s).");
        return outcome;
    }

    /// <summary>
    /// Restores the selected visual base's role-specific identity materials after a paired-cape
    /// scaffold substitution. This runs before ordinary saved removals/material assignments, so an
    /// explicit user edit remains authoritative and is never overwritten by the automatic overlay.
    /// </summary>
    private async Task<DeclarativeReplayOutcome> ApplyPairedCapeVisualOverlayMaterials(
        NativeSuitProject project,
        string projectRoot)
    {
        var outcome = new DeclarativeReplayOutcome();
        var overlay = project.PairedCapeAdapter?.VisualOverlay;
        if (overlay is null)
        {
            return outcome;
        }

        var assignments = new[]
        {
            new MaterialReplaceService.Assignment
            {
                Component = "CharacterMesh0",
                Slot = 0,
                MiPackagePath = overlay.PlayableBodyMaterialPackage,
                ApplyToPlayable = true,
                ApplyToCutscene = false,
            },
            new MaterialReplaceService.Assignment
            {
                Component = "CharacterMesh0",
                Slot = 0,
                MiPackagePath = overlay.CutsceneBodyMaterialPackage,
                ApplyToPlayable = false,
                ApplyToCutscene = true,
            },
            new MaterialReplaceService.Assignment
            {
                Component = "Face",
                Slot = 0,
                MiPackagePath = overlay.PlayableFaceMaterialPackage,
                ApplyToPlayable = true,
                ApplyToCutscene = false,
            },
            new MaterialReplaceService.Assignment
            {
                Component = "Face",
                Slot = 0,
                MiPackagePath = overlay.CutsceneFaceMaterialPackage,
                ApplyToPlayable = false,
                ApplyToCutscene = true,
            },
        };
        var service = new MaterialReplaceService(projectRoot);
        var applied = 0;
        foreach (var assignment in assignments)
        {
            var role = assignment.ApplyToPlayable ? "playable" : "cutscene";
            if (string.IsNullOrWhiteSpace(assignment.MiPackagePath) ||
                !ExtractedPackagePathService.IsContentPackagePath(assignment.MiPackagePath))
            {
                outcome.Failures.Add(
                    $"{assignment.Component}:0/{role}: certified visual-base material path is invalid ({assignment.MiPackagePath})");
                continue;
            }

            var result = await RunWithStructuredFileLockRetryAsync(
                () => service.Apply(
                    project.SlotId,
                    project.TargetPackages.Playable,
                    project.TargetPackages.Cutscene,
                    assignment),
                materialResult => materialResult.TransientFileLock ||
                                  materialResult.Files.Any(file => file.TransientFileLock),
                $"restore paired-cape visual-base {assignment.Component} material for {role}");
            var file = result.Files.FirstOrDefault(candidate =>
                candidate.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
            if (file?.Success == true)
            {
                applied++;
                continue;
            }

            outcome.Failures.Add(
                $"{assignment.Component}:0/{role}: {file?.Error ?? result.Error ?? "no result was returned"}");
            outcome.TransientFileLock |= file?.TransientFileLock == true || result.TransientFileLock;
        }

        AppendLog($"  restored {applied}/{assignments.Length} paired-cape visual-base identity material(s).");
        return outcome;
    }

    /// <summary>
    /// Shows every base-game gadget with an animation-compatibility badge for the
    /// current base playable's family. Data comes entirely from the shipped
    /// gamedata JSON (GameDataService) - no game extraction needed on the user's
    /// machine. "Foreign" gadgets are the ones that cause wrong equipment anims.
    /// </summary>
    /// <summary>Top-level /Game folders that contain game material instances (for the type dropdown).</summary>
    private IEnumerable<string> GameMaterialFolders()
    {
        var gd = GameDataService.Instance;
        var folders = gd.AssetsOfClass("MaterialInstanceConstant")
            .Select(a => MaterialGroupFolder(a.Path))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders)
        {
            yield return f;
        }
    }

    // Group MIs by the first two path segments under /Game (e.g. "Characters/Minifig").
    internal static string MaterialGroupFolder(string gamePath)
    {
        var p = UnrealPathUtil.NormalizePackagePath(gamePath).TrimStart('/');
        if (p.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            p = p["Game/".Length..];
        }
        var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length <= 1) return "";
        return segs.Length >= 3 ? $"{segs[0]}/{segs[1]}" : segs[0];
    }

    /// <summary>
    /// Materials rendered as a generated, searchable, paged tile grid - same UX
    /// as the Parts screen. "Your materials" = MIs you generated; a folder (or
    /// &lt;all game materials&gt;) = base-game MIs merged from the active extracted Content tree and
    /// the shipped fallback catalog. Click a tile to apply it to the selected slot.
    /// </summary>
    private void RefreshMaterialTiles(string? type)
    {
        var search = CurrentToyboxSearch();

        if (type == "All tool materials")
        {
            var materials = new ToolMaterialLibraryService(_projectRootText.Text.Trim())
                .LoadAvailable()
                .Where(material => MatchesToyboxSearch(
                    search,
                    material.DisplayName,
                    material.PackagePath,
                    material.Kind,
                    material.SourceMaterialPackagePath))
                .ToList();
            var tiles = materials.Select(material =>
            {
                var path = UnrealPathUtil.NormalizePackagePath(material.PackagePath);
                var isFace = material.Kind.Equals("Face", StringComparison.OrdinalIgnoreCase);
                return isFace
                    ? BuildFaceMaterialTile(
                        path,
                        isUserMade: true,
                        "TOOL MATERIAL LIBRARY",
                        allowDelete: false,
                        compatibleFaceMeshes: material.CompatibleFaceMeshPackagePaths)
                    : new VirtualTilePanel.Tile
                    {
                        Section = "TOOL MATERIAL LIBRARY",
                        Title = UnrealPathUtil.AssetName(path).Replace("MI_", ""),
                        Subtitle = "shared tool MI · drag to apply",
                        Accent = Theme.Materials,
                        DragPayload = new ToyboxDragPayload { Kind = "material", MaterialPath = path },
                        MenuFactory = () => BuildToolMaterialLibraryMenu(path),
                        ToolTip = $"Available to every suit in this workspace.\n{path}\nSource: {material.SourceMaterialPackagePath}",
                    };
            }).ToList();
            ShowVirtualTiles(
                tiles,
                header: "Every material created by the tool in this workspace. Drag one onto the current suit or right-click to apply/edit it; packaging brings the referenced cooked material into this suit automatically.",
                emptyMessage: "No available tool-created materials matched. Create one under Your materials first.");
            return;
        }

        if (type == "Your materials")
        {
            var mod = ExtractModFolder(_targetPlayableText.Text.Trim());
            var hasBase = !string.IsNullOrWhiteSpace(mod);
            var header = hasBase
                ? $"Materials you generated for slot [{_toyboxSlotLabel}]. Drag a tile onto a slot to apply it; right-click to edit. Use '＋ Create' for a new one, or switch the dropdown to a game folder to pull base-game MIs."
                : "All generated materials. Set a base suit before creating or assigning a material.";
            var tiles = new List<VirtualTilePanel.Tile>
            {
                hasBase
                    ? new() { Title = "＋ Create", Subtitle = "new material", Accent = Theme.Materials, Dashed = true, OnClick = OpenMaterialWizard }
                    : new() { Title = "Set base", Subtitle = "choose character", Accent = Theme.Base, Dashed = true, OnClick = () => SelectComboValue(_toyboxCategoryCombo, "Base") }
            };
            if (_currentProject is not null)
            {
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Title = "↻ Repair materials",
                    Subtitle = "recover + reapply",
                    Accent = Theme.Materials,
                    OnClick = () => { _ = RepairCurrentSuitMaterialsAsync(); },
                    ToolTip = "Recovers this suit's existing material packages and live custom-texture dependencies into the workspace library, then transactionally reapplies every saved material assignment. It does not erase or guess custom parameter values."
                });
            }

            foreach (var miPath in DiscoverUserMaterialPaths(mod))
            {
                var name = UnrealPathUtil.AssetName(miPath);
                var isFace = IsFaceMaterialPackage(miPath);
                if (!MatchesToyboxSearch(search, name, miPath))
                {
                    continue;
                }

                tiles.Add(new VirtualTilePanel.Tile
                {
                    Title = name.Replace("MI_", ""),
                    Subtitle = isFace ? "your face MI · apply to Face" : "your MI · drag to apply",
                    Accent = isFace ? Theme.Faces : Theme.Materials,
                    DragPayload = new ToyboxDragPayload { Kind = "material", MaterialPath = miPath, FaceOnly = isFace },
                    MenuFactory = () => isFace
                        ? BuildFaceMaterialTileMenu(miPath, isUserMade: true)
                        : BuildMaterialTileMenu(miPath, isUserMade: true),
                });
            }
            ShowVirtualTiles(tiles, header);
            return;
        }

        // Game-material grid from the active extraction plus the shipped fallback catalog.
        var gd = GameDataService.Instance;
        var availableGameMaterials = gd.AssetsOfClass("MaterialInstanceConstant").ToList();
        if (availableGameMaterials.Count == 0)
        {
            ShowVirtualTiles(
                new List<VirtualTilePanel.Tile> { new() { Title = "Browse…", Subtitle = "game MI from disk", Accent = Theme.Materials, OnClick = BrowseAndApplyGameMaterial } },
                "No material instances were found in the active extracted Content tree or bundled fallback. Use '＋ Create' or the disk browse instead.");
            return;
        }

        var folderFilter = (type is null || type == "<all game materials>") ? null : type;
        var all = availableGameMaterials
            .Where(a => folderFilter is null || MaterialGroupFolder(a.Path).Equals(folderFilter, StringComparison.OrdinalIgnoreCase))
            .Where(a => MatchesToyboxSearch(search, a.Path, a.Path[(a.Path.LastIndexOf('/') + 1)..]))
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ShowVirtualTiles(
            all.Select(a =>
            {
                var isFace = a.Path.Contains("/Attachments/Face/", StringComparison.OrdinalIgnoreCase) ||
                             UnrealPathUtil.AssetName(a.Path).StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase);
                return new VirtualTilePanel.Tile
                {
                    Title = a.Path[(a.Path.LastIndexOf('/') + 1)..].Replace("MI_", ""),
                    Subtitle = isFace ? "face material · apply to Face" : MaterialGroupFolder(a.Path),
                    Accent = isFace ? Theme.Faces : Theme.Materials,
                    DragPayload = new ToyboxDragPayload { Kind = "material", MaterialPath = a.Path, FaceOnly = isFace },
                    MenuFactory = () => isFace
                        ? BuildFaceMaterialTileMenu(a.Path, isUserMade: false)
                        : BuildMaterialTileMenu(a.Path, isUserMade: false),
                };
            }).ToList(),
            header: $"Base-game materials{(folderFilter is null ? "" : $" · {folderFilter}")} from the active extraction plus the bundled fallback for slot [{_toyboxSlotLabel}]. Drag onto a slot to apply; right-click to use one as a base for a new material. Type in the search box to filter.",
            emptyMessage: "No game materials matched. Try <all game materials> or clear the search box.");
    }

    private async Task RepairCurrentSuitMaterialsAsync()
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("repair suit materials"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        var repairProject = _currentProject;
        var projectRoot = _projectRootText.Text.Trim();
        var repairSlotId = repairProject.SlotId;
        var repairProjectService = new SuitProjectService(projectRoot);
        var packages = AssignedModMaterialPackagesForRelease(repairProject);
        if (packages.Count == 0)
        {
            Dialog.Info(this, "Repair materials", "This suit does not reference any tool-created /Game/Mods materials.");
            return;
        }

        if (!Dialog.Confirm(
                this,
                "Repair materials",
                $"Recover and validate {packages.Count} saved material package(s), then reapply every material assignment on a clean suit stage?\n\n" +
                "This keeps each material's current authored parameters. If the original material file and workspace copy are both gone, Batcomputer will stop instead of guessing them.",
                confirmText: "Repair materials",
                severity: Dialog.Level.Warn))
        {
            return;
        }

        var priorProject = JsonSerializer.Deserialize<NativeSuitProject>(
            JsonSerializer.Serialize(repairProject))
            ?? throw new InvalidOperationException("Could not snapshot the suit before repairing its materials.");
        BaseStageFilesystemSnapshot? stageSnapshot = null;
        ToolMaterialLibraryService.MaterialLibraryRepairTransaction? libraryRepair = null;
        Exception? reportedFailure = null;
        var closurePackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedPackages = 0;

        bool RepairContextIsStillActive() =>
            ReferenceEquals(_currentProject, repairProject) &&
            string.Equals(_projectRootText.Text.Trim(), projectRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(repairProject.SlotId, repairSlotId, StringComparison.OrdinalIgnoreCase);

        void RequireRepairContext()
        {
            if (!RepairContextIsStillActive())
            {
                throw new InvalidOperationException(
                    "The active suit changed while materials were being repaired. Batcomputer stopped before applying the repair to another suit.");
            }
        }

        using (var progress = new ProgressDialog(this, "Repairing suit materials", packages.Count + 2))
        {
            try
            {
                progress.SetStep("Backing up the current suit stage");
                progress.Report(repairProject.DisplayName);
                stageSnapshot = await CaptureBaseStageFilesystemAsync(
                    projectRoot,
                    repairSlotId,
                    new[] { "UnpatchedStage", "PatchedNameMapStage", "GraftedPartStage" });
                RequireRepairContext();

                progress.SetStep("Recovering the shared material library");
                progress.Report($"Validating {packages.Count} material closure(s)…");
                await Task.Yield();
                RequireRepairContext();
                var library = new ToolMaterialLibraryService(projectRoot);
                libraryRepair = library.BeginRepairMaterialClosures(packages);
                foreach (var dependency in libraryRepair.ClosurePackages)
                {
                    closurePackages.Add(dependency);
                }

                foreach (var package in packages)
                {
                    progress.SetStep($"Material {completedPackages + 1} of {packages.Count}");
                    progress.Report(package);
                    await Task.Yield();
                    RequireRepairContext();

                    AppendLog($"Repairing material closure: {package}");
                    if (!libraryRepair.ImportIntoProject(repairProject, package))
                    {
                        throw new InvalidOperationException(
                            $"Material '{package}' was recovered, but its workspace catalog entry could not be attached to this suit.");
                    }

                    completedPackages++;
                    progress.Advance(completedPackages, package);
                }

                progress.SetStep("Reapplying saved assignments");
                progress.Report("Rebuilding the clean declarative stage…");
                RequireRepairContext();
                await RebuildGraftStageFromDeclarativeAsync(
                    repairProject,
                    projectRoot,
                    persistProject: false);
                RequireRepairContext();
                progress.Advance(packages.Count + 1, "Saving the repaired suit recipe…");
                await RunWithFileLockRetryAsync(
                    () => repairProjectService.SaveProject(repairProject),
                    "save the repaired suit materials");
                RequireRepairContext();

                progress.SetStep("Certifying repaired stages");
                progress.Report("Finalizing the declarative material stage…");
                await FinalizeDeclarativeGraftStageAsync(repairProject, projectRoot);
                RequireRepairContext();
                libraryRepair.Commit();
                libraryRepair = null;
                progress.Advance(packages.Count + 2, "Repair complete");
                await DiscardBaseStageFilesystemBackupAsync(stageSnapshot, logFailure: true);
                stageSnapshot = null;
            }
            catch (Exception repairFailure)
            {
                reportedFailure = repairFailure;
                if (libraryRepair is not null)
                {
                    try
                    {
                        libraryRepair.Dispose();
                        libraryRepair = null;
                    }
                    catch (Exception libraryRestoreFailure)
                    {
                        reportedFailure = new AggregateException(
                            "Material repair failed and the previous workspace material library could not be completely restored.",
                            repairFailure,
                            libraryRestoreFailure);
                    }
                }
                if (stageSnapshot is not null)
                {
                    try
                    {
                        progress.SetStep("Restoring the previous suit stage");
                        progress.Report(repairProject.DisplayName);
                        await RestoreBaseStageFilesystemAsync(stageSnapshot);
                        repairProjectService.SaveProject(priorProject);
                    }
                    catch (Exception restoreFailure)
                    {
                        reportedFailure = new AggregateException(
                            "Material repair failed and the previous suit state could not be completely restored.",
                            reportedFailure,
                            restoreFailure);
                    }
                }
            }
        }

        if (reportedFailure is null && !RepairContextIsStillActive())
        {
            reportedFailure = new InvalidOperationException(
                "The active suit changed before the repaired suit could refresh the interface. The completed repair was not applied to the newly active suit.");
        }

        if (reportedFailure is null)
        {
            RecordChange("Materials", "Repair current suit", $"{packages.Count} material(s), {closurePackages.Count} package(s)", status: "repaired");
            AppendLog($"Repaired and reapplied {packages.Count} material(s) with {closurePackages.Count} live package(s) in their dependency closures.");
            _session.RaiseChanged();
            RefreshInspector();
            PopulateToyboxSlots();
            RefreshToyboxTiles();
            Dialog.Success(
                this,
                "Materials repaired",
                $"Recovered and reapplied {packages.Count} material(s). The clean suit stage now carries {closurePackages.Count} live material/texture package(s).");
            return;
        }

        if (RepairContextIsStillActive())
        {
            _currentProject = priorProject;
            ApplyProjectToFields(priorProject);
            _session.RaiseChanged();
            RefreshInspector();
            PopulateToyboxSlots();
            RefreshToyboxTiles();
        }

        AppendLog("Material repair stopped; the prior suit was kept: " + reportedFailure.Message);
        Dialog.Error(
            this,
            "Material repair failed",
            "Batcomputer kept the prior suit project and generated stages where possible. No material values were guessed.\n\n" +
            reportedFailure.Message);
    }

    /// <summary>
    /// User-made material instances can be in the optional export root, or in one of the
    /// current suit's persisted authoring stages after a stage rebuild. The latter is the
    /// authoritative source for older projects such as Electric, whose assignments are
    /// valid even though their original export folder is no longer configured.
    /// </summary>
    private IReadOnlyList<string> DiscoverUserMaterialPaths(string? mod)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedPrefix = string.IsNullOrWhiteSpace(mod) ? "/Game/Mods/" : $"/Game/Mods/{mod}/";

        if (_currentProject is not null)
        {
            foreach (var generated in _currentProject.GeneratedMaterials ?? Enumerable.Empty<GeneratedMaterialEntry>())
            {
                var package = UnrealPathUtil.NormalizePackagePath(generated.PackagePath);
                if (package.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(package);
                }
            }

            foreach (var assignment in _currentProject.MaterialAssignments)
            {
                var package = UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath);
                if (package.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(package);
                }
            }

            foreach (var package in (_currentProject.CustomStaticMeshes ?? new List<CustomStaticMeshImport>())
                         .SelectMany(mesh => StaticMeshObjProbeService.EffectiveMaterialSlots(mesh))
                         .Select(slot => UnrealPathUtil.NormalizePackagePath(slot.MaterialPath))
                         .Where(package => package.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(package);
            }
        }

        foreach (var contentRoot in GeneratedMaterialContentRoots(_currentProject))
        {
            var modRoot = string.IsNullOrWhiteSpace(mod)
                ? Path.Combine(contentRoot, "Mods")
                : Path.Combine(contentRoot, "Mods", mod);
            if (!Directory.Exists(modRoot))
            {
                continue;
            }

            var candidates = Directory.EnumerateFiles(modRoot, "MI_*.uasset", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(modRoot, "*.uasset", SearchOption.TopDirectoryOnly));
            var materialsFolder = Path.Combine(modRoot, "Materials");
            if (Directory.Exists(materialsFolder))
            {
                candidates = candidates.Concat(Directory.EnumerateFiles(materialsFolder, "*.uasset", SearchOption.AllDirectories));
            }

            foreach (var uasset in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Old Material Forge builds allowed names without MI_. Validate
                // those direct-root assets so legacy materials remain visible
                // without accidentally listing textures or Blueprints.
                if (!Path.GetFileName(uasset).StartsWith("MI_", StringComparison.OrdinalIgnoreCase) &&
                    !IsMaterialInstanceAsset(uasset))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(contentRoot, uasset)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                if (!relative.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                paths.Add("/Game/" + relative[..^".uasset".Length]);
            }
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool IsMaterialInstanceAsset(string uassetPath)
    {
        try
        {
            return new MaterialGenService(_projectRootText.Text.Trim())
                .ReadTemplate(uassetPath)
                .Status.Equals("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveMaterialDiskPath(string miGamePath, bool preferExport)
    {
        var diskPath = ResolveMiDiskPath(miGamePath, preferExport);
        if (diskPath is not null || !preferExport || _currentProject is null)
        {
            return diskPath;
        }

        var package = UnrealPathUtil.NormalizePackagePath(miGamePath);
        if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        foreach (var contentRoot in GeneratedMaterialContentRoots(_currentProject))
        {
            var candidate = Path.Combine(contentRoot, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var libraryUasset = new ToolMaterialLibraryService(_projectRootText.Text.Trim())
            .ResolvePackageUasset(package);
        if (!string.IsNullOrWhiteSpace(libraryUasset))
        {
            return libraryUasset;
        }

        return null;
    }

    private IEnumerable<string> GeneratedMaterialContentRoots(NativeSuitProject? project)
    {
        var exportRoot = AppSettings.Current.EffectiveExportContentRoot();
        if (!string.IsNullOrWhiteSpace(exportRoot))
        {
            yield return exportRoot;
        }

        if (project is null || string.IsNullOrWhiteSpace(project.SlotId))
        {
            yield break;
        }

        var projectRoot = new SuitProjectService(_projectRootText.Text.Trim())
            .ProjectOutputDirectory(project);
        foreach (var stage in new[] { "GraftedPartStage", "GraftedTorso2Stage", "PatchedNameMapStage", "IoStore" })
        {
            yield return stage.Equals("IoStore", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(projectRoot, stage, "Stage", "LEGOBatmanLotDK", "Content")
                : Path.Combine(projectRoot, stage, "LEGOBatmanLotDK", "Content");
        }
    }

    private async Task RenameGeneratedMaterialAsync(string oldPackagePath, string newPackagePath)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("rename the generated material"))
        {
            return;
        }

        var oldPackage = UnrealPathUtil.NormalizePackagePath(oldPackagePath);
        var newPackage = UnrealPathUtil.NormalizePackagePath(newPackagePath);
        if (oldPackage.Equals(newPackage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var library = new ToolMaterialLibraryService(_projectRootText.Text.Trim());
        var externalReferences = library.FindReferencingSuits(oldPackage, _currentProject?.SlotId);
        if (externalReferences.Count > 0)
        {
            AppendLog(
                $"Kept shared material {UnrealPathUtil.AssetName(oldPackage)} at its old path because it is still used by " +
                $"{string.Join(", ", externalReferences)}. The edited output was saved as the new independent material {UnrealPathUtil.AssetName(newPackage)}.");
            return;
        }

        var reassigned = 0;
        var registryUpdated = 0;
        var customMeshesUpdated = 0;
        if (_currentProject is not null)
        {
            var projectRoot = _projectRootText.Text.Trim();
            var priorProject = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(_currentProject))
                ?? throw new InvalidOperationException("Could not snapshot the suit before renaming its material.");
            var stageSnapshot = await CaptureBaseStageFilesystemAsync(
                projectRoot,
                _currentProject.SlotId,
                new[] { "UnpatchedStage", "PatchedNameMapStage", "GraftedPartStage" });
            try
            {
                foreach (var generated in _currentProject.GeneratedMaterials ?? Enumerable.Empty<GeneratedMaterialEntry>())
                {
                    if (!UnrealPathUtil.NormalizePackagePath(generated.PackagePath)
                            .Equals(oldPackage, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    generated.PackagePath = newPackage;
                    generated.DisplayName = UnrealPathUtil.AssetName(newPackage);
                    registryUpdated++;
                }

                foreach (var assignment in _currentProject.MaterialAssignments)
                {
                    if (!UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath)
                            .Equals(oldPackage, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    assignment.MiPackagePath = newPackage;
                    reassigned++;
                }

                customMeshesUpdated = ReplaceCustomStaticMeshMaterialReferences(
                    _currentProject,
                    oldPackage,
                    newPackage);

                if (reassigned > 0 || customMeshesUpdated > 0)
                {
                    // Rebuild transactionally before the new JSON becomes authoritative. The
                    // outer snapshot below can restore both project and stages if saving or final
                    // certification fails after the generated payload succeeds.
                    await RebuildGraftStageFromDeclarativeAsync(persistProject: false);
                }
                if (reassigned > 0 || registryUpdated > 0 || customMeshesUpdated > 0)
                {
                    await RunWithFileLockRetryAsync(
                        () => (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject),
                        "save the renamed generated material");
                }
                if (reassigned > 0 || customMeshesUpdated > 0)
                {
                    await FinalizeDeclarativeGraftStageAsync(_currentProject, projectRoot);
                }
                await DiscardBaseStageFilesystemBackupAsync(stageSnapshot, logFailure: true);
            }
            catch (Exception renameFailure)
            {
                _currentProject = priorProject;
                try
                {
                    await RestoreBaseStageFilesystemAsync(stageSnapshot);
                    ApplyProjectToFields(priorProject);
                    _session.RaiseChanged();
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException(
                        "The material rename failed and the previous suit stages could not be fully restored. Packaging remains blocked.",
                        renameFailure,
                        restoreFailure);
                }
                throw;
            }
        }

        var removed = DeleteGeneratedMaterialFiles(oldPackage);
        library.Remove(oldPackage);
        AppendLog($"Renamed material {UnrealPathUtil.AssetName(oldPackage)} to {UnrealPathUtil.AssetName(newPackage)}; updated {reassigned} assignment(s), {customMeshesUpdated} custom-mesh recipe(s), and {registryUpdated} saved material record(s), removed {removed} old file(s).");
        RefreshInspector();
    }

    internal static int ReplaceCustomStaticMeshMaterialReferences(
        NativeSuitProject project,
        string oldPackagePath,
        string replacementPackagePath)
    {
        var oldPackage = UnrealPathUtil.NormalizePackagePath(oldPackagePath);
        var replacement = UnrealPathUtil.NormalizePackagePath(replacementPackagePath);
        if (string.IsNullOrWhiteSpace(oldPackage) || string.IsNullOrWhiteSpace(replacement))
        {
            return 0;
        }

        var updated = 0;
        foreach (var mesh in project.CustomStaticMeshes ?? new List<CustomStaticMeshImport>())
        {
            if (mesh.MaterialSlots is { Count: > 0 })
            {
                foreach (var materialSlot in mesh.MaterialSlots)
                {
                    if (!UnrealPathUtil.NormalizePackagePath(materialSlot.MaterialPath)
                            .Equals(oldPackage, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    materialSlot.MaterialPath = replacement;
                    if (materialSlot.Slot == 0)
                    {
                        // Keep the legacy mirror current so an older Batcomputer build does not
                        // silently put a different material back onto the first section.
                        mesh.MaterialPath = replacement;
                    }
                    updated++;
                }
                continue;
            }

            if (UnrealPathUtil.NormalizePackagePath(mesh.MaterialPath)
                .Equals(oldPackage, StringComparison.OrdinalIgnoreCase))
            {
                mesh.MaterialPath = replacement;
                updated++;
            }
        }
        return updated;
    }

    internal static int CountCustomStaticMeshMaterialReferences(
        NativeSuitProject project,
        string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        return (project.CustomStaticMeshes ?? new List<CustomStaticMeshImport>())
            .Sum(mesh => StaticMeshObjProbeService.EffectiveMaterialSlots(mesh).Count(slot =>
                UnrealPathUtil.NormalizePackagePath(slot.MaterialPath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task DeleteGeneratedMaterialAsync(string miPackagePath)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("delete the generated material"))
        {
            return;
        }

        var package = UnrealPathUtil.NormalizePackagePath(miPackagePath);
        if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Material delete refused outside /Game/Mods: {package}");
            return;
        }

        var library = new ToolMaterialLibraryService(_projectRootText.Text.Trim());
        var externalReferences = library.FindReferencingSuits(package, _currentProject?.SlotId);
        if (externalReferences.Count > 0)
        {
            var users = string.Join("\n", externalReferences.Select(name => "• " + name));
            AppendLog($"Material delete blocked: {package} is still used by {string.Join(", ", externalReferences)}.");
            Dialog.Warn(
                this,
                "Material is shared",
                "This material is still saved on another suit, so deleting its cooked files would break that suit. Remove or replace it there first.\n\n" + users);
            return;
        }

        var assignments = _currentProject?.MaterialAssignments
            .Where(assignment => UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<SavedMaterialAssignment>();
        var customMeshReferences = _currentProject is null
            ? 0
            : CountCustomStaticMeshMaterialReferences(_currentProject, package);
        var detail = assignments.Count == 0 && customMeshReferences == 0
            ? "It is not assigned to this suit."
            : $"It is assigned to {assignments.Count} component slot(s) and {customMeshReferences} custom-mesh material slot(s). Those references will be removed and the stage rebuilt from the base.";
        if (!Dialog.Confirm(this, "Delete material",
                $"Delete '{UnrealPathUtil.AssetName(package)}'?\n\n{detail}\n\n{package}",
                confirmText: "Delete material", severity: Dialog.Level.Crit))
        {
            return;
        }

        var removedAssignments = 0;
        var removedRegistryEntries = 0;
        var resetCustomMeshMaterials = 0;
        if (_currentProject is not null)
        {
            var projectRoot = _projectRootText.Text.Trim();
            NativeSuitProject? priorProject = null;
            BaseStageFilesystemSnapshot? stageSnapshot = null;
            try
            {
                priorProject = JsonSerializer.Deserialize<NativeSuitProject>(
                    JsonSerializer.Serialize(_currentProject))
                    ?? throw new InvalidOperationException("Could not snapshot the suit before deleting its material.");
                stageSnapshot = await CaptureBaseStageFilesystemAsync(
                    projectRoot,
                    _currentProject.SlotId,
                    new[] { "UnpatchedStage", "PatchedNameMapStage", "GraftedPartStage" });

                if (assignments.Count > 0)
                {
                    removedAssignments = _currentProject.MaterialAssignments.RemoveAll(assignment =>
                        UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath)
                            .Equals(package, StringComparison.OrdinalIgnoreCase));
                }
                removedRegistryEntries = (_currentProject.GeneratedMaterials ?? new List<GeneratedMaterialEntry>())
                    .RemoveAll(material => UnrealPathUtil.NormalizePackagePath(material.PackagePath)
                        .Equals(package, StringComparison.OrdinalIgnoreCase));
                resetCustomMeshMaterials = ReplaceCustomStaticMeshMaterialReferences(
                    _currentProject,
                    package,
                    CustomStaticMeshImportService.DefaultMaterialPackagePath);

                if (removedAssignments > 0 || resetCustomMeshMaterials > 0)
                {
                    // Keep the working stage and project file recoverable until the replacement
                    // recipe has rebuilt in both runtime roles. Generated material files are
                    // deliberately deleted only after this transaction is certified.
                    await RebuildGraftStageFromDeclarativeAsync(persistProject: false);
                }
                if (removedAssignments > 0 || removedRegistryEntries > 0 || resetCustomMeshMaterials > 0)
                {
                    await RunWithFileLockRetryAsync(
                        () => (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject),
                        "save the generated material deletion");
                }
                if (removedAssignments > 0 || resetCustomMeshMaterials > 0)
                {
                    await FinalizeDeclarativeGraftStageAsync(_currentProject, projectRoot);
                }
                await DiscardBaseStageFilesystemBackupAsync(stageSnapshot, logFailure: true);
            }
            catch (Exception deleteFailure)
            {
                var reportedFailure = deleteFailure;
                if (priorProject is not null && stageSnapshot is not null)
                {
                    _currentProject = priorProject;
                    try
                    {
                        await RestoreBaseStageFilesystemAsync(stageSnapshot);
                        ApplyProjectToFields(priorProject);
                        _session.RaiseChanged();
                    }
                    catch (Exception restoreFailure)
                    {
                        reportedFailure = new AggregateException(
                            "The material deletion failed and the previous suit stages could not be fully restored. Packaging remains blocked.",
                            deleteFailure,
                            restoreFailure);
                    }
                }

                AppendLog("Material delete stopped; the prior material and suit were kept: " + reportedFailure.Message);
                Dialog.Error(
                    this,
                    "Material delete failed",
                    "Batcomputer could not rebuild the suit without this material, so it kept the prior project, generated stages, and material files.\n\n" +
                    reportedFailure.Message);
                return;
            }
        }

        var removedFiles = DeleteGeneratedMaterialFiles(package);
        try
        {
            library.Remove(package);
        }
        catch (Exception ex)
        {
            // The suit no longer references the material and its local cooked copies were already
            // removed. A locked disposable library entry must not undo that certified deletion.
            AppendLog($"Material library cleanup warning for {package}: {ex.Message}");
        }

        RecordChange("Materials", UnrealPathUtil.AssetName(package), "deleted", status: "deleted");
        AppendLog($"Deleted material {package}; removed {removedAssignments} assignment(s), reset {resetCustomMeshMaterials} custom-mesh recipe(s), removed {removedRegistryEntries} saved material record(s), and deleted {removedFiles} file(s).");
        RefreshInspector();
        RefreshToyboxTiles();
    }

    private int DeleteGeneratedMaterialFiles(string miPackagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(miPackagePath);
        if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var relative = package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
        var removed = 0;
        foreach (var contentRoot in GeneratedMaterialContentRoots(_currentProject)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var basePath = Path.Combine(contentRoot, relative);
            foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                var candidate = basePath + extension;
                try
                {
                    if (File.Exists(candidate))
                    {
                        File.Delete(candidate);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Material delete warning for {candidate}: {ex.Message}");
                }
            }
        }

        return removed;
    }

    private async void ApplyToyboxMaterial(string miPath)
    {
        _matAssignComponentText.Text = _toyboxComponent;
        _matAssignSlotText.Text = _toyboxSlot.ToString();
        _matAssignMiText.Text = miPath;
        SelectComboValue(_matAssignContextCombo, "both");
        await ApplyMaterialAssignmentAsync();
    }

    private void PickAndApplyCatalogMaterial()
    {
        var path = PickFromCatalog("MaterialInstanceConstant", "Pick a game material (active extraction + fallback)");
        if (path is not null)
        {
            ApplyToyboxMaterial(path);
        }
    }

    private void BrowseAndApplyGameMaterial()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Pick a game material instance (.uasset)",
            Filter = "Cooked asset (*.uasset)|*.uasset",
            InitialDirectory = AppSettings.Current.EffectiveExtractedContentRoot()
        };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var full = dialog.FileName;
        if (!FileSystemPathUtil.IsWithinDirectory(full, contentRoot))
        {
            AppendLog($"Material must be under the extracted content root: {contentRoot}");
            return;
        }

        var rel = full.Substring(contentRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
        var noExt = rel[..^".uasset".Length];
        var name = Path.GetFileName(noExt);
        ApplyToyboxMaterial($"/Game/{noExt}.{name}");
    }

    private Control CreateMaterialGenPanel()
    {
        var box = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Material generator (clone a game MI, retarget textures to your own pak paths)"
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        box.Controls.Add(layout);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.Controls.Add(top, 0, 0);
        top.Controls.Add(new Label { Text = "Base MI", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _matBaseText.Dock = DockStyle.Fill;
        top.Controls.Add(_matBaseText, 1, 0);
        _matBrowseButton.Text = "Browse";
        _matBrowseButton.Dock = DockStyle.Fill;
        _matBrowseButton.Click += (_, _) => BrowseBaseMi();
        top.Controls.Add(_matBrowseButton, 2, 0);
        _matReadButton.Text = "Read params";
        _matReadButton.Dock = DockStyle.Fill;
        _matReadButton.Click += (_, _) => ReadMaterialTemplate();
        top.Controls.Add(_matReadButton, 3, 0);

        ConfigureMatParamGrid(_matParamGrid);
        _matParamGrid.Dock = DockStyle.Fill;
        layout.Controls.Add(_matParamGrid, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.Controls.Add(bottom, 0, 2);
        bottom.Controls.Add(new Label { Text = "Output package", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _matOutputText.Dock = DockStyle.Fill;
        _matOutputText.PlaceholderText = "/Game/Mods/YourMod/Materials/MI_YourSuit_Body";
        bottom.Controls.Add(_matOutputText, 1, 0);
        var useGeneratedTexture = new Button { Text = "Use gen texture", Dock = DockStyle.Fill };
        useGeneratedTexture.Click += (_, _) => UseGeneratedTextureForSelectedMaterialGridRow();
        bottom.Controls.Add(useGeneratedTexture, 2, 0);
        _matGenerateButton.Text = "Generate material";
        _matGenerateButton.Dock = DockStyle.Fill;
        _matGenerateButton.Click += (_, _) => GenerateMaterial();
        bottom.Controls.Add(_matGenerateButton, 3, 0);

        var assign = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1 };
        assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));   // "Assign to"
        assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));  // component
        assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));   // "Slot"
        assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));   // slot
        assign.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // MI path
        assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));  // context
        assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));  // apply
        layout.Controls.Add(assign, 0, 3);

        assign.Controls.Add(new Label { Text = "Assign to", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _matAssignComponentText.Dock = DockStyle.Fill;
        _matAssignComponentText.Text = "CharacterMesh0";
        assign.Controls.Add(_matAssignComponentText, 1, 0);
        assign.Controls.Add(new Label { Text = "Slot", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 2, 0);
        _matAssignSlotText.Dock = DockStyle.Fill;
        _matAssignSlotText.Text = "0";
        assign.Controls.Add(_matAssignSlotText, 3, 0);
        _matAssignMiText.Dock = DockStyle.Fill;
        assign.Controls.Add(_matAssignMiText, 4, 0);
        _matAssignContextCombo.Dock = DockStyle.Fill;
        _matAssignContextCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _matAssignContextCombo.Items.AddRange(new object[] { "both", "playable", "cutscene" });
        _matAssignContextCombo.SelectedIndex = 0;
        assign.Controls.Add(_matAssignContextCombo, 5, 0);
        _matApplyButton.Text = "Apply material to stage";
        _matApplyButton.Dock = DockStyle.Fill;
        _matApplyButton.Click += async (_, _) => await ApplyMaterialAssignmentAsync();
        assign.Controls.Add(_matApplyButton, 6, 0);

        return box;
    }

    private void ReadMaterialTemplate()
    {
        var path = _matBaseText.Text.Trim();
        if (!File.Exists(path))
        {
            AppendLog($"Base MI not found: {path}");
            return;
        }

        var info = new MaterialGenService(_projectRootText.Text.Trim()).ReadTemplate(path);
        _matParamGrid.Rows.Clear();
        if (info.Status != "ok")
        {
            AppendLog($"Read material: {info.Status} {info.Error}");
            return;
        }

        AppendLog($"Base MI: {info.SourcePackagePath} ({info.TextureParams.Count} texture params)");
        foreach (var p in info.TextureParams)
        {
            _matParamGrid.Rows.Add(p.Name, p.CurrentTexturePath, "");
        }
    }

    private void GenerateMaterial()
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Generating the material"))
        {
            return;
        }

        var basePath = _matBaseText.Text.Trim();
        var outputPackage = _matOutputText.Text.Trim();
        if (!File.Exists(basePath))
        {
            AppendLog($"Base MI not found: {basePath}");
            return;
        }
        if (!outputPackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog("Output package must start with /Game/.");
            return;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _matParamGrid.Rows)
        {
            var name = row.Cells["Param"].Value?.ToString() ?? "";
            var tex = row.Cells["YourTexture"].Value?.ToString()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(tex))
            {
                map[name] = tex;
            }
        }
        if (map.Count == 0)
        {
            AppendLog("No texture mappings entered — fill 'Your texture path' for at least one param.");
            return;
        }

        var result = new MaterialGenService(_projectRootText.Text.Trim()).Generate(new MaterialGenService.GenRequest
        {
            BaseUassetPath = basePath,
            OutputPackagePath = outputPackage,
            ParamToTexture = map
        });
        AppendLog($"Generate material: {result.Status}");
        if (result.Status == "created")
        {
            // Pre-fill the assignment MI so "Apply material to stage" targets it.
            _matAssignMiText.Text = outputPackage;
        }
        if (!string.IsNullOrWhiteSpace(result.OutputUasset))
        {
            AppendLog($"  output: {result.OutputUasset}");
        }
        foreach (var r in result.Retargeted)
        {
            AppendLog($"  retargeted: {r}");
        }
        foreach (var w in result.Warnings)
        {
            AppendLog($"  warning: {w}");
        }
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppendLog(result.Error);
        }
    }

    internal sealed record ResolvedTemplateMaterialAssignment(string Context, string PackagePath);

    internal sealed record TemplateMaterialAssignmentResolution(
        IReadOnlyList<ResolvedTemplateMaterialAssignment> Assignments,
        string GameplayBaselinePackage,
        string Warning)
    {
        public bool IsRoleExpanded => Assignments.Count > 1;
    }

    /// <summary>
    /// Resolves a Material Forge output into the cooked runtime context(s) its donor was authored
    /// for. LOD-qualified sets are paired only with the sibling carrying the exact same qualifier;
    /// this deliberately refuses to guess between LOD0 and LOD1.
    /// </summary>
    internal static TemplateMaterialAssignmentResolution ResolveTemplateMaterialAssignments(
        IEnumerable<GeneratedMaterialEntry> knownMaterials,
        string selectedPackagePath,
        string requestedContext)
    {
        var selectedPackage = UnrealPathUtil.NormalizePackagePath(selectedPackagePath);
        var context = (requestedContext ?? "both").Trim().ToLowerInvariant();
        if (context is not ("both" or "playable" or "cutscene"))
        {
            context = "both";
        }

        var entries = (knownMaterials ?? Enumerable.Empty<GeneratedMaterialEntry>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PackagePath))
            .ToList();
        var selected = entries
            .Where(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath)
                .Equals(selectedPackage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => !string.IsNullOrWhiteSpace(entry.TemplateGroupId))
            .FirstOrDefault();
        if (selected is null || string.IsNullOrWhiteSpace(selected.TemplateGroupId))
        {
            return new TemplateMaterialAssignmentResolution(
                [new ResolvedTemplateMaterialAssignment(context, selectedPackage)],
                selectedPackage,
                "");
        }

        static string RuntimeContext(string? role)
        {
            if (role?.Contains("gameplay", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "playable";
            }
            if (role?.Contains("cutscene", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "cutscene";
            }
            return "";
        }

        static string RoleQualifier(string? role)
        {
            var normalized = (role ?? "").Trim().ToLowerInvariant()
                .Replace("gameplay", "", StringComparison.OrdinalIgnoreCase)
                .Replace("cutscene", "", StringComparison.OrdinalIgnoreCase);
            return string.Concat(normalized.Where(char.IsLetterOrDigit));
        }

        var selectedRuntimeContext = RuntimeContext(selected.TemplateOutputRole);
        var qualifier = RoleQualifier(selected.TemplateOutputRole);
        if (string.IsNullOrWhiteSpace(selectedRuntimeContext))
        {
            return new TemplateMaterialAssignmentResolution(
                Array.Empty<ResolvedTemplateMaterialAssignment>(),
                "",
                $"Material '{UnrealPathUtil.AssetName(selectedPackage)}' belongs to a paired template, but its output role is missing or unrecognized. Recreate the template set before applying it.");
        }

        var matchingFamily = entries
            .Where(entry => entry.TemplateGroupId.Equals(selected.TemplateGroupId, StringComparison.OrdinalIgnoreCase) &&
                            RoleQualifier(entry.TemplateOutputRole).Equals(qualifier, StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var gameplayMatches = matchingFamily.Where(entry =>
            RuntimeContext(entry.TemplateOutputRole).Equals("playable", StringComparison.OrdinalIgnoreCase)).ToList();
        var cutsceneMatches = matchingFamily.Where(entry =>
            RuntimeContext(entry.TemplateOutputRole).Equals("cutscene", StringComparison.OrdinalIgnoreCase)).ToList();
        if (gameplayMatches.Count > 1 || cutsceneMatches.Count > 1)
        {
            var qualifierText = string.IsNullOrWhiteSpace(qualifier) ? "the shared output" : qualifier.ToUpperInvariant();
            return new TemplateMaterialAssignmentResolution(
                Array.Empty<ResolvedTemplateMaterialAssignment>(),
                "",
                $"Material template group '{selected.TemplateGroupId}' has duplicate runtime outputs for {qualifierText}. Recreate the template set before applying it.");
        }

        var gameplayPackage = gameplayMatches.Count == 0
            ? ""
            : UnrealPathUtil.NormalizePackagePath(gameplayMatches[0].PackagePath);
        var cutscenePackage = cutsceneMatches.Count == 0
            ? ""
            : UnrealPathUtil.NormalizePackagePath(cutsceneMatches[0].PackagePath);

        var assignments = new List<ResolvedTemplateMaterialAssignment>();
        if (context is "both" or "playable" && !string.IsNullOrWhiteSpace(gameplayPackage))
        {
            assignments.Add(new ResolvedTemplateMaterialAssignment("playable", gameplayPackage));
        }
        if (context is "both" or "cutscene" && !string.IsNullOrWhiteSpace(cutscenePackage))
        {
            assignments.Add(new ResolvedTemplateMaterialAssignment("cutscene", cutscenePackage));
        }

        var expectedCount = context.Equals("both", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (assignments.Count != expectedCount)
        {
            var qualifierText = string.IsNullOrWhiteSpace(qualifier) ? "the shared output" : qualifier.ToUpperInvariant();
            return new TemplateMaterialAssignmentResolution(
                Array.Empty<ResolvedTemplateMaterialAssignment>(),
                gameplayPackage,
                $"Material template group '{selected.TemplateGroupId}' is incomplete for {qualifierText}: " +
                $"it needs the requested gameplay/cutscene output(s). Recreate the full template set; Batcomputer will not substitute another LOD or runtime context.");
        }

        return new TemplateMaterialAssignmentResolution(assignments, gameplayPackage, "");
    }

    private async Task ApplyMaterialAssignmentAsync()
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("apply the material assignment"))
        {
            return;
        }

        var slotId = _slotIdText.Text.Trim();
        var component = _matAssignComponentText.Text.Trim();
        var mi = UnrealPathUtil.NormalizePackagePath(_matAssignMiText.Text.Trim());

        if (string.IsNullOrWhiteSpace(slotId)) { AppendLog("Slot ID is empty."); return; }
        if (string.IsNullOrWhiteSpace(component)) { AppendLog("Component is empty."); return; }
        if (!ExtractedPackagePathService.IsContentPackagePath(mi)) { AppendLog("MI path must be a valid game or installed DLC content package."); return; }
        if (!int.TryParse(_matAssignSlotText.Text.Trim(), out var slot) || slot < 0) { AppendLog("Slot must be a non-negative integer."); return; }

        var context = _matAssignContextCombo.SelectedItem?.ToString() ?? "both";
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        if (FindCustomStaticMeshForComponent(_currentProject, component) is { } declaredCustomMesh &&
            !StaticMeshObjProbeService.EffectiveMaterialSlots(declaredCustomMesh)
                .Any(materialSlot => materialSlot.Slot == slot))
        {
            var available = string.Join(", ", StaticMeshObjProbeService.EffectiveMaterialSlots(declaredCustomMesh)
                .Select(materialSlot => materialSlot.Slot));
            var message = $"Custom mesh '{declaredCustomMesh.DisplayName}' has no material slot {slot}. " +
                          $"Available slot(s): {(string.IsNullOrWhiteSpace(available) ? "none" : available)}.";
            AppendLog("Apply material stopped: " + message);
            Dialog.Warn(this, "Material slot not found", message);
            return;
        }

        var materialLibrary = new ToolMaterialLibraryService(_projectRootText.Text.Trim());
        var knownMaterials = (_currentProject.GeneratedMaterials ?? new List<GeneratedMaterialEntry>())
            .Concat(materialLibrary.LoadAvailable())
            .ToList();
        var roleResolution = ResolveTemplateMaterialAssignments(knownMaterials, mi, context);
        if (roleResolution.Assignments.Count == 0)
        {
            AppendLog("Apply material stopped: " + roleResolution.Warning);
            Dialog.Warn(this, "Material template set is incomplete", roleResolution.Warning);
            return;
        }
        if (!string.IsNullOrWhiteSpace(roleResolution.Warning))
        {
            AppendLog("Apply material warning: " + roleResolution.Warning);
        }

        var projectRoot = _projectRootText.Text.Trim();
        var patchedContentRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            slotId,
            "PatchedNameMapStage",
            "LEGOBatmanLotDK",
            "Content");
        if (!Directory.Exists(patchedContentRoot))
        {
            if (!HasCurrentSuitBase())
            {
                AppendLog("Apply material stopped: set a base character before assigning materials.");
                return;
            }
            // The gated declarative rebuild below owns creation of a missing clean base. Keeping
            // that write inside RebuildGate prevents a material click from racing a saved restore
            // or another name-map rebuild before the assignment transaction has even started.
            AppendLog("Apply material: the clean editable stage will be created during the transactional rebuild.");
        }

        NativeSuitProject previousProjectSnapshot;
        try
        {
            previousProjectSnapshot = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(_currentProject))
                ?? throw new InvalidOperationException("Could not snapshot the suit before applying the material.");
        }
        catch (Exception ex)
        {
            AppendLog("Apply material stopped before staging: " + ex.Message);
            return;
        }

        var replacesBothRuntimeContexts = context.Equals("both", StringComparison.OrdinalIgnoreCase);
        _currentProject.MaterialAssignments.RemoveAll(assignment =>
            assignment.Component.Equals(component, StringComparison.OrdinalIgnoreCase) &&
            assignment.Slot == slot &&
            (replacesBothRuntimeContexts ||
             assignment.Context.Equals(context, StringComparison.OrdinalIgnoreCase)));
        foreach (var resolved in roleResolution.Assignments)
        {
            materialLibrary.ImportIntoProject(_currentProject, resolved.PackagePath);
            _currentProject.MaterialAssignments.Add(new SavedMaterialAssignment
            {
                Component = component,
                Slot = slot,
                MiPackagePath = resolved.PackagePath,
                Context = resolved.Context,
            });
        }
        // A custom static mesh also stores each OBJ section material declaratively. Keep that
        // source of truth aligned with a normal "both" assignment so rebuilding the mesh cannot
        // silently restore a donor/default material after the user already changed it.
        if (context.Equals("both", StringComparison.OrdinalIgnoreCase) &&
            FindCustomStaticMeshForComponent(_currentProject, component) is { } customMesh)
        {
            var resolvedMaterial = string.IsNullOrWhiteSpace(roleResolution.GameplayBaselinePackage)
                ? mi
                : roleResolution.GameplayBaselinePackage;
            if (customMesh.MaterialSlots is { Count: > 0 })
            {
                var materialSlot = customMesh.MaterialSlots.FirstOrDefault(candidate => candidate.Slot == slot);
                if (materialSlot is not null)
                {
                    materialSlot.MaterialPath = resolvedMaterial;
                    if (slot == 0)
                    {
                        customMesh.MaterialPath = resolvedMaterial;
                    }
                }
            }
            else if (slot == 0)
            {
                customMesh.MaterialPath = resolvedMaterial;
            }
        }

        var projectSaved = false;
        try
        {
            await RebuildGraftStageFromDeclarativeAsync(persistProject: false);
            await RunWithFileLockRetryAsync(
                () => (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject),
                "save the completed material assignment");
            projectSaved = true;
            await FinalizeDeclarativeGraftStageAsync(_currentProject, projectRoot);
        }
        catch (Exception ex)
        {
            if (!projectSaved)
            {
                _currentProject = previousProjectSnapshot;
                ApplyProjectToFields(_currentProject);
                UpdateSelectedLabels();
            }
            AppendLog(projectSaved
                ? "The material assignment was saved, but its generated stage could not be certified: " + ex.Message
                : "Apply material failed; the prior saved project was kept: " + ex.Message);
            Dialog.Error(
                this,
                projectSaved ? "Material saved; stage incomplete" : "Material apply failed",
                (projectSaved
                    ? "The assignment was saved, but packaging remains blocked until its declarative stage rebuild can be certified."
                    : "The material could not be applied to every required character package. The prior saved project remains active.") +
                "\n\n" + ex.Message);
            _session.RaiseChanged();
            RefreshInspector();
            PopulateToyboxSlots();
            return;
        }

        var appliedDescription = string.Join(
            "; ",
            roleResolution.Assignments.Select(assignment =>
                $"{assignment.Context}={assignment.PackagePath}"));
        RecordChange("Materials", $"{component} slot {slot}", appliedDescription);
        AppendLog($"Applied material [{component} slot {slot}] {appliedDescription} to the completed declarative stage.");
        RefreshInspector();
        PopulateToyboxSlots();
    }

    private void BrowseUassetInto(TextBox target)
    {
        using var dlg = new OpenFileDialog { Filter = "Cooked asset (*.uasset)|*.uasset|All files|*.*" };
        var start = AppSettings.Current.EffectiveExtractedContentRoot();
        if (!string.IsNullOrWhiteSpace(target.Text) && File.Exists(target.Text))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(target.Text);
            dlg.FileName = Path.GetFileName(target.Text);
        }
        else if (Directory.Exists(start))
        {
            dlg.InitialDirectory = start;
        }
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dlg.FileName;
        }
    }

    // Copies the suit's generated /Game/Mods/<mod> assets (material instances,
    // etc.) from the Export content root into the content root that gets packed,
    // so they end up inside the pak. The Characters subfolder is skipped so the
    // patched/grafted BP assets in the stage are never clobbered by stale exports.
    private void StageGeneratedMaterialsIntoContentRoot(NativeSuitProject project, string contentRootToPackage)
    {
        ThrowIfMaterialPackageCollidesWithCustomMesh(project);
        var customMeshPackages = DeclaredCustomMeshPackagesForRelease(project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mod = ExtractModFolder(project.TargetPackages?.Playable);
        var copied = 0;
        var preserved = 0;
        if (!string.IsNullOrWhiteSpace(mod))
        {
            var dst = Path.Combine(contentRootToPackage, "Mods", mod);
            var src = Path.Combine(AppSettings.Current.EffectiveExportContentRoot(), "Mods", mod);
            if (Directory.Exists(src))
            {
                var cookedPackageExtensions = new HashSet<string>(
                    [".uasset", ".uexp", ".ubulk"],
                    StringComparer.OrdinalIgnoreCase);
                // Capture pre-existing package ownership before copying starts. Newly copied
                // ExportContent packages must still receive all of their sidecars, while any
                // package already present in the certified stage is preserved atomically.
                var certifiedPackageBases = Directory.Exists(dst)
                    ? Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories)
                        .Where(existing => cookedPackageExtensions.Contains(Path.GetExtension(existing)))
                        .Select(existing => Path.ChangeExtension(Path.GetFullPath(existing), null) ?? existing)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(src.Length).TrimStart('\\', '/');
                    // Never overwrite the patched/grafted BP assets.
                    if (relative.StartsWith("Characters", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Custom mesh assets are generated fresh into the certified stage. Never let
                    // an older ExportContent copy of the same package replace (or stand in for)
                    // that authoritative mesh during packaging.
                    var relativePackage = Path.ChangeExtension(relative, null)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    var exportedPackage = UnrealPathUtil.NormalizePackagePath(
                        $"/Game/Mods/{mod}/{relativePackage}");
                    if (customMeshPackages.Contains(exportedPackage))
                    {
                        preserved++;
                        continue;
                    }

                    var destination = Path.Combine(dst, relative);
                    var destinationPackageBase = Path.ChangeExtension(Path.GetFullPath(destination), null)
                        ?? Path.GetFullPath(destination);
                    if (cookedPackageExtensions.Contains(Path.GetExtension(destination)) &&
                        certifiedPackageBases.Contains(destinationPackageBase))
                    {
                        // Do not create a hybrid package from a certified .uasset and stale export
                        // sidecars (or the reverse). Validation will fail closed if the certified
                        // package itself is incomplete.
                        preserved++;
                        continue;
                    }
                    if (File.Exists(destination))
                    {
                        // The fresh declarative stage wins over the disposable export cache.
                        preserved++;
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: false);
                    copied++;
                }
            }

            AppendLog(copied > 0
                ? $"Staged {copied} generated Mods\\{mod} asset file(s) into the pack content root."
                : $"No generated Mods\\{mod} assets to stage.");
            if (preserved > 0)
            {
                AppendLog($"  kept {preserved} fresh certified-stage file(s) instead of replacing them from ExportContent.");
            }
        }

        var library = new ToolMaterialLibraryService(_projectRootText.Text.Trim());
        var libraryCopies = StageReferencedToolMaterialsForRelease(
            project,
            library,
            contentRootToPackage);
        if (libraryCopies > 0)
        {
            AppendLog($"Staged {libraryCopies} referenced tool-material package file(s), including shared cross-suit materials.");
        }
    }

    internal static IReadOnlyList<string> ReferencedGeneratedMaterialPackagesForRelease(
        NativeSuitProject project,
        IEnumerable<string>? availableToolMaterialPackages = null)
    {
        var releasablePackages = (project.GeneratedMaterials ?? new List<GeneratedMaterialEntry>())
            .Select(entry => UnrealPathUtil.NormalizePackagePath(entry.PackagePath))
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var package in availableToolMaterialPackages ?? Enumerable.Empty<string>())
        {
            var normalized = UnrealPathUtil.NormalizePackagePath(package);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                releasablePackages.Add(normalized);
            }
        }

        return DeclaredReleaseMaterialPackages(project)
            .Where(package => releasablePackages.Contains(package))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static int StageReferencedToolMaterialsForRelease(
        NativeSuitProject project,
        ToolMaterialLibraryService library,
        string contentRoot)
    {
        ThrowIfMaterialPackageCollidesWithCustomMesh(project);
        // Older Material Forge projects saved only the assignment. LoadAvailable migrates those
        // packages into the durable library, so the assignment remains sufficient declarative
        // ownership even when GeneratedMaterials is empty.
        var availablePackages = library.LoadAvailable()
            .Select(material => material.PackagePath)
            .ToList();
        var referencedPackages = ReferencedGeneratedMaterialPackagesForRelease(
            project,
            availablePackages);

        var copied = 0;
        foreach (var package in referencedPackages)
        {
            copied += library.CopyMaterialClosureToContentRoot(package, contentRoot).Count;
        }

        // Every assigned /Game/Mods material must exist in the fresh stage,
        // even when an older project did not retain GeneratedMaterials and the
        // shared library can no longer recover it. Filtering the validation to
        // known library entries would let an assignment disappear silently.
        var requiredAssignedPackages = AssignedModMaterialPackagesForRelease(project);
        var missing = MissingReferencedGeneratedMaterialFiles(requiredAssignedPackages, contentRoot);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Project-generated material staging is incomplete. Missing or empty cooked package files: " +
                string.Join("; ", missing));
        }
        return copied;
    }

    internal static IReadOnlyList<string> AssignedModMaterialPackagesForRelease(NativeSuitProject project) =>
        DeclaredReleaseMaterialPackages(project)
            .Where(package => package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<string> DeclaredCustomMeshPackagesForRelease(NativeSuitProject project) =>
        (project.CustomStaticMeshes ?? new List<CustomStaticMeshImport>())
            .Select(mesh => CustomStaticMeshImportService.MeshPackagePathFor(project, mesh))
            .Select(UnrealPathUtil.NormalizePackagePath)
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<string> MaterialCustomMeshPackageCollisions(NativeSuitProject project)
    {
        var meshPackages = DeclaredCustomMeshPackagesForRelease(project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DeclaredReleaseMaterialPackages(project)
            .Concat((project.GeneratedMaterials ?? new List<GeneratedMaterialEntry>())
                .Select(material => UnrealPathUtil.NormalizePackagePath(material.PackagePath)))
            .Where(meshPackages.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> DeclaredReleaseMaterialPackages(NativeSuitProject project) =>
        (project.MaterialAssignments ?? new List<SavedMaterialAssignment>())
            .Select(assignment => assignment.MiPackagePath)
            .Concat((project.CustomStaticMeshes ?? new List<CustomStaticMeshImport>())
                .SelectMany(mesh => StaticMeshObjProbeService.EffectiveMaterialSlots(mesh)
                    .Select(slot => slot.MaterialPath)))
            .Select(UnrealPathUtil.NormalizePackagePath)
            .Where(package => !string.IsNullOrWhiteSpace(package));

    private static void ThrowIfMaterialPackageCollidesWithCustomMesh(NativeSuitProject project)
    {
        var collisions = MaterialCustomMeshPackageCollisions(project);
        if (collisions.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "A material and a declared custom mesh use the same Unreal package path. " +
            "Rename the material or re-import the mesh before packaging: " +
            string.Join("; ", collisions));
    }

    private static List<string> MissingReferencedGeneratedMaterialFiles(
        IEnumerable<string> referencedPackages,
        string contentRoot)
    {
        var missing = new List<string>();
        foreach (var package in referencedPackages)
        {
            var packageBase = PackagePathToContentPath(contentRoot, package);
            var missingExtensions = new[] { ".uasset", ".uexp" }
                .Where(extension =>
                {
                    var path = packageBase + extension;
                    return !File.Exists(path) || new FileInfo(path).Length == 0;
                })
                .ToList();
            if (missingExtensions.Count > 0)
            {
                missing.Add($"{package} ({string.Join(", ", missingExtensions)})");
            }
        }
        return missing;
    }
}
