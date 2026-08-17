namespace Batcomputer;

/// <summary>Manual fallback for choosing a base set from extracted files.</summary>
public sealed partial class BaseWizard : Form
{
    private readonly TextBox _suitName = new();
    private readonly TextBox _modFolder = new();
    private readonly TextBox _playable = new();
    private readonly TextBox _cutscene = new();
    private readonly TextBox _dcmd = new();

    public string SuitName => _suitName.Text.Trim();
    public string ModFolder => _modFolder.Text.Trim();
    public string PlayablePath => _playable.Text.Trim();
    public string CutscenePath => _cutscene.Text.Trim();
    public string DcmdPath => _dcmd.Text.Trim();

    public BaseWizard()
    {
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    public BaseWizard(string suitName, string modFolder, string playable, string cutscene, string dcmd)
    {
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();

        Text = "Set base suit";
        Width = 720;
        Height = 340;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;

        _suitName.Text = suitName;
        _modFolder.Text = modFolder;
        _playable.Text = playable;
        _cutscene.Text = cutscene;
        _dcmd.Text = dcmd;
        foreach (var input in new[] { _suitName, _modFolder, _playable, _cutscene, _dcmd })
        {
            Theme.StyleDarkInput(input);
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(18),
            BackColor = Theme.WindowBg,
        };
        for (var i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Choose the extracted playable and cutscene files to use as this suit's starting point.",
            ForeColor = Theme.OnDarkMuted,
            Font = Theme.Caption,
        }, 0, 0);

        root.Controls.Add(TwoField("Suit name", _suitName, "Mod folder", _modFolder), 0, 1);
        root.Controls.Add(FileRow("Playable .uasset", _playable), 0, 2);
        root.Controls.Add(FileRow("Cutscene .uasset", _cutscene), 0, 3);
        root.Controls.Add(FileRow("DCMD (optional)", _dcmd), 0, 4);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "Use as base", Width = 130 };
        Theme.StyleDarkButton(cancel);
        Theme.StyleGoldButton(ok);
        ok.Click += (_, _) =>
        {
            if (!File.Exists(PlayablePath) || !File.Exists(CutscenePath))
            {
                Dialog.Warn(this, "Set base suit", "Pick valid playable and cutscene .uasset files.");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons, 0, 5);
        CancelButton = cancel;
    }

    private static Control TwoField(string l1, TextBox t1, string l2, TextBox t2)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        row.Controls.Add(new Label { Text = l1, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        t1.Dock = DockStyle.Fill; row.Controls.Add(t1, 1, 0);
        row.Controls.Add(new Label { Text = l2, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        t2.Dock = DockStyle.Fill; row.Controls.Add(t2, 3, 0);
        return row;
    }

    private Control FileRow(string label, TextBox text)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        text.Dock = DockStyle.Fill; row.Controls.Add(text, 1, 0);
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
        Theme.StyleDarkButton(browse);
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Cooked asset (*.uasset)|*.uasset" };
            var start = AppSettings.Current.EffectiveExtractedContentRoot();
            if (Directory.Exists(start)) dlg.InitialDirectory = start;
            if (dlg.ShowDialog(this) == DialogResult.OK) text.Text = dlg.FileName;
        };
        row.Controls.Add(browse, 2, 0);
        return row;
    }
}
