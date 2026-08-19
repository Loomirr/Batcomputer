namespace Batcomputer;

/// <summary>
/// The "Your Character" panel extracted into a designer-editable control that
/// owns the component-row flow. MainForm keeps the row-building logic (PopulateToyboxSlots) and the
/// drag-drop wiring, operating on the exposed <see cref="SlotFlow"/> - the safe structural step,
/// matching the Inspector extraction.
/// </summary>
public partial class YourCharacterControl : UserControl
{
    public YourCharacterControl()
    {
        InitializeComponent();
        // Darker ground so the CardBg rows below read as separated cards.
        BackColor = Theme.PanelBg;
        Padding = new Padding(6);
        _slotFlow.BackColor = Theme.PanelBg;
        _titleLabel.ForeColor = Theme.Gold;
        _titleLabel.Font = Theme.Eyebrow;
        _diagram.BackColor = Theme.PanelBg;
        _minifigActions.BackColor = Theme.PanelBg;
        Theme.StyleDarkButton(_viewIn3dButton);
        _viewIn3dButton.FlatAppearance.BorderColor = Theme.Research;
        _viewIn3dButton.FlatAppearance.MouseOverBackColor = Theme.Tint(Theme.Research);
        _viewIn3dButton.ForeColor = Theme.Research;
        _viewIn3dButton.Font = Theme.Caption;
        _viewIn3dButton.Click += (_, _) => ViewIn3DRequested?.Invoke(this, EventArgs.Empty);
        _viewIn3dIcon.Click += (_, _) => _viewIn3dButton.PerformClick();
        if (EmbeddedAssets.LoadAnimated("3D.gif") is { } animatedIcon)
        {
            _viewIn3dIcon.Image = animatedIcon;
        }
        _minifigActions.BringToFront();
        SizeChanged += (_, _) => LayoutMinifigActions();
        LayoutMinifigActions();
    }

    /// <summary>The vertical flow of character-component rows (added/removed by PopulateToyboxSlots).</summary>
    public FlowLayoutPanel SlotFlow => _slotFlow;

    /// <summary>The minifig figure (region tint + click-to-select).</summary>
    public MinifigDiagram Diagram => _diagram;

    /// <summary>Raised when the minifig panel's preview command is selected.</summary>
    public event EventHandler? ViewIn3DRequested;

    /// <summary>
    /// Switches between the minifig figure and the classic slot list (Settings → General →
    /// "Show the character as a minifig"). Only one is ever visible.
    /// </summary>
    public void SetMinifigMode(bool useMinifig)
    {
        _diagram.Visible = useMinifig;
        _slotFlow.Visible = !useMinifig;
        _minifigActions.Visible = useMinifig;
        _minifigActions.BringToFront();
    }

    private void LayoutMinifigActions()
    {
        _minifigActions.Left = Math.Max(Padding.Left, ClientSize.Width - _minifigActions.Width - Padding.Right);
        _minifigActions.Top = 2;
    }
}
