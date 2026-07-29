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
    private void LoadProjectIntoUi(NativeSuitProject project)
    {
        _currentProject = project;
        MigratePartGraftInstances(project);

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
        _basePlayableText.Text = project.PlayableTemplate?.Uasset ?? "";
        _baseCutsceneText.Text = project.CutsceneTemplate?.Uasset ?? "";
        _baseDcmdText.Text = project.DcmdTemplate?.Uasset ?? "";
        if (!string.IsNullOrWhiteSpace(project.PackageBaseName))
        {
            // Restore the exact pak name last used for this suit so re-exports keep it.
            // Deliberately do NOT touch _lastAutoPackageBaseName: it means "the last name we
            // AUTO-DERIVED". Claiming a saved (possibly custom) name is auto-derived makes
            // DeriveOutputs - which UseAsBase calls - think it's free to re-derive, silently
            // renaming the suit's pak (e.g. ElectricBP_P -> ElectricLBM2_Electric_P).
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

        var stageRoot = Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitGuiProjects", project.SlotId, "PatchedNameMapStage");
        if (!Directory.Exists(stageRoot))
        {
            AppendLog("  note: no staged content for this suit yet — use Base → Set base suit to (re)build the stage before editing materials.");
        }
        else if (project.PartGrafts.Count > 0)
        {
            // The suit has declarative part grafts - rebuild the graft stage from the clean base
            // and replay ALL parts (+ removals + materials). This is the authoritative restore:
            // it guarantees what you see matches the saved part list regardless of the on-disk
            // stage's staleness. (Fire-and-forget: the rebuild's awaits resume on the UI thread;
            // it's guarded to no-op safely if the part index can't resolve a donor.)
            AppendLog($"  restoring {project.PartGrafts.Count} part(s) + {project.MaterialAssignments.Count} material(s) + saved removals…");
            _ = RebuildGraftStageFromDeclarativeAsync();
        }
        else if (project.MaterialAssignments.Count > 0 || project.Requirements.Any(r => r.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase)))
        {
            // Sync the existing stage with the suit's saved edits so what you see
            // (and can re-edit) matches what was saved.
            AppendLog($"  restoring {project.MaterialAssignments.Count} material assignment(s) + saved removals…");
            ApplySavedMaterials(project, logIfNone: false);
            ApplySavedComponentRemovals(project, logNoRemovals: false);
        }

        SelectComboValue(_toyboxCategoryCombo, "Materials");
        _session.RaiseChanged();
        RefreshToyboxTiles();
        UpdateToyboxChips();
    }

    private void OpenMaterialWizard()
    {
        var mod = ExtractModFolder(_targetPlayableText.Text.Trim()) ?? _modFolderText.Text.Trim();
        var suggested = $"MI_Batman_{(string.IsNullOrWhiteSpace(mod) ? "Suit" : mod)}_{_toyboxSlotLabel.Replace(" ", "").Replace("/", "")}";
        using var wiz = new MaterialWizard(_projectRootText.Text.Trim(), mod, suggested, _currentProject?.GeneratedTextures);
        if (wiz.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(wiz.ResultMiPackagePath))
        {
            return;
        }
        AppendLog($"Created material {wiz.ResultMiPackagePath}");
        SelectComboValue(_toyboxCategoryCombo, "Materials");
        RefreshToyboxTiles();
    }

    /// <summary>
    /// Opens the material wizard seeded with an existing MI as the base. When
    /// <paramref name="editInPlace"/> is true (a material you made) the output
    /// name defaults to the same MI so saving overwrites/edits it; otherwise
    /// (a base-game MI) it suggests a fresh mod-scoped name.
    /// </summary>
    private void OpenMaterialFromBase(string miGamePath, bool editInPlace)
    {
        var diskPath = ResolveMaterialDiskPath(miGamePath, preferExport: editInPlace);
        if (diskPath is null)
        {
            AppendLog(editInPlace
                ? $"Could not find the .uasset for {miGamePath} (export content root). Set your export root in Settings."
                : $"Could not find the .uasset for {miGamePath}. Extract base-game content first (Settings → extracted content root).");
            return;
        }

        var mod = ExtractModFolder(_targetPlayableText.Text.Trim()) ?? _modFolderText.Text.Trim();
        var baseStem = Path.GetFileNameWithoutExtension(diskPath);
        var suggested = editInPlace
            ? baseStem
            : $"MI_{(string.IsNullOrWhiteSpace(mod) ? "Suit" : mod)}_{baseStem.Replace("MI_", "")}_Custom";

        using var wiz = new MaterialWizard(_projectRootText.Text.Trim(), mod, suggested, _currentProject?.GeneratedTextures);
        wiz.PrefillBase(diskPath, suggested, editInPlace);
        if (wiz.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(wiz.ResultMiPackagePath))
        {
            return;
        }
        if (editInPlace)
        {
            RenameGeneratedMaterial(miGamePath, wiz.ResultMiPackagePath);
        }
        AppendLog($"{(editInPlace ? "Edited" : "Created from base")} material {wiz.ResultMiPackagePath}");
        SelectComboValue(_toyboxCategoryCombo, "Materials");
        RefreshToyboxTiles();
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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"Apply to slot [{_toyboxSlotLabel}]", null, (_, _) => ApplyToyboxMaterial(miGamePath));
        menu.Items.Add("Copy /Game path", null, (_, _) => { try { Clipboard.SetText(miGamePath); } catch { /* clipboard busy */ } });
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
    private void ApplySavedMaterials(NativeSuitProject project, bool logIfNone)
    {
        if (project.MaterialAssignments.Count == 0)
        {
            if (logIfNone) AppendLog("  no saved material assignments to re-apply.");
            return;
        }

        var slotId = project.SlotId;
        var playablePkg = project.TargetPackages.Playable;
        var cutscenePkg = project.TargetPackages.Cutscene;
        var service = new MaterialReplaceService(_projectRootText.Text.Trim());
        var reapplied = 0;
        foreach (var m in project.MaterialAssignments)
        {
            if (string.IsNullOrWhiteSpace(m.Component) || !m.MiPackagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var assignment = new MaterialReplaceService.Assignment
            {
                Component = m.Component,
                Slot = m.Slot,
                MiPackagePath = m.MiPackagePath,
                ApplyToPlayable = m.Context is "both" or "playable",
                ApplyToCutscene = m.Context is "both" or "cutscene",
            };
            var result = service.Apply(slotId, playablePkg, cutscenePkg, assignment);
            if (result.Files.Any(f => f.Success))
            {
                reapplied++;
            }
        }
        AppendLog($"  re-applied {reapplied}/{project.MaterialAssignments.Count} saved material assignment(s).");
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
        if (!gd.HasCatalog)
        {
            yield break;
        }

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
    private static string MaterialGroupFolder(string gamePath)
    {
        var p = gamePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? gamePath["/Game/".Length..] : gamePath;
        var segs = p.Split('/');
        if (segs.Length <= 1) return "";
        return segs.Length >= 3 ? $"{segs[0]}/{segs[1]}" : segs[0];
    }

    /// <summary>
    /// Materials rendered as a generated, searchable, paged tile grid - same UX
    /// as the Parts screen. "Your materials" = MIs you generated; a folder (or
    /// &lt;all game materials&gt;) = base-game MIs straight from the shipped catalog
    /// (zero extraction - applying only writes a /Game reference). Click a tile to
    /// apply it to the selected slot.
    /// </summary>
    private void RefreshMaterialTiles(string? type)
    {
        var search = CurrentToyboxSearch();

        if (type == "Your materials")
        {
            var header = $"Materials you generated for slot [{_toyboxSlotLabel}]. Drag a tile onto a slot to apply it; right-click to edit. Use '＋ Create' for a new one, or switch the dropdown to a game folder to pull base-game MIs.";
            var tiles = new List<VirtualTilePanel.Tile>
            {
                new() { Title = "＋ Create", Subtitle = "new material", Accent = Theme.Materials, Dashed = true, OnClick = OpenMaterialWizard }
            };
            var mod = ExtractModFolder(_targetPlayableText.Text.Trim());
            if (string.IsNullOrWhiteSpace(mod))
            {
                ShowVirtualTiles(tiles, header + "\n\nSet a base suit first (Base → Set base) to store generated materials.");
                return;
            }

            foreach (var miPath in DiscoverUserMaterialPaths(mod))
            {
                var name = UnrealPathUtil.AssetName(miPath);
                if (!MatchesToyboxSearch(search, name, miPath))
                {
                    continue;
                }

                tiles.Add(new VirtualTilePanel.Tile
                {
                    Title = name.Replace("MI_", ""),
                    Subtitle = "your MI · drag to apply",
                    Accent = Theme.Materials,
                    DragPayload = new ToyboxDragPayload { Kind = "material", MaterialPath = miPath },
                    MenuFactory = () => BuildMaterialTileMenu(miPath, isUserMade: true),
                });
            }
            ShowVirtualTiles(tiles, header);
            return;
        }

        // Game-material grid from the shipped catalog.
        var gd = GameDataService.Instance;
        if (!gd.HasCatalog)
        {
            ShowVirtualTiles(
                new List<VirtualTilePanel.Tile> { new() { Title = "Browse…", Subtitle = "game MI from disk", Accent = Theme.Materials, OnClick = BrowseAndApplyGameMaterial } },
                "Asset catalog not loaded (ship gamedata/*.json). Use '＋ Create' or the disk browse instead.");
            return;
        }

        var folderFilter = (type is null || type == "<all game materials>") ? null : type;
        var sourceFilter = FilterVal(0);
        var all = gd.AssetsOfClass("MaterialInstanceConstant")
            .Where(a => folderFilter is null || MaterialGroupFolder(a.Path).Equals(folderFilter, StringComparison.OrdinalIgnoreCase))
            .Where(a => sourceFilter is null || MaterialGroupFolder(a.Path).Equals(sourceFilter, StringComparison.OrdinalIgnoreCase))
            .Where(a => MatchesToyboxSearch(search, a.Path, a.Path[(a.Path.LastIndexOf('/') + 1)..]))
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ShowVirtualTiles(
            all.Select(a => new VirtualTilePanel.Tile
            {
                Title = a.Path[(a.Path.LastIndexOf('/') + 1)..].Replace("MI_", ""),
                Subtitle = MaterialGroupFolder(a.Path),
                Accent = Theme.Materials,
                DragPayload = new ToyboxDragPayload { Kind = "material", MaterialPath = a.Path },
                MenuFactory = () => BuildMaterialTileMenu(a.Path, isUserMade: false),
            }).ToList(),
            header: $"Base-game materials{(folderFilter is null ? "" : $" · {folderFilter}")} for slot [{_toyboxSlotLabel}]. Drag onto a slot to apply (no extraction needed); right-click to use one as a base for a new material. Type in the search box to filter.",
            emptyMessage: "No game materials matched. Try <all game materials> or clear the search box.");
    }

    /// <summary>
    /// User-made material instances can be in the optional export root, or in one of the
    /// current suit's persisted authoring stages after a stage rebuild. The latter is the
    /// authoritative source for older projects such as Electric, whose assignments are
    /// valid even though their original export folder is no longer configured.
    /// </summary>
    private IReadOnlyList<string> DiscoverUserMaterialPaths(string mod)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedPrefix = $"/Game/Mods/{mod}/";

        if (_currentProject is not null)
        {
            foreach (var assignment in _currentProject.MaterialAssignments)
            {
                var package = UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath);
                if (package.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(package);
                }
            }
        }

        foreach (var contentRoot in GeneratedMaterialContentRoots(_currentProject))
        {
            var modRoot = Path.Combine(contentRoot, "Mods", mod);
            if (!Directory.Exists(modRoot))
            {
                continue;
            }

            foreach (var uasset in Directory.EnumerateFiles(modRoot, "MI_*.uasset", SearchOption.AllDirectories))
            {
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

        var generatedRoot = AppSettings.GeneratedRootFor(_projectRootText.Text.Trim());
        var projectRoot = Path.Combine(generatedRoot, "NativeSuitGuiProjects", project.SlotId);
        foreach (var stage in new[] { "GraftedPartStage", "GraftedTorso2Stage", "PatchedNameMapStage", "IoStore" })
        {
            yield return stage.Equals("IoStore", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(projectRoot, stage, "Stage", "LEGOBatmanLotDK", "Content")
                : Path.Combine(projectRoot, stage, "LEGOBatmanLotDK", "Content");
        }
    }

    private void RenameGeneratedMaterial(string oldPackagePath, string newPackagePath)
    {
        var oldPackage = UnrealPathUtil.NormalizePackagePath(oldPackagePath);
        var newPackage = UnrealPathUtil.NormalizePackagePath(newPackagePath);
        if (oldPackage.Equals(newPackage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var reassigned = 0;
        if (_currentProject is not null)
        {
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

            if (reassigned > 0)
            {
                (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
                ApplySavedMaterials(_currentProject, logIfNone: false);
            }
        }

        var removed = DeleteGeneratedMaterialFiles(oldPackage);
        AppendLog($"Renamed material {UnrealPathUtil.AssetName(oldPackage)} to {UnrealPathUtil.AssetName(newPackage)}; updated {reassigned} assignment(s), removed {removed} old file(s).");
        RefreshInspector();
    }

    private async Task DeleteGeneratedMaterialAsync(string miPackagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(miPackagePath);
        if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Material delete refused outside /Game/Mods: {package}");
            return;
        }

        var assignments = _currentProject?.MaterialAssignments
            .Where(assignment => UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<SavedMaterialAssignment>();
        var detail = assignments.Count == 0
            ? "It is not assigned to this suit."
            : $"It is assigned to {assignments.Count} slot(s). Those assignments will be removed and the stage rebuilt from the base.";
        if (!Dialog.Confirm(this, "Delete material",
                $"Delete '{UnrealPathUtil.AssetName(package)}'?\n\n{detail}\n\n{package}",
                confirmText: "Delete material", severity: Dialog.Level.Crit))
        {
            return;
        }

        var removedAssignments = 0;
        if (_currentProject is not null && assignments.Count > 0)
        {
            removedAssignments = _currentProject.MaterialAssignments.RemoveAll(assignment =>
                UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath)
                    .Equals(package, StringComparison.OrdinalIgnoreCase));
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        }

        var removedFiles = DeleteGeneratedMaterialFiles(package);
        if (removedAssignments > 0 && _currentProject is not null)
        {
            await RebuildGraftStageFromDeclarativeAsync();
        }

        RecordChange("Materials", UnrealPathUtil.AssetName(package), "deleted", status: "deleted");
        AppendLog($"Deleted material {package}; removed {removedAssignments} assignment(s) and {removedFiles} file(s).");
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

    private void ApplyToyboxMaterial(string miPath)
    {
        _matAssignComponentText.Text = _toyboxComponent;
        _matAssignSlotText.Text = _toyboxSlot.ToString();
        _matAssignMiText.Text = miPath;
        SelectComboValue(_matAssignContextCombo, "both");
        ApplyMaterialAssignment();
    }

    private void PickAndApplyCatalogMaterial()
    {
        var path = PickFromCatalog("MaterialInstanceConstant", "Pick a game material (catalog · no extraction)");
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
        if (!full.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
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
        _matOutputText.Text = "/Game/Mods/ElectricLBM2/MI_Batman_ElectricLBM2_Body";
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
        _matApplyButton.Click += (_, _) => ApplyMaterialAssignment();
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

    private void ApplyMaterialAssignment()
    {
        var slotId = _slotIdText.Text.Trim();
        var component = _matAssignComponentText.Text.Trim();
        var mi = _matAssignMiText.Text.Trim();
        var playablePkg = _targetPlayableText.Text.Trim();
        var cutscenePkg = _targetCutsceneText.Text.Trim();

        if (string.IsNullOrWhiteSpace(slotId)) { AppendLog("Slot ID is empty."); return; }
        if (string.IsNullOrWhiteSpace(component)) { AppendLog("Component is empty."); return; }
        if (!mi.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) { AppendLog("MI path must start with /Game/ (generate a material first)."); return; }
        if (!int.TryParse(_matAssignSlotText.Text.Trim(), out var slot) || slot < 0) { AppendLog("Slot must be a non-negative integer."); return; }

        var context = _matAssignContextCombo.SelectedItem?.ToString() ?? "both";
        var assignment = new MaterialReplaceService.Assignment
        {
            Component = component,
            Slot = slot,
            MiPackagePath = mi,
            ApplyToPlayable = context is "both" or "playable",
            ApplyToCutscene = context is "both" or "cutscene"
        };

        var result = new MaterialReplaceService(_projectRootText.Text.Trim())
            .Apply(slotId, playablePkg, cutscenePkg, assignment);

        AppendLog($"Apply material [{component} slot {slot}] = {mi}: {result.Status}");
        if (result.Files.Any(f => f.Success))
        {
            var miName = mi[(mi.LastIndexOf('/') + 1)..];
            RecordChange("Materials", $"{component} slot {slot}", $"{miName} ({context})");

            // Persist so it survives stage rebuilds and reloads.
            EnsureProject();
            _currentProject!.MaterialAssignments.RemoveAll(m =>
                m.Component.Equals(component, StringComparison.OrdinalIgnoreCase) &&
                m.Slot == slot &&
                m.Context.Equals(context, StringComparison.OrdinalIgnoreCase));
            _currentProject.MaterialAssignments.Add(new SavedMaterialAssignment
            {
                Component = component, Slot = slot, MiPackagePath = mi, Context = context
            });
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        }
        if (!string.IsNullOrWhiteSpace(result.StageContentRoot))
        {
            AppendLog($"  stage: {result.StageContentRoot}");
        }
        foreach (var f in result.Files)
        {
            AppendLog($"  {f.Role}: success={f.Success} componentFound={f.ComponentFound} createdOverrideArray={f.CreatedOverrideArray}{(string.IsNullOrWhiteSpace(f.Error) ? "" : " error=" + f.Error)}");
        }
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppendLog(result.Error);
        }

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
        var mod = ExtractModFolder(project.TargetPackages?.Playable);
        if (string.IsNullOrWhiteSpace(mod))
        {
            return;
        }

        var dst = Path.Combine(contentRootToPackage, "Mods", mod);
        var copied = 0;
        var src = Path.Combine(AppSettings.Current.EffectiveExportContentRoot(), "Mods", mod);
        if (Directory.Exists(src))
        {
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(src.Length).TrimStart('\\', '/');
                // Never overwrite the patched/grafted BP assets.
                if (relative.StartsWith("Characters", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.Combine(dst, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied++;
            }
        }

        AppendLog(copied > 0
            ? $"Staged {copied} generated Mods\\{mod} asset file(s) into the pack content root."
            : $"No generated Mods\\{mod} assets to stage.");
    }
}
