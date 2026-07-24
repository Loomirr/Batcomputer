namespace Batcomputer;

/// <summary>
/// A small modeless progress window for long operations (packaging, bulk suit updates).
///
/// Modeless + owner-disabled rather than ShowDialog(), because the operations it reports on are
/// async: ShowDialog would block the caller. The awaits inside packaging pump the message loop, so
/// the bar/labels repaint on their own; <see cref="Report"/> also forces a repaint for the long
/// synchronous stretches (retoc, UAssetAPI) that never yield.
/// </summary>
public sealed class ProgressDialog : Form
{
    private readonly Label _stepLabel = new();
    private readonly Label _detailLabel = new();
    private readonly ThemedProgressBar _bar = new();
    private readonly Form? _owner;

    /// <param name="totalSteps">0 = indeterminate (marquee); otherwise a determinate bar.</param>
    public ProgressDialog(Form owner, string title, int totalSteps = 0)
    {
        _owner = owner;
        Text = title;
        Width = 520;
        Height = 168;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false; // no close box: the operation owns this window's lifetime
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;

        _stepLabel.Dock = DockStyle.Top;
        _stepLabel.Height = 30;
        _stepLabel.Padding = new Padding(16, 14, 16, 0);
        _stepLabel.ForeColor = Theme.Gold;
        _stepLabel.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        _stepLabel.Text = title;

        _detailLabel.Dock = DockStyle.Top;
        _detailLabel.Height = 34;
        _detailLabel.Padding = new Padding(16, 2, 16, 0);
        _detailLabel.ForeColor = Theme.OnDarkMuted;
        _detailLabel.Font = Theme.Mono;   // churning file paths stop resizing the eye
        _detailLabel.AutoEllipsis = true;
        _detailLabel.Text = "Starting…";

        _bar.Dock = DockStyle.Top;
        _bar.Height = 14;
        _bar.BackColor = Theme.WindowBg;
        if (totalSteps > 0)
        {
            _bar.Maximum = totalSteps;
            _bar.Value = 0;
        }
        else
        {
            _bar.Indeterminate = true;
        }

        var barHost = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(16, 0, 16, 0), BackColor = Theme.WindowBg };
        barHost.Controls.Add(_bar);

        Controls.Add(barHost);
        Controls.Add(_detailLabel);
        Controls.Add(_stepLabel);

        if (_owner is not null)
        {
            Owner = _owner;
            _owner.Enabled = false; // block input on the main window while the operation runs
        }
        Show(_owner);
        Refresh();
    }

    /// <summary>Updates the sub-status line (and repaints, for stretches that never yield).</summary>
    public void Report(string detail)
    {
        _detailLabel.Text = detail;
        _detailLabel.Refresh();
        Refresh();
    }

    /// <summary>Sets the bold headline (e.g. which suit is being processed).</summary>
    public void SetStep(string step)
    {
        _stepLabel.Text = step;
        _stepLabel.Refresh();
        Refresh();
    }

    /// <summary>Advances a determinate bar and updates the sub-status.</summary>
    public void Advance(int value, string detail)
    {
        if (!_bar.Indeterminate)
        {
            _bar.Value = value;
        }
        Report(detail);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _owner is not null)
        {
            _owner.Enabled = true; // always hand input back, even if the operation threw
            _owner.Activate();
        }
        base.Dispose(disposing);
    }
}
