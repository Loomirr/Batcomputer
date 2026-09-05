using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Texture import/cook and the UIMD + icon assets that ride along with a suit.
/// </summary>
public sealed partial class MainForm
{
    private const string NativeUimdIconCookProfile = "ui-suit-256-bc7";
    private const string NativeCharacterIconCookProfile = "ui-character-512-bc7";
    private const string NativeFaceDetailColorCookProfile = "face-detail-256x128-bc7";
    internal const string NativeFaceArtCookProfile = "face-art-512-bc7";
    private const string NativeFaceDetailNormalCookProfile = "face-detail-128-bc5";
    private const string NativeFaceDetailFullColorCookProfile = "face-detail-2048-bc7";
    private const string NativeFaceDetailFullNormalCookProfile = "face-detail-512-bc5";
    private const string NativeCtCookProfile = "ct-512-dxt1-native";
    private const string NativeRaoCookProfile = "rao-1024-dxt1-native";
    internal const string NativeMmrCookProfile = "mmr-2k-dxt1-native";

    internal sealed record UimdIconRecipeRequirement(
        string Role,
        string Path,
        string Kind,
        string CookProfile,
        string TemplateFolder,
        int Size);

    private enum TextureProfileSafety
    {
        Verified,
        Experimental,
    }

    internal enum TexturePackageRollbackDisposition
    {
        RestoredCoherentSnapshot,
        KeptVerifiedCurrentCook,
        PendingNoCoherentSnapshot,
    }

    private sealed record TextureCookPreset(
        string Id,
        string Label,
        string TemplateJson,
        int Width,
        int Height,
        string PixelFormat,
        TextureProfileSafety Safety,
        string ValidationNote)
    {
        public string Detail => $"{Width} x {Height} - {PixelFormat}";
        public string SafetyLabel => Safety == TextureProfileSafety.Verified ? "Verified" : "Experimental";

        public override string ToString() => $"{Label} [{SafetyLabel}] - {Detail}";
    }

    private sealed class TextureBackupSnapshot
    {
        public string CreatedUtc { get; set; } = "";
        public string Reason { get; set; } = "";
        public GeneratedTextureEntry Texture { get; set; } = new();
    }

    private sealed class TextureBackupManifest
    {
        public int SchemaVersion { get; set; } = 2;
        public bool SourceMatchesCook { get; set; }
        public bool IsCoherentSnapshot { get; set; }
        public string ValidationMode { get; set; } = "";
        public string SourceBackupName { get; set; } = "";
        public string TemplateJsonBackupName { get; set; } = "";
        public List<TextureBackupMember> Members { get; set; } = new();
    }

    private sealed class TextureBackupMember
    {
        public string Name { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private static Image? TryLoadCategoryIcon(string category)
    {
        var fileNames = category.Equals("3D viewer", StringComparison.OrdinalIgnoreCase)
            ? new[] { "3D.gif" }
            : category.Equals("Build mod", StringComparison.OrdinalIgnoreCase)
                ? new[] { "BuildMod.png" }
            : category.Equals("Textures", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Textures.png", "Materials.png" }
                : new[] { category + ".png" };

        foreach (var fileName in fileNames)
        {
            // Embedded in the assembly (see EmbeddedAssets); text fallback if one is missing.
            if (EmbeddedAssets.Load(fileName, new Size(22, 22)) is { } icon)
            {
                return icon;
            }
        }

        return null;
    }

    private void OpenIconsDialog()
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Editing suit icon paths"))
        {
            return;
        }

        if (_currentProject is null)
        {
            AppendLog("Set or load a base suit first (Base → Set base / Open suit).");
            return;
        }

        var donor = NativeMetadataDonorService.TryRead(
            _currentProject.DcmdTemplate,
            _currentProject.PlayableTemplate,
            _currentProject.CutsceneTemplate);
        var donorIcons = donor?.IconPaths ?? NativeMetadataDonorService.Icons.Empty;
        var generatedUiTextures = _currentProject.GeneratedTextures
            .Where(texture => IsUiTextureKind(texture.Kind) && !string.IsNullOrWhiteSpace(texture.PackagePath))
            .OrderBy(texture => texture.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var dlg = new UimdIconsDialog(
            donor?.UimdPackagePath ?? "",
            donorIcons,
            IconValueForDialog(_currentProject, _currentProject.IconMenu),
            IconValueForDialog(_currentProject, _currentProject.IconSuit),
            IconValueForDialog(_currentProject, _currentProject.IconLeft),
            IconValueForDialog(_currentProject, _currentProject.IconRight),
            generatedUiTextures);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        // Store icon paths raw (trimmed) so an explicit object suffix like
        // "...ElectricSuitFront.0" is preserved - UimdGenService honors it.
        _currentProject.IconMenu = PersistedIconValue(dlg.IconMenu, donorIcons.Menu);
        _currentProject.IconSuit = PersistedIconValue(dlg.IconSuit, donorIcons.Suit);
        _currentProject.IconLeft = PersistedIconValue(dlg.IconLeft, donorIcons.Left);
        _currentProject.IconRight = PersistedIconValue(dlg.IconRight, donorIcons.Right);
        NormalizeGeneratedUimdIconRecipes(_currentProject);
        (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        AppendLog("Saved suit icon paths. Repackage to bake them into the UIMD.");
    }

    private static string PersistedIconValue(string current, string donor) =>
        current.Equals(donor, StringComparison.OrdinalIgnoreCase) ? "" : current.Trim();

    private static string IconValueForDialog(NativeSuitProject project, string value)
    {
        var icon = value.Trim();
        if (string.IsNullOrWhiteSpace(icon) ||
            !icon.Contains("/UI/T_UI_Icon", StringComparison.OrdinalIgnoreCase))
        {
            return icon;
        }

        var matchesGeneratedTexture = project.GeneratedTextures.Any(texture =>
            texture.PackagePath.Equals(icon, StringComparison.OrdinalIgnoreCase) ||
            texture.ObjectPath.Equals(icon, StringComparison.OrdinalIgnoreCase));
        return matchesGeneratedTexture ? icon : "";
    }

    /// <summary>
    /// Thumbnail of an imported texture's source PNG, for use as a tile background.
    /// Returns a fresh CLONE per call, because VirtualTilePanel owns tile images and disposes
    /// them on SetTiles - handing out the cached master would dispose it out from under the
    /// next refresh. The decode is cached because the toybox rebuilds its tiles on every
    /// (debounced) search keystroke, and re-decoding several 2048x2048 PNGs per keystroke is
    /// what makes a grid feel sluggish. Downscaled on load: 2048x2048 is ~16MB as a full copy.
    /// </summary>
    private Image? LoadTextureThumbnail(string? sourcePng)
    {
        var path = sourcePng?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            if (!_textureThumbnailCache.TryGetValue(key, out var master))
            {
                // 'using' matters: Image.FromFile holds a lock on the file until disposed,
                // which would block re-importing over the same PNG.
                using var source = Image.FromFile(path);

                // Larger than a tile so DrawImageCover still has pixels to crop from.
                const int MaxEdge = 256;
                var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(source.Width, source.Height));
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));

                var thumbnail = new Bitmap(width, height);
                using (var g = Graphics.FromImage(thumbnail))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle(0, 0, width, height));
                }

                master = thumbnail;
                _textureThumbnailCache[key] = master;
            }

            return (Image)master.Clone();
        }
        catch
        {
            return null;
        }
    }

    private void DisposeTextureThumbnailCache()
    {
        foreach (var image in _textureThumbnailCache.Values)
        {
            try { image.Dispose(); } catch { /* best effort */ }
        }

        _textureThumbnailCache.Clear();
    }

    private void UseGeneratedSuitIconAsCover(SuitProjectService.ProjectSummary summary)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Changing the suit cover"))
        {
            return;
        }

        var svc = new SuitProjectService(_projectRootText.Text.Trim());
        var project = svc.LoadProject(summary.Path);
        var icon = project?.GeneratedTextures.FirstOrDefault(texture =>
            texture.Kind.Contains("UI", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(texture.SourcePng) &&
            File.Exists(texture.SourcePng));
        if (project is null || icon is null)
        {
            AppendLog($"No local generated UI icon is available for '{summary.DisplayName}'. Choose a cover image manually first.");
            return;
        }

        try
        {
            CopyCoverIntoProject(svc, project, icon.SourcePng);
            AppendLog($"Used generated UI icon '{icon.DisplayName}' as the cover for '{project.DisplayName}'.");
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            AppendLog($"Cover icon failed: {ex.Message}");
        }
    }

    private void RefreshTextureTiles(string? type)
    {
        var search = CurrentToyboxSearch();
        var header =
            "Textures are cooked Texture2D assets made from PNGs by copying a tested game template.\n" +
            "Click a texture tile to copy its /Game package path; right-click for object path, output folder, and source PNG helpers. " +
            "Use the copied path in your custom material texture parameters.";

        if (type == "Texture cooker notes")
        {
            ShowVirtualTiles(new List<VirtualTilePanel.Tile>
            {
                new()
                {
                    Title = "Native cook profiles",
                    Subtitle = "verified and experimental recipes",
                    Accent = Theme.Textures,
                    OnClick = () => AppendLog("Texture notes: verified profiles have passed their intended in-game use. Experimental profiles require confirmation and create a restorable backup before they replace an existing cooked texture."),
                    ToolTip = "Each profile uses a real native Texture2D template with matching mip layout. Check the profile safety label before importing or recooking."
                },
                new()
                {
                    Title = "Path format",
                    Subtitle = "/Game/Mods/<mod>/Textures/<name>",
                    Accent = Theme.Textures,
                    OnClick = () => CopyText("/Game/Mods/ExampleMod/Textures/T_Example", "Copied example texture package path."),
                    ToolTip = "Verified standalone Texture2D donors keep the owning mod's clean Textures folder and the unique name you enter."
                },
                new()
                {
                    Title = "Packaging",
                    Subtitle = "normal suit pak",
                    Accent = Theme.Textures,
                    OnClick = () => AppendLog("Generated Texture2D assets are staged directly into the owning suit mod when you build it; Batcomputer does not create a separate texture test pak."),
                    ToolTip = "Build mod includes only the generated texture assets referenced by that suit, under its own /Game/Mods/<mod>/Textures path."
                }
            }, header);
            return;
        }

        var tiles = new List<VirtualTilePanel.Tile>
        {
            new()
            {
                Title = "＋ Import PNG",
                Subtitle = "cook Texture2D",
                Accent = Theme.Textures,
                Dashed = true,
                OnClick = () => { _ = ImportTextureFromPngAsync(); },
                ToolTip = "Imports a PNG using a selected native cook profile, then remembers its /Game path on this suit. Compact mask and icon profiles reduce the final mod size."
            }
        };

        if (_currentProject?.GeneratedTextures.Count > 0)
        {
            tiles.Add(new VirtualTilePanel.Tile
            {
                Title = "↻ Reimport all",
                Subtitle = "this suit's textures",
                Accent = Theme.Textures,
                OnClick = () => { _ = ReimportAllCurrentSuitTexturesAsync(); },
                ToolTip = "Recooks every saved texture source for this suit with its existing profile. Coherent source/package snapshots roll back; verified edited-source cooks are kept and unresolved ones remain pending."
            });
        }

        if (_currentProject is null || _currentProject.GeneratedTextures.Count == 0)
        {
            ShowVirtualTiles(tiles, header + "\n\nNo generated textures saved on this suit yet.");
            return;
        }

        foreach (var texture in _currentProject.GeneratedTextures
            .Where(t => MatchesToyboxSearch(search, t.DisplayName, t.Kind, t.PackagePath, t.ObjectPath, t.SourcePng, t.PackageBaseName))
            .OrderByDescending(t => t.CreatedUtc, StringComparer.OrdinalIgnoreCase))
        {
            var title = string.IsNullOrWhiteSpace(texture.DisplayName)
                ? UnrealPathUtil.AssetName(texture.PackagePath)
                : texture.DisplayName;
            var exists = !string.IsNullOrWhiteSpace(texture.IoStoreRoot) && Directory.Exists(texture.IoStoreRoot);
            var safety = TextureProfileSafetyFor(texture.CookProfile, texture.Kind);
            tiles.Add(new VirtualTilePanel.Tile
            {
                Title = TrimMiddle(title, 30),
                Subtitle = $"{texture.Kind} · {TextureProfileSafetyLabel(safety)}\n{TextureCookDetail(texture)} · {TrimMiddle(texture.PackagePath, 38)}",
                Accent = exists ? Theme.Textures : Theme.OnDarkMuted,
                Image = LoadTextureThumbnail(texture.SourcePng),
                OnClick = () => CopyText(texture.PackagePath, $"Copied texture package path: {texture.PackagePath}"),
                ToolTip =
                    $"Package: {texture.PackagePath}\n" +
                    $"Object: {TextureObjectPath(texture)}\n" +
                    $"Safety: {TextureProfileSafetyLabel(safety)}\n" +
                    $"Note: {TextureProfileValidationNote(texture.CookProfile, texture.Kind)}\n" +
                    $"Cook: {TextureCookDetail(texture)}\n" +
                    $"PNG: {texture.SourcePng}\n" +
                    $"IoStore: {texture.IoStoreRoot}",
                MenuFactory = () => BuildTextureTileMenu(texture),
            });
        }

        ShowVirtualTiles(tiles, header, $"No generated textures matched '{search}'.");
    }

    private ContextMenuStrip BuildTextureTileMenu(GeneratedTextureEntry texture)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy package path", null, (_, _) => CopyText(texture.PackagePath, $"Copied texture package path: {texture.PackagePath}"));
        menu.Items.Add("Copy object path", null, (_, _) => CopyText(TextureObjectPath(texture), $"Copied texture object path: {TextureObjectPath(texture)}"));
        menu.Items.Add("Copy source PNG path", null, (_, _) => CopyText(texture.SourcePng, $"Copied source PNG path: {texture.SourcePng}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("View recipe safety…", null, (_, _) => ShowTextureRecipeSafety(texture));
        menu.Items.Add("Reimport image", null, (_, _) => { _ = ReimportCurrentSuitTextureAsync(texture); });
        menu.Items.Add("Replace image…", null, (_, _) => { _ = ReplaceCurrentSuitTextureImageAsync(texture); });
        menu.Items.Add("Change cook profile…", null, (_, _) => ChangeGeneratedTextureCookProfile(texture));
        var restore = menu.Items.Add("Restore latest texture backup", null, (_, _) => RestoreLatestTextureBackup(texture));
        restore.Enabled = FindLatestTextureBackup(texture) is not null;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open output folder", null, (_, _) => OpenTextureOutputFolder(texture));
        menu.Items.Add("Copy IoStore folder", null, (_, _) => CopyText(texture.IoStoreRoot, $"Copied IoStore folder: {texture.IoStoreRoot}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete texture from suit", null, (_, _) => DeleteGeneratedTexture(texture, deleteFiles: false));
        menu.Items.Add("Delete texture + generated files", null, (_, _) => DeleteGeneratedTexture(texture, deleteFiles: true));
        return menu;
    }

    private async Task ReimportCurrentSuitTextureAsync(GeneratedTextureEntry texture)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("reimport the generated texture"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null || !_currentProject.GeneratedTextures.Contains(texture))
        {
            return;
        }

        var project = _currentProject;
        var projectRoot = _projectRootText.Text.Trim();
        var editContext = CaptureCurrentProjectEditContext(project, projectRoot);
        using var rebuildLease = await EnterRebuildTransactionAsync();
        if (!CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Texture reimport stopped because another suit or workspace was selected.");
            return;
        }

        SuitProjectService.ProjectFileRollbackSnapshot projectFileRollback;
        try
        {
            projectFileRollback = await RunWithFileLockRetryAsync(
                () => editContext.Service.CaptureProjectFileRollback(project.SlotId),
                "snapshot the suit recipe before reimporting the texture");
        }
        catch (Exception ex)
        {
            AppendLog("Texture reimport stopped before cooking: " + ex.Message);
            return;
        }
        if (!CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Texture reimport stopped because another suit or workspace was selected.");
            return;
        }

        var priorRecipe = CloneGeneratedTextureEntry(texture);
        if (!ReimportGeneratedTextureSource(
                texture,
                confirm: true,
                createBackup: true,
                out var backupPath,
                out var hadPriorOutput))
        {
            return;
        }

        CurrentProjectSaveCapture? saveCapture = null;
        var projectSaveWritten = false;
        try
        {
            saveCapture = CaptureCurrentProjectSave(editContext, "save the reimported texture recipe");
            var saveResult = await CommitCurrentProjectSaveCaptureAsync(saveCapture);
            RequireCurrentProjectSaveCommitted(saveResult, "save the reimported texture recipe");
            projectSaveWritten = true;
            RecordChange("Textures", texture.DisplayName, texture.PackagePath, status: "reimported");
            _session.RaiseChanged();
            AppendLog($"Reimported and recooked texture '{texture.DisplayName}' from '{texture.SourcePng}'.");
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            Exception failure = ex;
            var restoreErrors = new List<Exception>();
            SuitProjectService.ProjectFileRestoreResult? projectRestore = null;
            try
            {
                projectRestore = await RunWithFileLockRetryAsync(
                    () => editContext.Service.TryRestoreProjectFile(
                        projectFileRollback,
                        saveCapture?.Snapshot,
                        projectSaveWritten,
                        () => CurrentProjectEditContextMatches(editContext)),
                    "restore the suit recipe after the texture save failed");
            }
            catch (Exception restoreError)
            {
                restoreErrors.Add(restoreError);
            }

            if (projectRestore?.Restored != true)
            {
                var ownershipText = projectRestore?.RejectedByContext == true
                    ? "another suit or workspace is now selected"
                    : "a newer save now owns this suit";
                AppendLog(
                    $"Texture reimport save failed, but rollback was not applied because {ownershipText}: {ex.Message}");
                Dialog.Error(
                    this,
                    "Texture reimport save was superseded",
                    $"The recook was not rolled back because {ownershipText}. The newer project state was left untouched.\n\n{ex.Message}");
                return;
            }

            TexturePackageRollbackDisposition? rollbackDisposition = null;
            var packageRollbackOwnershipLost = false;
            try
            {
                var rollbackStillOwned = editContext.Service.RunIfProjectFileRestoreStillCurrent(
                    projectRestore,
                    () =>
                    {
                        try
                        {
                            rollbackDisposition = RestoreTexturePackageFiles(texture, backupPath, hadPriorOutput);
                        }
                        finally
                        {
                            RestoreTextureRecipe(texture, priorRecipe);
                        }
                        if (rollbackDisposition.HasValue)
                        {
                            rollbackDisposition = TextureRollbackDispositionForFinalRecipe(
                                texture,
                                rollbackDisposition.Value);
                        }
                    });
                if (!rollbackStillOwned)
                {
                    packageRollbackOwnershipLost = true;
                }
            }
            catch (Exception restoreError)
            {
                restoreErrors.Add(restoreError);
            }

            if (packageRollbackOwnershipLost)
            {
                AppendLog(
                    $"Texture reimport save failed, and package rollback was skipped because a newer save took ownership: {failure.Message}");
                Dialog.Error(
                    this,
                    "Texture rollback was superseded",
                    "A newer save took ownership after the recook failed to save, so Batcomputer left its project and generated files untouched.\n\n" +
                    failure.Message);
                return;
            }

            if (restoreErrors.Count > 0)
            {
                failure = new AggregateException(
                    "The reimport could not be saved and the previous texture could not be completely restored.",
                    new[] { ex }.Concat(restoreErrors));
            }

            var rollbackText = rollbackDisposition switch
            {
                TexturePackageRollbackDisposition.RestoredCoherentSnapshot =>
                    "Restored the prior coherent source/package snapshot.",
                TexturePackageRollbackDisposition.PendingNoCoherentSnapshot =>
                    "No stale package-only snapshot was restored; this texture remains pending and must be reimported again.",
                _ =>
                    "Rollback could not be completed or verified; the texture remains pending and Batcomputer is not treating it as restored.",
            };
            AppendLog($"Texture reimport save failed. {rollbackText} {failure.Message}");
            Dialog.Error(
                this,
                "Texture reimport failed",
                "Batcomputer could not save the reimport. " + rollbackText +
                " Try again after closing any program that has the suit project open.\n\n" + failure.Message);
            RefreshToyboxTiles();
        }
    }

    internal static bool KeepVerifiedReimportCookInsteadOfPackageOnlyBackup(
        bool backupCanRestoreSource,
        bool currentCookVerified) =>
        TexturePackageRollbackDispositionFor(backupCanRestoreSource, currentCookVerified) ==
        TexturePackageRollbackDisposition.KeptVerifiedCurrentCook;

    internal static TexturePackageRollbackDisposition TexturePackageRollbackDispositionFor(
        bool backupCanRestoreSource,
        bool currentCookVerified) =>
        backupCanRestoreSource
            ? TexturePackageRollbackDisposition.RestoredCoherentSnapshot
            : currentCookVerified
                ? TexturePackageRollbackDisposition.KeptVerifiedCurrentCook
                : TexturePackageRollbackDisposition.PendingNoCoherentSnapshot;

    internal static TexturePackageRollbackDisposition TextureBatchRollbackDispositionFor(
        TexturePackageRollbackDisposition packageDisposition,
        bool verifiesAgainstRestoredRecipe) =>
        !verifiesAgainstRestoredRecipe &&
        packageDisposition is TexturePackageRollbackDisposition.KeptVerifiedCurrentCook or
            TexturePackageRollbackDisposition.RestoredCoherentSnapshot
            ? TexturePackageRollbackDisposition.PendingNoCoherentSnapshot
            : packageDisposition;

    private TexturePackageRollbackDisposition TextureRollbackDispositionForFinalRecipe(
        GeneratedTextureEntry finalRecipe,
        TexturePackageRollbackDisposition packageDisposition)
    {
        if (packageDisposition == TexturePackageRollbackDisposition.PendingNoCoherentSnapshot)
        {
            return packageDisposition;
        }

        var verifiesAgainstFinalRecipe =
            TryResolveSafeGeneratedTexturePaths(finalRecipe, out _, out var packageBase, out _) &&
            ValidateGeneratedTextureCook(finalRecipe, packageBase, out _);
        return TextureBatchRollbackDispositionFor(packageDisposition, verifiesAgainstFinalRecipe);
    }

    private async Task ReplaceCurrentSuitTextureImageAsync(GeneratedTextureEntry texture)
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("replace the generated texture image"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null || !_currentProject.GeneratedTextures.Contains(texture))
        {
            return;
        }
        var project = _currentProject;
        var projectRoot = _projectRootText.Text.Trim();

        using var dlg = new OpenFileDialog
        {
            Title = $"Replace image for {texture.DisplayName}",
            Filter = "Image files (*.png;*.bmp;*.jpg;*.jpeg)|*.png;*.bmp;*.jpg;*.jpeg|PNG images (*.png)|*.png|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ExistingDirectoryOrEmpty(texture.SourcePng),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!TryValidateTextureSourceImage(dlg.FileName, out var imageDetail, out var imageError))
        {
            AppendLog($"Texture image replacement blocked: {imageError}");
            Dialog.Error(this, "Image cannot be used", imageError);
            return;
        }

        if (!TryResolveGeneratedUimdIconRecipe(
                project,
                texture,
                out var iconRecipe,
                out var iconRecipeError))
        {
            AppendLog($"Texture image replacement blocked: {iconRecipeError}");
            Dialog.Warn(this, "Replace image", iconRecipeError);
            return;
        }

        var preflightError = GeneratedTextureReplacementPreflightError(
            texture,
            requireSavedTemplate: iconRecipe is null);
        if (!string.IsNullOrWhiteSpace(preflightError))
        {
            AppendLog($"Texture image replacement blocked: {preflightError}");
            Dialog.Warn(
                this,
                "Replace image",
                $"This texture cannot replace its image because its {preflightError}. Repair the saved texture recipe first.");
            return;
        }

        var preservedRecipeText = iconRecipe is null
            ? $"The existing {TextureCookDetail(texture)} cook profile"
            : $"The role-required native {iconRecipe.Size}px {iconRecipe.Kind.ToLowerInvariant()} profile (migrating an obsolete saved icon profile if needed)";
        if (!Dialog.Confirm(
                this,
                $"Replace image for {texture.DisplayName}?",
                $"Use {Path.GetFileName(dlg.FileName)} ({imageDetail}) as this texture's new saved source image?\n\n" +
                $"{preservedRecipeText} will be used, and the Unreal package path will be kept. The replacement will be cached with the suit and recooked now. " +
                "If a later step fails, Batcomputer restores only a complete, verified source/package snapshot; otherwise it keeps a verified new cook or marks the texture pending.",
                confirmText: "Replace + recook",
                severity: Dialog.Level.Warn))
        {
            return;
        }

        if (!ReferenceEquals(project, _currentProject) ||
            !_currentProject.GeneratedTextures.Contains(texture) ||
            !PathsEqual(projectRoot, _projectRootText.Text.Trim()))
        {
            AppendLog("Texture image replacement stopped because another suit or workspace was selected.");
            return;
        }
        var editContext = CaptureCurrentProjectEditContext(project, projectRoot);
        using var rebuildLease = await EnterRebuildTransactionAsync();
        if (!CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Texture image replacement stopped because another suit or workspace was selected.");
            return;
        }

        SuitProjectService.ProjectFileRollbackSnapshot projectFileRollback;
        try
        {
            projectFileRollback = await RunWithFileLockRetryAsync(
                () => editContext.Service.CaptureProjectFileRollback(project.SlotId),
                "snapshot the suit recipe before replacing the texture image");
        }
        catch (Exception ex)
        {
            AppendLog("Texture image replacement stopped before cooking: " + ex.Message);
            return;
        }
        if (!CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Texture image replacement stopped because another suit or workspace was selected.");
            return;
        }

        var priorRecipe = CloneGeneratedTextureEntry(texture);
        var hadPriorOutput = GeneratedTextureHasAnyCookedOutput(texture);
        var backupPath = CreateTextureBackup(texture, "Before replacing source image");
        if (hadPriorOutput && string.IsNullOrWhiteSpace(backupPath))
        {
            Dialog.Warn(
                this,
                "Replace image",
                "Batcomputer could not create a recoverable backup of the current cooked texture, so it left the image unchanged.");
            return;
        }
        if (hadPriorOutput && !TextureBackupHasCoherentSourceSnapshot(backupPath))
        {
            Dialog.Warn(
                this,
                "Replace image",
                "The current cooked texture does not have a source-coherent backup. Reimport its current saved image first, then use Replace image again. Nothing was changed.");
            return;
        }

        string? replacementSource = null;
        CurrentProjectSaveCapture? saveCapture = null;
        var projectSaveWritten = false;
        try
        {
            replacementSource = CacheReplacementTextureSource(dlg.FileName, texture.OutputRoot);
            texture.SourcePng = replacementSource;
            if (!ReimportGeneratedTextureSource(texture, confirm: false, createBackup: false))
            {
                throw new InvalidOperationException("The replacement image could not be cooked with this texture's saved profile.");
            }

            var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
            var sourceBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
            if (!ValidateGeneratedTextureCook(texture, sourceBase, out var validationError))
            {
                throw new InvalidOperationException("The replacement cook did not pass final validation: " + validationError);
            }

            saveCapture = CaptureCurrentProjectSave(editContext, "save the replacement texture recipe");
            var saveResult = await CommitCurrentProjectSaveCaptureAsync(saveCapture);
            RequireCurrentProjectSaveCommitted(saveResult, "save the replacement texture recipe");
            projectSaveWritten = true;
            RecordChange("Textures", texture.DisplayName, texture.PackagePath, status: "image replaced");
            _session.RaiseChanged();
            AppendLog(
                $"Replaced and recooked texture '{texture.DisplayName}' from '{Path.GetFileName(dlg.FileName)}'; " +
                $"package identity remains {texture.PackagePath}.");
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            Exception failure = ex;
            var restoreErrors = new List<Exception>();
            SuitProjectService.ProjectFileRestoreResult? projectRestore = null;
            try
            {
                projectRestore = await RunWithFileLockRetryAsync(
                    () => editContext.Service.TryRestoreProjectFile(
                        projectFileRollback,
                        saveCapture?.Snapshot,
                        projectSaveWritten,
                        () => CurrentProjectEditContextMatches(editContext)),
                    "restore the suit recipe after the replacement texture save failed");
            }
            catch (Exception restoreError)
            {
                restoreErrors.Add(restoreError);
            }

            if (projectRestore?.Restored != true)
            {
                var ownershipText = projectRestore?.RejectedByContext == true
                    ? "another suit or workspace is now selected"
                    : "a newer save now owns this suit";
                AppendLog(
                    $"Texture image replacement failed, but rollback was not applied because {ownershipText}: {ex.Message}");
                Dialog.Error(
                    this,
                    "Image replacement rollback was superseded",
                    $"The replacement was not rolled back because {ownershipText}. The newer project state was left untouched.\n\n{ex.Message}");
                return;
            }

            TexturePackageRollbackDisposition? rollbackDisposition = null;
            var packageRollbackOwnershipLost = false;
            try
            {
                var rollbackStillOwned = editContext.Service.RunIfProjectFileRestoreStillCurrent(
                    projectRestore,
                    () =>
                    {
                        try
                        {
                            rollbackDisposition = RestoreTexturePackageFiles(texture, backupPath, hadPriorOutput);
                        }
                        finally
                        {
                            RestoreTextureRecipe(texture, priorRecipe);
                        }
                        if (rollbackDisposition.HasValue)
                        {
                            rollbackDisposition = TextureRollbackDispositionForFinalRecipe(
                                texture,
                                rollbackDisposition.Value);
                        }
                    });
                if (!rollbackStillOwned)
                {
                    packageRollbackOwnershipLost = true;
                }
            }
            catch (Exception restoreError)
            {
                restoreErrors.Add(restoreError);
            }

            if (packageRollbackOwnershipLost)
            {
                AppendLog(
                    $"Texture image replacement failed, and package rollback was skipped because a newer save took ownership: {failure.Message}");
                Dialog.Error(
                    this,
                    "Image replacement rollback was superseded",
                    "A newer save took ownership while Batcomputer was resolving the failed replacement, so its project and generated files were left untouched.\n\n" +
                    failure.Message);
                return;
            }

            if (restoreErrors.Count > 0)
            {
                failure = new AggregateException(
                    "The replacement failed and the previous texture could not be completely restored.",
                    new[] { ex }.Concat(restoreErrors));
            }

            if (!packageRollbackOwnershipLost &&
                !string.IsNullOrWhiteSpace(replacementSource) &&
                !replacementSource.Equals(priorRecipe.SourcePng, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(replacementSource); } catch { /* best effort after rollback */ }
            }

            var rollbackText = rollbackDisposition switch
            {
                TexturePackageRollbackDisposition.RestoredCoherentSnapshot =>
                    "Restored the previous coherent source/package snapshot.",
                TexturePackageRollbackDisposition.PendingNoCoherentSnapshot =>
                    "No stale package-only snapshot was restored; the generated texture remains pending.",
                _ =>
                    "Rollback could not be completed or verified; the generated texture remains pending and is not being reported as restored.",
            };
            AppendLog($"Texture image replacement failed. {rollbackText} {failure.Message}");
            Dialog.Error(
                this,
                "Image replacement failed",
                rollbackText + "\n\n" + failure.Message);
            RefreshToyboxTiles();
        }
    }

    private static string ExistingDirectoryOrEmpty(string? path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path ?? "") ?? "";
            return Directory.Exists(directory) ? directory : "";
        }
        catch
        {
            return "";
        }
    }

    private static bool TryValidateTextureSourceImage(string path, out string detail, out string error)
    {
        detail = "";
        error = "";
        try
        {
            using var image = Image.FromFile(path);
            if (image.Width <= 0 || image.Height <= 0)
            {
                error = "The selected file does not contain a valid image size.";
                return false;
            }

            detail = $"{image.Width}x{image.Height}";
            return true;
        }
        catch (Exception ex)
        {
            error = $"The selected file could not be decoded as a PNG, BMP, or JPEG image.\n\n{ex.Message}";
            return false;
        }
    }

    private static string CacheReplacementTextureSource(string sourceImage, string outputRoot)
    {
        var sourceDirectory = Path.Combine(outputRoot, "Source");
        Directory.CreateDirectory(sourceDirectory);
        var extension = Path.GetExtension(sourceImage).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }
        var safeStem = MakeSafeTextureToken(Path.GetFileNameWithoutExtension(sourceImage));
        if (string.IsNullOrWhiteSpace(safeStem))
        {
            safeStem = "replacement";
        }
        var destination = Path.Combine(
            sourceDirectory,
            $"{safeStem}-replacement-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
        File.Copy(sourceImage, destination, overwrite: false);
        return destination;
    }

    private static GeneratedTextureEntry CloneGeneratedTextureEntry(GeneratedTextureEntry texture) =>
        JsonSerializer.Deserialize<GeneratedTextureEntry>(JsonSerializer.Serialize(texture))
        ?? throw new InvalidOperationException("Could not snapshot the texture recipe before replacing its image.");

    private string? GeneratedTextureReplacementPreflightError(
        GeneratedTextureEntry texture,
        bool requireSavedTemplate)
    {
        if (!TryResolveSafeGeneratedTexturePaths(texture, out _, out _, out var pathError))
        {
            return pathError;
        }
        if (requireSavedTemplate &&
            (string.IsNullOrWhiteSpace(texture.TemplateJson) || !File.Exists(texture.TemplateJson)))
        {
            return "saved donor template is missing";
        }
        var duplicateError = GeneratedTextureDuplicatePackageError(texture);
        if (!string.IsNullOrWhiteSpace(duplicateError))
        {
            return duplicateError;
        }
        return null;
    }

    private string? GeneratedTextureDuplicatePackageError(GeneratedTextureEntry texture)
    {
        if (_currentProject is null)
        {
            return null;
        }

        var package = UnrealPathUtil.NormalizePackagePath(texture.PackagePath);
        var owners = _currentProject.GeneratedTextures.Count(candidate =>
            UnrealPathUtil.NormalizePackagePath(candidate.PackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase));
        return owners > 1
            ? $"Unreal package path is owned by {owners} saved texture recipes; remove or rename the duplicate before recooking"
            : null;
    }

    private bool TryResolveSafeGeneratedTexturePaths(
        GeneratedTextureEntry texture,
        out string cookedContentRoot,
        out string packageBase,
        out string error)
    {
        cookedContentRoot = "";
        packageBase = "";
        error = "";
        if (string.IsNullOrWhiteSpace(texture.OutputRoot))
        {
            error = "cooked output folder is missing from the recipe";
            return false;
        }

        try
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(
                AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()),
                "TextureImports"));
            var outputRoot = Path.GetFullPath(texture.OutputRoot);
            if (!FileSystemPathUtil.IsWithinDirectory(outputRoot, allowedRoot, allowRoot: false))
            {
                error = "cooked output folder is outside this workspace's generated texture folder";
                return false;
            }

            var package = UnrealPathUtil.NormalizePackagePath(texture.PackagePath);
            if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                error = "Unreal package path must start with /Game/";
                return false;
            }
            var segments = package["/Game/".Length..].Split('/', StringSplitOptions.None);
            if (segments.Length == 0 || segments.Any(segment =>
                    string.IsNullOrWhiteSpace(segment) ||
                    segment is "." or ".." ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                error = "Unreal package path contains an empty, invalid, or traversal segment";
                return false;
            }

            cookedContentRoot = Path.GetFullPath(Path.Combine(
                outputRoot,
                "Cooked",
                "LEGOBatmanLotDK",
                "Content"));
            packageBase = Path.GetFullPath(PackagePathToContentPath(cookedContentRoot, package));
            if (!FileSystemPathUtil.IsWithinDirectory(packageBase, cookedContentRoot, allowRoot: false))
            {
                cookedContentRoot = "";
                packageBase = "";
                error = "Unreal package path resolves outside the texture's cooked Content folder";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            cookedContentRoot = "";
            packageBase = "";
            error = "saved output/package path is invalid (" + ex.Message + ")";
            return false;
        }
    }

    private async Task ReimportAllCurrentSuitTexturesAsync()
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("reimport all generated textures"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null || _currentProject.GeneratedTextures.Count == 0)
        {
            Dialog.Info(this, "Reimport all textures", "This suit does not have any saved generated textures.");
            return;
        }

        var project = _currentProject;
        var projectRoot = _projectRootText.Text.Trim();
        var editContext = CaptureCurrentProjectEditContext(project, projectRoot);

        var textures = project.GeneratedTextures
            .OrderBy(texture => texture.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(texture => texture.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var duplicatePackages = textures
            .Where(texture => !string.IsNullOrWhiteSpace(texture.PackagePath))
            .GroupBy(texture => UnrealPathUtil.NormalizePackagePath(texture.PackagePath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicatePackages.Count > 0)
        {
            AppendLog("Reimport all textures stopped: duplicate saved package path(s): " + string.Join("; ", duplicatePackages));
            Dialog.Error(
                this,
                "Duplicate texture recipes",
                "Nothing was changed. More than one saved texture recipe owns the same package path:\n\n" +
                string.Join("\n", duplicatePackages.Select(package => "• " + package)));
            return;
        }
        var preflightFailures = textures
            .Select(texture =>
            {
                if (!TryResolveGeneratedUimdIconRecipe(
                        project,
                        texture,
                        out var iconRecipe,
                        out var iconRecipeError))
                {
                    return (Texture: texture, Error: iconRecipeError);
                }

                // A legacy UIMD portrait may still point at an old 256px template that no
                // longer exists. Its required 512px donor is prepared immediately before
                // the recook, so do not reject it solely because the obsolete template is gone.
                return (
                    Texture: texture,
                    Error: GeneratedTextureReimportPreflightError(
                        texture,
                        requireSavedTemplate: iconRecipe is null));
            })
            .Where(result => !string.IsNullOrWhiteSpace(result.Error))
            .ToList();
        if (preflightFailures.Count > 0)
        {
            var detail = string.Join("\n", preflightFailures.Select(result =>
                $"• {result.Texture.DisplayName}: {result.Error}"));
            AppendLog("Reimport all textures stopped during preflight: " +
                      string.Join(" | ", preflightFailures.Select(result => $"{result.Texture.DisplayName}: {result.Error}")));
            Dialog.Error(this, "Textures need attention", "Nothing was changed. Fix these saved texture recipes first:\n\n" + detail);
            return;
        }

        if (!Dialog.Confirm(
                this,
                "Reimport all textures",
                $"Recook all {textures.Count} saved texture(s) for '{project.DisplayName}' using their saved profiles? " +
                "Legacy UIMD icons will migrate to the profile required by their assigned slot.\n\n" +
                "Batcomputer will snapshot every current package first. A texture whose source image was already edited cannot restore " +
                "its old package without the missing old source bytes; on failure it will keep a verified new cook or remain pending for another reimport.",
                confirmText: "Reimport all",
                severity: Dialog.Level.Warn))
        {
            return;
        }

        using var rebuildLease = await EnterRebuildTransactionAsync();
        if (!CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Reimport all textures stopped because another suit or workspace was selected.");
            return;
        }
        SuitProjectService.ProjectFileRollbackSnapshot projectFileRollback;
        try
        {
            projectFileRollback = await RunWithFileLockRetryAsync(
                () => editContext.Service.CaptureProjectFileRollback(project.SlotId),
                "snapshot the suit recipe before reimporting its textures");
        }
        catch (Exception ex)
        {
            AppendLog("Reimport all textures stopped before cooking: " + ex.Message);
            return;
        }
        var priorProject = JsonSerializer.Deserialize<NativeSuitProject>(
            JsonSerializer.Serialize(project))
            ?? throw new InvalidOperationException("Could not snapshot the suit before reimporting its textures.");
        var backups = new List<(GeneratedTextureEntry Texture, string? BackupPath, bool HadOutput)>();
        var rollbackDispositions = new List<(GeneratedTextureEntry Texture, TexturePackageRollbackDisposition Disposition)>();
        Exception? failure = null;
        var abandonedForContext = false;
        var completed = 0;
        CurrentProjectSaveCapture? saveCapture = null;
        var projectSaveWritten = false;

        using (var progress = new ProgressDialog(this, "Reimporting suit textures", textures.Count))
        {
            try
            {
                if (!CurrentProjectEditContextMatches(editContext))
                {
                    throw new CurrentProjectSaveSupersededException(
                        "The texture batch stopped because another suit or workspace was selected.");
                }
                progress.Report("Snapshotting current cooked packages…");
                foreach (var texture in textures)
                {
                    var hadOutput = GeneratedTextureHasAnyCookedOutput(texture);
                    var backupPath = CreateTextureBackup(texture, "Before reimporting every suit texture");
                    if (hadOutput && string.IsNullOrWhiteSpace(backupPath))
                    {
                        throw new InvalidOperationException(
                            $"Could not snapshot the current cooked files for '{texture.DisplayName}'. No texture was recooked.");
                    }
                    backups.Add((texture, backupPath, hadOutput));
                }

                foreach (var texture in textures)
                {
                    progress.SetStep($"Texture {completed + 1} of {textures.Count}");
                    progress.Report(texture.DisplayName);
                    await Task.Yield();
                    if (!CurrentProjectEditContextMatches(editContext))
                    {
                        throw new CurrentProjectSaveSupersededException(
                            "The texture batch stopped because another suit or workspace was selected.");
                    }
                    if (!ReimportGeneratedTextureSource(texture, confirm: false, createBackup: false))
                    {
                        throw new InvalidOperationException($"'{texture.DisplayName}' could not be recooked from its saved recipe.");
                    }
                    completed++;
                    progress.Advance(completed, texture.DisplayName);
                }

                saveCapture = CaptureCurrentProjectSave(editContext, "save the reimported suit textures");
                var saveResult = await CommitCurrentProjectSaveCaptureAsync(saveCapture);
                RequireCurrentProjectSaveCommitted(saveResult, "save the reimported suit textures");
                projectSaveWritten = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                var superseded = ContainsCurrentProjectSaveSuperseded(ex);
                progress.SetStep("Resolving texture rollback state");
                SuitProjectService.ProjectFileRestoreResult? projectRestore = null;
                try
                {
                    projectRestore = await RunWithFileLockRetryAsync(
                        () => editContext.Service.TryRestoreProjectFile(
                            projectFileRollback,
                            saveCapture?.Snapshot,
                            projectSaveWritten,
                            () => CurrentProjectEditContextMatches(editContext)),
                        "restore the suit recipe after a failed texture batch");
                }
                catch (Exception restoreError)
                {
                    superseded |= ContainsCurrentProjectSaveSuperseded(restoreError);
                    failure = new AggregateException(
                        "The texture batch failed and the prior suit recipe could not be restored.",
                        failure,
                        restoreError);
                }

                // Cooked texture rollback is governed by the same project ownership check. If a
                // newer batch/save owns the recipe, this older catch must not copy stale packages
                // back over its outputs.
                var packageRollbackStillOwned = false;
                if (projectRestore?.Restored == true)
                {
                    try
                    {
                        packageRollbackStillOwned = editContext.Service.RunIfProjectFileRestoreStillCurrent(
                            projectRestore,
                            () =>
                            {
                                var restoredRecipesByPackage = priorProject.GeneratedTextures
                                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.PackagePath))
                                    .GroupBy(
                                        candidate => UnrealPathUtil.NormalizePackagePath(candidate.PackagePath),
                                        StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(
                                        group => group.Key,
                                        group => group.First(),
                                        StringComparer.OrdinalIgnoreCase);
                                foreach (var backup in backups.AsEnumerable().Reverse())
                                {
                                    try
                                    {
                                        var disposition = RestoreTexturePackageFiles(
                                            backup.Texture,
                                            backup.BackupPath,
                                            backup.HadOutput);

                                        // A package-only backup may legitimately leave a successful edited-source
                                        // cook in place, while a coherent backup can restore a package through its
                                        // own immutable template snapshot. The project rollback above still restores
                                        // the recipe that existed before the batch. A legacy UIMD migration or a
                                        // refreshed/missing donor can therefore leave that final recipe unable to
                                        // validate the package. Prove against the recipe that will actually remain on
                                        // disk before calling the result kept/restored; otherwise staging must treat
                                        // it as pending and the user can safely retry the batch.
                                        var verifiesAgainstRestoredRecipe = false;
                                        if ((disposition is TexturePackageRollbackDisposition.KeptVerifiedCurrentCook or
                                                 TexturePackageRollbackDisposition.RestoredCoherentSnapshot) &&
                                            restoredRecipesByPackage.TryGetValue(
                                                UnrealPathUtil.NormalizePackagePath(backup.Texture.PackagePath),
                                                out var restoredRecipe) &&
                                            TryResolveSafeGeneratedTexturePaths(
                                                restoredRecipe,
                                                out _,
                                                out var restoredPackageBase,
                                                out _))
                                        {
                                            verifiesAgainstRestoredRecipe = ValidateGeneratedTextureCook(
                                                restoredRecipe,
                                                restoredPackageBase,
                                                out _);
                                        }
                                        disposition = TextureBatchRollbackDispositionFor(
                                            disposition,
                                            verifiesAgainstRestoredRecipe);
                                        rollbackDispositions.Add((backup.Texture, disposition));
                                    }
                                    catch (Exception restoreError)
                                    {
                                        failure = new AggregateException(
                                            "The texture batch failed and at least one prior cooked package could not be restored.",
                                            failure,
                                            restoreError);
                                    }
                                }
                            });
                    }
                    catch (Exception restoreError)
                    {
                        failure = new AggregateException(
                            "The texture batch failed while verifying ownership of its cooked-package rollback.",
                            failure,
                            restoreError);
                    }
                    if (!packageRollbackStillOwned)
                    {
                        superseded = true;
                    }
                }

                abandonedForContext = superseded ||
                                      !CurrentProjectEditContextMatches(editContext) ||
                                      projectRestore?.Restored != true;
                if (!abandonedForContext)
                {
                    _currentProject = priorProject;
                    ApplyProjectToFields(priorProject);
                }
            }
        }

        if (abandonedForContext)
        {
            AppendLog("Reimport all textures stopped; rollback was limited by project ownership and the current editor was left unchanged.");
            return;
        }
        if (failure is null && !CurrentProjectEditContextMatches(editContext))
        {
            AppendLog("Reimport all textures completed for the original suit after another suit or workspace was selected; the current editor was left unchanged.");
            return;
        }

        _session.RaiseChanged();
        RefreshToyboxTiles();
        if (failure is not null)
        {
            var restoredCount = rollbackDispositions.Count(item =>
                item.Disposition == TexturePackageRollbackDisposition.RestoredCoherentSnapshot);
            var keptCount = rollbackDispositions.Count(item =>
                item.Disposition == TexturePackageRollbackDisposition.KeptVerifiedCurrentCook);
            var pendingCount = rollbackDispositions.Count(item =>
                item.Disposition == TexturePackageRollbackDisposition.PendingNoCoherentSnapshot);
            var rollbackSummary =
                $"Rollback result: {restoredCount} coherent snapshot(s) restored; {keptCount} verified new cook(s) kept; " +
                $"{pendingCount} texture(s) left pending because no coherent source snapshot existed. " +
                "No package-only snapshot was published. Retry the batch for any pending texture or unsaved recipe migration.";
            AppendLog($"Reimport all textures failed after {completed}/{textures.Count}. {rollbackSummary} {failure.Message}");
            Dialog.Error(
                this,
                "Texture reimport failed",
                "Batcomputer stopped the batch. " + rollbackSummary + "\n\n" + failure.Message);
            return;
        }

        RecordChange("Textures", "All suit textures", $"{textures.Count} texture(s)", status: "reimported");
        AppendLog($"Reimported all {textures.Count} texture(s) for '{project.DisplayName}' and saved the suit once.");
        Dialog.Success(this, "Textures reimported", $"Recooked and verified all {textures.Count} saved texture(s) for this suit.");
    }

    internal static string? GeneratedTextureReimportPreflightError(
        GeneratedTextureEntry texture,
        bool requireSavedTemplate = true)
    {
        if (string.IsNullOrWhiteSpace(texture.SourcePng) || !File.Exists(texture.SourcePng))
        {
            return "saved source PNG is missing";
        }
        if (string.IsNullOrWhiteSpace(texture.OutputRoot))
        {
            return "cooked output folder is missing from the recipe";
        }
        if (string.IsNullOrWhiteSpace(texture.PackagePath))
        {
            return "Unreal package path is missing from the recipe";
        }
        if (requireSavedTemplate &&
            (string.IsNullOrWhiteSpace(texture.TemplateJson) || !File.Exists(texture.TemplateJson)))
        {
            return "saved donor template is missing";
        }
        return null;
    }

    private bool ReimportGeneratedTextureSource(
        GeneratedTextureEntry texture,
        bool confirm,
        bool createBackup)
    {
        return ReimportGeneratedTextureSource(
            texture,
            confirm,
            createBackup,
            out _,
            out _);
    }

    private bool ReimportGeneratedTextureSource(
        GeneratedTextureEntry texture,
        bool confirm,
        bool createBackup,
        out string? createdBackupPath,
        out bool hadPriorOutput)
    {
        createdBackupPath = null;
        hadPriorOutput = false;
        if (!TryResolveSafeGeneratedTexturePaths(
                texture,
                out var cookedContentRoot,
                out _,
                out var pathError))
        {
            if (confirm)
            {
                Dialog.Warn(this, "Reimport image", "This texture cannot be recooked because its " + pathError + ".");
            }
            AppendLog("Texture reimport blocked: " + pathError);
            return false;
        }
        var duplicateError = GeneratedTextureDuplicatePackageError(texture);
        if (!string.IsNullOrWhiteSpace(duplicateError))
        {
            if (confirm)
            {
                Dialog.Warn(this, "Reimport image", "This texture cannot be recooked because its " + duplicateError + ".");
            }
            AppendLog("Texture reimport blocked: " + duplicateError);
            return false;
        }
        hadPriorOutput = GeneratedTextureHasAnyCookedOutput(texture);
        UimdIconRecipeRequirement? iconRecipe = null;
        if (_currentProject is not null &&
            !TryResolveGeneratedUimdIconRecipe(
                _currentProject,
                texture,
                out iconRecipe,
                out var iconRecipeError))
        {
            if (confirm)
            {
                Dialog.Warn(this, "Reimport image", iconRecipeError);
            }
            AppendLog($"Texture reimport blocked: {iconRecipeError}");
            return false;
        }
        var preflightError = GeneratedTextureReimportPreflightError(
            texture,
            requireSavedTemplate: iconRecipe is null);
        if (!string.IsNullOrWhiteSpace(preflightError))
        {
            if (confirm)
            {
                Dialog.Warn(this, "Reimport image", $"This texture cannot be reimported because its {preflightError}. Import it again instead.");
            }
            return false;
        }
        var recipeDescription = iconRecipe is null
            ? "its existing profile"
            : $"the native {iconRecipe.Size}px {iconRecipe.Kind.ToLowerInvariant()} profile required by its {iconRecipe.Role} icon slot";
        if (confirm && !Dialog.Confirm(this, $"Reimport {texture.DisplayName}?", $"The current PNG will be cooked again in place using {recipeDescription}.", "Reimport"))
        {
            return false;
        }

        var priorKind = texture.Kind;
        var priorCookProfile = texture.CookProfile;
        var priorCookWidth = texture.CookWidth;
        var priorCookHeight = texture.CookHeight;
        var priorCookPixelFormat = texture.CookPixelFormat;
        var priorTemplateJson = texture.TemplateJson;

        void RestorePriorRecipe()
        {
            texture.Kind = priorKind;
            texture.CookProfile = priorCookProfile;
            texture.CookWidth = priorCookWidth;
            texture.CookHeight = priorCookHeight;
            texture.CookPixelFormat = priorCookPixelFormat;
            texture.TemplateJson = priorTemplateJson;
        }

        // Back up the still-active cooked package and its original recipe before a legacy icon
        // is migrated to its role-correct 256/512 profile. This keeps "Restore latest backup"
        // truthful as well as protecting the immediate recook.
        if (createBackup)
        {
            createdBackupPath = CreateTextureBackup(texture, "Before reimporting source image");
            if (!string.IsNullOrWhiteSpace(createdBackupPath))
            {
                AppendLog(TextureBackupHasCoherentSourceSnapshot(createdBackupPath)
                    ? $"Coherent texture source/package backup created: {createdBackupPath}"
                    : $"Diagnostic package snapshot created (not source-restorable because the saved image differs from the old cook): {createdBackupPath}");
            }
            else if (hadPriorOutput)
            {
                if (confirm)
                {
                    Dialog.Warn(
                        this,
                        "Reimport image",
                        "Batcomputer could not create a recoverable backup of the current cooked texture, so it left the texture unchanged.");
                }
                return false;
            }
        }

        if (iconRecipe is not null &&
            !TryNormalizeGeneratedUimdIconRecipeForReimport(texture, iconRecipe, out var normalizationError))
        {
            RestorePriorRecipe();
            if (confirm)
            {
                Dialog.Warn(this, "Reimport image", normalizationError);
            }
            AppendLog($"Texture reimport blocked: {normalizationError}");
            return false;
        }

        preflightError = GeneratedTextureReimportPreflightError(texture);
        if (!string.IsNullOrWhiteSpace(preflightError))
        {
            RestorePriorRecipe();
            if (confirm)
            {
                Dialog.Warn(this, "Reimport image", $"This texture cannot be reimported because its {preflightError}. Import it again instead.");
            }
            return false;
        }

        if (!EnsureGeneratedTextureCooked(texture, cookedContentRoot, forceRecook: true))
        {
            RestorePriorRecipe();
            TexturePackageRollbackDisposition? rollbackDisposition = null;
            if (createBackup)
            {
                try
                {
                    rollbackDisposition = RestoreTexturePackageFiles(texture, createdBackupPath, hadPriorOutput);
                }
                catch (Exception restoreError)
                {
                    AppendLog($"Texture reimport rollback failed: {restoreError.Message}");
                }
                finally
                {
                    // RestoreTexturePackageFiles may temporarily select a backup-owned template
                    // for self-contained validation. This helper has not saved any recipe, so the
                    // final in-memory recipe must remain exactly what the caller started with.
                    RestorePriorRecipe();
                }
                if (rollbackDisposition.HasValue)
                {
                    rollbackDisposition = TextureRollbackDispositionForFinalRecipe(
                        texture,
                        rollbackDisposition.Value);
                }
            }
            var rollbackText = TextureRollbackFailureText(rollbackDisposition);
            AppendLog($"Texture reimport cook failed. {rollbackText}");
            if (confirm)
            {
                Dialog.Warn(this, "Reimport image", "The texture could not be cooked again. " + rollbackText);
            }
            return false;
        }

        var cookedBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
        var cookedReport = cookedBase + ".texture-cook-report.json";
        var validationError = "";
        if (!TextureCookReportSourceMatchesFile(cookedReport, texture.SourcePng) ||
            !ValidateGeneratedTextureCook(texture, cookedBase, out validationError))
        {
            validationError = string.IsNullOrWhiteSpace(validationError)
                ? "the new cook report does not match the exact source image bytes"
                : validationError;
            if (confirm)
            {
                Dialog.Warn(
                    this,
                    "Reimport image",
                    "The recook finished, but its source image or generated package could not be verified. The saved recipe was not updated.\n\n" + validationError);
            }
            AppendLog($"Texture reimport verification failed: {validationError}");
            RestorePriorRecipe();
            TexturePackageRollbackDisposition? rollbackDisposition = null;
            if (createBackup)
            {
                try
                {
                    rollbackDisposition = RestoreTexturePackageFiles(texture, createdBackupPath, hadPriorOutput);
                }
                catch (Exception restoreError)
                {
                    AppendLog($"Texture reimport rollback failed: {restoreError.Message}");
                }
                finally
                {
                    RestorePriorRecipe();
                }
                if (rollbackDisposition.HasValue)
                {
                    rollbackDisposition = TextureRollbackDispositionForFinalRecipe(
                        texture,
                        rollbackDisposition.Value);
                }
            }
            AppendLog(TextureRollbackFailureText(rollbackDisposition));
            return false;
        }

        texture.CreatedUtc = DateTime.UtcNow.ToString("O");
        return true;
    }

    private static string TextureRollbackFailureText(TexturePackageRollbackDisposition? disposition) =>
        disposition switch
        {
            TexturePackageRollbackDisposition.RestoredCoherentSnapshot =>
                "The prior coherent source/package snapshot was restored.",
            TexturePackageRollbackDisposition.KeptVerifiedCurrentCook =>
                "The current cook still verifies against the saved source, so it was kept.",
            TexturePackageRollbackDisposition.PendingNoCoherentSnapshot =>
                "No coherent source/package snapshot was available; no stale package-only snapshot was restored. The texture remains pending and must be reimported.",
            _ => "Rollback could not be completed or verified. The texture remains pending and Batcomputer is not treating any generated output as restored.",
        };

    private static string TextureObjectPath(GeneratedTextureEntry texture) =>
        string.IsNullOrWhiteSpace(texture.ObjectPath) ? ToObjectPath(texture.PackagePath) : texture.ObjectPath;

    private void OpenTextureOutputFolder(GeneratedTextureEntry texture)
    {
        var dir = !string.IsNullOrWhiteSpace(texture.IoStoreRoot) && Directory.Exists(texture.IoStoreRoot)
            ? texture.IoStoreRoot
            : texture.OutputRoot;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            AppendLog($"Texture output folder not found: {dir}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Could not open texture output folder: {ex.Message}");
        }
    }

    private void DeleteGeneratedTexture(GeneratedTextureEntry texture, bool deleteFiles)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Deleting the generated texture"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            AppendLog("No suit project is open.");
            return;
        }

        var display = string.IsNullOrWhiteSpace(texture.DisplayName)
            ? UnrealPathUtil.AssetName(texture.PackagePath)
            : texture.DisplayName;
        var message = deleteFiles
            ? $"Delete texture '{display}' from this suit and remove its generated output folder?\n\n{texture.PackagePath}"
            : $"Delete texture '{display}' from this suit?\n\n{texture.PackagePath}";

        if (!Dialog.Confirm(this, "Delete texture", message,
                confirmText: "Delete", severity: Dialog.Level.Crit))
        {
            return;
        }

        var removed = _currentProject.GeneratedTextures.RemoveAll(t =>
            ReferenceEquals(t, texture) ||
            (!string.IsNullOrWhiteSpace(t.PackagePath) && t.PackagePath.Equals(texture.PackagePath, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(t.DisplayName) && t.DisplayName.Equals(texture.DisplayName, StringComparison.OrdinalIgnoreCase)));

        if (removed == 0)
        {
            AppendLog($"Texture delete skipped: '{display}' was not found on this suit.");
            return;
        }

        if (deleteFiles)
        {
            DeleteGeneratedTextureOutputFolder(texture);
        }

        try
        {
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        }
        catch (Exception ex)
        {
            AppendLog($"Texture delete warning: project save failed: {ex.Message}");
        }

        RecordChange("Textures", display, texture.PackagePath, status: deleteFiles ? "deleted" : "removed");
        AppendLog($"Deleted texture '{display}' from this suit.");
        RefreshToyboxTiles();
    }

    private void DeleteGeneratedTextureOutputFolder(GeneratedTextureEntry texture)
    {
        if (string.IsNullOrWhiteSpace(texture.OutputRoot) || !Directory.Exists(texture.OutputRoot))
        {
            AppendLog($"Generated texture output folder not found: {texture.OutputRoot}");
            return;
        }

        var projectRoot = Path.GetFullPath(_projectRootText.Text.Trim());
        var allowedRoot = Path.GetFullPath(Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureImports"));
        var outputRoot = Path.GetFullPath(texture.OutputRoot);
        if (!FileSystemPathUtil.IsWithinDirectory(outputRoot, allowedRoot, allowRoot: true))
        {
            AppendLog($"Refused to delete texture output outside the Generated texture workspace: {outputRoot}");
            return;
        }

        try
        {
            Directory.Delete(outputRoot, recursive: true);
            AppendLog($"Deleted generated texture output folder: {outputRoot}");
        }
        catch (Exception ex)
        {
            AppendLog($"Texture output delete warning: {ex.Message}");
        }
    }

    private async Task ImportTextureFromPngAsync()
    {
        if (!await AwaitLoadedProjectStageRestoresBeforeEditAsync("import the generated texture"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var projectRoot = _projectRootText.Text.Trim();
        if (!await EnsureTextureCookTemplatesAsync(projectRoot))
        {
            return;
        }

        using var dlg = new OpenFileDialog
        {
            Title = "Import PNG as Texture2D",
            Filter = "PNG images (*.png)|*.png|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var textureSettings = PromptForTextureImportSettings(Path.GetFileNameWithoutExtension(dlg.FileName), projectRoot);
        if (textureSettings is null || string.IsNullOrWhiteSpace(textureSettings.Value.Name))
        {
            AppendLog("Texture import cancelled (no texture name entered).");
            return;
        }

        var requestedName = textureSettings.Value.Name;
        var textureKind = textureSettings.Value.Kind;
        var cookPreset = textureSettings.Value.Preset;
        if (!ConfirmTextureProfileSafety(cookPreset, "import this texture"))
        {
            AppendLog("Texture import cancelled: experimental profile was not confirmed.");
            return;
        }
        var templateJson = cookPreset.TemplateJson;
        if (string.IsNullOrWhiteSpace(templateJson) || !File.Exists(templateJson))
        {
            AppendLog("Texture import needs the selected verified Texture2D template in the Generated workspace.");
            AppendLog($"Looked for: {templateJson}");
            return;
        }

        var rawRoot = DefaultTextureSourceRawRoot(projectRoot);

        AppendLog($"Texture profile selected: {cookPreset.Label} ({cookPreset.Detail})");
        AppendLog($"  template: {Path.GetFileName(templateJson)}");
        if (_currentProject.GeneratedTextures.Any(t =>
                t.DisplayName.Equals(requestedName, StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog($"Texture import cancelled: this suit already has a generated texture named '{requestedName}'. Pick a unique name.");
            return;
        }

        var slotIndex = NextTextureSlotIndex(_currentProject);
        string outputPackagePath;
        try
        {
            outputPackagePath = TexturePackagePathFromUserName(templateJson, requestedName, slotIndex, _modFolderText.Text.Trim(), _currentProject.SlotId, textureKind);
            if (_currentProject.GeneratedTextures.Any(t =>
                    t.PackagePath.Equals(outputPackagePath, StringComparison.OrdinalIgnoreCase)))
            {
                AppendLog($"Texture import cancelled: generated package path already exists on this suit: {outputPackagePath}");
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Texture path could not be generated safely: {ex.Message}");
            return;
        }
        var packageBaseName = MakeSafePackageBaseName($"Texture_{MakeSafeTextureToken(requestedName)}_{slotIndex:00000}_P");
        var safeSlot = MakeSafePackageBaseName(string.IsNullOrWhiteSpace(_currentProject.SlotId) ? "unsaved_suit" : _currentProject.SlotId);
        var outputRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureImports", safeSlot, $"{MakeSafeTextureToken(requestedName)}_{slotIndex:00000}");
        string sourcePng;
        try
        {
            sourcePng = CopyTextureSourceIntoOutput(dlg.FileName, outputRoot);
        }
        catch (Exception ex)
        {
            AppendLog($"Texture import could not cache its source PNG: {ex.Message}");
            return;
        }

        AppendLog($"Texture import: {Path.GetFileName(dlg.FileName)} as '{requestedName}' ({textureKind})");
        AppendLog($"  package path: {outputPackagePath}");
        AppendLog($"  cooked files: {Path.Combine(outputRoot, "Cooked")}");
        AppendLog("  mode: cook-only (texture will be packed with the suit, no separate texture test pak)");

        _toyboxPrimaryActionButton.Enabled = false;
        try
        {
            var nearestMips = UseNearestNeighborMipsForTextureKind(textureKind, cookPreset.Id);
            AppendLog(nearestMips
                ? "  mip mode: nearest-neighbor"
                : "  mip mode: high-quality filtered mips (complete chain)");

            var cookedContentRoot = Path.Combine(outputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
            var cookResult = await Task.Run(() => new TextureCookService(projectRoot).Cook(new TextureCookService.Request
            {
                SourceImagePath = sourcePng,
                TemplateJsonPath = templateJson,
                OutputContentRoot = cookedContentRoot,
                OutputPackagePath = outputPackagePath,
                NearestNeighborMips = nearestMips,
                BleedTransparentRgb = IsUiTextureKind(textureKind),
                WriteInlineMips = true,
                Bc7InputLayout = "rgba",
                Bc7Quality = IsNativeUimdIconCookProfile(cookPreset.Id)
                    ? "best"
                    : "balanced",
            }));

            foreach (var log in cookResult.Log)
            {
                AppendLog($"  texture cook: {log}");
            }
            foreach (var warning in cookResult.Warnings)
            {
                AppendLog($"  texture cook warning: {warning}");
            }
            if (!cookResult.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Texture import failed: {cookResult.Error?.Split('\n').FirstOrDefault() ?? cookResult.Status}");
                return;
            }

            AppendLog($"Texture import complete: {cookResult.OutputPackagePath}");

            var entry = BuildTextureEntryFromSummary(
                outputRoot,
                sourcePng,
                templateJson,
                rawRoot,
                outputPackagePath,
                packageBaseName,
                requestedName,
                textureKind,
                cookPreset);

            _currentProject.GeneratedTextures.RemoveAll(t =>
                t.PackagePath.Equals(entry.PackagePath, StringComparison.OrdinalIgnoreCase));
            _currentProject.GeneratedTextures.Add(entry);
            AutoAssignGeneratedUiIconSlots(_currentProject);
            RecordChange("Textures", entry.DisplayName, entry.PackagePath, status: "staged");
            try { (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject); } catch { /* best effort */ }

            CopyText(entry.PackagePath, $"Texture ready and package path copied: {entry.PackagePath}");
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            AppendLog("Texture import failed:");
            AppendLog(ex.ToString());
        }
        finally
        {
            _toyboxPrimaryActionButton.Enabled = true;
        }
    }

    private async Task<bool> EnsureTextureCookTemplatesAsync(string projectRoot)
    {
        // The native suit-selector donor is optional and uses a different,
        // inline-mip layout from the general texture templates. Normalize it
        // whenever it is available, even when the core donors were already
        // prepared by an older Batcomputer build.
        TextureCookTemplateService.NormalizeNativeSuitIconTemplate(projectRoot);
        TextureCookTemplateService.NormalizeNativeCharacterIconTemplate(projectRoot);
        var extractedContentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var optionalFolders = new[]
        {
            TextureCookTemplateService.NativeFaceDetailColorTemplateFolder,
            TextureCookTemplateService.NativeFaceArtTemplateFolder,
            TextureCookTemplateService.NativeFaceDetailNormalTemplateFolder,
            TextureCookTemplateService.NativeFaceDetailFullColorTemplateFolder,
            TextureCookTemplateService.NativeFaceDetailFullNormalTemplateFolder,
            TextureCookTemplateService.NativeCtTemplateFolder,
            TextureCookTemplateService.NativeRaoTemplateFolder,
        };
        var optionalTemplateMissing = optionalFolders.Any(folder =>
            !TextureCookTemplateService.IsTemplateReady(TextureCookTemplateService.TemplateJsonPath(projectRoot, folder)));
        if (optionalTemplateMissing && Directory.Exists(extractedContentRoot))
        {
            // Face, CT, and RAO donors are optional enhancements. Do not let
            // them block normal importing, but do prepare every compatible new
            // donor from an already-extracted Content tree before returning on
            // the core-template fast path.
            var optionalResult = TextureCookTemplateService.PrepareFromContentRoot(projectRoot, extractedContentRoot);
            foreach (var line in optionalResult.Logs)
            {
                AppendLog("  " + line);
            }
        }
        if (TextureCookTemplateService.HasCoreTemplates(projectRoot))
        {
            return true;
        }

        AppendLog("Preparing the base-game texture cook templates…");
        UseWaitCursor = true;
        try
        {
            var result = await new GameAssetRefreshService(projectRoot)
                .PrepareTextureCookTemplatesAsync(CancellationToken.None);
            foreach (var line in result.Logs)
            {
                AppendLog("  " + line);
            }
            foreach (var warning in result.Warnings)
            {
                AppendLog("  texture template warning: " + warning);
            }

            TextureCookTemplateService.NormalizeNativeSuitIconTemplate(projectRoot);
            TextureCookTemplateService.NormalizeNativeCharacterIconTemplate(projectRoot);
            return TextureCookTemplateService.HasCoreTemplates(projectRoot);
        }
        catch (Exception ex)
        {
            AppendLog("Texture cook template setup failed: " + ex.Message);
            Dialog.Error(this, "Texture profiles unavailable",
                "Batcomputer could not prepare its base-game texture donors.\n\n" + ex.Message);
            return false;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private static string CopyTextureSourceIntoOutput(string sourcePng, string outputRoot)
    {
        var sourceDirectory = Path.Combine(outputRoot, "Source");
        Directory.CreateDirectory(sourceDirectory);
        var destination = Path.Combine(sourceDirectory, Path.GetFileName(sourcePng));
        File.Copy(sourcePng, destination, overwrite: true);
        return destination;
    }

    private GeneratedTextureEntry BuildTextureEntryFromSummary(
        string outputRoot,
        string sourcePng,
        string templateJson,
        string rawRoot,
        string outputPackagePath,
        string packageBaseName,
        string displayName,
        string kind,
        TextureCookPreset cookPreset)
    {
        var entry = new GeneratedTextureEntry
        {
            DisplayName = displayName,
            Kind = string.IsNullOrWhiteSpace(kind) ? "Texture" : kind,
            CookProfile = cookPreset.Id,
            CookWidth = cookPreset.Width,
            CookHeight = cookPreset.Height,
            CookPixelFormat = cookPreset.PixelFormat,
            SourcePng = sourcePng,
            PackagePath = outputPackagePath,
            ObjectPath = ToObjectPath(outputPackagePath),
            TemplateJson = templateJson,
            SourceRawRoot = rawRoot,
            OutputRoot = outputRoot,
            IoStoreRoot = Path.Combine(outputRoot, "IoStore"),
            PackageBaseName = packageBaseName,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };

        var summaryPath = Path.Combine(outputRoot, "texture-duplicate-summary.json");
        if (!File.Exists(summaryPath))
        {
            return entry;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("OutputPackagePath", out var pkg))
            {
                entry.PackagePath = pkg.GetString() ?? entry.PackagePath;
                entry.ObjectPath = ToObjectPath(entry.PackagePath);
            }
            if (root.TryGetProperty("IoStoreRoot", out var io))
            {
                entry.IoStoreRoot = io.GetString() ?? entry.IoStoreRoot;
            }
            if (root.TryGetProperty("PackageBaseName", out var pak))
            {
                entry.PackageBaseName = pak.GetString() ?? entry.PackageBaseName;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Texture summary parse warning: {ex.Message}");
        }

        return entry;
    }

    private static List<TextureCookPreset> AvailableTextureCookPresets(string projectRoot, string textureKind)
    {
        if (IsSurfaceMaskTextureKind(textureKind))
        {
            TextureCookTemplateService.NormalizeNativeMmrTemplate(
                projectRoot,
                AppSettings.Current.EffectiveExtractedContentRoot());
        }
        TextureCookTemplateService.NormalizeCoreTemplates(projectRoot);
        var bgraPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, "TextureStandaloneTemplate_DroneControlBGRA8");
        var bc5Path = TextureCookTemplateService.TemplateJsonPath(projectRoot, "TextureStandaloneTemplate_BatarangBC5");
        var dxt5Path = TextureCookTemplateService.TemplateJsonPath(projectRoot, "TextureStandaloneTemplate_BatclawLogo_DXT5");
        var nativeMmrPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeMmrTemplateFolder);
        var nativeSuitIconPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeSuitIconTemplateFolder);
        var nativeCharacterIconPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeCharacterIconTemplateFolder);
        var nativeFaceDetailColorPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeFaceDetailColorTemplateFolder);
        var nativeFaceArtPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeFaceArtTemplateFolder);
        var nativeFaceDetailNormalPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeFaceDetailNormalTemplateFolder);
        var nativeFaceDetailFullColorPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeFaceDetailFullColorTemplateFolder);
        var nativeFaceDetailFullNormalPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeFaceDetailFullNormalTemplateFolder);
        var nativeCtPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeCtTemplateFolder);
        var nativeRaoPath = TextureCookTemplateService.TemplateJsonPath(projectRoot, TextureCookTemplateService.NativeRaoTemplateFolder);
        var dxt1Path = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_EoMColorMask_DXT1", "T_TPAGE_Batman_TheBatman2025_ColourMask.json");
        var candidates = new List<TextureCookPreset>();
        void Add(
            string id,
            string label,
            string template,
            int width,
            int height,
            string pixelFormat,
            TextureProfileSafety safety,
            string validationNote)
        {
            if (TextureCookTemplateService.IsTemplateReady(template))
            {
                candidates.Add(new TextureCookPreset(id, label, template, width, height, pixelFormat, safety, validationNote));
            }
        }

        if (IsSuitSelectorIconTextureKind(textureKind))
        {
            Add(NativeUimdIconCookProfile, "Native 256px BC7 UIMD icon", nativeSuitIconPath, 256, 256, "PF_BC7",
                TextureProfileSafety.Verified,
                "Uses the game's native suit-selector Texture2D layout: BC7 with nine inline mips.");
        }
        else if (IsCharacterIconTextureKind(textureKind))
        {
            Add(NativeCharacterIconCookProfile, "Native 512px BC7 character icon", nativeCharacterIconPath, 512, 512, "PF_BC7",
                TextureProfileSafety.Verified,
                "Uses the native UIMD character-card layout for menu, left, and right portraits: 512px BC7 with ten inline mips.");
        }
        else if (IsUiTextureKind(textureKind))
        {
            Add(NativeCharacterIconCookProfile, "Native 512px BC7 character icon", nativeCharacterIconPath, 512, 512, "PF_BC7",
                TextureProfileSafety.Verified,
                "For UIMD menu, left, and right portraits. Use Suit selector icon for the 256px tile.");
            Add(NativeUimdIconCookProfile, "Native 256px BC7 suit selector icon", nativeSuitIconPath, 256, 256, "PF_BC7",
                TextureProfileSafety.Verified,
                "For the UIMD suit-selector tile only. Use Character icon for menu, left, and right portraits.");
        }
        else if (textureKind.Equals("Face detail", StringComparison.OrdinalIgnoreCase))
        {
            Add(NativeFaceDetailColorCookProfile, "Native 256×128 BC7 facial detail", nativeFaceDetailColorPath, 256, 128, "PF_BC7",
                TextureProfileSafety.Verified,
                "For brows, face-print strips, and other compact non-square facial detail maps. Do not use for a full body texture.");
            Add(NativeFaceArtCookProfile, "Native 512px BC7 animated face art", nativeFaceArtPath, 512, 512, "PF_BC7",
                TextureProfileSafety.Verified,
                "Linear square layout for SK_LEGOface animated eye and mouth sheets. Preserve RGBA: alpha is the visible-print stencil.");
            Add(NativeFaceDetailFullColorCookProfile, "Native 2K BC7 full face detail", nativeFaceDetailFullColorPath, 2048, 2048, "PF_BC7",
                TextureProfileSafety.Verified,
                "For full face-print, wrap, mask, and decal artwork. Native 2K BC7 layout with a complete external and inline mip chain.");
        }
        else if (textureKind.Equals("Face detail normal", StringComparison.OrdinalIgnoreCase))
        {
            Add(NativeFaceDetailNormalCookProfile, "Native 128px BC5 facial normal", nativeFaceDetailNormalPath, 128, 128, "PF_BC5",
                TextureProfileSafety.Verified,
                "For compact eye, brow, and other small facial-normal details.");
            Add(NativeFaceDetailFullNormalCookProfile, "Native 512px BC5 face normal", nativeFaceDetailFullNormalPath, 512, 512, "PF_BC5",
                TextureProfileSafety.Verified,
                "For larger full-face normal detail maps. Native 512px BC5 layout with a complete external and inline mip chain.");
        }
        else if (textureKind.Equals("CT map", StringComparison.OrdinalIgnoreCase))
        {
            Add(NativeCtCookProfile, "Native 512px DXT1 CT", nativeCtPath, 512, 512, "PF_DXT1", TextureProfileSafety.Verified,
                "Linear native CT layout for compact character, hair, and attachment surface-detail maps.");
        }
        else if (textureKind.Equals("RAO map", StringComparison.OrdinalIgnoreCase))
        {
            Add(NativeRaoCookProfile, "Native 1K DXT1 RAO", nativeRaoPath, 1024, 1024, "PF_DXT1", TextureProfileSafety.Verified,
                "Linear native RAO layout for roughness/ambient-occlusion surface maps.");
        }
        else if (textureKind.Contains("normal", StringComparison.OrdinalIgnoreCase))
        {
            Add("normal-2k-bc5-legacy", "2K BC5 normal", bc5Path, 2048, 2048, "PF_BC5",
                TextureProfileSafety.Verified, "Verified on Electric's body normal map in game.");
        }
        else if (IsColorMaskTextureKind(textureKind))
        {
            Add("mask-2k-bgra8", "2K BGRA8 mask", bgraPath, 2048, 2048, "PF_B8G8R8A8",
                TextureProfileSafety.Verified, "Complete 12-mip native chain; verified on Electric's current colour mask.");
            Add("mask-1k-dxt1-legacy", "1K DXT1 colour mask", dxt1Path, 1024, 1024, "PF_DXT1",
                TextureProfileSafety.Experimental, "Legacy Electric-compatible donor. Test this exact texture role in game first.");
        }
        else if (IsSurfaceMaskTextureKind(textureKind))
        {
            Add(NativeMmrCookProfile, "Native 2K DXT1 MMR", nativeMmrPath, 2048, 2048, "PF_DXT1",
                TextureProfileSafety.Verified,
                "Verified on Electric's MMR in game at all texture-quality settings. Uses native EoM MMR metadata and the complete 12-mip layout. R is metalness and B is roughness; G is unused.");
            Add("mask-2k-bgra8", "2K BGRA8 packed map (legacy)", bgraPath, 2048, 2048, "PF_B8G8R8A8",
                TextureProfileSafety.Experimental, "Legacy AO-donor route. It does not carry the game's native MMR sampling metadata.");
            Add("packed-2k-dxt5-legacy", "2K DXT5 packed map", dxt5Path, 2048, 2048, "PF_DXT5",
                TextureProfileSafety.Experimental, "Legacy Electric-compatible donor. Test the target material in game first.");
        }
        else
        {
            Add("character-2k-bgra8", "2K BGRA8 colour", bgraPath, 2048, 2048, "PF_B8G8R8A8",
                TextureProfileSafety.Verified, "Complete 12-mip native chain; verified on Electric's base-colour maps in game.");
            Add("character-2k-dxt5-legacy", "2K DXT5 colour / packed map", dxt5Path, 2048, 2048, "PF_DXT5",
                TextureProfileSafety.Experimental, "Legacy donor. Test this target texture role in game first.");
        }

        return candidates;
    }

    private static TextureProfileSafety TextureProfileSafetyFor(string? profileId, string? textureKind = null)
    {
        if (string.Equals(profileId, "mask-2k-bgra8", StringComparison.OrdinalIgnoreCase) &&
            IsSurfaceMaskTextureKind(textureKind))
        {
            return TextureProfileSafety.Experimental;
        }

        return profileId?.ToLowerInvariant() switch
        {
            "normal-2k-bc5-legacy" => TextureProfileSafety.Verified,
            "character-2k-bgra8" => TextureProfileSafety.Verified,
            "mask-2k-bgra8" => TextureProfileSafety.Verified,
            NativeMmrCookProfile => TextureProfileSafety.Verified,
            NativeUimdIconCookProfile => TextureProfileSafety.Verified,
            NativeCharacterIconCookProfile => TextureProfileSafety.Verified,
            NativeFaceDetailColorCookProfile => TextureProfileSafety.Verified,
            NativeFaceArtCookProfile => TextureProfileSafety.Verified,
            NativeFaceDetailNormalCookProfile => TextureProfileSafety.Verified,
            NativeFaceDetailFullColorCookProfile => TextureProfileSafety.Verified,
            NativeFaceDetailFullNormalCookProfile => TextureProfileSafety.Verified,
            NativeCtCookProfile => TextureProfileSafety.Verified,
            NativeRaoCookProfile => TextureProfileSafety.Verified,
            _ => TextureProfileSafety.Experimental,
        };
    }

    internal static bool TextureProfileIsVerifiedForRegression(string? profileId, string? textureKind = null) =>
        TextureProfileSafetyFor(profileId, textureKind) == TextureProfileSafety.Verified;

    private static string TextureProfileSafetyLabel(TextureProfileSafety safety) => safety == TextureProfileSafety.Verified
        ? "Verified"
        : "Experimental";

    private static string TextureProfileValidationNote(string? profileId, string? textureKind = null)
    {
        if (string.Equals(profileId, "mask-2k-bgra8", StringComparison.OrdinalIgnoreCase) &&
            IsSurfaceMaskTextureKind(textureKind))
        {
            return "Legacy AO-donor packed-map route. It does not carry the game's native linear MMR sampling metadata; use Native 2K DXT1 MMR instead.";
        }

        return profileId?.ToLowerInvariant() switch
        {
        "normal-2k-bc5-legacy" => "Verified on Electric's body normal map in game.",
        "character-2k-bgra8" => "Verified on Electric's base-colour maps in game.",
        "mask-2k-bgra8" => "Verified on Electric's current colour mask.",
        NativeMmrCookProfile => "Verified on Electric's MMR in game at all texture-quality settings. Native linear PF_DXT1 with a complete 2048px-to-1px mip chain. R is metalness and B is roughness; G is unused.",
        NativeUimdIconCookProfile => "Verified native UIMD icon layout: 256px BC7 with nine inline mips.",
        NativeCharacterIconCookProfile => "Verified native character-icon layout: 512px BC7 with ten inline mips.",
        NativeFaceDetailColorCookProfile => "Verified compact face-detail layout: 256×128 BC7 with two external mips and a complete inline tail.",
        NativeFaceArtCookProfile => "Verified linear animated face-art layout: 512px BC7 with three external mips, a complete inline tail, and preserved RGBA alpha stencil.",
        NativeFaceDetailNormalCookProfile => "Verified compact facial-normal layout: 128px BC5 with one external mip and a complete inline tail.",
        NativeFaceDetailFullColorCookProfile => "Verified full face-detail layout: 2048px BC7 with five external mips and a complete inline tail.",
        NativeFaceDetailFullNormalCookProfile => "Verified full face-normal layout: 512px BC5 with three external mips and a complete inline tail.",
        NativeCtCookProfile => "Verified linear PF_DXT1 CT layout: 512px with three external mips and a complete inline tail.",
        NativeRaoCookProfile => "Verified linear PF_DXT1 RAO layout: 1024px with four external mips and a complete inline tail.",
        "ui-2k-dxt5-legacy" => "Retired for UIMD icons: its world/decal layout corrupts suit-menu images.",
        "ui-2k-bgra8" => "Retired for UIMD icons: its external-mip world-texture layout corrupts suit-menu images.",
        "ui-1k-bgra8" => "Retired for UIMD icons: use the native 256px BC7 profile.",
        "character-1k-bgra8" => "Lower-resolution character colour; verify visual quality in game.",
        "mask-1k-bgra8" => "Lower-resolution profile; verify the target use in game.",
        "normal-2k-bgra8" => "Deprecated normal-map route. Choose BC5 instead.",
        "character-2k-dxt5-legacy" => "Legacy donor. Test the target texture role in game first.",
        "packed-2k-dxt5-legacy" => "Legacy donor. Test the target material response in game first.",
        "mask-1k-dxt1-legacy" => "Legacy colour-mask donor. Test the target material in game first.",
            _ => "This saved recipe is not recognized yet. Keep it unless you are testing a new profile.",
        };
    }

    private bool ConfirmTextureProfileSafety(TextureCookPreset preset, string action)
    {
        if (preset.Safety == TextureProfileSafety.Verified)
        {
            return true;
        }

        return Dialog.Confirm(this, "Experimental texture recipe",
            $"{preset.Label}\n{preset.Detail}\n\n{preset.ValidationNote}\n\nThis will {action} with an experimental donor family. Batcomputer will back up an existing cooked texture before replacing it.",
            confirmText: "Use experimental", severity: Dialog.Level.Warn);
    }

    private void ShowTextureRecipeSafety(GeneratedTextureEntry texture)
    {
        var safety = TextureProfileSafetyFor(texture.CookProfile, texture.Kind);
        Dialog.Show(this, new Dialog.Model
        {
            Title = "Texture recipe",
            Subtitle = texture.DisplayName,
            Severity = safety == TextureProfileSafety.Verified ? Dialog.Level.Good : Dialog.Level.Warn,
            Chips = new List<(string Text, Color? Dot)>
            {
                (TextureProfileSafetyLabel(safety), safety == TextureProfileSafety.Verified ? Theme.Good : Theme.Warn),
                (TextureCookDetail(texture), Theme.Textures),
            },
            Fields = new List<(string Label, string Value)>
            {
                ("Profile", string.IsNullOrWhiteSpace(texture.CookProfile) ? "legacy / unspecified" : texture.CookProfile),
                ("Template", texture.TemplateJson),
                ("Package", texture.PackagePath),
            },
            CalloutTitle = TextureProfileSafetyLabel(safety),
            CalloutDetail = TextureProfileValidationNote(texture.CookProfile, texture.Kind),
            PrimaryText = "Done",
        });
    }

    private static string TextureCookDetail(GeneratedTextureEntry texture)
    {
        if (texture.CookWidth > 0 && texture.CookHeight > 0 && !string.IsNullOrWhiteSpace(texture.CookPixelFormat))
        {
            return $"{texture.CookWidth}x{texture.CookHeight} {texture.CookPixelFormat}";
        }

        return TextureTemplateSizeDetail(texture.TemplateJson);
    }

    private static string TextureTemplateSizeDetail(string? templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson) || !File.Exists(templateJson))
        {
            return "unknown cook";
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateJson));
            var root = FindTexture2DJsonRoot(doc.RootElement);
            var width = root.TryGetProperty("SizeX", out var widthEl) && widthEl.TryGetInt32(out var widthValue) ? widthValue : 0;
            var height = root.TryGetProperty("SizeY", out var heightEl) && heightEl.TryGetInt32(out var heightValue) ? heightValue : 0;
            var pixelFormat = root.TryGetProperty("PixelFormat", out var formatEl) ? formatEl.GetString() ?? "unknown" : "unknown";
            return width > 0 && height > 0 ? $"{width}x{height} {pixelFormat}" : pixelFormat;
        }
        catch
        {
            return "unknown cook";
        }
    }

    private void ChangeGeneratedTextureCookProfile(GeneratedTextureEntry texture)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Changing the texture cook profile"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        var projectRoot = _projectRootText.Text.Trim();
        var profileKind = TextureKindForCookProfileChange(
            texture.Kind,
            texture.DisplayName,
            texture.SourcePng,
            texture.PackagePath);
        var preset = PromptForTextureCookPreset(profileKind, projectRoot, texture.CookProfile);
        if (preset is null)
        {
            return;
        }

        var desiredKind = preset.Id.Equals(NativeMmrCookProfile, StringComparison.OrdinalIgnoreCase)
            ? profileKind
            : texture.Kind;
        if (string.Equals(texture.TemplateJson, preset.TemplateJson, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(texture.CookProfile, preset.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(texture.Kind, desiredKind, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Texture '{texture.DisplayName}' already uses {preset.Label}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(texture.OutputRoot))
        {
            AppendLog($"Texture profile change skipped '{texture.DisplayName}': generated output folder is missing.");
            return;
        }

        if (!ConfirmTextureProfileSafety(preset, "recook this texture"))
        {
            AppendLog($"Texture '{texture.DisplayName}' kept its existing profile because the experimental change was not confirmed.");
            return;
        }

        var backupPath = CreateTextureBackup(texture, $"Before changing to {preset.Id}");
        if (!string.IsNullOrWhiteSpace(backupPath))
        {
            AppendLog(TextureBackupHasCoherentSourceSnapshot(backupPath)
                ? $"Coherent texture source/package backup created: {backupPath}"
                : $"Diagnostic package snapshot created (not source-restorable): {backupPath}");
        }

        var oldTemplate = texture.TemplateJson;
        var oldProfile = texture.CookProfile;
        var oldWidth = texture.CookWidth;
        var oldHeight = texture.CookHeight;
        var oldPixelFormat = texture.CookPixelFormat;
        var oldKind = texture.Kind;
        texture.TemplateJson = preset.TemplateJson;
        texture.CookProfile = preset.Id;
        texture.CookWidth = preset.Width;
        texture.CookHeight = preset.Height;
        texture.CookPixelFormat = preset.PixelFormat;
        var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
        if (!EnsureGeneratedTextureCooked(texture, cookedContentRoot))
        {
            texture.TemplateJson = oldTemplate;
            texture.CookProfile = oldProfile;
            texture.CookWidth = oldWidth;
            texture.CookHeight = oldHeight;
            texture.CookPixelFormat = oldPixelFormat;
            texture.Kind = oldKind;
            AppendLog($"Texture '{texture.DisplayName}' kept its previous cook profile because the recook failed.");
            return;
        }
        // Legacy MMR entries were often saved as Character texture. Reclassify
        // only after the native MMR recook succeeds so a failed attempt leaves
        // every persisted field on the old, still-usable recipe.
        texture.Kind = desiredKind;

        try
        {
            (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject);
            AppendLog($"Texture '{texture.DisplayName}' now uses {preset.Label} ({preset.Detail}).");
        }
        catch (Exception ex)
        {
            AppendLog($"Texture profile save warning: {ex.Message}");
        }

        RefreshToyboxTiles();
    }

    private string? CreateTextureBackup(GeneratedTextureEntry texture, string reason)
    {
        if (!TryResolveSafeGeneratedTexturePaths(
                texture,
                out _,
                out var sourceBase,
                out var pathError))
        {
            AppendLog("Texture backup blocked: " + pathError);
            return null;
        }

        var sourceFiles = new[] { ".uasset", ".uexp", ".ubulk", ".texture-cook-report.json" }
            .Select(extension => sourceBase + extension)
            .Where(File.Exists)
            .ToList();
        if (sourceFiles.Count == 0)
        {
            return null;
        }

        string? stagingRoot = null;
        try
        {
            var backupRoot = Path.Combine(texture.OutputRoot, "TextureBackups",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
            stagingRoot = backupRoot + ".creating";
            Directory.CreateDirectory(stagingRoot);
            var snapshot = new TextureBackupSnapshot
            {
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                Reason = reason,
                Texture = CloneGeneratedTextureEntry(texture),
            };
            var snapshotPath = Path.Combine(stagingRoot, "recipe-before.json");
            File.WriteAllText(snapshotPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            var manifest = new TextureBackupManifest();
            manifest.Members.Add(TextureBackupMemberFor(snapshotPath));
            foreach (var source in sourceFiles)
            {
                var name = Path.GetFileName(source);
                var destination = Path.Combine(stagingRoot, name);
                File.Copy(source, destination, overwrite: false);
                manifest.Members.Add(TextureBackupMemberFor(destination));
            }

            var reportPath = sourceBase + ".texture-cook-report.json";
            var ownedSourceRoot = Path.GetFullPath(Path.Combine(texture.OutputRoot, "Source"));
            var ownedSourcePath = Path.GetFullPath(texture.SourcePng ?? "");
            manifest.SourceMatchesCook =
                FileSystemPathUtil.IsWithinDirectory(ownedSourcePath, ownedSourceRoot, allowRoot: false) &&
                TextureCookReportSourceMatchesFile(reportPath, ownedSourcePath);
            string? sourceDestination = null;
            if (manifest.SourceMatchesCook)
            {
                manifest.SourceBackupName = "source-before" + Path.GetExtension(ownedSourcePath);
                sourceDestination = Path.Combine(stagingRoot, manifest.SourceBackupName);
                File.Copy(ownedSourcePath, sourceDestination, overwrite: false);
                // The source can be edited by an image editor while the copy is in flight.
                // Only publish a source-restorable backup when the copied bytes themselves
                // are exactly the bytes named by the backed cook report.
                if (!TextureCookReportSourceMatchesFile(reportPath, sourceDestination))
                {
                    throw new IOException("The source image changed while its texture backup was being created.");
                }
                manifest.Members.Add(TextureBackupMemberFor(sourceDestination));
            }

            var templateSnapshot = TrySnapshotTextureTemplate(
                texture.TemplateJson,
                stagingRoot,
                manifest);
            var stagedPackageBase = Path.Combine(stagingRoot, Path.GetFileName(sourceBase));
            var stagedReportPath = stagedPackageBase + ".texture-cook-report.json";
            var immutableValidationError = "the matching source image was unavailable";
            var immutableSnapshotValid =
                sourceDestination is not null &&
                TextureCookReportMatchesImmutableSnapshot(
                    stagedReportPath,
                    sourceDestination,
                    stagedPackageBase,
                    texture.PackagePath,
                    out immutableValidationError);
            if (immutableSnapshotValid &&
                !TextureCookReportMatchesSavedEntry(stagedReportPath, snapshot.Texture))
            {
                immutableSnapshotValid = false;
                immutableValidationError = "the copied cook report does not match the immutable saved recipe fields";
            }
            var templateSnapshotValid =
                immutableSnapshotValid &&
                !string.IsNullOrWhiteSpace(templateSnapshot) &&
                TextureCookReportTemplateMatchesTemplate(stagedReportPath, templateSnapshot);
            manifest.IsCoherentSnapshot = manifest.SourceMatchesCook && immutableSnapshotValid;
            manifest.ValidationMode = templateSnapshotValid
                ? "template-snapshot"
                : manifest.IsCoherentSnapshot
                    ? "cook-report-snapshot"
                    : "diagnostic-package-only";
            manifest.TemplateJsonBackupName = templateSnapshotValid
                ? Path.GetFileName(templateSnapshot!)
                : "";
            if (manifest.SourceMatchesCook && !manifest.IsCoherentSnapshot)
            {
                AppendLog(
                    $"Texture backup remains diagnostic because its copied cook report/package did not validate: {immutableValidationError}");
            }

            File.WriteAllText(
                Path.Combine(stagingRoot, "backup-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(stagingRoot, backupRoot);
            stagingRoot = null;

            return backupRoot;
        }
        catch (Exception ex)
        {
            AppendLog($"Texture backup warning: {ex.Message}");
            try
            {
                if (!string.IsNullOrWhiteSpace(stagingRoot) && Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch { /* best effort */ }
            return null;
        }
    }

    private static TextureBackupMember TextureBackupMemberFor(string path)
    {
        using var stream = File.OpenRead(path);
        return new TextureBackupMember
        {
            Name = Path.GetFileName(path),
            Bytes = stream.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)),
        };
    }

    private static string? TrySnapshotTextureTemplate(
        string? templateJson,
        string backupRoot,
        TextureBackupManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(templateJson) || !File.Exists(templateJson))
        {
            return null;
        }

        var templateBase = Path.Combine(
            Path.GetDirectoryName(templateJson) ?? "",
            Path.GetFileNameWithoutExtension(templateJson));
        var sources = new List<(string Source, string Extension)>
        {
            (templateJson, ".json"),
            (templateBase + ".uasset", ".uasset"),
            (templateBase + ".uexp", ".uexp"),
        };
        if (sources.Any(item => !File.Exists(item.Source)))
        {
            // Legacy UIMD recipes can point at a retired template. The immutable cook
            // report/package snapshot below remains sufficient to validate their backup.
            return null;
        }
        if (File.Exists(templateBase + ".ubulk"))
        {
            sources.Add((templateBase + ".ubulk", ".ubulk"));
        }

        var copied = new List<string>();
        try
        {
            foreach (var item in sources)
            {
                var destination = Path.Combine(backupRoot, "template-recipe" + item.Extension);
                copied.Add(destination);
                var expected = TextureBackupMemberFor(item.Source);
                File.Copy(item.Source, destination, overwrite: false);
                var actual = TextureBackupMemberFor(destination);
                if (expected.Bytes != actual.Bytes ||
                    !expected.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The texture donor template changed while its backup was being created.");
                }
                manifest.Members.Add(actual);
            }
            return Path.Combine(backupRoot, "template-recipe.json");
        }
        catch
        {
            foreach (var path in copied)
            {
                manifest.Members.RemoveAll(member =>
                    member.Name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
                try { File.Delete(path); } catch { /* diagnostic report-only fallback */ }
            }
            return null;
        }
    }

    internal static bool TextureCookReportMatchesImmutableSnapshot(
        string reportPath,
        string sourceImagePath,
        string packageBase,
        string? expectedPackagePath,
        out string error)
    {
        error = "";
        if (!File.Exists(reportPath) || !File.Exists(sourceImagePath))
        {
            error = "the copied cook report or source image is missing";
            return false;
        }

        try
        {
            using var reportDoc = JsonDocument.Parse(File.ReadAllText(reportPath));
            var root = reportDoc.RootElement;
            var status = root.TryGetProperty("Status", out var statusElement)
                ? statusElement.GetString() ?? ""
                : "";
            var outputPackage = root.TryGetProperty("OutputPackagePath", out var packageElement)
                ? UnrealPathUtil.NormalizePackagePath(packageElement.GetString())
                : "";
            if (!status.Equals("created", StringComparison.OrdinalIgnoreCase) ||
                !outputPackage.Equals(
                    UnrealPathUtil.NormalizePackagePath(expectedPackagePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "the copied cook report does not identify the saved texture package";
                return false;
            }
            if (!TextureCookReportSourceMatchesFile(reportPath, sourceImagePath))
            {
                error = "the copied source bytes do not match the copied cook report";
                return false;
            }

            foreach (var output in new[]
                     {
                         (Suffix: "Uasset", Extension: ".uasset", Required: true),
                         (Suffix: "Uexp", Extension: ".uexp", Required: false),
                         (Suffix: "Ubulk", Extension: ".ubulk", Required: false),
                     })
            {
                long expectedBytes = 0;
                var hasBytes = root.TryGetProperty("Output" + output.Suffix + "Bytes", out var bytesElement) &&
                               bytesElement.TryGetInt64(out expectedBytes) && expectedBytes > 0;
                var expectedHash = root.TryGetProperty("Output" + output.Suffix + "Sha256", out var hashElement)
                    ? hashElement.GetString() ?? ""
                    : "";
                var reportHasMember = hasBytes && expectedHash.Length == 64;
                var memberPath = packageBase + output.Extension;
                var fileExists = File.Exists(memberPath);
                if ((output.Required && !reportHasMember) || reportHasMember != fileExists)
                {
                    error = $"the copied cook report/package disagrees about {output.Extension}";
                    return false;
                }
                if (!reportHasMember)
                {
                    continue;
                }

                using var stream = File.OpenRead(memberPath);
                if (stream.Length != expectedBytes ||
                    !Convert.ToHexString(SHA256.HashData(stream)).Equals(
                        expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = $"the copied {output.Extension} does not match its cook-report hash";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool GeneratedTextureHasAnyCookedOutput(GeneratedTextureEntry texture)
    {
        if (!TryResolveSafeGeneratedTexturePaths(texture, out _, out var packageBase, out _))
        {
            return false;
        }
        return new[] { ".uasset", ".uexp", ".ubulk", ".texture-cook-report.json" }
            .Any(extension => File.Exists(packageBase + extension));
    }

    private static bool TextureBackupHasCoherentSourceSnapshot(string? backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) ||
            !TryReadCompleteTextureBackup(backupPath, out var manifest, out _, out _))
        {
            return false;
        }
        return manifest?.IsCoherentSnapshot == true;
    }

    private static string? TextureBackupOwnedTemplateJson(string? backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) ||
            !TryReadCompleteTextureBackup(backupPath, out var manifest, out _, out _) ||
            manifest?.IsCoherentSnapshot != true ||
            !manifest.ValidationMode.Equals("template-snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.TemplateJsonBackupName))
        {
            return null;
        }

        var backupRoot = Path.GetFullPath(backupPath);
        var templatePath = Path.GetFullPath(Path.Combine(backupRoot, manifest.TemplateJsonBackupName));
        return FileSystemPathUtil.IsWithinDirectory(templatePath, backupRoot, allowRoot: false) &&
               File.Exists(templatePath)
            ? templatePath
            : null;
    }

    internal static bool TextureBackupSourceIsUnchangedOrMissing(
        string sourceDestination,
        long snapshotBytes,
        string snapshotSha256)
    {
        if (!File.Exists(sourceDestination))
        {
            return true;
        }
        if (snapshotBytes <= 0 || snapshotSha256.Length != 64)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(sourceDestination);
            return stream.Length == snapshotBytes &&
                   Convert.ToHexString(SHA256.HashData(stream))
                       .Equals(snapshotSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private TexturePackageRollbackDisposition RestoreTexturePackageFiles(
        GeneratedTextureEntry texture,
        string? backupPath,
        bool hadPriorOutput)
    {
        if (!TryResolveSafeGeneratedTexturePaths(
                texture,
                out _,
                out var destinationBase,
                out var pathError))
        {
            throw new InvalidOperationException(
                $"Texture '{texture.DisplayName}' has no safe recoverable output path: {pathError}.");
        }
        if (hadPriorOutput && string.IsNullOrWhiteSpace(backupPath))
        {
            throw new InvalidOperationException($"Texture '{texture.DisplayName}' had prior output but no backup was created.");
        }

        TextureBackupManifest? manifest = null;
        TextureBackupSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(backupPath) &&
            !TryReadCompleteTextureBackup(backupPath, out manifest, out snapshot, out var backupError))
        {
            throw new InvalidOperationException("Texture backup is incomplete or damaged: " + backupError);
        }

        string? sourceBackup = null;
        string? sourceDestination = null;
        string? restoredTemplateJson = null;
        var backupCanRestoreSource = manifest?.IsCoherentSnapshot == true;
        if (backupCanRestoreSource &&
            snapshot?.Texture is not null &&
            !string.IsNullOrWhiteSpace(manifest!.SourceBackupName))
        {
            sourceBackup = Path.Combine(backupPath!, manifest.SourceBackupName);
            var currentOutputRoot = Path.GetFullPath(texture.OutputRoot);
            var snapshotOutputRoot = Path.GetFullPath(snapshot.Texture.OutputRoot);
            if (!snapshotOutputRoot.Equals(currentOutputRoot, StringComparison.OrdinalIgnoreCase) ||
                !UnrealPathUtil.NormalizePackagePath(snapshot.Texture.PackagePath).Equals(
                    UnrealPathUtil.NormalizePackagePath(texture.PackagePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Texture backup belongs to a different output folder or package recipe.");
            }

            var sourceRoot = Path.GetFullPath(Path.Combine(currentOutputRoot, "Source"));
            sourceDestination = Path.GetFullPath(snapshot.Texture.SourcePng);
            if (!FileSystemPathUtil.IsWithinDirectory(sourceDestination, sourceRoot, allowRoot: false))
            {
                throw new InvalidOperationException("Texture backup source path escapes the saved Source folder.");
            }

            var sourceMember = manifest.Members.Single(member =>
                member.Name.Equals(manifest.SourceBackupName, StringComparison.OrdinalIgnoreCase));
            backupCanRestoreSource = TextureBackupSourceIsUnchangedOrMissing(
                sourceDestination,
                sourceMember.Bytes,
                sourceMember.Sha256);
            if (manifest.ValidationMode.Equals("template-snapshot", StringComparison.OrdinalIgnoreCase))
            {
                restoredTemplateJson = Path.Combine(backupPath!, manifest.TemplateJsonBackupName);
            }
        }

        var currentCookVerified = ValidateGeneratedTextureCook(
            texture,
            destinationBase,
            out _);
        var disposition = TexturePackageRollbackDispositionFor(
            backupCanRestoreSource,
            currentCookVerified);
        if (disposition != TexturePackageRollbackDisposition.RestoredCoherentSnapshot)
        {
            // A package-only snapshot cannot recreate a source/package pair. Copying it over
            // either a verified new cook or a failed/pending cook would manufacture a stale
            // output that the saved source can never validate. Leave the current state intact;
            // validation/staging will keep a pending entry blocked until it is recooked.
            return disposition;
        }

        var membersByName = manifest?.Members.ToDictionary(
            member => member.Name,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, TextureBackupMember>(StringComparer.OrdinalIgnoreCase);
        var expectedStem = Path.GetFileName(destinationBase);
        var packageMembers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk", ".texture-cook-report.json" })
        {
            var name = expectedStem + extension;
            if (membersByName.ContainsKey(name))
            {
                packageMembers[extension] = Path.Combine(backupPath!, name);
            }
        }
        if (hadPriorOutput && packageMembers.Count == 0)
        {
            throw new InvalidOperationException("Texture backup contains no cooked package members for this texture.");
        }

        if (manifest?.IsCoherentSnapshot == true &&
            snapshot?.Texture is not null &&
            !string.IsNullOrWhiteSpace(manifest.SourceBackupName))
        {
            if (!TextureCookReportMatchesImmutableSnapshot(
                    Path.Combine(backupPath!, expectedStem) + ".texture-cook-report.json",
                    sourceBackup!,
                    Path.Combine(backupPath!, expectedStem),
                    snapshot.Texture.PackagePath,
                    out var validationError))
            {
                throw new InvalidOperationException(
                    "Texture backup does not contain a coherent source/package snapshot: " + validationError);
            }
        }

        var restoreTargets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk", ".texture-cook-report.json" })
        {
            var destination = destinationBase + extension;
            restoreTargets[destination] = packageMembers.TryGetValue(extension, out var source)
                ? source
                : null;
        }
        if (!string.IsNullOrWhiteSpace(sourceBackup) &&
            !string.IsNullOrWhiteSpace(sourceDestination) &&
            !File.Exists(sourceDestination))
        {
            restoreTargets[sourceDestination] = sourceBackup;
        }

        RestoreTextureFilesTransactionally(restoreTargets);
        if (!string.IsNullOrWhiteSpace(restoredTemplateJson))
        {
            // Keep restored recipes independent of a donor template that may later be
            // refreshed, replaced, or removed. This path is itself part of the completed
            // backup and was integrity-checked before the live package swap.
            texture.TemplateJson = restoredTemplateJson;
        }
        return TexturePackageRollbackDisposition.RestoredCoherentSnapshot;
    }

    private static void RestoreTextureFilesTransactionally(
        IReadOnlyDictionary<string, string?> restoreTargets)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var prepared = new List<(string Destination, string? Staged, string? Rollback, bool Existed)>();

        try
        {
            // Prepare every replacement and every rollback copy before touching a live file.
            foreach (var target in restoreTargets)
            {
                var destination = Path.GetFullPath(target.Key);
                var directory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidOperationException("Texture restore target has no parent folder.");
                Directory.CreateDirectory(directory);

                string? staged = null;
                string? rollback = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(target.Value))
                    {
                        var source = Path.GetFullPath(target.Value);
                        if (!File.Exists(source))
                        {
                            throw new FileNotFoundException("Texture restore source is missing.", source);
                        }

                        staged = destination + $".restore-{transactionId}.new";
                        var expected = TextureBackupMemberFor(source);
                        File.Copy(source, staged, overwrite: false);
                        var actual = TextureBackupMemberFor(staged);
                        if (expected.Bytes != actual.Bytes ||
                            !expected.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException($"Texture restore staging changed while copying '{Path.GetFileName(destination)}'.");
                        }
                    }

                    var existed = File.Exists(destination);
                    if (existed)
                    {
                        rollback = destination + $".restore-{transactionId}.old";
                        File.Copy(destination, rollback, overwrite: false);
                    }
                    prepared.Add((destination, staged, rollback, existed));
                }
                catch
                {
                    foreach (var temporary in new[] { staged, rollback })
                    {
                        if (!string.IsNullOrWhiteSpace(temporary))
                        {
                            try { File.Delete(temporary); } catch { /* best effort */ }
                        }
                    }
                    throw;
                }
            }
        }
        catch
        {
            CleanupTextureRestoreFiles(prepared);
            throw;
        }

        var applied = new List<(string Destination, string? Staged, string? Rollback, bool Existed)>();
        try
        {
            foreach (var item in prepared)
            {
                if (item.Staged is null)
                {
                    if (File.Exists(item.Destination))
                    {
                        File.Delete(item.Destination);
                    }
                }
                else
                {
                    File.Move(item.Staged, item.Destination, overwrite: true);
                }
                applied.Add(item);
            }
        }
        catch (Exception applyError)
        {
            var rollbackErrors = new List<Exception>();
            // Restore each member whose atomic swap completed before the failure. This
            // converts a multi-file package restore into an all-or-old best-effort outcome.
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.Existed && !string.IsNullOrWhiteSpace(item.Rollback) && File.Exists(item.Rollback))
                    {
                        File.Move(item.Rollback, item.Destination, overwrite: true);
                    }
                    else if (!item.Existed && File.Exists(item.Destination))
                    {
                        File.Delete(item.Destination);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }
            CleanupTextureRestoreFiles(prepared);

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Texture restore failed and its live-file rollback was incomplete.",
                    new[] { applyError }.Concat(rollbackErrors));
            }
            throw;
        }

        CleanupTextureRestoreFiles(prepared);
    }

    private static void CleanupTextureRestoreFiles(
        IEnumerable<(string Destination, string? Staged, string? Rollback, bool Existed)> prepared)
    {
        foreach (var item in prepared)
        {
            foreach (var temporary in new[] { item.Staged, item.Rollback })
            {
                if (string.IsNullOrWhiteSpace(temporary))
                {
                    continue;
                }
                try { File.Delete(temporary); } catch { /* best effort; never mask the real result */ }
            }
        }
    }

    private static string? FindLatestTextureBackup(GeneratedTextureEntry texture)
    {
        if (string.IsNullOrWhiteSpace(texture.OutputRoot))
        {
            return null;
        }

        var root = Path.Combine(texture.OutputRoot, "TextureBackups");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root)
            .Where(path =>
                TryReadCompleteTextureBackup(path, out var manifest, out _, out _) &&
                manifest?.IsCoherentSnapshot == true)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool TryReadCompleteTextureBackup(
        string backupPath,
        out TextureBackupManifest? manifest,
        out TextureBackupSnapshot? snapshot,
        out string error)
    {
        manifest = null;
        snapshot = null;
        error = "";
        try
        {
            var manifestPath = Path.Combine(backupPath, "backup-manifest.json");
            var snapshotPath = Path.Combine(backupPath, "recipe-before.json");
            if (!File.Exists(manifestPath) || !File.Exists(snapshotPath))
            {
                error = "completion manifest or recipe snapshot is missing";
                return false;
            }

            manifest = JsonSerializer.Deserialize<TextureBackupManifest>(File.ReadAllText(manifestPath));
            snapshot = JsonSerializer.Deserialize<TextureBackupSnapshot>(File.ReadAllText(snapshotPath));
            if (manifest?.SchemaVersion != 2 || snapshot?.Texture is null || manifest.Members.Count == 0)
            {
                error = "manifest schema, recipe, or member list is invalid";
                return false;
            }
            if (manifest.Members.GroupBy(member => member.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            {
                error = "manifest contains duplicate member names";
                return false;
            }
            var manifestMemberNames = manifest.Members
                .Select(member => member.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!manifestMemberNames.Contains("recipe-before.json"))
            {
                error = "immutable recipe snapshot is not covered by the completion manifest";
                return false;
            }

            foreach (var member in manifest.Members)
            {
                if (string.IsNullOrWhiteSpace(member.Name) ||
                    !Path.GetFileName(member.Name).Equals(member.Name, StringComparison.Ordinal) ||
                    member.Bytes <= 0 || member.Sha256.Length != 64)
                {
                    error = "manifest contains an invalid member";
                    return false;
                }
                var memberPath = Path.Combine(backupPath, member.Name);
                if (!File.Exists(memberPath))
                {
                    error = $"backup member is missing: {member.Name}";
                    return false;
                }
                using var stream = File.OpenRead(memberPath);
                if (stream.Length != member.Bytes ||
                    !Convert.ToHexString(SHA256.HashData(stream)).Equals(member.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"backup member failed its integrity check: {member.Name}";
                    return false;
                }
            }

            var sourceBackupName = manifest.SourceBackupName;
            if (manifest.IsCoherentSnapshot &&
                (!manifest.SourceMatchesCook ||
                 !manifest.ValidationMode.Equals("template-snapshot", StringComparison.OrdinalIgnoreCase) &&
                 !manifest.ValidationMode.Equals("cook-report-snapshot", StringComparison.OrdinalIgnoreCase)))
            {
                error = "coherent snapshot has no immutable validation mode or matching source";
                return false;
            }
            if (manifest.SourceMatchesCook &&
                (string.IsNullOrWhiteSpace(sourceBackupName) ||
                 !manifest.Members.Any(member => member.Name.Equals(
                     sourceBackupName,
                     StringComparison.OrdinalIgnoreCase))))
            {
                error = "source-restorable backup does not contain its source image";
                return false;
            }
            if (manifest.IsCoherentSnapshot)
            {
                var packageStem = UnrealPathUtil.AssetName(snapshot.Texture.PackagePath);
                var packageBase = Path.Combine(backupPath, packageStem);
                var sourcePath = Path.Combine(backupPath, sourceBackupName);
                foreach (var requiredName in new[]
                         {
                             packageStem + ".uasset",
                             packageStem + ".texture-cook-report.json",
                         })
                {
                    if (!manifestMemberNames.Contains(requiredName))
                    {
                        error = $"immutable package member is not covered by the completion manifest: {requiredName}";
                        return false;
                    }
                }
                foreach (var optionalExtension in new[] { ".uexp", ".ubulk" })
                {
                    var optionalName = packageStem + optionalExtension;
                    if (File.Exists(Path.Combine(backupPath, optionalName)) &&
                        !manifestMemberNames.Contains(optionalName))
                    {
                        error = $"immutable package member is not covered by the completion manifest: {optionalName}";
                        return false;
                    }
                }
                if (!TextureCookReportMatchesImmutableSnapshot(
                        packageBase + ".texture-cook-report.json",
                        sourcePath,
                        packageBase,
                        snapshot.Texture.PackagePath,
                        out var immutableError))
                {
                    error = "immutable backup validation failed: " + immutableError;
                    return false;
                }
                if (!TextureCookReportMatchesSavedEntry(
                        packageBase + ".texture-cook-report.json",
                        snapshot.Texture))
                {
                    error = "immutable cook report does not match the saved recipe fields";
                    return false;
                }

                if (manifest.ValidationMode.Equals("template-snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    var templateName = manifest.TemplateJsonBackupName;
                    if (string.IsNullOrWhiteSpace(templateName) ||
                        !Path.GetFileName(templateName).Equals(templateName, StringComparison.Ordinal) ||
                        !templateName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "template-backed snapshot does not contain its immutable template JSON";
                        return false;
                    }
                    var templateStem = Path.GetFileNameWithoutExtension(templateName);
                    foreach (var requiredTemplateName in new[]
                             {
                                 templateStem + ".json",
                                 templateStem + ".uasset",
                                 templateStem + ".uexp",
                             })
                    {
                        if (!manifestMemberNames.Contains(requiredTemplateName))
                        {
                            error =
                                $"immutable template member is not covered by the completion manifest: {requiredTemplateName}";
                            return false;
                        }
                    }
                    var optionalTemplateBulk = templateStem + ".ubulk";
                    if (File.Exists(Path.Combine(backupPath, optionalTemplateBulk)) &&
                        !manifestMemberNames.Contains(optionalTemplateBulk))
                    {
                        error =
                            $"immutable template member is not covered by the completion manifest: {optionalTemplateBulk}";
                        return false;
                    }
                    var templatePath = Path.Combine(backupPath, templateName);
                    if (!TextureCookReportTemplateMatchesTemplate(
                            packageBase + ".texture-cook-report.json",
                            templatePath))
                    {
                        error = "immutable template snapshot no longer matches the copied cook report";
                        return false;
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            manifest = null;
            snapshot = null;
            return false;
        }
    }

    private void RestoreLatestTextureBackup(GeneratedTextureEntry texture)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Restoring the texture backup"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        var backupPath = FindLatestTextureBackup(texture);
        if (backupPath is null)
        {
            AppendLog($"No texture backup is available for '{texture.DisplayName}'.");
            return;
        }

        if (!Dialog.Confirm(this, "Restore texture backup",
                $"Restore the last cooked output and recipe for '{texture.DisplayName}'?\n\n{backupPath}",
                confirmText: "Restore", severity: Dialog.Level.Warn))
        {
            return;
        }

        try
        {
            var snapshotPath = Path.Combine(backupPath, "recipe-before.json");
            var snapshot = JsonSerializer.Deserialize<TextureBackupSnapshot>(File.ReadAllText(snapshotPath));
            if (snapshot?.Texture is null || string.IsNullOrWhiteSpace(texture.OutputRoot) || string.IsNullOrWhiteSpace(texture.PackagePath))
            {
                AppendLog($"Texture backup '{backupPath}' is incomplete.");
                return;
            }

            var disposition = RestoreTexturePackageFiles(texture, backupPath, hadPriorOutput: true);
            if (disposition != TexturePackageRollbackDisposition.RestoredCoherentSnapshot)
            {
                throw new InvalidOperationException(
                    "This backup no longer contains a coherent source/package snapshot and was not restored.");
            }

            RestoreTextureRecipe(
                texture,
                snapshot.Texture,
                TextureBackupOwnedTemplateJson(backupPath));
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
            RecordChange("Textures", texture.DisplayName, texture.PackagePath, status: "restored");
            AppendLog($"Restored texture '{texture.DisplayName}' from backup: {backupPath}");
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            AppendLog($"Texture backup restore failed: {ex.Message}");
        }
    }

    private static void RestoreTextureRecipe(
        GeneratedTextureEntry target,
        GeneratedTextureEntry source,
        string? verifiedBackupTemplateJson = null)
    {
        target.DisplayName = source.DisplayName;
        target.Kind = source.Kind;
        target.CookProfile = source.CookProfile;
        target.CookWidth = source.CookWidth;
        target.CookHeight = source.CookHeight;
        target.CookPixelFormat = source.CookPixelFormat;
        target.SourcePng = source.SourcePng;
        target.PackagePath = source.PackagePath;
        target.ObjectPath = source.ObjectPath;
        target.TemplateJson = !string.IsNullOrWhiteSpace(verifiedBackupTemplateJson) &&
                              File.Exists(verifiedBackupTemplateJson)
            ? verifiedBackupTemplateJson
            : source.TemplateJson;
        target.SourceRawRoot = source.SourceRawRoot;
        target.OutputRoot = source.OutputRoot;
        target.IoStoreRoot = source.IoStoreRoot;
        target.PackageBaseName = source.PackageBaseName;
        target.CreatedUtc = source.CreatedUtc;
    }

    private static string TextureTemplatePixelFormat(string templateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateJson));
            var root = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault(e =>
                    e.TryGetProperty("Type", out var type) &&
                    type.GetString()?.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) == true)
                : doc.RootElement;
            if (root.ValueKind == JsonValueKind.Undefined && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                root = doc.RootElement.EnumerateArray().First();
            }
            return root.TryGetProperty("PixelFormat", out var format)
                ? format.GetString() ?? "unknown"
                : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string DefaultTextureSourceRawRoot(string projectRoot)
    {
        foreach (var path in new[]
        {
            Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureTemplateRawProbe_clean"),
            Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureTemplateRawProbe")
        })
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "BatcomputerRawTextureProbe");
    }

    internal static string GuessTextureImportKind(string suggestedName)
    {
        var name = suggestedName ?? "";
        if (name.EndsWith("_RAO", StringComparison.OrdinalIgnoreCase))
        {
            return "RAO map";
        }
        if (name.EndsWith("_CT", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_CTUV", StringComparison.OrdinalIgnoreCase))
        {
            return "CT map";
        }
        if (name.Contains("normal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("_nrm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("_dnrm", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("_N_", StringComparison.Ordinal))
        {
            return "Normal map";
        }

        var compactName = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (compactName.Contains("colormask", StringComparison.OrdinalIgnoreCase) ||
            compactName.Contains("colourmask", StringComparison.OrdinalIgnoreCase))
        {
            return "Color mask";
        }

        // Strong texture-channel conventions win over broad UI words. Character textures often
        // contain body-region names such as Left/Right/Front, so T_RightArm_MMR must not become a
        // character portrait just because it contains "right".
        if (HasMmrNameSuffix(name) ||
            HasDelimitedOrmNameSuffix(name) ||
            name.Contains("rough", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("metal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("spec", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mask", StringComparison.OrdinalIgnoreCase))
        {
            return "Roughness/spec mask";
        }

        // _BC is the game convention for a base-colour texture. The current authoring label is
        // "Character texture", so this is intentional rather than a duplicate profile choice.
        if (HasBaseColorNameSuffix(name))
        {
            return "Character texture";
        }

        if (name.Contains("front", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("left", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("right", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("menu", StringComparison.OrdinalIgnoreCase))
        {
            return "Character icon";
        }

        if (name.Contains("suiticon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("suit_icon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("selector", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("icon", StringComparison.OrdinalIgnoreCase))
        {
            return "Suit selector icon";
        }

        if (name.Contains("ui", StringComparison.OrdinalIgnoreCase))
        {
            return "UI artwork";
        }

        return "Character texture";
    }

    internal static string TextureKindForCookProfileChange(
        string? currentKind,
        params string?[] identityCandidates)
    {
        foreach (var candidate in identityCandidates)
        {
            var assetName = Path.GetFileNameWithoutExtension((candidate ?? "").Trim());
            if (HasMmrNameSuffix(assetName) || HasDelimitedOrmNameSuffix(assetName))
            {
                return "Roughness/spec mask";
            }
        }

        return string.IsNullOrWhiteSpace(currentKind) ? "Texture" : currentKind;
    }

    private static bool HasMmrNameSuffix(string? name) =>
        (name ?? "").Trim().EndsWith("MMR", StringComparison.OrdinalIgnoreCase);

    private static bool HasDelimitedOrmNameSuffix(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Equals("ORM", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!value.EndsWith("ORM", StringComparison.OrdinalIgnoreCase) || value.Length <= 3)
        {
            return false;
        }

        return !char.IsLetterOrDigit(value[^4]);
    }

    private static bool HasBaseColorNameSuffix(string? name)
    {
        var value = (name ?? "").Trim();
        return value.EndsWith("_BC", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith("_BASECOLOR", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith("_BASECOLOUR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUiTextureKind(string? textureKind) =>
        !string.IsNullOrWhiteSpace(textureKind) &&
        (textureKind.Contains("ui", StringComparison.OrdinalIgnoreCase) ||
         textureKind.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
         textureKind.Contains("artwork", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuitSelectorIconTextureKind(string? textureKind) =>
        !string.IsNullOrWhiteSpace(textureKind) &&
        (IsExplicitSuitSelectorIconTextureKind(textureKind) ||
         textureKind.Equals("UI icon", StringComparison.OrdinalIgnoreCase));

    private static bool IsExplicitSuitSelectorIconTextureKind(string? textureKind) =>
        !string.IsNullOrWhiteSpace(textureKind) &&
        (textureKind.Equals("Suit selector icon", StringComparison.OrdinalIgnoreCase) ||
         textureKind.Contains("suit selector", StringComparison.OrdinalIgnoreCase));

    private static bool IsCharacterIconTextureKind(string? textureKind) =>
        !string.IsNullOrWhiteSpace(textureKind) &&
        (textureKind.Equals("Character icon", StringComparison.OrdinalIgnoreCase) ||
         textureKind.Contains("character portrait", StringComparison.OrdinalIgnoreCase));

    private static bool IsNativeUimdIconCookProfile(string? cookProfile) =>
        string.Equals(cookProfile, NativeUimdIconCookProfile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(cookProfile, NativeCharacterIconCookProfile, StringComparison.OrdinalIgnoreCase);

    private static bool UseNearestNeighborMipsForTextureKind(string? textureKind, string? cookProfile = null) => false;

    private static IReadOnlyList<UimdIconRecipeRequirement> UimdIconRecipeRequirements(
        NativeSuitProject project) =>
    [
        new("menu", project.IconMenu, "Character icon", NativeCharacterIconCookProfile, TextureCookTemplateService.NativeCharacterIconTemplateFolder, 512),
        new("suit selector", project.IconSuit, "Suit selector icon", NativeUimdIconCookProfile, TextureCookTemplateService.NativeSuitIconTemplateFolder, 256),
        new("left", project.IconLeft, "Character icon", NativeCharacterIconCookProfile, TextureCookTemplateService.NativeCharacterIconTemplateFolder, 512),
        new("right", project.IconRight, "Character icon", NativeCharacterIconCookProfile, TextureCookTemplateService.NativeCharacterIconTemplateFolder, 512),
    ];

    private static bool TryResolveGeneratedUimdIconRecipe(
        NativeSuitProject project,
        GeneratedTextureEntry texture,
        out UimdIconRecipeRequirement? requirement,
        out string error)
    {
        requirement = null;
        error = "";
        var matches = UimdIconRecipeRequirements(project)
            .Where(candidate => ReferenceEquals(
                FindGeneratedTextureByPackage(project, candidate.Path),
                texture))
            .ToList();
        if (matches.Count == 0)
        {
            return true;
        }

        var distinctProfiles = matches
            .Select(candidate => candidate.CookProfile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctProfiles.Count > 1)
        {
            error =
                $"'{texture.DisplayName}' is assigned to both the 256px suit selector and a 512px character portrait. " +
                "Import separate icon textures for those roles before reimporting.";
            return false;
        }

        requirement = matches[0];
        return true;
    }

    internal static UimdIconRecipeRequirement? GeneratedUimdIconRecipeRequirementForTest(
        NativeSuitProject project,
        GeneratedTextureEntry texture) =>
        TryResolveGeneratedUimdIconRecipe(project, texture, out var requirement, out _)
            ? requirement
            : null;

    private bool TryNormalizeGeneratedUimdIconRecipeForReimport(
        GeneratedTextureEntry texture,
        UimdIconRecipeRequirement requirement,
        out string error)
    {
        error = "";
        var projectRoot = _projectRootText.Text.Trim();
        var templateReady = requirement.CookProfile.Equals(NativeUimdIconCookProfile, StringComparison.OrdinalIgnoreCase)
            ? TextureCookTemplateService.NormalizeNativeSuitIconTemplate(projectRoot)
            : TextureCookTemplateService.NormalizeNativeCharacterIconTemplate(projectRoot);
        if (!templateReady)
        {
            error =
                $"Batcomputer could not prepare the native {requirement.Size}px donor required by the {requirement.Role} icon slot. " +
                "Run Full refresh, then reimport this image again.";
            return false;
        }

        var templateJson = TextureCookTemplateService.TemplateJsonPath(projectRoot, requirement.TemplateFolder);
        if (!File.Exists(templateJson))
        {
            error =
                $"The native {requirement.Size}px donor recipe required by the {requirement.Role} icon slot is still missing after preparation. " +
                "Run Full refresh, then reimport this image again.";
            return false;
        }

        var changed =
            !string.Equals(texture.Kind, requirement.Kind, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(texture.CookProfile, requirement.CookProfile, StringComparison.OrdinalIgnoreCase) ||
            texture.CookWidth != requirement.Size ||
            texture.CookHeight != requirement.Size ||
            !string.Equals(texture.CookPixelFormat, "PF_BC7", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(texture.TemplateJson, templateJson, StringComparison.OrdinalIgnoreCase);

        texture.Kind = requirement.Kind;
        texture.CookProfile = requirement.CookProfile;
        texture.CookWidth = requirement.Size;
        texture.CookHeight = requirement.Size;
        texture.CookPixelFormat = "PF_BC7";
        texture.TemplateJson = templateJson;
        if (changed)
        {
            AppendLog(
                $"UIMD icon reimport normalized: {requirement.Role} '{texture.DisplayName}' -> native {requirement.Size}px BC7 inline-mip layout.");
        }
        return true;
    }

    private bool AutoAssignGeneratedUiIconSlots(NativeSuitProject project)
    {
        var uiTextures = project.GeneratedTextures
            .Where(t => IsUiTextureKind(t.Kind) && !string.IsNullOrWhiteSpace(t.PackagePath))
            .ToList();
        if (uiTextures.Count == 0)
        {
            return false;
        }

        var changed = false;
        var suit = uiTextures
            .Select(t => new { Texture = t, Score = GeneratedUiIconScore(t, "suit") })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Texture.CreatedUtc, StringComparer.Ordinal)
            .FirstOrDefault();
        if (suit is not null)
        {
            var current = FindGeneratedTextureByPackage(project, project.IconSuit);
            var currentScore = current is null ? 0 : GeneratedUiIconScore(current, "suit");
            var canReplace =
                string.IsNullOrWhiteSpace(project.IconSuit) ||
                (current is not null && suit.Score > currentScore);
            if (canReplace &&
                !string.Equals(project.IconSuit, suit.Texture.PackagePath, StringComparison.OrdinalIgnoreCase))
            {
                var before = string.IsNullOrWhiteSpace(project.IconSuit) ? "<empty>" : project.IconSuit;
                project.IconSuit = suit.Texture.PackagePath;
                AppendLog($"Auto icon slot: Suit icon {before} -> {project.IconSuit}");
                changed = true;
            }
        }

        changed |= AutoFillEmptyGeneratedUiIconSlot(project, "menu");
        changed |= AutoFillEmptyGeneratedUiIconSlot(project, "left");
        changed |= AutoFillEmptyGeneratedUiIconSlot(project, "right");
        changed |= NormalizeGeneratedUimdIconRecipes(project);
        return changed;
    }

    /// <summary>
    /// UIMD has two distinct native icon formats: the suit selector is 256px,
    /// while the menu/left/right character portraits are 512px. Normalize by
    /// UIMD role, not filename, so legacy projects are repaired safely.
    /// </summary>
    private bool NormalizeGeneratedUimdIconRecipes(NativeSuitProject project)
    {
        var slots = UimdIconRecipeRequirements(project);
        var referenced = slots
            .Select(slot => new { Slot = slot, Texture = FindGeneratedTextureByPackage(project, slot.Path) })
            .Where(item => item.Texture is not null)
            .ToList();
        if (referenced.Count == 0)
        {
            return false;
        }

        var conflicts = referenced
            .GroupBy(item => item.Texture!.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Slot.CookProfile).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();
        foreach (var conflict in conflicts)
        {
            AppendLog($"UIMD icon recipe needs separate files: '{conflict.Key}' is assigned to both the 256px suit tile and a 512px character portrait. Import separate icon textures before packaging.");
        }

        var conflictPaths = conflicts
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = referenced
            .Where(item => !conflictPaths.Contains(item.Texture!.PackagePath))
            .GroupBy(item => item.Texture!.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (targets.Count == 0)
        {
            return false;
        }

        var projectRoot = _projectRootText.Text.Trim();
        var needsSuit = targets.Any(item => item.Slot.CookProfile.Equals(NativeUimdIconCookProfile, StringComparison.OrdinalIgnoreCase));
        var needsCharacter = targets.Any(item => item.Slot.CookProfile.Equals(NativeCharacterIconCookProfile, StringComparison.OrdinalIgnoreCase));
        var suitReady = !needsSuit || TextureCookTemplateService.NormalizeNativeSuitIconTemplate(projectRoot);
        var characterReady = !needsCharacter || TextureCookTemplateService.NormalizeNativeCharacterIconTemplate(projectRoot);
        if (!suitReady || !characterReady)
        {
            var missing = !suitReady ? "256px suit-icon" : "512px character-icon";
            AppendLog($"UIMD icon migration blocked: the native {missing} donor is unavailable. Refresh game assets before packaging this suit.");
            return false;
        }

        var changed = false;
        foreach (var item in targets)
        {
            var texture = item.Texture!;
            var desiredKind = item.Slot.Kind;
            var nativeTemplate = TextureCookTemplateService.TemplateJsonPath(projectRoot, item.Slot.TemplateFolder);
            var recipeChanged =
                !string.Equals(texture.Kind, desiredKind, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(texture.CookProfile, item.Slot.CookProfile, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(texture.TemplateJson, nativeTemplate, StringComparison.OrdinalIgnoreCase) ||
                texture.CookWidth != item.Slot.Size ||
                texture.CookHeight != item.Slot.Size ||
                !string.Equals(texture.CookPixelFormat, "PF_BC7", StringComparison.OrdinalIgnoreCase);
            if (!recipeChanged)
            {
                continue;
            }

            texture.Kind = desiredKind;
            texture.CookProfile = item.Slot.CookProfile;
            texture.CookWidth = item.Slot.Size;
            texture.CookHeight = item.Slot.Size;
            texture.CookPixelFormat = "PF_BC7";
            texture.TemplateJson = nativeTemplate;
            AppendLog($"UIMD icon recipe normalized: {item.Slot.Role} '{texture.DisplayName}' -> native {item.Slot.Size}px BC7 inline-mip layout.");
            changed = true;
        }

        return changed;
    }

    private static string? UimdIconRoleConflictError(NativeSuitProject project)
    {
        var slots = new[]
        {
            new { Role = "menu", Path = project.IconMenu, Size = 512 },
            new { Role = "suit selector", Path = project.IconSuit, Size = 256 },
            new { Role = "left", Path = project.IconLeft, Size = 512 },
            new { Role = "right", Path = project.IconRight, Size = 512 },
        };
        var conflicting = slots
            .Where(slot => !string.IsNullOrWhiteSpace(slot.Path))
            .GroupBy(slot => slot.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Select(slot => slot.Size).Distinct().Count() > 1);
        if (conflicting is null)
        {
            return null;
        }

        return $"UIMD icon '{conflicting.Key}' is assigned to both the 256px suit selector and a 512px character portrait. " +
               "Import separate files for those roles before packaging.";
    }

    private bool AutoFillEmptyGeneratedUiIconSlot(NativeSuitProject project, string slot)
    {
        string current = slot switch
        {
            "menu" => project.IconMenu,
            "left" => project.IconLeft,
            "right" => project.IconRight,
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        var candidate = project.GeneratedTextures
            .Where(t => IsUiTextureKind(t.Kind) && !string.IsNullOrWhiteSpace(t.PackagePath))
            .Select(t => new { Texture = t, Score = GeneratedUiIconScore(t, slot) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Texture.CreatedUtc, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        switch (slot)
        {
            case "menu":
                project.IconMenu = candidate.Texture.PackagePath;
                break;
            case "left":
                project.IconLeft = candidate.Texture.PackagePath;
                break;
            case "right":
                project.IconRight = candidate.Texture.PackagePath;
                break;
        }

        AppendLog($"Auto icon slot: {slot} icon <empty> -> {candidate.Texture.PackagePath}");
        return true;
    }

    private static GeneratedTextureEntry? FindGeneratedTextureByPackage(NativeSuitProject project, string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return null;
        }

        return project.GeneratedTextures.FirstOrDefault(t =>
            t.PackagePath.Equals(packagePath, StringComparison.OrdinalIgnoreCase) ||
            t.ObjectPath.Equals(packagePath, StringComparison.OrdinalIgnoreCase));
    }

    private static int GeneratedUiIconScore(GeneratedTextureEntry texture, string slot)
    {
        var token = string.Join(" ",
            texture.DisplayName,
            Path.GetFileNameWithoutExtension(texture.SourcePng ?? ""),
            texture.PackagePath ?? "").ToLowerInvariant();

        // Legacy projects labelled all four UIMD images as "UI icon". Only the
        // explicit modern selector kind receives the role bonus; otherwise the
        // filename tokens keep ElectricSuit ahead of ElectricSuitFront/Left/Right.
        var nativeSuitTileBonus = slot == "suit" && IsExplicitSuitSelectorIconTextureKind(texture.Kind) ? 1000 : 0;
        return nativeSuitTileBonus + (slot switch
        {
            "suit" when token.Contains("suiticon") => 120,
            "suit" when token.Contains("icon") => 100,
            "suit" when token.Contains("suit") && !token.Contains("front") && !token.Contains("left") && !token.Contains("right") => 30,
            "menu" when token.Contains("menu") => 110,
            "menu" when token.Contains("front") => 90,
            "left" when token.Contains("left") => 100,
            "right" when token.Contains("right") => 100,
            _ => 0
        });
    }

    private string TexturePackagePathFromUserName(string templateJson, string requestedName, int index, string currentModFolder, string slotId, string? textureKind = null)
    {
        var assetName = MakeCleanTextureAssetName(requestedName);
        var mod = MakeSafeTextureToken(currentModFolder);
        if (string.IsNullOrWhiteSpace(mod) || mod.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            mod = MakeSafeTextureToken(slotId);
        }

        if (string.IsNullOrWhiteSpace(mod))
        {
            mod = "SuitTextures";
        }

        var cleanPackagePath = $"/Game/Mods/{mod}/Textures/{assetName}";
        // A standalone Texture2D package can be rewritten by UAssetAPI even when
        // its cooked mips are inline in a split .uexp. Only the older raw
        // IoStore-payload templates need the compact same-length identity patch.
        if (TextureTemplateNeedsSameLengthPath(templateJson, textureKind))
        {
            var safePath = BuildSameLengthTexturePackagePath(templateJson, requestedName, index, mod, slotId);
            AppendLog(
                $"Texture path note: '{textureKind}' uses an inline-only Texture2D template. " +
                $"Using same-length safe path '{safePath}' so FModel/game can read the cooked platform data.");
            return safePath;
        }

        if (TextureTemplateIsStandaloneUasset(templateJson))
        {
            return cleanPackagePath;
        }

        var (templatePackageLength, templateNameLength) = ReadTextureTemplateLengths(templateJson);
        if (TryBuildSameLengthTexturePackagePath(templateJson, requestedName, index, mod, slotId, out var fallbackPath))
        {
            AppendLog(
                $"Texture path note: template is an IoStore payload, not a standalone UAssetAPI package. " +
                $"Using compatibility path '{fallbackPath}' until a standalone Texture2D template is configured.");
            return fallbackPath;
        }

        var fixedLengthAssetName = MakeFixedLengthTextureAssetName(requestedName, index, templateNameLength);
        throw new InvalidOperationException(
            $"Texture template length mismatch. Could not fit generated asset '{fixedLengthAssetName}' into a safe fallback path " +
            $"with template package length {templatePackageLength}. Use a standalone Texture2D template or create a matching-length IoStore template.");
    }

    private static bool TextureTemplateNeedsSameLengthPath(string templateJson, string? textureKind) =>
        IsUiTextureKind(textureKind) &&
        TextureTemplateIsInlineOnly(templateJson) &&
        !TextureTemplateIsStandaloneUasset(templateJson);

    private static bool TextureTemplateIsInlineOnly(string templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson) || !File.Exists(templateJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateJson));
            var root = FindTexture2DJsonRoot(doc.RootElement);
            if (root.ValueKind == JsonValueKind.Undefined ||
                !root.TryGetProperty("Mips", out var mips) ||
                mips.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var count = 0;
            foreach (var mip in mips.EnumerateArray())
            {
                count++;
                if (!mip.TryGetProperty("BulkData", out var bulk) ||
                    !bulk.TryGetProperty("BulkDataFlags", out var flagsEl))
                {
                    return false;
                }

                var flags = flagsEl.GetString() ?? "";
                if (!flags.Contains("ForceInlinePayload", StringComparison.OrdinalIgnoreCase) ||
                    flags.Contains("PayloadInSeperateFile", StringComparison.OrdinalIgnoreCase) ||
                    flags.Contains("PayloadInSeparateFile", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement FindTexture2DJsonRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                if (element.TryGetProperty("Type", out var type) &&
                    type.GetString()?.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return element;
                }
            }

            return default;
        }

        return root;
    }

    private static bool TryBuildSameLengthTexturePackagePath(
        string templateJson,
        string requestedName,
        int index,
        string mod,
        string slotId,
        out string packagePath)
    {
        packagePath = "";
        var (templatePackageLength, templateNameLength) = ReadTextureTemplateLengths(templateJson);
        var fixedLengthAssetName = MakeFixedLengthTextureAssetName(requestedName, index, templateNameLength);
        var targetPrefixLength = templatePackageLength - fixedLengthAssetName.Length;
        if (targetPrefixLength <= "/Game/Mods//".Length)
        {
            return false;
        }

        var modsPrefix = "/Game/Mods/";
        var folderLength = targetPrefixLength - modsPrefix.Length - 1;
        if (folderLength < 1)
        {
            return false;
        }

        var folder = MakeFixedLengthModFolderName(
            string.IsNullOrWhiteSpace(mod) ? slotId : mod,
            index,
            folderLength);
        packagePath = $"{modsPrefix}{folder}/{fixedLengthAssetName}";
        return packagePath.Length == templatePackageLength &&
               UnrealPathUtil.AssetName(packagePath).Length == templateNameLength;
    }

    private static string BuildSameLengthTexturePackagePath(string templateJson, string requestedName, int index, string mod, string slotId)
    {
        if (TryBuildSameLengthTexturePackagePath(templateJson, requestedName, index, mod, slotId, out var packagePath))
        {
            return packagePath;
        }

        var (templatePackageLength, templateNameLength) = ReadTextureTemplateLengths(templateJson);
        throw new InvalidOperationException(
            $"Inline Texture2D template requires same-length identity patching, but no safe /Game/Mods path could fit " +
            $"template package length {templatePackageLength} and asset-name length {templateNameLength}.");
    }

    private static bool TextureTemplateIsStandaloneUasset(string templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson))
        {
            return false;
        }

        var templateBase = Path.Combine(
            Path.GetDirectoryName(templateJson) ?? "",
            Path.GetFileNameWithoutExtension(templateJson));
        var uasset = templateBase + ".uasset";
        if (!File.Exists(uasset))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(uasset);
            Span<byte> signature = stackalloc byte[4];
            if (fs.Read(signature) != 4)
            {
                return false;
            }

            return signature[0] == 0xC1 && signature[1] == 0x83 && signature[2] == 0x2A && signature[3] == 0x9E;
        }
        catch
        {
            return false;
        }
    }

    private static int NextTextureSlotIndex(NativeSuitProject project)
    {
        for (var i = 1; i <= 99999; i++)
        {
            var suffix = $"_{i:00000}_P";
            if (!project.GeneratedTextures.Any(t => t.PackageBaseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return project.GeneratedTextures.Count + 1;
    }

    private static string MakeFixedLengthTextureAssetName(string requestedName, int index, int targetLength)
    {
        var token = MakeSafeTextureToken(requestedName);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = "Texture";
        }

        const string prefix = "T_";
        var hash = LongHash($"{token}|{index}");
        var separatorLength = targetLength >= prefix.Length + token.Length + 1 + 4 ? 1 : 0;
        var minHashLength = Math.Min(4, Math.Max(1, targetLength - prefix.Length - separatorLength - 1));
        var coreLength = targetLength - prefix.Length - separatorLength - minHashLength;
        if (coreLength < 1)
        {
            throw new InvalidOperationException($"Texture template asset name is too short to make a unique generated name ({targetLength}).");
        }

        var core = token.Length > coreLength ? token[..coreLength] : token;
        var availableHashLength = targetLength - prefix.Length - core.Length - separatorLength;
        if (availableHashLength < minHashLength)
        {
            core = core[..Math.Max(1, core.Length - (minHashLength - availableHashLength))];
            availableHashLength = targetLength - prefix.Length - core.Length - separatorLength;
        }

        var separator = separatorLength == 1 ? "_" : "";
        return $"{prefix}{core}{separator}{hash[..availableHashLength]}";
    }

    private static string MakeCleanTextureAssetName(string requestedName)
    {
        var token = MakeSafeTextureToken(requestedName);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = "Texture";
        }

        if (!token.StartsWith("T_", StringComparison.OrdinalIgnoreCase))
        {
            token = "T_" + token;
        }

        const int maxAssetNameLength = 64;
        return token.Length <= maxAssetNameLength ? token : token[..maxAssetNameLength].TrimEnd('_');
    }

    private static string MakeSafeTextureToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        var lastWasUnderscore = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }

        var safe = sb.ToString().Trim('_');
        if (safe.Length > 0 && char.IsDigit(safe[0]))
        {
            safe = "Tex_" + safe;
        }

        return safe;
    }

    private void UseGeneratedTextureForSelectedMaterialGridRow()
    {
        EnsureProject();
        if (_currentProject is null || _currentProject.GeneratedTextures.Count == 0)
        {
            AppendLog("No generated textures are saved on this suit yet.");
            return;
        }

        var row = _matParamGrid.CurrentRow;
        if (row is null || row.IsNewRow)
        {
            AppendLog("Select a material parameter row first.");
            return;
        }

        var texture = PickGeneratedTextureFromCurrentProject();
        if (texture is null)
        {
            return;
        }

        row.Cells["YourTexture"].Value = texture.PackagePath;
        var param = row.Cells["Param"].Value?.ToString() ?? "parameter";
        AppendLog($"Material param '{param}' set to generated texture: {texture.PackagePath}");
    }

    private GeneratedTextureEntry? PickGeneratedTextureFromCurrentProject()
    {
        var textures = _currentProject?.GeneratedTextures
            .Where(t => !string.IsNullOrWhiteSpace(t.PackagePath))
            .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<GeneratedTextureEntry>();
        if (textures.Count == 0)
        {
            return null;
        }
        if (textures.Count == 1)
        {
            return textures[0];
        }

        using var form = new AdaptiveDialogForm
        {
            Text = "Pick generated texture",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(760, 420),
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(600, 340),
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark
        };
        form.Shown += (_, _) => Theme.UseDarkTitleBar(form);

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false
        };
        Theme.StyleListBox(list);
        foreach (var texture in textures)
        {
            list.Items.Add(new GeneratedTextureListItem(texture));
        }
        list.SelectedIndex = 0;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            BackColor = Theme.PanelBg
        };
        var ok = new Button { Text = "Use", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        Theme.StyleGoldButton(ok);
        Theme.StyleSmallDarkButton(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        form.Controls.Add(list);
        form.Controls.Add(buttons);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        return list.SelectedItem is GeneratedTextureListItem item ? item.Texture : null;
    }

    /// <summary>
    /// Stages a suit's saved texture recipes without changing them. In particular,
    /// a legacy texture with no CookProfile is allowed only when its complete
    /// previously cooked output exists; packaging must never pick a newer donor or
    /// rewrite that asset on the user's behalf.
    /// </summary>
    private bool StageGeneratedTexturesIntoContentRoot(
        NativeSuitProject project,
        string contentRootToPackage,
        out string error,
        bool persistProjectChanges = true)
    {
        error = "";

        var iconRoleConflict = UimdIconRoleConflictError(project);
        if (!string.IsNullOrWhiteSpace(iconRoleConflict))
        {
            error = iconRoleConflict;
            AppendLog("Texture stage blocked: " + iconRoleConflict);
            return false;
        }

        if (NormalizeGeneratedUimdIconRecipes(project) && persistProjectChanges)
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

        if (project.GeneratedTextures.Count == 0)
        {
            ClearDedicatedGeneratedTextureStage(
                project,
                contentRootToPackage,
                throwOnFailure: !persistProjectChanges);
            if (persistProjectChanges)
            {
                WriteGeneratedTextureStageManifest(project, new List<string>());
            }
            return true;
        }

        var stageErrors = new List<string>();
        foreach (var texture in project.GeneratedTextures)
        {
            if (!TryPrepareGeneratedTextureForStaging(texture, out var textureError))
            {
                stageErrors.Add(textureError);
            }
        }
        if (stageErrors.Count > 0)
        {
            error = string.Join("\n", stageErrors);
            foreach (var stageError in stageErrors)
            {
                AppendLog("Texture stage blocked: " + stageError);
            }
            return false;
        }

        ClearDedicatedGeneratedTextureStage(
            project,
            contentRootToPackage,
            throwOnFailure: !persistProjectChanges);
        var copied = 0;
        var stagedRelativeFiles = new List<string>();
        foreach (var texture in project.GeneratedTextures)
        {
            var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
            var sourceBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
            var destBase = PackagePathToContentPath(contentRootToPackage, texture.PackagePath);
            var stagedThisTexture = 0;

            foreach (var staleExt in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                var staleDst = destBase + staleExt;
                if (File.Exists(staleDst))
                {
                    File.Delete(staleDst);
                }
            }

            foreach (var ext in GeneratedTextureRequiredExtensions(texture.TemplateJson))
            {
                var src = sourceBase + ext;
                if (!File.Exists(src))
                {
                    continue;
                }

                var dst = destBase + ext;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                copied++;
                stagedThisTexture++;
                stagedRelativeFiles.Add(Path.GetRelativePath(contentRootToPackage, dst));
            }

            if (stagedThisTexture == 0)
            {
                // TryPrepareGeneratedTextureForStaging already verified this. Keep
                // the guard for a file removed between validation and copy.
                error = $"'{texture.DisplayName}' disappeared from its cooked output while staging. Re-import or recook that texture, then package again.";
                AppendLog("Texture stage blocked: " + error);
                return false;
            }
            else
            {
                AppendLog($"Staged texture '{texture.DisplayName}' ({texture.Kind}) -> {texture.PackagePath} ({stagedThisTexture} file(s)).");
            }
        }

        if (persistProjectChanges)
        {
            WriteGeneratedTextureStageManifest(project, stagedRelativeFiles);
        }
        AppendLog($"Staged {copied} generated texture file(s) into the pack content root.");
        return true;
    }

    private bool TryPrepareGeneratedTextureForStaging(GeneratedTextureEntry texture, out string error)
    {
        var label = string.IsNullOrWhiteSpace(texture.DisplayName) ? "unnamed texture" : texture.DisplayName;
        if (string.IsNullOrWhiteSpace(texture.PackagePath) || string.IsNullOrWhiteSpace(texture.OutputRoot))
        {
            error = $"'{label}' has no package path or cooked-output folder.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(texture.TemplateJson) || !File.Exists(texture.TemplateJson))
        {
            error = $"'{label}' has no readable saved donor template. Choose a cook profile before packaging it.";
            return false;
        }

        var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
        var sourceBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
        var hasCompleteOutput = GeneratedTextureRequiredCookedFilesExist(sourceBase, texture.TemplateJson);
        if (string.IsNullOrWhiteSpace(texture.CookProfile))
        {
            if (!hasCompleteOutput && !File.Exists(texture.SourcePng))
            {
                error = $"Legacy texture '{label}' has no cook profile and no complete existing cooked output. Choose a cook profile before packaging; Batcomputer will not silently migrate it.";
                return false;
            }

            AppendLog($"Legacy texture safety check '{label}': validating its full mip recipe before staging.");
        }

        // Always validate a recorded recipe against its cook report. This is
        // inexpensive when it matches, and is essential after an automatic
        // donor/profile migration because the old files may still look
        // structurally complete while containing the retired payload layout.
        if (!EnsureGeneratedTextureCooked(texture, cookedContentRoot))
        {
            error = string.IsNullOrWhiteSpace(texture.CookProfile)
                ? $"Legacy texture '{label}' could not be proven safe across texture-quality settings. Restore its source PNG and choose Change cook profile so Batcomputer can rebuild the complete mip chain."
                : $"'{label}' could not regenerate its saved recipe. Check its PNG source and donor template, then try again.";
            return false;
        }
        if (!ValidateGeneratedTextureCook(texture, sourceBase, out var validationError))
        {
            error = $"'{label}' still has missing or unverified cooked output files after staging preparation: {validationError}";
            return false;
        }

        error = "";
        return true;
    }

    private bool EnsureGeneratedTextureCooked(
        GeneratedTextureEntry texture,
        string cookedContentRoot,
        bool forceRecook = false)
    {
        if (string.IsNullOrWhiteSpace(texture.SourcePng) ||
            string.IsNullOrWhiteSpace(texture.TemplateJson) ||
            string.IsNullOrWhiteSpace(texture.PackagePath))
        {
            return false;
        }

        TextureCookTemplateService.NormalizeCoreTemplates(_projectRootText.Text.Trim());
        var sourceBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
        var reportPath = sourceBase + ".texture-cook-report.json";
        var needsRecook = forceRecook ||
            !GeneratedTextureRequiredCookedFilesExist(sourceBase, texture.TemplateJson) ||
            ReadTextureEncoderVersion(reportPath) < TextureCookService.CurrentEncoderVersion ||
            !TextureCookReportSourceMatchesFile(reportPath, texture.SourcePng) ||
            !TextureCookReportPixelFormatMatchesTemplate(reportPath, texture.TemplateJson) ||
            !TextureCookReportTemplateMatchesTemplate(reportPath, texture.TemplateJson) ||
            !TextureCookReportOutputMatchesFiles(reportPath, sourceBase, texture.TemplateJson) ||
            !TextureCookReportMatchesSavedEntry(reportPath, texture);

        if (!needsRecook)
        {
            return true;
        }

        if (!File.Exists(texture.SourcePng))
        {
            AppendLog($"Texture recook skipped '{texture.DisplayName}': source PNG missing: {texture.SourcePng}");
            return false;
        }

        if (!File.Exists(texture.TemplateJson))
        {
            AppendLog($"Texture recook skipped '{texture.DisplayName}': template JSON missing: {texture.TemplateJson}");
            return false;
        }

        var nearestMips = UseNearestNeighborMipsForTextureKind(texture.Kind, texture.CookProfile);
        AppendLog($"Recooking texture '{texture.DisplayName}' with encoder v{TextureCookService.CurrentEncoderVersion} ({(nearestMips ? "nearest mips" : "complete high-quality mip chain")})…");
        var result = new TextureCookService(_projectRootText.Text.Trim()).Cook(new TextureCookService.Request
        {
            SourceImagePath = texture.SourcePng,
            TemplateJsonPath = texture.TemplateJson,
            OutputContentRoot = cookedContentRoot,
            OutputPackagePath = texture.PackagePath,
            NearestNeighborMips = nearestMips,
            BleedTransparentRgb = IsUiTextureKind(texture.Kind),
            // Streamed and inline mips are one atomic texture cook. Keeping the
            // donor's inline tail makes lower texture-quality settings display
            // unrelated pixels even when the top mip looks correct on Epic.
            WriteInlineMips = true,
            Bc7InputLayout = "rgba",
            Bc7Quality = IsNativeUimdIconCookProfile(texture.CookProfile)
                ? "best"
                : "balanced",
        });

        foreach (var warning in result.Warnings)
        {
            AppendLog($"  texture recook warning: {warning}");
        }

        if (!result.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"  texture recook failed: {result.Error?.Split('\n').FirstOrDefault() ?? result.Status}");
            return false;
        }

        AppendLog($"  texture recook ok: {texture.PackagePath}");
        return true;
    }

    private static bool IsColorMaskTextureKind(string? kind) =>
        (kind ?? "").Contains("color mask", StringComparison.OrdinalIgnoreCase) ||
        (kind ?? "").Contains("colour mask", StringComparison.OrdinalIgnoreCase);

    private static bool IsSurfaceMaskTextureKind(string? kind) =>
        (kind ?? "").Contains("mmr", StringComparison.OrdinalIgnoreCase) ||
        (kind ?? "").Contains("orm", StringComparison.OrdinalIgnoreCase) ||
        (kind ?? "").Contains("rough", StringComparison.OrdinalIgnoreCase) ||
        (kind ?? "").Contains("spec", StringComparison.OrdinalIgnoreCase) ||
        (kind ?? "").Contains("metal", StringComparison.OrdinalIgnoreCase) ||
        (kind ?? "").Contains("packed", StringComparison.OrdinalIgnoreCase);

    private static int ReadTextureEncoderVersion(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
            return doc.RootElement.TryGetProperty("EncoderVersion", out var version) && version.TryGetInt32(out var value)
                ? value
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool GeneratedTextureRequiredCookedFilesExist(string sourceBase, string? templateJson)
    {
        if (!File.Exists(sourceBase + ".uasset") || string.IsNullOrWhiteSpace(templateJson))
        {
            return false;
        }

        var templateBase = Path.Combine(
            Path.GetDirectoryName(templateJson) ?? "",
            Path.GetFileNameWithoutExtension(templateJson));
        if (File.Exists(templateBase + ".uexp") && !File.Exists(sourceBase + ".uexp"))
        {
            return false;
        }

        if (TextureTemplateHasExternalMips(templateJson) && !File.Exists(sourceBase + ".ubulk"))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Proves that a generated texture package is the complete output of the recipe currently
    /// saved on its suit. Material dependency staging also uses this check, so an older archive
    /// cannot hide a missing external mip payload or a mixed/tampered recook.
    /// </summary>
    internal static bool ValidateGeneratedTextureCook(
        GeneratedTextureEntry texture,
        string sourceBase,
        out string reason)
    {
        if (texture is null)
        {
            reason = "the saved texture recipe is missing";
            return false;
        }
        if (string.IsNullOrWhiteSpace(texture.PackagePath))
        {
            reason = "the saved texture recipe has no package path";
            return false;
        }
        if (string.IsNullOrWhiteSpace(texture.TemplateJson) || !File.Exists(texture.TemplateJson))
        {
            reason = "the saved donor template is missing";
            return false;
        }
        if (!GeneratedTextureRequiredCookedFilesExist(sourceBase, texture.TemplateJson))
        {
            var missing = GeneratedTextureRequiredExtensions(texture.TemplateJson)
                .Where(extension =>
                {
                    var path = sourceBase + extension;
                    return !File.Exists(path) || new FileInfo(path).Length <= 0;
                })
                .ToList();
            reason = missing.Count == 0
                ? "one or more required cooked package files are empty"
                : "missing or empty required file(s): " + string.Join(", ", missing);
            return false;
        }

        var reportPath = sourceBase + ".texture-cook-report.json";
        if (!File.Exists(reportPath))
        {
            reason = "the texture cook report is missing";
            return false;
        }
        if (!TextureCookReportMatchesSavedEntry(reportPath, texture))
        {
            reason = "the cook report does not match the saved package path or profile";
            return false;
        }
        var sourceImagePath = ResolveGeneratedTextureSourceForValidation(texture, sourceBase);
        if (!TextureCookReportSourceMatchesFile(reportPath, sourceImagePath))
        {
            reason = "the source image bytes do not match the image recorded by the last cook";
            return false;
        }
        if (!TextureCookReportPixelFormatMatchesTemplate(reportPath, texture.TemplateJson) ||
            !TextureCookReportTemplateMatchesTemplate(reportPath, texture.TemplateJson))
        {
            reason = "the cook report does not match the saved donor recipe";
            return false;
        }
        if (!TextureCookReportOutputMatchesFiles(reportPath, sourceBase, texture.TemplateJson))
        {
            reason = "the cooked package files do not match the sizes and SHA-256 hashes in the cook report";
            return false;
        }

        reason = "";
        return true;
    }

    private static string ResolveGeneratedTextureSourceForValidation(
        GeneratedTextureEntry texture,
        string packageBase)
    {
        var sourceName = Path.GetFileName(texture.SourcePng ?? "");
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return texture.SourcePng ?? "";
        }

        try
        {
            var normalizedBase = Path.GetFullPath(packageBase)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var savedCookedRoot = Path.GetFullPath(Path.Combine(
                texture.OutputRoot ?? "",
                "Cooked",
                "LEGOBatmanLotDK",
                "Content"));
            var candidateUsesSavedOutput = FileSystemPathUtil.IsWithinDirectory(
                normalizedBase,
                savedCookedRoot,
                allowRoot: false);
            if (candidateUsesSavedOutput &&
                !string.IsNullOrWhiteSpace(texture.SourcePng) &&
                File.Exists(texture.SourcePng))
            {
                return texture.SourcePng;
            }

            var marker = Path.DirectorySeparatorChar +
                         Path.Combine("Cooked", "LEGOBatmanLotDK", "Content") +
                         Path.DirectorySeparatorChar;
            var markerIndex = normalizedBase.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= 0)
            {
                return texture.SourcePng ?? "";
            }

            var outputRoot = normalizedBase[..markerIndex];
            var sourceRoot = Path.GetFullPath(Path.Combine(outputRoot, "Source"));
            var rebasedSource = Path.GetFullPath(Path.Combine(sourceRoot, sourceName));
            if (FileSystemPathUtil.IsWithinDirectory(rebasedSource, sourceRoot) &&
                File.Exists(rebasedSource))
            {
                return rebasedSource;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Fall back to the saved source path below.
        }

        // A rebased candidate prefers its physically adjacent source. Fall back
        // to the saved path only when that adjacent copy is unavailable.
        if (!string.IsNullOrWhiteSpace(texture.SourcePng) && File.Exists(texture.SourcePng))
        {
            return texture.SourcePng;
        }

        return texture.SourcePng ?? "";
    }

    internal static bool TextureCookReportSourceMatchesFile(string reportPath, string? sourceImagePath)
    {
        if (!File.Exists(reportPath) ||
            string.IsNullOrWhiteSpace(sourceImagePath) ||
            !File.Exists(sourceImagePath))
        {
            return false;
        }

        try
        {
            using var reportDoc = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (!reportDoc.RootElement.TryGetProperty("SourceImageBytes", out var bytesElement) ||
                !bytesElement.TryGetInt64(out var expectedBytes) ||
                expectedBytes <= 0 ||
                !reportDoc.RootElement.TryGetProperty("SourceImageSha256", out var hashElement))
            {
                return false;
            }

            var expectedHash = hashElement.GetString() ?? "";
            using var sourceStream = File.OpenRead(sourceImagePath);
            return sourceStream.Length == expectedBytes &&
                   expectedHash.Length == 64 &&
                   Convert.ToHexString(SHA256.HashData(sourceStream))
                       .Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TextureCookReportMatchesSavedEntry(
        string reportPath,
        GeneratedTextureEntry texture)
    {
        try
        {
            using var reportDoc = JsonDocument.Parse(File.ReadAllText(reportPath));
            var root = reportDoc.RootElement;
            var status = root.TryGetProperty("Status", out var statusElement)
                ? statusElement.GetString() ?? ""
                : "";
            var outputPackage = root.TryGetProperty("OutputPackagePath", out var packageElement)
                ? UnrealPathUtil.NormalizePackagePath(packageElement.GetString())
                : "";
            var encoderVersion = root.TryGetProperty("EncoderVersion", out var encoderElement) &&
                                 encoderElement.TryGetInt32(out var parsedEncoderVersion)
                ? parsedEncoderVersion
                : 0;
            if (!status.Equals("created", StringComparison.OrdinalIgnoreCase) ||
                encoderVersion < TextureCookService.CurrentEncoderVersion ||
                !outputPackage.Equals(
                    UnrealPathUtil.NormalizePackagePath(texture.PackagePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (texture.CookWidth > 0 &&
                (!root.TryGetProperty("Width", out var widthElement) ||
                 !widthElement.TryGetInt32(out var width) || width != texture.CookWidth))
            {
                return false;
            }
            if (texture.CookHeight > 0 &&
                (!root.TryGetProperty("Height", out var heightElement) ||
                 !heightElement.TryGetInt32(out var height) || height != texture.CookHeight))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(texture.CookPixelFormat))
            {
                var reportFormat = root.TryGetProperty("PixelFormat", out var formatElement)
                    ? formatElement.GetString() ?? ""
                    : "";
                if (!reportFormat.Equals(texture.CookPixelFormat, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GeneratedTextureRequiredExtensions(string? templateJson)
    {
        var extensions = new List<string> { ".uasset" };
        if (string.IsNullOrWhiteSpace(templateJson))
        {
            extensions.Add(".uexp");
            extensions.Add(".ubulk");
            return extensions;
        }

        var templateBase = Path.Combine(
            Path.GetDirectoryName(templateJson) ?? "",
            Path.GetFileNameWithoutExtension(templateJson));
        if (File.Exists(templateBase + ".uexp"))
        {
            extensions.Add(".uexp");
        }

        if (TextureTemplateHasExternalMips(templateJson))
        {
            extensions.Add(".ubulk");
        }

        return extensions;
    }

    private static bool TextureTemplateHasExternalMips(string templateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateJson));
            var root = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault(e =>
                    e.TryGetProperty("Type", out var type) &&
                    type.GetString()?.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) == true)
                : doc.RootElement;
            if (root.ValueKind == JsonValueKind.Undefined && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                root = doc.RootElement.EnumerateArray().First();
            }

            if (!root.TryGetProperty("Mips", out var mips))
            {
                return true;
            }

            foreach (var mip in mips.EnumerateArray())
            {
                var flags = mip.GetProperty("BulkData").GetProperty("BulkDataFlags").GetString() ?? "";
                if (flags.Contains("PayloadInSeperateFile", StringComparison.OrdinalIgnoreCase) ||
                    flags.Contains("PayloadInSeparateFile", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool TextureCookReportPixelFormatMatchesTemplate(string reportPath, string? templateJson)
    {
        if (!File.Exists(reportPath) || string.IsNullOrWhiteSpace(templateJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
            var cookedFormat = doc.RootElement.TryGetProperty("PixelFormat", out var format)
                ? format.GetString() ?? ""
                : "";
            var templateFormat = TextureTemplatePixelFormat(templateJson);
            return !string.IsNullOrWhiteSpace(cookedFormat) &&
                   cookedFormat.Equals(templateFormat, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TextureCookReportTemplateMatchesTemplate(string reportPath, string? templateJson)
    {
        if (!File.Exists(reportPath) || string.IsNullOrWhiteSpace(templateJson))
        {
            return false;
        }

        try
        {
            using var reportDoc = JsonDocument.Parse(File.ReadAllText(reportPath));
            var cookedTemplate = reportDoc.RootElement.TryGetProperty("TemplatePackagePath", out var template)
                ? template.GetString() ?? ""
                : "";
            var expectedTemplate = TextureTemplatePackagePath(templateJson);
            var (expectedMips, expectedExternal, expectedInline) = TextureTemplateMipCounts(templateJson);
            var cookedMips = reportDoc.RootElement.TryGetProperty("MipCount", out var mipCount) && mipCount.TryGetInt32(out var mipValue)
                ? mipValue
                : -1;
            var cookedExternal = reportDoc.RootElement.TryGetProperty("ExternalMipCount", out var externalCount) && externalCount.TryGetInt32(out var externalValue)
                ? externalValue
                : -1;
            var cookedInline = reportDoc.RootElement.TryGetProperty("InlineMipCount", out var inlineCount) && inlineCount.TryGetInt32(out var inlineValue)
                ? inlineValue
                : -1;
            var cookedFingerprint = reportDoc.RootElement.TryGetProperty("RecipeFingerprint", out var fingerprint)
                ? fingerprint.GetString() ?? ""
                : "";
            var expectedFingerprint = TextureCookService.RecipeFingerprintFor(templateJson);
            return !string.IsNullOrWhiteSpace(cookedTemplate) &&
                   cookedTemplate.Equals(expectedTemplate, StringComparison.OrdinalIgnoreCase) &&
                   cookedMips == expectedMips &&
                   cookedExternal == expectedExternal &&
                   cookedInline == expectedInline &&
                   !string.IsNullOrWhiteSpace(cookedFingerprint) &&
                   cookedFingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TextureCookReportOutputMatchesFiles(
        string reportPath,
        string sourceBase,
        string? templateJson)
    {
        if (!File.Exists(reportPath) || string.IsNullOrWhiteSpace(templateJson))
        {
            return false;
        }

        try
        {
            using var reportDoc = JsonDocument.Parse(File.ReadAllText(reportPath));
            foreach (var extension in GeneratedTextureRequiredExtensions(templateJson))
            {
                var suffix = extension switch
                {
                    ".uasset" => "Uasset",
                    ".uexp" => "Uexp",
                    ".ubulk" => "Ubulk",
                    _ => throw new InvalidOperationException($"Unsupported generated texture extension '{extension}'."),
                };
                var path = sourceBase + extension;
                if (!File.Exists(path) ||
                    !reportDoc.RootElement.TryGetProperty("Output" + suffix + "Bytes", out var bytesElement) ||
                    !bytesElement.TryGetInt64(out var expectedBytes) || expectedBytes <= 0 ||
                    !reportDoc.RootElement.TryGetProperty("Output" + suffix + "Sha256", out var hashElement))
                {
                    return false;
                }

                var expectedHash = hashElement.GetString() ?? "";
                using var stream = File.OpenRead(path);
                if (stream.Length != expectedBytes ||
                    expectedHash.Length != 64 ||
                    !Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (int Total, int External, int Inline) TextureTemplateMipCounts(string templateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateJson));
            var root = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault(element =>
                    element.TryGetProperty("Type", out var type) &&
                    type.GetString()?.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) == true)
                : doc.RootElement;
            if (root.ValueKind == JsonValueKind.Undefined && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                root = doc.RootElement.EnumerateArray().First();
            }

            var total = 0;
            var external = 0;
            var inline = 0;
            foreach (var mip in root.GetProperty("Mips").EnumerateArray())
            {
                total++;
                var flags = mip.GetProperty("BulkData").GetProperty("BulkDataFlags").GetString() ?? "";
                if (flags.Contains("ForceInlinePayload", StringComparison.OrdinalIgnoreCase))
                {
                    inline++;
                }
                else if (flags.Contains("PayloadInSep", StringComparison.OrdinalIgnoreCase))
                {
                    external++;
                }
            }
            return (total, external, inline);
        }
        catch
        {
            return (-1, -1, -1);
        }
    }

    private static string TextureTemplatePackagePath(string templateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateJson));
            var root = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault(e =>
                    e.TryGetProperty("Type", out var type) &&
                    type.GetString()?.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) == true)
                : doc.RootElement;
            if (root.ValueKind == JsonValueKind.Undefined && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                root = doc.RootElement.EnumerateArray().First();
            }
            return root.TryGetProperty("Package", out var package)
                ? UnrealPathUtil.NormalizePackagePath(package.GetString())
                : "";
        }
        catch
        {
            return "";
        }
    }

    private void ClearDedicatedGeneratedTextureStage(
        NativeSuitProject project,
        string contentRootToPackage,
        bool throwOnFailure = false)
    {
        if (string.IsNullOrWhiteSpace(contentRootToPackage))
        {
            return;
        }

        ClearGeneratedTextureStageManifestEntries(project, contentRootToPackage, throwOnFailure);

        var textureStage = Path.Combine(contentRootToPackage, "Mods", "Tex", "Textures");
        if (!Directory.Exists(textureStage))
        {
            return;
        }

        var contentRootFull = Path.GetFullPath(contentRootToPackage);
        var textureStageFull = Path.GetFullPath(textureStage);
        if (!FileSystemPathUtil.IsWithinDirectory(textureStageFull, contentRootFull))
        {
            AppendLog($"Texture stage cleanup skipped: path escaped content root ({textureStageFull}).");
            return;
        }

        try
        {
            Directory.Delete(textureStageFull, recursive: true);
            AppendLog("Cleared stale generated texture staging folder: Mods\\Tex\\Textures.");
        }
        catch (Exception ex)
        {
            AppendLog($"Texture stage cleanup warning: {ex.Message}");
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    "The disposable release stage could not clear stale generated textures.",
                    ex);
            }
        }
    }

    private string GeneratedTextureStageManifestPath(NativeSuitProject project)
    {
        var projectRoot = _projectRootText.Text.Trim();
        var slotId = string.IsNullOrWhiteSpace(project.SlotId) ? "unsaved_suit" : project.SlotId;
        return Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "generated-textures-stage-manifest.json");
    }

    private void ClearGeneratedTextureStageManifestEntries(
        NativeSuitProject project,
        string contentRootToPackage,
        bool throwOnFailure)
    {
        var manifestPath = GeneratedTextureStageManifestPath(project);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            var contentRootFull = Path.GetFullPath(contentRootToPackage);
            var manifest = JsonSerializer.Deserialize<GeneratedTextureStageManifest>(File.ReadAllText(manifestPath))
                           ?? new GeneratedTextureStageManifest();
            var removed = 0;
            foreach (var relative in manifest.Files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(contentRootFull, relative));
                if (!FileSystemPathUtil.IsWithinDirectory(full, contentRootFull))
                {
                    AppendLog($"Texture stage cleanup skipped escaped manifest entry: {relative}");
                    continue;
                }

                if (!File.Exists(full))
                {
                    continue;
                }

                File.Delete(full);
                removed++;
            }

            if (removed > 0)
            {
                AppendLog($"Cleared {removed} previously staged generated texture file(s).");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Texture manifest cleanup warning: {ex.Message}");
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    "The disposable release stage could not clear generated textures listed by the saved stage manifest.",
                    ex);
            }
        }
    }

    private void WriteGeneratedTextureStageManifest(NativeSuitProject project, List<string> stagedRelativeFiles)
    {
        try
        {
            var manifestPath = GeneratedTextureStageManifestPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifest = new GeneratedTextureStageManifest
            {
                Files = stagedRelativeFiles
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            AppendLog($"Texture manifest write warning: {ex.Message}");
        }
    }

    // /Game/Mods/<mod>/Characters/DA_DCMD_<Family>_<Stem>_Playable -> /Game/Mods/<mod>/UI/DA_UIMD_<Family>_<Stem>
    private static string DeriveUimdPackagePath(string dcmdPackagePath)
    {
        dcmdPackagePath = UnrealPathUtil.NormalizePackagePath(dcmdPackagePath);
        var mod = ExtractModFolder(dcmdPackagePath) ?? "Suit";
        var dcmdStem = UnrealPathUtil.AssetName(dcmdPackagePath);
        var suitStem = dcmdStem;
        const string prefix = "DA_DCMD_";
        const string suffix = "_Playable";
        if (suitStem.StartsWith(prefix, StringComparison.Ordinal)) suitStem = suitStem[prefix.Length..];
        if (suitStem.EndsWith(suffix, StringComparison.Ordinal)) suitStem = suitStem[..^suffix.Length];
        return $"/Game/Mods/{mod}/UI/DA_UIMD_{suitStem}";
    }

    internal static string DeriveUimdPackagePathForTest(string dcmdPackagePath) =>
        DeriveUimdPackagePath(dcmdPackagePath);
}
