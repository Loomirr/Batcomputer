using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// The right-hand inspector, change tracking, and the log pane.
/// </summary>
public sealed partial class MainForm
{
    private bool _inspectorRemovalInProgress;

    private void RecordChange(string category, string target, string detail, string status = "applied")
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }
        RecordChange(_currentProject, category, target, detail, status);
    }

    /// <summary>Records a change against the project that was actually edited.</summary>
    private void RecordChange(
        NativeSuitProject project,
        string category,
        string target,
        string detail,
        string status = "applied")
    {
        // Collapse duplicates: re-doing an idempotent action (re-picking the same base, re-grafting
        // the same hair, re-applying the same material) previously appended an identical card every
        // time - the review list filled with 8 identical "Base" entries. Drop any prior entry with
        // the same Category+Target+Detail so this one refreshes in place (updated time, moved to top)
        // instead of stacking. Genuinely distinct edits differ in Target or Detail and are kept.
        project.Changes.RemoveAll(c =>
            string.Equals(c.Category, category, StringComparison.Ordinal) &&
            string.Equals(c.Target, target, StringComparison.Ordinal) &&
            string.Equals(c.Detail, detail, StringComparison.Ordinal));
        project.Changes.Add(new SavedChange
        {
            When = DateTime.Now.ToString("o"),
            Category = category,
            Target = target,
            Detail = detail,
            Status = status,
        });
        try
        {
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(project);
        }
        catch { /* best effort — a save failure shouldn't break the edit */ }
        _session.RaiseChanged();
    }

    private void CopyChangeSummary()
    {
        if (Changes.Count == 0)
        {
            AppendLog("No changes recorded for this suit yet.");
            return;
        }

        var lines = Changes.Select(c =>
            $"[{c.Status}] {c.Category} · {c.Target}: {c.Detail} ({FormatWhen(c.When)})");
        var summary = $"Suit: {_suitNameText.Text}  (mod {_modFolderText.Text})" +
            Environment.NewLine + string.Join(Environment.NewLine, lines);
        try
        {
            Clipboard.SetText(summary);
            AppendLog($"Copied {Changes.Count} change(s) to clipboard.");
        }
        catch (Exception ex)
        {
            AppendLog($"Copy failed: {ex.Message}");
        }
    }

    private Control CreateInspectorPanel()
    {
        // The Inspector's visual composition lives in the designer-editable InspectorControl.
        // MainForm keeps the refresh/drag logic and wires it to the control's exposed children.
        _inspector.Dock = DockStyle.Fill;
        _inspector.Margin = new Padding(3);
        _inspector.RefreshRequested += (_, _) => RefreshInspector();
        _inspector.RoleChanged += (_, _) => RefreshInspector();
        _inspector.BreakdownRequested += async (_, _) => await ViewAssetBreakdownAsync(_inspector.Role);
        _inspector.PreflightRequested += (_, _) => RunV2PreflightFromUi();
        _inspector.ResolveMaterialPath = data =>
        {
            var payload = TryGetToyboxDragPayload(data);
            return payload is not null && payload.Kind.Equals("material", StringComparison.OrdinalIgnoreCase)
                ? payload.MaterialPath
                : null;
        };
        _inspector.ComponentSelected += component =>
        {
            var first = _characterSlots.FirstOrDefault(t =>
                t.Component.Equals(component, StringComparison.OrdinalIgnoreCase));
            if (first.Component is not null)
            {
                SelectToyboxSlot(first.Label, first.Component, first.Slot);
            }
        };
        _inspector.ComponentRemoveRequested += component =>
            _ = RemoveInspectorComponentAsync(component);
        _inspector.SlotSelected += (component, slot) =>
            SelectToyboxSlot(FriendlySlotLabel(component, slot), component, slot);
        _inspector.SlotMaterialDropped += (component, slot, materialPath) =>
        {
            SelectToyboxSlot(FriendlySlotLabel(component, slot), component, slot);
            AppendLog($"Dropped material {materialPath} onto inspector {component} slot {slot}.");
            ApplyToyboxMaterial(materialPath);
            RefreshInspector();
        };
        return _inspector;
    }

    private async Task RemoveInspectorComponentAsync(string component)
    {
        if (string.IsNullOrWhiteSpace(component) || _inspectorRemovalInProgress)
        {
            return;
        }

        _inspectorRemovalInProgress = true;
        try
        {
            var project = _currentProject;
            var customMesh = FindCustomStaticMeshForComponent(project, component);
            if (customMesh is not null)
            {
                await RemoveCustomStaticMeshAsync(customMesh);
                return;
            }

            if (project is null)
            {
                Dialog.Warn(this, "No active suit", "Open a suit before removing one of its parts.");
                return;
            }

            var activeGlider = ActiveGliderVisualComponent(project);
            if (!string.IsNullOrWhiteSpace(activeGlider) &&
                activeGlider.Equals(component, StringComparison.OrdinalIgnoreCase) &&
                !PairedCapeAdapterTargetsComponent(project, component))
            {
                if (Dialog.Confirm(
                        this,
                        "Remove custom glider",
                        "Remove this custom glider and restore the gameplay donor's original glide visual?"))
                {
                    await ClearCustomGliderAsync();
                }
                return;
            }

            const int componentSlot = 0;
            var first = _characterSlots.FirstOrDefault(slot =>
                slot.Component.Equals(component, StringComparison.OrdinalIgnoreCase));
            var label = first.Component is null
                ? FriendlySlotLabel(component, componentSlot)
                : first.Label;
            if (!Dialog.Confirm(
                    this,
                    "Remove part from suit",
                    $"Remove '{label}' from this suit?\n\n" +
                    "Batcomputer will rebuild both the playable and cutscene versions. " +
                    "Your original game files are not changed."))
            {
                return;
            }

            if (first.Component is not null)
            {
                SelectToyboxSlot(label, component, componentSlot);
            }

            await RemoveToyboxPartAsync(label, component, componentSlot);
        }
        catch (Exception ex)
        {
            AppendLog($"Inspector removal failed for '{component}': {ex}");
            Dialog.Error(
                this,
                "Part was not removed",
                "Batcomputer could not finish the inspector removal. The saved suit remains active.\n\n" +
                ex.Message,
                windowTitle: "Parts");
        }
        finally
        {
            _inspectorRemovalInProgress = false;
        }
    }

    private Control CreateInspectorTabs()
    {
        _inspectorTabs.Dock = DockStyle.Fill;
        _inspectorTabs.Margin = new Padding(3);
        _researchInspector.Dock = DockStyle.Fill;
        _researchInspector.CopyPathButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_researchInspector.SelectedPackagePath)) return;
            try
            {
                Clipboard.SetText(_researchInspector.SelectedPackagePath);
                AppendLog("Copied research package path to the clipboard.");
            }
            catch { /* clipboard may be busy */ }
        };
        _inspectorTabs.AddTab(SuitTabName, CreateInspectorPanel());
        _inspectorTabs.AddTab(NotebookTabName, CreateModNotebookPanel());
        if (AppSettings.Current.ShowResearchTools)
        {
            _inspectorTabs.AddTab(ResearchTabName, _researchInspector);
        }
        return _inspectorTabs;
    }

    internal static CustomStaticMeshImport? FindCustomStaticMeshForComponent(
        NativeSuitProject? project,
        string component)
    {
        if (project?.CustomStaticMeshes is not { Count: > 0 } || string.IsNullOrWhiteSpace(component))
        {
            return null;
        }

        static string WithoutSlot(string value)
        {
            var colon = value.IndexOf(':');
            return (colon >= 0 ? value[..colon] : value).Trim();
        }

        var expected = WithoutSlot(component);
        return project.CustomStaticMeshes.FirstOrDefault(mesh =>
            WithoutSlot(CustomStaticMeshImportService.ComponentNameFor(mesh))
                .Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    // Inspector material drops are handled by InspectorControl's per-slot rows now
    // (see _inspector.SlotMaterialDropped wiring) - no tree hit-testing needed.

    /// <summary>
    /// Rebuilds the inspector: identity, the component cards for the selected role, and the issue
    /// list. <see cref="_customSlotKeys"/> is still populated from the PLAYABLE role regardless of
    /// which role is being viewed, because that's what drives the minifig's customized state.
    /// </summary>
    private void RefreshInspector()
    {
        if (DeferStageBackedRefreshWhileLoadedProjectRestores())
        {
            return;
        }

        _isRefreshingInspector = true;
        try
        {
            SyncProjectFieldsForViews();
            var slotId = _slotIdText.Text.Trim();
            var pak = CurrentPackageBaseName();
            var packaged = false;
            try
            {
                var utoc = Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitGuiProjects", slotId, "IoStore", pak + ".utoc");
                packaged = !string.IsNullOrWhiteSpace(slotId) && File.Exists(utoc);
            }
            catch { /* best effort */ }

            _inspector.SetIdentity(
                _suitNameText.Text.Trim(),
                ExtractModFolder(_targetPlayableText.Text.Trim()) ?? "",
                slotId,
                packaged);

            if (string.IsNullOrWhiteSpace(slotId))
            {
                _inspector.SetMessage("Set a base suit first (Base category → Set base).");
                _customSlotKeys.Clear();
                UpdateSlotDots();
                UpdateToyboxChips();
                return;
            }

            _customSlotKeys.Clear();
            var service = new MaterialReplaceService(_projectRootText.Text.Trim());
            var rows = new List<InspectorControl.ComponentRow>();
            var issues = new List<InspectorControl.IssueRow>();
            var reports = new Dictionary<string, MaterialReplaceService.InspectorReport>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var role in new[] { "playable", "cutscene" })
            {
                try
                {
                    var targetPackage = role.Equals("cutscene", StringComparison.OrdinalIgnoreCase)
                        ? _targetCutsceneText.Text.Trim()
                        : _targetPlayableText.Text.Trim();
                    reports[role] = service.DescribeStageComponents(slotId, role, targetPackage);
                }
                catch (Exception ex)
                {
                    issues.Add(new InspectorControl.IssueRow
                    {
                        Title = $"{Capitalize(role)} could not be read",
                        Detail = ex.Message,
                        Level = InspectorControl.Severity.Crit,
                    });
                }
            }

            foreach (var role in new[] { "playable", "cutscene" })
            {
                if (!reports.TryGetValue(role, out var report))
                {
                    continue;
                }

                var isViewed = role.Equals(_inspector.Role, StringComparison.OrdinalIgnoreCase);

                if (!report.Found && isViewed)
                {
                    issues.Add(new InspectorControl.IssueRow
                    {
                        Title = $"{Capitalize(role)} not staged",
                        Detail = report.Message ?? "",
                        Level = InspectorControl.Severity.Warn,
                    });
                }

                foreach (var comp in report.Components)
                {
                    if (_currentProject?.Requirements.Any(requirement =>
                            requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
                            RequirementTargetsComponent(requirement.TargetComponent, comp.Name)) == true)
                    {
                        // Preserve-node hides remain physically present for Blueprint safety, but
                        // they are declaratively absent from the authored suit just like an
                        // ordinary unlinked component. Do not make a hidden Head appear to survive.
                        continue;
                    }

                    var customMesh = FindCustomStaticMeshForComponent(_currentProject, comp.Name);
                    var friendlyComponent = customMesh?.DisplayName?.Trim();
                    var friendlyMesh = customMesh is null
                        ? ""
                        : string.IsNullOrWhiteSpace(customMesh.SourceObjRelativePath)
                            ? "Imported custom mesh"
                            : "OBJ · " + Path.GetFileName(customMesh.SourceObjRelativePath);
                    if (role == "playable")
                    {
                        foreach (var s in comp.Slots.Where(s => !s.IsDefault))
                        {
                            _customSlotKeys.Add($"{comp.Name}:{s.Slot}");
                        }
                    }

                    if (!isViewed)
                    {
                        continue;
                    }

                    var removableInBothRoles = new[] { "playable", "cutscene" }.All(requiredRole =>
                        reports.TryGetValue(requiredRole, out var requiredReport) &&
                        requiredReport.Found &&
                        requiredReport.Components.Any(requiredComponent =>
                            requiredComponent.IsScsCreated &&
                            requiredComponent.Name.Equals(comp.Name, StringComparison.OrdinalIgnoreCase)));

                    rows.Add(new InspectorControl.ComponentRow
                    {
                        Name = comp.Name,
                        DisplayName = friendlyComponent ?? "",
                        Class = comp.Class,
                        Mesh = comp.Mesh,
                        DisplayMesh = friendlyMesh,
                        // A normal component edit always rebuilds both character roles. Do not
                        // offer an action that is known up-front to succeed in only the viewed role.
                        // Project-owned custom meshes remain removable even from a damaged stage so
                        // the declarative cleanup path can recover the suit.
                        CanRemove = customMesh is not null || removableInBothRoles,
                        RemoveDisabledText = comp.IsScsCreated
                            ? "Not removable — matching playable/cutscene component is missing"
                            : "Inherited body — choose a replacement instead",
                        Slots = comp.Slots.OrderBy(s => s.Slot).Select(s => new InspectorControl.SlotRow
                        {
                            Slot = s.Slot,
                            Material = s.Material,
                            Overridden = !s.IsDefault,
                        }).ToList(),
                    });

                    foreach (var s in comp.Slots.Where(s => string.IsNullOrWhiteSpace(s.Material)))
                    {
                        issues.Add(new InspectorControl.IssueRow
                        {
                            Title = $"{(friendlyComponent ?? comp.Name)} slot {s.Slot} has no material",
                            Detail = "Ships with the mesh default. Drop one, or ignore.",
                            Level = InspectorControl.Severity.Crit,
                        });
                    }
                }
            }

            // The one preflight rule that's cheap enough to surface live. Note the three-way status:
            // a base we cannot READ is not a base without a cape, and reporting it as one made suits
            // that glide perfectly well show a defect they did not have.
            if (_currentProject is not null)
            {
                var status = new AnimArchetypeGraftService().BaseGlideVisual(_currentProject, out _);
                if (status == AnimArchetypeGraftService.GlideVisualStatus.Absent)
                {
                    issues.Add(new InspectorControl.IssueRow
                    {
                        Title = "No native glide visual on this base",
                        Detail = "Gliders can't be shown on a civilian base.",
                        Level = InspectorControl.Severity.Warn,
                    });
                }
                else if (status == AnimArchetypeGraftService.GlideVisualStatus.Unknown)
                {
                    var missing = _currentProject.PlayableTemplate?.Uasset;
                    issues.Add(new InspectorControl.IssueRow
                    {
                        Title = "Base asset is missing",
                        Detail = string.IsNullOrWhiteSpace(missing)
                            ? "No base template is recorded, so base checks are skipped."
                            : $"Not found: {UnrealPathUtil.AssetName(missing)}. Its asset dump was probably " +
                              "replaced by a newer extract. Re-pick the base to refresh the path - checks " +
                              "that read the base are skipped until then.",
                        Level = InspectorControl.Severity.Warn,
                    });
                }
            }

            _inspector.SetComponents(rows);
            _inspector.SetIssues(issues);
        }
        finally
        {
            _isRefreshingInspector = false;
            RefreshModNotebook();
        }

        if (!string.IsNullOrWhiteSpace(_pendingInspectorComponentFocus))
        {
            SelectInspectorNodeForSlot(_pendingInspectorComponentFocus, _pendingInspectorSlotFocus);
        }
        UpdateSlotDots();
        UpdateToyboxChips();
    }

    /// <summary>
    /// Lets the user pick any base-game asset of a class. Material instances come from the active
    /// extracted Content tree plus the bundled fallback; other classes use the bundled catalog.
    /// Returns the /Game object path (Package.ObjectName) or null if cancelled.
    /// </summary>
    private string? PickFromCatalog(string className, string title)
    {
        var gd = GameDataService.Instance;
        var assets = gd.AssetsOfClass(className)
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (assets.Count == 0)
        {
            AppendLog($"No '{className}' assets were found in the active extraction or bundled catalog.");
            return null;
        }

        using var dlg = new AdaptiveDialogForm
        {
            Text = title,
            Width = 760,
            Height = 560,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(600, 420),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);
        var search = new TextBox { Dock = DockStyle.Top, Height = 30, PlaceholderText = $"Filter {assets.Count} {className}…" };
        Theme.StyleDarkInput(search);
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.None };
        Theme.StyleListBox(list);
        var ok = new Button { Text = "Use selected", Dock = DockStyle.Bottom, Height = 34 };
        Theme.StyleGoldButton(ok);
        ok.DialogResult = DialogResult.OK;

        void Fill(string term)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var a in assets)
            {
                if (string.IsNullOrWhiteSpace(term) || a.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    list.Items.Add(a.Path);
                }
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        list.DoubleClick += (_, _) => { if (list.SelectedItem is not null) { ok.PerformClick(); } };
        Fill("");

        dlg.Controls.Add(list);
        dlg.Controls.Add(search);
        dlg.Controls.Add(ok);
        if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedItem is null)
        {
            return null;
        }

        var pkg = list.SelectedItem.ToString()!;
        var leaf = pkg[(pkg.LastIndexOf('/') + 1)..];
        return $"{pkg}.{leaf}"; // /Game/...Path.ObjectName
    }

    private async Task ViewAssetBreakdownAsync(string role)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("open the asset breakdown"))
        {
            return;
        }

        var slotId = _slotIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(slotId))
        {
            AppendLog("Set a base suit first (Advanced → Base suit).");
            return;
        }

        MaterialReplaceService.InspectorReport LoadReport()
        {
            var targetPackage = role.Equals("cutscene", StringComparison.OrdinalIgnoreCase)
                ? _targetCutsceneText.Text.Trim()
                : _targetPlayableText.Text.Trim();
            return new MaterialReplaceService(_projectRootText.Text.Trim())
                .DescribeStageComponents(slotId, role, targetPackage);
        }

        MaterialReplaceService.InspectorReport report;
        try
        {
            report = LoadReport();
        }
        catch (Exception ex)
        {
            AppendLog($"View {role} failed: {ex.Message}");
            return;
        }

        using var dlg = new AdaptiveDialogForm
        {
            Text = $"{role} materials" + (string.IsNullOrWhiteSpace(report.AssetFile) ? "" : $" — {report.AssetFile}"),
            Width = 760,
            Height = 560,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(620, 440),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(8, 8, 8, 0),
            ForeColor = Theme.OnDarkMuted,
            Text = report.Found
                ? $"Materials on the {role} character. Select a slot and click “Edit slot” to open it under Materials, then drag a material onto it."
                : (report.Message ?? "No staged content to inspect.")
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BackColor = Theme.CardBg,
            ForeColor = Theme.OnDark,
            BorderStyle = BorderStyle.None,
        };
        Theme.StyleListView(list);
        list.Columns.Add("Component", 220);
        list.Columns.Add("Slot", 48);
        list.Columns.Add("Current material", 460);

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var comp in report.Components)
            {
                if (comp.Slots.Count == 0)
                {
                    var row = new ListViewItem(comp.Name);
                    row.SubItems.Add("—");
                    row.SubItems.Add("(no material slots)");
                    row.ForeColor = Theme.OnDarkMuted;
                    list.Items.Add(row);
                    continue;
                }
                foreach (var s in comp.Slots)
                {
                    var row = new ListViewItem(comp.Name);
                    row.SubItems.Add(s.Slot.ToString());
                    row.SubItems.Add(s.IsDefault ? "(mesh default)" : s.Material);
                    if (s.IsDefault) row.ForeColor = Theme.OnDarkMuted;
                    row.Tag = (comp.Name, s.Slot);
                    list.Items.Add(row);
                }
            }
            list.EndUpdate();
        }
        Fill();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var close = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(close);
        var edit = new Button { Text = "Edit slot →", Width = 120, Height = 30 };
        Theme.StyleGoldButton(edit);

        void EditSelected()
        {
            if (list.SelectedItems.Count == 0 ||
                list.SelectedItems[0].Tag is not ValueTuple<string, int> target)
            {
                return;
            }
            var (component, slot) = target;
            SelectComboValue(_toyboxCategoryCombo, "Materials");
            SelectToyboxSlot(FriendlySlotLabel(component, slot), component, slot);
            AppendLog($"Editing {role} material — selected {component} slot {slot}. Drag or create a material for it under Materials.");
            dlg.Close();
        }

        edit.Click += (_, _) => EditSelected();
        list.DoubleClick += (_, _) => EditSelected();
        buttons.Controls.Add(close);
        buttons.Controls.Add(edit);

        dlg.Controls.Add(list);
        dlg.Controls.Add(buttons);
        dlg.Controls.Add(info);
        dlg.ShowDialog(this);
    }

    private void VerifyLastGameLogForCurrentSuit()
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Verifying the current suit log"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var slotId = _currentProject.SlotId;
        var packageBaseName = CurrentPackageBaseName();
        var logPath = EffectiveGameUe4ssLogPath();

        AppendLog($"Verifying last UE4SS log for slot '{slotId}'…");
        if (!File.Exists(logPath))
        {
            AppendLog($"  ✗ UE4SS.log not found: {logPath}");
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadLines(logPath).TakeLast(3500).ToArray();
        }
        catch (Exception ex)
        {
            AppendLog($"  ✗ failed to read UE4SS.log: {ex.Message}");
            return;
        }

        LogMarker(lines, "V2 runtime ready", "MULTI_SUIT_CONFIG_READY", mustContainSlot: false);
        LogMarker(lines, "suit JSON loaded", "Suit JSON loaded", slotId);
        LogMarker(lines, "button injected", "SLOT_INJECTED", slotId);
        LogMarker(lines, "DCMD resolved", "DONOR_DCMD_BRIDGE_DCMD_RESOLVE", slotId);
        LogMarker(lines, "donor patched", "DONOR_DCMD_BRIDGE_PATCH", slotId);
        LogMarker(lines, "self-bounce probe", "SINGLE_DONOR_SELF_BOUNCE_END", mustContainSlot: false);

        var packageHits = lines.Count(line => line.Contains(packageBaseName, StringComparison.OrdinalIgnoreCase));
        AppendLog(packageHits > 0
            ? $"  ✓ package name appeared in log {packageHits} time(s): {packageBaseName}"
            : $"  • package name did not appear in recent log window: {packageBaseName}");

        void LogMarker(string[] recentLines, string label, string marker, string? mustContain = null, bool mustContainSlot = true)
        {
            var matches = recentLines
                .Where(line => line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Where(line => !mustContainSlot || string.IsNullOrWhiteSpace(mustContain) || line.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                AppendLog($"  ✗ {label}: no recent '{marker}' line{(mustContainSlot && !string.IsNullOrWhiteSpace(mustContain) ? $" for {mustContain}" : "")}.");
                return;
            }

            AppendLog($"  ✓ {label}: {matches.Count} recent hit(s). Last: {TrimLogLine(matches[^1], 260)}");
        }
    }

    private string EffectiveGameUe4ssLogPath() =>
        Path.Combine(EffectiveGameRootFolder(), "Binaries", "Win64", "ue4ss", "UE4SS.log");

    private static string TrimLogLine(string line, int maxLength)
    {
        line = line.Trim();
        if (line.Length <= maxLength)
        {
            return line;
        }

        return line[..Math.Max(0, maxLength - 3)] + "...";
    }

    // Adapter: preserves the original AppendLog entry point every handler calls, delegating the
    // rendering to the extracted designer-editable DiagnosticsControl. Behavior-identical.
    private void AppendLog(string message) => _diagnostics.AppendLog(message);
}
