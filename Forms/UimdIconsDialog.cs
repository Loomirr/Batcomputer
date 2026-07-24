namespace Batcomputer;

/// <summary>
/// Modal editor for the suit's UIMD icon texture paths. The four fields map to the
/// UIMD's MenuIcon / SuitIcon / LeftFacing / RightFacing. Leave a field blank to
/// keep the base Batman icon for that slot. Paths are /Game object paths pointing
/// at textures the modder ships in their own pak (nothing is staged here).
/// </summary>
public sealed partial class UimdIconsDialog : Form
{
    private readonly TextBox _menu = new();
    private readonly TextBox _suit = new();
    private readonly TextBox _left = new();
    private readonly TextBox _right = new();

    public string IconMenu => _menu.Text.Trim();
    public string IconSuit => _suit.Text.Trim();
    public string IconLeft => _left.Text.Trim();
    public string IconRight => _right.Text.Trim();

    public UimdIconsDialog()
    {
        InitializeComponent();
    }

    public UimdIconsDialog(string mod, string menu, string suit, string left, string right)
    {
        InitializeComponent();
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();

        Text = "Suit icons (UIMD)";
        Width = 760;
        Height = 360;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;

        var safeMod = string.IsNullOrWhiteSpace(mod) ? "Suit" : mod;
        _menu.Text = string.IsNullOrWhiteSpace(menu) ? $"/Game/Mods/{safeMod}/UI/T_UI_IconChar_{safeMod}_Menu_BCA" : menu;
        _suit.Text = string.IsNullOrWhiteSpace(suit) ? $"/Game/Mods/{safeMod}/UI/T_UI_IconSuit_{safeMod}_BCA" : suit;
        _left.Text = string.IsNullOrWhiteSpace(left) ? $"/Game/Mods/{safeMod}/UI/T_UI_IconChar_{safeMod}_Left_BCA" : left;
        _right.Text = string.IsNullOrWhiteSpace(right) ? $"/Game/Mods/{safeMod}/UI/T_UI_IconChar_{safeMod}_Right_BCA" : right;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        for (var i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.OnDarkMuted,
            Text = "Point each icon at a texture in YOUR pak (/Game/... object path). Blank = keep the Batman default. Textures aren't staged — ship them in your own texture pak."
        }, 0, 0);

        root.Controls.Add(Row("Menu icon", _menu), 0, 1);
        root.Controls.Add(Row("Suit icon", _suit), 0, 2);
        root.Controls.Add(Row("Left facing", _left), 0, 3);
        root.Controls.Add(Row("Right facing", _right), 0, 4);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        Theme.StyleSmallDarkButton(cancel);
        var ok = new Button { Text = "Save icons", Width = 120 };
        Theme.StyleGoldButton(ok);
        ok.Click += (_, _) =>
        {
            foreach (var t in new[] { IconMenu, IconSuit, IconLeft, IconRight })
            {
                if (!string.IsNullOrWhiteSpace(t) && !t.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                {
                    Dialog.Warn(this, "Suit icons", "Icon paths must start with /Game/ (or be blank).");
                    return;
                }
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons, 0, 5);
        CancelButton = cancel;
    }

    private Control Row(string label, TextBox text)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.OnDark }, 0, 0);
        text.Dock = DockStyle.Fill;
        Theme.StyleDarkInput(text);
        row.Controls.Add(text, 1, 0);
        return row;
    }
}
