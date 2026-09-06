namespace Batcomputer;

/// <summary>Modal picker listing saved suit projects to reopen.</summary>
public sealed partial class LoadSuitDialog : AdaptiveForm
{
    private readonly ListView _list = new();
    private readonly List<SuitProjectService.ProjectSummary> _projects = new();
    private readonly SearchBox _search = new();
    private readonly Label _count = new();
    private readonly TextBox _detail = CharacterMenuStyle.DetailText();
    private readonly Button _open = new();
    private readonly Label _empty = CharacterMenuStyle.Label("No matching saved projects.\nTry another search.");
    private Func<SuitProjectService.ProjectSummary, bool, bool, bool>? _deleteSuit;
    private string _itemNoun = "suit";

    public string? SelectedPath { get; private set; }

    public LoadSuitDialog()
    {
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    public LoadSuitDialog(IReadOnlyList<SuitProjectService.ProjectSummary> projects,
        Func<SuitProjectService.ProjectSummary, bool, bool, bool>? deleteSuit = null,
        string libraryTitle = "All suits", string libraryDescription = "Open a saved suit or right-click one for library actions.", string? actionText = null)
    {
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        _projects.AddRange(projects.OrderByDescending(p => p.Modified));
        _deleteSuit = deleteSuit;
        _itemNoun = libraryTitle.Equals("Your characters", StringComparison.OrdinalIgnoreCase) ? "character" : "suit";

        Controls.Clear();
        Text = "Batcomputer - " + libraryTitle;
        ClientSize = new Size(1000, 660);
        MinimumSize = new Size(760, 510);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);
        root.Controls.Add(CharacterMenuStyle.Header(libraryTitle, libraryDescription), 0, 0);
        var body = CharacterMenuStyle.Grid(2, 1); body.RowStyles.Add(new(SizeType.Percent, 100));
        body.ColumnStyles.Add(new(SizeType.Percent, 62)); body.ColumnStyles.Add(new(SizeType.Percent, 38)); root.Controls.Add(body, 0, 1);
        var browser = CharacterMenuStyle.Card(); browser.Margin = new Padding(0, 0, 12, 0); body.Controls.Add(browser, 0, 0);
        var left = CharacterMenuStyle.Grid(1, 2); left.RowStyles.Add(new(SizeType.Absolute, 44)); left.RowStyles.Add(new(SizeType.Percent, 100)); browser.Controls.Add(left);
        var detailCard = CharacterMenuStyle.Card(); body.Controls.Add(detailCard, 1, 0);
        var right = CharacterMenuStyle.Grid(1, 2); right.RowStyles.Add(new(SizeType.Absolute, 36)); right.RowStyles.Add(new(SizeType.Percent, 100)); detailCard.Controls.Add(right);
        right.Controls.Add(CharacterMenuStyle.Label("Your selection", Theme.Heading, Theme.Materials), 0, 0); right.Controls.Add(_detail, 0, 1);

        _search.Dock = DockStyle.Fill;
        _search.Height = 30;
        _search.PlaceholderText = $"Search saved {_itemNoun}s…";
        _search.TextChanged += (_, _) => RefreshList();
        left.Controls.Add(_search, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BackColor = Theme.SlateDark;
        Theme.StyleListView(_list);
        _list.ForeColor = Theme.OnDark;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(_itemNoun == "character" ? "CHARACTER" : "SUIT", 306);
        _list.Columns.Add("LAST EDITED", 150);
        _list.DoubleClick += (_, _) => Accept();
        _list.MouseDown += OnListMouseDown;
        _list.SelectedIndexChanged += (_, _) => RefreshSelection();
        var listHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        listHost.Controls.Add(_list); listHost.Controls.Add(_empty); _empty.TextAlign = ContentAlignment.MiddleCenter;
        left.Controls.Add(listHost, 0, 1);

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var cancel = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        var open = _open; open.Text = actionText ?? (_itemNoun == "character" ? "Use character" : "Open suit");
        Theme.StyleGoldButton(open);
        open.Click += (_, _) => Accept();
        var hint = _count; hint.Dock = DockStyle.Fill; hint.Font = Theme.Caption; hint.ForeColor = Theme.OnDarkMuted;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        open.Dock = DockStyle.Fill;
        open.Margin = new Padding(4, 0, 4, 0);
        cancel.Dock = DockStyle.Fill;
        cancel.Margin = Padding.Empty;
        buttons.Controls.Add(hint, 0, 0);
        buttons.Controls.Add(open, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);
        root.Controls.Add(buttons, 0, 2);
        CancelButton = cancel;
        AcceptButton = open;

        Resize += (_, _) => ResizeColumns();
        RefreshList();
        Theme.ApplyReadableTheme(this);
        _detail.BorderStyle = BorderStyle.None; _detail.BackColor = Theme.CardBg;
        _detail.ForeColor = Theme.OnDarkMuted; _detail.Font = Theme.Body;
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not SuitProjectService.ProjectSummary summary)
        {
            return;
        }
        SelectedPath = summary.Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RefreshList()
    {
        if (WinFormsDesignerSupport.IsInDesigner()) return;

        var query = _search.Text.Trim();
        var visible = _projects
            .Where(p => string.IsNullOrWhiteSpace(query)
                        || p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || p.CharacterId.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || p.SlotId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in visible)
        {
            var item = new ListViewItem(p.DisplayName) { Tag = p };
            item.SubItems.Add(p.Modified.ToString("MMM d, yyyy"));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        _empty.Visible = _list.Items.Count == 0;
        if (_empty.Visible) _empty.BringToFront();

        _count.Text = visible.Count == _projects.Count
            ? $"{_projects.Count} saved {_itemNoun}{(_projects.Count == 1 ? "" : "s")}"
            : $"{visible.Count} of {_projects.Count} {_itemNoun}s";
        ResizeColumns();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as SuitProjectService.ProjectSummary : null;
        _open.Enabled = selected is not null;
        _detail.Text = selected is null ? "Select a saved " + _itemNoun + " to see its identity and continue.\r\n\r\n" +
            (_projects.Count == 0 ? "Nothing has been saved yet. Create a character or suit first." : "Search by display name or ID.") :
            selected.DisplayName + "\r\n\r\n" + (selected.IsCharacter ? "YOUR CHARACTER\r\n" + selected.CharacterId +
                "\r\n\r\nCreates an independent suit owned by this character. Its default character is included automatically when building." :
                _open.Text == "Copy design" ? "SAVED SUIT DESIGN\r\n\r\nCopies parts, materials, textures and gameplay choices into a new character. The original project stays unchanged." :
                "SAVED SUIT\r\n\r\nOpen to customize its appearance, abilities, equipment and animations.") +
            "\r\n\r\nPROJECT ID\r\n" + selected.SlotId + "\r\n\r\nLAST EDITED\r\n" + selected.Modified.ToString("f") +
            (_deleteSuit is null ? "" : "\r\n\r\nRight-click the list entry for removal options.");
    }

    private void ResizeColumns()
    {
        if (_list.Columns.Count != 2) return;
        var width = Math.Max(200, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        _list.Columns[1].Width = Math.Min(126 * DeviceDpi / 96, width / 2);
        _list.Columns[0].Width = width - _list.Columns[1].Width;
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item?.Tag is not SuitProjectService.ProjectSummary summary) return;

        hit.Item.Selected = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open " + _itemNoun, null, (_, _) => Accept());
        if (_deleteSuit is null) { menu.Show(_list, e.Location); return; }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete from tool", null, (_, _) => Delete(summary, deleteFromGame: false, deleteFromTool: true));
        menu.Items.Add("Delete from game", null, (_, _) => Delete(summary, deleteFromGame: true, deleteFromTool: false));
        menu.Items.Add("Delete from tool and game", null, (_, _) => Delete(summary, deleteFromGame: true, deleteFromTool: true));
        menu.Show(_list, e.Location);
    }

    private void Delete(SuitProjectService.ProjectSummary summary, bool deleteFromGame, bool deleteFromTool)
    {
        if (_deleteSuit is null || !_deleteSuit(summary, deleteFromGame, deleteFromTool)) return;
        if (deleteFromTool) _projects.RemoveAll(p => string.Equals(p.Path, summary.Path, StringComparison.OrdinalIgnoreCase));
        RefreshList();
    }
}
