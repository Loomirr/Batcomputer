using System.ComponentModel;

#nullable enable

namespace Batcomputer;

partial class SettingsForm
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
        _designerPreviewLabel.Size = new Size(1040, 460);
        _designerPreviewLabel.TabIndex = 0;
        _designerPreviewLabel.Text = "Settings / First-time Setup\r\n\r\nDesigner shell only. Runtime builds path rows from AppSettings.";
        _designerPreviewLabel.TextAlign = ContentAlignment.MiddleCenter;

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1040, 460);
        Controls.Add(_designerPreviewLabel);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "SettingsForm";
        StartPosition = FormStartPosition.CenterParent;
        Text = "Batcomputer — Settings";

        ResumeLayout(false);
    }
}
