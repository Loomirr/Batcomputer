namespace Batcomputer;

/// <summary>A read-only library browser. Choosing a material does not clone or repair it.</summary>
internal sealed class ItemMaterialPickerForm : AdaptiveForm
{
    internal string SelectedPackage { get; private set; } = "";
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search materials by name or folder…" };
    private readonly ThemedDropDown _scope = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted };
    private readonly TextBox _path = new() { Dock = DockStyle.Fill, PlaceholderText = "Advanced: paste an exact cooked material package" };
    private readonly Button _use;
    private List<(string Name, string Path, string Source)> _entries = [];

    internal ItemMaterialPickerForm(string current, bool ownOnly = false)
    {
        Text = "Batcomputer — Choose material"; ClientSize = new(850, 650); MinimumSize = new(700, 540);
        StartPosition = FormStartPosition.CenterParent; BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 6 };
        foreach (var h in new[] { 70, 44 }) root.RowStyles.Add(new(SizeType.Absolute, h));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 30));
        root.RowStyles.Add(new(SizeType.Absolute, 42)); root.RowStyles.Add(new(SizeType.Absolute, 48)); Controls.Add(root);
        root.Controls.Add(new Label { Text = "Choose a material", Font = Theme.Heading, Dock = DockStyle.Fill, ForeColor = Theme.OnDark });
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        filters.ColumnStyles.Add(new(SizeType.Absolute, 180)); filters.ColumnStyles.Add(new(SizeType.Percent, 100));
        _scope.Items.AddRange(["All materials", "Your materials", "Game materials"]); _scope.SelectedIndex = ownOnly ? 1 : 0;
        filters.Controls.Add(_scope); filters.Controls.Add(_search, 1, 0); root.Controls.Add(filters, 0, 1);
        _list.Columns.Add("Material", 280); _list.Columns.Add("Library", 110); _list.Columns.Add("Package", 390); Theme.StyleListView(_list);
        root.Controls.Add(_list, 0, 2); root.Controls.Add(_status, 0, 3); root.Controls.Add(_path, 0, 4);
        Theme.StyleDarkInput(_search); Theme.StyleDarkInput(_path); _path.Text = current;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        _use = ItemWorkshopUi.Button("Use material", true); var cancel = ItemWorkshopUi.Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange([_use, cancel]); root.Controls.Add(footer, 0, 5); CancelButton = cancel; AcceptButton = _use;
        _search.TextChanged += (_, _) => Filter(); _scope.SelectedIndexChanged += (_, _) => Filter();
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedItems.Count == 1) _path.Text = ((ValueTuple<string, string, string>)_list.SelectedItems[0].Tag!).Item2; };
        _list.DoubleClick += (_, _) => Accept(); _use.Click += (_, _) => Accept();
        Shown += async (_, _) =>
        {
            Theme.UseDarkTitleBar(this); _status.Text = "Loading material library…";
            try
            {
                var entries = await Task.Run(() =>
                {
                    var own = new ToolMaterialLibraryService(AppSettings.Current.EffectiveProjectRoot()).LoadMetadataSnapshot();
                    return own.Select(m => (Name: (string.IsNullOrWhiteSpace(m.DisplayName) ? UnrealPathUtil.AssetName(m.PackagePath) : m.DisplayName) +
                            (string.IsNullOrWhiteSpace(m.TemplateOutputRole) ? "" : " · " + m.TemplateOutputRole),
                            Path: m.PackagePath, Source: "Your materials"))
                        .Concat(GameDataService.Instance.AssetsOfClass("MaterialInstanceConstant").Select(m => (Name: UnrealPathUtil.AssetName(m.Path), Path: m.Path, Source: "Game materials")))
                        .GroupBy(m => UnrealPathUtil.NormalizePackagePath(m.Path), StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First()).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
                });
                if (IsDisposed) return; _entries = entries; Filter();
            }
            catch (Exception ex) { if (!IsDisposed) _status.Text = "Library unavailable; paste a package below. " + ex.Message; }
        };
    }
    private void Filter()
    {
        var query = _search.Text.Trim(); var scope = _scope.SelectedItem?.ToString();
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var e in _entries.Where(e => (_scope.SelectedIndex == 0 || e.Source == scope) &&
                     (query.Length == 0 || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(query, StringComparison.OrdinalIgnoreCase))))
            _list.Items.Add(new ListViewItem([e.Name, e.Source, e.Path]) { Tag = e });
        _list.EndUpdate(); _status.Text = $"{_list.Items.Count:n0} materials · selection previews on your model after applying";
    }
    private void Accept()
    {
        var path = UnrealPathUtil.NormalizePackagePath(_path.Text.Trim());
        if (!HeldItemService.ValidPackage(path)) { Dialog.Warn(this, "Choose a material", "Select a library material or enter a cooked /Game or DLC package path."); return; }
        SelectedPackage = path; DialogResult = DialogResult.OK; Close();
    }
}

internal static class ItemWorkshopUi
{
    private sealed class StatusHost : Panel
    {
        internal StatusHost(Label label) { Font = label.Font = Theme.Body; Dock = DockStyle.Fill; label.Dock = DockStyle.Fill; Controls.Add(label); }
        public override Size GetPreferredSize(Size proposedSize) => new(0, Font.Height + 8);
    }
    internal static TableLayoutPanel Footer(Label status, Button cancel, Button apply)
    {
        // Preferred-size rows/columns grow with font and DPI; no implicit row can exceed a fixed footer.
        var footer = new TableLayoutPanel { Name = "WorkshopFooter", Dock = DockStyle.Fill, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Padding = new(0, 8, 0, 0), Margin = Padding.Empty };
        footer.RowStyles.Add(new(SizeType.AutoSize));
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        status.Margin = Padding.Empty; status.AutoEllipsis = true;
        var message = new StatusHost(status) { Margin = new(3, 3, 12, 3) };
        cancel.Dock = apply.Dock = DockStyle.Fill; footer.Controls.Add(message, 0, 0); footer.Controls.Add(cancel, 1, 0); footer.Controls.Add(apply, 2, 0);
        return footer;
    }
    internal static Button Button(string text, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new(100, 34), Margin = new(3), AutoEllipsis = true };
        if (primary) Theme.StyleGoldButton(b); else Theme.StyleDarkButton(b); return b;
    }
    internal static Control Header(string title, string description)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.RowStyles.Add(new(SizeType.Absolute, 36)); panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = title, Font = Theme.Heading, ForeColor = Theme.OnDark, Dock = DockStyle.Fill });
        panel.Controls.Add(new Label { Text = description, ForeColor = Theme.OnDarkMuted, Dock = DockStyle.Fill }, 0, 1); return panel;
    }
    internal static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new(340, 0), ForeColor = Theme.OnDarkMuted, Margin = new(4, 8, 4, 12) };
    internal static FlowLayoutPanel Section(string title, string description, params Control[] controls)
    {
        var card = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 340,
            Padding = new(10), Margin = new(0, 0, 0, 12), FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.PanelBg };
        card.Controls.Add(new Label { Text = title, Font = new Font(Theme.Body, FontStyle.Bold), ForeColor = Theme.OnDark,
            AutoSize = true, MaximumSize = new(310, 0), Margin = new(3, 3, 3, 6) });
        if (description.Length > 0) card.Controls.Add(new Label { Text = description, ForeColor = Theme.OnDarkMuted, AutoSize = true, MaximumSize = new(310, 0), Margin = new(3, 0, 3, 8) });
        foreach (var control in controls) card.Controls.Add(control);
        return card;
    }
    internal static void FitColumns(ListView list, params float[] proportions)
    {
        void Fit() { var width = Math.Max(100, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4); for (var i = 0; i < proportions.Length; i++) list.Columns[i].Width = Math.Max(40, (int)(width * proportions[i])); }
        list.ClientSizeChanged += (_, _) => Fit(); Fit();
    }
}

internal sealed class ItemInspectorPages : UserControl
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new(12), BackColor = Theme.CardBg };
    private readonly FlowLayoutPanel _navigation = new() { Dock = DockStyle.Top, Height = 48, WrapContents = false, AutoScroll = true, BackColor = Theme.WindowBg };
    private readonly List<(Button Button, Control Page)> _pages = [];
    internal ItemInspectorPages() { Dock = DockStyle.Fill; Controls.Add(_content); Controls.Add(_navigation); }
    internal void Add(string title, Control page)
    {
        page.Dock = DockStyle.Fill; _content.Controls.Add(page);
        var button = ItemWorkshopUi.Button(title); button.MinimumSize = new(64, 34); _navigation.Controls.Add(button); _pages.Add((button, page));
        button.Click += (_, _) => Select(page); Select(_pages[0].Page);
    }
    private void Select(Control selected)
    {
        foreach (var (button, page) in _pages) { page.Visible = page == selected; if (page == selected) Theme.StyleGoldButton(button); else Theme.StyleDarkButton(button); }
        selected.BringToFront();
    }
    internal void Select(string title) { var entry = _pages.FirstOrDefault(p => p.Button.Text == title); if (entry.Page is not null) Select(entry.Page); }
    internal static FlowLayoutPanel ScrollPage() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.CardBg };
}
