namespace Batcomputer;

/// <summary>Small modal progress surface used by the game-asset refresh command.</summary>
public sealed class AssetRefreshProgressForm : Form
{
    private readonly Label _phase = new();
    private readonly Label _status = new();
    private readonly ThemedProgressBar _progress = new();
    private readonly Button _cancel = new();

    public event EventHandler? CancelRequested;

    public AssetRefreshProgressForm()
    {
        Text = "Refreshing game assets";
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 150);

        _phase.Left = 18;
        _phase.Top = 16;
        _phase.Width = 584;
        _phase.Height = 20;
        _phase.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _phase.ForeColor = Theme.Gold;
        _phase.Text = "Preparing";

        _status.Left = 18;
        _status.Top = 38;
        _status.Width = 584;
        _status.Height = 20;
        _status.AutoEllipsis = true;
        _status.Font = Theme.Mono;
        _status.ForeColor = Theme.OnDarkMuted;
        _status.Text = "Starting…";

        _progress.Left = 18;
        _progress.Top = 68;
        _progress.Width = 584;
        _progress.Height = 14;
        _progress.Maximum = 100;

        Theme.StyleDarkButton(_cancel);
        _cancel.Text = "Cancel";
        _cancel.Left = 502;
        _cancel.Top = 106;
        _cancel.Width = 100;
        _cancel.Height = 28;
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _phase.Text = "Cancelling…";
            CancelRequested?.Invoke(this, EventArgs.Empty);
        };

        Controls.Add(_phase);
        Controls.Add(_status);
        Controls.Add(_progress);
        Controls.Add(_cancel);
        Theme.ApplyReadableTheme(this);
    }

    public void SetProgress(GameAssetRefreshService.Progress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        _progress.Indeterminate = false;
        _progress.Value = Math.Clamp(progress.Percent, 0, 100);
        _phase.Text = progress.Phase;
        _status.Text = progress.Detail;
    }

    public void SetIndeterminate(string status)
    {
        if (IsDisposed)
        {
            return;
        }

        _progress.Indeterminate = true;
        _phase.Text = status;
        _status.Text = "";
    }

    public void SetFinished(string status)
    {
        if (IsDisposed)
        {
            return;
        }

        _progress.Indeterminate = false;
        _progress.Value = 100;
        _phase.Text = status;
        _status.Text = "";
        _cancel.Enabled = false;
    }
}
