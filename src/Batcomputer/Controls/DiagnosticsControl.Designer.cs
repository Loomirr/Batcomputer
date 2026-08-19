namespace Batcomputer;

partial class DiagnosticsControl
{
    private System.ComponentModel.IContainer components = null;
    private TextBox _logText;

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
        _logText = new TextBox();
        SuspendLayout();
        //
        // _logText
        //
        _logText.BorderStyle = BorderStyle.None;
        _logText.Dock = DockStyle.Fill;
        _logText.Font = new Font(FontFamily.GenericMonospace, 9f);
        _logText.Location = new Point(0, 0);
        _logText.Multiline = true;
        _logText.Name = "_logText";
        _logText.ReadOnly = true;
        _logText.ScrollBars = ScrollBars.Vertical;
        _logText.ShortcutsEnabled = true;
        _logText.Size = new Size(1500, 200);
        _logText.TabIndex = 0;
        _logText.WordWrap = true;
        //
        // DiagnosticsControl
        //
        AutoScaleDimensions = new SizeF(7f, 15f);
        AutoScaleMode = AutoScaleMode.Font;
        Controls.Add(_logText);
        Name = "DiagnosticsControl";
        Size = new Size(1500, 200);
        ResumeLayout(false);
    }
}
