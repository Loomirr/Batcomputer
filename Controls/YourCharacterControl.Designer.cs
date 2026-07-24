namespace Batcomputer;

partial class YourCharacterControl
{
    private System.ComponentModel.IContainer components = null;
    private Label _titleLabel;
    private FlowLayoutPanel _slotFlow;
    private MinifigDiagram _diagram;

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
        _titleLabel = new Label();
        _slotFlow = new FlowLayoutPanel();
        _diagram = new MinifigDiagram();
        SuspendLayout();
        //
        // _slotFlow
        //
        // The slot list is retired: the panel is now just the character figure. The flow is kept
        // (hidden) so the existing MainForm plumbing that references it stays valid.
        _slotFlow.Dock = DockStyle.Fill;
        _slotFlow.FlowDirection = FlowDirection.TopDown;
        _slotFlow.WrapContents = false;
        _slotFlow.AutoScroll = true;
        _slotFlow.Visible = false;
        _slotFlow.Name = "_slotFlow";
        //
        // _diagram
        //
        _diagram.Dock = DockStyle.Fill;
        _diagram.Name = "_diagram";
        //
        // _titleLabel
        //
        _titleLabel.Dock = DockStyle.Top;
        _titleLabel.Height = 22;
        _titleLabel.Text = "YOUR CHARACTER";
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.Font = new Font(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
        _titleLabel.Name = "_titleLabel";
        //
        // YourCharacterControl
        //
        AutoScaleMode = AutoScaleMode.Inherit;
        Controls.Add(_slotFlow);
        Controls.Add(_diagram);
        Controls.Add(_titleLabel);
        Name = "YourCharacterControl";
        Size = new Size(226, 840);
        ResumeLayout(false);
    }
}
