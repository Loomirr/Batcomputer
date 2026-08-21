using System.ComponentModel;

#nullable enable

namespace Batcomputer;

partial class LoadSuitDialog
{
    private IContainer? components = null;
    private Label _designerPreviewLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new Container();
        _designerPreviewLabel = new Label();
        SuspendLayout();

        _designerPreviewLabel.BackColor = Color.FromArgb(43, 47, 54);
        _designerPreviewLabel.BorderStyle = BorderStyle.FixedSingle;
        _designerPreviewLabel.Dock = DockStyle.Fill;
        _designerPreviewLabel.ForeColor = Color.FromArgb(236, 238, 242);
        _designerPreviewLabel.Location = new Point(0, 0);
        _designerPreviewLabel.Name = "_designerPreviewLabel";
        _designerPreviewLabel.Padding = new Padding(18);
        _designerPreviewLabel.Size = new Size(620, 460);
        _designerPreviewLabel.TabIndex = 0;
        _designerPreviewLabel.Text = "Load Saved Suit Dialog\r\n\r\nDesigner shell only. Runtime fills the saved suit list from SuitProjectService.";
        _designerPreviewLabel.TextAlign = ContentAlignment.MiddleCenter;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(26, 29, 34);
        ClientSize = new Size(620, 460);
        Controls.Add(_designerPreviewLabel);
        ForeColor = Color.FromArgb(236, 238, 242);
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "LoadSuitDialog";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Load saved suit";

        ResumeLayout(false);
    }
}
