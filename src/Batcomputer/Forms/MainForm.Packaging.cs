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
    private sealed record PackageBuildResult(
        bool Success,
        string BuildId,
        string SlotId,
        string PackageBaseName,
        string IoStoreDirectory,
        BuildManifestService.Manifest? Manifest,
        string FailureDetail)
    {
        public static PackageBuildResult Failed(string detail) =>
            new(false, "", "", "", "", null, detail);

        public static PackageBuildResult Completed(
            BuildManifestService.Manifest manifest,
            string ioStoreDirectory) =>
            new(
                true,
                manifest.BuildId,
                manifest.SlotId,
                manifest.PackageBaseName,
                ioStoreDirectory,
                manifest,
                "");
    }

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
        // The host used to retain a 2/4px padding while collapsed, leaving less
        // vertical room than the header itself and clipping both captions.
        _mainLogGroupBox.Text = "";
        _mainLogGroupBox.Padding = Padding.Empty;
        var diagnosticsHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            // Leave a real 32px control row after padding. The previous 34px
            // outer height could clip the bundled condensed font at 125% DPI.
            Height = 40,
            Padding = new Padding(6, 4, 6, 4),
            BackColor = Theme.CardBg,
            ColumnCount = 2,
            RowCount = 1,
        };
        diagnosticsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        diagnosticsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104f));
        diagnosticsHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _diagnosticsHeaderButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = _diagnosticsCollapsed ? "▸  Diagnostics" : "▾  Diagnostics",
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Margin = Padding.Empty,
        };
        Theme.StyleSmallDarkButton(_diagnosticsHeaderButton);
        _diagnosticsHeaderButton.Font = Theme.BodyStrong;
        _diagnosticsHeaderButton.Click += (_, _) => ToggleDiagnostics();
        var copyLog = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Copy log",
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
        };
        Theme.StyleSmallDarkButton(copyLog);
        copyLog.Font = Theme.BodyStrong;
        copyLog.Click += (_, _) =>
        {
            copyLog.Text = _diagnostics.TryCopyLogToClipboard() ? "Copied" : "Copy failed";
        };
        diagnosticsHeader.Controls.Add(_diagnosticsHeaderButton, 0, 0);
        diagnosticsHeader.Controls.Add(copyLog, 1, 0);
        _diagnostics.Dock = DockStyle.Fill;
        _mainLogGroupBox.Controls.Add(_diagnostics);
        _mainLogGroupBox.Controls.Add(diagnosticsHeader);
        diagnosticsHeader.BringToFront();
        ApplyDiagnosticsLayout();
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
        menu.Items.Add("Refresh part index", null, async (_, _) => await BuildPartIndexAsync());
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
            Font = AppFonts.Condensed(7.5f, FontStyle.Bold),
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
        var name = new Label { Text = label, Left = 24, Top = 4, Width = 174, Height = 16, AutoSize = false, BackColor = Color.Transparent, ForeColor = Theme.OnDark, Font = AppFonts.Condensed(9f, FontStyle.Bold) };
        // AutoEllipsis + generous height keeps a long, unbreakable material token on a
        // single line (truncated with "…") instead of wrapping off the visible row.
        var sub = new Label { Text = subtitle, Left = 24, Top = 21, Width = 178, Height = 16, AutoSize = false, AutoEllipsis = true, BackColor = Color.Transparent, ForeColor = Theme.OnDarkMuted, Font = AppFonts.Condensed(7.5f, FontStyle.Bold) };
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
        menu.Items.Add("Set cover image…", null, (_, _) => SetSuitCoverImage(summary));
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
            Text = "After install: fully restart the game, then open the owning character's suit menu. " +
                   "If the suit is missing, confirm LOTDK Expanded is enabled and use Verify last UE4SS log for a focused diagnosis."
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

            AppendLog(count == 0
                ? $"No .pak/.ucas/.utoc found in {ioStoreDir}."
                : $"Installed {count} file(s) to {dest}. Use Build Mod to install the mod manifest and registry plugin.");
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
        menu.Items.Add("Repair texture cook templates", null, async (_, _) =>
        {
            var projectRoot = _projectRootText.Text.Trim();
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            {
                AppendLog("Texture-template repair cannot start: project root is missing. Open Setup first.");
                return;
            }

            await EnsureTextureCookTemplatesAsync(projectRoot);
        });
        menu.Items.Add("Open active extracted Content", null, (_, _) =>
        {
            var contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            if (!Directory.Exists(contentRoot))
            {
                Dialog.Warn(this, "Extracted Content not found",
                    $"Batcomputer's active extracted Content folder does not exist:\n\n{contentRoot}\n\n" +
                    "Open Setup to correct the path, or run Refresh all character assets.");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(contentRoot) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Dialog.Error(this, "Could not open extracted Content", ex.Message);
            }
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

    private async Task<PackageBuildResult> PackagePatchedIoStoreAsync()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return PackageBuildResult.Failed("No suit is open.");
        }

        // In batch mode the bulk operation owns the progress window (one per suit would flicker).
        using var progress = _batchMode ? null : new ProgressDialog(this, "Packaging suit");
        _packageProgress = progress ?? _packageProgress;
        try
        {
            return await PackagePatchedIoStoreCoreAsync();
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

    private async Task<PackageBuildResult> PackagePatchedIoStoreCoreAsync()
    {
        if (_currentProject is null)
        {
            return PackageBuildResult.Failed("No suit is open.");
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

        if (!File.Exists(script))
        {
            AppendLog($"IoStore packaging script not found: {script}");
            return PackageBuildResult.Failed("The IoStore packaging script was not found.");
        }

        var authoringContentRoot = CurrentPackageContentRoot(_currentProject);
        if (IsIncompleteDeclarativeGraftStage(_currentProject, authoringContentRoot))
        {
            const string message =
                "The current grafted-part stage did not finish rebuilding, so packaging was stopped before a partial suit could be emitted. " +
                "Close any asset viewer holding this suit's generated files, then edit/reapply a part or base to rebuild the stage.";
            AppendLog("IoStore package aborted: " + message);
            Dialog.Error(this, "Incomplete generated stage", message);
            return PackageBuildResult.Failed(message);
        }

        NativeSuitProject packageProject;
        PackagePreparationStage preparationStage;
        try
        {
            packageProject = CloneProjectForPackagePreparation(_currentProject);
            preparationStage = await CreatePackagePreparationStageAsync(packageProject, projectRoot);
        }
        catch (Exception ex)
        {
            var message =
                "Batcomputer could not create an isolated package-preparation copy, so the certified authoring stage was left untouched. " +
                ex.Message;
            AppendLog("IoStore package aborted: " + message);
            Dialog.Error(this, "Package preparation failed", message);
            return PackageBuildResult.Failed(message);
        }

        var contentRootToPackage = preparationStage.ContentRoot;
        try
        {
            AppendLog($"Package preparation copy: {contentRootToPackage}");
            var packageGliderComponent = ActiveGliderVisualComponent(packageProject);
            if (!string.IsNullOrWhiteSpace(packageGliderComponent))
            {
                var gliderRestore = RestoreProtectedGliderComponent(
                    packageProject,
                    packageGliderComponent,
                    projectRoot,
                    contentRootToPackage);
                if (!gliderRestore.Success)
                {
                    var message =
                        $"The active glider component '{packageGliderComponent}' could not be restored in every required character package, so packaging was stopped. " +
                        gliderRestore.Summary;
                    AppendLog("IoStore package aborted: " + message);
                    Dialog.Error(this, "Incomplete glider restoration", message);
                    return PackageBuildResult.Failed(message);
                }

                // Package preparation owns a snapshot of the authoring declaration. Suppress a
                // stale glider removal only in that snapshot; do not rewrite the saved project or
                // falsely certify its authoring stage during a release build.
                if (RemoveSavedRemovalForComponent(packageProject, packageGliderComponent))
                {
                    AppendLog($"Package: ignored stale remove-component rule for active glider component '{packageGliderComponent}' in the disposable preparation copy.");
                }
            }

            var removalReplay = ApplySavedComponentRemovals(
                packageProject,
                logNoRemovals: false,
                stageContentRootOverride: contentRootToPackage);
            if (!removalReplay.Success)
            {
                var message =
                    "Saved component removals did not apply to every required character package, so packaging was stopped. " +
                    removalReplay.Summary;
                AppendLog("IoStore package aborted: " + message);
                Dialog.Error(this, "Incomplete component replay", message);
                return PackageBuildResult.Failed(message);
            }

        AppendLog("Packaging IoStore trio…");
        AppendLog($"Content root: {contentRootToPackage}");
        AppendLog($"Package base name: {packageBaseName}");

        // Generated material instances live in the Export content root, not the
        // patched/grafted stage. Copy the suit's own /Game/Mods/<mod> assets into
        // the content root so they get bundled into the pak (otherwise materials
        // resolve to null at runtime and render grey).
        _packageProgress?.Report("Staging materials and textures…");
        StageGeneratedMaterialsIntoContentRoot(packageProject, contentRootToPackage);
        if (!StageGeneratedTexturesIntoContentRoot(
                packageProject,
                contentRootToPackage,
                out var textureStageError,
                persistProjectChanges: false))
        {
            AppendLog("IoStore package aborted: " + textureStageError);
            return PackageBuildResult.Failed(textureStageError);
        }

        // Every suit needs its own DCMD (points to the menu icon + equipment + the
        // generated pawn/cutscene classes). Generate it into the pack content root.
        _packageProgress?.Report("Generating DCMD / UIMD metadata…");
        StageGeneratedDcmdIntoContentRoot(
            packageProject,
            contentRootToPackage,
            persistAutoAssignedIcons: false,
            requireSuccess: true);

        // Stage library-owned cooked animations (preserve-path/proven-clone/
        // imported) that this suit's overrides reference. external/base-game anims are NOT shipped
        // (they live in the modder's own pak or the base game).
        StageLibraryAnimsIntoContentRoot(packageProject, contentRootToPackage);

        // Apply the custom-archetype pipeline (clone archetype + reparent playable/
        // cutscene + anim/equipment/visual graft) to the ACTUAL packaged root - the
        // grafted-parts stage diverges from the name-map stage, so this must run here
        // or archetype suits with grafted parts package without their animations.
        if (packageProject.UseCustomArchetype)
        {
            _packageProgress?.Report("Applying custom archetype + animation pipeline…");
            var archAnim = new AnimArchetypeGraftService().ApplyToPackagedRoot(packageProject, contentRootToPackage);
            AppendLog($"Custom archetype pipeline: {archAnim.Status}");
            foreach (var line in archAnim.Log) AppendLog("  " + line);
            if (!string.IsNullOrWhiteSpace(archAnim.Error)) AppendLog("  " + archAnim.Error.Split('\n')[0]);
            if (string.Equals(archAnim.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(archAnim.Error))
            {
                var message = archAnim.Error ?? "Custom archetype preparation failed.";
                AppendLog("IoStore package aborted: " + message);
                Dialog.Error(this, "Custom archetype failed", message);
                return PackageBuildResult.Failed(message);
            }
        }

        // Re-apply saved material assignments to the FINAL packaged stage. A part/
        // glider graft can rebuild the stage from the base playable (dropping the
        // materials), so without this the pak ships with base-game materials instead
        // of the suit's (e.g. Batman face/body instead of the chosen ThomasWayne ones).
        var materialReplay = ApplySavedMaterials(
            packageProject,
            logIfNone: false,
            stageContentRootOverride: contentRootToPackage);
        if (!materialReplay.Success)
        {
            var message =
                "Saved materials did not apply to every required character package, so packaging was stopped. " +
                materialReplay.Summary;
            AppendLog("IoStore package aborted: " + message);
            Dialog.Error(this, "Incomplete material replay", message);
            return PackageBuildResult.Failed(message);
        }

        var runtimeJsonPath = StageRuntimeV2SuitJson(packageProject, buildId);
        AppendLog($"Runtime V2 suit JSON: {runtimeJsonPath}");
        _packageProgress?.Report("Checking the package…");
        if (!RunV2PackagePreflight(packageProject, contentRootToPackage, runtimeJsonPath, logHeader: true,
                out var preflightErrors, out var preflightWarnings))
        {
            AppendLog("Package check failed; nothing was sent to retoc.");
            _packageProgress?.Report("Package check failed.");
            return PackageBuildResult.Failed("Package preflight failed.");
        }
        _packageProgress?.Report($"Building IoStore trio ({packageBaseName})…");

        var outputRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            slotId,
            "IoStore");
        var expectedTrioPaths = new[] { ".pak", ".ucas", ".utoc" }
            .Select(extension => Path.Combine(outputRoot, packageBaseName + extension))
            .ToList();
        try
        {
            Directory.CreateDirectory(outputRoot);
            // Remove only this package's previous certified outputs. A successful retoc exit can
            // now be trusted only when all three files reappear during this attempt.
            foreach (var previousOutput in expectedTrioPaths)
            {
                File.Delete(previousOutput);
            }
            File.Delete(Path.Combine(outputRoot, "build-manifest.json"));
        }
        catch (Exception ex)
        {
            var message = "The previous package output could not be cleared, so Batcomputer cannot prove that the next trio is fresh. " + ex.Message;
            AppendLog("IoStore package aborted: " + message);
            return PackageBuildResult.Failed(message);
        }

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
                return PackageBuildResult.Failed("PowerShell could not be started for IoStore packaging.");
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
            if (process.ExitCode != 0)
            {
                return PackageBuildResult.Failed($"IoStore packaging exited with code {process.ExitCode}.");
            }

            var missingOrEmpty = BuildManifestService.FindMissingOrEmptyFiles(expectedTrioPaths)
                .Select(Path.GetFileName)
                .ToList();
            if (missingOrEmpty.Count > 0)
            {
                var message =
                    "IoStore reported success but did not freshly produce a complete trio: " +
                    string.Join(", ", missingOrEmpty);
                AppendLog("IoStore package failed: " + message);
                return PackageBuildResult.Failed(message);
            }

            AppendLog($"Copy the generated .pak/.ucas/.utoc from: {outputRoot}");
            var manifest = WriteBuildManifest(packageProject, buildId, contentRootToPackage, outputRoot,
                packageBaseName, preflightErrors, preflightWarnings);
            if (manifest is null)
            {
                return PackageBuildResult.Failed(
                    "The trio was built, but its build-ID manifest could not be written; automatic install was refused.");
            }

            var completed = PackageBuildResult.Completed(manifest, outputRoot);
            if (!new BuildManifestService().VerifyInstallableTrio(
                    manifest,
                    completed.BuildId,
                    completed.SlotId,
                    completed.PackageBaseName,
                    completed.IoStoreDirectory,
                    out var certificationError))
            {
                AppendLog("IoStore package failed certification: " + certificationError);
                return PackageBuildResult.Failed(certificationError);
            }

            return completed;
        }
        catch (Exception ex)
        {
            AppendLog("IoStore packaging failed:");
            AppendLog(ex.ToString());
            return PackageBuildResult.Failed(ex.Message);
        }
        finally
        {
            _packagePatchedIoStoreButton.Enabled = true;
        }
        }
        finally
        {
            await CleanupPackagePreparationStageAsync(preparationStage);
        }
    }

    /// <summary>
    /// Installs only the exact trio certified by the successful package attempt returned to the
    /// caller. Unlike the legacy manual install command, this cannot discover and copy an older
    /// trio merely because it is still present in the slot's output directory.
    /// </summary>
    private bool InstallFreshlyBuiltTrio(PackageBuildResult build) =>
        InstallFreshlyBuiltTrio(build, out _);

    private bool InstallFreshlyBuiltTrio(
        PackageBuildResult build,
        out bool destinationConsistent)
    {
        destinationConsistent = true;
        if (!build.Success || build.Manifest is null)
        {
            AppendLog("Fresh package install refused: no successful certified build was provided.");
            return false;
        }

        var manifestService = new BuildManifestService();
        if (!manifestService.VerifyInstallableTrio(
                build.Manifest,
                build.BuildId,
                build.SlotId,
                build.PackageBaseName,
                build.IoStoreDirectory,
                out var verificationError))
        {
            AppendLog("Fresh package install refused: " + verificationError);
            return false;
        }

        var destination = AppSettings.Current.EffectiveGamePaksModFolder();
        var installFiles = build.Manifest.TrioFiles
            .Select(entry => new TrioInstallTransactionService.FileSpec(
                Path.Combine(build.IoStoreDirectory, entry.File),
                entry.File,
                entry.Sha256,
                entry.Size))
            .ToList();
        var installResult = new TrioInstallTransactionService().Install(installFiles, destination);
        destinationConsistent = installResult.DestinationConsistent;
        foreach (var warning in installResult.Warnings)
        {
            AppendLog("Fresh package install warning: " + warning);
        }
        if (!installResult.Success)
        {
            AppendLog("Fresh package install failed: " + installResult.Detail);
            return false;
        }

        AppendLog(
            $"Installed freshly built trio {build.PackageBaseName} (build {build.BuildId}) to {destination}.");
        return true;
    }

    private string CurrentPackageContentRoot(NativeSuitProject project)
    {
        var projectRoot = _projectRootText.Text.Trim();
        var slotId = project.SlotId;
        var defaultPatchedContentRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content");
        var genericGraftedContentRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "GraftedPartStage", "LEGOBatmanLotDK", "Content");
        var graftedContentRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "GraftedTorso2Stage", "LEGOBatmanLotDK", "Content");
        // A declarative project must never fall back to an older base-only stage. If its
        // graft stage was removed by a failed rebuild, return the expected (missing) root so
        // every caller fails closed instead of packaging PatchedNameMapStage/GraftedTorso2Stage.
        return ProjectRequiresCompletedGraftStage(project) || Directory.Exists(genericGraftedContentRoot)
            ? genericGraftedContentRoot
            : Directory.Exists(graftedContentRoot)
                ? graftedContentRoot
                : defaultPatchedContentRoot;
    }

    private bool IsIncompleteDeclarativeGraftStage(NativeSuitProject project, string contentRoot)
    {
        var projectStageRoot = Path.Combine(
            AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()),
            "NativeSuitGuiProjects",
            project.SlotId);
        if (File.Exists(Path.Combine(projectStageRoot, IncompleteDeclarativeStageMarkerName)))
        {
            return true;
        }

        // A failed declarative rebuild can remove GraftedPartStage before aborting. In that
        // state CurrentPackageContentRoot falls back to PatchedNameMapStage/GraftedTorso2Stage,
        // so inspecting only the selected root would let a suit silently package without its
        // saved grafts or custom meshes. Derive the required stage from project state instead.
        if (ProjectRequiresCompletedGraftStage(project))
        {
            var expectedContentRoot = DeclarativeGraftContentRoot(project);
            var expectedStageRoot = Directory.GetParent(expectedContentRoot)?.Parent;
            return !Directory.Exists(expectedContentRoot) ||
                   expectedStageRoot is null ||
                   !File.Exists(Path.Combine(expectedStageRoot.FullName, CompletedGraftStageMarkerName));
        }

        var legoBatmanRoot = Directory.GetParent(contentRoot);
        var stageRoot = legoBatmanRoot?.Parent;
        return stageRoot is not null &&
               stageRoot.Name.Equals("GraftedPartStage", StringComparison.OrdinalIgnoreCase) &&
               (!Directory.Exists(contentRoot) ||
                !File.Exists(Path.Combine(stageRoot.FullName, CompletedGraftStageMarkerName)));
    }

    internal static bool ProjectRequiresCompletedGraftStage(NativeSuitProject project) =>
        project.PartGrafts is { Count: > 0 } ||
        project.CustomStaticMeshes is { Count: > 0 } ||
        project.MaterialAssignments is { Count: > 0 } ||
        project.UseCustomArchetype ||
        project.EquipmentSlots is { Count: > 0 } ||
        project.GliderGrafted ||
        !string.IsNullOrWhiteSpace(project.GliderType) ||
        project.Requirements.Any(requirement =>
            requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase));

    private string DeclarativeGraftContentRoot(NativeSuitProject project)
    {
        var projectRoot = _projectRootText.Text.Trim();
        return Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            project.SlotId,
            "GraftedPartStage",
            "LEGOBatmanLotDK",
            "Content");
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
    private async void ShowPackageContentsPreview()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var projectRoot = _projectRootText.Text.Trim();
        var preparation = await PrepareSuitForReleaseAsync(
            _currentProject,
            new SuitProjectService(projectRoot));
        if (preparation.Prepared is null)
        {
            AppendLog("Package preview failed: " + preparation.Error);
            Dialog.Warn(this, "Package contents preview", preparation.Error);
            return;
        }

        var prepared = preparation.Prepared;
        PackageContentPreviewService.Preview? preview = null;
        try
        {
            preview = new PackageContentPreviewService(projectRoot)
                .Build(prepared.Stage.ContentRoot, prepared.Project.SlotId);
        }
        catch (Exception ex)
        {
            AppendLog($"Package preview failed: {ex.Message}");
        }
        finally
        {
            await CleanupPackagePreparationStageAsync(prepared.Stage);
        }
        if (preview is null)
        {
            return;
        }

        using var dlg = new AdaptiveDialogForm
        {
            Text = "Package contents preview",
            Width = 900,
            Height = 620,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(720, 500),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);

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

    private async void RunV2PreflightFromUi()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var projectRoot = _projectRootText.Text.Trim();
        var preparation = await PrepareSuitForReleaseAsync(
            _currentProject,
            new SuitProjectService(projectRoot));
        if (preparation.Prepared is null)
        {
            AppendLog("Package check failed: " + preparation.Error);
            return;
        }

        var prepared = preparation.Prepared;
        try
        {
            var runtimeJson = StageRuntimeV2SuitJson(prepared.Project);
            RunV2PackagePreflight(
                prepared.Project,
                prepared.Stage.ContentRoot,
                runtimeJson,
                logHeader: true);
        }
        catch (Exception ex)
        {
            AppendLog("Package check failed: " + ex.Message);
        }
        finally
        {
            await CleanupPackagePreparationStageAsync(prepared.Stage);
        }
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
            AppendLog("Checking the package…");
        }
        if (IsIncompleteDeclarativeGraftStage(project, contentRootToPackage))
        {
            errors.Add("The grafted-part stage is incomplete; rebuild the declarative part stage before packaging.");
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
            // This validator is the final proof against cooked-schema/component crashes and
            // missing runtime animation dependencies. Treat an unexpected parse/mappings failure
            // as a blocker: warning-and-continue would let an unverified paired-cape (or any other
            // structurally unreadable suit) reach IoStore packaging.
            errors.Add($"stage structural validation could not run, so packaging is blocked: {ex.Message}");
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
            ? $"Package check passed ({warnings.Count} warning(s))."
            : $"Package check failed ({errors.Count} error(s), {warnings.Count} warning(s)).");

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

    private void StageGeneratedDcmdIntoContentRoot(
        NativeSuitProject project,
        string contentRootToPackage,
        bool persistAutoAssignedIcons = true,
        bool requireSuccess = false)
    {
        var dcmdPkg = project.TargetPackages?.Dcmd;
        var playablePkg = project.TargetPackages?.Playable;
        var cutscenePkg = project.TargetPackages?.Cutscene;
        if (string.IsNullOrWhiteSpace(dcmdPkg) || string.IsNullOrWhiteSpace(playablePkg) || string.IsNullOrWhiteSpace(cutscenePkg))
        {
            const string message = "DCMD generation cannot run because the target packages are not set (use a base suit first).";
            AppendLog(message);
            if (requireSuccess)
            {
                throw new InvalidOperationException(message);
            }
            return;
        }

        // Generate the suit's own UIMD (icon + description) first, then the DCMD
        // that points at it. Icons currently inherit the Batman defaults; retarget
        // to modder icon textures is a follow-up.
        if (AutoAssignGeneratedUiIconSlots(project) && persistAutoAssignedIcons)
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
        var metadataDonor = NativeMetadataDonorService.TryRead(
            project.DcmdTemplate,
            project.PlayableTemplate,
            project.CutsceneTemplate);
        if (metadataDonor is null)
        {
            AppendLog("Native metadata donor could not be read; generating the required DCMD/UIMD from the base Batman metadata.");
        }

        var pawnTag = DerivePawnTag(project);
        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIconOverride(metadataDonor?.IconPaths.Menu ?? UimdGenService.SrcMenuIcon, project.IconMenu);
        AddIconOverride(metadataDonor?.IconPaths.Suit ?? UimdGenService.SrcSuitIcon, project.IconSuit);
        AddIconOverride(metadataDonor?.IconPaths.Left ?? UimdGenService.SrcLeftIcon, project.IconLeft);
        AddIconOverride(metadataDonor?.IconPaths.Right ?? UimdGenService.SrcRightIcon, project.IconRight);
        var uimdResult = new UimdGenService(_projectRootText.Text.Trim()).Generate(
            uimdOutputBase,
            uimdPkg,
            icons.Count > 0 ? icons : null,
            pawnTag: pawnTag,
            donor: metadataDonor);
        var uimdSource = metadataDonor is null
            ? "from base Batman metadata"
            : $"from {UnrealPathUtil.AssetName(metadataDonor.UimdPackagePath)}";
        AppendLog($"UIMD generate: {uimdResult.Status}{(icons.Count > 0 ? $" ({icons.Count} changed icon(s))" : $" ({uimdSource})")}");
        if (!string.IsNullOrWhiteSpace(uimdResult.Error))
        {
            AppendLog("  " + uimdResult.Error);
            if (requireSuccess)
            {
                throw new InvalidOperationException(
                    $"Required UIMD generation failed ({uimdResult.Status}): {uimdResult.Error}");
            }
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

        var result = new DcmdGenService(_projectRootText.Text.Trim())
            .Generate(
                outputBase,
                dcmdPkg!,
                playablePkg!,
                cutscenePkg!,
                uimdPackagePath: uimdPkg,
                targetPawnTag: pawnTag,
                progressTag: project.ProgressTag,
                donor: metadataDonor);

        AppendLog($"DCMD generate: {result.Status} (PawnTag -> {pawnTag}, written beside BP assets at {dcmdPkg})");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppendLog("  " + result.Error);
            if (requireSuccess)
            {
                throw new InvalidOperationException(
                    $"Required DCMD generation failed ({result.Status}): {result.Error}");
            }
        }
        else
        {
            AppendLog($"  wrote {Path.GetFileName(result.OutputUasset)} ({result.Repointed.Count} name(s) repointed, UIMetaData -> {uimdPkg})");
            InjectStagedEquipment(project, result.OutputUasset, requireSuccess);
        }

        void AddIconOverride(string source, string target)
        {
            if (!string.IsNullOrWhiteSpace(source) &&
                !string.IsNullOrWhiteSpace(target) &&
                !source.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                icons[source] = target;
            }
        }
    }

    /// <summary>
    /// Injects the suit's staged extra gadgets (project.EquipmentAdds) into the
    /// freshly-written DCMD's EquipmentList. Resolves each gadget's DA_ETA package
    /// (and its upgrade set when present) from the shipped catalog.
    /// </summary>
    private void InjectStagedEquipment(
        NativeSuitProject project,
        string dcmdUasset,
        bool requireSuccess = false)
    {
        if (project.EquipmentSlots.Count == 0)
        {
            return;
        }

        var gd = GameDataService.Instance;
        var refs = new List<DcmdGenService.EquipmentSlotRef>();
        var unresolved = new List<string>();
        foreach (var change in project.EquipmentSlots.OrderBy(s => s.Slot))
        {
            var eq = gd.FindEquipment(change.Gadget);
            var resolutionError = EquipmentDependencyService.SavedChangeResolutionError(change, eq);
            if (resolutionError is not null)
            {
                AppendLog("  " + resolutionError + " — skipped");
                unresolved.Add(resolutionError);
                continue;
            }
            // SavedChangeResolutionError established both the catalog record and required ETA.
            var resolvedEquipment = eq!;
            var upgrade = string.IsNullOrWhiteSpace(resolvedEquipment.UpgradePackage)
                ? null
                : resolvedEquipment.UpgradePackage;
            refs.Add(new DcmdGenService.EquipmentSlotRef(
                change.Slot,
                resolvedEquipment.Name,
                resolvedEquipment.EtaPackage,
                upgrade));
        }

        if (requireSuccess && unresolved.Count > 0)
        {
            throw new InvalidOperationException(
                "Required DCMD equipment injection could not resolve every saved change: " +
                string.Join(" | ", unresolved));
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
            if (requireSuccess)
            {
                throw new InvalidOperationException(
                    $"Required DCMD equipment injection failed ({r.Status}): {r.Error}");
            }
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
    /// Writes the build-ID manifest beside the freshly-packaged IoStore trio. Returning null keeps
    /// callers from treating an unverified trio as eligible for automatic install.
    /// </summary>
    private BuildManifestService.Manifest? WriteBuildManifest(NativeSuitProject project, string buildId, string contentRootPacked,
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
            AppendLog($"  build_id {manifest.BuildId} · {manifest.ShippedPackages.Count} included package(s) · {manifest.TrioFiles.Count} trio file(s) · {warnings.Count} warning(s)");
            ShowPackageSuccessDialog(manifest, ioStoreDir);
            return manifest;
        }
        catch (Exception ex)
        {
            AppendLog($"  ⚠ could not write build manifest: {ex.Message}");
            return null;
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

        using var dlg = new AdaptiveDialogForm
        {
            Text = "Package built",
            Width = 620,
            Height = 560,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(560, 480),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(14, 12, 14, 0),
            Font = AppFonts.Condensed(13f, FontStyle.Bold),
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
                $"Included packages: {manifest.ShippedPackages.Count}\n" +
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
        install.Click += (_, _) =>
        {
            dlg.Close();
            InstallFreshlyBuiltTrio(PackageBuildResult.Completed(manifest, ioStoreDir));
        };
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
