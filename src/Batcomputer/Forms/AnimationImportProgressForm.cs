namespace Batcomputer;

/// <summary>Compact progress surface for validating and importing a cooked animation package.</summary>
public sealed class AnimationImportProgressForm : AdaptiveForm
{
    private readonly Label _phase = new();
    private readonly Label _detail = new();
    private readonly ThemedProgressBar _progress = new();

    public AnimationImportProgressForm(string packageName)
    {
        Text = "Importing animations";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(600, 168);

        var eyebrow = new Label
        {
            Left = 20,
            Top = 18,
            Width = 560,
            Height = 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "ANIMATION LIBRARY",
            Font = Theme.Eyebrow,
            ForeColor = Theme.Animations,
        };
        _phase.SetBounds(20, 42, 560, 26);
        _phase.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _phase.Font = Theme.Heading;
        _phase.ForeColor = Theme.OnDark;
        _phase.Text = "Preparing import";

        _detail.SetBounds(20, 72, 560, 38);
        _detail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _detail.Font = Theme.Caption;
        _detail.ForeColor = Theme.OnDarkMuted;
        _detail.AutoEllipsis = true;
        _detail.Text = packageName;

        _progress.SetBounds(20, 126, 560, 14);
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _progress.Indeterminate = true;

        Controls.AddRange([eyebrow, _phase, _detail, _progress]);
        Theme.ApplyReadableTheme(this);
    }

    public void SetPhase(string phase, string detail)
    {
        if (IsDisposed)
        {
            return;
        }
        _phase.Text = phase;
        _detail.Text = detail;
        _progress.Indeterminate = true;
        Update();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(this);
    }
}
