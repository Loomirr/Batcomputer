using System.Text;

namespace Batcomputer;

/// <summary>
/// Edits one suit's declarative DPRD AbilitySets composition. The form works on a private copy and
/// returns intent to MainForm; it never writes a project or cooked asset itself.
/// </summary>
public sealed class AbilityExplorerForm : AdaptiveForm
{
    private readonly NativeSuitProject _project;
    private readonly AbilityEditorCatalog _catalog;
    private readonly AbilityLoadoutProfile _working;
    private readonly Dictionary<string, AbilitySetCatalogEntry> _setsByPackage;
    private readonly HashSet<string> _inheritedPackages;

    private readonly TextBox _search = new();
    private readonly ThemedDropDown _view = new();
    private readonly ThemedDropDown _fightingStyle = new();
    private readonly TreeView _tree = new();
    private readonly Label _summary = new();
    private readonly Label _detailTitle = new();
    private readonly Label _detailSubtitle = new();
    private readonly RichTextBox _details = new();
    private readonly CheckBox _unsafeCoreEdits = new();
    private readonly Button _addSet = new();
    private readonly Button _removeSet = new();
    private readonly Button _moveUp = new();
    private readonly Button _moveDown = new();
    private readonly Button _addGrant = new();
    private readonly Button _editGrant = new();
    private readonly Button _removeGrant = new();
    private readonly Button _save = new();
    private readonly Button _applyStyle = new();
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 180 };

    private SetNodeTag? _selectedSet;
    private GrantNodeTag? _selectedGrant;
    private bool _resetToDonor;
    private bool _initializingUnsafe;

    public AbilityLoadoutProfile? ResultProfile { get; private set; }
    public bool ResetToDonorRequested => _resetToDonor;

    public AbilityExplorerForm(
        NativeSuitProject project,
        AbilityEditorCatalog catalog,
        string? initialSetPackage = null,
        string? initialGrantPackage = null,
        bool libraryView = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _setsByPackage = catalog.AvailableAbilitySets
            .Concat(catalog.InheritedAbilitySets)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PackagePath))
            .GroupBy(entry => Normalize(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _inheritedPackages = catalog.InheritedAbilitySets
            .Select(entry => Normalize(entry.PackagePath))
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _working = CreateWorkingProfile(project.AbilityLoadout, catalog);

        Text = "Batcomputer — Ability workshop";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1260, 860);
        MinimumSize = new Size(1040, 720);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;

        BuildLayout();
        if (libraryView)
        {
            _view.SelectedIndex = 1;
        }
        RebuildTree();
        if (!string.IsNullOrWhiteSpace(initialSetPackage))
        {
            SelectNode(_tree.Nodes, initialSetPackage, initialGrantPackage);
        }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            BackColor = Theme.WindowBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 214));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        Controls.Add(root);
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildMain(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildTree();
        };
        _view.SelectedIndexChanged += (_, _) => RebuildTree();
        _tree.AfterSelect += (_, e) => ShowNode(e.Node?.Tag);
        _unsafeCoreEdits.CheckedChanged += (_, _) => HandleUnsafeCoreToggle();
        _addSet.Click += (_, _) => AddSelectedSet();
        _removeSet.Click += (_, _) => ToggleSelectedSet();
        _moveUp.Click += (_, _) => MoveSelectedSet(-1);
        _moveDown.Click += (_, _) => MoveSelectedSet(1);
        _addGrant.Click += (_, _) => AddGrant();
        _editGrant.Click += (_, _) => EditGrant();
        _removeGrant.Click += (_, _) => ToggleSelectedGrant();
        FormClosed += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Dispose();
        };
    }

    private Control BuildHeader()
    {
        var card = Card(Theme.CardHi, new Padding(18, 10, 16, 8), new Padding(0, 0, 0, 10));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            BackColor = Color.Transparent,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 255));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "ABILITY WORKSHOP",
            Dock = DockStyle.Fill,
            Font = Theme.Title,
            ForeColor = Theme.Abilities,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        var introduction = new Label { Text = $"{_project.DisplayName} · choose one combat style, then customize its abilities", Dock = DockStyle.Fill,
            ForeColor = Theme.OnDarkMuted, Font = Theme.Caption, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        layout.Controls.Add(introduction, 0, 1);
        layout.SetColumnSpan(introduction, 2);

        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Search set, ability, input tag, category or package…";
        _search.Margin = new Padding(0, 1, 8, 0);
        Theme.StyleDarkInput(_search);
        layout.Controls.Add(_search, 0, 2);

        _view.Dock = DockStyle.Fill;
        _view.Items.AddRange(new object[] { "Current loadout", "Ability-set library" });
        _view.SelectedIndex = 0;
        _view.Margin = new Padding(0, 1, 0, 0);
        layout.Controls.Add(_view, 1, 2);

        _fightingStyle.Dock = DockStyle.Fill;
        _fightingStyle.Margin = new Padding(0, 5, 8, 0);
        _fightingStyle.Items.Add(new FightingStyleChoice("", "Fighting style: donor default"));
        foreach (var entry in FightingStyleLibraryService.Build(_setsByPackage.Values))
        {
            _fightingStyle.Items.Add(new FightingStyleChoice(entry.Id, entry.Label, entry));
        }
        var selectedStyle = Enumerable.Range(0, _fightingStyle.Items.Count)
            .FirstOrDefault(index => _fightingStyle.Items[index] is FightingStyleChoice choice &&
                                     choice.Id.Equals(_working.FightingStyleId, StringComparison.OrdinalIgnoreCase));
        _fightingStyle.SelectedIndex = selectedStyle;
        layout.Controls.Add(_fightingStyle, 0, 3);

        _applyStyle.Text = "Apply style bundle";
        _applyStyle.Dock = DockStyle.Fill;
        _applyStyle.Margin = new Padding(0, 5, 0, 0);
        Theme.StyleGoldButton(_applyStyle);
        _applyStyle.Click += (_, _) => ApplySelectedFightingStyle();
        _fightingStyle.SelectedIndexChanged += (_, _) =>
        {
            var choice = _fightingStyle.SelectedItem as FightingStyleChoice;
            _applyStyle.Text = choice?.Entry is { Profile: null } ? "Inspect source dependencies" : "Apply style bundle";
            if (choice?.Entry is { } entry) ShowFightingStyle(entry);
        };
        layout.Controls.Add(_applyStyle, 1, 3);
        var swordSettings = new Button { Text = "Combat settings…", Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 0) };
        Theme.StyleGoldButton(swordSettings);
        swordSettings.Click += (_, _) =>
        {
            if (!SwordCombatService.Enabled(_working))
            {
                Dialog.Info(this, "Select a player weapon preset", "Choose Sword, Baseball bat or Baton — player adapter and apply its style bundle first.");
                return;
            }
            using var editor = new SwordCombatSettingsForm(_working.SwordCombat ?? PlayerMeleeAdapterService.Defaults(_working.FightingStyleId), _working.FightingStyleId);
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                _working.SwordCombat = editor.Result;
                _resetToDonor = false;
                RebuildTree();
            }
        };
        var heldItems = new Button { Text = "Held items…  ·  add / edit / remove", Dock = DockStyle.Fill, Margin = new Padding(0, 5, 8, 0) };
        Theme.StyleDarkButton(heldItems);
        heldItems.Click += (_, _) => {
            using var editor = new HeldItemsForm(HeldItemService.Resolve(_working));
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            _working.HeldItems = editor.Result.Select(i => i.Clone()).ToList(); _resetToDonor = false;
            _search.Clear(); _view.SelectedIndex = 0; RebuildTree();
        };
        layout.Controls.Add(heldItems, 0, 4);
        layout.Controls.Add(swordSettings, 1, 4);
        return card;
    }

    private Control BuildMain()
    {
        // A percentage layout is intentionally used instead of SplitContainer. SplitContainer
        // validates/repaints its splitter while WinForms is still applying DPI and adaptive-window
        // bounds, which can throw before this dialog becomes visible on smaller/scaled displays.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.WindowBg,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildTreeCard(), 0, 0);
        layout.Controls.Add(BuildDetailCard(), 1, 0);
        return layout;
    }

    private Control BuildTreeCard()
    {
        var card = Card(Theme.CardBg, new Padding(12, 10, 12, 10), new Padding(0, 0, 2, 0));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "01  /  LOADOUT & BUNDLE",
            Dock = DockStyle.Fill,
            Font = Theme.Eyebrow,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        _tree.Dock = DockStyle.Fill;
        _tree.BackColor = Theme.SlateDark;
        _tree.ForeColor = Theme.OnDark;
        _tree.LineColor = Theme.SlateLight;
        _tree.BorderStyle = BorderStyle.None;
        _tree.HideSelection = false;
        _tree.FullRowSelect = true;
        _tree.ShowNodeToolTips = true;
        _tree.ShowRootLines = false;
        _tree.ItemHeight = Math.Max(30, TextRenderer.MeasureText("Ag", Theme.Body).Height + 10);
        _tree.Font = Theme.Body;
        _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        _tree.DrawNode += (_, e) =>
        {
            if (e.Node is null) return;
            var selected = (e.State & TreeNodeStates.Selected) != 0;
            var row = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(0, _tree.ClientSize.Width - e.Bounds.X), e.Bounds.Height);
            using var background = new SolidBrush(selected ? Theme.CardHi : _tree.BackColor);
            e.Graphics.FillRectangle(background, row);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, _tree.Font, row,
                selected ? Theme.OnDark : e.Node.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        };
        layout.Controls.Add(_tree, 0, 1);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 7, 0, 0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _summary.Dock = DockStyle.Fill;
        _summary.Font = Theme.Caption;
        _summary.ForeColor = Theme.OnDarkMuted;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        bottom.Controls.Add(_summary, 0, 0);

        var order = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        ConfigureSmallButton(_moveUp, "Move up", 84);
        ConfigureSmallButton(_moveDown, "Move down", 92);
        order.Controls.Add(_moveUp);
        order.Controls.Add(_moveDown);
        bottom.Controls.Add(order, 1, 0);
        layout.Controls.Add(bottom, 0, 2);
        return card;
    }

    private Control BuildDetailCard()
    {
        var card = Card(Theme.CardBg, new Padding(14, 10, 14, 10), new Padding(6, 0, 0, 0));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "02  /  SELECTION DETAILS",
            Dock = DockStyle.Fill,
            Font = Theme.Eyebrow,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        _detailTitle.Dock = DockStyle.Fill;
        _detailTitle.Font = Theme.Title;
        _detailTitle.ForeColor = Theme.OnDark;
        _detailTitle.AutoEllipsis = true;
        layout.Controls.Add(_detailTitle, 0, 1);
        _detailSubtitle.Dock = DockStyle.Fill;
        _detailSubtitle.Font = Theme.Caption;
        _detailSubtitle.ForeColor = Theme.OnDarkMuted;
        _detailSubtitle.AutoEllipsis = true;
        layout.Controls.Add(_detailSubtitle, 0, 2);

        var detailSurface = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SlateDark, Padding = new Padding(12, 10, 6, 10) };
        _details.Dock = DockStyle.Fill;
        _details.ReadOnly = true;
        _details.BorderStyle = BorderStyle.None;
        _details.BackColor = Theme.SlateDark;
        _details.ForeColor = Theme.OnDarkMuted;
        _details.Font = Theme.Body;
        _details.WordWrap = true;
        _details.DetectUrls = false;
        detailSurface.Controls.Add(_details);
        layout.Controls.Add(detailSurface, 0, 3);

        _unsafeCoreEdits.Text = "Advanced: allow removing or editing core ability entries";
        _unsafeCoreEdits.Dock = DockStyle.Fill;
        _unsafeCoreEdits.ForeColor = Theme.Warn;
        _unsafeCoreEdits.Font = Theme.Caption;
        _initializingUnsafe = true;
        _unsafeCoreEdits.Checked = _working.AllowUnsafeCoreEdits;
        _initializingUnsafe = false;
        layout.Controls.Add(_unsafeCoreEdits, 0, 4);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0),
        };
        ConfigureSmallButton(_addSet, "Add set", 82, primary: true);
        ConfigureSmallButton(_removeSet, "Remove set", 102);
        ConfigureSmallButton(_addGrant, "Add ability", 102, primary: true);
        ConfigureSmallButton(_editGrant, "Edit ability", 102);
        ConfigureSmallButton(_removeGrant, "Remove ability", 120);
        actions.Controls.AddRange(new Control[] { _addSet, _removeSet, _addGrant, _editGrant, _removeGrant });
        layout.Controls.Add(actions, 0, 5);
        ShowNode(null);
        return card;
    }

    private Control BuildFooter()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.WindowBg,
            Padding = new Padding(0, 10, 0, 0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var hint = new Label
        {
            Text = "Changes apply only to this suit's generated DPRD and AbilitySet clones.",
            Dock = DockStyle.Fill,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        layout.Controls.Add(hint, 0, 0);
        var reset = new Button { Text = "Reset to donor", Width = 132, Height = 34, Margin = new Padding(0, 0, 8, 0) };
        Theme.StyleDarkButton(reset);
        reset.Click += (_, _) => ResetToDonor();
        layout.Controls.Add(reset, 1, 0);
        var cancel = new Button { Text = "Cancel", Width = 92, Height = 34, Margin = new Padding(0, 0, 8, 0), DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        layout.Controls.Add(cancel, 2, 0);
        _save.Text = "Save abilities";
        _save.Width = 126;
        _save.Height = 34;
        _save.Margin = Padding.Empty;
        Theme.StyleGoldButton(_save);
        _save.Click += (_, _) => CommitAndClose();
        layout.Controls.Add(_save, 3, 0);
        CancelButton = cancel;
        return layout;
    }

    private void RebuildTree(string? selectPackage = null, string? selectGrant = null)
    {
        var query = _search.Text.Trim();
        var library = string.Equals(_view.SelectedItem?.ToString(), "Ability-set library", StringComparison.OrdinalIgnoreCase);
        var previousPackage = selectPackage ?? _selectedSet?.Selection.PackagePath;
        var previousGrant = selectGrant ?? _selectedGrant?.PackagePath;

        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            if (library)
            {
                BuildLibraryTree(query);
            }
            else
            {
                BuildCurrentTree(query);
            }
        }
        finally
        {
            _tree.EndUpdate();
        }

        var setCount = _working.AbilitySets.Count(set => set.Enabled);
        var changedSets = _working.AbilitySets.Count(IsSelectionChanged);
        _summary.Text = library
            ? $"{_catalog.AvailableAbilitySets.Count} cataloged set(s)"
            : $"{setCount} source sets · {changedSets} edited";

        if (!SelectNode(_tree.Nodes, previousPackage, previousGrant) && _tree.Nodes.Count > 0)
        {
            _tree.SelectedNode = _tree.Nodes[0];
        }
        if (_tree.Nodes.Count == 0) ShowNode(null);
    }

    private void BuildCurrentTree(string query)
    {
        var heldEntries = AbilityLoadoutPresentation.HeldEntries(_working, _project.TargetPackages.Playable);
        if (heldEntries.Count > 0 && Matches(query, "Held items", string.Join(' ', heldEntries.Select(e => e.Label + " " + e.Package + " " + e.Detail)))) {
            var node = new TreeNode("Held items  [independent · staged]") { ForeColor = Theme.Abilities,
                Tag = new BundleNodeTag("Held items", "Independent of combat", "Edit visibility, models and materials with Held items. No attacks are granted by these entries.") };
            foreach (var entry in heldEntries) node.Nodes.Add(new TreeNode(entry.Label) { ForeColor = Theme.Good, ToolTipText = entry.Package,
                Tag = new BundleNodeTag(entry.Label, "Held item · generated at build", entry.Package + "\n\n" + entry.Detail) });
            _tree.Nodes.Add(node); node.Expand();
        }
        if (FightingStyleProfileService.Find(_working.FightingStyleId) is { } style)
        {
            var entries = AbilityLoadoutPresentation.BundleEntries(_working, _project.TargetPackages.Playable);
            if (Matches(query, style.DisplayName, string.Join(' ', entries.Select(e => e.Label + " " + e.Package))))
            {
                var bundle = new TreeNode($"{style.DisplayName}  [staged bundle]") { ForeColor = Theme.Abilities,
                    Tag = new BundleNodeTag(style.DisplayName, "Applied to this suit when built", style.SafetySummary +
                        "\n\nGenerated dependencies below are read-only. Use the style picker or Combat settings to change them.") };
                foreach (var entry in entries)
                    bundle.Nodes.Add(new TreeNode(entry.Label) { ForeColor = Theme.Good, ToolTipText = entry.Package,
                        Tag = new BundleNodeTag(entry.Label, "Bundle dependency · generated / coordinated at build", entry.Package + "\n\n" + entry.Detail) });
                _tree.Nodes.Add(bundle); bundle.Expand();
            }
        }
        foreach (var selection in OrderedSelections())
        {
            _setsByPackage.TryGetValue(Normalize(selection.PackagePath), out var entry);
            var inherited = _inheritedPackages.Contains(Normalize(selection.PackagePath));
            var grants = CatalogGrants(selection, entry).ToList();
            if (!Matches(query, selection.PackagePath, entry?.DisplayName, entry?.Category,
                    string.Join(' ', grants.Select(grant => grant.PackagePath + " " + grant.InputTag)),
                    string.Join(' ', selection.AddedGameplayAbilities.Select(grant => grant.PackagePath + " " + grant.InputTag))))
            {
                continue;
            }

            var status = !selection.Enabled ? "removed" : !inherited ? "added" : IsSelectionChanged(selection) ? "changed" : "inherited";
            if (SwordCombatService.Enabled(_working) && selection.Enabled && AbilityDependencyService.IsCombatSet(selection.PackagePath))
                status = "source → " + PlayerMeleeAdapterService.Label(_working.FightingStyleId).ToLowerInvariant() + " bundle";
            var title = $"{(selection.Enabled ? "●" : "○")} {DisplayName(selection.PackagePath, entry)}  [{status}]";
            var tag = new SetNodeTag(selection, entry, inherited, IsLibraryNode: false);
            var node = new TreeNode(title)
            {
                Tag = tag,
                ToolTipText = selection.PackagePath,
                ForeColor = !selection.Enabled ? Theme.Warn : IsSelectionChanged(selection) ? Theme.Abilities : Theme.OnDark,
            };
            AddGrantNodes(node, selection, entry);
            _tree.Nodes.Add(node);
            if (IsSelectionChanged(selection)) node.Expand();
        }
    }

    private void BuildLibraryTree(string query)
    {
        var entries = _catalog.AvailableAbilitySets
            .Concat(_catalog.InheritedAbilitySets)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PackagePath))
            .DistinctBy(entry => Normalize(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
            .Where(entry => Matches(query, entry.DisplayName, entry.PackagePath, entry.Category,
                string.Join(' ', entry.GameplayAbilities.Select(grant => grant.PackagePath))))
            .OrderBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => DisplayName(entry.PackagePath, entry), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var categoryGroup in entries.GroupBy(
                     entry => string.IsNullOrWhiteSpace(entry.Category) ? "Other" : entry.Category,
                     StringComparer.OrdinalIgnoreCase))
        {
            var category = new TreeNode(categoryGroup.Key.ToUpperInvariant())
            {
                ForeColor = Theme.OnDarkMuted,
                ToolTipText = $"{categoryGroup.Count()} ability set(s)",
            };
            foreach (var entry in categoryGroup)
            {
                var selection = FindSelection(entry.PackagePath) ?? new AbilitySetSelection
                {
                    PackagePath = Normalize(entry.PackagePath),
                    Enabled = false,
                    Order = int.MaxValue,
                };
                var active = FindSelection(entry.PackagePath)?.Enabled == true;
                var tag = new SetNodeTag(selection, entry, _inheritedPackages.Contains(Normalize(entry.PackagePath)), IsLibraryNode: true);
                var node = new TreeNode($"{(active ? "✓" : "+")} {DisplayName(entry.PackagePath, entry)}")
                {
                    Tag = tag,
                    ToolTipText = entry.PackagePath,
                    ForeColor = active ? Theme.Good : entry.IsAvailable ? Theme.OnDark : Theme.Warn,
                };
                foreach (var grant in entry.GameplayAbilities)
                {
                    node.Nodes.Add(new TreeNode(UnrealPathUtil.AssetName(grant.PackagePath))
                    {
                        Tag = new GrantNodeTag(selection, entry, grant.PackagePath, grant.AbilityLevel, grant.InputTag, IsAdded: false, IsRemoved: false, IsLibraryNode: true),
                        ForeColor = Theme.OnDarkMuted,
                        ToolTipText = grant.PackagePath,
                    });
                }
                category.Nodes.Add(node);
            }
            category.Expand();
            _tree.Nodes.Add(category);
        }
    }

    private void AddGrantNodes(TreeNode parent, AbilitySetSelection selection, AbilitySetCatalogEntry? entry)
    {
        foreach (var grant in CatalogGrants(selection, entry))
        {
            var removed = selection.RemovedGameplayAbilities.Contains(Normalize(grant.PackagePath), StringComparer.OrdinalIgnoreCase);
            parent.Nodes.Add(new TreeNode($"{(removed ? "○" : "  ")} {UnrealPathUtil.AssetName(grant.PackagePath)}" +
                                          (string.IsNullOrWhiteSpace(grant.InputTag) ? "" : $"  ·  {grant.InputTag}"))
            {
                Tag = new GrantNodeTag(selection, entry, grant.PackagePath, grant.AbilityLevel, grant.InputTag, IsAdded: false, IsRemoved: removed, IsLibraryNode: false),
                ForeColor = removed ? Theme.Warn : Theme.OnDarkMuted,
                ToolTipText = grant.PackagePath,
            });
        }
        foreach (var grant in selection.AddedGameplayAbilities)
        {
            parent.Nodes.Add(new TreeNode($"+ {UnrealPathUtil.AssetName(grant.PackagePath)}" +
                                          (string.IsNullOrWhiteSpace(grant.InputTag) ? "" : $"  ·  {grant.InputTag}"))
            {
                Tag = new GrantNodeTag(selection, entry, grant.PackagePath, grant.AbilityLevel, grant.InputTag, IsAdded: true, IsRemoved: false, IsLibraryNode: false),
                ForeColor = Theme.Good,
                ToolTipText = grant.PackagePath,
            });
        }
    }

    private void ShowNode(object? tag)
    {
        _selectedSet = tag switch
        {
            SetNodeTag set => set,
            GrantNodeTag grantNode => new SetNodeTag(grantNode.Selection, grantNode.Catalog, _inheritedPackages.Contains(Normalize(grantNode.Selection.PackagePath)), grantNode.IsLibraryNode),
            _ => null,
        };
        _selectedGrant = tag as GrantNodeTag;

        if (tag is BundleNodeTag bundle)
        {
            _detailTitle.Text = bundle.Title;
            _detailSubtitle.Text = bundle.Subtitle;
            _details.Text = bundle.Detail;
        }
        else if (_selectedGrant is { } grant)
        {
            _detailTitle.Text = UnrealPathUtil.AssetName(grant.PackagePath);
            _detailSubtitle.Text = grant.IsAdded ? "Suit-local gameplay ability grant" : grant.IsRemoved ? "Removed donor grant" : "Inherited gameplay ability grant";
            var details = new StringBuilder();
            AddDetail(details, "Ability", grant.PackagePath);
            AddDetail(details, "Ability set", grant.Selection.PackagePath);
            AddDetail(details, "Level", grant.AbilityLevel.ToString());
            AddDetail(details, "Input tag", string.IsNullOrWhiteSpace(grant.InputTag) ? "Passive / none" : grant.InputTag);
            AddDetail(details, "Status", grant.IsAdded ? "Added for this suit" : grant.IsRemoved ? "Removed for this suit" : "Using gameplay donor");
            _details.Text = details.ToString().TrimEnd();
        }
        else if (_selectedSet is { } set)
        {
            _detailTitle.Text = DisplayName(set.Selection.PackagePath, set.Catalog);
            _detailSubtitle.Text = set.Selection.Enabled ? "Active AbilitySet" : set.IsLibraryNode ? "Available AbilitySet" : "Removed AbilitySet";
            var details = new StringBuilder();
            AddDetail(details, "Package", set.Selection.PackagePath);
            AddDetail(details, "Category", set.Catalog?.Category ?? "Unknown");
            AddDetail(details, "Source", set.Catalog?.Source ?? "Unknown");
            AddDetail(details, "Availability", set.Catalog?.IsAvailable == false ? "Missing from active extraction" : "Available");
            AddDetail(details, "Loadout", set.Inherited ? "Inherited from gameplay donor" : "Added for this suit");
            AddDetail(details, "Order", set.Selection.Order.ToString());
            AddDetail(details, "Known grants", (set.Catalog?.GameplayAbilities.Count ?? 0).ToString());
            AddDetail(details, "Added grants", set.Selection.AddedGameplayAbilities.Count.ToString());
            AddDetail(details, "Removed grants", set.Selection.RemovedGameplayAbilities.Count.ToString());
            if (IsCore(set))
            {
                details.AppendLine();
                details.AppendLine("CORE SET");
                details.AppendLine("Removing or editing this set can disable input, health, movement, or other required runtime behavior.");
            }
            _details.Text = details.ToString().TrimEnd();
        }
        else
        {
            _detailTitle.Text = "Choose an ability set";
            _detailSubtitle.Text = "Inspect the donor loadout or add a base-game/DLC set from the library.";
            _details.Text = _catalog.Warnings.Count == 0
                ? "CURRENT LOADOUT\n  Ordered AbilitySets read from the gameplay donor's DPRD.\n\nLIBRARY\n  Base-game and installed DLC AbilitySets available to this suit."
                : "CATALOG NOTES\n" + string.Join("\n", _catalog.Warnings.Select(warning => "• " + warning));
        }
        SetButtonState();
    }

    private void SetButtonState()
    {
        var set = _selectedSet;
        var grant = _selectedGrant;
        var librarySet = set?.IsLibraryNode == true;
        var currentSet = set is not null && !librarySet;
        var actualSelection = set is null ? null : FindSelection(set.Selection.PackagePath);

        _addSet.Visible = librarySet;
        _addSet.Enabled = librarySet && set?.Catalog?.IsAvailable != false && actualSelection?.Enabled != true;
        _removeSet.Visible = currentSet;
        _removeSet.Enabled = currentSet && set is not null && (!IsCore(set) || _working.AllowUnsafeCoreEdits || !set.Selection.Enabled);
        _removeSet.Text = set?.Selection.Enabled == false ? "Restore set" : "Remove set";
        var enabledOrder = OrderedEnabledSelections();
        _moveUp.Enabled = currentSet && set?.Selection.Enabled == true && enabledOrder.FindIndex(item => ReferenceEquals(item, set.Selection)) > 0;
        _moveDown.Enabled = currentSet && set?.Selection.Enabled == true && enabledOrder.FindIndex(item => ReferenceEquals(item, set.Selection)) is var index && index >= 0 && index < enabledOrder.Count - 1;
        _addGrant.Visible = currentSet;
        _addGrant.Enabled = currentSet && set?.Selection.Enabled == true && set.Catalog?.IsAvailable != false && (!IsCore(set) || _working.AllowUnsafeCoreEdits);
        _editGrant.Visible = grant?.IsAdded == true && !grant.IsLibraryNode;
        _editGrant.Enabled = _editGrant.Visible;
        _removeGrant.Visible = grant is not null && !grant.IsLibraryNode;
        _removeGrant.Enabled = _removeGrant.Visible && (set is null || !IsCore(set) || _working.AllowUnsafeCoreEdits || grant!.IsRemoved);
        _removeGrant.Text = grant?.IsRemoved == true ? "Restore ability" : "Remove ability";
    }

    private void AddSelectedSet()
    {
        if (_selectedSet is not { IsLibraryNode: true } selected || selected.Catalog?.IsAvailable == false)
        {
            return;
        }
        if (AbilityDependencyService.IsCombatSet(selected.Selection.PackagePath))
        {
            var style = AbilityDependencyService.StyleForMeleeSet(selected.Selection.PackagePath);
            if (style is null)
            {
                Dialog.Warn(
                    this,
                    "Combat style needs a complete preset",
                    $"{UnrealPathUtil.AssetName(selected.Selection.PackagePath)} is an exclusive combat set, but Batcomputer has not traced its combat effect, held-item, and animation closure yet. It was not added by itself.");
                return;
            }
            ApplyFightingStyle(style);
            return;
        }

        if (AbilityDependencyService.AddedSetCompatibilityError(
                WorkingProject(),
                selected.Selection.PackagePath) is { } dependencyError)
        {
            Dialog.Warn(this, "Ability-set dependency is missing", dependencyError);
            return;
        }

        if (AbilityDependencyService.CardinalityForSet(selected.Selection.PackagePath) ==
            AbilitySetCardinality.OneGrappleProfile)
        {
            foreach (var other in _working.AbilitySets.Where(selection =>
                         selection.Enabled &&
                         AbilityDependencyService.CardinalityForSet(selection.PackagePath) ==
                         AbilitySetCardinality.OneGrappleProfile &&
                         !SamePackage(selection.PackagePath, selected.Selection.PackagePath)).ToList())
            {
                if (_inheritedPackages.Contains(Normalize(other.PackagePath))) other.Enabled = false;
                else _working.AbilitySets.Remove(other);
            }
        }

        var existing = FindSelection(selected.Selection.PackagePath);
        if (existing is null)
        {
            existing = new AbilitySetSelection
            {
                PackagePath = Normalize(selected.Selection.PackagePath),
                Enabled = true,
                Order = NextOrder(),
            };
            _working.AbilitySets.Add(existing);
        }
        else
        {
            existing.Enabled = true;
            if (existing.Order == int.MaxValue) existing.Order = NextOrder();
        }
        _resetToDonor = false;
        _search.Clear();
        _view.SelectedItem = "Current loadout";
        RebuildTree(existing.PackagePath);
    }

    private void ToggleSelectedSet()
    {
        if (_selectedSet is not { IsLibraryNode: false } selected)
        {
            return;
        }
        var selection = FindSelection(selected.Selection.PackagePath);
        if (selection is null) return;
        if (selection.Enabled && AbilityDependencyService.RequiredSetRemovalReason(WorkingProject(), selection.PackagePath) is { } dependencyReason)
        {
            Dialog.Warn(this, "Ability set is required", dependencyReason);
            return;
        }
        if (selection.Enabled && IsCore(selected) && !_working.AllowUnsafeCoreEdits)
        {
            Dialog.Warn(this, "Core ability set is protected",
                "Enable 'Advanced: allow removing or editing core ability entries' before changing this set.");
            return;
        }
        if (selection.Enabled && IsCore(selected) && !ConfirmUnsafeCoreEdit("remove", selection.PackagePath)) return;

        if (selected.Inherited)
        {
            selection.Enabled = !selection.Enabled;
        }
        else
        {
            _working.AbilitySets.Remove(selection);
        }
        NormalizeOrder();
        _resetToDonor = false;
        RebuildTree(selected.Inherited ? selection.PackagePath : null);
    }

    private void MoveSelectedSet(int delta)
    {
        if (_selectedSet is not { IsLibraryNode: false } selected || !selected.Selection.Enabled) return;
        var ordered = OrderedEnabledSelections();
        var index = ordered.FindIndex(item => ReferenceEquals(item, selected.Selection));
        var other = index + delta;
        if (index < 0 || other < 0 || other >= ordered.Count) return;
        (ordered[index].Order, ordered[other].Order) = (ordered[other].Order, ordered[index].Order);
        NormalizeOrder();
        _resetToDonor = false;
        RebuildTree(selected.Selection.PackagePath);
    }

    private void AddGrant()
    {
        if (_selectedSet is not { IsLibraryNode: false } selected || !selected.Selection.Enabled) return;
        if (IsCore(selected) && !_working.AllowUnsafeCoreEdits) return;
        if (IsCore(selected) && !ConfirmUnsafeCoreEdit("add an ability to", selected.Selection.PackagePath)) return;

        using var dialog = new GameplayAbilityGrantDialog(_catalog.GameplayAbilities);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        if (AbilityDependencyService.AddedGrantCompatibilityError(WorkingProject(), dialog.Result.PackagePath) is { } dependencyError)
        {
            Dialog.Warn(this, "Ability dependency is missing", dependencyError);
            return;
        }
        UpsertGrant(selected.Selection, dialog.Result);
        _resetToDonor = false;
        _search.Clear();
        RebuildTree(selected.Selection.PackagePath, dialog.Result.PackagePath);
    }

    private void EditGrant()
    {
        if (_selectedGrant is not { IsAdded: true, IsLibraryNode: false } selected) return;
        var current = selected.Selection.AddedGameplayAbilities.FirstOrDefault(grant =>
            Normalize(grant.PackagePath).Equals(Normalize(selected.PackagePath), StringComparison.OrdinalIgnoreCase));
        if (current is null) return;
        using var dialog = new GameplayAbilityGrantDialog(_catalog.GameplayAbilities, current);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        if (AbilityDependencyService.AddedGrantCompatibilityError(WorkingProject(), dialog.Result.PackagePath) is { } dependencyError)
        {
            Dialog.Warn(this, "Ability dependency is missing", dependencyError);
            return;
        }
        selected.Selection.AddedGameplayAbilities.Remove(current);
        UpsertGrant(selected.Selection, dialog.Result);
        _resetToDonor = false;
        _search.Clear();
        RebuildTree(selected.Selection.PackagePath, dialog.Result.PackagePath);
    }

    private void ToggleSelectedGrant()
    {
        if (_selectedGrant is not { IsLibraryNode: false } selected) return;
        if (!selected.IsRemoved &&
            AbilityDependencyService.RequiredGrantRemovalReason(WorkingProject(), selected.PackagePath) is { } dependencyReason)
        {
            Dialog.Warn(this, "Ability is required", dependencyReason);
            return;
        }
        var set = new SetNodeTag(selected.Selection, selected.Catalog,
            _inheritedPackages.Contains(Normalize(selected.Selection.PackagePath)), false);
        if (IsCore(set) && !_working.AllowUnsafeCoreEdits) return;
        if (!selected.IsRemoved && IsCore(set) && !ConfirmUnsafeCoreEdit("change an ability in", selected.Selection.PackagePath)) return;

        if (selected.IsAdded)
        {
            selected.Selection.AddedGameplayAbilities.RemoveAll(grant =>
                Normalize(grant.PackagePath).Equals(Normalize(selected.PackagePath), StringComparison.OrdinalIgnoreCase));
        }
        else if (selected.IsRemoved)
        {
            selected.Selection.RemovedGameplayAbilities.RemoveAll(path =>
                Normalize(path).Equals(Normalize(selected.PackagePath), StringComparison.OrdinalIgnoreCase));
        }
        else if (!selected.Selection.RemovedGameplayAbilities.Contains(Normalize(selected.PackagePath), StringComparer.OrdinalIgnoreCase))
        {
            selected.Selection.RemovedGameplayAbilities.Add(Normalize(selected.PackagePath));
        }
        _resetToDonor = false;
        RebuildTree(selected.Selection.PackagePath, selected.PackagePath);
    }

    private void HandleUnsafeCoreToggle()
    {
        if (_initializingUnsafe) return;
        if (_unsafeCoreEdits.Checked && !_working.AllowUnsafeCoreEdits)
        {
            var accepted = Dialog.Confirm(this,
                "Unlock core ability edits?",
                "Core AbilitySets may provide input, health, spawning, movement, combat, and save-state behavior. Removing or changing one can make the suit unusable or crash only when a specific action starts.\n\nBatcomputer will still keep edits suit-local, but it cannot prove arbitrary combinations are gameplay-safe.",
                confirmText: "Unlock advanced edits",
                severity: Dialog.Level.Crit);
            if (!accepted)
            {
                _initializingUnsafe = true;
                _unsafeCoreEdits.Checked = false;
                _initializingUnsafe = false;
                return;
            }
        }
        _working.AllowUnsafeCoreEdits = _unsafeCoreEdits.Checked;
        SetButtonState();
    }

    private bool ConfirmUnsafeCoreEdit(string action, string package) => Dialog.Confirm(
        this,
        "Core ability edit",
        $"You are about to {action} '{UnrealPathUtil.AssetName(package)}'. This may disable required runtime behavior or crash when the affected action is used.\n\nThe base-game asset is never changed; only this suit's generated clone is affected.",
        confirmText: "Apply core edit",
        severity: Dialog.Level.Crit);

    private void ResetToDonor()
    {
        if (!Dialog.Confirm(this,
                "Reset all ability changes?",
                "This removes every set/grant change from this suit and restores the gameplay donor's complete ability loadout.",
                confirmText: "Reset abilities"))
        {
            return;
        }
        _working.AbilitySets.Clear();
        _working.FightingStyleId = "";
        _working.SwordCombat = null;
        _working.HeldItems = [];
        _working.DonorDprdPackage = Normalize(_catalog.DonorDprdPackage);
        _working.DonorAbilitySetFingerprint = _catalog.DonorAbilitySetFingerprint;
        _working.DonorAbilitySetPackages = _catalog.InheritedAbilitySets
            .Select(entry => Normalize(entry.PackagePath))
            .Where(package => package.Length > 0)
            .ToList();
        foreach (var (entry, index) in _catalog.InheritedAbilitySets.Select((entry, index) => (entry, index)))
        {
            _working.AbilitySets.Add(new AbilitySetSelection
            {
                PackagePath = Normalize(entry.PackagePath),
                Enabled = true,
                Order = index,
            });
        }
        _working.AllowUnsafeCoreEdits = false;
        _initializingUnsafe = true;
        _unsafeCoreEdits.Checked = false;
        _initializingUnsafe = false;
        _fightingStyle.SelectedIndex = 0;
        _resetToDonor = true;
        _view.SelectedItem = "Current loadout";
        RebuildTree();
    }

    private void CommitAndClose()
    {
        NormalizeOrder();
        var dependencies = AbilityDependencyService.Build(WorkingProject());
        var dependencyErrors = dependencies.Issues
            .Where(issue => issue.Severity == AbilityDependencySeverity.Error)
            .Select(issue => "• " + issue.Message)
            .ToList();
        if (dependencyErrors.Count > 0)
        {
            Dialog.Error(
                this,
                "Ability dependencies are incomplete",
                string.Join("\n\n", dependencyErrors) +
                "\n\nNo changes were saved. Use a fighting-style preset or add the required equipment first.");
            return;
        }
        var dependencyWarnings = dependencies.Issues
            .Where(issue => issue.Severity == AbilityDependencySeverity.Warning)
            .Select(issue => "• " + issue.Message)
            .ToList();
        if (dependencyWarnings.Count > 0 &&
            !Dialog.Confirm(
                this,
                "Experimental dependency bundle",
                string.Join("\n\n", dependencyWarnings),
                confirmText: "Save for testing",
                severity: Dialog.Level.Warn))
        {
            return;
        }
        var hasEffectiveChanges = HasEffectiveChanges();
        if (!_resetToDonor && hasEffectiveChanges && !_working.AbilitySets.Where(set => set.Enabled).Any())
        {
            if (!Dialog.Confirm(this,
                    "Save an empty ability loadout?",
                    "This suit will have no DPRD AbilitySets. It may lack input, health, movement, and all combat behavior.",
                    confirmText: "Save empty loadout",
                    severity: Dialog.Level.Crit))
            {
                return;
            }
        }
        if (!_resetToDonor && hasEffectiveChanges && !_working.AllowUnsafeCoreEdits && HasUnsafeCoreChanges())
        {
            Dialog.Warn(this, "Core edits are locked",
                "This saved profile already changes a protected core set. Unlock advanced core edits or reset those entries before saving.");
            return;
        }

        ResultProfile = _resetToDonor || !hasEffectiveChanges ? null : CloneProfile(_working);
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool HasEffectiveChanges()
    {
        var enabled = OrderedEnabledSelections().Select(set => Normalize(set.PackagePath)).ToList();
        var inherited = _catalog.InheritedAbilitySets.Select(set => Normalize(set.PackagePath)).ToList();
        if (!string.IsNullOrWhiteSpace(_working.FightingStyleId)) return true;
        if (HeldItemService.Resolve(_working).Count > 0) return true;
        if (!enabled.SequenceEqual(inherited, StringComparer.OrdinalIgnoreCase)) return true;
        return _working.AbilitySets.Any(set =>
            set.AddedGameplayAbilities.Count > 0 || set.RemovedGameplayAbilities.Count > 0 || !set.Enabled);
    }

    private bool HasUnsafeCoreChanges() => _working.AbilitySets.Any(selection =>
    {
        _setsByPackage.TryGetValue(Normalize(selection.PackagePath), out var entry);
        var tag = new SetNodeTag(selection, entry, _inheritedPackages.Contains(Normalize(selection.PackagePath)), false);
        return IsCore(tag) && (!selection.Enabled || selection.AddedGameplayAbilities.Count > 0 || selection.RemovedGameplayAbilities.Count > 0);
    });

    private bool IsSelectionChanged(AbilitySetSelection selection)
    {
        var package = Normalize(selection.PackagePath);
        var inherited = _inheritedPackages.Contains(package);
        if (inherited != selection.Enabled) return true;
        if (selection.AddedGameplayAbilities.Count > 0 || selection.RemovedGameplayAbilities.Count > 0) return true;
        var inheritedIndex = _catalog.InheritedAbilitySets.FindIndex(entry =>
            Normalize(entry.PackagePath).Equals(package, StringComparison.OrdinalIgnoreCase));
        return selection.Enabled && inheritedIndex >= 0 && selection.Order != inheritedIndex;
    }

    private bool IsCore(SetNodeTag set) => set.Catalog?.IsCore == true ||
                                           set.Selection.PackagePath.Contains("/CoreAbilities/", StringComparison.OrdinalIgnoreCase) ||
                                           UnrealPathUtil.AssetName(set.Selection.PackagePath).Contains("CoreAbilitySet", StringComparison.OrdinalIgnoreCase) ||
                                           UnrealPathUtil.AssetName(set.Selection.PackagePath).Contains("StartingStats", StringComparison.OrdinalIgnoreCase) ||
                                           UnrealPathUtil.AssetName(set.Selection.PackagePath).Contains("InputBuffer", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<GameplayAbilityCatalogEntry> CatalogGrants(AbilitySetSelection selection, AbilitySetCatalogEntry? entry) =>
        (entry?.GameplayAbilities ?? new List<GameplayAbilityCatalogEntry>())
        .Where(grant => !selection.AddedGameplayAbilities.Any(added =>
            Normalize(added.PackagePath).Equals(Normalize(grant.PackagePath), StringComparison.OrdinalIgnoreCase)));

    private AbilitySetSelection? FindSelection(string package) => _working.AbilitySets.FirstOrDefault(set =>
        Normalize(set.PackagePath).Equals(Normalize(package), StringComparison.OrdinalIgnoreCase));

    private List<AbilitySetSelection> OrderedSelections() => _working.AbilitySets
        .Where(set => set.Enabled)
        .OrderBy(set => set.Order)
        .ThenBy(set => set.PackagePath, StringComparer.OrdinalIgnoreCase)
        .Concat(_working.AbilitySets.Where(set => !set.Enabled).OrderBy(set => set.Order))
        .ToList();

    private List<AbilitySetSelection> OrderedEnabledSelections() => _working.AbilitySets
        .Where(set => set.Enabled)
        .OrderBy(set => set.Order)
        .ThenBy(set => set.PackagePath, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private int NextOrder() => _working.AbilitySets.Where(set => set.Enabled).Select(set => set.Order).DefaultIfEmpty(-1).Max() + 1;

    private void NormalizeOrder()
    {
        var order = 0;
        foreach (var selection in _working.AbilitySets.Where(set => set.Enabled).OrderBy(set => set.Order).ThenBy(set => set.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            selection.Order = order++;
        }
    }

    private static void UpsertGrant(AbilitySetSelection selection, CustomGameplayAbilityGrant grant)
    {
        grant.PackagePath = Normalize(grant.PackagePath);
        selection.AddedGameplayAbilities.RemoveAll(existing =>
            Normalize(existing.PackagePath).Equals(grant.PackagePath, StringComparison.OrdinalIgnoreCase));
        selection.RemovedGameplayAbilities.RemoveAll(path =>
            Normalize(path).Equals(grant.PackagePath, StringComparison.OrdinalIgnoreCase));
        selection.AddedGameplayAbilities.Add(grant);
    }

    private static AbilityLoadoutProfile CreateWorkingProfile(AbilityLoadoutProfile? saved, AbilityEditorCatalog catalog)
    {
        if (saved is not null && !catalog.SavedLoadoutNeedsRemap)
        {
            var current = CloneProfile(saved);
            HeldItemService.Migrate(current);
            current.DonorDprdPackage = Normalize(catalog.DonorDprdPackage);
            current.DonorAbilitySetFingerprint = catalog.DonorAbilitySetFingerprint;
            current.DonorAbilitySetPackages = catalog.InheritedAbilitySets
                .Select(entry => Normalize(entry.PackagePath))
                .Where(package => package.Length > 0)
                .ToList();
            return current;
        }
        return new AbilityLoadoutProfile
        {
            HeldItems = [],
            DonorDprdPackage = Normalize(catalog.DonorDprdPackage),
            DonorAbilitySetFingerprint = catalog.DonorAbilitySetFingerprint,
            DonorAbilitySetPackages = catalog.InheritedAbilitySets
                .Select(entry => Normalize(entry.PackagePath))
                .Where(package => package.Length > 0)
                .ToList(),
            AbilitySets = catalog.InheritedAbilitySets.Select((entry, index) => new AbilitySetSelection
            {
                PackagePath = Normalize(entry.PackagePath),
                Enabled = true,
                Order = index,
            }).ToList(),
        };
    }

    internal static AbilityLoadoutProfile CloneProfile(AbilityLoadoutProfile source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        DonorDprdPackage = source.DonorDprdPackage ?? "",
        DonorAbilitySetFingerprint = source.DonorAbilitySetFingerprint ?? "",
        DonorAbilitySetPackages = (source.DonorAbilitySetPackages ?? new List<string>()).ToList(),
        FightingStyleId = source.FightingStyleId ?? "",
        SwordCombat = source.SwordCombat?.Clone(),
        HeldItems = source.HeldItems?.Select(i => i.Clone()).ToList(),
        AllowUnsafeCoreEdits = source.AllowUnsafeCoreEdits,
        AbilitySets = (source.AbilitySets ?? new List<AbilitySetSelection>()).Select(set => new AbilitySetSelection
        {
            PackagePath = set.PackagePath ?? "",
            Enabled = set.Enabled,
            Order = set.Order,
            RemovedGameplayAbilities = (set.RemovedGameplayAbilities ?? new List<string>()).ToList(),
            AddedGameplayAbilities = (set.AddedGameplayAbilities ?? new List<CustomGameplayAbilityGrant>()).Select(grant => new CustomGameplayAbilityGrant
            {
                PackagePath = grant.PackagePath ?? "",
                AbilityLevel = grant.AbilityLevel,
                InputTag = grant.InputTag ?? "",
            }).ToList(),
        }).ToList(),
    };

    private bool SelectNode(TreeNodeCollection nodes, string? package, string? grant)
    {
        foreach (TreeNode node in nodes)
        {
            var matches = node.Tag switch
            {
                GrantNodeTag g when !string.IsNullOrWhiteSpace(grant) =>
                    Normalize(g.Selection.PackagePath).Equals(Normalize(package), StringComparison.OrdinalIgnoreCase) &&
                    Normalize(g.PackagePath).Equals(Normalize(grant), StringComparison.OrdinalIgnoreCase),
                SetNodeTag s when string.IsNullOrWhiteSpace(grant) =>
                    Normalize(s.Selection.PackagePath).Equals(Normalize(package), StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (matches)
            {
                _tree.SelectedNode = node;
                node.EnsureVisible();
                return true;
            }
            if (SelectNode(node.Nodes, package, grant)) return true;
        }
        return false;
    }

    private static string DisplayName(string package, AbilitySetCatalogEntry? entry) =>
        !string.IsNullOrWhiteSpace(entry?.DisplayName) ? entry.DisplayName : UnrealPathUtil.AssetName(package);

    private static bool Matches(string query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var haystack = string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? package) => UnrealPathUtil.NormalizePackagePath(package ?? "");

    private void ApplySelectedFightingStyle()
    {
        if (_fightingStyle.SelectedItem is not FightingStyleChoice choice) return;
        if (string.IsNullOrWhiteSpace(choice.Id))
        {
            RestoreDonorFightingStyle();
            return;
        }
        if (choice.Entry is { Profile: null } source)
        {
            ShowFightingStyle(source);
            return;
        }
        var style = FightingStyleProfileService.Find(choice.Id);
        if (style is not null) ApplyFightingStyle(style);
    }

    private void ApplyFightingStyle(FightingStyleProfile style)
    {
        if (!_setsByPackage.TryGetValue(style.MeleeAbilitySetPackage, out var source) || !source.IsAvailable)
        {
            Dialog.Info(this, "Fighting style unavailable", "The melee AbilitySet is not readable in your active extraction. Run Full refresh before applying this style.");
            return;
        }
        var donorFamily = _project.BaseProfile?.GameplayFamily ?? "";
        if (!style.NativeGameplayFamily.Equals(donorFamily, StringComparison.OrdinalIgnoreCase) &&
            !Dialog.Confirm(
                this,
                "Apply experimental fighting style?",
                $"{style.DisplayName} will replace the current melee style as one atomic bundle. Cross-family combat still needs an in-game test.\n\n{string.Join("\n\n", style.SafetyNotes)}",
                confirmText: "Apply complete bundle",
                severity: Dialog.Level.Warn))
        {
            return;
        }

        AbilityDependencyService.ApplyFightingStyle(_working, style, _inheritedPackages);
        _resetToDonor = false;
        _search.Clear();
        SelectStyle(style.Id);
        _view.SelectedItem = "Current loadout";
        RebuildTree(style.MeleeAbilitySetPackage);
    }

    private void ShowFightingStyle(FightingStyleLibraryService.Entry entry)
    {
        _selectedSet = null;
        _selectedGrant = null;
        _detailTitle.Text = entry.Profile?.DisplayName ?? "Enemy / other combat source";
        _detailSubtitle.Text = entry.Profile is null ? "Inspection only — player adapter required" : "Coordinated fighting-style bundle";
        _details.Text = FightingStyleLibraryService.Describe(entry);
        _addSet.Enabled = _removeSet.Enabled = _moveUp.Enabled = _moveDown.Enabled = false;
        _addGrant.Enabled = _editGrant.Enabled = _removeGrant.Enabled = false;
    }

    private void RestoreDonorFightingStyle()
    {
        var donorCombat = _catalog.InheritedAbilitySets
            .FirstOrDefault(entry => AbilityDependencyService.IsCombatSet(entry.PackagePath));
        foreach (var selection in _working.AbilitySets
                     .Where(selection => AbilityDependencyService.IsCombatSet(selection.PackagePath))
                     .ToList())
        {
            if (_inheritedPackages.Contains(Normalize(selection.PackagePath)))
            {
                selection.Enabled = donorCombat is not null && SamePackage(selection.PackagePath, donorCombat.PackagePath);
            }
            else
            {
                _working.AbilitySets.Remove(selection);
            }
        }
        var allStyleSupport = FightingStyleProfileService.Catalog()
            .SelectMany(style => style.SupportingAbilitySetPackages)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in _working.AbilitySets
                     .Where(selection => allStyleSupport.Contains(Normalize(selection.PackagePath)))
                     .ToList())
        {
            var package = Normalize(selection.PackagePath);
            if (_inheritedPackages.Contains(package))
            {
                selection.Enabled = true;
            }
            else
            {
                _working.AbilitySets.Remove(selection);
            }
        }
        AbilityDependencyService.ClearFightingStyle(_working);
        NormalizeOrder();
        _resetToDonor = false;
        _view.SelectedItem = "Current loadout";
        RebuildTree(donorCombat?.PackagePath);
    }

    private NativeSuitProject WorkingProject() => new()
    {
        BaseProfile = _project.BaseProfile,
        PlayableTemplate = _project.PlayableTemplate,
        DcmdTemplate = _project.DcmdTemplate,
        EquipmentSlots = _project.EquipmentSlots,
        AnimationOverrides = _project.AnimationOverrides,
        AnimationSlotOverrides = _project.AnimationSlotOverrides,
        LocomotionOverrides = _project.LocomotionOverrides,
        AbilityLoadout = _working,
    };

    private void SelectStyle(string id)
    {
        for (var index = 0; index < _fightingStyle.Items.Count; index++)
        {
            if (_fightingStyle.Items[index] is FightingStyleChoice choice &&
                choice.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                _fightingStyle.SelectedIndex = index;
                return;
            }
        }
    }

    private static bool SamePackage(string? left, string? right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static void AddDetail(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) text.AppendLine($"{label}: {value}");
    }

    private static RoundedPanel Card(Color backColor, Padding padding, Padding margin) => new()
    {
        Dock = DockStyle.Fill,
        BackColor = backColor,
        BorderColor = Theme.LineSoft,
        CornerRadius = Theme.Radius,
        Padding = padding,
        Margin = margin,
    };

    private static void ConfigureSmallButton(Button button, string text, int width, bool primary = false)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 32;
        button.Margin = new Padding(0, 0, 6, 0);
        if (primary) Theme.StyleGoldButton(button); else Theme.StyleDarkButton(button);
    }

    private sealed record SetNodeTag(
        AbilitySetSelection Selection,
        AbilitySetCatalogEntry? Catalog,
        bool Inherited,
        bool IsLibraryNode);

    private sealed record BundleNodeTag(string Title, string Subtitle, string Detail);

    private sealed record GrantNodeTag(
        AbilitySetSelection Selection,
        AbilitySetCatalogEntry? Catalog,
        string PackagePath,
        int AbilityLevel,
        string InputTag,
        bool IsAdded,
        bool IsRemoved,
        bool IsLibraryNode);

    private sealed record FightingStyleChoice(string Id, string Label, FightingStyleLibraryService.Entry? Entry = null)
    {
        public override string ToString() => Label;
    }

    private sealed class GameplayAbilityGrantDialog : AdaptiveForm
    {
        private readonly IReadOnlyList<GameplayAbilityCatalogEntry> _catalog;
        private readonly TextBox _search = new();
        private readonly ListBox _list = new();
        private readonly TextBox _package = new();
        private readonly NumericUpDown _level = new();
        private readonly TextBox _inputTag = new();

        public CustomGameplayAbilityGrant? Result { get; private set; }

        public GameplayAbilityGrantDialog(
            IReadOnlyList<GameplayAbilityCatalogEntry> catalog,
            CustomGameplayAbilityGrant? current = null)
        {
            _catalog = catalog ?? Array.Empty<GameplayAbilityCatalogEntry>();
            Text = current is null ? "Batcomputer — Add ability" : "Batcomputer — Edit ability";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 680);
            MinimumSize = new Size(680, 560);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.WindowBg;
            ForeColor = Theme.OnDark;
            Font = Theme.Body;
            Build(current);
            ApplySearch();
        }

        private void Build(CustomGameplayAbilityGrant? current)
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(14) };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(shell);
            var heading = new Label { Text = (current is null ? "ADD GAMEPLAY ABILITY" : "EDIT GAMEPLAY ABILITY") +
                "\nChoose an ability, then configure its level and input.", Dock = DockStyle.Fill, Font = Theme.Heading,
                ForeColor = Theme.Abilities, Padding = new Padding(6, 4, 0, 0) };
            shell.Controls.Add(heading, 0, 0);
            var card = Card(Theme.CardBg, new Padding(4), Padding.Empty);
            shell.Controls.Add(card, 0, 1);
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(14),
                BackColor = Theme.CardBg,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            card.Controls.Add(root);

            _search.Dock = DockStyle.Fill;
            _search.PlaceholderText = "Search gameplay abilities…";
            Theme.StyleDarkInput(_search);
            _search.TextChanged += (_, _) => ApplySearch();
            root.Controls.Add(_search, 0, 0);
            _list.Dock = DockStyle.Fill;
            _list.BackColor = Theme.SlateDark;
            _list.ForeColor = Theme.OnDark;
            _list.BorderStyle = BorderStyle.None;
            Theme.StyleListBox(_list);
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = true;
            _list.SelectedIndexChanged += (_, _) =>
            {
                if (_list.SelectedItem is AbilityChoice choice) _package.Text = choice.PackagePath;
            };
            _list.DoubleClick += (_, _) => Accept();
            root.Controls.Add(_list, 0, 1);
            root.Controls.Add(new Label
            {
                Text = "Choose a catalog entry or enter an exact /Game or DLC package path.",
                Dock = DockStyle.Fill,
                ForeColor = Theme.OnDarkMuted,
                Font = Theme.Caption,
            }, 0, 2);

            _package.Dock = DockStyle.Fill;
            _package.PlaceholderText = "/Game/.../GA_Ability";
            _package.Text = current?.PackagePath ?? "";
            Theme.StyleDarkInput(_package);
            root.Controls.Add(_package, 0, 3);

            var levelRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            levelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            levelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            levelRow.Controls.Add(new Label { Text = "Ability level", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _level.Minimum = 1;
            _level.Maximum = 999;
            _level.Value = Math.Clamp(current?.AbilityLevel ?? 1, 1, 999);
            _level.Dock = DockStyle.Left;
            _level.Width = 100;
            _level.BackColor = Theme.Slate;
            _level.ForeColor = Theme.OnDark;
            _level.BorderStyle = BorderStyle.FixedSingle;
            levelRow.Controls.Add(_level, 1, 0);
            root.Controls.Add(levelRow, 0, 4);

            var tagRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            tagRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            tagRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tagRow.Controls.Add(new Label { Text = "Input tag", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _inputTag.Dock = DockStyle.Fill;
            _inputTag.PlaceholderText = "Leave empty for passive/no input";
            _inputTag.Text = current?.InputTag ?? "";
            Theme.StyleDarkInput(_inputTag);
            tagRow.Controls.Add(_inputTag, 1, 0);
            root.Controls.Add(tagRow, 0, 5);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 8, 0, 0),
            };
            var use = new Button { Text = current is null ? "Add ability" : "Save ability", Width = 120, Height = 34 };
            Theme.StyleGoldButton(use);
            use.Click += (_, _) => Accept();
            var cancel = new Button { Text = "Cancel", Width = 92, Height = 34, DialogResult = DialogResult.Cancel };
            Theme.StyleDarkButton(cancel);
            footer.Controls.Add(use);
            footer.Controls.Add(cancel);
            root.Controls.Add(footer, 0, 6);
            AcceptButton = use;
            CancelButton = cancel;
        }

        private void ApplySearch()
        {
            var query = _search.Text.Trim();
            var selected = (_list.SelectedItem as AbilityChoice)?.PackagePath;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var ability in _catalog
                             .Where(entry => Matches(query, entry.PackagePath, entry.InputTag, entry.SourceAbilitySetPackage))
                             .DistinctBy(entry => Normalize(entry.PackagePath), StringComparer.OrdinalIgnoreCase)
                             .OrderBy(entry => UnrealPathUtil.AssetName(entry.PackagePath), StringComparer.OrdinalIgnoreCase))
                {
                    _list.Items.Add(new AbilityChoice(ability.PackagePath, ability.InputTag));
                }
            }
            finally
            {
                _list.EndUpdate();
            }
            if (!string.IsNullOrWhiteSpace(selected))
            {
                for (var i = 0; i < _list.Items.Count; i++)
                {
                    if (_list.Items[i] is AbilityChoice choice && choice.PackagePath.Equals(selected, StringComparison.OrdinalIgnoreCase))
                    {
                        _list.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void Accept()
        {
            var package = Normalize(_package.Text);
            if (!ExtractedPackagePathService.IsContentPackagePath(package) ||
                package.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2 ||
                package.EndsWith('/'))
            {
                Dialog.Warn(this, "Invalid gameplay ability path",
                    "Enter an exact cooked package path such as /Game/Characters/Abilities/.../GA_Ability.");
                return;
            }
            Result = new CustomGameplayAbilityGrant
            {
                PackagePath = package,
                AbilityLevel = (int)_level.Value,
                InputTag = _inputTag.Text.Trim(),
            };
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed record AbilityChoice(string PackagePath, string InputTag)
        {
            public override string ToString() => UnrealPathUtil.AssetName(PackagePath) +
                                                 (string.IsNullOrWhiteSpace(InputTag) ? "" : $"  ·  {InputTag}");
        }
    }
}
