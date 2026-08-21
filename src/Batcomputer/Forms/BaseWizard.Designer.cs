using System.ComponentModel;

#nullable enable

namespace Batcomputer;

partial class BaseWizard
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
        _designerPreviewLabel.Size = new Size(720, 340);
        _designerPreviewLabel.TabIndex = 0;
        _designerPreviewLabel.Text = "Set Base Suit Wizard\r\n\r\nDesigner shell only. Runtime builds the file rows and validation buttons from the existing constructor.";
        _designerPreviewLabel.TextAlign = ContentAlignment.MiddleCenter;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 340);
        Controls.Add(_designerPreviewLabel);
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "BaseWizard";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Set base suit";

        ResumeLayout(false);
    }
}
