using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// The tile browser shell: categories, tiles, filters, and the Home/Base surfaces.
/// </summary>
public sealed partial class MainForm
{
    private Control CreateToyboxPanel()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        // The extra height keeps the workspace labels and suit details readable.
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Theme.StyleTooltip(_toyboxToolTip); // readable dark tooltips app-wide

        // Command bar hosted in its designer-editable shell.
        var commandBar = new CommandBarControl { Dock = DockStyle.Fill };
        commandBar.HostContent(CreateToyboxHeader());
        outer.Controls.Add(commandBar, 0, 0);

        outer.Controls.Add(CreateWorkspaceFolderTabs(), 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(8, 6, 8, 8), BackColor = Theme.PanelBg };
        _toyboxBodyLayout = body;
        // A one-row TableLayout defaults that row to AutoSize. The inspector's preferred design
        // height could therefore extend behind the Diagnostics drawer and clip bottom actions.
        // Constrain the workspace to the height its parent actually gives it.
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.Controls.Add(body, 0, 2);

        // Category rail hosted in its designer-editable shell.
        var workflowRail = new WorkflowRailControl { Dock = DockStyle.Fill, BackColor = Theme.FrameLine, Padding = new Padding(0, 0, 1, 0) };
        workflowRail.HostContent(CreateCategoryRail());
        _suitWorkflowRail = workflowRail;
        body.Controls.Add(workflowRail, 0, 0);

        var homeRail = new WorkflowRailControl { Dock = DockStyle.Fill, Visible = false, BackColor = Theme.FrameLine, Padding = new Padding(0, 0, 1, 0) };
        homeRail.HostContent(CreateHomeCategoryRail());
        _homeWorkflowRail = homeRail;
        body.Controls.Add(homeRail, 0, 0);

        // Character panel - the "Your Character" designer-editable control owns the row flow;
        // MainForm wires the drop targets (drag a part/material onto the character).
        _yourCharacter.Dock = DockStyle.Fill;
        _yourCharacter.Margin = new Padding(3);
        WireToyboxCharacterDropTarget(_yourCharacter.SlotFlow);
        WireToyboxCharacterDropTarget(_yourCharacter);
        // The figure covers the panel now, so it has to accept the drops the rows used to.
        WireMinifigDropTarget(_yourCharacter.Diagram);
        _yourCharacter.ViewIn3DRequested += (_, _) => ViewCurrentSuitIn3D();
        _yourCharacter.Diagram.RegionActivated += SelectFirstSlotInRegion;
        _yourCharacter.Diagram.RegionContextRequested += ShowRegionContextMenu;
        _yourCharacter.Diagram.RegionDescriber = DescribeRegion;
        _yourCharacter.Diagram.SlotActivated += (component, slot) =>
        {
            var label = _characterSlots.FirstOrDefault(t =>
                t.Component.Equals(component, StringComparison.OrdinalIgnoreCase) && t.Slot == slot).Label ?? component;
            SelectToyboxSlot(label, component, slot);
        };
        body.Controls.Add(_yourCharacter, 1, 0);

        // Workspace: toolbar + tiles.
        var toyBox = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBg, Margin = new Padding(3) };
        var toyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        toyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        toyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        toyBox.Controls.Add(toyLayout);

        // Fixed ends, search takes the slack. A FlowLayoutPanel used to clip the search box off the
        // right edge whenever the window was narrow or a filter caption ran long.
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _toyboxCategoryCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        var categories = new List<object>
        {
            "Home", "Base", "Materials", "Faces", "Textures", "Parts", "Equipment", "Gliders", "Animations",
            "Build mod", "Review"
        };
        if (AppSettings.Current.ShowResearchTools)
        {
            categories.Add("Research");
        }
        _toyboxCategoryCombo.Items.AddRange(categories.ToArray());
        _toyboxCategoryCombo.SelectedIndex = 0;
        _toyboxCategoryCombo.Visible = false;
        _toyboxCategoryCombo.SelectedIndexChanged += (_, _) => HandleToyboxCategoryChanged();
        // The type list is now the filter button's "scope" section. This combo stays as the model
        // behind it - it is read and set from ~30 places - but is never shown.
        _toyboxTypeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _toyboxTypeCombo.Visible = false;
        _toyboxTypeCombo.SelectedIndexChanged += (_, _) => { SyncFilterScope(); RefreshToyboxTiles(); };

        _toyboxFilters.Height = 30;
        _toyboxFilters.Margin = new Padding(0, 4, 6, 0);
        _toyboxFilters.Visible = false;
        _toyboxFilters.FiltersChanged += (_, _) => { RefreshToyboxTiles(); };
        _toyboxFilters.ScopeChanged += value => SelectComboValue(_toyboxTypeCombo, value);

        _toyboxSearchText.Height = 30;
        _toyboxSearchText.Dock = DockStyle.Fill;
        _toyboxSearchText.MinimumSize = new Size(150, 30);
        _toyboxSearchText.MaximumSize = new Size(0, 30);
        _toyboxSearchText.Margin = new Padding(0, 4, 6, 0);
        _toyboxSearchText.PlaceholderText = "Search toybox…";
        _toyboxSearchDebounce.Tick += (_, _) => { _toyboxSearchDebounce.Stop(); RefreshToyboxTiles(); };
        _toyboxSearchText.TextChanged += (_, _) => { _toyboxSearchDebounce.Stop(); _toyboxSearchDebounce.Start(); };
        Theme.StyleGoldButton(_toyboxPrimaryActionButton);
        _toyboxPrimaryActionButton.Width = 160;
        _toyboxPrimaryActionButton.Height = 30;
        _toyboxPrimaryActionButton.Margin = new Padding(0, 4, 6, 0);
        _toyboxPrimaryActionButton.Click += (_, _) => RunPrimaryAction();
        var refreshTiles = new IconButton { Size = new Size(30, 30), Margin = new Padding(0, 4, 0, 0) };
        _toyboxToolTip.SetToolTip(refreshTiles, "Rebuild the tiles for the current view");
        refreshTiles.Click += (_, _) => RefreshToyboxTiles();
        // Hidden drivers: the nav rail sets the category, the filter button sets the type. Parented
        // so their events still fire.
        _toyboxCategoryCombo.Visible = false;
        toyBox.Controls.Add(_toyboxCategoryCombo);
        toyBox.Controls.Add(_toyboxTypeCombo);
        toolbar.Controls.Add(_toyboxFilters, 0, 0);
        toolbar.Controls.Add(_toyboxSearchText, 1, 0);
        toolbar.Controls.Add(_toyboxPrimaryActionButton, 2, 0);
        toolbar.Controls.Add(refreshTiles, 3, 0);
        toyLayout.Controls.Add(toolbar, 0, 0);

        _toyboxTileFlow.Dock = DockStyle.Fill;
        _toyboxTileFlow.FlowDirection = FlowDirection.LeftToRight;
        _toyboxTileFlow.WrapContents = true;
        _toyboxTileFlow.AutoScroll = true;
        _toyboxTileFlow.Padding = new Padding(4);
        toyLayout.Controls.Add(_toyboxTileFlow, 0, 1);

        // Virtualized parts grid shares the tile cell; visibility is toggled per category
        // (grid for the big Parts list, flow for the small action/note categories).
        _toyboxTileGrid.Dock = DockStyle.Fill;
        _toyboxTileGrid.Visible = false;
        toyLayout.Controls.Add(_toyboxTileGrid, 0, 1);

        _toyboxSelectionLabel.Dock = DockStyle.Fill;
        _toyboxSelectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _toyboxSelectionLabel.ForeColor = Theme.OnDarkMuted;
        toyLayout.Controls.Add(_toyboxSelectionLabel, 0, 2);

        var rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Margin = new Padding(3),
            BackColor = Theme.FrameLine,
        };
        rightSplit.Panel1.Padding = new Padding(1);
        rightSplit.Panel2.Padding = new Padding(1);
        _toyboxWorkspaceSplit = rightSplit;
        var rightSplitSet = false;
        rightSplit.SizeChanged += (_, _) =>
        {
            if (rightSplitSet || rightSplit.Width <= 700) return;
            var distance = rightSplit.Width - 330;
            var max = rightSplit.Width - rightSplit.Panel2MinSize - 1;
            if (distance > max) distance = max;
            if (distance < rightSplit.Panel1MinSize) distance = rightSplit.Panel1MinSize;
            try { rightSplit.SplitterDistance = distance; rightSplitSet = true; } catch { /* not sized yet */ }
        };
        var toybox = new ToyboxControl { Dock = DockStyle.Fill };
        toybox.HostContent(toyBox);
        rightSplit.Panel1.Controls.Add(toybox);
        rightSplit.Panel2.Controls.Add(CreateInspectorTabs());
        body.Controls.Add(rightSplit, 2, 0);

        // The viewer owns a dedicated full-width host. It no longer competes with the character
        // panel or inspector, and its renderer can be released as soon as the folder is left.
        var viewerHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(3),
            BackColor = Theme.WindowBg,
            Visible = false,
        };
        var viewerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(4),
        };
        viewerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        viewerHost.Controls.Add(viewerLayout);
        _viewerWorkspaceHost = viewerHost;
        _viewerHostLayout = viewerLayout;
        body.Controls.Add(viewerHost, 0, 0);
        body.SetColumnSpan(viewerHost, 3);

        SelectWorkspaceFolder(WorkspaceFolder.Home, refresh: false);
        PopulateToyboxSlots();
        PopulateToyboxTypes();
        UpdatePrimaryAction();
        ConfigureToyboxFilters();
        SelectToyboxSlot("Body", "CharacterMesh0", 0);
        RefreshToyboxTiles();
        RefreshInspector();
        return outer;
    }

    private Control CreateToyboxHeader()
    {
        // Keep the header solid so child controls blend into it.
        var header = new Panel { Dock = DockStyle.Fill, BackColor = HeaderGround };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var w = Math.Max(1, header.Width);
            var h = Math.Max(1, header.Height);
            using var line = new LinearGradientBrush(new Rectangle(0, h - 2, w, 2),
                Theme.Gold, Color.FromArgb(0, Theme.GoldDim), LinearGradientMode.Horizontal);
            g.FillRectangle(line, 0, h - 2, w, 2);
        };
        header.Resize += (_, _) => header.Invalidate();

        // --- brand -----------------------------------------------------------
        var brand = new Panel { Dock = DockStyle.Left, Width = 196, BackColor = Color.Transparent };
        _headerBrand = brand;
        RefreshHeaderWordmark();
        brand.Paint += (_, e) =>
        {
            var wordmark = _headerWordmark;
            if (wordmark is null)
            {
                return;
            }

            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            const int targetH = 34;
            var scale = targetH / (float)wordmark.Height;
            var dw = (int)(wordmark.Width * scale);
            var dest = new Rectangle(18, (brand.Height - targetH) / 2, dw, targetH);
                // Soft shadow: collapse RGB to black and keep a fraction of the alpha. Scaling
                // alpha alone would just ghost the yellow/red logo and fringe the edges.
            using (var shadow = new ImageAttributes())
            {
                var black = new ColorMatrix(new[]
                {
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0.40f, 0 },
                    new float[] { 0, 0, 0, 0, 1 },
                });
                shadow.SetColorMatrix(black);
                var soft = dest;
                soft.Offset(1, 2);
                g.DrawImage(wordmark, soft, 0, 0, wordmark.Width, wordmark.Height, GraphicsUnit.Pixel, shadow);
            }
            g.DrawImage(wordmark, dest);
        };
        header.Controls.Add(brand);

        // --- actions (right) --------------------------------------------------
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 540,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            WrapContents = false,
            Padding = new Padding(0, 23, 12, 0),
        };

        _menuButton.Text = "☰";
        _menuButton.Width = 42; _menuButton.Height = 34; _menuButton.Margin = new Padding(6, 0, 0, 0);
        Theme.StyleDarkButton(_menuButton);
        _menuButton.ForeColor = Theme.Gold;
        _menuButton.Click += (_, _) =>
        {
            var menu = BuildMainMenu();
            menu.Show(_menuButton, new Point(_menuButton.Width - menu.Width, _menuButton.Height));
        };

        _toyboxPackageButton.Text = "●  Build mod";
        _toyboxPackageButton.Width = 120; _toyboxPackageButton.Height = 34; _toyboxPackageButton.Margin = new Padding(6, 0, 0, 0);
        Theme.StyleGoldButton(_toyboxPackageButton);
        _toyboxPackageButton.Click += async (_, _) => await BuildModForCurrentSuitAsync();

        _toyboxSaveButton.Text = "Save suit";
        _toyboxSaveButton.Width = 90; _toyboxSaveButton.Height = 34; _toyboxSaveButton.Margin = new Padding(6, 0, 0, 0);
        Theme.StyleDarkButton(_toyboxSaveButton);
        _toyboxSaveButton.Enabled = false;
        _toyboxToolTip.SetToolTip(_toyboxSaveButton, "Save the current suit project");
        _toyboxSaveButton.Click += (_, _) => SaveCurrentSuit();

        // Kept alive off-bar: still referenced by other code paths.
        _settingsButton.Text = "Settings";
        _refreshGameAssetsButton.Text = "Refresh assets";

        // Status reads as a pill whose border picks up the state colour.
        _toyboxStatusChip.AutoSize = false;
        _toyboxStatusChip.Width = 124; _toyboxStatusChip.Height = 26; _toyboxStatusChip.Margin = new Padding(6, 4, 4, 0);
        _toyboxStatusChip.ForeColor = Theme.OnDarkMuted;
        _toyboxStatusChip.BackColor = Color.Transparent;
        _toyboxStatusChip.Font = Theme.Caption;
        _toyboxStatusChip.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.Clear(ControlGround.Resolve(_toyboxStatusChip));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, _toyboxStatusChip.Width - 1, _toyboxStatusChip.Height - 1);
            using (var path = Theme.RoundedRect(r, r.Height / 2))
            {
                using var fill = new SolidBrush(Color.FromArgb(38, 42, 50));
                g.FillPath(fill, path);
                using var pen = new Pen(Theme.Blend(_statusChipAccent, Theme.LineSoft, 0.5));
                g.DrawPath(pen, path);
            }
            using (var dot = new SolidBrush(_statusChipAccent))
            {
                g.FillEllipse(dot, 11, r.Height / 2 - 3, 6, 6);
            }
            TextRenderer.DrawText(g, _toyboxStatusChip.Text, Theme.Caption,
                new Rectangle(23, 0, r.Width - 27, r.Height), _toyboxStatusChip.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        right.Controls.Add(_menuButton);
        right.Controls.Add(_toyboxPackageButton);
        right.Controls.Add(_toyboxSaveButton);
        right.Controls.Add(_toyboxStatusChip);
        header.Controls.Add(right);

        // --- workspace context (fills the middle) -----------------------------
        var suit = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        _suitNameText.BorderStyle = BorderStyle.None;
        _suitNameText.BackColor = HeaderGround;
        _suitNameText.ForeColor = Theme.OnDark;
        _suitNameText.Font = Theme.Title;

        // The pencil marks the name as editable; both brighten together on hover/focus.
        _suitNamePencil = new Label
        {
            Text = "✎",
            AutoSize = false, Width = 16, Height = 20,
            Font = Theme.Body, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.IBeam,
        };
        _suitNamePencil.Click += (_, _) => _suitNameText.Focus();
        _tipsHeader.SetToolTip(_suitNamePencil, "Click the name to rename this suit");
        _tipsHeader.SetToolTip(_suitNameText, "Click to rename this suit");

        _suitNameText.Enter += (_, _) => RefreshSuitNameState();
        _suitNameText.Leave += (_, _) => RefreshSuitNameState();
        _suitNameText.MouseEnter += (_, _) => { _suitNameHover = true; RefreshSuitNameState(); };
        _suitNameText.MouseLeave += (_, _) => { _suitNameHover = false; RefreshSuitNameState(); };
        _suitNamePencil.MouseEnter += (_, _) => { _suitNameHover = true; RefreshSuitNameState(); };
        _suitNamePencil.MouseLeave += (_, _) => { _suitNameHover = false; RefreshSuitNameState(); };

        // The backing field still drives package derivation, but the header presents the
        // actual release mod that contains this suit instead of a raw folder name.
        _modFolderText.BorderStyle = BorderStyle.None;
        _modFolderText.BackColor = HeaderMetaGround;
        _modFolderText.ForeColor = Theme.OnDarkMuted;
        _modFolderText.Font = Theme.Caption;
        _modFolderText.Visible = false;

        _headerModCaption = new Label
        {
            Text = "CURRENT MOD", AutoSize = false, Height = 14,
            Font = Theme.Eyebrow, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
        };
        var modDot = new Label
        {
            Text = "●", AutoSize = false, Width = 12, Height = 22,
            Font = Theme.BodyStrong, ForeColor = Theme.Mods,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
        };
        _headerModValue = new Label
        {
            Text = "No mod selected", AutoSize = false, Height = 22,
            Font = Theme.Heading, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        _headerModDetail = new Label
        {
            AutoSize = false, Height = 15,
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        _tipsHeader.SetToolTip(_headerModValue, "Current release mod. Manage its suit list from Home.");

        _headerSuitCaption = new Label
        {
            Text = "CURRENT SUIT", AutoSize = false, Height = 14,
            Font = Theme.Eyebrow, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
        };

        _headerMetaLabel = new Label
        {
            AutoSize = false, Height = 16,
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        suit.Controls.Add(_headerModCaption);
        suit.Controls.Add(modDot);
        suit.Controls.Add(_headerModValue);
        suit.Controls.Add(_headerModDetail);
        suit.Controls.Add(_headerSuitCaption);
        suit.Controls.Add(_suitNameText);
        suit.Controls.Add(_suitNamePencil);
        suit.Controls.Add(_headerMetaLabel);

        void LayoutSuit()
        {
            var modWidth = Math.Clamp(suit.Width / 3, 126, 176);
            var suitLeft = modWidth + 35;

            _headerModCaption.Left = 18;
            _headerModCaption.Top = 10;
            _headerModCaption.Width = modWidth - 18;
            modDot.Left = 18;
            modDot.Top = 26;
            _headerModValue.Left = modDot.Right + 2;
            _headerModValue.Top = 23;
            _headerModValue.Width = Math.Max(60, modWidth - 14);
            _headerModDetail.Left = 18;
            _headerModDetail.Top = 49;
            _headerModDetail.Width = modWidth;

            _headerSuitCaption.Left = suitLeft;
            _headerSuitCaption.Top = 10;
            _headerSuitCaption.Width = Math.Max(70, suit.Width - suitLeft - 12);
            _suitNameText.Top = 23;
            _suitNameText.Left = suitLeft;
            _suitNameText.Width = Math.Max(80, suit.Width - suitLeft - 34);
            _suitNamePencil.Top = _suitNameText.Top + 2;
            _suitNamePencil.Left = _suitNameText.Right + 5;

            _headerMetaLabel.Top = 49;
            _headerMetaLabel.Left = suitLeft;
            _headerMetaLabel.Width = Math.Max(40, suit.Width - suitLeft - 10);
            RefreshSuitNameState();
        }
        suit.Resize += (_, _) => LayoutSuit();
        suit.HandleCreated += (_, _) => LayoutSuit();

        header.Controls.Add(suit);
        suit.BringToFront();

        return header;
    }

    private Control CreateCategoryRail()
    {
        var rail = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.PanelBg, Padding = new Padding(4, 6, 4, 6) };
        var cats = new[] { ("Home", "⌂"), ("Base", "◱"), ("Materials", "◈"), ("Faces", "◉"), ("Textures", "▣"), ("Parts", "◆"), ("Equipment", "★"), ("Gliders", "︾"), ("Animations", "➤"), ("Research", "⌕") };

        // Load PNGs per category instead of requiring every category to have one.
        // Home/Textures can fall back to glyphs without disabling the real bundled
        // art for Materials/Parts/Faces/etc.
        foreach (var (cat, glyph) in cats)
        {
            var button = RailButton(cat, glyph);
            _categoryRailButtons[cat] = button;
            if (cat.Equals("Research", StringComparison.OrdinalIgnoreCase))
            {
                _researchRailButton = button;
                button.Visible = AppSettings.Current.ShowResearchTools;
            }
            rail.Controls.Add(button);
        }
        UpdateCategoryRailSelection();
        return rail;
    }

    private Control CreateWorkspaceFolderTabs()
    {
        var strip = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.WindowBg,
            Padding = new Padding(28, 4, 8, 0),
        };
        strip.Paint += (_, e) =>
        {
            using var line = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(line, 0, strip.ClientSize.Height - 1, strip.ClientSize.Width, strip.ClientSize.Height - 1);
        };
        var tabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 0, 1),
        };
        strip.Controls.Add(tabs);

        tabs.Controls.Add(CreateWorkspaceFolderButton(WorkspaceFolder.Home, "Home", Theme.Gold, "Home.png"));
        tabs.Controls.Add(CreateWorkspaceFolderButton(WorkspaceFolder.Suits, "Suits", Theme.Base, "Suits.png"));
        tabs.Controls.Add(CreateWorkspaceFolderButton(WorkspaceFolder.Viewer, "3D viewer", Theme.Gliders, "3D.gif"));
        return strip;
    }

    private Button CreateWorkspaceFolderButton(WorkspaceFolder folder, string text, Color accent, string? iconAsset = null)
    {
        var button = new Button
        {
            Text = text,
              Width = folder switch
              {
                  WorkspaceFolder.Home => 112,
                  WorkspaceFolder.Suits => 110,
                  WorkspaceFolder.Viewer => 124,
                  _ => 100,
            },
            Height = 38,
            Margin = new Padding(folder == WorkspaceFolder.Home ? 0 : 5, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 1, BorderColor = Theme.LineSoft },
            BackColor = Theme.Slate,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.BodyStrong,
            Cursor = Cursors.Hand,
            Tag = accent,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Padding = iconAsset is null ? Padding.Empty : new Padding(9, 0, 7, 0),
        };
        if (string.Equals(iconAsset, "3D.gif", StringComparison.OrdinalIgnoreCase))
        {
            AttachAnimatedNavigationIcon(button, iconAsset!, new Size(17, 17));
        }
        else if (iconAsset is not null)
        {
            button.Image = LoadNavigationIcon(iconAsset, new Size(17, 17));
        }
        button.FlatAppearance.MouseOverBackColor = Theme.CardBg;
        button.Click += (_, _) => SelectWorkspaceFolder(folder);
        _workspaceFolderButtons[folder] = button;
        return button;
    }

    private static void AttachAnimatedNavigationIcon(Button button, string assetName, Size size)
    {
        var animation = EmbeddedAssets.LoadAnimated(assetName);
        if (animation is null)
        {
            button.Image = LoadNavigationIcon(assetName, size);
            return;
        }

        // PictureBox owns GIF playback; Button.Image only paints the first frame.
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Padding = new Padding(size.Width + 16, 0, 4, 0);
        var picture = new PictureBox
        {
            Image = animation,
            BackColor = Color.Transparent,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = size,
            TabStop = false,
        };
        void PlaceAnimation()
        {
            picture.Location = new Point(8, Math.Max(0, (button.ClientSize.Height - picture.Height) / 2));
        }
        PlaceAnimation();
        button.Resize += (_, _) => PlaceAnimation();
        picture.Click += (_, _) => button.PerformClick();
        button.Controls.Add(picture);
    }

    private Control CreateHomeCategoryRail()
    {
        var rail = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.PanelBg,
            Padding = new Padding(4, 6, 4, 6),
        };
        AddHomeRailButton(rail, HomeWorkspaceSection.Mods, "Mods", Theme.Mods, "Mods.png");
        AddHomeRailButton(rail, HomeWorkspaceSection.Suits, "Suits", Theme.Base, "Suits.png");
        AddHomeRailButton(rail, HomeWorkspaceSection.BuildMod, "Build mod", Theme.Equipment, "BuildMod.png");
        AddHomeRailButton(rail, HomeWorkspaceSection.Review, "Review", Theme.Research, "Review.png");
        UpdateHomeWorkspaceRailSelection();
        return rail;
    }

    private void AddHomeRailButton(FlowLayoutPanel rail, HomeWorkspaceSection section, string label, Color accent, string iconAsset)
    {
        var button = new Button
        {
            Text = label,
            Width = 88,
            Height = 56,
            Margin = new Padding(1, 1, 1, 3),
            Padding = new Padding(0, 3, 0, 3),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 1, BorderColor = Theme.FrameLine },
            Font = Theme.Caption,
            TextAlign = ContentAlignment.BottomCenter,
            ForeColor = accent,
            BackColor = Theme.PanelBg,
            Cursor = Cursors.Hand,
            Tag = accent,
            Image = LoadNavigationIcon(iconAsset, new Size(20, 20)),
            ImageAlign = ContentAlignment.TopCenter,
            TextImageRelation = TextImageRelation.ImageAboveText,
        };
        button.FlatAppearance.MouseOverBackColor = Theme.Tint(accent);
        button.Click += (_, _) => SelectHomeWorkspaceSection(section);
        _homeWorkspaceButtons[section] = button;
        rail.Controls.Add(button);
    }

    private static Bitmap? LoadNavigationIcon(string assetName, Size size)
    {
        using var source = EmbeddedAssets.Load(assetName);
        if (source is null)
        {
            return null;
        }

        var icon = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(icon))
        {
            // Bitmap's backing store starts black. Clear it explicitly so transparent PNG padding
            // stays transparent instead of becoming a little black tile behind the navigation art.
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(Point.Empty, size));
        }

        return icon;
    }

    private void SelectHomeWorkspaceSection(HomeWorkspaceSection section)
    {
        _homeWorkspaceSection = section;
        SelectWorkspaceFolder(WorkspaceFolder.Home, refresh: false);
        UpdateHomeWorkspaceRailSelection();
        RefreshToyboxTiles();
    }

    private static string HomeCategoryForSection(HomeWorkspaceSection section) => section switch
    {
        HomeWorkspaceSection.BuildMod => "Build mod",
        HomeWorkspaceSection.Review => "Review",
        _ => "Home",
    };

    private static bool IsHomeOnlyCategory(string category) =>
        category.Equals("Build mod", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("Review", StringComparison.OrdinalIgnoreCase);

    private void UpdateHomeWorkspaceRailSelection()
    {
        foreach (var (section, button) in _homeWorkspaceButtons)
        {
            var accent = button.Tag is Color color ? color : Theme.Inspector;
            var selected = section == _homeWorkspaceSection;
            button.BackColor = selected ? Theme.Tint(accent) : Theme.PanelBg;
            button.FlatAppearance.BorderSize = selected ? 1 : 0;
            button.FlatAppearance.BorderColor = accent;
        }
    }

    private void SelectWorkspaceFolder(WorkspaceFolder folder, bool refresh = true)
    {
        if (_toyboxBodyLayout is null || _toyboxWorkspaceSplit is null)
        {
            return;
        }

        _switchingWorkspaceFolder = true;
        _toyboxBodyLayout.SuspendLayout();
        try
        {
            _workspaceFolder = folder;
            var isHome = folder == WorkspaceFolder.Home;
            var isSuits = folder == WorkspaceFolder.Suits;
            var isViewer = folder == WorkspaceFolder.Viewer;
            var isDedicatedWorkspace = isViewer;

            if (_suitWorkflowRail is not null) _suitWorkflowRail.Visible = isSuits;
            if (_homeWorkflowRail is not null) _homeWorkflowRail.Visible = isHome;
            _yourCharacter.Visible = isSuits;
            if (_viewerWorkspaceHost is not null) _viewerWorkspaceHost.Visible = isViewer;
            _toyboxWorkspaceSplit.Visible = !isDedicatedWorkspace;

            // Reset every layout fact on every switch. Incremental span changes were the source
            // of Home progressively shrinking after selecting its subcategories.
            _toyboxBodyLayout.SetColumn(_toyboxWorkspaceSplit, isHome ? 1 : 2);
            _toyboxBodyLayout.SetColumnSpan(_toyboxWorkspaceSplit, isHome ? 2 : 1);
            _toyboxWorkspaceSplit.Panel2Collapsed = isHome;

            var selectedCategory = _toyboxCategoryCombo.SelectedItem?.ToString() ?? "Home";
            if (isHome)
            {
                var homeCategory = HomeCategoryForSection(_homeWorkspaceSection);
                if (!selectedCategory.Equals(homeCategory, StringComparison.OrdinalIgnoreCase))
                {
                    SelectComboValue(_toyboxCategoryCombo, homeCategory);
                }
            }
            else if (isSuits && IsHomeOnlyCategory(selectedCategory))
            {
                SelectComboValue(_toyboxCategoryCombo, "Home");
            }

            foreach (var (workspace, button) in _workspaceFolderButtons)
            {
                var accent = button.Tag is Color color ? color : Theme.Inspector;
                var selected = workspace == folder;
                button.BackColor = selected ? Theme.Tint(accent) : Theme.Slate;
                button.ForeColor = selected ? accent : Theme.OnDarkMuted;
                button.FlatAppearance.BorderColor = selected ? accent : Theme.LineSoft;
            }
            UpdateHomeWorkspaceRailSelection();
        }
        finally
        {
            _switchingWorkspaceFolder = false;
            _toyboxBodyLayout.ResumeLayout(performLayout: true);
            _toyboxBodyLayout.PerformLayout();
        }

        if (folder != WorkspaceFolder.Viewer)
        {
            HideViewerPanel();
        }
        else
        {
            ShowViewerPanel();
        }

        if (refresh && folder != WorkspaceFolder.Viewer)
        {
            RefreshToyboxTiles();
        }
    }

    private void HandleToyboxCategoryChanged()
    {
        var category = _toyboxCategoryCombo.SelectedItem?.ToString() ?? "Home";
        if (!_switchingWorkspaceFolder &&
            _workspaceFolder != WorkspaceFolder.Suits &&
            !category.Equals("Home", StringComparison.OrdinalIgnoreCase) &&
            !IsHomeOnlyCategory(category))
        {
            SelectWorkspaceFolder(WorkspaceFolder.Suits, refresh: false);
        }

        UpdateCategoryRailSelection();
        PopulateToyboxTypes();
        UpdatePrimaryAction();
        ConfigureToyboxFilters();
        SelectInspectorTabForCategory();
        RefreshToyboxTiles();
    }

    private void UpdateToyboxChips()
    {
        var hasBase = HasCurrentSuitBase();
        // The dot is drawn by the pill, so the text is just the label now.
        _toyboxStatusChip.Text = hasBase ? "base set" : "no base yet";
        _toyboxStatusChip.ForeColor = hasBase ? Color.FromArgb(191, 233, 216) : Theme.OnDarkMuted;
        _statusChipAccent = hasBase ? Theme.Good : Theme.Warn;
        _toyboxStatusChip.Invalidate();

        var hasOpenSuit = _currentProject is not null;
        SetHeaderCommandState(_toyboxSaveButton, hasOpenSuit, isPrimary: false,
            readyHint: "Save the current suit project",
            unavailableHint: "Create or open a suit before saving.");
        SetHeaderCommandState(_toyboxPackageButton, hasOpenSuit && hasBase, isPrimary: true,
            readyHint: "Build and install the current suit's mod",
            unavailableHint: "Set a visual base and gameplay donor before building a mod.");
        RefreshHeaderMeta();
    }

    /// <summary>A generated slot name is not a base. A visual source and a real playable donor are both required.</summary>
    private bool HasCurrentSuitBase() => BaseEligibilityService.Evaluate(_currentProject).IsReady;

    /// <summary>
    /// WinForms turns disabled button labels nearly black on this dark theme. Keep the command text
    /// readable and let the command itself explain the missing prerequisite instead.
    /// </summary>
    private void SetHeaderCommandState(Button button, bool available, bool isPrimary, string readyHint, string unavailableHint)
    {
        button.Enabled = true;
        if (available)
        {
            if (isPrimary)
            {
                Theme.StyleGoldButton(button);
            }
            else
            {
                Theme.StyleDarkButton(button);
                button.ForeColor = Theme.Gold;
            }
            _toyboxToolTip.SetToolTip(button, readyHint);
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Theme.SlateLight;
        button.FlatAppearance.MouseOverBackColor = Theme.Slate;
        button.FlatAppearance.MouseDownBackColor = Theme.Slate;
        button.BackColor = Theme.SlateDark;
        button.ForeColor = Theme.Gold;
        button.Cursor = Cursors.Default;
        _toyboxToolTip.SetToolTip(button, unavailableHint);
    }

    /// <summary>A one-line summary of what the selected base grants - most importantly
    /// whether it has a native glide visual (so gliders can be shown). This is the exact
    /// distinction that made gliders fail on civilian bases like ThomasWayne.</summary>
    private string BaseInheritanceSummary()
    {
        if (_currentProject is null)
        {
            return "";
        }
        try
        {
            var status = new AnimArchetypeGraftService().BaseGlideVisual(_currentProject, out var glide);
            return status switch
            {
                AnimArchetypeGraftService.GlideVisualStatus.Present =>
                    $"✓ Native glide visual (component '{glide}') — you can switch the glider look in the Gliders tab.",
                AnimArchetypeGraftService.GlideVisualStatus.Absent =>
                    "⚠ No native glide visual on this base — gliders can't be shown (civilian base). Use a Batman/Catwoman/Nightwing/Gordon base for gliders.",
                // Unknown: the base asset is gone (a pruned extract) or unreadable. Say that, rather
                // than claiming the base has no cape - a wrong "no" here reads as a real defect.
                _ => "? Base asset not readable — can't check the glide visual. Re-point the base if this persists.",
            };
        }
        catch
        {
            return "";
        }
    }

    private string NativeIdentityTileSubtitle()
    {
        var tag = _currentProject?.PawnTag?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(tag) ? "pawn tag ✗ not set" : TrimMiddle(tag, 22);
    }

    private void OpenBaseWizard()
    {
        // Prefer the catalog-driven character picker; fall back to manual file
        // browsing only if the user asks or the catalog isn't loaded.
        if (GameDataService.Instance.HasCatalog)
        {
            using var picker = new BaseCharacterPicker();
            if (picker.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            if (!picker.BrowseManuallyRequested && !string.IsNullOrWhiteSpace(picker.SelectedVisualPackage))
            {
                _ = ApplyBaseCharacterFromCatalog(picker.SelectedVisualPackage!);
                return;
            }
        }

        OpenBaseWizardManual();
    }

    /// <summary>
    /// Resolves the cutscene/DCMD siblings of a picked playable from the catalog
    /// (same folder, predictable naming), maps each /Game package to its extracted
    /// .uasset, fills the base fields, and rebuilds the stage.
    /// </summary>
    private async Task ApplyBaseCharacterFromCatalog(string playablePackage)
    {
        if (BaseEligibilityService.IsCutsceneVisualPackage(playablePackage))
        {
            await ApplyVisualCutsceneBaseFromCatalog(playablePackage);
            return;
        }

        var stem = playablePackage[(playablePackage.LastIndexOf('/') + 1)..];
        var baseStem = stem.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase)
            ? stem[..^"_Playable".Length]  // BP_Batman_1966
            : stem;

        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        string Disk(string pkg) => Path.Combine(extracted, pkg["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");

        var playableDisk = Disk(playablePackage);
        if (!File.Exists(playableDisk))
        {
            AppendLog($"Base character not extracted on disk: {playableDisk}. Extract that character's folder, or use 'Browse files…'.");
            return;
        }
        var visualSource = TemplateFromUasset(playableDisk, "visual-character", extracted);
        var folderDisk = Path.GetDirectoryName(playableDisk)!;

        // The cutscene/DCMD sibling naming isn't a clean swap - e.g.
        // BP_Batman_1966_Playable ↔ BP_Batman_1966_Default_Cutscene. Search the
        // folder for "<baseStem>*_Cutscene" (prefer the exact/Default one) so we
        // don't miss it and bail before staging.
        string? FindSibling(string suffix, string prefixReplace, string? prefixWith)
        {
            var baseWithoutPrefix = !string.IsNullOrEmpty(prefixReplace) &&
                                    baseStem.StartsWith(prefixReplace, StringComparison.OrdinalIgnoreCase)
                ? baseStem[prefixReplace.Length..]
                : baseStem;
            var wantPrefix = prefixWith is null ? baseStem : prefixWith + baseWithoutPrefix;
            var matches = Directory.Exists(folderDisk)
                ? Directory.EnumerateFiles(folderDisk, "*.uasset")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => n!.StartsWith(wantPrefix, StringComparison.OrdinalIgnoreCase) &&
                                n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n!.Length) // shortest = closest match
                    .ToList()
                : new List<string?>();
            var exact = matches.FirstOrDefault(n => n!.Equals(wantPrefix + suffix, StringComparison.OrdinalIgnoreCase));
            var chosen = exact ?? matches.FirstOrDefault(n => n!.Contains("Default", StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
            return chosen is null ? null : Path.Combine(folderDisk, chosen + ".uasset");
        }

        var cutsceneDisk = FindSibling("_Cutscene", "BP_", null);
        var dcmdDisk = FindSibling("_Playable", "BP_", "DA_DCMD_"); // DA_DCMD_<X>_Playable
        var visualCutsceneSource = cutsceneDisk is null
            ? null
            : TemplateFromUasset(cutsceneDisk, "visual-cutscene", extracted);

        _basePlayableText.Text = playableDisk;
        _baseCutsceneText.Text = cutsceneDisk ?? "";
        _baseDcmdText.Text = dcmdDisk ?? "";
        // Machinery check: heroes carry their own BP_CAT_Archetype (abilities/anim/equipment
        // family). Villains/NPCs don't - offer to INHERIT machinery from a chosen hero. The
        // base still supplies the visual (body mesh + parts).
        // Villain/NPC base: its body + movement setup live in its NPC PARENT class
        // (BP_NPC_Quest), so it can't be reparented into a working playable (the result is
        // invisible/uncontrollable). Instead, BUILD ON a donor hero's proven playable and
        // RESKIN it with the villain's identity materials - the game's characters are just a
        // shared minifig body + a material, so a Talia playable painted with Joker's materials
        // IS a playable Joker.
        (string BodyMi, string FaceMi) villainVisual = ("", "");
        var baseMachinery = AnimArchetypeGraftService.DetectDonor(playableDisk, extracted, UiMappings());
        if (baseMachinery is null || !baseMachinery.Valid)
        {
            AppendLog($"'{UnrealPathUtil.AssetName(playablePackage)}' is a villain/NPC — its body and movement live in its NPC class, so it cannot become a playable directly. Applying its appearance to the tested playable base instead.");

            // The runtime bridge uses TheBatman2025 as the proven NPC machinery donor. That does
            // not change this suit's own PawnTag, UI metadata, or generated package family.
            const string PingPongDonorPlayable = "/Game/Characters/Minifig/Batman/BP_Batman_TheBatman2025_Playable";
            var machineryDonor = PingPongDonorPlayable;
            var donorPlayableDisk = Path.Combine(extracted, machineryDonor["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(donorPlayableDisk))
            {
                AppendLog($"Machinery donor (TheBatman2025 playable) not extracted on disk: {donorPlayableDisk}. Extract /Game/Characters/Minifig/Batman/ and re-pick the base.");
                return;
            }
            AppendLog("Villain reskin donor = TheBatman2025 (the runtime always spawns through it, so the playable must be Batman-family; a mismatched donor like Talia goes invisible/glitched in gameplay).");

            // Capture the villain's identity materials, then switch the staged base to the
            // DONOR's playable + cutscene (both reskinned via the material assignments below).
            var villainFolder = playablePackage.Split('/') is { Length: >= 2 } segs ? segs[^2] : "";
            villainVisual = AnimArchetypeGraftService.ExtractCharacterMaterials(playableDisk, villainFolder, UiMappings());

            playableDisk = donorPlayableDisk;
            _basePlayableText.Text = donorPlayableDisk;
            cutsceneDisk = ResolveCutsceneSibling(machineryDonor) ?? donorPlayableDisk;
            _baseCutsceneText.Text = cutsceneDisk;

            // Use the DONOR's OWN DCMD as the template (cloned + retagged), NOT an empty one.
            // The runtime ping-pong bridge PATCHES the live donor DCMD from our generated DCMD by
            // copying Pawn/MenuActor/CinematicsActor/UIMetaData/EquipmentList/etc. If our generated
            // DCMD is a hollow shell (no template), those copies are EMPTY → the spawned pawn has no
            // body/abilities/input → invisible AND uncontrollable. That - not family mismatch - is
            // the real suit-two failure (the working suit-one has its DCMD template set). Cloning the
            // donor's DCMD gives the generated DCMD the real wiring the bridge needs to copy.
            var donorFolder = Path.GetDirectoryName(donorPlayableDisk)!;
            var donorStem = Path.GetFileNameWithoutExtension(donorPlayableDisk); // BP_Batman_TheBatman2025_Playable
            var donorDcmdName = "DA_DCMD_" + (donorStem.StartsWith("BP_", StringComparison.OrdinalIgnoreCase)
                ? donorStem["BP_".Length..]
                : donorStem);
            var donorDcmdDisk = Path.Combine(donorFolder, donorDcmdName + ".uasset");
            if (File.Exists(donorDcmdDisk))
            {
                _baseDcmdText.Text = donorDcmdDisk;
                AppendLog($"Villain reskin DCMD template = {donorDcmdName} (the donor's own — carries the Pawn/ability/equipment wiring the runtime bridge copies onto the live donor).");
            }
            else
            {
                _baseDcmdText.Text = "";
                AppendLog($"⚠ Donor DCMD not found on disk ({donorDcmdDisk}); the DCMD would be generated hollow → invisible/uncontrollable pawn. Extract /Game/Characters/Minifig/Batman/ and re-pick the base.");
            }

            AppendLog($"Villain reskin: building on {UnrealPathUtil.AssetName(machineryDonor)}'s working playable, painted with {UnrealPathUtil.AssetName(playablePackage)}'s look" +
                      (string.IsNullOrWhiteSpace(villainVisual.BodyMi) ? "" : $" · body {UnrealPathUtil.AssetName(villainVisual.BodyMi)}") +
                      (string.IsNullOrWhiteSpace(villainVisual.FaceMi) ? "" : $" · face {UnrealPathUtil.AssetName(villainVisual.FaceMi)}") + ".");
        }

        if (cutsceneDisk is null)
        {
            AppendLog($"note: no cutscene found for {baseStem} in {folderDisk}. Use 'Browse files…' to pick one, or the base can't be staged.");
        }
        else
        {
            AppendLog($"Base: {stem}  (cutscene {Path.GetFileNameWithoutExtension(cutsceneDisk)}{(dcmdDisk is null ? ", no DCMD" : ", DCMD " + Path.GetFileNameWithoutExtension(dcmdDisk))})");
        }

        if (!await UseAsBase())
        {
            return;
        }

        if (_currentProject is not null)
        {
            RecordBaseProfile(visualSource, visualCutsceneSource, _currentProject.PlayableTemplate);
        }
        // Reskin: paint the donor's working playable + cutscene with the villain's body + face
        // materials (context "both" so gameplay AND cutscenes look like the villain).
        if (_currentProject is not null && (!string.IsNullOrWhiteSpace(villainVisual.BodyMi) || !string.IsNullOrWhiteSpace(villainVisual.FaceMi)))
        {
            // Clear any prior body/face reskin assignments first so re-basing REPLACES them instead
            // of stacking duplicates (a re-base was adding a 2nd body+face pair each time).
            _currentProject.MaterialAssignments.RemoveAll(m =>
                m.Slot == 0 &&
                (m.Component.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) ||
                 m.Component.Equals("Face", StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(villainVisual.BodyMi))
            {
                _currentProject.MaterialAssignments.Add(new SavedMaterialAssignment { Component = "CharacterMesh0", Slot = 0, MiPackagePath = villainVisual.BodyMi, Context = "both" });
            }
            if (!string.IsNullOrWhiteSpace(villainVisual.FaceMi))
            {
                _currentProject.MaterialAssignments.Add(new SavedMaterialAssignment { Component = "Face", Slot = 0, MiPackagePath = villainVisual.FaceMi, Context = "both" });
            }
            try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
            ApplySavedMaterials(_currentProject, logIfNone: false);
            AppendLog("Applied the villain's body + face materials to the donor playable.");
        }

        // Auto-transplant the villain's own visual attachment parts (hair/hats/accessories) onto the
        // donor playable AND auto-hide the donor's parts the villain doesn't have (e.g. Batman's cape
        // on a capeless Joker) so it matches the villain exactly. The reskin materials above cover the
        // shared body+face; unique parts like Joker's hair are separate SCS components, pulled from the
        // part index and grafted declaratively. UseAsBase already staged the clean donor base, so add
        // the parts + removal rules and rebuild ONCE so both apply together.
        if (_currentProject is not null && (baseMachinery is null || !baseMachinery.Valid) && _partIndex is not null)
        {
            var villainIndexParts = _partIndex.Parts
                .Where(p => p.SourcePackagePath.Equals(playablePackage, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var villainParts = villainIndexParts
                .Where(IsTransplantableVillainPart)
                .GroupBy(p => p.Slot, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            foreach (var vp in villainParts)
            {
                UpsertPartGraft(vp.Slot, false, vp, vp);
            }
            if (villainParts.Count > 0)
            {
                AppendLog($"Auto-transplanting {villainParts.Count} of the villain's part(s) so it matches exactly: " +
                          string.Join(", ", villainParts.Select(p => $"{p.Slot}={p.MeshObjectName}")) + ".");
            }

            // Hide the donor's cosmetic parts whose visual KIND the villain doesn't have. KEEP: core
            // shared-minifig components (body/head/face/limbs/root), any kind the villain DOES have,
            // and the ACTIVE glider component (gliders are swapped separately - a gliding cape stays).
            var hidden = new List<string>();
            try
            {
                var villainKinds = villainIndexParts
                    .Select(p => VisualKindOf(p.Slot))
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var protectedGlider = ActiveGliderVisualComponent(_currentProject);
                var donorComponents = new ComponentRemoveService(_projectRootText.Text.Trim())
                    .ListScsComponentNames(_currentProject.SlotId, _currentProject.TargetPackages.Playable, "");
                foreach (var comp in donorComponents)
                {
                    var kind = VisualKindOf(comp);
                    if (IsCoreKeepComponent(comp, kind)) continue;          // body/head/face/limbs/root
                    if (villainKinds.Contains(kind)) continue;             // villain has this kind
                    if (!string.IsNullOrWhiteSpace(protectedGlider) &&
                        protectedGlider.Equals(comp, StringComparison.OrdinalIgnoreCase)) continue; // active glider
                    var key = ToyboxSlotKey(comp, 0);
                    if (_currentProject.Requirements.Any(r =>
                            r.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
                            r.TargetComponent.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    _currentProject.Requirements.Add(new NativeSuitRequirement
                    {
                        Id = $"remove-{comp}-0".Replace(' ', '-').ToLowerInvariant(),
                        Kind = "remove-component",
                        SourcePackage = _currentProject.TargetPackages.Playable,
                        TargetComponent = key,
                        Notes = $"Auto-hidden on base select: {UnrealPathUtil.AssetName(playablePackage)} has no '{kind}' part."
                    });
                    hidden.Add(comp);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  ⚠ auto-hide donor parts skipped: {ex.Message}");
            }
            if (hidden.Count > 0)
            {
                AppendLog($"Auto-hiding {hidden.Count} donor part(s) the character doesn't have: {string.Join(", ", hidden)} (gliders are kept — swap them in the Gliders tab).");
            }

            if (villainParts.Count > 0 || hidden.Count > 0)
            {
                try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
                AppendLog("Rebuilding to apply the villain's parts + hide donor-only parts…");
                await RebuildGraftStageFromDeclarativeAsync();
            }
            else
            {
                AppendLog("No extra villain parts to transplant and no donor-only parts to hide — materials cover the look.");
            }
        }

        var fam = GameDataService.Instance.FamilyForBasePath(playablePackage)?.Name ?? "unknown family";
        RecordChange("Base", _suitNameText.Text.Trim(), $"{UnrealPathUtil.AssetName(playablePackage)} ({fam})");
        UpdateToyboxChips();
        SelectComboValue(_toyboxCategoryCombo, "Base");
        _session.RaiseChanged();
    }

    private async Task ApplyVisualCutsceneBaseFromCatalog(string visualCutscenePackage)
    {
        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        var visualDisk = PackageToExtractedUasset(visualCutscenePackage, extracted);
        var visual = TemplateFromUasset(visualDisk, "visual-cutscene", extracted);
        if (visual is null)
        {
            AppendLog($"Visual cutscene is not extracted on disk: {visualDisk}. Extract that character folder, then try again.");
            return;
        }

        var recommendedGameplay = TemplateFromUasset(
            FindPlayableSiblingForVisual(visualDisk) ?? "",
            "playable",
            extracted);
        var recommendedPackage = IsEligibleGameplayDonor(recommendedGameplay, extracted, out _)
            ? recommendedGameplay!.PackagePath
            : null;
        if (!string.IsNullOrWhiteSpace(recommendedPackage))
        {
            AppendLog($"Recommended gameplay donor for '{visual.Stem}': {recommendedGameplay!.Stem}. Choose it or select a different playable.");
        }
        else
        {
            AppendLog($"Visual source '{visual.Stem}' needs a separate gameplay donor.");
        }

        // A cutscene visual never silently chooses machinery. Authors can deliberately
        // combine any visual character with any eligible playable donor.
        var donorPackage = PromptForMachineryDonor(recommendedPackage);
        if (string.IsNullOrWhiteSpace(donorPackage))
        {
            AppendLog("Visual base not staged. Pick a gameplay donor to provide movement, equipment, and runtime behavior.");
            return;
        }
        var gameplay = TemplateFromUasset(PackageToExtractedUasset(donorPackage, extracted), "playable", extracted);
        if (!IsEligibleGameplayDonor(gameplay, extracted, out var donorDetail))
        {
            AppendLog($"The selected gameplay donor cannot be used: {donorDetail}");
            return;
        }

        var dcmdDisk = FindDcmdSiblingForPlayable(gameplay!.Uasset);
        _basePlayableText.Text = gameplay.Uasset;
        _baseCutsceneText.Text = visual.Uasset;
        _baseDcmdText.Text = dcmdDisk ?? "";

        if (!await UseAsBase())
        {
            return;
        }

        RecordBaseProfile(visual, visual, gameplay);
        ApplyVisualSourceMaterials(visual);
        await ApplyVisualAttachmentsToGameplayDonorAsync(visual.PackagePath);

        var visualFamily = _currentProject?.BaseProfile?.VisualFamily;
        var donorFamily = _currentProject?.BaseProfile?.GameplayFamily;
        RecordChange("Base", _suitNameText.Text.Trim(),
            $"visual {visual.Stem} ({(string.IsNullOrWhiteSpace(visualFamily) ? "unknown" : visualFamily)}) + gameplay {gameplay.Stem} ({(string.IsNullOrWhiteSpace(donorFamily) ? "unknown" : donorFamily)})");
        AppendLog($"Visual base = {visual.Stem}; gameplay donor = {gameplay.Stem}. The donor supplies movement, equipment, and runtime behavior.");
        UpdateToyboxChips();
        SelectComboValue(_toyboxCategoryCombo, "Base");
        _session.RaiseChanged();
    }

    private static string PackageToExtractedUasset(string packagePath, string extractedRoot)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        return package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(extractedRoot, package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset")
            : "";
    }

    private static string? FindPlayableSiblingForVisual(string visualUasset)
    {
        var folder = Path.GetDirectoryName(visualUasset);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var visualStem = Path.GetFileNameWithoutExtension(visualUasset);
        var key = BaseEligibilityService.CharacterStem(visualStem);
        var exact = "BP_" + key + "_Playable";
        return Directory.EnumerateFiles(folder, "*.uasset")
            .OrderBy(path => Path.GetFileNameWithoutExtension(path).Equals(exact, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .FirstOrDefault(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase) &&
                       BaseEligibilityService.CharacterStem(name).Equals(key, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string? FindDcmdSiblingForPlayable(string playableUasset)
    {
        var folder = Path.GetDirectoryName(playableUasset);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var playableStem = Path.GetFileNameWithoutExtension(playableUasset);
        var dcmdStem = "DA_DCMD_" + (playableStem.StartsWith("BP_", StringComparison.OrdinalIgnoreCase)
            ? playableStem["BP_".Length..]
            : playableStem);
        var exact = Path.Combine(folder, dcmdStem + ".uasset");
        if (File.Exists(exact))
        {
            return exact;
        }

        return Directory.EnumerateFiles(folder, "*.uasset")
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                .Equals(dcmdStem, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsEligibleGameplayDonor(TemplateRecord? donor, string contentRoot, out string detail)
    {
        if (donor is null || !BaseEligibilityService.IsGameplayDonorPackage(donor.PackagePath))
        {
            detail = "it is not a _Playable character Blueprint";
            return false;
        }

        var detected = AnimArchetypeGraftService.DetectDonor(donor.Uasset, contentRoot, UiMappings());
        if (detected is null || !detected.Valid)
        {
            detail = "it has no readable runtime archetype";
            return false;
        }

        detail = $"{(string.IsNullOrWhiteSpace(detected.Family) ? "playable" : detected.Family)} runtime family";
        return true;
    }

    private void RecordBaseProfile(TemplateRecord? visualSource, TemplateRecord? visualCutsceneSource, TemplateRecord? gameplayDonor)
    {
        if (_currentProject is null || gameplayDonor is null)
        {
            return;
        }

        var visual = visualCutsceneSource ?? visualSource ?? _currentProject.CutsceneTemplate;
        if (visual is null)
        {
            return;
        }

        _currentProject.VisualSourceTemplate = visualSource ?? visual;
        _currentProject.VisualCutsceneSourceTemplate = visualCutsceneSource ??
            (BaseEligibilityService.IsCutsceneVisualPackage(visual.PackagePath) ? visual : null);
        var profile = BaseEligibilityService.CreateProfile(visual.PackagePath, gameplayDonor.PackagePath);
        var detected = AnimArchetypeGraftService.DetectDonor(
            gameplayDonor.Uasset,
            AppSettings.Current.EffectiveExtractedContentRoot(),
            UiMappings());
        if (detected is { Valid: true } && !string.IsNullOrWhiteSpace(detected.Family))
        {
            profile.GameplayFamily = detected.Family;
        }
        _currentProject.BaseProfile = profile;
        try
        {
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        }
        catch
        {
            // The stage remains usable; the next normal save will persist the profile.
        }
    }

    private void ApplyVisualSourceMaterials(TemplateRecord visualSource)
    {
        if (_currentProject is null)
        {
            return;
        }

        var folder = visualSource.PackagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse().Skip(1).FirstOrDefault() ?? "";
        var (bodyMi, faceMi) = AnimArchetypeGraftService.ExtractCharacterMaterials(visualSource.Uasset, folder, UiMappings());
        if (string.IsNullOrWhiteSpace(bodyMi) && string.IsNullOrWhiteSpace(faceMi))
        {
            AppendLog($"No body or face material override was found on visual source {visualSource.Stem}; keeping the donor materials.");
            return;
        }

        _currentProject.MaterialAssignments.RemoveAll(m =>
            m.Slot == 0 &&
            (m.Component.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) ||
             m.Component.Equals("Face", StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(bodyMi))
        {
            _currentProject.MaterialAssignments.Add(new SavedMaterialAssignment { Component = "CharacterMesh0", Slot = 0, MiPackagePath = bodyMi, Context = "both" });
        }
        if (!string.IsNullOrWhiteSpace(faceMi))
        {
            _currentProject.MaterialAssignments.Add(new SavedMaterialAssignment { Component = "Face", Slot = 0, MiPackagePath = faceMi, Context = "both" });
        }

        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
        ApplySavedMaterials(_currentProject, logIfNone: false);
        AppendLog("Applied the visual source's body and face materials.");
    }

    private async Task ApplyVisualAttachmentsToGameplayDonorAsync(string visualSourcePackage)
    {
        if (_currentProject is null)
        {
            return;
        }
        if (!await EnsurePartIndexAsync())
        {
            AppendLog("Visual attachments were skipped because the part index could not be built.");
            return;
        }

        var partIndex = _partIndex;
        if (partIndex is null)
        {
            return;
        }

        var sourceParts = partIndex.Parts
            .Where(p => p.SourcePackagePath.Equals(visualSourcePackage, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var attachments = sourceParts
            .Where(IsTransplantableVillainPart)
            .GroupBy(OccupancyGroupOf, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        foreach (var source in attachments)
        {
            var playable = source.Context.Equals("playable", StringComparison.OrdinalIgnoreCase)
                ? source
                : FindExactMeshCounterpartPart(source, "playable") ?? source;
            var cutscene = source.Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase)
                ? source
                : FindExactMeshCounterpartPart(source, "cutscene") ?? source;
            UpsertPartGraft(source.Slot, false, playable, cutscene);
        }

        var sourceKinds = sourceParts
            .Select(p => VisualKindOf(p.Slot))
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = new List<string>();
        try
        {
            if (attachments.Any(part => OccupancyGroupOf(part).StartsWith("head.", StringComparison.OrdinalIgnoreCase)) &&
                EnsureVisualHeadAttachmentHidesDonorHead(_currentProject))
            {
                hidden.Add("Head");
            }

            var protectedGlider = ActiveGliderVisualComponent(_currentProject);
            var donorComponents = new ComponentRemoveService(_projectRootText.Text.Trim())
                .ListScsComponentNames(_currentProject.SlotId, _currentProject.TargetPackages.Playable, "");
            foreach (var component in donorComponents)
            {
                var kind = VisualKindOf(component);
                if (IsCoreKeepComponent(component, kind) || sourceKinds.Contains(kind) ||
                    (!string.IsNullOrWhiteSpace(protectedGlider) && protectedGlider.Equals(component, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var key = ToyboxSlotKey(component, 0);
                if (_currentProject.Requirements.Any(r =>
                        r.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
                        r.TargetComponent.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _currentProject.Requirements.Add(new NativeSuitRequirement
                {
                    Id = $"remove-{component}-0".Replace(' ', '-').ToLowerInvariant(),
                    Kind = "remove-component",
                    SourcePackage = _currentProject.TargetPackages.Playable,
                    TargetComponent = key,
                    Notes = $"Auto-hidden on visual-base select: {UnrealPathUtil.AssetName(visualSourcePackage)} has no '{kind}' part."
                });
                hidden.Add(component);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  visual attachment cleanup skipped: {ex.Message}");
        }

        if (attachments.Count == 0 && hidden.Count == 0)
        {
            return;
        }

        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
        AppendLog($"Applied {attachments.Count} visual attachment(s) and hid {hidden.Count} donor-only part(s).");
        await RebuildGraftStageFromDeclarativeAsync();
    }

    private async void OpenBaseWizardManual()
    {
        using var wiz = new BaseWizard(
            _suitNameText.Text.Trim(),
            _modFolderText.Text.Trim(),
            _basePlayableText.Text.Trim(),
            _baseCutsceneText.Text.Trim(),
            _baseDcmdText.Text.Trim());
        if (wiz.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        _suitNameText.Text = wiz.SuitName;
        _modFolderText.Text = wiz.ModFolder;
        _basePlayableText.Text = wiz.PlayablePath;
        _baseCutsceneText.Text = wiz.CutscenePath;
        _baseDcmdText.Text = wiz.DcmdPath;
        if (!await UseAsBase())
        {
            return;
        }
        var fam = GameDataService.Instance.FamilyForBasePath(wiz.PlayablePath)?.Name ?? "unknown family";
        RecordChange("Base", wiz.SuitName, $"{System.IO.Path.GetFileName(wiz.PlayablePath)} ({fam})");
        UpdateToyboxChips();
        _session.RaiseChanged();
    }

    private void SelectInspectorTabForCategory()
    {
        var research = _toyboxCategoryCombo.SelectedItem?.ToString()
            ?.Equals("Research", StringComparison.OrdinalIgnoreCase) == true &&
            AppSettings.Current.ShowResearchTools &&
            _inspectorTabs.ContainsTab(ResearchTabName);
        if (_inspectorTabs.Count > 0)
        {
            if (research)
            {
                _inspectorTabs.SelectTab(ResearchTabName);
            }
            else if (string.IsNullOrWhiteSpace(_inspectorTabs.SelectedName) ||
                     string.Equals(_inspectorTabs.SelectedName, ResearchTabName, StringComparison.OrdinalIgnoreCase))
            {
                // Preserve the user's Notes tab while moving around normal authoring categories.
                // Only Research owns the inspector while its dedicated workspace is selected.
                _inspectorTabs.SelectTab(SuitTabName);
            }
        }
    }

    private void WireToyboxCharacterDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
        {
            var payload = TryGetToyboxDragPayload(e.Data);
            e.Effect = payload is not null &&
                       payload.Kind.Equals("part", StringComparison.OrdinalIgnoreCase) &&
                       payload.Part is not null
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        };
        control.DragOver += (_, e) =>
        {
            var payload = TryGetToyboxDragPayload(e.Data);
            e.Effect = payload is not null &&
                       payload.Kind.Equals("part", StringComparison.OrdinalIgnoreCase) &&
                       payload.Part is not null
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        };
        control.DragDrop += async (_, e) =>
        {
            var payload = TryGetToyboxDragPayload(e.Data);
            if (payload is null ||
                !payload.Kind.Equals("part", StringComparison.OrdinalIgnoreCase) ||
                payload.Part is null)
            {
                return;
            }

            await ApplyToyboxPartDropToCharacterAsync(payload.Part);
        };
    }

    private static ToyboxDragPayload? TryGetToyboxDragPayload(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(typeof(ToyboxDragPayload)))
        {
            return null;
        }

        return data.GetData(typeof(ToyboxDragPayload)) as ToyboxDragPayload;
    }

    private async Task ApplyToyboxDropAsync(ToyboxDragPayload payload, string label, string component, int slot)
    {
        SelectToyboxSlot(label, component, slot);

        if (payload.Kind.Equals("material", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(payload.MaterialPath))
        {
            if (payload.FaceOnly && !component.Contains("face", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Face material drop refused: '{payload.MaterialPath}' belongs on the Face component, not {component}.");
                Dialog.Warn(this, "Face material", "Face materials can only be applied to the Face component. Drop this tile on the Face row or use Apply to Face from its right-click menu.");
                return;
            }
            if (payload.FaceOnly && !CanApplyFaceMaterial(payload.MaterialPath, component, slot, confirmUnknown: true))
            {
                return;
            }
            AppendLog($"Dropped material {payload.MaterialPath} onto {component} slot {slot}.");
            ApplyToyboxMaterial(payload.MaterialPath);
            RefreshInspector();
            return;
        }

        if (payload.Kind.Equals("part", StringComparison.OrdinalIgnoreCase) && payload.Part is not null)
        {
            SelectToyboxPart(payload.Part);
            if (!payload.Part.Slot.Equals(component, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Dropped {payload.Part.Slot} part onto {component}. It will graft into its native '{payload.Part.Slot}' slot, not force-relabel the target row.");
            }
            AppendLog($"Dropped part {CleanPartMeshDisplayName(payload.Part)} onto Your Character.");
            await GraftSelectedPartsAsync();
            _session.RaiseChanged();
            return;
        }

        AppendLog("Dropped toybox item was not recognized.");
    }

    private void PopulateToyboxTypes()
    {
        _toyboxTypeCombo.Items.Clear();
        switch (_toyboxCategoryCombo.SelectedItem?.ToString())
        {
            case "Home":
                _toyboxTypeCombo.Items.Add("Overview");
                break;
            case "Build mod":
                _toyboxTypeCombo.Items.Add("Release workspace");
                break;
            case "Base":
                _toyboxTypeCombo.Items.Add("Base suit");
                break;
            case "Materials":
                _toyboxTypeCombo.Items.Add("Your materials");
                _toyboxTypeCombo.Items.Add("<all game materials>");
                foreach (var folder in GameMaterialFolders())
                {
                    _toyboxTypeCombo.Items.Add(folder);
                }
                break;
            case "Textures":
                _toyboxTypeCombo.Items.Add("Your textures");
                _toyboxTypeCombo.Items.Add("Texture cooker notes");
                break;
            case "Parts":
                if (_partIndex is null)
                {
                    LoadPartIndexAndRefreshGrid(logIfMissing: false);
                }

                if (_partIndex is null || _partIndex.Parts.Count == 0)
                {
                    _toyboxTypeCombo.Items.Add("Build part index first");
                }
                else
                {
                    _toyboxTypeCombo.Items.Add("<all parts>");
                    foreach (var slot in _partIndex.Parts
                        .Where(part => part.HasMesh)
                        .Where(part => !IsGliderVisualPart(part))
                        .Where(part => !part.Slot.Equals("Face", StringComparison.OrdinalIgnoreCase))
                        .Select(part => part.Slot)
                        .Where(slot => !string.IsNullOrWhiteSpace(slot))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(slot => SlotSortKey(slot, 0))
                        .ThenBy(slot => slot, StringComparer.OrdinalIgnoreCase))
                    {
                        _toyboxTypeCombo.Items.Add(slot);
                    }
                }
                // Attachment libraries come straight from the shipped catalog (no
                // part index needed) - hair/hat static meshes graft like any part.
                _toyboxTypeCombo.Items.Add("Attachment: Hair");
                _toyboxTypeCombo.Items.Add("Attachment: Hat");
                break;
            case "Faces":
                _toyboxTypeCombo.Items.Add("<all faces>");
                foreach (var folder in AttachmentCatalogService.FaceCharacterFolders())
                {
                    _toyboxTypeCombo.Items.Add(folder);
                }
                break;
            case "Equipment":
                _toyboxTypeCombo.Items.AddRange(new object[]
                {
                    "Recommended",
                    "Special controllers",
                    "Family-only",
                    "Testing / boss",
                    "All gadgets"
                });
                break;
            case "Gliders":
                _toyboxTypeCombo.Items.AddRange(new object[] { "Glider presets", "Wingsuit decals" });
                break;
            case "Animations":
                _toyboxTypeCombo.Items.Add("Overview & setup");
                _toyboxTypeCombo.Items.Add("Replace idle/walk/run");
                _toyboxTypeCombo.Items.Add("Swap category by family");
                _toyboxTypeCombo.Items.Add("Browse: Montage composites");
                _toyboxTypeCombo.Items.Add("Browse: Layer blocks");
                foreach (var c in GameDataService.Instance.AnimCategories("Layer"))
                {
                    _toyboxTypeCombo.Items.Add($"Browse: Layer · {c}");
                }
                break;
            case "Review":
                _toyboxTypeCombo.Items.AddRange(new object[] { "All changes", "Base", "Materials", "Textures", "Parts", "Equipment" });
                break;
            case "Research":
                _toyboxTypeCombo.Items.AddRange(new object[] { "Character assets", "Playable / cutscene", "Materials / ColorMask" });
                break;
            default:
                _toyboxTypeCombo.Items.Add("(coming soon)");
                break;
        }
        _toyboxTypeCombo.SelectedIndex = 0;
    }

    private void ClearToyboxTiles()
    {
        ClearAndDisposeControls(_toyboxTileFlow);
        // Default every refresh to the flow surface; the Parts branch opts into the virtual grid.
        _toyboxTileGrid.SetHero(null);
        _toyboxTileGrid.SetTiles(Array.Empty<VirtualTilePanel.Tile>());
        _toyboxTileGrid.Visible = false;
        _toyboxTileFlow.Visible = true;
        _toyboxTileFlow.BringToFront();
    }

    /// <summary>Switches the tile cell to the virtualized grid and loads it with <paramref name="tiles"/>.
    /// Optional <paramref name="header"/> note is painted above the tiles; <paramref name="emptyMessage"/>
    /// shows when the list is empty.</summary>
    private void ShowVirtualTiles(IReadOnlyList<VirtualTilePanel.Tile> tiles, string header = "", string emptyMessage = "",
        VirtualTilePanel.HeroModel? hero = null)
    {
        _toyboxTileFlow.Visible = false;
        _toyboxTileGrid.Visible = true;
        _toyboxTileGrid.BringToFront();
        _toyboxTileGrid.SetHero(hero);
        _toyboxTileGrid.HeaderText = header;
        _toyboxTileGrid.EmptyMessage = emptyMessage;
        _toyboxTileGrid.SetTiles(tiles);
    }

    private VirtualTilePanel.Tile ResearchTile(CharacterResearchService.ResearchAssetRecord asset) => new()
    {
        Title = TrimMiddle(asset.AssetName, 30),
        Subtitle = $"{(asset.HasUexp ? "paired" : "uasset only")} · character asset\n{TrimMiddle(Path.GetDirectoryName(asset.RelativePath)?.Replace('\\', '/') ?? "Characters", 24)}",
        Accent = Theme.Research,
        ToolTip = $"{asset.PackagePath}\n{asset.RelativePath}\nClick to inspect with UAssetAPI.",
        OnClick = () => _ = InspectResearchAssetAsync(asset),
        MenuFactory = () =>
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Inspect asset", null, (_, _) => _ = InspectResearchAssetAsync(asset));
            menu.Items.Add("Copy package path", null, (_, _) =>
            {
                try { Clipboard.SetText(asset.PackagePath); } catch { /* clipboard may be busy */ }
            });
            menu.Items.Add("Copy extracted file path", null, (_, _) =>
            {
                try { Clipboard.SetText(asset.UassetPath); } catch { /* clipboard may be busy */ }
            });
            return menu;
        }
    };

    private void RefreshResearchTiles(string? type)
    {
        _researchInspector.ClearInspection();
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        if (!Directory.Exists(Path.Combine(root, "Characters")))
        {
            _toyboxTileFlow.Controls.Add(FullWidthNote(
                "No extracted Content/Characters folder was found. Run Refresh game assets first, or fix the extracted dump path in Setup."));
            return;
        }

        var service = GetCharacterResearchService(root);
        var assets = service.GetAssets(root, type, CurrentToyboxSearch());
        var filter = string.IsNullOrWhiteSpace(CurrentToyboxSearch()) ? "no search filter" : $"search: {CurrentToyboxSearch()}";
        var header = $"Read-only research browser · {assets.Count:N0} matching asset(s) · {filter}. Click one to inspect exports, imports, and tagged references.";
        ShowVirtualTiles(
            assets.Select(ResearchTile).ToList(),
            header,
            "No character assets matched. Try a shorter search or switch the Research type filter.");
    }

    private CharacterResearchService GetCharacterResearchService(string contentRoot)
    {
        if (_characterResearchService is null ||
            !string.Equals(_characterResearchRoot, contentRoot, StringComparison.OrdinalIgnoreCase))
        {
            _characterResearchRoot = contentRoot;
            _characterResearchService = new CharacterResearchService();
        }

        return _characterResearchService;
    }

    private async Task InspectResearchAssetAsync(CharacterResearchService.ResearchAssetRecord asset)
    {
        if (_toyboxCategoryCombo.SelectedItem?.ToString() != "Research")
        {
            return;
        }

        _researchInspector.ShowLoading(asset);
        AppendLog($"Research inspect: {asset.PackagePath}");
        try
        {
            var service = GetCharacterResearchService(AppSettings.Current.EffectiveExtractedContentRoot());
            var inspection = await Task.Run(() => service.Inspect(asset));
            if (IsDisposed)
            {
                return;
            }

            _researchInspector.ShowInspection(inspection);
            AppendLog(inspection.Succeeded
                ? $"Research parsed: exports={inspection.ExportLines.Count}, imports={inspection.ImportLines.Count}."
                : "Research parse failed; see the Research inspector for the error.");
        }
        catch (Exception ex)
        {
            _researchInspector.ClearInspection("Research inspection failed: " + ex.Message);
            AppendLog("Research inspection failed: " + ex.Message);
        }
    }

    private void RefreshHomeWorkspaceTiles()
    {
        switch (_homeWorkspaceSection)
        {
            case HomeWorkspaceSection.Suits:
                RefreshHomeSuitLibraryTiles();
                return;
            case HomeWorkspaceSection.BuildMod:
                RefreshBuildModTiles();
                return;
            case HomeWorkspaceSection.Review:
                RefreshReviewTiles(_toyboxTypeCombo.SelectedItem?.ToString());
                return;
            default:
                RefreshHomeTiles();
                return;
        }
    }

    private void RefreshHomeSuitLibraryTiles()
    {
        var service = new SuitProjectService(_projectRootText.Text.Trim());
        var suits = new List<SuitProjectService.ProjectSummary>();
        try { suits = service.ListProjects().OrderByDescending(suit => suit.Modified).ToList(); } catch { /* no saved suits yet */ }

        var hero = new VirtualTilePanel.HeroModel
        {
            Overline = "YOUR LIBRARY",
            Title = "Suits",
            Subtitle = suits.Count == 0
                ? "Create a suit, then choose its base character and parts."
                : $"{suits.Count} saved suit{(suits.Count == 1 ? "" : "s")} ready to reopen or add to a mod.",
            ThumbAccent = Theme.Base,
            Chips = new List<(string, Color)>
            {
                ($"{suits.Count} saved", Theme.Base),
                (_currentProject is null ? "no suit open" : "current suit open", _currentProject is null ? Theme.OnDarkMuted : Theme.Good),
            },
        };
        var tiles = new List<VirtualTilePanel.Tile>
        {
            new()
            {
                Section = "SUITS",
                Title = "＋ New suit",
                Subtitle = "start a custom character",
                Accent = Theme.Base,
                Dashed = true,
                OnClick = () => StartNewSuit(),
            },
            new()
            {
                Section = "SUITS",
                Title = "Open suit",
                Subtitle = "browse saved projects",
                Accent = Theme.Base,
                OnClick = LoadSuit,
            },
        };
        foreach (var suit in suits.Take(20))
        {
            var captured = suit;
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = "YOUR SUITS",
                Title = TrimMiddle(captured.DisplayName, 26),
                Subtitle = $"saved · {captured.Modified:MMM d}",
                Accent = Theme.Base,
                Image = LoadSuitCoverImage(captured),
                OnClick = () => OpenRecentProject(captured.Path),
                MenuFactory = () => BuildSuitTileMenu(captured),
            });
        }
        ShowVirtualTiles(tiles, hero: hero);
    }

    private void RefreshSuitWorkspaceTiles()
    {
        var hasSuit = _currentProject is not null;
        var hero = new VirtualTilePanel.HeroModel
        {
            Overline = "SUIT WORKSPACE",
            Title = hasSuit ? _currentProject!.DisplayName : "Choose a suit",
            Subtitle = hasSuit
                ? "Set the visual base, then customize parts, materials, equipment, gliders, and animation choices."
                : "Create or open a saved suit to start building a character.",
            ThumbAccent = Theme.Base,
            Chips = new List<(string, Color)>
            {
                (hasSuit ? "suit open" : "no suit open", hasSuit ? Theme.Good : Theme.OnDarkMuted),
                (HasCurrentSuitBase() ? "base set" : "base needed", HasCurrentSuitBase() ? Theme.Good : Theme.Warn),
            },
        };
        var tiles = new List<VirtualTilePanel.Tile>
        {
            new()
            {
                Section = "SUIT",
                Title = "＋ New suit",
                Subtitle = "start a custom character",
                Accent = Theme.Base,
                Dashed = true,
                OnClick = () => StartNewSuit(),
            },
            new()
            {
                Section = "SUIT",
                Title = "Open suit",
                Subtitle = "browse saved projects",
                Accent = Theme.Base,
                OnClick = LoadSuit,
            },
        };
        if (hasSuit)
        {
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = "SUIT",
                Title = HasCurrentSuitBase() ? "Change visual base" : "Set visual base",
                Subtitle = HasCurrentSuitBase() ? "visual + gameplay donor" : "choose a character or cutscene",
                Accent = Theme.Base,
                OnClick = OpenBaseWizard,
            });
        }

        var suitService = new SuitProjectService(_projectRootText.Text.Trim());
        var savedSuits = new List<SuitProjectService.ProjectSummary>();
        try { savedSuits = suitService.ListProjects().OrderByDescending(suit => suit.Modified).ToList(); } catch { /* no saved suits yet */ }
        foreach (var suit in savedSuits.Take(12))
        {
            var captured = suit;
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = "YOUR SUITS",
                Title = TrimMiddle(captured.DisplayName, 26),
                Subtitle = $"saved · {captured.Modified:MMM d}",
                Accent = Theme.Base,
                Image = LoadSuitCoverImage(captured),
                OnClick = () => OpenRecentProject(captured.Path),
                MenuFactory = () => BuildSuitTileMenu(captured),
            });
        }
        ShowVirtualTiles(tiles, hero: hero);
    }

    /// <summary>
    /// The Home/Mods section follows the release hierarchy: choose a mod, build its suits,
    /// then build one mod release. Saved suits remain visible when no mod is active,
    /// but an active workspace only shows the suits that will ship together.
    /// </summary>
    private void RefreshHomeTiles()
    {
        var partCount = _partIndex?.Parts.Count ?? 0;
        var suitService = new SuitProjectService(_projectRootText.Text.Trim());
        var savedSuits = new List<SuitProjectService.ProjectSummary>();
        var savedMods = new List<ModProjectService.ModSummary>();
        try { savedSuits = suitService.ListProjects().ToList(); } catch { /* no saved suits yet */ }
        try { savedMods = ModService.ListMods().ToList(); } catch { /* no saved mods yet */ }

        var (activeSummary, activeMod) = ResolveHomeActiveMod(savedMods);
        var activeEntries = activeMod?.Suits
            .OrderBy(entry => entry.MenuOrder)
            .ToList() ?? new List<ModSuitEntry>();
        var suitsByPath = savedSuits.ToDictionary(summary => summary.Path, StringComparer.OrdinalIgnoreCase);
        var activeSuits = activeEntries.Select(entry =>
        {
            var resolved = ModService.ResolveSuitProjectPath(entry);
            suitsByPath.TryGetValue(resolved, out var summary);
            summary ??= savedSuits.FirstOrDefault(candidate =>
                string.Equals(candidate.SlotId, entry.SuitId, StringComparison.OrdinalIgnoreCase));
            return (Entry: entry, Summary: summary);
        }).ToList();
        var hasActiveMod = activeSummary is not null && activeMod is not null;
        var activeSuitCount = activeEntries.Count;
        var activeContentCount = activeSuitCount;
        var currentSlot = _slotIdText.Text.Trim();
        var currentSuitIsInActiveMod = hasActiveMod && activeEntries.Any(entry =>
            string.Equals(entry.SuitId, currentSlot, StringComparison.OrdinalIgnoreCase));

        var chips = new List<(string, Color)>
        {
            (hasActiveMod ? "active mod" : "no active mod", hasActiveMod ? Theme.Mods : Theme.Warn),
            ($"{(hasActiveMod ? activeSuitCount : savedSuits.Count)} suit{((hasActiveMod ? activeSuitCount : savedSuits.Count) == 1 ? "" : "s")}", Theme.Base),
            (partCount > 0 ? $"{partCount} parts" : "index not built", partCount > 0 ? Theme.Materials : Theme.OnDarkMuted),
        };

        var hero = new VirtualTilePanel.HeroModel
        {
            Overline = "MOD WORKSPACE",
            Title = hasActiveMod ? activeSummary!.DisplayName : "Start your first mod",
            Subtitle = hasActiveMod
                ? $"{activeSuitCount} suit{(activeSuitCount == 1 ? "" : "s")} grouped into one mod release."
                : "Create or select a mod first, then add the suits that ship together.",
            Badge = "",
            ThumbAccent = hasActiveMod ? Theme.Mods : Theme.Gold,
            Chips = chips,
            Workflow = new[]
            {
                new VirtualTilePanel.HeroModel.WorkflowStep
                {
                    Label = "1. MOD",
                    Detail = hasActiveMod ? "mod selected" : "choose a mod",
                    Accent = Theme.Mods,
                    Complete = hasActiveMod,
                    Current = !hasActiveMod,
                },
                new VirtualTilePanel.HeroModel.WorkflowStep
                {
                    Label = "2. CONTENT",
                    Detail = hasActiveMod
                        ? (activeContentCount > 0 ? "suits" : "add content")
                        : "select a mod first",
                    Accent = Theme.Base,
                    Current = hasActiveMod && activeContentCount == 0,
                },
                new VirtualTilePanel.HeroModel.WorkflowStep
                {
                    Label = "3. BUILD",
                    Detail = activeContentCount > 0 ? "release when ready" : "add content first",
                    Accent = Theme.Gold,
                    Current = hasActiveMod && activeContentCount > 0,
                },
            },
        };

        const string SectionMod = "MODS";
        const string SectionSuits = "2. SUITS";
        const string SectionBuild = "3. BUILD MOD";
        const string SectionSavedSuits = "SAVED SUITS";
        var tiles = new List<VirtualTilePanel.Tile>();

        tiles.Add(new()
        {
            Section = SectionMod,
            Title = "＋ New mod",
            Subtitle = "start a release collection",
            Accent = Theme.Gold,
            Dashed = true,
            OnClick = CreateModFlow,
        });

        foreach (var mod in savedMods.Take(6))
        {
            var captured = mod;
            var isActive = hasActiveMod && string.Equals(captured.Path, activeSummary!.Path, StringComparison.OrdinalIgnoreCase);
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionMod,
                Title = TrimMiddle(captured.DisplayName, 26),
                Subtitle = isActive
                    ? $"{captured.SuitCount} suit{(captured.SuitCount == 1 ? "" : "s")} · current mod"
                    : $"{captured.SuitCount} suit{(captured.SuitCount == 1 ? "" : "s")} · select workspace",
                Accent = isActive ? Theme.Mods : Theme.OnDarkMuted,
                OnClick = () => SelectHomeMod(captured.Path),
                MenuFactory = () => BuildModTileMenu(captured.Path, captured.ModId),
            });
        }

        if (!hasActiveMod)
        {
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionSavedSuits,
                Title = "All suits",
                Subtitle = $"{savedSuits.Count} saved in the tool",
                Accent = Theme.Base,
                OnClick = LoadSuit,
            });
            foreach (var suit in savedSuits.Take(10))
            {
                var captured = suit;
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionSavedSuits,
                    Title = TrimMiddle(captured.DisplayName, 26),
                    Subtitle = $"saved suit · {captured.Modified:MMM d}",
                    Accent = Theme.Base,
                    Image = LoadSuitCoverImage(captured),
                    OnClick = () => OpenRecentProject(captured.Path),
                    MenuFactory = () => BuildSuitTileMenu(captured),
                });
            }
            ShowVirtualTiles(tiles, hero: hero);
            return;
        }

        var modPath = activeSummary!.Path;
        var modName = activeSummary.DisplayName;
        tiles.Add(new VirtualTilePanel.Tile
        {
            Section = SectionSuits,
            Title = "＋ Add a suit",
            Subtitle = "create inside this mod",
            Accent = Theme.Base,
            Dashed = true,
            OnClick = () => StartNewSuitInMod(modPath),
        });
        tiles.Add(new VirtualTilePanel.Tile
        {
            Section = SectionSuits,
            Title = "Manage suits",
            Subtitle = "add or remove saved suits",
            Accent = Theme.Base,
            OnClick = () => EditModSuits(modPath),
        });
        tiles.Add(new VirtualTilePanel.Tile
        {
            Section = SectionSuits,
            Title = "All suits",
            Subtitle = $"{savedSuits.Count} saved in the tool",
            Accent = Theme.Base,
            OnClick = LoadSuit,
        });

        foreach (var (entry, summary) in activeSuits.Take(10))
        {
            var capturedEntry = entry;
            var capturedSummary = summary;
            var title = capturedSummary is null ? capturedEntry.SuitId : TrimMiddle(capturedSummary.DisplayName, 26);
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionSuits,
                Title = title,
                Subtitle = capturedSummary is null ? "missing saved suit" : $"reopen · {capturedSummary.Modified:MMM d}",
                Accent = Theme.Base,
                Image = capturedSummary is null ? null : LoadSuitCoverImage(capturedSummary),
                OnClick = capturedSummary is null ? () => EditModSuits(modPath) : () => OpenRecentProject(capturedSummary.Path),
                MenuFactory = capturedSummary is null ? null : () => BuildSuitTileMenu(capturedSummary),
            });
        }

        if (activeContentCount == 0)
        {
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionBuild,
                Title = "Add content first",
                Subtitle = $"{modName} needs a suit before it can build",
                Accent = Theme.Gold,
                Dashed = true,
                OnClick = () => StartNewSuitInMod(modPath),
            });
        }
        else
        {
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionBuild,
                Title = $"Build {TrimMiddle(modName, 20)}",
                Subtitle = "build and install your mod/suits to your game",
                Accent = Theme.Gold,
                OnClick = () => BuildMod(modPath),
            });
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionBuild,
                Title = "Manage mod",
                Subtitle = "identity, suits, output",
                Accent = Theme.Research,
                OnClick = () => OpenModDetails(modPath, activeSummary.ModId),
            });
            if (currentSuitIsInActiveMod && !string.IsNullOrWhiteSpace(_targetPlayableText.Text.Trim()))
            {
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionBuild,
                    Title = "Check current suit",
                    Subtitle = "before packaging",
                    Accent = Theme.Materials,
                    OnClick = RunV2PreflightFromUi,
                });
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = SectionBuild,
                    Title = "Preview current suit",
                    Subtitle = "inspect package contents",
                    Accent = Theme.Materials,
                    OnClick = ShowPackageContentsPreview,
                });
            }
        }

        ShowVirtualTiles(tiles, hero: hero);
    }

    /// <summary>Base: "pick, then configure" - a hero showing the chosen base + identity/config tiles
    /// that only appear once a base is picked (progressive disclosure).</summary>
    private void RefreshBaseTiles()
    {
        var profile = _currentProject?.BaseProfile;
        var hasBase = HasCurrentSuitBase();
        var visualPackage = profile?.VisualBasePackage ?? _currentProject?.CutsceneTemplate?.PackagePath ?? "";
        var gameplayPackage = profile?.GameplayDonorPackage ?? _currentProject?.PlayableTemplate?.PackagePath ?? "";
        var baseName = hasBase ? UnrealPathUtil.AssetName(visualPackage) : "";
        var summary = hasBase ? BaseInheritanceSummary() : "";
        var glideOk = summary.StartsWith("✓", StringComparison.Ordinal);

        var chips = new List<(string, Color)>();
        if (hasBase)
        {
            chips.Add(("visual " + (string.IsNullOrWhiteSpace(profile?.VisualFamily) ? "base" : profile.VisualFamily), Theme.Base));
            chips.Add(("gameplay " + (string.IsNullOrWhiteSpace(profile?.GameplayFamily) ? "donor" : profile.GameplayFamily), Theme.Good));
            chips.Add((glideOk ? "glide visual" : "no glide", glideOk ? Theme.Gliders : Theme.Warn));
        }

        var hero = new VirtualTilePanel.HeroModel
        {
            Title = hasBase ? baseName : "Pick a base character",
            Subtitle = hasBase
                ? $"Visual: {UnrealPathUtil.AssetName(visualPackage)}. Gameplay donor: {UnrealPathUtil.AssetName(gameplayPackage)} — movement, equipment, and animations are inherited."
                : "Choose any character or cutscene for the look, then pair it with a playable donor for runtime behavior.",
            Badge = hasBase ? "Base set" : "No base",
            BadgeColor = hasBase ? Theme.Good : Theme.Warn,
            ThumbAccent = Theme.Base,
            Chips = chips,
        };

        const string SectionBase = "BASE";
        const string SectionIdentity = "IDENTITY";
        var tiles = new List<VirtualTilePanel.Tile>
        {
            new() { Section = SectionBase, Title = hasBase ? "Change visual base" : "Pick visual base", Subtitle = hasBase ? "visual + gameplay donor" : "start with a character or cutscene", Accent = Theme.Base, Dashed = !hasBase, OnClick = OpenBaseWizard },
        };
        if (hasBase)
        {
            tiles.Add(new() { Section = SectionIdentity, Title = "Native identity", Subtitle = NativeIdentityTileSubtitle(), Accent = Theme.Gold, OnClick = EditNativeIdentity });
            tiles.Add(new() { Section = SectionIdentity, Title = "Set icons", Subtitle = "menu / UIMD", Accent = Theme.Base, OnClick = OpenIconsDialog });
        }

        ShowVirtualTiles(tiles, hero: hero);
    }

    private void RefreshToyboxTiles()
    {
        if (_workspaceFolder == WorkspaceFolder.Viewer)
        {
            return;
        }

        ClearToyboxTiles();
        UpdateToyboxChips();
        var category = _toyboxCategoryCombo.SelectedItem?.ToString();
        var type = _toyboxTypeCombo.SelectedItem?.ToString();

        if (_workspaceFolder == WorkspaceFolder.Home)
        {
            SetHomeInspectorCollapsed(true);
            RefreshHomeWorkspaceTiles();
            return;
        }

        HideViewerPanel();
        SetHomeInspectorCollapsed(false);

        if (category == "Home")
        {
            RefreshSuitWorkspaceTiles();
            return;
        }

        if (category == "Base")
        {
            RefreshBaseTiles();
            return;
        }

        if (category == "Materials")
        {
            RefreshMaterialTiles(type);
            return;
        }

        if (category == "Textures")
        {
            RefreshTextureTiles(type);
            return;
        }

        if (category == "Faces")
        {
            RefreshFaceTiles(type);
            return;
        }

        if (category == "Parts")
        {
            var selectedSlot = _toyboxTypeCombo.SelectedItem?.ToString() ?? "<all parts>";
            var isAttachment = selectedSlot.StartsWith("Attachment:", StringComparison.OrdinalIgnoreCase);
            var sourceFilter = FilterVal(2);
            var showOnlyYourMeshes = string.Equals(sourceFilter, "Your meshes", StringComparison.OrdinalIgnoreCase);
            var customMeshes = sourceFilter is null || showOnlyYourMeshes
                ? CustomStaticMeshTiles(CurrentToyboxSearch())
                : new List<VirtualTilePanel.Tile>();

            if (!isAttachment && _partIndex is null)
            {
                LoadPartIndexAndRefreshGrid(logIfMissing: false);
            }

            if (!isAttachment && (_partIndex is null || _partIndex.Parts.Count == 0))
            {
                customMeshes.Add(new VirtualTilePanel.Tile
                {
                    Section = "NATIVE PARTS",
                    Title = "Build part index",
                    Subtitle = "scan extracted character Blueprints",
                    Accent = Theme.Parts,
                    Dashed = true,
                    OnClick = () => _ = BuildPartIndexAsync(),
                });
                ShowVirtualTiles(customMeshes,
                    header: "Import a custom OBJ now, or build the native part index to browse the game's parts. Attachment catalogs remain available without an index.",
                    emptyMessage: "Build the native part index to browse extracted character parts.");
                return;
            }

            var parts = showOnlyYourMeshes
                ? new List<NativeSuitPartRecord>()
                : ToyboxPartCandidates(selectedSlot).ToList();
            customMeshes.AddRange(parts.Select(PartTile));

            // Virtualized: render ALL matches (no paging / "Load more") - only visible tiles paint.
            ShowVirtualTiles(customMeshes,
                header: parts.Count == 0
                    ? $"No native parts matched '{selectedSlot}'. Your custom imported meshes are still available here."
                    : "",
                emptyMessage: "No native parts matched. Try <all parts> or rebuild the part index after changing setup paths.");
            return;
        }

        if (category == "Animations")
        {
            RefreshAnimationTiles(type);
            return;
        }

        if (category == "Gliders")
        {
            RefreshGliderTiles(type);
            return;
        }

        if (category == "Research")
        {
            RefreshResearchTiles(type);
            return;
        }

        RefreshEquipmentCompatTiles(type);
    }

    /// <summary>
    /// Home is the mod workspace, not an edit surface. It keeps the character overview visible but
    /// gives the workspace the inspector's width; choosing any authoring category restores it.
    /// </summary>
    private void SetHomeInspectorCollapsed(bool collapsed)
    {
        if (_toyboxWorkspaceSplit is not null)
        {
            _toyboxWorkspaceSplit.Panel2Collapsed = collapsed;
        }
    }

    /// <summary>
    /// Per-animation locomotion override tiles: each idle/walk/run pose the suit's
    /// own ABP_Core plays, with a picker to replace it with a custom or borrowed
    /// AnimSequence. Safe (same animgraph, only the pose changes).
    /// </summary>
    private void RefreshLocomotionTiles()
    {
        EnsureProject();
        var archetypeOn = _currentProject?.UseCustomArchetype == true;
        if (!archetypeOn)
        {
            _toyboxTileFlow.Controls.Add(FullWidthNote("⚠ Turn on Custom archetype (This suit's composition tab) first — locomotion overrides need it."));
            return;
        }

        var donor = _currentProject is null ? null
            : AnimArchetypeGraftService.DetectDonorForProject(_currentProject, "", UiMappings());
        if (donor is null || string.IsNullOrEmpty(donor.Family))
        {
            _toyboxTileFlow.Controls.Add(FullWidthNote("Could not detect this suit's family from its base playable. Set a base suit first."));
            return;
        }

        var seqs = AnimArchetypeGraftService.DetectLocomotionSequences(donor.Family, UiMappings());
        _toyboxTileFlow.Controls.Add(FullWidthNote(
            $"{donor.Family}'s idle/walk/run poses (from ABP_Core_{donor.Family}). Click one to replace it with a custom AnimSequence or another family's. Safe — keeps this suit's own animgraph. Applies on next generate."));

        if (seqs.Count == 0)
        {
            _toyboxTileFlow.Controls.Add(MakeNoteTile($"No overridable locomotion sequences found for {donor.Family} (ABP_Core not extracted, or this character has none)."));
            return;
        }

        foreach (var (name, package) in seqs)
        {
            var current = _currentProject?.LocomotionOverrides.FirstOrDefault(o => o.DonorSequence == name);
            var slot = name.Replace($"_{donor.Family}", "").Replace("A_", "");
            var sub = current is null ? "donor default" : $"→ {current.ReplacementSequence}";
            var n = name; var p = package;
            var tile = MakeTile(slot, sub, () => PickLocomotionReplacement(n, p), current is null ? Theme.OnDarkMuted : Theme.Animations);
            tile.Height = 84;
            _toyboxTileFlow.Controls.Add(tile);
        }
    }

    /// <summary>Lists everything applied/staged this session (the "what changed" screen).</summary>
    private void RefreshReviewTiles(string? filter)
    {
        // Show only the ACTIVE state: the newest change per (category + target). Older superseded
        // edits (e.g. re-picking a base, re-applying a material to the same slot) are collapsed so
        // Review reflects what the suit currently is, not the full historical log.
        var seen = new HashSet<(string, string)>();
        var all = new List<SavedChange>();
        foreach (var c in Changes.AsEnumerable().Reverse()) // newest first
        {
            if (seen.Add((c.Category.ToLowerInvariant(), (c.Target ?? "").ToLowerInvariant())))
            {
                all.Add(c);
            }
        }

        // Per-category counts for the summary header (grouped, human-readable).
        var counts = all.GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}: {g.Count()}");
        var suitName = string.IsNullOrWhiteSpace(_suitNameText.Text) ? "this suit" : _suitNameText.Text.Trim();
        var header = all.Count == 0
            ? $"Review — no changes recorded for {suitName} yet."
            : $"{suitName} — {all.Count} change(s)" +
              (counts.Any() ? $"  ·  {string.Join("  ·  ", counts)}" : "") +
              "\nNewest first, persists across sessions. Click a change for detail; right-click to remove; use 'Copy summary' to export the full list.";

        var items = all.AsEnumerable();
        if (!string.IsNullOrEmpty(filter) && filter != "All changes")
        {
            items = items.Where(c => c.Category.Equals(filter, StringComparison.OrdinalIgnoreCase));
        }

        var tiles = items.Select(c =>
        {
            var glyph = c.Status == "applied" ? "✓" : c.Status == "staged" ? "◷" : "•";
            return new VirtualTilePanel.Tile
            {
                Title = $"{glyph} {c.Category}",
                Subtitle = $"{c.Target}\n{c.Detail}",
                Accent = Theme.CategoryColor(c.Category),
                ToolTip = $"{c.Category} · {c.Target}\n{c.Detail}\nStatus: {c.Status} · {FormatWhen(c.When)}",
                OnClick = () => Dialog.Info(null, "Change detail", $"{c.Category} · {c.Target}\n\n{c.Detail}\n\nStatus: {c.Status}\nWhen: {FormatWhen(c.When)}\n\nRight-click to remove this change."),
                MenuFactory = () =>
                {
                    var menu = new ContextMenuStrip();
                    menu.Items.Add("Remove this change", null, async (_, _) => await RemoveReviewChangeAsync(c));
                    return menu;
                },
            };
        }).ToList();

        ShowVirtualTiles(tiles, header,
            emptyMessage: string.IsNullOrEmpty(filter) || filter == "All changes"
                ? "Nothing recorded yet. Apply a base, material, part, or equipment and it shows up here."
                : $"No '{filter}' changes. Switch the dropdown to 'All changes'.");
    }

    /// <summary>
    /// Removes a change from the review log and reverts its persisted intent where
    /// that maps cleanly (equipment slot, glider type, saved material assignment) so
    /// the next package won't re-apply it. A visual revert of already-staged geometry
    /// needs a re-stage (Pick base character) - noted in the log.
    /// </summary>
    private async Task RemoveReviewChangeAsync(SavedChange change)
    {
        if (_currentProject is null)
        {
            return;
        }
        if (!Dialog.Confirm(this,
                "Remove this change?",
                $"{change.Category} · {change.Target}\n{change.Detail}\n\nThis removes it from the list and stops it re-applying on the next package. Already-staged geometry may need 'Pick base character' to fully rebuild.",
                confirmText: "Remove change"))
        {
            return;
        }

        NativeSuitProject previousProjectSnapshot;
        try
        {
            previousProjectSnapshot = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(_currentProject))
                ?? throw new InvalidOperationException("Could not snapshot the suit before removing the change.");
        }
        catch (Exception ex)
        {
            AppendLog("Remove change stopped before staging: " + ex.Message);
            return;
        }

        _currentProject.Changes.Remove(change);
        var requiresCleanRebuild = false;

        // Best-effort revert of the persisted intent by category.
        switch (change.Category)
        {
            case "Gliders":
                _currentProject.PartGrafts.RemoveAll(graft => graft.IsGlider);
                _currentProject.GliderType = "";
                _currentProject.GliderMaterial = "";
                _currentProject.GliderGrafted = false;
                _currentProject.GliderAnimLas = "";
                _currentProject.GliderAnimMas = "";
                requiresCleanRebuild = true;
                AppendLog("Cleared glider intent (visual + glide-animation injection); rebuilding the stage from the clean base.");
                break;
            case "Equipment":
                _currentProject.EquipmentSlots.Clear();
                requiresCleanRebuild = true;
                AppendLog("Cleared equipment intent — re-add gadgets you still want.");
                break;
            case "Materials":
                // Drop saved assignments whose slot/context is named in this change's target.
                var before = _currentProject.MaterialAssignments.Count;
                _currentProject.MaterialAssignments.RemoveAll(m =>
                    change.Target.Contains(m.Slot.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    change.Target.Contains(m.Component, StringComparison.OrdinalIgnoreCase));
                if (_currentProject.MaterialAssignments.Count != before)
                {
                    requiresCleanRebuild = true;
                }
                break;
        }

        var projectRoot = _projectRootText.Text.Trim();
        var projectSaved = false;
        try
        {
            if (requiresCleanRebuild)
            {
                await RebuildGraftStageFromDeclarativeAsync(persistProject: false);
            }
            await RunWithFileLockRetryAsync(
                () => (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(_currentProject),
                "save the removed review change");
            projectSaved = true;
            if (requiresCleanRebuild)
            {
                await FinalizeDeclarativeGraftStageAsync(_currentProject, projectRoot);
            }
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
                ? "The change removal was saved, but its rebuilt stage could not be certified: " + ex.Message
                : "Remove change failed; the prior saved project was kept: " + ex.Message);
            Dialog.Error(
                this,
                projectSaved ? "Change saved; stage incomplete" : "Remove change failed",
                (projectSaved
                    ? "The project change was saved, but packaging remains blocked until the generated stage can be certified."
                    : "The change could not be removed and rebuilt safely. The prior saved project remains active.") +
                "\n\n" + ex.Message);
            _session.RaiseChanged();
            RefreshInspector();
            RefreshToyboxTiles();
            return;
        }

        AppendLog($"Removed change: {change.Category} · {change.Target}");
        _session.RaiseChanged();
        RefreshToyboxTiles();
    }

    private void RefreshEquipmentCompatTiles(string? filter)
    {
        var gd = GameDataService.Instance;
        if (!gd.Loaded)
        {
            ShowVirtualTiles(Array.Empty<VirtualTilePanel.Tile>(),
                emptyMessage: "Equipment compatibility data not loaded. Ship gamedata/*.json next to the tool (or rebuild it with --build-gamedata) to see anim-compatibility badges.");
            return;
        }

        var basePath = _currentProject?.BaseProfile?.GameplayDonorPackage;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = _currentProject?.PlayableTemplate?.PackagePath;
        }
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = _basePlayableText.Text.Trim();
        }
        var family = gd.FamilyForBasePath(basePath);
        var familyLabel = family?.Name ?? "unknown";

        var header = family is null
            ? "Pick a gameplay donor to see exact compatibility. Equipment is grouped by its real dependency type."
            : $"Gameplay donor: {familyLabel}. Recommended gadgets are native or have a complete cross-family graft. Controller and family-only equipment are separated so their extra requirements stay visible.";

        var familyFilter = FilterVal(0);   // owning family
        var search = CurrentToyboxSearch();
        var tiles = new List<VirtualTilePanel.Tile>();
        foreach (var eq in gd.Db.Equipment)
        {
            if (!MatchesToyboxSearch(search, eq.Name, string.Join(" ", eq.NativeFamilies)))
            {
                continue;
            }

            var profile = EquipmentDependencyService.Analyze(eq, family?.Name);
            if (!EquipmentMatchesView(profile, filter))
            {
                continue;
            }

            if (familyFilter is not null &&
                !eq.NativeFamilies.Contains(familyFilter, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var compat = gd.CheckEquipment(eq.Name, basePath);

            var (glyph, accent) = profile.Support switch
            {
                EquipmentSupportKind.Native => ("", Theme.Equipment),
                EquipmentSupportKind.CrossFamily => ("+", Theme.Info),
                EquipmentSupportKind.Controller => ("!", Color.FromArgb(220, 160, 40)),
                EquipmentSupportKind.FamilyOnly => ("!", Theme.Warn),
                _ => ("?", Theme.OnDarkMuted),
            };

            var owners = eq.NativeFamilies.Count > 0 ? string.Join("/", eq.NativeFamilies) : "no family";
            var capturedEq = eq;
            var capturedCompat = compat;
            var capturedProfile = profile;
            tiles.Add(new VirtualTilePanel.Tile
            {
                Title = string.IsNullOrEmpty(glyph) ? eq.Name : $"{glyph} {eq.Name}",
                Subtitle = $"{profile.SupportLabel} · {owners}",
                Accent = accent,
                OnClick = () => ShowEquipmentCompatDetail(capturedEq, capturedCompat, capturedProfile),
                ToolTip = profile.Summary,
            });
        }
        ShowVirtualTiles(tiles, header, emptyMessage: "No gadgets matched the current filter/search.");
    }

    private static bool EquipmentMatchesView(EquipmentDependencyProfile profile, string? filter) =>
        filter switch
        {
            "Special controllers" => profile.Support == EquipmentSupportKind.Controller,
            "Family-only" => profile.Support == EquipmentSupportKind.FamilyOnly,
            "Testing / boss" => profile.Support == EquipmentSupportKind.Experimental,
            "All gadgets" => true,
            _ => profile.Support is EquipmentSupportKind.Native or EquipmentSupportKind.CrossFamily
        };

    private void ShowEquipmentCompatDetail(
        GameDataEquipment eq,
        GameDataService.CompatResult compat,
        EquipmentDependencyProfile profile)
    {
        var isForeign = compat.Level == GameDataService.Compatibility.Foreign;
        var hasLayerAnims = !string.IsNullOrEmpty(eq.LayerAnimSet);
        var hasMontageAnims = !string.IsNullOrEmpty(eq.MontageAnimSet);
        var hasGraft = hasLayerAnims || hasMontageAnims;
        var customArchetype = _currentProject?.UseCustomArchetype == true;
        var requiredFamily = profile.RequiredGameplayFamily;
        var currentFamily = _currentProject?.BaseProfile?.GameplayFamily;
        if (string.IsNullOrWhiteSpace(currentFamily) && _currentProject is not null)
        {
            currentFamily = GameDataService.Instance
                .FamilyForBasePath(_currentProject.PlayableTemplate?.PackagePath ?? "")?
                .Name;
        }
        var controllerFamilyMismatch =
            profile.Support == EquipmentSupportKind.Controller &&
            !string.IsNullOrWhiteSpace(requiredFamily) &&
            !string.Equals(requiredFamily, currentFamily, StringComparison.OrdinalIgnoreCase);

        var model = new Dialog.Model
        {
            WindowTitle = "Equipment",
            Title = eq.Name,
            Subtitle = eq.NativeFamilies.Count > 0
                ? $"Native to {string.Join(", ", eq.NativeFamilies)}"
                : "No native family",
            Message = profile.Summary,
            Severity = profile.Support switch
            {
                EquipmentSupportKind.Native => Dialog.Level.Good,
                EquipmentSupportKind.CrossFamily => Dialog.Level.Info,
                EquipmentSupportKind.Controller when controllerFamilyMismatch => Dialog.Level.Crit,
                EquipmentSupportKind.Controller => Dialog.Level.Warn,
                EquipmentSupportKind.FamilyOnly => Dialog.Level.Warn,
                _ => Dialog.Level.Crit
            },
            PrimaryText = "Add gadget",
            SecondaryText = "Cancel",
        };
        model.Chips.Add((profile.SupportLabel, isForeign ? Theme.Warn : Theme.Good));
        model.Chips.Add((profile.Architecture, profile.Support == EquipmentSupportKind.Controller ? Theme.Warn : Theme.Info));
        if (!string.IsNullOrWhiteSpace(profile.RequiredGameplayFamily))
        {
            model.Chips.Add(($"{profile.RequiredGameplayFamily} base required", Theme.Warn));
        }
        model.Chips.Add((hasGraft ? "anims graftable" : "no anim set", hasGraft ? Theme.Good : Theme.Warn));

        model.Fields.Add(("ETA", eq.EtaPackage));
        model.Fields.Add(("ED", string.IsNullOrEmpty(eq.EdPackage) ? "(none)" : eq.EdPackage));
        if (hasLayerAnims)
        {
            model.Fields.Add(("Layer anims", eq.LayerAnimSet));
        }
        if (hasMontageAnims)
        {
            model.Fields.Add(("Montage anims", eq.MontageAnimSet));
        }
        if (profile.AbilitySets.Count > 0)
        {
            model.Fields.Add(("Controller set", string.Join(", ", profile.AbilitySets)));
        }
        if (eq.VisualAbilities.Count > 0 || profile.ExtraGrantedAbilities.Count > 0)
        {
            var grantedCount = eq.VisualAbilities
                .Concat(profile.ExtraGrantedAbilities)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            model.Fields.Add(("Granted abilities",
                $"{grantedCount} native grant(s)"));
        }
        if (profile.DefinitionAbilities.Count > 0)
        {
            model.Fields.Add(("ED actions",
                string.Join(", ", profile.DefinitionAbilities.Select(UnrealPathUtil.AssetName))));
        }
        if (profile.RuntimeActors.Count > 0)
        {
            model.Fields.Add(("Runtime actors", string.Join(", ", profile.RuntimeActors)));
        }

        switch (profile.Support)
        {
            case EquipmentSupportKind.Controller:
                if (controllerFamilyMismatch)
                {
                    model.CalloutTitle = $"Requires a {requiredFamily} gameplay base";
                    model.CalloutDetail =
                        $"{eq.Name} controls a remote pawn. It is confirmed to work only with a {requiredFamily} gameplay donor, " +
                        $"not {(string.IsNullOrWhiteSpace(currentFamily) ? "this base" : currentFamily)}. " +
                        $"Re-select the visual base, choose a {requiredFamily} playable donor, then add this gadget.";
                    model.PrimaryText = "Close";
                    model.SecondaryText = "";
                }
                else
                {
                    model.CalloutTitle = "Special controller graft";
                    model.CalloutDetail =
                        "Packaging appends the gadget's complete native controller set to this suit's " +
                        "cloned DPRD. That preserves its input tags, levels, granted attributes, and " +
                        "gameplay cues. Deploy actions and spawned actors stay on the equipment definition.";
                }
                break;
            case EquipmentSupportKind.FamilyOnly:
                model.CalloutTitle = "No animation set to graft";
                model.CalloutDetail =
                    $"Use a {string.Join("/", eq.NativeFamilies)} gameplay donor for the reliable path. " +
                    "A foreign donor has no separate ability or animation records the tool can safely graft.";
                break;
            case EquipmentSupportKind.Experimental:
                model.CalloutTitle = "No playable-family dependency chain";
                model.CalloutDetail =
                    "This can be staged for research, but the game may be missing player input, draw, " +
                    "animation, or runtime actor wiring for it.";
                break;
            case EquipmentSupportKind.CrossFamily when !hasGraft:
                model.CalloutTitle = "No equipment animation set";
                model.CalloutDetail =
                    "The loadout and listed abilities can be added, but this gadget does not expose a " +
                    "separate equipment animation set to merge.";
                break;
            case EquipmentSupportKind.CrossFamily when !customArchetype:
                model.CalloutTitle = "Custom archetype will be enabled";
                model.CalloutDetail =
                    "This suit needs its own archetype for foreign animation and ability data. " +
                    "Batcomputer will enable it when the gadget is added.";
                break;
            case EquipmentSupportKind.CrossFamily:
                model.CalloutTitle = "Dependency graft ready";
                model.CalloutDetail =
                    "The gadget loadout, listed abilities, and available MAS/LAS data will be merged " +
                    "when the mod is built.";
                break;
            default:
                model.CalloutTitle = "Native dependency path";
                model.CalloutDetail =
                    "The gameplay donor already supplies this gadget's character-side dependencies.";
                break;
        }

        if (!Dialog.Show(this, model))
        {
            return;
        }

        if (controllerFamilyMismatch)
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            AppendLog("Set a base suit first before adding equipment.");
            return;
        }

        // Gadgets only work on combat families whose data has an Equipment array.
        // Non-combat bases (e.g. ThomasWayne) can't carry gadgets - warn instead of
        // silently packaging a suit with no equipment.
        if (!new AnimArchetypeGraftService().BaseSupportsEquipment(_currentProject, out var famName))
        {
            var proceed = Dialog.Confirm(this,
                "This base can't carry gadgets",
                $"{(string.IsNullOrWhiteSpace(famName) ? "This family" : famName)} has no equipment/combat components on its playable, so it can't use gadgets at all.\n\n" +
                $"'{eq.Name}' will be recorded but won't work in-game on this base. Pick a combat character as the base instead.",
                confirmText: "Add anyway",
                windowTitle: "Equipment");
            if (!proceed)
            {
                return;
            }
        }

        var slot = AskEquipmentSlot(eq);
        if (slot < 0)
        {
            return;
        }

        // One gadget per slot: drop any prior staged change for this slot.
        _currentProject.EquipmentSlots.RemoveAll(s => s.Slot == slot);
        _currentProject.EquipmentSlots.Add(new EquipmentSlotChange { Slot = slot, Gadget = eq.Name });

        if (isForeign && !_currentProject.UseCustomArchetype)
        {
            _currentProject.UseCustomArchetype = true;
            RecordChange("Animations", "archetype", "enabled for foreign equipment", status: "staged");
            AppendLog($"Enabled the custom archetype for '{eq.Name}' dependency grafting.");
        }

        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }

        var note = profile.SupportLabel.ToLowerInvariant();
        RecordChange("Equipment", $"slot {slot + 1}", $"{eq.Name} ({note})", status: "staged");
        AppendLog($"Staged '{eq.Name}' into equipment slot {slot + 1} and saved. See Review.");
    }

    private Button MakeTile(string title, string subtitle, Action onClick, Color accent, bool dashed = false)
    {
        var b = new Button
        {
            Width = 118,
            Height = 82,
            Margin = new Padding(5),
            Text = "",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelBg,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.PanelBg;
        // 0 = rest, 1 = hovered; eased so the fill/border warm up instead of flicking.
        var hoverT = 0.0;
        b.MouseEnter += (_, _) => Animator.Start(b, "hover", hoverT, 1, 120, v => { hoverT = v; b.Invalidate(); });
        b.MouseLeave += (_, _) => Animator.Start(b, "hover", hoverT, 0, 140, v => { hoverT = v; b.Invalidate(); });
        b.HandleDestroyed += (_, _) => Animator.Cancel(b, "hover");
        b.Paint += (_, e) =>
        {
            var g = e.Graphics;
            // Clear the whole button to the parent's ground first so the *corners* outside the
            // rounded path blend into the surface (otherwise the button's own square BackColor
            // shows through at the four corners). Mirrors RoundedPanel.
            g.Clear(ControlGround.Resolve(b));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
            using var path = Theme.RoundedRect(r, Theme.RadiusSm);
            // Blend weights the first colour by hoverT, so the hovered colour comes first.
            using (var fill = new SolidBrush(dashed
                       ? Theme.Blend(Theme.Slate, Theme.PanelBg, hoverT)
                       : Theme.Blend(Theme.CardHi, Theme.CardBg, hoverT)))
            {
                g.FillPath(fill, path);
            }
            using (var pen = new Pen(dashed
                       ? accent
                       : Theme.Blend(accent, Theme.Blend(accent, Theme.LineSoft, 0.55), hoverT)))
            {
                if (dashed) pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, title, Theme.BodyStrong, new Rectangle(6, 10, b.Width - 12, 32), accent,
                TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(subtitle))
            {
                TextRenderer.DrawText(g, subtitle, Theme.Caption, new Rectangle(6, 46, b.Width - 12, b.Height - 52),
                    Theme.OnDarkMuted, TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// A drag-only toybox tile: left-click no longer applies (that only happens
    /// by dragging onto a slot). Left-click just shows a hint; right-click uses
    /// the supplied context menu for edit/base/apply actions.
    /// </summary>
    private Button MakeDragTile(string title, string subtitle, Color accent, ToyboxDragPayload payload, ContextMenuStrip menu)
    {
        var tile = MakeTile(title, subtitle, () => AppendLog("Drag this tile onto a slot to apply it, or right-click for options."), accent);
        EnableToyboxTileDrag(tile, payload);
        tile.ContextMenuStrip = menu;
        return tile;
    }

    private void EnableToyboxTileDrag(Control tile, ToyboxDragPayload payload)
    {
        tile.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _toyboxDragStartPoint = e.Location;
            }
        };
        tile.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var dragRect = new Rectangle(
                _toyboxDragStartPoint.X - (SystemInformation.DragSize.Width / 2),
                _toyboxDragStartPoint.Y - (SystemInformation.DragSize.Height / 2),
                SystemInformation.DragSize.Width,
                SystemInformation.DragSize.Height);

            if (!dragRect.Contains(e.Location))
            {
                tile.DoDragDrop(payload, DragDropEffects.Copy);
            }
        };
    }

    private Label MakeNoteTile(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 520,
        Height = 70,
        ForeColor = Theme.OnDarkMuted,
        Margin = new Padding(6)
    };

    private string CurrentToyboxSearch() => _toyboxSearchText.Text.Trim();

    private static bool MatchesToyboxSearch(string search, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var haystack = string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        return search
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The selected filter value, or null for the "Any…/All…" placeholder (no filtering).</summary>
    /// <summary>The value picked in filter slot <paramref name="index"/>, or null for "any".</summary>
    private string? FilterVal(int index) => _toyboxFilters.Value(index);

    /// <summary>
    /// Mirrors the hidden type combo into the filter button's scope section. The combo is still the
    /// source of truth - PopulateToyboxTypes and a dozen "jump to this view" callers write to it -
    /// so the button follows it rather than the other way round.
    /// </summary>
    private void SyncFilterScope()
    {
        var items = _toyboxTypeCombo.Items.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
        _toyboxFilters.SetScope(ScopeTitleForCategory(), items, _toyboxTypeCombo.SelectedItem?.ToString());
    }

    /// <summary>What the type list is actually choosing between, per category.</summary>
    private string ScopeTitleForCategory() => _toyboxCategoryCombo.SelectedItem?.ToString() switch
    {
        "Parts" => "Part slot",
        "Materials" => "Material set",
        "Faces" => "Face set",
        "Animations" => "Animation view",
        "Equipment" => "Gadget set",
        "Gliders" => "Glider view",
        "Review" => "Change area",
        _ => "View",
    };

    /// <summary>Configures the filter button for the current category. Each browser gets the filters
    /// that make sense for it (Parts → context/mesh/source, Equipment → family, Materials/Faces/
    /// Gliders/Textures → source, Animations → kind, Review → area). Called on category change + init.</summary>
    private void ConfigureToyboxFilters()
    {
        SyncFilterScope();

        IEnumerable<string> PartSources() => _partIndex is null
            ? Enumerable.Empty<string>()
            : _partIndex.Parts.Where(p => p.HasMesh && !IsGliderVisualPart(p) && !p.Slot.Equals("Face", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.CharacterFolder).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> GliderSources() => _partIndex is null
            ? Enumerable.Empty<string>()
            : GliderService.NativeGliderParts(_partIndex, "").Select(p => p.CharacterFolder)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> AnimCategories() => GameDataService.Instance.Loaded
            ? GameDataService.Instance.AnimSets("Layer").Concat(GameDataService.Instance.AnimSets("Montage"))
                .Select(a => a.Category).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

        IEnumerable<string> EquipmentFamilies() => GameDataService.Instance.Loaded
            ? GameDataService.Instance.Db.Equipment.SelectMany(e => e.NativeFamilies)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

        switch (_toyboxCategoryCombo.SelectedItem?.ToString())
        {
            case "Parts":
                _toyboxFilters.SetGroups(
                    new FilterGroup("Context", "Any context", new[] { "Playable", "Cutscene" }),
                    new FilterGroup("Mesh", "Any mesh", new[] { "Skeletal", "Static" }),
                    new FilterGroup("Source", "Any source", new[] { "Your meshes" }.Concat(PartSources())));
                break;
            case "Equipment":
                // Family (who owns the gadget) is concrete + base-independent. (Native/Foreign is
                // base-dependent, so it lives as the ✓/⚠ badge on each tile instead of a filter.)
                _toyboxFilters.SetGroups(
                    new FilterGroup("Family", "Any family", EquipmentFamilies()));
                break;
            case "Gliders":
                _toyboxFilters.SetGroups(
                    new FilterGroup("Source", "Any source", GliderSources()),
                    new FilterGroup("Type", "Any type", new[] { "Glide cape", "Wingsuit", "Character glider" }));
                break;
            case "Materials":
                _toyboxFilters.SetGroups(
                    new FilterGroup("Source", "Any source", GameMaterialFolders()));
                break;
            case "Faces":
                _toyboxFilters.SetGroups(
                    new FilterGroup("Source", "Any source", AttachmentCatalogService.FaceCharacterFolders()));
                break;
            case "Animations":
                // Only bites on the "Browse:" types; the overview and swap views ignore it.
                _toyboxFilters.SetGroups(
                    new FilterGroup("Category", "Any category", AnimCategories()));
                break;
            default:
                _toyboxFilters.SetGroups();
                break;
        }
    }

    private Control CreateBaseSuitPanel()
    {
        var box = new GroupBox { Dock = DockStyle.Fill, Text = "Pick the suit to start from" };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(10), AutoScroll = true };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        box.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Choose an existing playable + cutscene .uasset from the extracted game content. The tool copies them, renames to your mod's paths, then you edit materials (step 2) and parts (step 3)."
        }, 0, 0);

        layout.Controls.Add(BuildBaseRow("Playable", _basePlayableText), 0, 1);
        layout.Controls.Add(BuildBaseRow("Cutscene", _baseCutsceneText), 0, 2);
        layout.Controls.Add(BuildBaseRow("DCMD (optional)", _baseDcmdText), 0, 3);

        var idBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4 };
        idBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        idBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        idBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        idBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddLabeledText(idBox, "Slot ID", _slotIdText, 0, 0);
        AddLabeledText(idBox, "Display name", _displayNameText, 2, 0);
        AddLabeledText(idBox, "Playable →", _targetPlayableText, 0, 1, 3);
        AddLabeledText(idBox, "Cutscene →", _targetCutsceneText, 0, 2, 3);
        AddLabeledText(idBox, "DCMD →", _targetDcmdText, 0, 3, 3);
        layout.Controls.Add(idBox, 0, 4);

        _useAsBaseButton.Text = "Use as base →";
        _useAsBaseButton.Dock = DockStyle.Left;
        _useAsBaseButton.Width = 160;
        _useAsBaseButton.Click += async (_, _) => await UseAsBase();
        layout.Controls.Add(_useAsBaseButton, 0, 5);

        _detectedLabel.Dock = DockStyle.Fill;
        _detectedLabel.ForeColor = Theme.OnDarkMuted;
        _detectedLabel.Text = "No base set yet.";
        layout.Controls.Add(_detectedLabel, 0, 6);

        return box;
    }

    private void BrowseBaseMi()
    {
        using var dlg = new OpenFileDialog { Filter = "Material Instance (*.uasset)|*.uasset|All files|*.*" };
        var start = AppSettings.Current.EffectiveExtractedContentRoot();
        if (Directory.Exists(start))
        {
            dlg.InitialDirectory = start;
        }
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _matBaseText.Text = dlg.FileName;
        }
    }

    private static List<(CustomStaticMeshImport Mesh, string SourcePath)> CaptureCustomMeshSources(
        NativeSuitProject project,
        string projectOutputDirectory)
    {
        var outputRoot = Path.GetFullPath(projectOutputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var captured = new List<(CustomStaticMeshImport Mesh, string SourcePath)>();
        foreach (var mesh in project.CustomStaticMeshes)
        {
            if (string.IsNullOrWhiteSpace(mesh.SourceObjRelativePath))
            {
                throw new InvalidOperationException($"Custom mesh '{mesh.DisplayName}' has no project-owned OBJ source.");
            }
            var source = Path.GetFullPath(Path.Combine(outputRoot, mesh.SourceObjRelativePath));
            if (!source.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Custom mesh '{mesh.DisplayName}' points outside its suit project.");
            }
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"The saved OBJ for custom mesh '{mesh.DisplayName}' is missing.", source);
            }
            captured.Add((mesh, source));
        }
        return captured;
    }

    private sealed record BaseStageDirectorySnapshot(
        string Name,
        string StagePath,
        string BackupPath,
        bool Existed);

    private sealed class BaseStageFilesystemSnapshot
    {
        public required string SlotRoot { get; init; }
        public required string BackupRoot { get; init; }
        public required IReadOnlyList<BaseStageDirectorySnapshot> Stages { get; init; }
        public required string IncompleteMarkerPath { get; init; }
        public required bool IncompleteMarkerExisted { get; init; }
        public byte[]? IncompleteMarkerContents { get; init; }
        public required string ProjectPath { get; init; }
        public required bool ProjectFileExisted { get; init; }
        public string? ProjectFileContents { get; init; }
    }

    private static void CopyBaseStageDirectory(string sourceDirectory, string destinationDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Generated stage snapshot source was not found: {source}");
        }
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            FileSystemPathUtil.IsWithinDirectory(destination, source))
        {
            throw new InvalidOperationException("Refused to copy a generated stage into itself.");
        }

        Directory.CreateDirectory(destination);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly).ToList();
        foreach (var file in files.Where(file =>
                     !Path.GetFileName(file).Equals(CompletedGraftStageMarkerName, StringComparison.OrdinalIgnoreCase)))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Generated stage snapshots do not follow reparse-point files: {file}");
            }
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Generated stage snapshots do not follow reparse-point directories: {directory}");
            }
            CopyBaseStageDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        // A completion marker is a commit record, not ordinary payload. Copy it only after the
        // complete subtree has succeeded so an interrupted restore can never expose a partial
        // GraftedPartStage as packageable.
        foreach (var marker in files.Where(file =>
                     Path.GetFileName(file).Equals(CompletedGraftStageMarkerName, StringComparison.OrdinalIgnoreCase)))
        {
            if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Generated stage snapshots do not follow reparse-point files: {marker}");
            }
            File.Copy(marker, Path.Combine(destination, Path.GetFileName(marker)), overwrite: true);
        }
    }

    private async Task<BaseStageFilesystemSnapshot> CaptureBaseStageFilesystemAsync(
        string projectRoot,
        string slotId)
    {
        slotId = (slotId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(slotId) ||
            slotId is "." or ".." ||
            slotId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !Path.GetFileName(slotId).Equals(slotId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refused to stage an unsafe slot ID: '{slotId}'.");
        }

        var guiOutputRoot = Path.GetFullPath(Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects"));
        Directory.CreateDirectory(guiOutputRoot);
        var slotRoot = Path.GetFullPath(Path.Combine(guiOutputRoot, slotId));
        if (!FileSystemPathUtil.IsWithinDirectory(slotRoot, guiOutputRoot))
        {
            throw new InvalidOperationException("Refused to snapshot a generated stage outside NativeSuitGuiProjects.");
        }

        var backupContainer = Path.GetFullPath(Path.Combine(guiOutputRoot, ".base-stage-backups"));
        var backupRoot = Path.GetFullPath(Path.Combine(backupContainer, Guid.NewGuid().ToString("N")));
        if (!FileSystemPathUtil.IsWithinDirectory(backupContainer, guiOutputRoot) ||
            !FileSystemPathUtil.IsWithinDirectory(backupRoot, backupContainer))
        {
            throw new InvalidOperationException("Refused to create a base-stage backup outside the generated output root.");
        }

        var stageNames = new[]
        {
            "UnpatchedStage",
            "PatchedNameMapStage",
            "GraftedPartStage",
            "GraftedTorso2Stage",
        };
        var stages = stageNames.Select(name =>
        {
            var stagePath = Path.GetFullPath(Path.Combine(slotRoot, name));
            var backupPath = Path.GetFullPath(Path.Combine(backupRoot, name));
            if (!FileSystemPathUtil.IsWithinDirectory(stagePath, slotRoot) ||
                !FileSystemPathUtil.IsWithinDirectory(backupPath, backupRoot))
            {
                throw new InvalidOperationException($"Refused unsafe generated stage path for '{name}'.");
            }
            return new BaseStageDirectorySnapshot(
                name,
                stagePath,
                backupPath,
                Directory.Exists(stagePath));
        }).ToList();
        var incompleteMarkerPath = Path.Combine(slotRoot, IncompleteDeclarativeStageMarkerName);
        var incompleteMarkerExisted = File.Exists(incompleteMarkerPath);
        var incompleteMarkerContents = incompleteMarkerExisted
            ? File.ReadAllBytes(incompleteMarkerPath)
            : null;
        var projectPath = Path.GetFullPath(
            new SuitProjectService(projectRoot).ProjectPathForSlot(slotId));
        if (!FileSystemPathUtil.IsWithinDirectory(projectPath, guiOutputRoot) ||
            !projectPath.EndsWith(".native-suit-project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refused to snapshot a suit project outside NativeSuitGuiProjects.");
        }
        var projectFileExisted = File.Exists(projectPath);
        var projectFileContents = projectFileExisted ? File.ReadAllText(projectPath) : null;

        try
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    Directory.CreateDirectory(backupRoot);
                    foreach (var stage in stages.Where(stage => stage.Existed))
                    {
                        // A copy retry starts from an empty destination so a partial first copy
                        // can never masquerade as a complete snapshot.
                        if (Directory.Exists(stage.BackupPath))
                        {
                            Directory.Delete(stage.BackupPath, recursive: true);
                        }
                        CopyBaseStageDirectory(stage.StagePath, stage.BackupPath);
                    }
                    return true;
                },
                $"snapshot generated stages for slot '{slotId}'");
        }
        catch
        {
            try
            {
                if (Directory.Exists(backupRoot))
                {
                    Directory.Delete(backupRoot, recursive: true);
                }
            }
            catch
            {
                // The source stages were never changed. A partial backup can be safely left for
                // manual cleanup if another process keeps it locked.
            }
            throw;
        }

        return new BaseStageFilesystemSnapshot
        {
            SlotRoot = slotRoot,
            BackupRoot = backupRoot,
            Stages = stages,
            IncompleteMarkerPath = incompleteMarkerPath,
            IncompleteMarkerExisted = incompleteMarkerExisted,
            IncompleteMarkerContents = incompleteMarkerContents,
            ProjectPath = projectPath,
            ProjectFileExisted = projectFileExisted,
            ProjectFileContents = projectFileContents,
        };
    }

    private async Task ClearBaseStageFilesystemAsync(BaseStageFilesystemSnapshot snapshot)
    {
        foreach (var stage in snapshot.Stages)
        {
            if (!FileSystemPathUtil.IsWithinDirectory(stage.StagePath, snapshot.SlotRoot))
            {
                throw new InvalidOperationException($"Refused to clear unsafe generated stage path: {stage.StagePath}");
            }
            await RunWithFileLockRetryAsync(
                () =>
                {
                    File.Delete(Path.Combine(stage.StagePath, CompletedGraftStageMarkerName));
                    if (Directory.Exists(stage.StagePath))
                    {
                        Directory.Delete(stage.StagePath, recursive: true);
                    }
                    return true;
                },
                $"clear {stage.Name} for the new base");
        }
    }

    private async Task RestoreBaseStageFilesystemAsync(BaseStageFilesystemSnapshot snapshot)
    {
        var restoreErrors = new List<Exception>();
        foreach (var stage in snapshot.Stages)
        {
            try
            {
                await RunWithFileLockRetryAsync(
                    () =>
                    {
                        // Invalidate a newly-built stage before any destructive restore work. If
                        // another file stays locked, packaging still cannot accept the partial tree.
                        File.Delete(Path.Combine(stage.StagePath, CompletedGraftStageMarkerName));
                        if (Directory.Exists(stage.StagePath))
                        {
                            Directory.Delete(stage.StagePath, recursive: true);
                        }
                        if (stage.Existed)
                        {
                            if (!Directory.Exists(stage.BackupPath))
                            {
                                throw new DirectoryNotFoundException(
                                    $"Base-stage backup is missing for {stage.Name}: {stage.BackupPath}");
                            }
                            CopyBaseStageDirectory(stage.BackupPath, stage.StagePath);
                        }
                        return true;
                    },
                    $"restore previous {stage.Name}");
            }
            catch (Exception ex)
            {
                restoreErrors.Add(new InvalidOperationException(
                    $"Could not restore the previous {stage.Name}.", ex));
            }
        }

        var projectFileRestored = false;
        try
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    if (snapshot.ProjectFileExisted)
                    {
                        AtomicFileUtil.WriteAllText(
                            snapshot.ProjectPath,
                            snapshot.ProjectFileContents ?? string.Empty);
                    }
                    else
                    {
                        File.Delete(snapshot.ProjectPath);
                    }
                    return true;
                },
                "restore the previous suit project file");
            projectFileRestored = true;
        }
        catch (Exception ex)
        {
            restoreErrors.Add(new InvalidOperationException(
                "Could not restore the previous suit project file.", ex));
        }

        try
        {
            if (projectFileRestored && restoreErrors.Count == 0)
            {
                await RunWithFileLockRetryAsync(
                    () =>
                    {
                        if (snapshot.IncompleteMarkerExisted)
                        {
                            File.WriteAllBytes(
                                snapshot.IncompleteMarkerPath,
                                snapshot.IncompleteMarkerContents ?? Array.Empty<byte>());
                        }
                        else
                        {
                            File.Delete(snapshot.IncompleteMarkerPath);
                        }
                        return true;
                    },
                    "restore the previous declarative-stage transaction marker");
            }
            else
            {
                // Never reinstate a prior packageable marker state when any stage or project
                // rollback failed. A base-only project has no graft completion marker to protect
                // it, so this root sentinel is the final fail-closed guard for every stage kind.
                await RunWithFileLockRetryAsync(
                    () =>
                    {
                        Directory.CreateDirectory(snapshot.SlotRoot);
                        File.WriteAllText(
                            snapshot.IncompleteMarkerPath,
                            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                        return true;
                    },
                    "retain an incomplete marker after failed base-stage rollback");
            }
        }
        catch (Exception ex)
        {
            restoreErrors.Add(new InvalidOperationException(
                "Could not restore the previous declarative-stage transaction marker.", ex));
        }

        if (restoreErrors.Count > 0)
        {
            throw new AggregateException(
                $"One or more previous generated stages could not be restored. Backup retained at {snapshot.BackupRoot}",
                restoreErrors);
        }

        await DiscardBaseStageFilesystemBackupAsync(snapshot, logFailure: true);
    }

    private async Task<bool> DiscardBaseStageFilesystemBackupAsync(
        BaseStageFilesystemSnapshot snapshot,
        bool logFailure)
    {
        try
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    if (Directory.Exists(snapshot.BackupRoot))
                    {
                        Directory.Delete(snapshot.BackupRoot, recursive: true);
                    }
                    return true;
                },
                "discard completed base-stage backup");
            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                AppendLog($"  warning: base-stage backup could not be removed and was left at {snapshot.BackupRoot}: {ex.Message}");
            }
            return false;
        }
    }

    private void RestoreAfterFailedBaseChange(NativeSuitProject snapshot)
    {
        _currentProject = snapshot;
        ApplyProjectToFields(snapshot);
        _basePlayableText.Text = snapshot.PlayableTemplate?.Uasset ?? "";
        _baseCutsceneText.Text = snapshot.CutsceneTemplate?.Uasset ?? "";
        _baseDcmdText.Text = snapshot.DcmdTemplate?.Uasset ?? "";
        UpdateSelectedLabels();
        _detectedLabel.ForeColor = Theme.Warn;
        _detectedLabel.Text = "Base change did not complete; the previous saved project was kept.";
        _session.RaiseChanged();
        RefreshInspector();
        RefreshToyboxTiles();
    }

    private bool _useAsBaseInProgress;

    private async Task<bool> UseAsBase()
    {
        if (_useAsBaseInProgress)
        {
            AppendLog("A base change is already running; the second request was ignored.");
            return false;
        }

        _useAsBaseInProgress = true;
        var workspaceWasEnabled = _mainWorkspaceHost.Enabled;
        _mainWorkspaceHost.Enabled = false;
        var gateHeld = false;
        try
        {
            // Own the rebuild gate before EnsureProject or any shared project/UI mutation. The
            // disabled workspace prevents another edit from changing _currentProject while an
            // awaited copy/retry yields back to the WinForms message loop.
            await RebuildGate.WaitAsync();
            gateHeld = true;
            return await UseAsBaseCore();
        }
        catch (Exception ex)
        {
            AppendLog("Use-as-base stopped before its stage transaction could complete: " + ex.Message);
            return false;
        }
        finally
        {
            if (gateHeld)
            {
                RebuildGate.Release();
            }
            _mainWorkspaceHost.Enabled = workspaceWasEnabled;
            _useAsBaseInProgress = false;
        }
    }

    private async Task<bool> UseAsBaseCore()
    {
        EnsureProject();
        if (_currentProject is null || _projectService is null)
        {
            return false;
        }

        var contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var playable = TemplateFromUasset(_basePlayableText.Text.Trim(), "playable", contentRoot);
        var cutscene = TemplateFromUasset(_baseCutsceneText.Text.Trim(), "cutscene", contentRoot);
        if (playable is null)
        {
            AppendLog("Playable base .uasset not found, or not under the Extracted content root (see Settings).");
            return false;
        }
        if (cutscene is null)
        {
            AppendLog("Cutscene base .uasset not found, or not under the Extracted content root (see Settings).");
            return false;
        }

        var baseEligibility = BaseEligibilityService.Evaluate(cutscene.PackagePath, playable.PackagePath);
        if (!baseEligibility.IsReady)
        {
            AppendLog($"Base not staged: {baseEligibility.Detail}");
            return false;
        }
        if (!IsEligibleGameplayDonor(playable, contentRoot, out var donorDetail))
        {
            AppendLog($"Base not staged: the playable donor is not ready ({donorDetail}). Pick a real playable donor for movement, equipment, and runtime behavior.");
            return false;
        }

        var projectRoot = _projectRootText.Text.Trim();
        NativeSuitProject previousProjectSnapshot;
        List<(CustomStaticMeshImport Mesh, string SourcePath)> previousMeshSources;
        try
        {
            previousProjectSnapshot = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(_currentProject))
                ?? throw new InvalidOperationException("Could not snapshot the current suit before changing its base.");
            previousMeshSources = CaptureCustomMeshSources(
                _currentProject,
                _projectService.ProjectOutputDirectory(_currentProject));
        }
        catch (Exception ex)
        {
            AppendLog("Base not staged: " + ex.Message);
            return false;
        }

        var previousSlotId = _currentProject.SlotId;
        var previousProjectPath = _projectService.ProjectPathForSlot(previousSlotId);
        _currentProject.PlayableTemplate = playable;
        _currentProject.CutsceneTemplate = cutscene;
        _currentProject.DcmdTemplate = TemplateFromUasset(_baseDcmdText.Text.Trim(), "dcmd", contentRoot);
        _currentProject.VisualSourceTemplate = cutscene;
        _currentProject.VisualCutsceneSourceTemplate = cutscene;
        _currentProject.BaseProfile = BaseEligibilityService.CreateProfile(cutscene.PackagePath, playable.PackagePath);
        var metadataDonor = NativeMetadataDonorService.TryRead(
            _currentProject.DcmdTemplate,
            _currentProject.PlayableTemplate,
            _currentProject.CutsceneTemplate);
        if (!string.IsNullOrWhiteSpace(metadataDonor?.ProgressTag) &&
            (string.IsNullOrWhiteSpace(_currentProject.ProgressTag) ||
             _currentProject.ProgressTag.Equals("GameProgress.Definitions.Characters.Batman.TheBatman2025", StringComparison.OrdinalIgnoreCase)))
        {
            _currentProject.ProgressTag = metadataDonor.ProgressTag;
        }
        DeriveOutputs();
        ReadFieldsIntoProject(_currentProject);
        if (!ValidateUseAsBaseTargetPackages(_currentProject))
        {
            RestoreAfterFailedBaseChange(previousProjectSnapshot);
            RefreshInspector();
            return false;
        }
        if (!previousSlotId.Equals(_currentProject.SlotId, StringComparison.OrdinalIgnoreCase))
        {
            var destinationProjectPath = _projectService.ProjectPathForSlot(_currentProject.SlotId);
            var destinationOutputDirectory = _projectService.ProjectOutputDirectory(_currentProject);
            var destinationHasProject = File.Exists(destinationProjectPath);
            var destinationHasOwnedFiles = Directory.Exists(destinationOutputDirectory) &&
                                           Directory.EnumerateFileSystemEntries(destinationOutputDirectory).Any();
            if (destinationHasProject || destinationHasOwnedFiles)
            {
                AppendLog(
                    $"Base not staged: slot '{_currentProject.SlotId}' already belongs to another saved/generated suit. " +
                    "Choose a different suit identity or remove that project explicitly; Batcomputer will not overwrite its JSON, stages, or ImportedMeshes.");
                RestoreAfterFailedBaseChange(previousProjectSnapshot);
                return false;
            }
        }
        if (!previousSlotId.Equals(_currentProject.SlotId, StringComparison.OrdinalIgnoreCase) &&
            previousMeshSources.Count > 0)
        {
            try
            {
                var importer = new CustomStaticMeshImportService();
                foreach (var (mesh, sourcePath) in previousMeshSources)
                {
                    importer.CopySourceIntoProject(projectRoot, _currentProject, mesh, sourcePath);
                }
                AppendLog($"Copied {previousMeshSources.Count} project-owned custom OBJ source(s) into the new suit slot '{_currentProject.SlotId}'.");
            }
            catch (Exception ex)
            {
                AppendLog("Base not staged: custom mesh sources could not be copied into the new suit slot. " + ex.Message);
                RestoreAfterFailedBaseChange(previousProjectSnapshot);
                return false;
            }
        }
        ApplyProjectToFields(_currentProject);
        UpdateSelectedLabels();

        BaseStageFilesystemSnapshot? stageFilesystemSnapshot = null;
        try
        {
            stageFilesystemSnapshot = await CaptureBaseStageFilesystemAsync(
                    projectRoot,
                    _currentProject.SlotId);
                try
                {
                    // Base-only suits need the same durable crash guard as declarative suits.
                    // Write it before clearing any stage; Finalize removes it only after the new
                    // project JSON and all generated outputs form one committed unit.
                    await MarkDeclarativeStageIncompleteAsync(_currentProject, projectRoot);
                    // Start the new base from clean generated stages. ImportedMeshes is a sibling
                    // project-owned source directory and is deliberately outside this transaction.
                    await ClearBaseStageFilesystemAsync(stageFilesystemSnapshot);
                    AppendLog("  snapshotted and cleared UnpatchedStage, PatchedNameMapStage, GraftedPartStage, and legacy GraftedTorso2Stage for the base change.");

                    _projectService.CreateUnpatchedStage(_currentProject);
                    AppendLog($"Staged base: {playable.Stem} + {cutscene.Stem}{(_currentProject.DcmdTemplate is null ? " (no DCMD)" : " + DCMD")}");
                    if (!PatchNameMapsWithUAssetApi())
                    {
                        throw new InvalidOperationException(
                            "Base stage did not complete. Fix the patch error logged above, then set the base again.");
                    }

                    // Replay every declarative edit after changing the base. Custom meshes and
                    // material/removal-only suits used to disappear here because only native
                    // PartGrafts triggered this pass.
                    if (ProjectRequiresCompletedGraftStage(_currentProject))
                    {
                        await RebuildGraftStageCoreAsync(persistProject: false);
                    }

                    // Commit the project identity only after the new base and every declarative
                    // edit have staged successfully. Until here the previous project JSON, stage
                    // directories, and mod links remain recoverable.
                    var savedProjectPath = _projectService.SaveProject(_currentProject);
                    await FinalizeDeclarativeGraftStageAsync(_currentProject, projectRoot);
                    if (!previousSlotId.Equals(_currentProject.SlotId, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var relinked = ModService.RelinkSuitReferences(
                                previousSlotId,
                                previousProjectPath,
                                _currentProject,
                                savedProjectPath);
                            if (relinked > 0)
                            {
                                AppendLog($"Updated {relinked} mod suit reference(s) for '{_currentProject.SlotId}'.");
                            }
                            _projectService.DeleteSavedProjectFile(previousProjectPath);
                            AppendLog($"Replaced the temporary project ID '{previousSlotId}' with '{_currentProject.SlotId}'.");
                        }
                        catch (Exception migrationError)
                        {
                            // The new-slot JSON and its stages are already a consistent, valid
                            // unit. Keep that unit (an orphan is recoverable) and leave the old
                            // slot untouched instead of rolling the new stages back underneath the
                            // newly saved JSON.
                            AppendLog(
                                $"  warning: the new slot was staged and saved, but old-slot/mod-reference cleanup did not finish: {migrationError.Message}");
                        }
                    }

                    // Backup cleanup is bounded and best effort. If an external process locks the
                    // backup itself, the completed new stage remains authoritative and the backup
                    // path is logged for later cleanup.
                    await DiscardBaseStageFilesystemBackupAsync(stageFilesystemSnapshot, logFailure: true);
                    stageFilesystemSnapshot = null;
                }
                catch (Exception stageFailure)
                {
                    if (stageFilesystemSnapshot is not null)
                    {
                        var recoveryBackupRoot = stageFilesystemSnapshot.BackupRoot;
                        try
                        {
                            await RestoreBaseStageFilesystemAsync(stageFilesystemSnapshot);
                            AppendLog("  restored the previous generated stages after the failed base change.");
                            stageFilesystemSnapshot = null;
                        }
                        catch (Exception restoreFailure)
                        {
                            throw new AggregateException(
                                $"The base change failed and its previous generated stages could not be fully restored. " +
                                $"Recovery backup: {recoveryBackupRoot}",
                                stageFailure,
                                restoreFailure);
                        }
                    }
                    throw;
                }
        }
        catch (Exception ex)
        {
            AppendLog("Use-as-base failed:");
            AppendLog(ex.ToString());
            RestoreAfterFailedBaseChange(previousProjectSnapshot);
            return false;
        }

        _detectedLabel.ForeColor = Color.SeaGreen;
        _detectedLabel.Text = $"Base set → {_targetPlayableText.Text.Trim()} + _Cutscene. Now go to step 2 (Materials) or step 3 (Parts).";
        AppendLog("Base ready.");
        SelectComboValue(_toyboxCategoryCombo, "Base");
        PopulateToyboxSlots();
        RefreshInspector();
        RefreshToyboxTiles();
        return true;
    }

    private void ApplyResearchToolsVisibility()
    {
        var showResearch = AppSettings.Current.ShowResearchTools;

        if (_researchRailButton is not null)
        {
            _researchRailButton.Visible = showResearch;
        }

        var researchCategoryIndex = -1;
        for (var i = 0; i < _toyboxCategoryCombo.Items.Count; i++)
        {
            if (string.Equals(_toyboxCategoryCombo.Items[i]?.ToString(), "Research", StringComparison.OrdinalIgnoreCase))
            {
                researchCategoryIndex = i;
                break;
            }
        }

        if (showResearch && researchCategoryIndex < 0)
        {
            _toyboxCategoryCombo.Items.Add("Research");
        }
        else if (!showResearch && researchCategoryIndex >= 0)
        {
            if (string.Equals(_toyboxCategoryCombo.SelectedItem?.ToString(), "Research", StringComparison.OrdinalIgnoreCase))
            {
                SelectComboValue(_toyboxCategoryCombo, "Home");
            }

            for (var i = _toyboxCategoryCombo.Items.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_toyboxCategoryCombo.Items[i]?.ToString(), "Research", StringComparison.OrdinalIgnoreCase))
                {
                    _toyboxCategoryCombo.Items.RemoveAt(i);
                }
            }
        }

        var hasResearchTab = _inspectorTabs.ContainsTab(ResearchTabName);
        if (showResearch && !hasResearchTab)
        {
            _inspectorTabs.AddTab(ResearchTabName, _researchInspector);
        }
        else if (!showResearch && hasResearchTab)
        {
            if (string.Equals(_inspectorTabs.SelectedName, ResearchTabName, StringComparison.OrdinalIgnoreCase))
            {
                _inspectorTabs.SelectTab(SuitTabName);
            }
            _inspectorTabs.RemoveTab(ResearchTabName);
        }

        SelectInspectorTabForCategory();
    }

    private void RebaseCurrentSuitToActiveDump()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        var newRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        if (string.IsNullOrWhiteSpace(newRoot) || !Directory.Exists(newRoot))
        {
            AppendLog($"Rebase: the active extracted Content root is not usable ({newRoot}). Set it in Setup first.");
            return;
        }

        var svc = new RebaseSuitService();
        var preview = svc.Rebase(_currentProject, newRoot, apply: false);

        var lines = preview.Select(c => c.Status switch
        {
            "ok" => $"  {c.Role}:  REBASE\n      from {c.OldPath}\n      to   {c.NewPath}",
            "unchanged" => $"  {c.Role}:  already current",
            "missing" => $"  {c.Role}:  ✗ NOT FOUND in the new dump\n      wanted {c.NewPath}",
            _ => $"  {c.Role}:  (no template set)",
        });

        var willChange = preview.Count(c => c.Status == "ok");
        var missing = preview.Count(c => c.Status == "missing");

        var body =
            $"Active dump:\n  {newRoot}\n\n" +
            string.Join("\n", lines) + "\n\n" +
            (missing > 0
                ? "⚠ One or more templates are missing from the new dump. Re-extract that character, or re-pick the base manually.\n\n"
                : "") +
            (willChange == 0
                ? "Nothing to change — this suit already points at the active dump."
                : $"Apply {willChange} rebase(s)? Only the base SOURCE paths change; your suit's own /Game/Mods/... output paths are untouched.");

        if (willChange == 0)
        {
            Dialog.Info(this, "Rebase suit", body);
            AppendLog("Rebase: nothing to change — already on the active dump.");
            return;
        }

        if (!Dialog.Confirm(this, "Rebase suit to current dump", body,
                confirmText: "Rebase",
                severity: missing > 0 ? Dialog.Level.Warn : Dialog.Level.Info))
        {
            return;
        }

        var applied = svc.Rebase(_currentProject, newRoot, apply: true);
        foreach (var c in applied.Where(c => c.Status == "ok"))
        {
            AppendLog($"Rebased {c.Role} → {c.NewPath}");
        }

        // Reflect the new source paths in the UI + persist.
        _basePlayableText.Text = _currentProject.PlayableTemplate?.Uasset ?? "";
        _baseCutsceneText.Text = _currentProject.CutsceneTemplate?.Uasset ?? "";
        _baseDcmdText.Text = _currentProject.DcmdTemplate?.Uasset ?? "";
        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); }
        catch (Exception ex) { AppendLog($"Rebase: save failed: {ex.Message}"); }

        RecordChange("Base", _suitNameText.Text.Trim(), $"rebased {applied.Count(c => c.Status == "ok")} template(s) to the active dump");
        AppendLog("Rebase complete. Re-stage (Base → Set base) and re-package to rebuild against the new dump.");
        _session.RaiseChanged();
    }
}
