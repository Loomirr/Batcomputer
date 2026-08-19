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
        _minifigActions.Location = new Point(86, 2);
        _minifigActions.Size = new Size(134, 36);
        _minifigActions.Name = "_minifigActions";
        //
        // _viewIn3dButton
        //
        _viewIn3dButton.Dock = DockStyle.Fill;
        _viewIn3dButton.Padding = new Padding(30, 0, 6, 0);
        _viewIn3dButton.Text = "View in 3D";
        _viewIn3dButton.TextAlign = ContentAlignment.MiddleCenter;
        _viewIn3dButton.AutoEllipsis = true;
        _viewIn3dButton.Name = "_viewIn3dButton";
        //
        // _viewIn3dIcon
        //
        _viewIn3dIcon.BackColor = Color.Transparent;
        _viewIn3dIcon.Location = new Point(7, 9);
        _viewIn3dIcon.Name = "_viewIn3dIcon";
        _viewIn3dIcon.Size = new Size(18, 18);
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
        _titleLabel.Font = Theme.Eyebrow;
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
