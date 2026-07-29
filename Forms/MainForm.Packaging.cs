using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Staging, preflight, packaging to IoStore, and installing to the game.
/// </summary>
public sealed partial class MainForm
{
    private void BuildLayout()
    {
        SuspendLayout();
        _mainWorkspaceHost.Controls.Clear();
        _mainLogGroupBox.Controls.Clear();

        // Suit name / mod folder / settings now live in the Builder header
        // (CreateToyboxHeader). Keep their behavior hooks here.
        _settingsButton.Click += (_, _) => OpenSettings();
        _suitNameText.TextChanged += (_, _) => DeriveOutputs();
        _modFolderText.TextChanged += (_, _) => DeriveOutputs();

        // The current Home/toybox workflow owns the whole window. The retired tabbed fallback is
        // deliberately not created so old controls cannot resurface through a hidden window.
        var assembly = new CharacterAssemblyControl { Dock = DockStyle.Fill };
        assembly.HostContent(CreateToyboxPanel());
        _mainWorkspaceHost.Controls.Add(assembly);

        // Collapsible diagnostics drawer: a click-to-toggle header bar over the log.
        _mainLogGroupBox.Text = "";
        var diagHeader = new Button
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = "▾  Diagnostics",
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        Theme.StyleSmallDarkButton(diagHeader);
        diagHeader.Click += (_, _) => ToggleDiagnostics(diagHeader);
        _diagnostics.Dock = DockStyle.Fill;
        _mainLogGroupBox.Controls.Add(_diagnostics);
        _mainLogGroupBox.Controls.Add(diagHeader);
        ResumeLayout(true);
    }

    /// <summary>
    /// The ☰ overflow menu - everything that isn't the primary Build mod command, grouped by intent:
    /// suit lifecycle, this-suit build tools, library-wide actions, then settings.
    /// </summary>
    private ContextMenuStrip BuildMainMenu()
    {
        var menu = new ContextMenuStrip { BackColor = Theme.CardBg, ForeColor = Theme.OnDark, ShowImageMargin = false };

        menu.Items.Add("New suit", null, (_, _) => StartNewSuit());
        menu.Items.Add("Open suit…", null, (_, _) => LoadSuit());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Preview package…", null, (_, _) => ShowPackageContentsPreview());
        menu.Items.Add("Rebase suit to current dump…", null, (_, _) => RebaseCurrentSuitToActiveDump());
        menu.Items.Add("Clean generated output…", null, (_, _) => CleanGeneratedOutputForCurrentSuit());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Update ALL mods…", null, (_, _) => { _ = UpdateAllModsAsync(); });
        // Reuse the existing refresh menu (it already carries the research-profile warning).
        menu.Items.Add(new ToolStripMenuItem("Refresh game assets") { DropDown = BuildAssetRefreshMenu() });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        return menu;
    }

    private Control BuildSectionDivider(string text)
    {
        var lbl = new Label
        {
            Text = text,
            Width = 206,
            Height = 20,
            Margin = new Padding(2, 8, 2, 2),
            ForeColor = Theme.Gold,
            Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0)
        };
        return lbl;
    }

    /// <summary>
    /// A character-panel row for a non-mesh aspect (glider/equipment/animations).
    /// Clicking jumps to its category. When <paramref name="onMaterialDrop"/> is set,
    /// the row accepts MATERIAL drops only (parts are rejected) and forwards the
    /// dropped material's /Game path.
    /// </summary>
    private Control BuildActionRow(string label, string subtitle, Color accent, Action onClick, Action<string>? onMaterialDrop)
    {
        var row = new RoundedPanel { Width = 206, Height = 42, Margin = new Padding(2, 2, 2, 2), BackColor = Theme.CardBg, CornerRadius = Theme.RadiusSm, Cursor = Cursors.Hand };
        var dot = new StatusDot { Width = 10, Height = 10, Left = 8, Top = 16, DotColor = accent };
        var name = new Label { Text = label, Left = 24, Top = 4, Width = 174, Height = 16, AutoSize = false, BackColor = Color.Transparent, ForeColor = Theme.OnDark, Font = new Font(Font.FontFamily, 9f, FontStyle.Bold) };
        // AutoEllipsis + generous height keeps a long, unbreakable material token on a
        // single line (truncated with "…") instead of wrapping off the visible row.
        var sub = new Label { Text = subtitle, Left = 24, Top = 21, Width = 178, Height = 16, AutoSize = false, AutoEllipsis = true, BackColor = Color.Transparent, ForeColor = Theme.OnDarkMuted, Font = new Font(Font.FontFamily, 7.5f) };
        _toyboxToolTip.SetToolTip(sub, subtitle);

        void Click(object? s, EventArgs e) => onClick();
        row.Click += Click; name.Click += Click; sub.Click += Click; dot.Click += Click;

        if (onMaterialDrop is not null)
        {
            foreach (var c in new Control[] { row, name, sub, dot })
            {
                WireMaterialOnlyDropTarget(c, row, accent, onMaterialDrop);
            }
        }

        row.Controls.Add(dot);
        row.Controls.Add(name);
        row.Controls.Add(sub);
        return row;
    }

    private ContextMenuStrip BuildSuitTileMenu(SuitProjectService.ProjectSummary summary)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Set cover image...", null, (_, _) => SetSuitCoverImage(summary));
        menu.Items.Add("Use generated suit icon", null, (_, _) => UseGeneratedSuitIconAsCover(summary));
        menu.Items.Add("Clear cover image", null, (_, _) => ClearSuitCoverImage(summary));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete from game", null, (_, _) => DeleteSavedSuit(summary, deleteFromGame: true, deleteFromTool: false));
        menu.Items.Add("Delete from tool", null, (_, _) => DeleteSavedSuit(summary, deleteFromGame: false, deleteFromTool: true));
        menu.Items.Add("Delete from tool and game", null, (_, _) => DeleteSavedSuit(summary, deleteFromGame: true, deleteFromTool: true));
        return menu;
    }

    private Control BuildBaseRow(string label, TextBox textBox)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 2, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        textBox.Dock = DockStyle.Fill;
        row.Controls.Add(textBox, 1, 0);
        var browse = new Button { Text = "Browse", Dock = DockStyle.Fill };
        browse.Click += (_, _) => BrowseUassetInto(textBox);
        row.Controls.Add(browse, 2, 0);
        return row;
    }

    private Control CreatePackagePanel()
    {
        var box = new GroupBox { Dock = DockStyle.Fill, Text = "Package and install" };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        box.Controls.Add(layout);

        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Build and install mods. A mod may contain one suit, but the mod is always the release unit." }, 0, 0);

        _packagePatchedIoStoreButton.Text = "Build mod for current suit";
        _packagePatchedIoStoreButton.Dock = DockStyle.Left;
        _packagePatchedIoStoreButton.Width = 220;
        layout.Controls.Add(_packagePatchedIoStoreButton, 0, 1);

        _installButton.Text = "Install containing mod";
        _installButton.Dock = DockStyle.Left;
        _installButton.Width = 260;
        _installButton.Click += (_, _) => InstallModForCurrentSuit();
        layout.Controls.Add(_installButton, 0, 2);

        _verifyGameLogButton.Text = "Verify last UE4SS log";
        _verifyGameLogButton.Dock = DockStyle.Left;
        _verifyGameLogButton.Width = 220;
        _verifyGameLogButton.Click += (_, _) => VerifyLastGameLogForCurrentSuit();
        layout.Controls.Add(_verifyGameLogButton, 0, 3);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkOrange,
            Text = "After install: launch the game, F8 probes paths, Ctrl+F8 runs the self-bounce donor test, F9 runs the old command swap. Remove older paks with the same /Game/Mods paths so the new trio wins the mount."
        }, 0, 4);
        return box;
    }

    private static bool IsGamePackagePath(string? packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidateUseAsBaseTargetPackages(NativeSuitProject project)
    {
        var missing = new List<string>();
        if (!IsGamePackagePath(project.TargetPackages.Playable))
        {
            missing.Add("playable");
        }
        if (!IsGamePackagePath(project.TargetPackages.Cutscene))
        {
            missing.Add("cutscene");
        }
        if (project.DcmdTemplate is not null && !IsGamePackagePath(project.TargetPackages.Dcmd))
        {
            missing.Add("DCMD");
        }

        if (missing.Count == 0)
        {
            return true;
        }

        AppendLog("Use-as-base failed: target package paths are not ready for " + string.Join(", ", missing) + ".");
        AppendLog("Start a new suit or fill the suit name/mod folder first so the tool can create /Game/Mods/<Mod>/Characters/... paths.");
        return false;
    }

    private void InstallTrio()
    {
        EnsureProject();
        if (_currentProject is not null)
        {
            ReadFieldsIntoProject(_currentProject);
        }

        var slotId = _slotIdText.Text.Trim();
        var ioStoreDir = Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitGuiProjects", slotId, "IoStore");
        var dest = AppSettings.Current.EffectiveGamePaksModFolder();
        if (!Directory.Exists(ioStoreDir))
        {
            AppendLog($"No packaged trio at {ioStoreDir}. Package it first (button above).");
            return;
        }
        try
        {
            Directory.CreateDirectory(dest);
            var count = 0;
            foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
            {
                foreach (var file in Directory.GetFiles(ioStoreDir, "*" + ext))
                {
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
                    count++;
                }
            }

            string jsonInstallMessage = "";
            if (_currentProject is not null)
            {
                var runtimeJson = StageRuntimeV2SuitJson(_currentProject);
                var suitsRoot = EffectiveGameRuntimeSuitsFolder();
                var suitJsonDir = Path.Combine(suitsRoot, slotId);
                Directory.CreateDirectory(suitJsonDir);
                var installedJson = Path.Combine(suitJsonDir, "suit.json");
                File.Copy(runtimeJson, installedJson, overwrite: true);
                jsonInstallMessage = $" Runtime JSON installed to {installedJson}.";
            }

            AppendLog(count == 0
                ? $"No .pak/.ucas/.utoc found in {ioStoreDir}.{jsonInstallMessage}"
                : $"Installed {count} file(s) to {dest}.{jsonInstallMessage}");
        }
        catch (Exception ex)
        {
            AppendLog("Install failed: " + ex.Message);
        }
    }

    private ContextMenuStrip BuildAssetRefreshMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh all character assets", null, (_, _) =>
        {
            _ = RefreshGameAssetsAsync(GameAssetRefreshService.RefreshProfile.AllCharacterAssets);
        });
        menu.Items.Add("Refresh Batman donor assets", null, (_, _) =>
        {
            _ = RefreshGameAssetsAsync(GameAssetRefreshService.RefreshProfile.BatmanDonors);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Full refresh", null, (_, _) =>
        {
            var answer = Dialog.Confirm(this,
                "Developer research refresh",
                "This extracts character, animation, equipment, UI, collectables, and GameFeature research assets. It takes longer and uses substantially more disk space.",
                confirmText: "Extract");
            if (answer)
            {
                _ = RefreshGameAssetsAsync(GameAssetRefreshService.RefreshProfile.DeveloperResearch);
            }
        });
        return menu;
    }

    private void StageUnpatchedFiles()
    {
        EnsureProject();
        if (_currentProject is null || _projectService is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var stageRoot = _projectService.CreateUnpatchedStage(_currentProject);
        AppendLog($"Staged unpatched donor package files under: {stageRoot}");
        AppendLog("These files are NOT game-ready yet. They still need UAssetAPI internal package/class/name-map rewriting.");
    }

    private async Task PackagePatchedIoStoreAsync()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        // In batch mode the bulk operation owns the progress window (one per suit would flicker).
        using var progress = _batchMode ? null : new ProgressDialog(this, "Packaging suit");
        _packageProgress = progress ?? _packageProgress;
        try
        {
            await PackagePatchedIoStoreCoreAsync();
        }
        finally
        {
            if (progress is not null)
            {
                _packageProgress = null;
            }
        }
    }

    private void PackageStep(string detail)
    {
        _packageProgress?.Report(detail);
        AppendLog(detail);
    }

    private async Task PackagePatchedIoStoreCoreAsync()
    {
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);

        var projectRoot = _projectRootText.Text.Trim();
        var slotId = _currentProject.SlotId;
        var packageBaseName = CurrentPackageBaseName();
        // Immutable build id for this package attempt - stamped into the runtime suit.json and the
        // build manifest so the shipped pak and its manifest stay cross-referenced.
        var buildId = Guid.NewGuid().ToString("N");
        // Persist the pak name used for this export so it's the default next time.
        _currentProject.PackageBaseName = packageBaseName;
        _lastAutoPackageBaseName = packageBaseName;
        try { (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject); } catch { /* best effort */ }
        var script = Path.Combine(projectRoot, "tools", "Build-NativeSuitGuiPatchedIoStore.ps1");

        var packageGliderComponent = ActiveGliderVisualComponent(_currentProject);
        if (!string.IsNullOrWhiteSpace(packageGliderComponent))
        {
            if (RemoveSavedRemovalForComponent(_currentProject, packageGliderComponent))
            {
                AppendLog($"Package: removed stale remove-component rule for active glider component '{packageGliderComponent}'.");
                try { (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject); } catch { /* best effort */ }
            }

            RestoreProtectedGliderComponent(_currentProject, packageGliderComponent);
        }

        ApplySavedComponentRemovals(_currentProject, logNoRemovals: false);
        var contentRootToPackage = CurrentPackageContentRoot(_currentProject);
        if (!File.Exists(script))
        {
            AppendLog($"IoStore packaging script not found: {script}");
            return;
        }

        AppendLog("Packaging IoStore trio...");
        AppendLog($"Content root: {contentRootToPackage}");
        AppendLog($"Package base name: {packageBaseName}");

        // Generated material instances live in the Export content root, not the
        // patched/grafted stage. Copy the suit's own /Game/Mods/<mod> assets into
        // the content root so they get bundled into the pak (otherwise materials
        // resolve to null at runtime and render grey).
        _packageProgress?.Report("Staging materials and textures…");
        StageGeneratedMaterialsIntoContentRoot(_currentProject, contentRootToPackage);
        if (!StageGeneratedTexturesIntoContentRoot(_currentProject, contentRootToPackage, out var textureStageError))
        {
            AppendLog("IoStore package aborted: " + textureStageError);
            return;
        }

        // Every suit needs its own DCMD (points to the menu icon + equipment + the
        // generated pawn/cutscene classes). Generate it into the pack content root.
        _packageProgress?.Report("Generating DCMD / UIMD metadata…");
        StageGeneratedDcmdIntoContentRoot(_currentProject, contentRootToPackage);

        // Stage library-owned cooked animations (preserve-path/proven-clone/
        // imported) that this suit's overrides reference. external/base-game anims are NOT shipped
        // (they live in the modder's own pak or the base game).
        StageLibraryAnimsIntoContentRoot(_currentProject, contentRootToPackage);

        // Apply the custom-archetype pipeline (clone archetype + reparent playable/
        // cutscene + anim/equipment/visual graft) to the ACTUAL packaged root - the
        // grafted-parts stage diverges from the name-map stage, so this must run here
        // or archetype suits with grafted parts package without their animations.
        if (_currentProject.UseCustomArchetype)
        {
            _packageProgress?.Report("Applying custom archetype + animation pipeline…");
            var archAnim = new AnimArchetypeGraftService().ApplyToPackagedRoot(_currentProject, contentRootToPackage);
            AppendLog($"Custom archetype pipeline: {archAnim.Status}");
            foreach (var line in archAnim.Log) AppendLog("  " + line);
            if (!string.IsNullOrWhiteSpace(archAnim.Error)) AppendLog("  " + archAnim.Error.Split('\n')[0]);
        }

        // Re-apply saved material assignments to the FINAL packaged stage. A part/
        // glider graft can rebuild the stage from the base playable (dropping the
        // materials), so without this the pak ships with base-game materials instead
        // of the suit's (e.g. Batman face/body instead of the chosen ThomasWayne ones).
        ApplySavedMaterials(_currentProject, logIfNone: false);

        var runtimeJsonPath = StageRuntimeV2SuitJson(_currentProject, buildId);
        AppendLog($"Runtime V2 suit JSON: {runtimeJsonPath}");
        _packageProgress?.Report("Running preflight checks…");
        if (!RunV2PackagePreflight(_currentProject, contentRootToPackage, runtimeJsonPath, logHeader: true,
                out var preflightErrors, out var preflightWarnings))
        {
            AppendLog("V2 preflight failed; package aborted before retoc.");
            _packageProgress?.Report("Preflight failed — package aborted.");
            return;
        }
        _packageProgress?.Report($"Building IoStore trio ({packageBaseName})…");

        _packagePatchedIoStoreButton.Enabled = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-ProjectRoot");
            psi.ArgumentList.Add(projectRoot);
            psi.ArgumentList.Add("-SlotId");
            psi.ArgumentList.Add(slotId);
            psi.ArgumentList.Add("-PatchedContentRoot");
            psi.ArgumentList.Add(contentRootToPackage);
            psi.ArgumentList.Add("-PackageBaseName");
            psi.ArgumentList.Add(packageBaseName);
            psi.ArgumentList.Add("-RetocExe");
            psi.ArgumentList.Add(AppSettings.Current.EffectiveRetocExePath());

            using var process = Process.Start(psi);
            if (process is null)
            {
                AppendLog("Failed to start powershell.");
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                AppendLog(stdout.Trim());
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                AppendLog(stderr.Trim());
            }

            AppendLog($"IoStore packaging exit code: {process.ExitCode}");
            if (process.ExitCode == 0)
            {
                var outputRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "IoStore");
                AppendLog($"Copy the generated .pak/.ucas/.utoc from: {outputRoot}");
                WriteBuildManifest(_currentProject, buildId, contentRootToPackage, outputRoot,
                    packageBaseName, preflightErrors, preflightWarnings);
            }
        }
        catch (Exception ex)
        {
            AppendLog("IoStore packaging failed:");
            AppendLog(ex.ToString());
        }
        finally
        {
            _packagePatchedIoStoreButton.Enabled = true;
        }
    }

    private string CurrentPackageContentRoot(NativeSuitProject project)
    {
        var projectRoot = _projectRootText.Text.Trim();
        var slotId = project.SlotId;
        var defaultPatchedContentRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content");
        var genericGraftedContentRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "GraftedPartStage", "LEGOBatmanLotDK", "Content");
        var graftedContentRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "GraftedTorso2Stage", "LEGOBatmanLotDK", "Content");
        return Directory.Exists(genericGraftedContentRoot)
            ? genericGraftedContentRoot
            : Directory.Exists(graftedContentRoot)
                ? graftedContentRoot
                : defaultPatchedContentRoot;
    }

    /// <summary>
    /// Repoints this suit's base playable/cutscene/DCMD templates at the active extracted dump and
    /// shows a before/after report. Required after a game update: assets generated from a pre-update
    /// dump can parse fine yet crash in-game.
    /// </summary>
    /// <summary>
    /// Renames the suit's pak base name. Lives on Home now that the command bar only carries
    /// Package/Install - each suit needs its OWN pak name, or two suits overwrite each other.
    /// </summary>
    private void EditPackageBaseName()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        var current = CurrentPackageBaseName();
        var value = PromptForText("Package (pak) name",
            "Each suit needs its own pak name — two suits sharing one will overwrite each other in the game's ~mods folder.",
            current);
        if (value is null)
        {
            return;
        }

        var safe = MakeSafePackageBaseName(value.Trim());
        if (string.IsNullOrWhiteSpace(safe))
        {
            AppendLog("Pak name: empty/invalid — unchanged.");
            return;
        }

        _packageBaseNameText.Text = safe;
        _currentProject.PackageBaseName = safe;
        _lastAutoPackageBaseName = safe;
        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); }
        catch (Exception ex) { AppendLog($"Pak name: save failed: {ex.Message}"); }
        AppendLog($"Pak name set to '{safe}'. Re-package to build under the new name.");
        _session.RaiseChanged();
        RefreshToyboxTiles();
    }

    /// <summary>
    /// Shows exactly which assets the current stage will ship, with duplicate /Game object paths
    /// called out. Duplicates are the silent failure: IoStore mount priority picks a winner, so the
    /// game can load another suit's asset while the tool and FModel both look correct.
    /// </summary>
    private void ShowPackageContentsPreview()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var contentRoot = CurrentPackageContentRoot(_currentProject);
        StageGeneratedMaterialsIntoContentRoot(_currentProject, contentRoot);
        if (!StageGeneratedTexturesIntoContentRoot(_currentProject, contentRoot, out var textureStageError))
        {
            Dialog.Warn(this, "Package contents preview", textureStageError);
            return;
        }
        StageGeneratedDcmdIntoContentRoot(_currentProject, contentRoot);

        PackageContentPreviewService.Preview preview;
        try
        {
            preview = new PackageContentPreviewService(_projectRootText.Text.Trim())
                .Build(contentRoot, _currentProject.SlotId);
        }
        catch (Exception ex)
        {
            AppendLog($"Package preview failed: {ex.Message}");
            return;
        }

        using var dlg = new Form
        {
            Text = "Package contents preview",
            Width = 900,
            Height = 620,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(12, 10, 12, 0),
            ForeColor = preview.HasErrors ? Color.FromArgb(232, 96, 96) : Theme.OnDarkMuted,
            Text = preview.HasErrors
                ? $"⚠ {preview.Collisions.Count} duplicate package path(s) — packaging is blocked until resolved."
                : $"{preview.Assets.Count} asset(s) · {preview.TotalBytes / 1024} KB · no duplicate paths.",
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = Theme.CardBg,
            ForeColor = Theme.OnDark,
            BorderStyle = BorderStyle.None,
        };
        Theme.StyleListView(list);
        list.Columns.Add("Package path", 440);
        list.Columns.Add("Size", 70);
        list.Columns.Add("uexp", 46);
        list.Columns.Add("ubulk", 52);
        list.Columns.Add("Status", 250);

        var collisionsByPkg = preview.Collisions
            .Where(c => !string.IsNullOrWhiteSpace(c.PackagePath))
            .GroupBy(c => c.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(c => c.Detail)), StringComparer.OrdinalIgnoreCase);

        foreach (var a in preview.Assets)
        {
            var row = new ListViewItem(a.PackagePath);
            row.SubItems.Add($"{a.SizeBytes / 1024} KB");
            row.SubItems.Add(a.HasUexp ? "✓" : "—");
            row.SubItems.Add(a.HasUbulk ? "✓" : "—");
            if (collisionsByPkg.TryGetValue(a.PackagePath, out var detail))
            {
                row.SubItems.Add("DUPLICATE — " + detail);
                row.ForeColor = Color.FromArgb(232, 96, 96);
            }
            else
            {
                row.SubItems.Add("ok");
            }
            list.Items.Add(row);
        }

        // Collisions with no staged asset behind them (e.g. missing stage root) still need surfacing.
        foreach (var c in preview.Collisions.Where(c => string.IsNullOrWhiteSpace(c.PackagePath)))
        {
            var row = new ListViewItem("(stage)") { ForeColor = Color.FromArgb(232, 96, 96) };
            row.SubItems.Add("—");
            row.SubItems.Add("—");
            row.SubItems.Add("—");
            row.SubItems.Add(c.Detail);
            list.Items.Add(row);
        }

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var close = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.OK };
        Theme.StyleDarkButton(close);
        var copy = new Button { Text = "Copy list", Width = 100, Height = 30 };
        Theme.StyleDarkButton(copy);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, preview.Assets.Select(a => a.PackagePath)));
                AppendLog($"Copied {preview.Assets.Count} package path(s) to clipboard.");
            }
            catch (Exception ex) { AppendLog($"Copy failed: {ex.Message}"); }
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);

        dlg.Controls.Add(list);
        dlg.Controls.Add(buttons);
        dlg.Controls.Add(header);
        dlg.ShowDialog(this);
    }

    private void RunV2PreflightFromUi()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var contentRoot = CurrentPackageContentRoot(_currentProject);
        StageGeneratedMaterialsIntoContentRoot(_currentProject, contentRoot);
        if (!StageGeneratedTexturesIntoContentRoot(_currentProject, contentRoot, out var textureStageError))
        {
            AppendLog("V2 package preflight blocked: " + textureStageError);
            return;
        }
        StageGeneratedDcmdIntoContentRoot(_currentProject, contentRoot);
        var runtimeJson = StageRuntimeV2SuitJson(_currentProject);
        RunV2PackagePreflight(_currentProject, contentRoot, runtimeJson, logHeader: true);
    }

    private bool RunV2PackagePreflight(NativeSuitProject project, string contentRootToPackage, string runtimeJsonPath, bool logHeader) =>
        RunV2PackagePreflight(project, contentRootToPackage, runtimeJsonPath, logHeader, out _, out _);

    private bool RunV2PackagePreflight(NativeSuitProject project, string contentRootToPackage, string runtimeJsonPath, bool logHeader,
        out List<string> errorsOut, out List<string> warningsOut)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        errorsOut = errors;
        warningsOut = warnings;

        if (logHeader)
        {
            AppendLog("Running V2 package preflight...");
        }

        if (string.IsNullOrWhiteSpace(project.SlotId))
        {
            errors.Add("slot_id is empty.");
        }

        if (string.IsNullOrWhiteSpace(project.DisplayName))
        {
            warnings.Add("display name is empty; the DLL will still load the suit, but the menu text may be ugly.");
        }

        if (!Directory.Exists(contentRootToPackage))
        {
            errors.Add($"package content root does not exist: {contentRootToPackage}");
        }

        // Duplicate /Game object paths - within this staging tree, or against another suit's last
        // shipped manifest. Mount priority decides silently, so this blocks rather than warns.
        try
        {
            var preview = new PackageContentPreviewService(_projectRootText.Text.Trim())
                .Build(contentRootToPackage, project.SlotId);
            foreach (var c in preview.Collisions)
            {
                var message = string.IsNullOrWhiteSpace(c.PackagePath) ? c.Detail : $"{c.PackagePath}: {c.Detail}";
                if (c.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("duplicate package path — " + message);
                }
                else
                {
                    warnings.Add(message);
                }
            }
            if (preview.Collisions.Count == 0)
            {
                AppendLog($"  package contents: {preview.Assets.Count} asset(s), {preview.TotalBytes / 1024} KB, no duplicate paths.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"package-content preview could not run: {ex.Message}");
        }

        var dcmd = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Dcmd);
        var playable = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable);
        var cutscene = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Cutscene);
        var uimd = DeriveUimdPackagePath(dcmd);

        CheckV2PackagePath("DCMD", dcmd, mustBeModsPath: true, errors, warnings);
        CheckV2PackagePath("Playable", playable, mustBeModsPath: true, errors, warnings);
        CheckV2PackagePath("Cutscene", cutscene, mustBeModsPath: true, errors, warnings);
        CheckV2PackagePath("UIMD", uimd, mustBeModsPath: true, errors, warnings);

        CheckStagedPackageFiles("DCMD", contentRootToPackage, dcmd, requireUexp: true, errors, warnings);
        CheckStagedPackageFiles("Playable", contentRootToPackage, playable, requireUexp: true, errors, warnings);
        CheckStagedPackageFiles("Cutscene", contentRootToPackage, cutscene, requireUexp: true, errors, warnings);
        CheckStagedPackageFiles("UIMD", contentRootToPackage, uimd, requireUexp: true, errors, warnings);

        if (!File.Exists(runtimeJsonPath))
        {
            errors.Add($"runtime V2 suit.json was not staged: {runtimeJsonPath}");
        }
        else
        {
            var json = File.ReadAllText(runtimeJsonPath);
            if (!json.Contains("\"schema_version\": 2", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("runtime suit.json does not contain schema_version 2.");
            }
            if (!json.Contains("\"native\"", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("runtime suit.json does not contain native asset block.");
            }
            if (!json.Contains(dcmd, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"runtime suit.json does not point at the generated DCMD: {dcmd}");
            }
        }

        var packageBaseName = CurrentPackageBaseName();
        var outputRoot = Path.Combine(AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitGuiProjects", project.SlotId, "IoStore");
        foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
        {
            var existing = Path.Combine(outputRoot, packageBaseName + ext);
            if (File.Exists(existing))
            {
                warnings.Add($"local package output already exists and will be overwritten: {existing}");
            }

            var installed = Path.Combine(AppSettings.Current.EffectiveGamePaksModFolder(), packageBaseName + ext);
            if (File.Exists(installed))
            {
                warnings.Add($"installed package with this name already exists: {installed}");
            }
        }

        // Structural asset validation - the crash classes (orphan SCS nodes, class/mesh
        // mismatch, unparseable asset) + the silent glider-invisible failure. ERRORs block.
        try
        {
            var stageFindings = new StageValidationService(contentRootToPackage, AppSettings.Current.EffectiveUsmapPath())
                .Validate(project);
            foreach (var finding in stageFindings)
            {
                if (finding.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(finding.Message);
                }
                else
                {
                    warnings.Add(finding.Message);
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"stage structural validation could not run: {ex.Message}");
        }

        // Registration uniqueness: another installed suit reusing our pak base name (under a
        // different slot) would overwrite our pak, or be overwritten by it - the shared
        // ThomasWayneBP_P / three-Thomas-buttons incident. Block on a real collision.
        try
        {
            var suitsRoot = EffectiveGameRuntimeSuitsFolder();
            var ourPak = CurrentPackageBaseName();
            if (Directory.Exists(suitsRoot) && !string.IsNullOrWhiteSpace(ourPak))
            {
                foreach (var dir in Directory.EnumerateDirectories(suitsRoot))
                {
                    var otherJson = Path.Combine(dir, "suit.json");
                    if (!File.Exists(otherJson))
                    {
                        continue;
                    }
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(otherJson));
                        var root = doc.RootElement;
                        var otherSlot = root.TryGetProperty("slot_id", out var s) ? s.GetString() ?? "" : "";
                        var otherPak = root.TryGetProperty("package_base_name", out var p) ? p.GetString() ?? "" : "";
                        if (otherSlot.Equals(project.SlotId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // our own installed entry — expected on re-package
                        }
                        if (!string.IsNullOrWhiteSpace(otherPak) && otherPak.Equals(ourPak, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"another installed suit '{otherSlot}' ({Path.GetFileName(dir)}) uses the same pak name '{ourPak}' — packaging would overwrite it (or be overwritten). Give this suit a unique package_base_name.");
                        }
                    }
                    catch { /* skip unreadable suit.json */ }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"registration uniqueness check could not run: {ex.Message}");
        }

        // Package-PATH collision: every suit ships its assets under
        // /Game/Mods/<modFolder>/..., so two suits that share a mod folder emit the SAME
        // package paths - whichever pak the game loads last shadows the other, silently
        // breaking one suit even when their pak NAMES differ. Block on a shared mod folder
        // used by a different slot. (A suit re-packaging itself is fine - same slot.)
        try
        {
            var ourMod = ExtractModFolder(project.TargetPackages?.Playable);
            if (!string.IsNullOrWhiteSpace(ourMod))
            {
                var svc = _projectService ??= new SuitProjectService(_projectRootText.Text.Trim());
                foreach (var summary in svc.ListProjects())
                {
                    if (summary.SlotId.Equals(project.SlotId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // our own project
                    }
                    NativeSuitProject? other;
                    try { other = svc.LoadProject(summary.Path); }
                    catch { continue; }
                    var otherMod = ExtractModFolder(other?.TargetPackages?.Playable);
                    if (!string.IsNullOrWhiteSpace(otherMod) &&
                        otherMod.Equals(ourMod, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"suit '{summary.SlotId}' ({summary.DisplayName}) ships to the same mod folder '/Game/Mods/{ourMod}/' — both would emit identical package paths and overwrite each other in-game. Give one suit a different base character / mod folder before packaging.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"package-path collision check could not run: {ex.Message}");
        }

        foreach (var warning in warnings)
        {
            AppendLog("  ⚠ " + warning);
        }

        foreach (var error in errors)
        {
            AppendLog("  ✗ " + error);
        }

        AppendLog(errors.Count == 0
            ? $"V2 preflight passed ({warnings.Count} warning(s))."
            : $"V2 preflight failed ({errors.Count} error(s), {warnings.Count} warning(s)).");

        return errors.Count == 0;
    }

    private static void CheckV2PackagePath(string label, string packagePath, bool mustBeModsPath, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            errors.Add($"{label} package path is empty.");
            return;
        }

        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{label} package path must start with /Game/: {packagePath}");
        }

        if (packagePath.Contains(".", StringComparison.Ordinal))
        {
            warnings.Add($"{label} package path contains an object suffix; generated V2 JSON prefers package paths without .Asset suffix: {packagePath}");
        }

        if (mustBeModsPath && !packagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"{label} is not under /Game/Mods/. This may be intentional for donor assets, but generated V2 suit assets should usually live under /Game/Mods/<Mod>/: {packagePath}");
        }
    }

    private static void CheckStagedPackageFiles(string label, string contentRoot, string packagePath, bool requireUexp, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(contentRoot))
        {
            return;
        }

        var basePath = PackagePathToContentPath(contentRoot, packagePath);
        var uasset = basePath + ".uasset";
        var uexp = basePath + ".uexp";
        if (!File.Exists(uasset))
        {
            errors.Add($"{label} .uasset is missing from staged package content: {uasset}");
        }
        if (requireUexp && !File.Exists(uexp))
        {
            errors.Add($"{label} .uexp is missing from staged package content: {uexp}");
        }
        if (!File.Exists(basePath + ".ubulk"))
        {
            warnings.Add($"{label} has no .ubulk beside it. This is fine for many metadata/BP assets, but worth noting.");
        }
    }

    private static string ModFolderFromPackagePath(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return "";
        }

        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        const string prefix = "/Game/Mods/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var rest = normalized[prefix.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : rest;
    }

    private void StageGeneratedDcmdIntoContentRoot(NativeSuitProject project, string contentRootToPackage)
    {
        var dcmdPkg = project.TargetPackages?.Dcmd;
        var playablePkg = project.TargetPackages?.Playable;
        var cutscenePkg = project.TargetPackages?.Cutscene;
        if (string.IsNullOrWhiteSpace(dcmdPkg) || string.IsNullOrWhiteSpace(playablePkg) || string.IsNullOrWhiteSpace(cutscenePkg))
        {
            AppendLog("Skipping DCMD generation — target packages not set (use a base suit first).");
            return;
        }

        // Generate the suit's own UIMD (icon + description) first, then the DCMD
        // that points at it. Icons currently inherit the Batman defaults; retarget
        // to modder icon textures is a follow-up.
        if (AutoAssignGeneratedUiIconSlots(project))
        {
            try
            {
                (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(project);
            }
            catch (Exception ex)
            {
                AppendLog($"Auto icon slot save warning: {ex.Message}");
            }
        }

        var uimdPkg = DeriveUimdPackagePath(dcmdPkg!);
        var uimdOutputBase = PackagePathToContentPath(contentRootToPackage, uimdPkg);
        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        // IconSuit IS written into the UIMD: the custom suit needs its own suit icon wherever
        // the game reads UIMetaData (menu, HUD). The side effect -- the NATIVE donor button
        // rendering our icon while its DCMD is patched -- is corrected at runtime instead, by
        // re-asserting the donor's own icon onto the native button (see the DLL's native
        // source-button icon re-assert). Fixing it here was tried and reverted: it left the
        // suit without its own icon in every UIMetaData-driven surface.
        if (!string.IsNullOrWhiteSpace(project.IconMenu)) icons[UimdGenService.SrcMenuIcon] = project.IconMenu;
        if (!string.IsNullOrWhiteSpace(project.IconSuit)) icons[UimdGenService.SrcSuitIcon] = project.IconSuit;
        if (!string.IsNullOrWhiteSpace(project.IconLeft)) icons[UimdGenService.SrcLeftIcon] = project.IconLeft;
        if (!string.IsNullOrWhiteSpace(project.IconRight)) icons[UimdGenService.SrcRightIcon] = project.IconRight;
        var uimdResult = new UimdGenService(_projectRootText.Text.Trim()).Generate(uimdOutputBase, uimdPkg, icons.Count > 0 ? icons : null);
        AppendLog($"UIMD generate: {uimdResult.Status}{(icons.Count > 0 ? $" ({icons.Count} custom icon(s))" : " (default Batman icons)")}");
        if (!string.IsNullOrWhiteSpace(uimdResult.Error))
        {
            AppendLog("  " + uimdResult.Error);
        }
        else
        {
            AppendLog($"  wrote {Path.GetFileName(uimdResult.OutputUasset)} ({uimdResult.Repointed.Count} name(s) repointed)");
        }

        // Native-suit bridge builds keep the generated DCMD beside the generated
        // playable/cutscene under /Game/Mods/<Mod>/Characters. The runtime bridge
        // loads this DCMD directly and copies its payload onto the unlocked
        // TheBatman2025 donor, so we do not need to place generated DCMDs in the
        // base-game scanned Character folder anymore.
        var outputBase = PackagePathToContentPath(contentRootToPackage, dcmdPkg!);

        // Purge any stale scanned-location DCMD left by older registry experiments.
        // The builder now owns only the /Game/Mods/<Mod>/Characters copy.
        var staleScannedDcmdPkg = "/Game/Characters/Minifig/Batman/" + UnrealPathUtil.AssetName(dcmdPkg!);
        var staleBase = PackagePathToContentPath(contentRootToPackage, staleScannedDcmdPkg);
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            try { if (File.Exists(staleBase + ext)) { File.Delete(staleBase + ext); AppendLog($"  removed stale scanned DCMD {Path.GetFileName(staleBase + ext)} from old registry-test location"); } }
            catch { /* best effort */ }
        }

        var pawnTag = DerivePawnTag(project);
        var result = new DcmdGenService(_projectRootText.Text.Trim())
            .Generate(outputBase, dcmdPkg!, playablePkg!, cutscenePkg!, uimdPkg, pawnTag);

        AppendLog($"DCMD generate: {result.Status} (PawnTag -> {pawnTag}, shipped beside BP assets at {dcmdPkg})");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppendLog("  " + result.Error);
        }
        else
        {
            AppendLog($"  wrote {Path.GetFileName(result.OutputUasset)} ({result.Repointed.Count} name(s) repointed, UIMetaData -> {uimdPkg})");
            InjectStagedEquipment(project, result.OutputUasset);
        }
    }

    /// <summary>
    /// Injects the suit's staged extra gadgets (project.EquipmentAdds) into the
    /// freshly-written DCMD's EquipmentList. Resolves each gadget's DA_ETA package
    /// (and its upgrade set when present) from the shipped catalog.
    /// </summary>
    private void InjectStagedEquipment(NativeSuitProject project, string dcmdUasset)
    {
        if (project.EquipmentSlots.Count == 0)
        {
            return;
        }

        var gd = GameDataService.Instance;
        var refs = new List<DcmdGenService.EquipmentSlotRef>();
        foreach (var change in project.EquipmentSlots.OrderBy(s => s.Slot))
        {
            var eq = gd.FindEquipment(change.Gadget);
            if (eq is null || string.IsNullOrWhiteSpace(eq.EtaPackage))
            {
                AppendLog($"  equipment slot {change.Slot + 1} '{change.Gadget}': no catalog ETA — skipped");
                continue;
            }
            var upgrade = string.IsNullOrWhiteSpace(eq.UpgradePackage) ? null : eq.UpgradePackage;
            refs.Add(new DcmdGenService.EquipmentSlotRef(change.Slot, eq.Name, eq.EtaPackage, upgrade));
        }

        if (refs.Count == 0)
        {
            return;
        }

        var r = new DcmdGenService(_projectRootText.Text.Trim()).ReplaceEquipment(dcmdUasset, refs);
        AppendLog($"  equipment inject: {r.Status} applied=[{string.Join(", ", r.Applied)}]");
        if (!string.IsNullOrWhiteSpace(r.Error))
        {
            AppendLog("  " + r.Error);
        }
    }

    private string StageRuntimeV2SuitJson(NativeSuitProject project, string buildId = "")
    {
        var slotId = string.IsNullOrWhiteSpace(project.SlotId)
            ? _slotIdText.Text.Trim()
            : project.SlotId.Trim();

        if (string.IsNullOrWhiteSpace(slotId))
        {
            slotId = "native_suit";
        }

        var projectRoot = _projectRootText.Text.Trim();
        var runtimeJsonDir = Path.Combine(AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            slotId,
            "RuntimeJson",
            slotId);
        Directory.CreateDirectory(runtimeJsonDir);

        var dcmd = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Dcmd);
        var playable = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable);
        var cutscene = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Cutscene);
        var uimd = DeriveUimdPackagePath(dcmd);
        var icon = FirstNonEmpty(
            project.IconSuit,
            project.IconMenu,
            project.IconRight,
            project.IconLeft,
            UimdGenService.SrcSuitIcon);

        var json = new Dictionary<string, object?>
        {
            ["schema_version"] = 2,
            ["format"] = "native_v2",
            ["enabled"] = true,
            ["slot_id"] = slotId,
            ["display_name"] = string.IsNullOrWhiteSpace(project.DisplayName) ? slotId : project.DisplayName,
            ["description"] = project.Description ?? "",
            ["menu_order"] = 1000,
            ["icon_asset"] = icon,
            ["package_base_name"] = CurrentPackageBaseName(),
            ["build_id"] = buildId,
            ["native"] = new Dictionary<string, object?>
            {
                ["dcmd"] = dcmd,
                ["playable"] = playable,
                ["cutscene"] = cutscene,
                ["uimd"] = uimd,
                ["icon"] = icon
            }
        };

        var equipmentRules = BuildEquipmentReplacementRules(project);
        if (equipmentRules.Count > 0)
        {
            json["equipment_replacements"] = equipmentRules;
        }

        var path = Path.Combine(runtimeJsonDir, "suit.json");
        File.WriteAllText(path, JsonSerializer.Serialize(json, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        return path;
    }

    /// <summary>
    /// Writes the build manifest beside the freshly-packaged IoStore trio. Best-effort:
    /// a manifest failure never fails an otherwise-successful package.
    /// </summary>
    private void WriteBuildManifest(NativeSuitProject project, string buildId, string contentRootPacked,
        string ioStoreDir, string packageBaseName, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        try
        {
            var dcmd = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Dcmd);
            var declared = new Dictionary<string, string>
            {
                ["dcmd"] = dcmd,
                ["playable"] = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable),
                ["cutscene"] = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Cutscene),
                ["uimd"] = DeriveUimdPackagePath(dcmd)
            };
            var mod = ExtractModFolder(project.TargetPackages?.Playable) ?? "";
            var (manifest, manifestPath) = new BuildManifestService().Write(
                buildId, contentRootPacked, mod, ioStoreDir, packageBaseName,
                project.SlotId,
                string.IsNullOrWhiteSpace(project.DisplayName) ? project.SlotId : project.DisplayName,
                declared, errors, warnings);
            AppendLog($"Build manifest: {manifestPath}");
            AppendLog($"  build_id {manifest.BuildId} · {manifest.ShippedPackages.Count} shipped package(s) · {manifest.TrioFiles.Count} trio file(s) · {warnings.Count} warning(s)");
            ShowPackageSuccessDialog(manifest, ioStoreDir);
        }
        catch (Exception ex)
        {
            AppendLog($"  ⚠ could not write build manifest: {ex.Message}");
        }
    }

    /// <summary>
    /// A clear post-package success view - exactly which trio files were
    /// generated (name + size), the build id, shipped-package + warning counts, and the recommended
    /// in-game test order, with one-click Install / Open-output-folder.
    /// </summary>
    private void ShowPackageSuccessDialog(BuildManifestService.Manifest manifest, string ioStoreDir)
    {
        if (_batchMode)
        {
            return; // bulk update reports one summary at the end instead of a dialog per suit
        }

        // The progress window currently owns input (main form disabled) - close it before the
        // summary so this dialog isn't stacked behind it on a disabled owner.
        _packageProgress?.Dispose();
        _packageProgress = null;

        static string Kb(long bytes) => bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";

        using var dlg = new Form
        {
            Text = "Package built",
            Width = 620,
            Height = 560,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            MinimizeBox = false,
            MaximizeBox = false,
        };

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(14, 12, 14, 0),
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            ForeColor = Theme.Gold,
            Text = $"✓ {manifest.DisplayName} packaged",
        };

        var trioText = manifest.TrioFiles.Count == 0
            ? "  (no trio files found)"
            : string.Join("\n", manifest.TrioFiles.Select(t => $"   • {t.File}   ({Kb(t.Size)})"));

        var body = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 4, 16, 8),
            ForeColor = Theme.OnDark,
            Text =
                $"Build ID:  {manifest.BuildId}\n" +
                $"Game build:  {manifest.GameBuild}\n" +
                $"Shipped packages:  {manifest.ShippedPackages.Count}\n" +
                $"Warnings:  {manifest.Validation.GetValueOrDefault("warnings")?.Count ?? 0}   ·   Errors:  {manifest.Validation.GetValueOrDefault("errors")?.Count ?? 0}\n\n" +
                "Output trio:\n" + trioText + "\n\n" +
                $"Location:\n   {ioStoreDir}\n\n" +
                "Recommended in-game test:\n" +
                "   1. Install (button below), then launch the game.\n" +
                "   2. Open the suit menu — your suit should appear; select it.\n" +
                "   3. Change level, then return to the menu.\n" +
                "   4. Save and reload to confirm it persists.",
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var close = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.OK };
        Theme.StyleDarkButton(close);
        var openFolder = new Button { Text = "Open output folder", Width = 150, Height = 30 };
        Theme.StyleDarkButton(openFolder);
        openFolder.Click += (_, _) =>
        {
            try { if (Directory.Exists(ioStoreDir)) System.Diagnostics.Process.Start("explorer.exe", $"\"{ioStoreDir}\""); }
            catch (Exception ex) { AppendLog($"Could not open folder: {ex.Message}"); }
        };
        var install = new Button { Text = "Install now", Width = 110, Height = 30 };
        Theme.StyleGoldButton(install);
        install.Click += (_, _) => { dlg.Close(); InstallTrio(); };
        buttons.Controls.Add(close);
        buttons.Controls.Add(install);
        buttons.Controls.Add(openFolder);

        dlg.Controls.Add(body);
        dlg.Controls.Add(buttons);
        dlg.Controls.Add(title);
        dlg.ShowDialog(this);
    }

    /// <summary>
    /// Builds the runtime equipment_replacements rules the DLL consumes for its
    /// live EquipmentContainer swap (the actual mechanism that changes a suit's
    /// gadgets in-game - the DCMD EquipmentList alone does not do this because the
    /// suit runs on the donor pawn tag). Each rule targets an explicit 0-based
    /// slot and carries object paths for the new gadget/upgrade (and the old ones
    /// it replaces, for matching).
    /// </summary>
    private List<Dictionary<string, object?>> BuildEquipmentReplacementRules(NativeSuitProject project)
    {
        var rules = new List<Dictionary<string, object?>>();
        if (project.EquipmentSlots.Count == 0)
        {
            return rules;
        }

        var gd = GameDataService.Instance;
        var baseSlots = CurrentEquipmentSlotNames(); // e.g. ["Batarang","Batclaw"]

        foreach (var change in project.EquipmentSlots.OrderBy(s => s.Slot))
        {
            var newEq = gd.FindEquipment(change.Gadget);
            if (newEq is null || string.IsNullOrWhiteSpace(newEq.EtaPackage))
            {
                AppendLog($"  equipment rule slot {change.Slot + 1} '{change.Gadget}': no catalog ETA — skipped");
                continue;
            }

            var rule = new Dictionary<string, object?>
            {
                ["slot"] = change.Slot,
                ["with_equipment"] = ToObjectPath(newEq.EtaPackage),
            };
            if (!string.IsNullOrWhiteSpace(newEq.UpgradePackage))
            {
                rule["with_upgrade"] = ToObjectPath(newEq.UpgradePackage);
            }

            // Old occupant of this slot (for replace_equipment / replace_upgrade).
            if (change.Slot >= 0 && change.Slot < baseSlots.Count)
            {
                var oldEq = gd.FindEquipment(baseSlots[change.Slot]);
                if (oldEq is not null && !string.IsNullOrWhiteSpace(oldEq.EtaPackage))
                {
                    rule["replace_equipment"] = ToObjectPath(oldEq.EtaPackage);
                    if (!string.IsNullOrWhiteSpace(oldEq.UpgradePackage))
                    {
                        rule["replace_upgrade"] = ToObjectPath(oldEq.UpgradePackage);
                    }
                }
            }

            rules.Add(rule);
        }

        return rules;
    }

    private string CurrentPackageBaseName()
    {
        var value = MakeSafePackageBaseName(_packageBaseNameText.Text.Trim());
        if (string.IsNullOrWhiteSpace(value))
        {
            value = MakeSafePackageBaseName($"{_slotIdText.Text.Trim()}_P");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = "NativeSuit_P";
        }

        _packageBaseNameText.Text = value;
        return value;
    }

    private static string MakeSafePackageBaseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        value = Path.GetFileNameWithoutExtension(value.Trim());
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray();
        var safe = new string(chars).Trim('_');
        while (safe.Contains("__", StringComparison.Ordinal))
        {
            safe = safe.Replace("__", "_", StringComparison.Ordinal);
        }

        return safe;
    }

    private static string PackagePathToContentPath(string contentRoot, string packagePath)
    {
        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        var rel = packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
            ? packagePath["/Game/".Length..]
            : packagePath.TrimStart('/');
        return Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    }
}
