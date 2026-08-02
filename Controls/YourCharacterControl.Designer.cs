namespace Batcomputer;

partial class YourCharacterControl
{
    private System.ComponentModel.IContainer components = null;
    private Label _titleLabel;
    private FlowLayoutPanel _slotFlow;
    private MinifigDiagram _diagram;
    private Panel _minifigActions;
    private Button _viewIn3dButton;
    private PictureBox _viewIn3dIcon;

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
        _minifigActions = new Panel();
        _viewIn3dButton = new Button();
        _viewIn3dIcon = new PictureBox();
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
        // _minifigActions
        //
        _minifigActions.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _minifigActions.Location = new Point(152, 3);
        _minifigActions.Size = new Size(68, 54);
        _minifigActions.Name = "_minifigActions";
        //
        // _viewIn3dButton
        //
        _viewIn3dButton.Dock = DockStyle.Fill;
        _viewIn3dButton.Padding = new Padding(0, 0, 0, 2);
        _viewIn3dButton.Text = "View 3D";
        _viewIn3dButton.TextAlign = ContentAlignment.BottomCenter;
        _viewIn3dButton.Name = "_viewIn3dButton";
        //
        // _viewIn3dIcon
        //
        _viewIn3dIcon.BackColor = Color.Transparent;
        _viewIn3dIcon.Location = new Point(21, 3);
        _viewIn3dIcon.Name = "_viewIn3dIcon";
        _viewIn3dIcon.Size = new Size(26, 26);
        _viewIn3dIcon.SizeMode = PictureBoxSizeMode.Zoom;
        _viewIn3dIcon.TabStop = false;
        _minifigActions.Controls.Add(_viewIn3dButton);
        _viewIn3dButton.Controls.Add(_viewIn3dIcon);
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
        Controls.Add(_minifigActions);
        Controls.Add(_titleLabel);
        Name = "YourCharacterControl";
        Size = new Size(226, 840);
        ResumeLayout(false);
    }
}
