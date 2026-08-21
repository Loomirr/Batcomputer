namespace Batcomputer;

partial class CommandBarControl
{
    private System.ComponentModel.IContainer components = null;

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
        SuspendLayout();
        //
        // CommandBarControl
        //
        AutoScaleMode = AutoScaleMode.Inherit;
        Name = "CommandBarControl";
        Size = new Size(1280, 80);
        ResumeLayout(false);
    }
}
