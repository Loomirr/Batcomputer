namespace Batcomputer;

public sealed partial class AnimationExplorerForm
{
    private readonly ComboBox _treeMode = new();
    private readonly ComboBox _treeFilter = new();
    private readonly ComboBox _choiceSource = new();
    private readonly TextBox _choiceSearch = new();
    private readonly ListView _choices = new();
    private readonly Label _choiceInfo = new();
    private readonly System.Windows.Forms.Timer _choiceDebounce = new() { Interval = 180 };
    private IReadOnlyList<AnimationReplacementCandidate> _allChoices = [];
    private List<AnimationReplacementCandidate> _visibleChoices = [];
    private AnimationReplacementCandidate? SelectedReplacement => _choices.SelectedIndices.Count == 1 &&
        _choices.SelectedIndices[0] < _visibleChoices.Count ? _visibleChoices[_choices.SelectedIndices[0]] : null;

    private string SelectedTreeFilter() => _treeFilter.SelectedIndex switch {
        1 => "changed", 2 => "movement", 3 => "actions", 4 => "layers", 5 => "library", _ => "all"
    };

    private Control BuildTreeFilters()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        foreach (var box in new[] { _treeMode, _treeFilter }) {
            box.Dock = DockStyle.Fill; box.DropDownStyle = ComboBoxStyle.DropDownList; Theme.StyleDarkCombo(box);
        }
        _treeMode.Items.AddRange(["Simple tree", "Native hierarchy"]); _treeMode.SelectedIndex = 0;
        _treeFilter.Items.AddRange(["All animations", "Changed on this character", "Movement & idles", "Actions", "Layers", "Your cooked clips"]); _treeFilter.SelectedIndex = 0;
        row.Controls.Add(_treeMode, 0, 0); row.Controls.Add(_treeFilter, 1, 0);
        return row;
    }

    private Control BuildReplacementBrowser()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(0, 8, 0, 0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        panel.Controls.Add(new Label { Text = "REPLACEMENT", Dock = DockStyle.Fill, Font = Theme.Eyebrow, ForeColor = Theme.Animations }, 0, 0);
        _choiceSource.Dock = DockStyle.Fill; _choiceSource.DropDownStyle = ComboBoxStyle.DropDownList;
        _choiceSource.Items.AddRange(["All compatible sources", "Your cooked clips", "Base game", "Show unavailable imports"]);
        _choiceSource.SelectedIndex = 0; Theme.StyleDarkCombo(_choiceSource);
        _choiceSearch.Dock = DockStyle.Fill; _choiceSearch.PlaceholderText = "Find a replacement by animation or character…"; Theme.StyleDarkInput(_choiceSearch);
        panel.Controls.Add(_choiceSource, 0, 1); panel.Controls.Add(_choiceSearch, 0, 2);
        _choices.Dock = DockStyle.Fill; _choices.View = View.Details; _choices.VirtualMode = true;
        _choices.FullRowSelect = true; _choices.MultiSelect = false; _choices.HideSelection = false;
        _choices.BackColor = Theme.SlateDark; _choices.ForeColor = Theme.OnDark; _choices.BorderStyle = BorderStyle.None;
        _choices.Columns.Add("Animation", 280); _choices.Columns.Add("Source", 115);
        _choices.RetrieveVirtualItem += (_, e) => {
            if (e.ItemIndex >= _visibleChoices.Count) { e.Item = new ListViewItem(""); return; }
            var choice = _visibleChoices[e.ItemIndex];
            e.Item = new ListViewItem(choice.Name.Replace('_', ' ')) { ForeColor = choice.CanSelect ? Theme.OnDark : Theme.OnDarkMuted };
            e.Item.SubItems.Add(choice.LibraryEntry is null ? choice.Source : "Your clips");
        };
        _choices.Resize += (_, _) => _choices.Columns[0].Width = Math.Max(150, _choices.ClientSize.Width - 135);
        panel.Controls.Add(_choices, 0, 3);
        _choiceInfo.Dock = DockStyle.Fill; _choiceInfo.Font = Theme.Caption; _choiceInfo.ForeColor = Theme.OnDarkMuted; _choiceInfo.AutoEllipsis = true;
        panel.Controls.Add(_choiceInfo, 0, 4);
        return panel;
    }

    private void WireBrowserEvents()
    {
        _treeMode.SelectedIndexChanged += (_, _) => RebuildTree();
        _treeFilter.SelectedIndexChanged += (_, _) => RebuildTree();
        _choiceSource.SelectedIndexChanged += (_, _) => FilterReplacementChoices();
        _choiceSearch.TextChanged += (_, _) => { _choiceDebounce.Stop(); _choiceDebounce.Start(); };
        _choiceDebounce.Tick += (_, _) => { _choiceDebounce.Stop(); FilterReplacementChoices(); };
        _choices.SelectedIndexChanged += (_, _) => ShowReplacementChoice();
        FormClosed += (_, _) => { _choiceDebounce.Stop(); _choiceDebounce.Dispose(); };
        _tree.NodeMouseDoubleClick += (_, e) => {
            if ((e.Node?.Tag as AnimationExplorerNode)?.CharacterTarget is not null) _choiceSearch.Focus();
        };
    }

    private void LoadReplacementChoices(CharacterAnimationTargetSnapshot? target)
    {
        _allChoices = target is null ? [] : AnimationReplacementCatalogService.Build(target, _library);
        FilterReplacementChoices();
    }

    private void FilterReplacementChoices()
    {
        var query = _choiceSearch.Text.Trim().Replace('_', ' ');
        var customPackages = _allChoices.Where(candidate => candidate.LibraryEntry is not null && candidate.CanSelect)
            .Select(candidate => candidate.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _choices.SelectedIndices.Clear();
        _visibleChoices = _allChoices.Where(candidate => _choiceSource.SelectedIndex switch {
            1 => candidate.LibraryEntry is not null && candidate.CanSelect,
            2 => candidate.LibraryEntry is null && candidate.CanSelect &&
                 !candidate.PackagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase),
            3 => candidate.LibraryEntry is not null && !candidate.CanSelect,
            _ => candidate.CanSelect,
        }).Where(candidate => candidate.LibraryEntry is not null || !customPackages.Contains(candidate.PackagePath))
          .Where(candidate => string.IsNullOrEmpty(query) ||
            (candidate.Name + " " + candidate.PackagePath + " " + candidate.Detail).Replace('_', ' ').Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _choices.VirtualListSize = _visibleChoices.Count;
        _choices.Invalidate();
        ShowReplacementChoice();
    }

    private void ShowReplacementChoice()
    {
        var choice = SelectedReplacement;
        _choiceInfo.Text = choice is null
            ? _selectedTarget is null ? "Select an animation in the character tree." : $"{_visibleChoices.Count} choices. Select one to see its source and apply it."
            : choice.CanSelect ? choice.Detail + "\n" + choice.PackagePath : choice.IncompatibilityReason;
        SetTargetButtons(_project is null ? null : _selectedTarget);
    }
}
