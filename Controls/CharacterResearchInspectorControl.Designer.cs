namespace Batcomputer;

partial class CharacterResearchInspectorControl
{
    private System.ComponentModel.IContainer components = null;
    private TableLayoutPanel _layout;
    private Label _titleLabel;
    private Label _infoLabel;
    private Button _copyPathButton;
    private RichTextBox _details;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null)
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        _layout = new TableLayoutPanel();
        _titleLabel = new Label();
        _infoLabel = new Label();
        _copyPathButton = new Button();
        _details = new RichTextBox();
        _layout.SuspendLayout();
        SuspendLayout();
        //
        // _layout
        //
        _layout.ColumnCount = 1;
        _layout.Dock = DockStyle.Fill;
        _layout.RowCount = 4;
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _layout.Controls.Add(_titleLabel, 0, 0);
        _layout.Controls.Add(_infoLabel, 0, 1);
        _layout.Controls.Add(_copyPathButton, 0, 2);
        _layout.Controls.Add(_details, 0, 3);
        //
        // _titleLabel
        //
        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.Font = new Font(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
        _titleLabel.Text = "RESEARCH INSPECTOR";
        //
        // _infoLabel
        //
        _infoLabel.Dock = DockStyle.Fill;
        _infoLabel.AutoEllipsis = true;
        //
        // _copyPathButton
        //
        _copyPathButton.Dock = DockStyle.Left;
        _copyPathButton.Text = "Copy package path";
        _copyPathButton.Width = 150;
        _copyPathButton.Enabled = false;
        //
        // _details
        //
        _details.Dock = DockStyle.Fill;
        _details.Font = new Font("Consolas", 8f);
        _details.ScrollBars = RichTextBoxScrollBars.Vertical;
        //
        // CharacterResearchInspectorControl
        //
        AutoScaleMode = AutoScaleMode.Inherit;
        Controls.Add(_layout);
        Name = "CharacterResearchInspectorControl";
        Size = new Size(330, 840);
        _layout.ResumeLayout(false);
        ResumeLayout(false);
    }
}
