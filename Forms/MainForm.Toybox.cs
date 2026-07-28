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
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        // 72px gives the wordmark room and lets the suit block centre properly.
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Theme.StyleTooltip(_toyboxToolTip); // readable dark tooltips app-wide

        // Command bar hosted in its designer-editable shell.
        var commandBar = new CommandBarControl { Dock = DockStyle.Fill };
        commandBar.HostContent(CreateToyboxHeader());
        outer.Controls.Add(commandBar, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(6), BackColor = Theme.PanelBg };
        _toyboxBodyLayout = body;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        // Wider than the old 226 list column: the minifig figure is width-bound (its height follows
        // its width), and it needs side gutters for the callout labels.
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.Controls.Add(body, 0, 1);

        // Category rail hosted in its designer-editable shell.
        var workflowRail = new WorkflowRailControl { Dock = DockStyle.Fill };
        workflowRail.HostContent(CreateCategoryRail());
        body.Controls.Add(workflowRail, 0, 0);

        // Character panel - the "Your Character" designer-editable control owns the row flow;
        // MainForm wires the drop targets (drag a part/material onto the character).
        _yourCharacter.Dock = DockStyle.Fill;
        _yourCharacter.Margin = new Padding(3);
        WireToyboxCharacterDropTarget(_yourCharacter.SlotFlow);
        WireToyboxCharacterDropTarget(_yourCharacter);
        // The figure covers the panel now, so it has to accept the drops the rows used to.
        WireMinifigDropTarget(_yourCharacter.Diagram);
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
        var categories = new List<object> { "Home", "Base", "Materials", "Textures", "Parts", "Equipment", "Gliders", "Animations", "3D viewer", "Build mod", "Review" };
        if (AppSettings.Current.ShowResearchTools)
        {
            categories.Add("Research");
        }
        _toyboxCategoryCombo.Items.AddRange(categories.ToArray());
        _toyboxCategoryCombo.SelectedIndex = 0;
        _toyboxCategoryCombo.Visible = false;
        _toyboxCategoryCombo.SelectedIndexChanged += (_, _) => { PopulateToyboxTypes(); UpdatePrimaryAction(); ConfigureToyboxFilters(); SelectInspectorTabForCategory(); RefreshToyboxTiles(); };
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
        _toyboxSearchText.PlaceholderText = "Search toybox...";
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

        // The 3D viewer shares the same cell; it is built on first use because listing every
        // character means scanning the paks.
        _viewerHostLayout = toyLayout;

        _toyboxSelectionLabel.Dock = DockStyle.Fill;
        _toyboxSelectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _toyboxSelectionLabel.ForeColor = Theme.OnDarkMuted;
        toyLayout.Controls.Add(_toyboxSelectionLabel, 0, 2);

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Margin = new Padding(3) };
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
        // Toybox workspace hosted in its designer-editable shell (UI Phase 1/2, host-unchanged;
        // the tile browser is decomposed + virtualized in UI Phase 4).
        var toybox = new ToyboxControl { Dock = DockStyle.Fill };
        toybox.HostContent(toyBox);
        rightSplit.Panel1.Controls.Add(toybox);
        rightSplit.Panel2.Controls.Add(CreateInspectorTabs());
        body.Controls.Add(rightSplit, 2, 0);

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
        // Flat ground, not a gradient. Child controls that clear themselves resolve the ground from
        // BackColor, so a painted gradient shows up as lighter/darker boxes behind them.
        var header = new Panel { Dock = DockStyle.Fill, BackColor = HeaderGround };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var w = Math.Max(1, header.Width);
            var h = Math.Max(1, header.Height);
            // Gold underline fading to the right, rather than a hard rule across the whole bar.
            using var line = new LinearGradientBrush(new Rectangle(0, h - 2, w, 2),
                Theme.Gold, Color.FromArgb(0, Theme.GoldDim), LinearGradientMode.Horizontal);
            g.FillRectangle(line, 0, h - 2, w, 2);
        };
        header.Resize += (_, _) => header.Invalidate();

        // --- brand -----------------------------------------------------------
        var brand = new Panel { Dock = DockStyle.Left, Width = 196, BackColor = Color.Transparent };
        var wordmark = EmbeddedAssets.Load("Header.png");
        if (wordmark is not null)
        {
            brand.Paint += (_, e) =>
            {
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
        }
        else
        {
            // No art: still read as branded.
            brand.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, Text = "BATCOMPUTER", ForeColor = Theme.Gold,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold | FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent,
                Padding = new Padding(18, 0, 0, 0),
            });
        }
        header.Controls.Add(brand);

        var divider = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Color.Transparent };
        divider.Paint += (_, e) =>
        {
            var dh = Math.Max(2, divider.Height - 26);
            using var b = new LinearGradientBrush(new Rectangle(0, 0, 1, dh),
                Color.FromArgb(0, 58, 63, 73), Color.FromArgb(255, 58, 63, 73), LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(b, 0, 13, 1, dh);
        };
        header.Controls.Add(divider);

        // --- actions (right) --------------------------------------------------
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 540,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            WrapContents = false,
            Padding = new Padding(0, 19, 12, 0),
        };

        _menuButton.Text = "☰";
        _menuButton.Width = 36; _menuButton.Height = 34; _menuButton.Margin = new Padding(6, 0, 0, 0);
        Theme.StyleDarkButton(_menuButton);
        _menuButton.ForeColor = Theme.Gold;
        _menuButton.Click += (_, _) =>
        {
            var menu = BuildMainMenu();
            menu.Show(_menuButton, new Point(_menuButton.Width - menu.Width, _menuButton.Height));
        };

        _toyboxInstallButton.Text = "↓  Install mod";
        _toyboxInstallButton.Width = 112; _toyboxInstallButton.Height = 34; _toyboxInstallButton.Margin = new Padding(6, 0, 0, 0);
        Theme.StyleDarkButton(_toyboxInstallButton);
        _toyboxInstallButton.Click += (_, _) => InstallModForCurrentSuit();

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
        right.Controls.Add(_toyboxInstallButton);
        right.Controls.Add(_toyboxPackageButton);
        right.Controls.Add(_toyboxSaveButton);
        right.Controls.Add(_toyboxStatusChip);
        header.Controls.Add(right);

        // --- suit identity (fills the middle) ---------------------------------
        var suit = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        _suitNameText.BorderStyle = BorderStyle.None;
        _suitNameText.BackColor = SuitNameGround;
        _suitNameText.ForeColor = Theme.OnDark;
        _suitNameText.Font = new Font("Segoe UI", 13.5f, FontStyle.Bold);

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

        // Meta line: the mod folder stays editable, slot and pak are read-only echoes.
        // TextBox cannot be transparent, so it uses a solid colour sampled from the header
        // gradient at this height instead.
        _modFolderText.BorderStyle = BorderStyle.None;
        _modFolderText.BackColor = HeaderMetaGround;
        _modFolderText.ForeColor = Theme.OnDarkMuted;
        _modFolderText.Font = Theme.Caption;
        _tipsHeader.SetToolTip(_modFolderText, "Mod folder for this suit");

        _headerMetaLabel = new Label
        {
            AutoSize = false, Height = 16,
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        suit.Controls.Add(_suitNameText);
        suit.Controls.Add(_suitNamePencil);
        suit.Controls.Add(_modFolderText);
        suit.Controls.Add(_headerMetaLabel);

        void LayoutSuit()
        {
            var mid = suit.Height / 2;
            _suitNameText.Top = mid - 21;
            _suitNameText.Left = 18;
            _suitNameText.Width = Math.Max(90, Math.Min(280, suit.Width - 70));
            _suitNamePencil.Top = _suitNameText.Top + 2;
            _suitNamePencil.Left = _suitNameText.Right + 5;

            _modFolderText.Top = mid + 7;
            _modFolderText.Left = 18;
            _modFolderText.Width = 120;
            _headerMetaLabel.Top = mid + 6;
            _headerMetaLabel.Left = _modFolderText.Right + 2;
            _headerMetaLabel.Width = Math.Max(40, suit.Width - _headerMetaLabel.Left - 8);
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
        var cats = new[] { ("Home", "⌂"), ("Base", "◱"), ("Materials", "◈"), ("Textures", "▣"), ("Parts", "◆"), ("Equipment", "★"), ("Gliders", "︾"), ("Animations", "➤"), ("3D viewer", "◐"), ("Build mod", "▰"), ("Review", "✎"), ("Research", "⌕") };

        // Load PNGs per category instead of requiring every category to have one.
        // Home/Textures can fall back to glyphs without disabling the real bundled
        // art for Materials/Parts/Faces/etc.
        foreach (var (cat, glyph) in cats)
        {
            var button = RailButton(cat, glyph);
            if (cat.Equals("Research", StringComparison.OrdinalIgnoreCase))
            {
                _researchRailButton = button;
                button.Visible = AppSettings.Current.ShowResearchTools;
            }
            rail.Controls.Add(button);
        }
        return rail;
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
        SetHeaderCommandState(_toyboxInstallButton, hasOpenSuit && hasBase, isPrimary: false,
            readyHint: "Install the built mod for the current suit",
            unavailableHint: "Set a visual base, gameplay donor, and build a mod before installing.");

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

    private string DescriptionTileSubtitle()
    {
        var d = _descriptionText.Text.Trim();
        if (string.IsNullOrWhiteSpace(d))
        {
            return "add menu text";
        }
        return d.Length <= 22 ? d : d[..21] + "…";
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
        var baseMachinery = AnimArchetypeGraftService.DetectDonor(playableDisk, extracted, null);
        if (baseMachinery is null || !baseMachinery.Valid)
        {
            AppendLog($"'{UnrealPathUtil.AssetName(playablePackage)}' is a villain/NPC — its body & movement live in its NPC class, so it can't be reparented into a working playable. Reskinning its look onto the runtime's proven playable base.");

            // CRITICAL: the runtime bridge ALWAYS ping-pongs through the TheBatman2025 donor
            // (DerivePawnTag is fixed to Pawns.Playable.Batman.TheBatman2025). The game applies
            // that donor's family identity/DPRD to the spawned pawn, so the reskin MUST build on a
            // Batman-family playable - building on Talia (or any other family) makes the spawned
            // pawn's identity fight its body → invisible/glitched (the exact failure). So force the
            // machinery donor to TheBatman2025's own playable (the ping-pong-matching base).
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
            villainVisual = AnimArchetypeGraftService.ExtractCharacterMaterials(playableDisk, villainFolder, null);

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
        SelectComboValue(_toyboxCategoryCombo, "Materials");
        _session.RaiseChanged(); // UI Phase 2: single project-state refresh (Your Character + Inspector)
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

        var gameplay = TemplateFromUasset(FindPlayableSiblingForVisual(visualDisk) ?? "", "playable", extracted);
        if (!IsEligibleGameplayDonor(gameplay, extracted, out var donorDetail))
        {
            AppendLog($"Visual source '{visual.Stem}' needs a gameplay donor: {donorDetail}");
            var donorPackage = PromptForMachineryDonor();
            if (string.IsNullOrWhiteSpace(donorPackage))
            {
                AppendLog("Visual base not staged. Pick a gameplay donor to provide movement, equipment, and runtime behavior.");
                return;
            }
            gameplay = TemplateFromUasset(PackageToExtractedUasset(donorPackage, extracted), "playable", extracted);
            if (!IsEligibleGameplayDonor(gameplay, extracted, out donorDetail))
            {
                AppendLog($"The selected gameplay donor cannot be used: {donorDetail}");
                return;
            }
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
        SelectComboValue(_toyboxCategoryCombo, "Materials");
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
        AppendLog("Applied the visual source's body and face materials to the gameplay donor.");
    }

    private async Task ApplyVisualAttachmentsToGameplayDonorAsync(string visualSourcePackage)
    {
        if (_currentProject is null || _partIndex is null)
        {
            return;
        }

        var sourceParts = _partIndex.Parts
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
                : FindCounterpartPart(source, "playable") ?? source;
            var cutscene = source.Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase)
                ? source
                : FindCounterpartPart(source, "cutscene") ?? source;
            UpsertPartGraft(source.Slot, false, playable, cutscene);
        }

        var sourceKinds = sourceParts
            .Select(p => VisualKindOf(p.Slot))
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = new List<string>();
        try
        {
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

    private void OpenBaseWizardManual()
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
        _ = UseAsBase();
        var fam = GameDataService.Instance.FamilyForBasePath(wiz.PlayablePath)?.Name ?? "unknown family";
        RecordChange("Base", wiz.SuitName, $"{System.IO.Path.GetFileName(wiz.PlayablePath)} ({fam})");
        UpdateToyboxChips();
        SelectComboValue(_toyboxCategoryCombo, "Materials");
        _session.RaiseChanged(); // UI Phase 2: single project-state refresh (Your Character + Inspector)
    }

    private void SelectInspectorTabForCategory()
    {
        var research = _toyboxCategoryCombo.SelectedItem?.ToString()
            ?.Equals("Research", StringComparison.OrdinalIgnoreCase) == true &&
            AppSettings.Current.ShowResearchTools &&
            _inspectorTabs.ContainsTab(ResearchTabName);
        if (_inspectorTabs.Count > 0)
        {
            _inspectorTabs.SelectTab(research ? ResearchTabName : SuitTabName);
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
            _session.RaiseChanged(); // UI Phase 2: single project-state refresh (Your Character + Inspector)
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
                // Split by whether the gadget belongs to a playable family: "Playable equipment"
                // (a real hero gadget - proven) vs "Testing / boss" (no family - boss/NPC weapons
                // that don't reliably work as player gear yet). Default to the playable set.
                _toyboxTypeCombo.Items.AddRange(new object[] { "Playable equipment", "Testing / boss (no family)", "All gadgets" });
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
    }

    /// <summary>Switches the tile cell to the virtualized grid and loads it with <paramref name="tiles"/>.
    /// Optional <paramref name="header"/> note is painted above the tiles; <paramref name="emptyMessage"/>
    /// shows when the list is empty.</summary>
    private void ShowVirtualTiles(IReadOnlyList<VirtualTilePanel.Tile> tiles, string header = "", string emptyMessage = "",
        VirtualTilePanel.HeroModel? hero = null)
    {
        _toyboxTileFlow.Visible = false;
        _toyboxTileGrid.Visible = true;
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

    /// <summary>
    /// Home follows the actual release hierarchy: choose a mod, build its suits,
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
        var currentSlot = _slotIdText.Text.Trim();
        var currentSuitIsInActiveMod = hasActiveMod && activeEntries.Any(entry =>
            string.Equals(entry.SuitId, currentSlot, StringComparison.OrdinalIgnoreCase));

        var chips = new List<(string, Color)>
        {
            (hasActiveMod ? "active mod" : "no active mod", hasActiveMod ? Theme.Research : Theme.Warn),
            ($"{(hasActiveMod ? activeSuitCount : savedSuits.Count)} suit{((hasActiveMod ? activeSuitCount : savedSuits.Count) == 1 ? "" : "s")}", Theme.Parts),
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
            ThumbAccent = hasActiveMod ? Theme.Research : Theme.Gold,
            Chips = chips,
            Workflow = new[]
            {
                new VirtualTilePanel.HeroModel.WorkflowStep
                {
                    Label = "1. MOD",
                    Detail = hasActiveMod ? "mod selected" : "choose a mod",
                    Accent = Theme.Research,
                    Complete = hasActiveMod,
                    Current = !hasActiveMod,
                },
                new VirtualTilePanel.HeroModel.WorkflowStep
                {
                    Label = "2. SUITS",
                    Detail = hasActiveMod
                        ? (activeSuitCount > 0 ? "add or edit" : "next: add a suit")
                        : "select a mod first",
                    Accent = Theme.Base,
                    Current = hasActiveMod && activeSuitCount == 0,
                },
                new VirtualTilePanel.HeroModel.WorkflowStep
                {
                    Label = "3. BUILD",
                    Detail = activeSuitCount > 0 ? "release when ready" : "add a suit first",
                    Accent = Theme.Gold,
                    Current = hasActiveMod && activeSuitCount > 0,
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
                Accent = isActive ? Theme.Research : Theme.OnDarkMuted,
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
                Accent = Theme.Parts,
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
                    Accent = Theme.Parts,
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
            Accent = Theme.Parts,
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
                Accent = Theme.Parts,
                Image = capturedSummary is null ? null : LoadSuitCoverImage(capturedSummary),
                OnClick = capturedSummary is null ? () => EditModSuits(modPath) : () => OpenRecentProject(capturedSummary.Path),
                MenuFactory = capturedSummary is null ? null : () => BuildSuitTileMenu(capturedSummary),
            });
        }

        if (activeSuitCount == 0)
        {
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = SectionBuild,
                Title = "Add a suit first",
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
                    Title = "Validate current suit",
                    Subtitle = "preflight checks",
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
            tiles.Add(new() { Section = SectionIdentity, Title = "Description", Subtitle = DescriptionTileSubtitle(), Accent = Theme.Base, OnClick = EditSuitDescription });
            tiles.Add(new() { Section = SectionIdentity, Title = "Set icons", Subtitle = "menu / UIMD", Accent = Theme.Base, OnClick = OpenIconsDialog });
        }

        ShowVirtualTiles(tiles, hero: hero);
    }

    private void RefreshToyboxTiles()
    {
        ClearToyboxTiles();
        UpdateToyboxChips();
        var category = _toyboxCategoryCombo.SelectedItem?.ToString();
        var type = _toyboxTypeCombo.SelectedItem?.ToString();

        if (category == ViewerCategory)
        {
            SetHomeInspectorCollapsed(false);
            ShowViewerPanel();
            return;
        }
        HideViewerPanel();
        SetHomeInspectorCollapsed(category == "Home");

        if (category == "Home")
        {
            RefreshHomeTiles();
            return;
        }

        if (category == "Build mod")
        {
            RefreshBuildModTiles();
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

            if (!isAttachment && _partIndex is null)
            {
                LoadPartIndexAndRefreshGrid(logIfMissing: false);
            }

            if (!isAttachment && (_partIndex is null || _partIndex.Parts.Count == 0))
            {
                _toyboxTileFlow.Controls.Add(MakeTile("Build index", "scan extracted BPs", () => { _ = BuildPartIndexAsync(); }, Theme.Parts, dashed: true));
                _toyboxTileFlow.Controls.Add(MakeNoteTile("First-time setup must point at your UAssetGUI extracted Content dump. Then build the part index to fill this toybox.\n\nTip: switch the dropdown to 'Attachment: Hair' or 'Attachment: Hat' — those come from the shipped catalog and need no part index."));
                return;
            }

            var parts = ToyboxPartCandidates(selectedSlot).ToList();
            if (parts.Count == 0)
            {
                // Few controls - keep the note on the flow surface.
                _toyboxTileFlow.Controls.Add(MakeNoteTile($"No indexed parts matched '{selectedSlot}'. Try <all parts> or rebuild the part index after changing setup paths."));
                return;
            }

            // Virtualized: render ALL matches (no paging / "Load more") - only visible tiles paint.
            ShowVirtualTiles(parts.Select(PartTile).ToList());
            return;
        }

        if (category == "Animations")
        {
            RefreshAnimationTiles(type);
            return;
        }

        if (category == "Review")
        {
            RefreshReviewTiles(type);
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
                    menu.Items.Add("Remove this change", null, (_, _) => RemoveReviewChange(c));
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
    private void RemoveReviewChange(SavedChange change)
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

        _currentProject.Changes.Remove(change);

        // Best-effort revert of the persisted intent by category.
        switch (change.Category)
        {
            case "Gliders":
                _currentProject.GliderType = "";
                _currentProject.GliderMaterial = "";
                _currentProject.GliderGrafted = false;
                _currentProject.GliderAnimLas = "";
                _currentProject.GliderAnimMas = "";
                AppendLog("Cleared glider intent (visual + glide-animation injection). Re-pick base to fully rebuild the stage if the glide component was already repointed.");
                break;
            case "Equipment":
                _currentProject.EquipmentSlots.Clear();
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
                    ApplySavedMaterials(_currentProject, logIfNone: false);
                }
                break;
        }

        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
        AppendLog($"Removed change: {change.Category} · {change.Target}");
        _session.RaiseChanged(); // UI Phase 2: single project-state refresh (Your Character + Inspector)
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

        var basePath = _basePlayableText.Text.Trim();
        var family = gd.FamilyForBasePath(basePath);
        var familyLabel = family?.Name ?? "unknown";

        // "Playable equipment" = gadget belongs to at least one playable family (a real hero gadget,
        // proven). "Testing / boss (no family)" = NativeFamilies empty (boss/NPC weapons like
        // FreezeGun/MachineGun - not reliably usable as player gear yet; parked research).
        // Default (empty) + "Playable equipment" + "All gadgets" show playable; only the testing
        // view (or "All") shows no-family gadgets.
        var showPlayable = filter != "Testing / boss (no family)";
        var showTesting = filter == "Testing / boss (no family)" || filter == "All gadgets";

        var header = filter == "Testing / boss (no family)"
            ? "Testing / boss equipment: gadgets with NO playable family (boss/NPC weapons, e.g. FreezeGun). These do NOT reliably work as player gear yet — parked research. Use at your own risk."
            : family is null
                ? $"Playable equipment (belongs to a hero family). Base family not recognized from '{(basePath.Length > 0 ? basePath : "<no base playable set>")}' — set the base playable to see ✓/⚠ per-gadget anim compatibility."
                : $"Playable equipment. Base family: {familyLabel}.   ✓ native anims  ·  ⚠ foreign gadget — its anim sets graft in on package (needs the custom archetype).   Data shipped with the tool — no extraction needed.";

        var familyFilter = FilterVal(0);   // owning family
        var search = CurrentToyboxSearch();
        var tiles = new List<VirtualTilePanel.Tile>();
        foreach (var eq in gd.Db.Equipment)
        {
            if (!MatchesToyboxSearch(search, eq.Name, string.Join(" ", eq.NativeFamilies)))
            {
                continue;
            }

            var hasFamily = eq.NativeFamilies.Count > 0;
            if (hasFamily && !showPlayable)
            {
                continue;
            }
            if (!hasFamily && !showTesting)
            {
                continue;
            }

            if (familyFilter is not null &&
                !eq.NativeFamilies.Contains(familyFilter, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var compat = gd.CheckEquipment(eq.Name, basePath);

            var (glyph, accent) = compat.Level switch
            {
                GameDataService.Compatibility.Native => ("", Theme.Equipment),
                GameDataService.Compatibility.Foreign => ("⚠", Color.FromArgb(220, 160, 40)),
                _ => ("•", Theme.OnDarkMuted),
            };

            var owners = eq.NativeFamilies.Count > 0 ? string.Join("/", eq.NativeFamilies) : "no family";
            var capturedEq = eq;
            var capturedCompat = compat;
            tiles.Add(new VirtualTilePanel.Tile
            {
                Title = string.IsNullOrEmpty(glyph) ? eq.Name : $"{glyph} {eq.Name}",
                Subtitle = owners,
                Accent = accent,
                OnClick = () => ShowEquipmentCompatDetail(capturedEq, capturedCompat),
            });
        }
        ShowVirtualTiles(tiles, header, emptyMessage: "No gadgets matched the current filter/search.");
    }

    private void ShowEquipmentCompatDetail(GameDataEquipment eq, GameDataService.CompatResult compat)
    {
        var isForeign = compat.Level == GameDataService.Compatibility.Foreign;
        var isHeld = eq.VisualAbilities.Count > 0;

        // AnimArchetypeGraftService.Graft() clones MAS_Char/LAS_Char and injects a foreign gadget's
        // anim blocks at package time - but ONLY when the suit uses its own archetype, and only for
        // the anim sets the gadget actually ships. Both facts drive what this dialog says.
        var hasLayerAnims = !string.IsNullOrEmpty(eq.LayerAnimSet);
        var hasMontageAnims = !string.IsNullOrEmpty(eq.MontageAnimSet);
        var hasGraft = hasLayerAnims || hasMontageAnims;
        var customArchetype = _currentProject?.UseCustomArchetype == true;

        var model = new Dialog.Model
        {
            WindowTitle = "Equipment",
            Title = eq.Name,
            Subtitle = eq.NativeFamilies.Count > 0
                ? $"Native to {string.Join(", ", eq.NativeFamilies)}"
                : "No native family",
            Severity = isForeign ? Dialog.Level.Warn : Dialog.Level.Good,
            PrimaryText = "Add gadget",
            SecondaryText = "Cancel",
        };
        model.Chips.Add((compat.Level.ToString(), isForeign ? Theme.Warn : Theme.Good));
        model.Chips.Add((isHeld ? "Held" : "Thrown", isHeld ? Theme.Info : Theme.OnDarkMuted));
        model.Chips.Add((hasGraft ? "anims graftable" : "no anim set", hasGraft ? Theme.Good : Theme.Warn));

        if (!isHeld)
        {
            model.Message =
                "This is a throwing weapon — it has no persistent held mesh and is only visible while " +
                "aiming or throwing. That's the same as the base game. Use a held gadget (Whip, Batons, " +
                "BattleStaff, CatClaws, Drone, Goggles, Tablet…) if you want it visible in-hand.";
        }

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

        if (isForeign)
        {
            if (!hasGraft)
            {
                model.CalloutTitle = "No animation set to graft";
                model.CalloutDetail =
                    $"{eq.Name} is from another family and ships no equipment anim set, so there's nothing " +
                    "to graft in. It will equip, but its animations may look wrong.";
            }
            else if (!customArchetype)
            {
                model.CalloutTitle = "Turn on the custom archetype";
                model.CalloutDetail =
                    "Its animations graft in automatically when you package — but only if this suit uses " +
                    "its own archetype. With that off, the gadget equips but may animate wrong.";
            }
            else
            {
                model.Severity = Dialog.Level.Info;
                model.CalloutTitle = "Animations graft in when you package";
                model.CalloutDetail =
                    $"{eq.Name} is from another family, so its anim sets are cloned into this suit's " +
                    "MAS/LAS and its loadout entry added automatically on the next package.";
            }
        }

        if (!Dialog.Show(this, model))
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

        // Persist immediately - otherwise a staged gadget is lost if the suit is
        // reopened before packaging (reload reads the on-disk project, which never
        // saw the change). This is why an equipped gadget could silently vanish.
        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }

        var note = compat.Level == GameDataService.Compatibility.Foreign
            ? "foreign — will equip; anims may need a graft"
            : "native anims";
        RecordChange("Equipment", $"slot {slot + 1}", $"{eq.Name} ({note})", status: "staged");
        AppendLog($"Staged '{eq.Name}' into equipment slot {slot + 1} and saved. See Review.");
        PopulateToyboxSlots(); // keep the character-panel Equipment row in sync
    }

    private Button MakeTile(string title, string subtitle, Action onClick, Color accent, bool dashed = false)
    {
        // Owner-drawn rounded card (matches the VirtualTilePanel tiles) instead of the
        // old flat accent-outline Button.
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
                    new FilterGroup("Source", "Any source", PartSources()));
                break;
            case "Equipment":
                // Family (who owns the gadget) is concrete + base-independent. (Native/Foreign is
                // base-dependent, so it lives as the ✓/⚠ badge on each tile instead of a filter.)
                _toyboxFilters.SetGroups(
                    new FilterGroup("Family", "Any family", EquipmentFamilies()));
                break;
            case "Gliders":
                _toyboxFilters.SetGroups(
                    new FilterGroup("Source", "Any source", GliderSources()));
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
        _detectedLabel.ForeColor = Color.DimGray;
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

    private async Task<bool> UseAsBase()
    {
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

        EnsureProject();
        if (_currentProject is null || _projectService is null)
        {
            return false;
        }

        DeriveOutputs();
        ReadFieldsIntoProject(_currentProject);
        _currentProject.PlayableTemplate = playable;
        _currentProject.CutsceneTemplate = cutscene;
        _currentProject.DcmdTemplate = TemplateFromUasset(_baseDcmdText.Text.Trim(), "dcmd", contentRoot);
        _currentProject.VisualSourceTemplate = cutscene;
        _currentProject.VisualCutsceneSourceTemplate = cutscene;
        _currentProject.BaseProfile = BaseEligibilityService.CreateProfile(cutscene.PackagePath, playable.PackagePath);
        if (!ValidateUseAsBaseTargetPackages(_currentProject))
        {
            return false;
        }
        UpdateSelectedLabels();

        try
        {
            // Hold the rebuild gate across the WHOLE staging pass: PatchNameMapsWithUAssetApi writes
            // the PatchedNameMapStage packages EXCLUSIVELY, and a concurrent rebuild copies that same
            // stage - overlapping them threw "used by another process". Call the gate-free core for
            // the replay so we don't deadlock re-acquiring our own gate.
            await RebuildGate.WaitAsync();
            try
            {
            // Purge any stale GraftedPartStage from a PRIOR base/graft session. Re-picking the
            // base regenerates the PatchedNameMapStage, but the graft stage is only copied from
            // patched when it doesn't already exist - so a leftover stage keeps its old (possibly
            // broken/orphaned) component grafts. Worse, an orphaned static "Head" component there
            // makes the next hair graft CLONE that orphan (same-asset path) instead of building a
            // fresh donor shell, propagating the break. Deleting it forces a clean start.
            var staleGraftStage = Path.Combine(
                AppSettings.GeneratedRootFor(_projectRootText.Text.Trim()), "NativeSuitGuiProjects",
                _currentProject.SlotId, "GraftedPartStage");
            if (Directory.Exists(staleGraftStage))
            {
                try
                {
                    Directory.Delete(staleGraftStage, recursive: true);
                    AppendLog("  cleared stale GraftedPartStage (fresh base → fresh graft stage).");
                }
                catch (Exception cleanupEx)
                {
                    AppendLog($"  ⚠ could not clear old GraftedPartStage: {cleanupEx.Message}");
                }
            }

            _projectService.CreateUnpatchedStage(_currentProject);
            AppendLog($"Staged base: {playable.Stem} + {cutscene.Stem}{(_currentProject.DcmdTemplate is null ? " (no DCMD)" : " + DCMD")}");
            PatchNameMapsWithUAssetApi();
            _projectService.SaveProject(_currentProject);
            _detectedLabel.ForeColor = Color.SeaGreen;
            _detectedLabel.Text = $"Base set → {_targetPlayableText.Text.Trim()} + _Cutscene. Now go to step 2 (Materials) or step 3 (Parts).";
            AppendLog("Base ready.");
            // Re-picking the base purged the graft stage - replay the suit's declared parts
            // onto the fresh base so grafted hair/hats/etc. survive a re-base (mirrors how
            // materials/removals are replayed).
            if (_currentProject.PartGrafts.Count > 0)
            {
                await RebuildGraftStageCoreAsync();
            }
            PopulateToyboxSlots();
            RefreshToyboxTiles();
            }
            finally
            {
                RebuildGate.Release();
            }
        }
        catch (Exception ex)
        {
            AppendLog("Use-as-base failed:");
            AppendLog(ex.ToString());
            return false;
        }
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
