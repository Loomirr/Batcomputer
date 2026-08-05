namespace Batcomputer;

/// <summary>Red Brick authoring, reusable brick templates, and icon selection.</summary>
public sealed partial class MainForm
{
    private sealed record RedBrickLibraryItem(string ModPath, string ModName, ModRedBrickEntry Brick);

    private Control CreateRedBrickWorkspace()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12, 10, 12, 12),
            BackColor = Theme.PanelBg,
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBg, Padding = new Padding(16, 12, 16, 12) };
        header.Paint += (_, e) =>
        {
            using var accent = new Pen(Theme.RedBricks, 3);
            e.Graphics.DrawLine(accent, 1, 0, 1, header.Height);
        };
        _redBrickWorkspaceTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "RED BRICKS",
            ForeColor = Theme.RedBricks,
            Font = Theme.Title,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _redBrickWorkspaceSubtitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            ForeColor = Theme.OnDarkMuted,
            Font = Theme.Body,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _redBrickPrimaryActionButton = new Button
        {
            Text = "+ New red brick",
            Dock = DockStyle.Right,
            Width = 156,
            Font = Theme.BodyStrong,
        };
        StyleRedBrickPrimaryButton(_redBrickPrimaryActionButton);
        _redBrickPrimaryActionButton.Click += (_, _) => CreateRedBrick();
        header.Controls.Add(_redBrickPrimaryActionButton);
        header.Controls.Add(_redBrickWorkspaceSubtitle);
        header.Controls.Add(_redBrickWorkspaceTitle);
        shell.Controls.Add(header, 0, 0);
        shell.SetColumnSpan(header, 2);

        var rail = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.SlateDark,
            Padding = new Padding(5, 8, 5, 8),
        };
        AddRedBrickRailButton(rail, RedBrickWorkspaceSection.ThisMod, "This mod", "RedBricks.png");
        AddRedBrickRailButton(rail, RedBrickWorkspaceSection.BaseGame, "Base game", "RedBricks.png");
        AddRedBrickRailButton(rail, RedBrickWorkspaceSection.Library, "Modded", "RedBricks.png");
        AddRedBrickRailButton(rail, RedBrickWorkspaceSection.Icons, "Icons", "Textures.png");
        shell.Controls.Add(rail, 0, 1);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.WindowBg, Padding = new Padding(10) };
        body.Paint += (_, e) =>
        {
            using var border = new Pen(Theme.LineSoft);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, body.Width - 1), Math.Max(0, body.Height - 1));
        };
        _redBrickTileGrid = new VirtualTilePanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.WindowBg,
            Margin = Padding.Empty,
        };
        body.Controls.Add(_redBrickTileGrid);
        shell.Controls.Add(body, 1, 1);
        UpdateRedBrickWorkspaceRailSelection();
        return shell;
    }

    private static void StyleRedBrickPrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 119, 119);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(190, 57, 57);
        button.BackColor = Theme.RedBricks;
        button.ForeColor = Theme.SlateDark;
        button.Cursor = Cursors.Hand;
    }

    private void AddRedBrickRailButton(FlowLayoutPanel rail, RedBrickWorkspaceSection section, string label, string iconAsset)
    {
        var button = new Button
        {
            Text = label,
            Width = 90,
            Height = 58,
            Margin = new Padding(1, 1, 1, 4),
            Padding = new Padding(0, 4, 0, 3),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0, BorderColor = Theme.RedBricks },
            Font = Theme.Caption,
            TextAlign = ContentAlignment.BottomCenter,
            ForeColor = Theme.RedBricks,
            BackColor = Theme.SlateDark,
            Cursor = Cursors.Hand,
            Image = LoadNavigationIcon(iconAsset, new Size(21, 21)),
            ImageAlign = ContentAlignment.TopCenter,
            TextImageRelation = TextImageRelation.ImageAboveText,
        };
        button.FlatAppearance.MouseOverBackColor = Theme.Tint(Theme.RedBricks);
        button.Click += (_, _) => SelectRedBrickWorkspaceSection(section);
        _redBrickWorkspaceButtons[section] = button;
        rail.Controls.Add(button);
    }

    private void SelectRedBrickWorkspaceSection(RedBrickWorkspaceSection section)
    {
        _redBrickWorkspaceSection = section;
        UpdateRedBrickWorkspaceRailSelection();
        RefreshRedBrickWorkspace();
    }

    private void OpenRedBrickWorkspaceForMod(string modProjectPath)
    {
        _homeActiveModProjectPath = modProjectPath;
        _redBrickWorkspaceSection = RedBrickWorkspaceSection.ThisMod;
        SelectWorkspaceFolder(WorkspaceFolder.RedBricks);
    }

    private void RefreshHomeRedBrickTiles()
    {
        var summaries = ModService.ListMods().ToList();
        var (activeSummary, activeMod) = ResolveHomeActiveMod(summaries);
        var library = GetRedBrickLibrary(summaries);
        var activeBricks = activeMod?.RedBricks
            .OrderBy(brick => brick.MenuOrder)
            .ThenBy(brick => brick.DisplayName)
            .ToList() ?? new List<ModRedBrickEntry>();
        var hasActiveMod = activeSummary is not null && activeMod is not null;

        var hero = new VirtualTilePanel.HeroModel
        {
            Overline = "MOD CONTENT",
            Title = hasActiveMod ? "Red Bricks in " + activeSummary!.DisplayName : "Red Brick library",
            Subtitle = hasActiveMod
                ? $"{activeBricks.Count} Red Brick{(activeBricks.Count == 1 ? "" : "s")} will build with this mod's suits in one independent release payload."
                : "Choose a mod to add a Red Brick, or open the library to browse existing ones.",
            ThumbAccent = Theme.RedBricks,
            Chips =
            [
                ($"{library.Count} saved", Theme.RedBricks),
                (hasActiveMod ? "mod selected" : "no mod selected", hasActiveMod ? Theme.Mods : Theme.Warn),
            ],
        };
        const string SectionCurrent = "THIS MOD";
        const string SectionLibrary = "LIBRARY";
        var tiles = new List<VirtualTilePanel.Tile>();
        if (hasActiveMod)
        {
            var modPath = activeSummary!.Path;
            tiles.Add(new()
            {
                Section = SectionCurrent,
                Title = "+ Add Red Brick",
                Subtitle = "create or reuse in this mod",
                Accent = Theme.RedBricks,
                Dashed = true,
                OnClick = () => OpenRedBrickWorkspaceForMod(modPath),
            });
            tiles.Add(new()
            {
                Section = SectionCurrent,
                Title = "Manage Red Bricks",
                Subtitle = $"{activeBricks.Count} in {activeSummary.DisplayName}",
                Accent = Theme.RedBricks,
                OnClick = () => OpenRedBrickWorkspaceForMod(modPath),
            });
            foreach (var brick in activeBricks.Take(10))
            {
                var captured = brick;
                tiles.Add(new()
                {
                    Section = SectionCurrent,
                    Title = TrimMiddle(captured.DisplayName, 26),
                    Subtitle = $"{captured.PrimaryColourRow} | {captured.SecondaryColourRow} | {captured.TertiaryColourRow}",
                    Accent = Theme.RedBricks,
                    Image = LoadRedBrickIconPreview(captured),
                    OnClick = () => OpenRedBrickWorkspaceForMod(modPath),
                });
            }
        }
        else
        {
            tiles.Add(new()
            {
                Section = SectionCurrent,
                Title = "Choose a mod",
                Subtitle = "select the release that will own this Red Brick",
                Accent = Theme.RedBricks,
                Dashed = true,
                OnClick = () => SelectHomeWorkspaceSection(HomeWorkspaceSection.Mods),
            });
        }

        foreach (var item in library.Take(10))
        {
            var captured = item;
            tiles.Add(new()
            {
                Section = SectionLibrary,
                Title = TrimMiddle(captured.Brick.DisplayName, 26),
                Subtitle = captured.ModName,
                Accent = Theme.RedBricks,
                Image = LoadRedBrickIconPreview(captured.Brick),
                OnClick = () =>
                {
                    if (hasActiveMod) AddLibraryBrickToActiveMod(captured);
                    else OpenRedBrickWorkspaceForMod(captured.ModPath);
                },
            });
        }
        ShowVirtualTiles(tiles, hero: hero);
    }

    private void UpdateRedBrickWorkspaceRailSelection()
    {
        foreach (var (section, button) in _redBrickWorkspaceButtons)
        {
            var selected = section == _redBrickWorkspaceSection;
            button.BackColor = selected ? Theme.Tint(Theme.RedBricks) : Theme.SlateDark;
            button.FlatAppearance.BorderSize = selected ? 1 : 0;
            button.FlatAppearance.BorderColor = Theme.RedBricks;
        }
    }

    private void RefreshRedBrickWorkspace()
    {
        if (_redBrickTileGrid is null || _redBrickWorkspaceSubtitle is null || _redBrickWorkspaceTitle is null)
        {
            return;
        }

        var summaries = ModService.ListMods().ToList();
        var (summary, mod) = ResolveHomeActiveMod(summaries);
        if (_redBrickPrimaryActionButton is not null) _redBrickPrimaryActionButton.Enabled = mod is not null;

        var tiles = _redBrickWorkspaceSection switch
        {
            RedBrickWorkspaceSection.BaseGame => BuildBaseGameRedBrickTiles(),
            RedBrickWorkspaceSection.Library => BuildRedBrickLibraryTiles(summary, mod, summaries),
            RedBrickWorkspaceSection.Icons => BuildRedBrickIconTiles(summary, mod),
            _ => BuildCurrentModRedBrickTiles(summary, mod),
        };
        _redBrickTileGrid.SetHero(null);
        _redBrickTileGrid.HeaderText = string.Empty;
        _redBrickTileGrid.EmptyMessage = string.Empty;
        _redBrickTileGrid.SetTiles(tiles);
    }

    private IReadOnlyList<VirtualTilePanel.Tile> BuildCurrentModRedBrickTiles(
        ModProjectService.ModSummary? summary,
        NativeSuitModProject? mod)
    {
        const string section = "THIS MOD";
        _redBrickWorkspaceTitle!.Text = "RED BRICKS";
        if (summary is null || mod is null)
        {
            _redBrickWorkspaceSubtitle!.Text = "Choose a mod on Home before adding its Red Bricks.";
            return
            [
                new VirtualTilePanel.Tile
                {
                    Section = section,
                    Title = "Choose a mod",
                    Subtitle = "select the release that will own this Red Brick",
                    Accent = Theme.RedBricks,
                    Dashed = true,
                    OnClick = () => SelectHomeWorkspaceSection(HomeWorkspaceSection.Mods),
                },
            ];
        }

        var bricks = (mod.RedBricks ?? [])
            .OrderBy(item => item.MenuOrder)
            .ThenBy(item => item.DisplayName)
            .ToList();
        _redBrickWorkspaceSubtitle!.Text = $"{summary.DisplayName} - {bricks.Count} Red Brick{(bricks.Count == 1 ? "" : "s")} - each one builds in this mod's independent payload.";

        var tiles = new List<VirtualTilePanel.Tile>
        {
            CreateRedBrickVirtualTile(
                section,
                "+ Red Brick",
                $"create in {summary.DisplayName}",
                Theme.RedBricks,
                dashed: true,
                onClick: () => CreateRedBrick()),
        };
        tiles.AddRange(bricks.Select(brick => CreateOwnedRedBrickVirtualTile(section, mod, brick)));
        return tiles;
    }

    private IReadOnlyList<VirtualTilePanel.Tile> BuildRedBrickLibraryTiles(
        ModProjectService.ModSummary? activeSummary,
        NativeSuitModProject? activeMod,
        IReadOnlyList<ModProjectService.ModSummary> summaries)
    {
        const string section = "MODDED RED BRICKS";
        var library = GetRedBrickLibrary(summaries);
        _redBrickWorkspaceTitle!.Text = "RED BRICK LIBRARY";
        _redBrickWorkspaceSubtitle!.Text = activeSummary is null
            ? $"{library.Count} saved Red Brick{(library.Count == 1 ? "" : "s")}. Select a mod on Home to add one to a release."
            : $"{library.Count} saved Red Brick{(library.Count == 1 ? "" : "s")}. Choose one to add a copy to {activeSummary.DisplayName}.";

        var tiles = new List<VirtualTilePanel.Tile>();
        if (activeMod is not null && activeSummary is not null)
        {
            tiles.Add(CreateRedBrickVirtualTile(section, "+ Red Brick", $"create in {activeSummary.DisplayName}", Theme.RedBricks,
                dashed: true, onClick: () => CreateRedBrick()));
        }
        if (library.Count == 0)
        {
            tiles.Add(CreateRedBrickVirtualTile(section, "No saved Red Bricks", "create one in a selected mod, then reuse it here", Theme.RedBricks,
                dashed: true, onClick: activeMod is null ? () => SelectHomeWorkspaceSection(HomeWorkspaceSection.Mods) : () => CreateRedBrick()));
            return tiles;
        }

        tiles.AddRange(library.Select(item => CreateLibraryRedBrickVirtualTile(section, item, activeSummary, activeMod)));
        return tiles;
    }

    private IReadOnlyList<VirtualTilePanel.Tile> BuildBaseGameRedBrickTiles()
    {
        const string section = "BASE GAME";
        _redBrickWorkspaceTitle!.Text = "BASE-GAME RED BRICKS";
        var catalog = BaseGameRedBrickCatalogService.LoadCurrent();
        if (!catalog.IsAvailable)
        {
            _redBrickWorkspaceSubtitle!.Text = catalog.Error;
            return
            [
                CreateRedBrickVirtualTile(section, "Native data unavailable", "run a full game extraction to browse real Red Brick definitions", Theme.RedBricks, dashed: true),
            ];
        }

        _redBrickWorkspaceSubtitle!.Text = $"{catalog.Definitions.Count} definitions read from DA_RedBrickData_Main. Click one to inspect its native palette.";
        return catalog.Definitions.Select(definition =>
        {
            var captured = definition;
            return CreateRedBrickVirtualTile(
                section,
                TrimMiddle(captured.DisplayName, 26),
                $"{captured.PrimaryColourRow} | {captured.SecondaryColourRow} | {captured.TertiaryColourRow}",
                Theme.RedBricks,
                LoadNavigationIcon("RedBrickNB.png", new Size(56, 56)),
                () => ShowBaseGameRedBrickDetails(captured));
        }).ToArray();
    }

    private IReadOnlyList<VirtualTilePanel.Tile> BuildRedBrickIconTiles(
        ModProjectService.ModSummary? summary,
        NativeSuitModProject? mod)
    {
        const string section = "RED BRICK ICONS";
        _redBrickWorkspaceTitle!.Text = "RED BRICK ICONS";
        if (summary is null || mod is null)
        {
            _redBrickWorkspaceSubtitle!.Text = "Choose a mod to manage the Red Brick icon textures owned by that release.";
            return
            [
                CreateRedBrickVirtualTile(section, "Choose a mod", "icons are cooked into the selected mod", Theme.RedBricks,
                    dashed: true, onClick: () => SelectHomeWorkspaceSection(HomeWorkspaceSection.Mods)),
            ];
        }

        var icons = GetActiveModCookedIconChoices(mod);
        _redBrickWorkspaceSubtitle!.Text = icons.Count == 0
            ? $"{summary.DisplayName} has no cooked Red Brick icon textures yet."
            : $"{summary.DisplayName} - use a cooked Red Brick texture for a menu icon.";

        var tiles = new List<VirtualTilePanel.Tile>
        {
            CreateRedBrickVirtualTile(section, "+ Cook Red Brick icon", "import PNG | locked to the RedBrick profile", Theme.RedBricks,
                dashed: true, onClick: async () => await ImportRedBrickTextureAsync()),
        };
        if (icons.Count == 0)
        {
            tiles.Add(CreateRedBrickVirtualTile(section, "No Red Brick icons", "cook one here, then select it while creating a brick", Theme.RedBricks));
            return tiles;
        }

        tiles.AddRange(icons.Select(icon =>
        {
            var captured = icon;
            var texture = captured.Texture;
            return CreateRedBrickVirtualTile(
                section,
                TrimMiddle(texture.DisplayName, 26),
                $"{texture.CookWidth}x{texture.CookHeight} | RedBrick profile\n{TrimMiddle(texture.PackagePath, 38)}",
                Theme.RedBricks,
                LoadTextureThumbnail(texture.SourcePng),
                () => CopyText(texture.PackagePath, $"Copied Red Brick texture package path: {texture.PackagePath}"),
                menuFactory: () => CreateRedBrickIconMenu(mod, texture));
        }));
        return tiles;
    }

    private VirtualTilePanel.Tile CreateOwnedRedBrickVirtualTile(string section, NativeSuitModProject mod, ModRedBrickEntry brick)
    {
        var captured = brick;
        return CreateRedBrickVirtualTile(
            section,
            TrimMiddle(captured.DisplayName, 26),
            $"{captured.PrimaryColourRow} | {captured.SecondaryColourRow} | {captured.TertiaryColourRow}",
            captured.Enabled ? Theme.RedBricks : Theme.OnDarkMuted,
            LoadRedBrickIconPreview(captured, mod),
            () => EditRedBrick(mod, captured),
            menuFactory: () => CreateOwnedRedBrickMenu(mod, captured),
            toolTip: captured.BrickId);
    }

    private VirtualTilePanel.Tile CreateLibraryRedBrickVirtualTile(
        string section,
        RedBrickLibraryItem item,
        ModProjectService.ModSummary? activeSummary,
        NativeSuitModProject? activeMod)
    {
        var captured = item;
        var ownBrick = activeSummary is not null && string.Equals(activeSummary.Path, captured.ModPath, StringComparison.OrdinalIgnoreCase);
        var owner = ModService.LoadMod(captured.ModPath);
        return CreateRedBrickVirtualTile(
            section,
            ownBrick ? TrimMiddle(captured.Brick.DisplayName, 26) : "+ " + TrimMiddle(captured.Brick.DisplayName, 24),
            ownBrick ? $"{captured.ModName} | open in this mod" : $"{captured.ModName} | add a copy",
            Theme.RedBricks,
            LoadRedBrickIconPreview(captured.Brick, owner),
            () =>
            {
                if (ownBrick && activeMod is not null) EditRedBrick(activeMod, captured.Brick);
                else AddLibraryBrickToActiveMod(captured);
            },
            menuFactory: () => CreateLibraryRedBrickMenu(captured, ownBrick));
    }

    private static VirtualTilePanel.Tile CreateRedBrickVirtualTile(
        string section,
        string title,
        string subtitle,
        Color accent,
        Image? image = null,
        Action? onClick = null,
        bool dashed = false,
        Func<ContextMenuStrip?>? menuFactory = null,
        string? toolTip = null) => new()
        {
            Section = section,
            Title = title,
            Subtitle = subtitle,
            Accent = accent,
            Image = image,
            Dashed = dashed,
            OnClick = onClick,
            MenuFactory = menuFactory,
            ToolTip = toolTip,
        };

    private ContextMenuStrip CreateOwnedRedBrickMenu(NativeSuitModProject mod, ModRedBrickEntry brick)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit Red Brick...", null, (_, _) => EditRedBrick(mod, brick));
        menu.Items.Add(brick.Enabled ? "Disable" : "Enable", null, (_, _) =>
        {
            brick.Enabled = !brick.Enabled;
            ModService.SaveMod(mod);
            RefreshRedBrickWorkspace();
        });
        menu.Items.Add("Delete", null, (_, _) => DeleteRedBrick(mod, brick));
        return menu;
    }

    private ContextMenuStrip CreateLibraryRedBrickMenu(RedBrickLibraryItem item, bool ownBrick)
    {
        var menu = new ContextMenuStrip();
        if (!ownBrick)
        {
            menu.Items.Add("Add copy to current mod", null, (_, _) => AddLibraryBrickToActiveMod(item));
        }
        menu.Items.Add("Copy Brick ID", null, (_, _) => Clipboard.SetText(item.Brick.BrickId));
        menu.Items.Add("Copy /Game icon path", null, (_, _) => Clipboard.SetText(item.Brick.IconTexturePackagePath));
        return menu;
    }

    private ContextMenuStrip CreateRedBrickIconMenu(
        NativeSuitModProject mod,
        GeneratedTextureEntry texture)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy package path", null, (_, _) => CopyText(texture.PackagePath, $"Copied Red Brick texture package path: {texture.PackagePath}"));
        menu.Items.Add("Copy object path", null, (_, _) => CopyText(TextureObjectPath(texture), $"Copied Red Brick texture object path: {TextureObjectPath(texture)}"));
        menu.Items.Add("Copy source PNG path", null, (_, _) => CopyText(texture.SourcePng, $"Copied Red Brick texture source PNG path: {texture.SourcePng}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reimport image", null, (_, _) => ReimportRedBrickTexture(mod, texture));
        menu.Items.Add("Open output folder", null, (_, _) => OpenTextureOutputFolder(texture));
        return menu;
    }

    private async Task ImportRedBrickTextureAsync()
    {
        var (summary, mod) = ResolveHomeActiveMod(ModService.ListMods().ToList());
        if (summary is null || mod is null)
        {
            Dialog.Info(this, "Red Brick icons", "Select a mod on Home before cooking an icon for it.");
            return;
        }

        var projectRoot = AppSettings.Current.EffectiveProjectRoot();
        if (!await EnsureTextureCookTemplatesAsync(projectRoot))
        {
            return;
        }

        using var picker = new OpenFileDialog
        {
            Title = "Import Red Brick icon",
            Filter = "PNG images (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            using var image = Image.FromFile(picker.FileName);
            if (image.Width != 512 || image.Height != 512)
            {
                Dialog.Warn(this, "Red Brick icon", "Red Brick icons must be exactly 512x512 pixels.");
                return;
            }
        }
        catch
        {
            Dialog.Warn(this, "Red Brick icon", "The selected file could not be opened as a PNG.");
            return;
        }

        var settings = PromptForTextureImportSettings(
            Path.GetFileNameWithoutExtension(picker.FileName),
            projectRoot,
            lockedKind: "RedBrick");
        if (settings is null || string.IsNullOrWhiteSpace(settings.Value.Name))
        {
            return;
        }

        var requestedName = settings.Value.Name;
        var preset = settings.Value.Preset;
        var slotIndex = NextRedBrickTextureSlotIndex(mod);
        string packagePath;
        try
        {
            packagePath = TexturePackagePathFromUserName(
                preset.TemplateJson,
                requestedName,
                slotIndex,
                mod.ModId,
                mod.ModId,
                "RedBrick");
        }
        catch (Exception ex)
        {
            AppendLog("Red Brick icon path could not be generated: " + ex.Message);
            return;
        }

        if ((mod.RedBrickTextures ?? []).Any(texture =>
                texture.PackagePath.Equals(packagePath, StringComparison.OrdinalIgnoreCase)))
        {
            Dialog.Warn(this, "Red Brick icon", "That generated package path already belongs to this mod. Choose another name.");
            return;
        }

        var token = MakeSafeTextureToken(requestedName);
        var outputRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "RedBrickTextureImports",
            MakeSafePackageBaseName(mod.ModId),
            $"{token}_{slotIndex:00000}");
        string sourcePng;
        try
        {
            sourcePng = CopyTextureSourceIntoOutput(picker.FileName, outputRoot);
        }
        catch (Exception ex)
        {
            AppendLog("Red Brick icon source copy failed: " + ex.Message);
            return;
        }

        if (_redBrickPrimaryActionButton is not null) _redBrickPrimaryActionButton.Enabled = false;
        try
        {
            var cookedContentRoot = Path.Combine(outputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
            var result = await Task.Run(() => new TextureCookService(projectRoot).Cook(new TextureCookService.Request
            {
                SourceImagePath = sourcePng,
                TemplateJsonPath = preset.TemplateJson,
                OutputContentRoot = cookedContentRoot,
                OutputPackagePath = packagePath,
                NearestNeighborMips = false,
            }));
            foreach (var line in result.Log) AppendLog("  Red Brick icon: " + line);
            foreach (var warning in result.Warnings) AppendLog("  Red Brick icon warning: " + warning);
            if (!result.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
            {
                Dialog.Error(this, "Red Brick icon", result.Error?.Split('\n').FirstOrDefault() ?? "The icon could not be cooked.");
                return;
            }

            var entry = BuildTextureEntryFromSummary(
                outputRoot,
                sourcePng,
                preset.TemplateJson,
                DefaultTextureSourceRawRoot(projectRoot),
                packagePath,
                MakeSafePackageBaseName($"RedBrick_{token}_{slotIndex:00000}_P"),
                requestedName,
                "RedBrick",
                preset);
            mod.RedBrickTextures ??= new List<GeneratedTextureEntry>();
            mod.RedBrickTextures.RemoveAll(texture =>
                texture.PackagePath.Equals(entry.PackagePath, StringComparison.OrdinalIgnoreCase));
            mod.RedBrickTextures.Add(entry);
            ModService.SaveMod(mod);
            AppendLog($"Cooked Red Brick icon '{entry.DisplayName}' -> {entry.PackagePath}");
            _redBrickWorkspaceSection = RedBrickWorkspaceSection.Icons;
            UpdateRedBrickWorkspaceRailSelection();
            RefreshRedBrickWorkspace();
            RefreshHomeRedBrickTiles();
            Dialog.Info(this, "Red Brick icon ready",
                $"'{entry.DisplayName}' was cooked for {summary.DisplayName}. It is now listed in Red Bricks > Icons and can be selected from the Cooked Red Brick texture source when creating or editing a Red Brick.");
        }
        catch (Exception ex)
        {
            AppendLog("Red Brick icon cook failed: " + ex.Message);
            Dialog.Error(this, "Red Brick icon", ex.Message.Split('\n').FirstOrDefault() ?? "The icon could not be cooked.");
        }
        finally
        {
            if (_redBrickPrimaryActionButton is not null) _redBrickPrimaryActionButton.Enabled = true;
        }
    }

    private static int NextRedBrickTextureSlotIndex(NativeSuitModProject mod)
    {
        for (var index = 1; index <= 99999; index++)
        {
            var suffix = $"_{index:00000}_P";
            if (!(mod.RedBrickTextures ?? []).Any(texture =>
                    texture.PackageBaseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return index;
            }
        }

        return (mod.RedBrickTextures?.Count ?? 0) + 1;
    }

    private List<RedBrickLibraryItem> GetRedBrickLibrary(IEnumerable<ModProjectService.ModSummary> summaries)
    {
        var result = new List<RedBrickLibraryItem>();
        foreach (var summary in summaries)
        {
            var mod = ModService.LoadMod(summary.Path);
            if (mod is null) continue;
            foreach (var brick in mod.RedBricks ?? Enumerable.Empty<ModRedBrickEntry>())
            {
                result.Add(new RedBrickLibraryItem(summary.Path, summary.DisplayName, brick));
            }
        }
        return result
            .OrderBy(item => item.Brick.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ModName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<CookedRedBrickIconChoice> GetActiveModCookedIconChoices(NativeSuitModProject mod)
    {
        var choices = new List<CookedRedBrickIconChoice>();
        foreach (var texture in (mod.RedBrickTextures ?? [])
            .Where(texture => IsRedBrickTextureKind(texture.Kind) && !string.IsNullOrWhiteSpace(texture.PackagePath))
            .OrderBy(texture => texture.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(new CookedRedBrickIconChoice(mod.DisplayName, mod.ModId, texture));
        }
        return choices
            .GroupBy(choice => choice.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(choice => choice.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Texture.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Image? LoadRedBrickIconPreview(ModRedBrickEntry brick, NativeSuitModProject? mod = null)
    {
        var sourcePng = brick.IconSourcePng;
        if (string.IsNullOrWhiteSpace(sourcePng) && mod is not null && !string.IsNullOrWhiteSpace(brick.IconTexturePackagePath))
        {
            sourcePng = (mod.RedBrickTextures ?? [])
                .FirstOrDefault(texture => texture.PackagePath.Equals(brick.IconTexturePackagePath, StringComparison.OrdinalIgnoreCase))?
                .SourcePng ?? "";
        }
        if (!string.IsNullOrWhiteSpace(sourcePng) && File.Exists(sourcePng))
        {
            try
            {
                using var source = Image.FromFile(sourcePng);
                return new Bitmap(source, new Size(72, 72));
            }
            catch
            {
                // A missing thumbnail should not prevent the authoring tile from opening.
            }
        }
        return LoadNavigationIcon("RedBricks.png", new Size(60, 60));
    }

    private void CreateRedBrick(CookedRedBrickIconChoice? iconChoice = null)
    {
        var (summary, mod) = ResolveHomeActiveMod(ModService.ListMods().ToList());
        if (summary is null || mod is null)
        {
            Dialog.Info(this, "Red Bricks", "Create or select a mod from Home > Mods before adding a Red Brick.");
            return;
        }

        var entry = new ModRedBrickEntry
        {
            MenuOrder = (mod.RedBricks?.Count ?? 0) * 100 + 100,
            IconTexturePackagePath = iconChoice?.PackagePath ?? "",
        };
        using var dialog = new RedBrickEditorDialog(entry, RedBrickPalette.CurrentRows, GetActiveModCookedIconChoices(mod), isNew: true);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        mod.RedBricks ??= new List<ModRedBrickEntry>();
        mod.RedBricks.Add(entry);
        ModService.SaveMod(mod);
        AppendLog($"Added Red Brick '{entry.DisplayName}' ({entry.BrickId}) to mod '{mod.DisplayName}'.");
        RefreshRedBrickWorkspace();
        RefreshToyboxTiles();
    }

    private void CreateRedBrickFromBaseGame(BaseGameRedBrickDefinition definition, NativeSuitModProject? activeMod)
    {
        if (activeMod is null)
        {
            Dialog.Info(this, "Red Bricks", "Choose a mod on Home before adding a Red Brick to it.");
            return;
        }

        var entry = new ModRedBrickEntry
        {
            DisplayName = definition.DisplayName,
            BrickId = UniqueRedBrickId(activeMod, definition.Id),
            Description = "Tint palette based on the shipped " + definition.DisplayName + " Red Brick.",
            PrimaryColourRow = definition.PrimaryColourRow,
            SecondaryColourRow = definition.SecondaryColourRow,
            TertiaryColourRow = definition.TertiaryColourRow,
            MenuOrder = (activeMod.RedBricks?.Count ?? 0) * 100 + 100,
        };
        using var dialog = new RedBrickEditorDialog(entry, RedBrickPalette.CurrentRows, GetActiveModCookedIconChoices(activeMod), isNew: true);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        activeMod.RedBricks ??= new List<ModRedBrickEntry>();
        activeMod.RedBricks.Add(entry);
        ModService.SaveMod(activeMod);
        AppendLog($"Added Red Brick '{entry.DisplayName}' from the native {definition.DisplayName} tint definition.");
        _redBrickWorkspaceSection = RedBrickWorkspaceSection.ThisMod;
        UpdateRedBrickWorkspaceRailSelection();
        RefreshRedBrickWorkspace();
        RefreshToyboxTiles();
    }

    private void ShowBaseGameRedBrickDetails(BaseGameRedBrickDefinition definition)
    {
        Dialog.Info(this, definition.DisplayName,
            $"Native Red Brick definition\n\n" +
            $"ID: {definition.Id}\n" +
            $"Primary: {definition.PrimaryColourRow}\n" +
            $"Secondary: {definition.SecondaryColourRow}\n" +
            $"Tertiary: {definition.TertiaryColourRow}\n\n" +
            "This is read-only base-game data. Create a Red Brick from the This mod page to add one to your release.");
    }

    private void ReimportRedBrickTexture(NativeSuitModProject mod, GeneratedTextureEntry texture)
    {
        if (!ReimportGeneratedTextureSource(texture))
        {
            return;
        }

        ModService.SaveMod(mod);
        AppendLog($"Reimported Red Brick icon '{texture.DisplayName}' from its source PNG.");
        RefreshRedBrickWorkspace();
        RefreshToyboxTiles();
    }

    private void AddLibraryBrickToActiveMod(RedBrickLibraryItem source)
    {
        var (summary, target) = ResolveHomeActiveMod(ModService.ListMods().ToList());
        if (summary is null || target is null)
        {
            Dialog.Info(this, "Red Bricks", "Select the mod that should receive this Red Brick first.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(source.Brick.IconTexturePackagePath) &&
            !GetActiveModCookedIconChoices(target).Any(icon =>
                icon.PackagePath.Equals(source.Brick.IconTexturePackagePath, StringComparison.OrdinalIgnoreCase)))
        {
            Dialog.Warn(this, "Icon texture is not in this mod",
                "This Red Brick uses a cooked Red Brick texture supplied by another mod. Import the icon into this mod, then choose it while editing the copied Red Brick.");
            return;
        }

        var entry = CloneRedBrick(source.Brick);
        entry.BrickId = UniqueRedBrickId(target, entry.BrickId);
        entry.MenuOrder = (target.RedBricks?.Count ?? 0) * 100 + 100;
        target.RedBricks ??= new List<ModRedBrickEntry>();
        target.RedBricks.Add(entry);
        ModService.SaveMod(target);
        AppendLog($"Added a copy of Red Brick '{entry.DisplayName}' to '{target.DisplayName}' as '{entry.BrickId}'.");
        RefreshRedBrickWorkspace();
        RefreshToyboxTiles();
    }

    private static ModRedBrickEntry CloneRedBrick(ModRedBrickEntry source) => new()
    {
        BrickId = source.BrickId,
        DisplayName = source.DisplayName,
        Description = source.Description,
        IconSourcePng = source.IconSourcePng,
        IconTexturePackagePath = source.IconTexturePackagePath,
        PrimaryColourRow = source.PrimaryColourRow,
        SecondaryColourRow = source.SecondaryColourRow,
        TertiaryColourRow = source.TertiaryColourRow,
        EffectPreset = source.EffectPreset,
        Enabled = source.Enabled,
        UnlockedByDefault = source.UnlockedByDefault,
    };

    private static string UniqueRedBrickId(NativeSuitModProject mod, string candidate)
    {
        var safe = ModProjectService.DeriveModId(candidate);
        if (string.IsNullOrWhiteSpace(safe)) safe = "RedBrick";
        var existing = new HashSet<string>((mod.RedBricks ?? []).Select(brick => brick.BrickId), StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(safe)) return safe;
        for (var suffix = 2; ; suffix++)
        {
            var next = safe + suffix;
            if (!existing.Contains(next)) return next;
        }
    }

    private void EditRedBrick(NativeSuitModProject mod, ModRedBrickEntry brick)
    {
        using var dialog = new RedBrickEditorDialog(brick, RedBrickPalette.CurrentRows, GetActiveModCookedIconChoices(mod), isNew: false);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        ModService.SaveMod(mod);
        AppendLog($"Updated Red Brick '{brick.DisplayName}' in mod '{mod.DisplayName}'.");
        RefreshRedBrickWorkspace();
        RefreshToyboxTiles();
    }

    private void DeleteRedBrick(NativeSuitModProject mod, ModRedBrickEntry brick)
    {
        if (!Dialog.Confirm(this, $"Delete {brick.DisplayName}?", "This removes the Red Brick from this mod. The next build removes it from the release.", "Delete"))
        {
            return;
        }
        mod.RedBricks.Remove(brick);
        ModService.SaveMod(mod);
        AppendLog($"Removed Red Brick '{brick.DisplayName}' from mod '{mod.DisplayName}'.");
        RefreshRedBrickWorkspace();
        RefreshToyboxTiles();
    }
}

internal sealed record CookedRedBrickIconChoice(string ModName, string ModPath, GeneratedTextureEntry Texture)
{
    public string PackagePath => Texture.PackagePath;
    public override string ToString() => $"{Texture.DisplayName} ({Texture.CookWidth}x{Texture.CookHeight})";
}

internal sealed class RedBrickEditorDialog : Form
{
    private readonly ModRedBrickEntry _entry;
    private readonly IReadOnlyList<CookedRedBrickIconChoice> _cookedIcons;
    private readonly TextBox _name = new();
    private readonly TextBox _id = new();
    private readonly TextBox _description = new();
    private readonly ComboBox _iconMode = new();
    private readonly ComboBox _cookedIconPicker = new();
    private readonly TextBox _iconValue = new();
    private readonly Button _chooseIcon = new();
    private readonly ComboBox _primary = new();
    private readonly ComboBox _secondary = new();
    private readonly ComboBox _tertiary = new();
    private readonly CheckBox _unlocked = new();
    private CookedRedBrickIconChoice? _selectedCookedIcon;
    private string _sourceIconPng;
    private string _cookedIconPackagePath;
    private Control? _legacyIconPicker;

    public RedBrickEditorDialog(
        ModRedBrickEntry entry,
        IReadOnlyList<string> colours,
        IReadOnlyList<CookedRedBrickIconChoice> cookedIcons,
        bool isNew)
    {
        _entry = entry;
        _cookedIcons = cookedIcons;
        _sourceIconPng = entry.IconSourcePng;
        _cookedIconPackagePath = entry.IconTexturePackagePath;
        _selectedCookedIcon = cookedIcons.FirstOrDefault(icon =>
            icon.PackagePath.Equals(_cookedIconPackagePath, StringComparison.OrdinalIgnoreCase));
        Text = isNew ? "Create Red Brick" : "Edit Red Brick";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(780, 760);
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(20), BackColor = Theme.WindowBg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 162));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label { Dock = DockStyle.Fill, Text = isNew ? "Create Red Brick" : "Edit Red Brick", Font = Theme.Title, ForeColor = Theme.RedBricks, TextAlign = ContentAlignment.MiddleLeft };
        root.Controls.Add(title, 0, 0);

        var identity = CreateSection("IDENTITY", 3);
        AddField(identity.Grid, 0, "Display name", _name);
        AddField(identity.Grid, 1, "Brick ID", _id);
        AddField(identity.Grid, 2, "Description", _description, multiline: true);
        root.Controls.Add(identity.Panel, 0, 1);

        var icon = CreateSection("MENU ICON", 2);
        _iconMode.Items.AddRange(["Cooked Red Brick texture (from this mod)", "Legacy PNG icon (cook during build)"]);
        _iconMode.DropDownStyle = ComboBoxStyle.DropDownList;
        Theme.StyleDarkCombo(_iconMode);
        _iconMode.SelectedIndex = !string.IsNullOrWhiteSpace(_cookedIconPackagePath) || isNew ? 0 : 1;
        _iconMode.SelectedIndexChanged += (_, _) => UpdateIconMode();
        AddField(icon.Grid, 0, "Source", _iconMode);
        var iconHolder = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        _cookedIconPicker.Dock = DockStyle.Fill;
        _cookedIconPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        Theme.StyleDarkCombo(_cookedIconPicker);
        foreach (var cookedIcon in _cookedIcons)
        {
            _cookedIconPicker.Items.Add(cookedIcon);
        }
        _cookedIconPicker.SelectedItem = _selectedCookedIcon;
        _cookedIconPicker.SelectedIndexChanged += (_, _) =>
        {
            _selectedCookedIcon = _cookedIconPicker.SelectedItem as CookedRedBrickIconChoice;
            if (_selectedCookedIcon is not null) _cookedIconPackagePath = _selectedCookedIcon.PackagePath;
        };

        var legacyIconPicker = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        legacyIconPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        legacyIconPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        legacyIconPicker.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Theme.StyleDarkInput(_iconValue);
        _iconValue.ReadOnly = true;
        _iconValue.Dock = DockStyle.Fill;
        _iconValue.Margin = new Padding(0, 4, 8, 4);
        _chooseIcon.Text = "Browse...";
        Theme.StyleDarkButton(_chooseIcon);
        _chooseIcon.Dock = DockStyle.Fill;
        _chooseIcon.Margin = new Padding(0, 4, 0, 4);
        _chooseIcon.Click += (_, _) => ChooseIcon();
        legacyIconPicker.Controls.Add(_iconValue, 0, 0);
        legacyIconPicker.Controls.Add(_chooseIcon, 1, 0);
        _legacyIconPicker = legacyIconPicker;
        iconHolder.Controls.Add(legacyIconPicker);
        iconHolder.Controls.Add(_cookedIconPicker);
        AddField(icon.Grid, 1, "Icon", iconHolder);
        root.Controls.Add(icon.Panel, 0, 2);

        var palette = CreateSection("TINT PALETTE", 3);
        ConfigureColours(_primary, colours, entry.PrimaryColourRow);
        ConfigureColours(_secondary, colours, entry.SecondaryColourRow);
        ConfigureColours(_tertiary, colours, entry.TertiaryColourRow);
        AddField(palette.Grid, 0, "Primary", _primary);
        AddField(palette.Grid, 1, "Secondary", _secondary);
        AddField(palette.Grid, 2, "Tertiary", _tertiary);
        root.Controls.Add(palette.Panel, 0, 3);

        var availability = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBg, Padding = new Padding(14, 8, 14, 8) };
        availability.Paint += (_, e) =>
        {
            using var line = new Pen(Theme.LineSoft);
            e.Graphics.DrawRectangle(line, 0, 0, Math.Max(0, availability.Width - 1), Math.Max(0, availability.Height - 1));
        };
        _unlocked.Text = "Unlocked by default";
        _unlocked.Checked = entry.UnlockedByDefault;
        _unlocked.AutoSize = true;
        _unlocked.ForeColor = Theme.OnDark;
        _unlocked.Location = new Point(14, 13);
        availability.Controls.Add(_unlocked);
        root.Controls.Add(availability, 0, 4);

        _name.Text = entry.DisplayName;
        _id.Text = entry.BrickId;
        _description.Text = entry.Description;
        if (!isNew) _id.ReadOnly = true;
        _name.TextChanged += (_, _) =>
        {
            if (isNew) _id.Text = ModProjectService.DeriveModId(_name.Text);
        };
        UpdateIconMode();

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new Button { Text = "Cancel", Width = 106, Height = 32, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        var save = new Button { Text = isNew ? "Create Red Brick" : "Save changes", Width = 136, Height = 32 };
        StyleRedBrickButton(save);
        save.Click += (_, _) => Save();
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        root.Controls.Add(actions, 0, 5);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static (Panel Panel, TableLayoutPanel Grid) CreateSection(string title, int rows)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBg, Padding = new Padding(14, 8, 14, 10) };
        panel.Paint += (_, e) =>
        {
            using var line = new Pen(Theme.LineSoft);
            e.Graphics.DrawRectangle(line, 0, 0, Math.Max(0, panel.Width - 1), Math.Max(0, panel.Height - 1));
        };
        var heading = new Label { Dock = DockStyle.Top, Height = 21, Text = title, ForeColor = Theme.RedBricks, Font = Theme.Caption, TextAlign = ContentAlignment.MiddleLeft };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = rows, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < rows; row++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        panel.Controls.Add(grid);
        panel.Controls.Add(heading);
        return (panel, grid);
    }

    private static void AddField(TableLayoutPanel root, int row, string label, Control input, bool multiline = false)
    {
        var title = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.OnDarkMuted, Font = Theme.Caption };
        root.Controls.Add(title, 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 0, 4);
        if (input is TextBox box)
        {
            Theme.StyleDarkInput(box);
            box.Multiline = multiline;
        }
        else if (input is ComboBox combo)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            Theme.StyleDarkCombo(combo);
        }
        root.Controls.Add(input, 1, row);
    }

    private static void StyleRedBrickButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 119, 119);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(190, 57, 57);
        button.BackColor = Theme.RedBricks;
        button.ForeColor = Theme.SlateDark;
        button.Font = Theme.BodyStrong;
        button.Cursor = Cursors.Hand;
    }

    private void UpdateIconMode()
    {
        var cooked = _iconMode.SelectedIndex == 0;
        _cookedIconPicker.Visible = cooked;
        if (_legacyIconPicker is not null) _legacyIconPicker.Visible = !cooked;
        if (cooked && _selectedCookedIcon is null && _cookedIcons.Count > 0)
        {
            _cookedIconPicker.SelectedItem = _cookedIcons.FirstOrDefault(icon =>
                icon.PackagePath.Equals(_cookedIconPackagePath, StringComparison.OrdinalIgnoreCase)) ?? _cookedIcons.First();
        }

        _chooseIcon.Text = "Browse...";
        _iconValue.Text = _sourceIconPng;
    }

    private void ChooseIcon()
    {
        if (_iconMode.SelectedIndex != 1)
        {
            return;
        }

        using var open = new OpenFileDialog { Filter = "PNG images|*.png", Title = "Choose Red Brick icon" };
        if (open.ShowDialog(this) == DialogResult.OK)
        {
            _sourceIconPng = open.FileName;
            _cookedIconPackagePath = "";
            UpdateIconMode();
        }
    }

    private static void ConfigureColours(ComboBox box, IReadOnlyList<string> colours, string selected)
    {
        box.Items.AddRange(colours.Cast<object>().ToArray());
        box.SelectedItem = colours.FirstOrDefault(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? colours.FirstOrDefault();
    }

    private void Save()
    {
        var displayName = _name.Text.Trim();
        var brickId = ModProjectService.DeriveModId(_id.Text);
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(brickId))
        {
            MessageBox.Show(this, "Enter a display name and a valid Brick ID.", "Red Brick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_iconMode.SelectedIndex == 1)
        {
            var iconPath = _sourceIconPng.Trim();
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath) || !Path.GetExtension(iconPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Choose a readable 512x512 PNG icon.", "Red Brick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                using var icon = Image.FromFile(iconPath);
                if (icon.Width != 512 || icon.Height != 512)
                {
                    MessageBox.Show(this, "Red Brick icons must be exactly 512x512 pixels.", "Red Brick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch
            {
                MessageBox.Show(this, "The selected icon could not be opened as a PNG.", "Red Brick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _entry.IconTexturePackagePath = "";
            _entry.IconSourcePng = iconPath;
        }
        else
        {
            var package = _selectedCookedIcon?.PackagePath ?? _cookedIconPackagePath;
            if (string.IsNullOrWhiteSpace(package))
            {
                MessageBox.Show(this, "Choose a cooked Red Brick texture from this mod.", "Red Brick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _entry.IconTexturePackagePath = package;
            _entry.IconSourcePng = "";
        }

        _entry.DisplayName = displayName;
        _entry.BrickId = brickId;
        _entry.Description = _description.Text.Trim();
        _entry.PrimaryColourRow = _primary.SelectedItem?.ToString() ?? "BrightRed";
        _entry.SecondaryColourRow = _secondary.SelectedItem?.ToString() ?? "MediumBlue";
        _entry.TertiaryColourRow = _tertiary.SelectedItem?.ToString() ?? "BrightYellow";
        _entry.UnlockedByDefault = _unlocked.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
