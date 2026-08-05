namespace Batcomputer;

/// <summary>Small modal progress surface used by the game-asset refresh command.</summary>
public sealed class AssetRefreshProgressForm : Form
{
    private readonly Label _phase = new();
    private readonly Label _status = new();
    private readonly ThemedProgressBar _progress = new();
    private readonly Button _cancel = new();

    public event EventHandler? CancelRequested;

    public AssetRefreshProgressForm(bool firstRun = false)
    {
        Text = firstRun ? "Batcomputer - First-time extraction" : "Refreshing game assets";
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, firstRun ? 210 : 150);

        var offset = 0;
        if (firstRun)
        {
            var eyebrow = new Label
            {
                Left = 18, Top = 16, Width = 584, Height = 16,
                Text = "SETUP - STEP 2 OF 2", Font = Theme.Eyebrow, ForeColor = Theme.Gold,
            };
            var title = new Label
            {
                Left = 18, Top = 36, Width = 584, Height = 24,
                Text = "First-time game asset extraction", Font = AppFonts.Condensed(13f, FontStyle.Bold), ForeColor = Theme.OnDark,
            };
            var intro = new Label
            {
                Left = 18, Top = 62, Width = 584, Height = 32,
                Text = "Batcomputer is extracting character, animation, and localisation assets. This can use about 18 GB and may take a while.",
                Font = Theme.Body, ForeColor = Theme.OnDarkMuted,
            };
            Controls.AddRange(new Control[] { eyebrow, title, intro });
            offset = 60;
        }

        _phase.Left = 18;
        _phase.Top = 16 + offset;
        _phase.Width = 584;
        _phase.Height = 20;
        _phase.Font = AppFonts.Condensed(10f, FontStyle.Bold);
        _phase.ForeColor = Theme.Gold;
        _phase.Text = "Preparing";

        _status.Left = 18;
        _status.Top = 38 + offset;
        _status.Width = 584;
        _status.Height = 20;
        _status.AutoEllipsis = true;
        _status.Font = Theme.Mono;
        _status.ForeColor = Theme.OnDarkMuted;
        _status.Text = "Starting…";

        _progress.Left = 18;
        _progress.Top = 68 + offset;
        _progress.Width = 584;
        _progress.Height = 14;
        _progress.Maximum = 100;

        Theme.StyleDarkButton(_cancel);
        _cancel.Text = "Cancel";
        _cancel.Left = 502;
        _cancel.Top = 106 + offset;
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
