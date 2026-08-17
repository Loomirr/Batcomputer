namespace Batcomputer;

/// <summary>Modal picker listing saved suit projects to reopen.</summary>
public sealed partial class LoadSuitDialog : Form
{
    private readonly ListView _list = new();
    private readonly List<SuitProjectService.ProjectSummary> _projects = new();
    private readonly SearchBox _search = new();
    private readonly Label _count = new();
    private Func<SuitProjectService.ProjectSummary, bool, bool, bool>? _deleteSuit;

    public string? SelectedPath { get; private set; }

    public LoadSuitDialog()
    {
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    public LoadSuitDialog(IReadOnlyList<SuitProjectService.ProjectSummary> projects,
        Func<SuitProjectService.ProjectSummary, bool, bool, bool>? deleteSuit = null)
    {
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        _projects.AddRange(projects.OrderByDescending(p => p.Modified));
        _deleteSuit = deleteSuit;

        Controls.Clear();
        Text = "Batcomputer - All suits";
        ClientSize = new Size(780, 560);
        MinimumSize = new Size(660, 440);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18, 16, 18, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.WindowBg };
        var rail = new Panel { Left = 0, Top = 3, Width = 3, Height = 38, BackColor = Theme.Base };
        var overline = new Label
        {
            Left = 14, Top = 0, Width = 280, Height = 16,
            Text = "YOUR SUITS", Font = Theme.Caption, ForeColor = Theme.Base,
        };
        var title = new Label
        {
            Left = 14, Top = 15, Width = 410, Height = 28,
            Text = "All suits", Font = AppFonts.Condensed(14f, FontStyle.Bold), ForeColor = Theme.OnDark,
        };
        var subtitle = new Label
        {
            Left = 14, Top = 43, Width = 510, Height = 18,
            Text = "Open a saved suit or right-click one for library actions.", Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
        };
        _count.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _count.TextAlign = ContentAlignment.MiddleRight;
        _count.SetBounds(540, 15, 204, 28);
        _count.Font = Theme.BodyStrong;
        _count.ForeColor = Theme.OnDarkMuted;
        header.Controls.AddRange(new Control[] { rail, overline, title, subtitle, _count });
        root.Controls.Add(header, 0, 0);

        _search.Dock = DockStyle.Fill;
        _search.Height = 30;
        _search.PlaceholderText = "Search saved suits…";
        _search.TextChanged += (_, _) => RefreshList();
        root.Controls.Add(_search, 0, 1);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BackColor = Theme.SlateDark;
        Theme.StyleListView(_list);
        _list.ForeColor = Theme.OnDark;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("SUIT", 306);
        _list.Columns.Add("SLOT", 282);
        _list.Columns.Add("LAST EDITED", 150);
        _list.DoubleClick += (_, _) => Accept();
        _list.MouseDown += OnListMouseDown;
        root.Controls.Add(_list, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        var open = new Button { Text = "Open suit", Width = 120, Height = 30 };
        Theme.StyleGoldButton(open);
        open.Click += (_, _) => Accept();
        var hint = new Label
        {
            AutoSize = false, Width = 390, Height = 24, Margin = new Padding(0, 4, 0, 0),
            Text = "Right-click a suit to delete it from the tool or game.", Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(open);
        buttons.Controls.Add(hint);
        root.Controls.Add(buttons, 0, 3);
        CancelButton = cancel;
        AcceptButton = open;

        Resize += (_, _) => ResizeColumns();
        RefreshList();
        Theme.ApplyReadableTheme(this);
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
                        || p.SlotId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in visible)
        {
            var item = new ListViewItem(p.DisplayName) { Tag = p };
            item.SubItems.Add(p.SlotId);
            item.SubItems.Add(p.Modified.ToString("MMM d, yyyy  h:mm tt"));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;

        _count.Text = visible.Count == _projects.Count
            ? $"{_projects.Count} saved suit{(_projects.Count == 1 ? "" : "s")}"
            : $"{visible.Count} of {_projects.Count} suits";
        ResizeColumns();
    }

    private void ResizeColumns()
    {
        if (_list.Columns.Count != 3) return;
        var width = Math.Max(320, _list.ClientSize.Width);
        _list.Columns[0].Width = (int)(width * 0.42);
        _list.Columns[1].Width = (int)(width * 0.36);
        _list.Columns[2].Width = Math.Max(130, width - _list.Columns[0].Width - _list.Columns[1].Width - 4);
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item?.Tag is not SuitProjectService.ProjectSummary summary) return;

        hit.Item.Selected = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open suit", null, (_, _) => Accept());
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
