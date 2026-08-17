namespace Batcomputer;

/// <summary>Progress surface for the one-time UE registry writer preparation.</summary>
public sealed class RegistryWriterProgressForm : Form
{
    private readonly Label _phase = new();
    private readonly Label _detail = new();
    private readonly ThemedProgressBar _progress = new();

    public RegistryWriterProgressForm()
    {
        Text = "Batcomputer - Finishing setup";
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
        ClientSize = new Size(620, 188);

        var eyebrow = new Label
        {
            Left = 18, Top = 18, Width = 584, Height = 16,
            Text = "SETUP - STEP 1 OF 2", Font = Theme.Eyebrow, ForeColor = Theme.Gold,
        };
        var title = new Label
        {
            Left = 18, Top = 40, Width = 584, Height = 26,
            Text = "Preparing the Asset Registry writer", Font = AppFonts.Condensed(13f, FontStyle.Bold), ForeColor = Theme.OnDark,
        };
        var intro = new Label
        {
            Left = 18, Top = 68, Width = 584, Height = 32,
            Text = "This one-time UE 5.6 build lets new suits register in the game. The first build can take a few minutes.",
            Font = Theme.Body, ForeColor = Theme.OnDarkMuted,
        };

        _phase.SetBounds(18, 112, 584, 18);
        _phase.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _phase.Font = Theme.BodyStrong;
        _phase.ForeColor = Theme.OnDark;
        _phase.Text = "Starting the writer build…";

        _detail.SetBounds(18, 132, 584, 18);
        _detail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _detail.Font = Theme.Caption;
        _detail.ForeColor = Theme.OnDarkMuted;
        _detail.Text = "Next: first-time game asset extraction.";

        _progress.SetBounds(18, 158, 584, 12);
        _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _progress.Indeterminate = true;

        Controls.AddRange(new Control[] { eyebrow, title, intro, _phase, _detail, _progress });
        Theme.ApplyReadableTheme(this);
    }

    public void UpdateFromWriterLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateFromWriterLog(line));
            return;
        }

        if (line.Contains("Verifying the UE 5.6 Asset Registry writer", StringComparison.OrdinalIgnoreCase))
        {
            _phase.Text = "Verifying the writer";
            _detail.Text = "Creating a small test AssetRegistry.bin before extraction starts.";
        }
        else if (line.Contains("Rebuilding the UE 5.6 Asset Registry writer", StringComparison.OrdinalIgnoreCase))
        {
            _phase.Text = "Rebuilding the writer";
            _detail.Text = "The previous cached build no longer matches this UE installation.";
        }
        else if (line.Contains("Preparing the UE 5.6 Asset Registry writer", StringComparison.OrdinalIgnoreCase))
        {
            _phase.Text = "Building the writer";
            _detail.Text = "Compiling the small local UE helper. This is only needed once.";
        }
    }

    public void SetFinished()
    {
        if (IsDisposed) return;
        _progress.Indeterminate = false;
        _progress.Value = 100;
        _phase.Text = "Asset Registry writer ready";
        _detail.Text = "Starting first-time game asset extraction…";
    }

    public void SetFailed(string error)
    {
        if (IsDisposed) return;
        _progress.Indeterminate = false;
        _progress.Value = 0;
        _phase.Text = "Asset Registry writer needs attention";
        _detail.Text = error;
    }
}
