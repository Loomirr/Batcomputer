using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Texture import/cook and the UIMD + icon assets that ride along with a suit.
/// </summary>
public sealed partial class MainForm
{
    private static Image? TryLoadCategoryIcon(string category)
    {
        var fileNames = category.Equals("Textures", StringComparison.OrdinalIgnoreCase)
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

        var mod = ExtractModFolder(_targetPlayableText.Text.Trim()) ?? _modFolderText.Text.Trim();
        using var dlg = new UimdIconsDialog(mod, _currentProject.IconMenu, _currentProject.IconSuit, _currentProject.IconLeft, _currentProject.IconRight);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        // Store icon paths raw (trimmed) so an explicit object suffix like
        // "...ElectricSuitFront.0" is preserved - UimdGenService honors it.
        _currentProject.IconMenu = dlg.IconMenu.Trim();
        _currentProject.IconSuit = dlg.IconSuit.Trim();
        _currentProject.IconLeft = dlg.IconLeft.Trim();
        _currentProject.IconRight = dlg.IconRight.Trim();
        (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        AppendLog("Saved suit icon paths. Repackage to bake them into the UIMD.");
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
            RefreshHomeTiles();
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
            "Textures are cooked Texture2D assets made from PNGs through the proven one-template duplicate path.\n" +
            "Click a texture tile to copy its /Game package path; right-click for object path, output folder, and source PNG helpers. " +
            "Use the copied path in your custom material texture parameters.";

        if (type == "Texture cooker notes")
        {
            ShowVirtualTiles(new List<VirtualTilePanel.Tile>
            {
                new()
                {
                    Title = "Current proof",
                    Subtitle = "2048² PF_DXT5 / BC3",
                    Accent = Theme.Textures,
                    OnClick = () => AppendLog("Texture notes: the current cooker clones one known-good 2048x2048 PF_DXT5 template and duplicates it to a same-length /Game path."),
                    ToolTip = "The working path uses external .ubulk mips and preserves the template's inline mips. FModel verified this for user-imported 2048x2048 PNG Texture2D assets."
                },
                new()
                {
                    Title = "Path format",
                    Subtitle = "/Game/Mods/Tex/Textures/...",
                    Accent = Theme.Textures,
                    OnClick = () => CopyText("/Game/Mods/Tex/Textures/T_Example_____________1234ABCD", "Copied example texture package path."),
                    ToolTip = "Current Texture2D repathing is template-length based. The safe proof path uses a short Tex mod folder plus a Textures folder, then embeds your chosen name in a fixed-length asset name."
                },
                new()
                {
                    Title = "Packaging",
                    Subtitle = "normal suit pak",
                    Accent = Theme.Textures,
                    OnClick = () => AppendLog("Texture imports still emit a separate proof trio for FModel checks, but normal suit packaging stages only this suit's generated Texture2D files into the suit pak."),
                    ToolTip = "The proof trio is raw-patch based and may show template-source chunks. The normal Package button copies only each generated texture's cooked .uasset/.ubulk into the suit content root."
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
                ToolTip = "Imports a 2048x2048 PNG, cooks it through the template Texture2D duplicate path, and remembers the new /Game path on this suit."
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
            tiles.Add(new VirtualTilePanel.Tile
            {
                Title = TrimMiddle(title, 30),
                Subtitle = $"{texture.Kind} · click copies path\n{TrimMiddle(texture.PackagePath, 38)}",
                Accent = exists ? Theme.Textures : Theme.OnDarkMuted,
                Image = LoadTextureThumbnail(texture.SourcePng),
                OnClick = () => CopyText(texture.PackagePath, $"Copied texture package path: {texture.PackagePath}"),
                ToolTip =
                    $"Package: {texture.PackagePath}\n" +
                    $"Object: {TextureObjectPath(texture)}\n" +
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
        menu.Items.Add("Open output folder", null, (_, _) => OpenTextureOutputFolder(texture));
        menu.Items.Add("Copy IoStore folder", null, (_, _) => CopyText(texture.IoStoreRoot, $"Copied IoStore folder: {texture.IoStoreRoot}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete texture from suit", null, (_, _) => DeleteGeneratedTexture(texture, deleteFiles: false));
        menu.Items.Add("Delete texture + generated files", null, (_, _) => DeleteGeneratedTexture(texture, deleteFiles: true));
        return menu;
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
        if (!outputRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Refused to delete texture output outside _generated\\TextureImports: {outputRoot}");
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
        var script = Path.Combine(projectRoot, "tools", "Build-TextureDuplicateFromTemplate.ps1");
        if (!File.Exists(script))
        {
            AppendLog($"Texture duplicate script not found: {script}");
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

        var textureSettings = PromptForTextureImportSettings(Path.GetFileNameWithoutExtension(dlg.FileName));
        if (textureSettings is null || string.IsNullOrWhiteSpace(textureSettings.Value.Name))
        {
            AppendLog("Texture import cancelled (no texture name entered).");
            return;
        }

        var requestedName = textureSettings.Value.Name;
        var textureKind = textureSettings.Value.Kind;
        var templateJson = DefaultTextureTemplateJson(projectRoot, textureKind);
        if (string.IsNullOrWhiteSpace(templateJson) || !File.Exists(templateJson))
        {
            AppendLog("Texture import needs a proven Texture2D template JSON. Expected a standalone BGRA8/BC5/BC1/BC3 template under _generated.");
            AppendLog($"Looked for: {templateJson}");
            return;
        }

        var rawRoot = DefaultTextureSourceRawRoot(projectRoot);
        if (!TextureTemplateIsStandaloneUasset(templateJson) &&
            (string.IsNullOrWhiteSpace(rawRoot) || !Directory.Exists(rawRoot)))
        {
            AppendLog("Texture import needs a raw IoStore source root from the proven template container.");
            AppendLog($"Expected: {rawRoot}");
            AppendLog("Create it with retoc unpack-raw on the known-working SuitSlots_P.utoc before importing textures.");
            return;
        }

        AppendLog($"Texture template selected: {Path.GetFileName(templateJson)} ({TextureTemplatePixelFormat(templateJson)})");
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

        AppendLog($"Texture import: {Path.GetFileName(dlg.FileName)} as '{requestedName}' ({textureKind})");
        AppendLog($"  package path: {outputPackagePath}");
        AppendLog($"  cooked files: {Path.Combine(outputRoot, "Cooked")}");
        AppendLog("  mode: cook-only (texture will be packed with the suit, no separate texture test pak)");

        _toyboxPrimaryActionButton.Enabled = false;
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
            psi.ArgumentList.Add("-SourcePng");
            psi.ArgumentList.Add(dlg.FileName);
            psi.ArgumentList.Add("-TemplateJson");
            psi.ArgumentList.Add(templateJson);
            psi.ArgumentList.Add("-SourceRawRoot");
            psi.ArgumentList.Add(rawRoot);
            psi.ArgumentList.Add("-OutputPackagePath");
            psi.ArgumentList.Add(outputPackagePath);
            psi.ArgumentList.Add("-ProjectRoot");
            psi.ArgumentList.Add(projectRoot);
            psi.ArgumentList.Add("-OutputRoot");
            psi.ArgumentList.Add(outputRoot);
            psi.ArgumentList.Add("-PackageBaseName");
            psi.ArgumentList.Add(packageBaseName);
            psi.ArgumentList.Add("-CookOnly");
            if (!UseNearestNeighborMipsForTextureKind(textureKind))
            {
                psi.ArgumentList.Add("-LinearMips");
                AppendLog("  mip mode: high-quality UI mips (alpha-safe)");
            }
            // Derived from the assembly so renaming the app doesn't silently break this.
            // A single-file publish has no side-by-side dll; the File.Exists below handles that.
            var toolDll = Path.Combine(AppContext.BaseDirectory,
                typeof(MainForm).Assembly.GetName().Name + ".dll");
            if (File.Exists(toolDll))
            {
                psi.ArgumentList.Add("-ToolDll");
                psi.ArgumentList.Add(toolDll);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                AppendLog("Failed to start powershell for texture import.");
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(stdout)) AppendLog(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) AppendLog(stderr.Trim());

            AppendLog($"Texture import exit code: {process.ExitCode}");
            if (process.ExitCode != 0)
            {
                return;
            }

            var entry = BuildTextureEntryFromSummary(
                outputRoot,
                dlg.FileName,
                templateJson,
                rawRoot,
                outputPackagePath,
                packageBaseName,
                requestedName,
                textureKind);

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

    private GeneratedTextureEntry BuildTextureEntryFromSummary(
        string outputRoot,
        string sourcePng,
        string templateJson,
        string rawRoot,
        string outputPackagePath,
        string packageBaseName,
        string displayName,
        string kind)
    {
        var entry = new GeneratedTextureEntry
        {
            DisplayName = displayName,
            Kind = string.IsNullOrWhiteSpace(kind) ? "Texture" : kind,
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

    private static string DefaultTextureTemplateJson(string projectRoot, string textureKind = "")
    {
        var bgraPath = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_DroneControlBGRA8", "T_GA_DroneControl_BatGirl_AO.json");
        var bc5Path = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_BatarangBC5", "T_Batarang_N.json");
        var suitIconBc7Path = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_SuitIconUI_BC7", "T_SuitIcon_NULL_BCA.json");
        var suitIconDxt5Path = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_SuitIconUI_DXT5", "T_SuitIcon_NULL_BCA.json");
        var uiDxt5Path = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_DebugMenuUI_DXT5", "T_DebugMenuStarImageBorder2.json");
        var dxt5Path = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_BatclawLogo_DXT5", "T_DECAL_BatclawLogo.json");
        var bc1Path = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureStandaloneTemplate_BatarangAO", "T_Batarang_AO.json");

        var preferredPaths = textureKind.Contains("normal", StringComparison.OrdinalIgnoreCase)
            ? new[] { bc5Path, bgraPath, dxt5Path, bc1Path }
            : textureKind.Contains("color mask", StringComparison.OrdinalIgnoreCase) ||
              textureKind.Contains("colour mask", StringComparison.OrdinalIgnoreCase)
                // Use the standalone BGRA8 shell for generated colour masks.
                // The raw EoM/ColourMask donor contains split IoStore data-resource
                // metadata that FModel does not reliably rediscover after a
                // variable-length package/name rewrite. BGRA8 is already proven
                // through the standalone texture path and preserves mask channels
                // without compression quantization.
                ? new[] { bgraPath, dxt5Path, bc1Path }
            : textureKind.Contains("ui", StringComparison.OrdinalIgnoreCase) ||
              textureKind.Contains("icon", StringComparison.OrdinalIgnoreCase)
                ? new[] { suitIconBc7Path, bgraPath, suitIconDxt5Path, uiDxt5Path, dxt5Path, bc1Path }
            : new[] { bgraPath, dxt5Path, bc1Path };

        foreach (var path in preferredPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        foreach (var path in new[]
        {
            Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureTemplateUnpacked_20260712", "TemplatePng.json"),
            Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureTemplateUnpacked", "TemplatePng.json")
        })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var exportRoot = AppSettings.Current.EffectiveExportContentRoot();
        return Path.Combine(exportRoot, "Mods", "ElectricLBM2", "T_Batman_ElectricLBM2_ColorMask.json");
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

        return Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "SuitSlotsRawProbe");
    }

    private static string GuessTextureImportKind(string suggestedName)
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

        if (name.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ui", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("front", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("left", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("right", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("menu", StringComparison.OrdinalIgnoreCase))
        {
            return "UI icon";
        }

        if (name.Contains("_mmr", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("_orm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rough", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("metal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("spec", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mask", StringComparison.OrdinalIgnoreCase))
        {
            return "Roughness/spec mask";
        }

        return "Character texture";
    }

    private static bool IsUiTextureKind(string? textureKind) =>
        !string.IsNullOrWhiteSpace(textureKind) &&
        (textureKind.Contains("ui", StringComparison.OrdinalIgnoreCase) ||
         textureKind.Contains("icon", StringComparison.OrdinalIgnoreCase));

    private static bool UseNearestNeighborMipsForTextureKind(string? textureKind) =>
        !IsUiTextureKind(textureKind);

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

        return slot switch
        {
            "suit" when token.Contains("suiticon") => 120,
            "suit" when token.Contains("icon") => 100,
            "suit" when token.Contains("suit") && !token.Contains("front") && !token.Contains("left") && !token.Contains("right") => 30,
            "menu" when token.Contains("menu") => 110,
            "menu" when token.Contains("front") => 90,
            "left" when token.Contains("left") => 100,
            "right" when token.Contains("right") => 100,
            _ => 0
        };
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
                $"Using same-length proof path '{fallbackPath}' until a standalone Texture2D template is configured.");
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

        using var form = new Form
        {
            Text = "Pick generated texture",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(760, 420),
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark
        };

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.SlateDark,
            ForeColor = Theme.OnDark,
            IntegralHeight = false
        };
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

    private void StageGeneratedTexturesIntoContentRoot(NativeSuitProject project, string contentRootToPackage)
    {
        ClearDedicatedGeneratedTextureStage(project, contentRootToPackage);

        if (project.GeneratedTextures.Count == 0)
        {
            WriteGeneratedTextureStageManifest(project, new List<string>());
            return;
        }

        var copied = 0;
        var projectChanged = false;
        var stagedRelativeFiles = new List<string>();
        foreach (var texture in project.GeneratedTextures)
        {
            if (string.IsNullOrWhiteSpace(texture.PackagePath) || string.IsNullOrWhiteSpace(texture.OutputRoot))
            {
                AppendLog($"Texture stage skipped '{texture.DisplayName}': missing package path or output root.");
                continue;
            }

            projectChanged |= UpgradeGeneratedTextureTemplateIfAvailable(texture);
            projectChanged |= UpgradeGeneratedTexturePackagePathIfNeeded(project, texture);
            var cookedContentRoot = Path.Combine(texture.OutputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
            EnsureGeneratedTextureCooked(texture, cookedContentRoot);
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
                AppendLog($"Texture stage skipped '{texture.DisplayName}': required cooked files not found at {sourceBase}. Re-import the texture.");
            }
            else
            {
                AppendLog($"Staged texture '{texture.DisplayName}' ({texture.Kind}) -> {texture.PackagePath} ({stagedThisTexture} file(s)).");
            }
        }

        WriteGeneratedTextureStageManifest(project, stagedRelativeFiles);
        if (projectChanged)
        {
            try
            {
                (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(project);
            }
            catch (Exception ex)
            {
                AppendLog($"Texture template upgrade save warning: {ex.Message}");
            }
        }
        AppendLog($"Staged {copied} generated texture file(s) into the pack content root.");
    }

    private bool UpgradeGeneratedTextureTemplateIfAvailable(GeneratedTextureEntry texture)
    {
        var projectRoot = _projectRootText.Text.Trim();
        var preferred = DefaultTextureTemplateJson(projectRoot, texture.Kind);
        if (string.IsNullOrWhiteSpace(preferred) ||
            !File.Exists(preferred) ||
            string.Equals(Path.GetFullPath(preferred), Path.GetFullPath(texture.TemplateJson ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var before = TextureTemplatePixelFormat(texture.TemplateJson ?? "");
        var after = TextureTemplatePixelFormat(preferred);
        texture.TemplateJson = preferred;
        AppendLog($"Texture template upgraded '{texture.DisplayName}' ({texture.Kind}): {before} -> {after}. It will be recooked before staging.");
        return true;
    }

    private bool UpgradeGeneratedTexturePackagePathIfNeeded(NativeSuitProject project, GeneratedTextureEntry texture)
    {
        if (string.IsNullOrWhiteSpace(texture.TemplateJson) ||
            string.IsNullOrWhiteSpace(texture.PackagePath))
        {
            return false;
        }

        var needsSameLengthPath = TextureTemplateNeedsSameLengthPath(texture.TemplateJson, texture.Kind);
        var isStandaloneTemplate = TextureTemplateIsStandaloneUasset(texture.TemplateJson);
        if (!needsSameLengthPath && !isStandaloneTemplate)
        {
            // Do not reinterpret older non-standalone body/material entries.
            // Their package path may intentionally be tied to a raw IoStore
            // donor and they are outside this UI-path migration.
            return false;
        }

        var currentName = UnrealPathUtil.AssetName(texture.PackagePath);
        var index = TextureSlotIndexFromPackageBaseName(texture.PackageBaseName);
        if (index <= 0)
        {
            index = Math.Max(1, project.GeneratedTextures.IndexOf(texture) + 1);
        }

        var modFolder = TextureModFolderForProject(project);
        var newPackagePath = TexturePackagePathFromUserName(
            texture.TemplateJson,
            string.IsNullOrWhiteSpace(texture.DisplayName) ? currentName : texture.DisplayName,
            index,
            modFolder,
            project.SlotId,
            texture.Kind);

        if (texture.PackagePath.Equals(newPackagePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var oldPackagePath = texture.PackagePath;
        texture.PackagePath = newPackagePath;
        texture.ObjectPath = ToObjectPath(newPackagePath);
        ReplaceGeneratedTexturePathReferences(project, oldPackagePath, newPackagePath);
        var reason = needsSameLengthPath
            ? "same-length fallback"
            : "standalone clean path";
        AppendLog($"Texture path migrated '{texture.DisplayName}' ({texture.Kind}, {reason}): {oldPackagePath} -> {newPackagePath}");
        return true;
    }

    private static int TextureSlotIndexFromPackageBaseName(string packageBaseName)
    {
        if (string.IsNullOrWhiteSpace(packageBaseName))
        {
            return 0;
        }

        var marker = packageBaseName.LastIndexOf("_P", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
        {
            return 0;
        }

        var end = marker;
        var start = end;
        while (start > 0 && char.IsDigit(packageBaseName[start - 1]))
        {
            start--;
        }

        return start < end && int.TryParse(packageBaseName[start..end], out var value)
            ? value
            : 0;
    }

    private static string TextureModFolderForProject(NativeSuitProject project)
    {
        foreach (var path in new[] { project.TargetPackages.Dcmd, project.TargetPackages.Playable, project.TargetPackages.Cutscene })
        {
            var mod = ModFolderFromPackagePath(path);
            if (!string.IsNullOrWhiteSpace(mod))
            {
                return mod;
            }
        }

        return MakeSafeTextureToken(project.SlotId);
    }

    private static void ReplaceGeneratedTexturePathReferences(NativeSuitProject project, string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        if (project.IconMenu.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) project.IconMenu = newPath;
        if (project.IconSuit.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) project.IconSuit = newPath;
        if (project.IconLeft.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) project.IconLeft = newPath;
        if (project.IconRight.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) project.IconRight = newPath;
    }

    private void EnsureGeneratedTextureCooked(GeneratedTextureEntry texture, string cookedContentRoot)
    {
        if (string.IsNullOrWhiteSpace(texture.SourcePng) ||
            string.IsNullOrWhiteSpace(texture.TemplateJson) ||
            string.IsNullOrWhiteSpace(texture.PackagePath))
        {
            return;
        }

        var sourceBase = PackagePathToContentPath(cookedContentRoot, texture.PackagePath);
        var reportPath = sourceBase + ".texture-cook-report.json";
        var needsRecook =
            !GeneratedTextureRequiredCookedFilesExist(sourceBase, texture.TemplateJson) ||
            ReadTextureEncoderVersion(reportPath) < TextureCookService.CurrentEncoderVersion ||
            !TextureCookReportPixelFormatMatchesTemplate(reportPath, texture.TemplateJson) ||
            !TextureCookReportTemplateMatchesTemplate(reportPath, texture.TemplateJson);

        if (!needsRecook)
        {
            return;
        }

        if (!File.Exists(texture.SourcePng))
        {
            AppendLog($"Texture recook skipped '{texture.DisplayName}': source PNG missing: {texture.SourcePng}");
            return;
        }

        if (!File.Exists(texture.TemplateJson))
        {
            AppendLog($"Texture recook skipped '{texture.DisplayName}': template JSON missing: {texture.TemplateJson}");
            return;
        }

        var nearestMips = UseNearestNeighborMipsForTextureKind(texture.Kind);
        AppendLog($"Recooking texture '{texture.DisplayName}' with encoder v{TextureCookService.CurrentEncoderVersion} ({(nearestMips ? "nearest mips" : "high-quality UI mips")})...");
        var result = new TextureCookService(_projectRootText.Text.Trim()).Cook(new TextureCookService.Request
        {
            SourceImagePath = texture.SourcePng,
            TemplateJsonPath = texture.TemplateJson,
            OutputContentRoot = cookedContentRoot,
            OutputPackagePath = texture.PackagePath,
            NearestNeighborMips = nearestMips,
            // Native EoM ColorMask templates contain both external and inline
            // mips. Rewrite the inline tail too; otherwise the lower mips remain
            // the donor image and Red Brick sampling can fall back to stale data.
            WriteInlineMips = IsColorMaskTextureKind(texture.Kind)
        });

        foreach (var warning in result.Warnings)
        {
            AppendLog($"  texture recook warning: {warning}");
        }

        if (!result.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"  texture recook failed: {result.Error?.Split('\n').FirstOrDefault() ?? result.Status}");
            return;
        }

        AppendLog($"  texture recook ok: {texture.PackagePath}");
    }

    private static bool IsColorMaskTextureKind(string? kind) =>
        (kind ?? "").Contains("color mask", StringComparison.OrdinalIgnoreCase) ||
        (kind ?? "").Contains("colour mask", StringComparison.OrdinalIgnoreCase);

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
            return !string.IsNullOrWhiteSpace(cookedTemplate) &&
                   cookedTemplate.Equals(expectedTemplate, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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

    private void ClearDedicatedGeneratedTextureStage(NativeSuitProject project, string contentRootToPackage)
    {
        if (string.IsNullOrWhiteSpace(contentRootToPackage))
        {
            return;
        }

        ClearGeneratedTextureStageManifestEntries(project, contentRootToPackage);

        var textureStage = Path.Combine(contentRootToPackage, "Mods", "Tex", "Textures");
        if (!Directory.Exists(textureStage))
        {
            return;
        }

        var contentRootFull = Path.GetFullPath(contentRootToPackage);
        var textureStageFull = Path.GetFullPath(textureStage);
        if (!textureStageFull.StartsWith(contentRootFull, StringComparison.OrdinalIgnoreCase))
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
        }
    }

    private string GeneratedTextureStageManifestPath(NativeSuitProject project)
    {
        var projectRoot = _projectRootText.Text.Trim();
        var slotId = string.IsNullOrWhiteSpace(project.SlotId) ? "unsaved_suit" : project.SlotId;
        return Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects", slotId, "generated-textures-stage-manifest.json");
    }

    private void ClearGeneratedTextureStageManifestEntries(NativeSuitProject project, string contentRootToPackage)
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
                if (!full.StartsWith(contentRootFull, StringComparison.OrdinalIgnoreCase))
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

    // /Game/Mods/<mod>/Characters/DA_DCMD_Batman_<Stem>_Playable -> /Game/Mods/<mod>/UI/DA_UIMD_Batman_<Stem>
    private static string DeriveUimdPackagePath(string dcmdPackagePath)
    {
        dcmdPackagePath = UnrealPathUtil.NormalizePackagePath(dcmdPackagePath);
        var mod = ExtractModFolder(dcmdPackagePath) ?? "Suit";
        var dcmdStem = UnrealPathUtil.AssetName(dcmdPackagePath);
        var suitStem = dcmdStem;
        const string prefix = "DA_DCMD_Batman_";
        const string suffix = "_Playable";
        if (suitStem.StartsWith(prefix, StringComparison.Ordinal)) suitStem = suitStem[prefix.Length..];
        if (suitStem.EndsWith(suffix, StringComparison.Ordinal)) suitStem = suitStem[..^suffix.Length];
        return $"/Game/Mods/{mod}/UI/DA_UIMD_Batman_{suitStem}";
    }
}
