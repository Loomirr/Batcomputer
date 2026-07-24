namespace Batcomputer;

/// <summary>Modal picker listing saved suit projects to reopen.</summary>
public sealed partial class LoadSuitDialog : Form
{
    private readonly ListView _list = new();

    public string? SelectedPath { get; private set; }

    public LoadSuitDialog()
    {
        InitializeComponent();
    }

    public LoadSuitDialog(IReadOnlyList<SuitProjectService.ProjectSummary> projects)
    {
        InitializeComponent();
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();

        Text = "Load saved suit";
        Width = 620;
        Height = 460;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(root);

        root.Controls.Add(new Label { Text = "Pick a saved suit to continue editing:", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted }, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BackColor = Theme.SlateDark;
        Theme.StyleListView(_list);
        _list.ForeColor = Theme.OnDark;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Suit", 240);
        _list.Columns.Add("Slot id", 200);
        _list.Columns.Add("Modified", 130);
        foreach (var p in projects)
        {
            var item = new ListViewItem(p.DisplayName) { Tag = p.Path };
            item.SubItems.Add(p.SlotId);
            item.SubItems.Add(p.Modified.ToString("g"));
            _list.Items.Add(item);
        }
        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }
        _list.DoubleClick += (_, _) => Accept();
        root.Controls.Add(_list, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        Theme.StyleSmallDarkButton(cancel);
        var open = new Button { Text = "Load", Width = 120 };
        Theme.StyleGoldButton(open);
        open.Click += (_, _) => Accept();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(open);
        root.Controls.Add(buttons, 0, 2);
        CancelButton = cancel;

        if (projects.Count == 0)
        {
            _list.Items.Add(new ListViewItem("(no saved suits found)"));
            open.Enabled = false;
        }
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not string path)
        {
            return;
        }
        SelectedPath = path;
        DialogResult = DialogResult.OK;
        Close();
    }
}
