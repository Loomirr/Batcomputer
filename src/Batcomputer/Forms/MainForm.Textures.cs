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
    internal const string NativeMmrCookProfile = "mmr-2k-dxt1-native";

    private enum TextureProfileSafety
    {
        Verified,
        Experimental,
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
        menu.Items.Add("Reimport image", null, (_, _) => ReimportCurrentSuitTexture(texture));
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

    private void ReimportCurrentSuitTexture(GeneratedTextureEntry texture)
    {
        EnsureProject();
        if (_currentProject is null || !ReimportGeneratedTextureSource(texture))
        {
            return;
        }

        (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        AppendLog($"Reimported texture '{texture.DisplayName}' from its source PNG.");
        RefreshToyboxTiles();
    }

    private bool ReimportGeneratedTextureSource(GeneratedTextureEntry texture)
    {
        if (string.IsNullOrWhiteSpace(texture.SourcePng) || !File.Exists(texture.SourcePng))
        {
            Dialog.Warn(this, "Reimport image", "The saved source PNG is missing. Import a new texture instead.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(texture.OutputRoot) || string.IsNullOrWhiteSpace(texture.PackagePath))
        {
            Dialog.Warn(this, "Reimport image", "This texture does not have a complete cooked output location.");
            return false;
        }
        if (!Dialog.Confirm(this, $"Reimport {texture.DisplayName}?", "The current PNG will be cooked again in place using its existing profile.", "Reimport"))
        {
            return false;
        }

        var backupPath = CreateTextureBackup(texture, "Before reimporting source image");
        if (!string.IsNullOrWhiteSpace(backupPath))
        {
            AppendLog($"Texture backup created: {backupPath}");
        }

        var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
        var sourceBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
        var reportPath = sourceBase + ".texture-cook-report.json";
        try
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
        catch (Exception ex)
        {
            AppendLog($"Texture reimport could not clear its previous cook report: {ex.Message}");
        }

        if (!EnsureGeneratedTextureCooked(texture, cookedContentRoot))
        {
            Dialog.Warn(this, "Reimport image", "The texture could not be cooked again. The previous generated files were left in place.");
            return false;
        }

        texture.CreatedUtc = DateTime.UtcNow.ToString("O");
        return true;
    }

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

        if (IsUiTextureKind(textureKind))
        {
            Add(NativeUimdIconCookProfile, "Native 256px BC7 UIMD icon", nativeSuitIconPath, 256, 256, "PF_BC7",
                TextureProfileSafety.Verified,
                "Uses the game's native suit-menu Texture2D layout: BC7 with nine inline mips. Verified for suit, menu, left, and right UIMD icon slots.");
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
                TextureProfileSafety.Experimental,
                "Native EoM MMR metadata and the complete 12-mip layout are structurally verified; in-game material response is pending acceptance. R is metalness and B is roughness; G is unused.");
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
            NativeUimdIconCookProfile => TextureProfileSafety.Verified,
            _ => TextureProfileSafety.Experimental,
        };
    }

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
        NativeMmrCookProfile => "Native linear PF_DXT1 MMR with a complete 2048px-to-1px mip chain. R is metalness and B is roughness; G is unused. In-game acceptance is pending.",
        NativeUimdIconCookProfile => "Verified native UIMD icon layout: 256px BC7 with nine inline mips.",
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
            AppendLog($"Texture backup created: {backupPath}");
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
        if (string.IsNullOrWhiteSpace(texture.OutputRoot) || string.IsNullOrWhiteSpace(texture.PackagePath))
        {
            return null;
        }

        var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
        var sourceBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
        var sourceFiles = new[] { ".uasset", ".uexp", ".ubulk", ".texture-cook-report.json" }
            .Select(extension => sourceBase + extension)
            .Where(File.Exists)
            .ToList();
        if (sourceFiles.Count == 0)
        {
            return null;
        }

        try
        {
            var backupRoot = Path.Combine(texture.OutputRoot, "TextureBackups",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            Directory.CreateDirectory(backupRoot);
            var snapshot = new TextureBackupSnapshot
            {
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                Reason = reason,
                Texture = texture,
            };
            File.WriteAllText(Path.Combine(backupRoot, "recipe-before.json"),
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var source in sourceFiles)
            {
                File.Copy(source, Path.Combine(backupRoot, Path.GetFileName(source)), overwrite: true);
            }

            return backupRoot;
        }
        catch (Exception ex)
        {
            AppendLog($"Texture backup warning: {ex.Message}");
            return null;
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
            .Where(path => File.Exists(Path.Combine(path, "recipe-before.json")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void RestoreLatestTextureBackup(GeneratedTextureEntry texture)
    {
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

            var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
            var destinationBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
            foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk", ".texture-cook-report.json" })
            {
                var destination = destinationBase + extension;
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                var source = Path.Combine(backupPath, Path.GetFileName(destination));
                if (File.Exists(source))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                }
            }

            RestoreTextureRecipe(texture, snapshot.Texture);
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

    private static void RestoreTextureRecipe(GeneratedTextureEntry target, GeneratedTextureEntry source)
    {
        target.Kind = source.Kind;
        target.CookProfile = source.CookProfile;
        target.CookWidth = source.CookWidth;
        target.CookHeight = source.CookHeight;
        target.CookPixelFormat = source.CookPixelFormat;
        target.TemplateJson = source.TemplateJson;
        target.SourceRawRoot = source.SourceRawRoot;
        target.PackageBaseName = source.PackageBaseName;
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
        if (name.Contains("normal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("_nrm", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("_N_", StringComparison.Ordinal))
        {
            return "Normal map";
        }

        if (name.Contains("color mask", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("colour mask", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("colormask", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("colourmask", StringComparison.OrdinalIgnoreCase))
        {
            return "Color mask";
        }

        if (name.Contains("front", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("left", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("right", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("menu", StringComparison.OrdinalIgnoreCase))
        {
            return "UI artwork";
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

        if (HasMmrNameSuffix(name) ||
            HasDelimitedOrmNameSuffix(name) ||
            name.Contains("rough", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("metal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("spec", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mask", StringComparison.OrdinalIgnoreCase))
        {
            return "Roughness/spec mask";
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

    private static bool IsNativeUimdIconCookProfile(string? cookProfile) =>
        string.Equals(cookProfile, NativeUimdIconCookProfile, StringComparison.OrdinalIgnoreCase);

    private static bool UseNearestNeighborMipsForTextureKind(string? textureKind, string? cookProfile = null) => false;

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
    /// Every generated texture referenced by a UIMD icon slot must use the
    /// game's compact 256px BC7 layout. The retired 1K/2K UI profiles borrowed
    /// world-texture donors with external mips; FModel could parse parts of
    /// those assets, but the suit menu sampled corrupted data in game.
    ///
    /// This is deliberately role-based rather than name-based so old projects
    /// such as Electric (legacy "UI icon") and projects already saved with a
    /// generic profile are upgraded without renaming their package paths.
    /// </summary>
    private bool NormalizeGeneratedUimdIconRecipes(NativeSuitProject project)
    {
        var slots = new (string Name, string Path, string Kind)[]
        {
            ("menu", project.IconMenu, "UI artwork"),
            ("suit", project.IconSuit, "Suit selector icon"),
            ("left", project.IconLeft, "UI artwork"),
            ("right", project.IconRight, "UI artwork"),
        };
        var targets = slots
            .Select(slot => new { Slot = slot, Texture = FindGeneratedTextureByPackage(project, slot.Path) })
            .Where(item => item.Texture is not null)
            .GroupBy(item => item.Texture!.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Slot.Name.Equals("suit", StringComparison.OrdinalIgnoreCase))
                .First())
            .ToList();
        if (targets.Count == 0)
        {
            return false;
        }

        var projectRoot = _projectRootText.Text.Trim();
        if (!TextureCookTemplateService.NormalizeNativeSuitIconTemplate(projectRoot))
        {
            var needsUpgrade = targets.Any(item =>
                !IsNativeUimdIconCookProfile(item.Texture!.CookProfile) ||
                !item.Texture.TemplateJson.Contains(
                    TextureCookTemplateService.NativeSuitIconTemplateFolder,
                    StringComparison.OrdinalIgnoreCase));
            if (needsUpgrade)
            {
                AppendLog("UIMD icon migration blocked: the native 256px BC7 donor is unavailable. Refresh game assets before packaging this suit.");
            }
            return false;
        }

        var nativeTemplate = TextureCookTemplateService.TemplateJsonPath(
            projectRoot,
            TextureCookTemplateService.NativeSuitIconTemplateFolder);
        var changed = false;
        foreach (var item in targets)
        {
            var texture = item.Texture!;
            var desiredKind = item.Slot.Kind;
            var recipeChanged =
                !string.Equals(texture.Kind, desiredKind, StringComparison.OrdinalIgnoreCase) ||
                !IsNativeUimdIconCookProfile(texture.CookProfile) ||
                !string.Equals(texture.TemplateJson, nativeTemplate, StringComparison.OrdinalIgnoreCase) ||
                texture.CookWidth != 256 ||
                texture.CookHeight != 256 ||
                !string.Equals(texture.CookPixelFormat, "PF_BC7", StringComparison.OrdinalIgnoreCase);
            if (!recipeChanged)
            {
                continue;
            }

            texture.Kind = desiredKind;
            texture.CookProfile = NativeUimdIconCookProfile;
            texture.CookWidth = 256;
            texture.CookHeight = 256;
            texture.CookPixelFormat = "PF_BC7";
            texture.TemplateJson = nativeTemplate;
            AppendLog($"UIMD icon recipe normalized: {item.Slot.Name} '{texture.DisplayName}' -> native 256px BC7 inline-mip layout.");
            changed = true;
        }

        return changed;
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
        var reportPath = sourceBase + ".texture-cook-report.json";
        if (!GeneratedTextureRequiredCookedFilesExist(sourceBase, texture.TemplateJson) ||
            !TextureCookReportOutputMatchesFiles(reportPath, sourceBase, texture.TemplateJson))
        {
            error = $"'{label}' still has missing or unverified cooked output files after staging preparation.";
            return false;
        }

        error = "";
        return true;
    }

    private bool EnsureGeneratedTextureCooked(GeneratedTextureEntry texture, string cookedContentRoot)
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
        var needsRecook =
            !GeneratedTextureRequiredCookedFilesExist(sourceBase, texture.TemplateJson) ||
            ReadTextureEncoderVersion(reportPath) < TextureCookService.CurrentEncoderVersion ||
            !TextureCookReportPixelFormatMatchesTemplate(reportPath, texture.TemplateJson) ||
            !TextureCookReportTemplateMatchesTemplate(reportPath, texture.TemplateJson) ||
            !TextureCookReportOutputMatchesFiles(reportPath, sourceBase, texture.TemplateJson);

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
