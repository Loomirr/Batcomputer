using System.Diagnostics;

namespace Batcomputer;

/// <summary>
/// A modeless progress window, with an optional live activity view for builds.
///
/// Modeless + owner-disabled rather than ShowDialog(), because the operations it reports on are
/// async: ShowDialog would block the caller. The awaits inside packaging pump the message loop, so
/// the bar/labels repaint on their own; <see cref="Report"/> also forces a repaint for the long
/// synchronous stretches (retoc, UAssetAPI) that never yield.
/// </summary>
public sealed class ProgressDialog : AdaptiveForm
{
    private readonly Label _stepLabel = new();
    private readonly Label _detailLabel = new();
    private readonly ThemedProgressBar _bar = new();
    private readonly Form? _owner;
    private readonly bool _ownerWasEnabled;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private readonly DiagnosticsControl? _activity;
    private readonly Label _contextLabel = new();
    private readonly Label _elapsedLabel = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 250 };
    private string? _pendingDetail;
    private string? _pendingStep;
    private string? _pendingContext;
    private int _pendingValue = -1;

    /// <param name="totalSteps">0 = indeterminate (marquee); otherwise a determinate bar.</param>
    public ProgressDialog(Form owner, string title, int totalSteps = 0, bool detailed = false)
        : this(owner, title, totalSteps, detailed, showWindow: true) { }

    internal ProgressDialog(Form owner, string title, int totalSteps, bool detailed, bool showWindow)
    {
        _owner = owner;
        _ownerWasEnabled = owner.Enabled;
        Text = title;
        AutoScaleMode = AutoScaleMode.Dpi;
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
        _stepLabel.Name = "BuildPhase";
        _stepLabel.Height = 30;
        _stepLabel.Padding = new Padding(16, 14, 16, 0);
        _stepLabel.ForeColor = Theme.Gold;
        _stepLabel.Font = AppFonts.Condensed(10f, FontStyle.Bold);
        _stepLabel.Text = title;

        _detailLabel.Dock = DockStyle.Top;
        _detailLabel.Name = "BuildDetail";
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

        if (detailed)
        {
            Width = 880;
            Height = 610;
            MinimumSize = new Size(620, 460);
            FormBorderStyle = FormBorderStyle.Sizable;
            Font = ReleaseUi.Body;
            _stepLabel.Height = 42;
            _stepLabel.Font = ReleaseUi.Title;
            _stepLabel.ForeColor = Theme.OnDark;
            _stepLabel.Padding = Padding.Empty;
            _detailLabel.Height = 46;
            _detailLabel.Font = ReleaseUi.Body;
            _detailLabel.ForeColor = Theme.OnDark;
            _detailLabel.Padding = new Padding(0, 5, 0, 0);
            _contextLabel.Dock = DockStyle.Top;
            _contextLabel.Name = "BuildContext";
            _contextLabel.Height = 34;
            _contextLabel.Padding = new Padding(0, 2, 0, 0);
            _contextLabel.AutoEllipsis = true;
            _contextLabel.ForeColor = Theme.OnDarkMuted;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 208));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var eyebrow = new Label { Dock = DockStyle.Top, Height = 24, Text = "RELEASE WORKSPACE", Font = ReleaseUi.Small, ForeColor = Theme.Gold };
            var status = new RoundedPanel { Dock = DockStyle.Top, Height = 102, BackColor = Theme.CardBg, BorderColor = Theme.LineSoft,
                CornerRadius = 10, Padding = new Padding(16, 12, 16, 12) };
            var taskLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = "CURRENT TASK", Font = ReleaseUi.Small, ForeColor = Theme.OnDarkMuted };
            _bar.Height = 6;
            _bar.Dock = DockStyle.Bottom;
            _bar.BackColor = Theme.CardBg;
            status.Controls.Add(_bar);
            status.Controls.Add(_detailLabel);
            status.Controls.Add(taskLabel);
            header.Controls.Add(status);
            header.Controls.Add(_contextLabel);
            header.Controls.Add(_stepLabel);
            header.Controls.Add(eyebrow);

            _activity = new DiagnosticsControl { Dock = DockStyle.Fill, MaximumCharacters = 200_000, LogForeColor = Theme.OnDarkMuted };
            var logHost = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(14), BackColor = Theme.SlateDark,
                BorderColor = Theme.LineSoft, CornerRadius = 10, Margin = Padding.Empty };
            logHost.Controls.Add(_activity);
            var logHeading = new Label { Dock = DockStyle.Top, Height = 26, Text = "Activity", Font = ReleaseUi.Emphasis, ForeColor = Theme.OnDark };
            logHost.Controls.Add(logHeading);
            var footer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 14, 0, 0), Margin = Padding.Empty };
            var copy = ReleaseUi.Button("Copy activity log", 150);
            copy.Dock = DockStyle.Right;
            copy.Click += (_, _) => copy.Text = _activity.TryCopyLogToClipboard() ? "Copied" : "Copy failed; retry";
            _elapsedLabel.Dock = DockStyle.Left;
            _elapsedLabel.Width = 150;
            _elapsedLabel.Font = ReleaseUi.Small;
            _elapsedLabel.ForeColor = Theme.OnDarkMuted;
            _elapsedLabel.TextAlign = ContentAlignment.MiddleLeft;
            _elapsedLabel.Name = "BuildElapsed";
            footer.Controls.Add(copy);
            footer.Controls.Add(_elapsedLabel);
            var tips = new ToolTip();
            tips.SetToolTip(logHeading, "Scroll up to pause following. Full output stays in Diagnostics after this window closes.");
            Disposed += (_, _) => tips.Dispose();
            Controls.Clear();
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(logHost, 0, 1);
            layout.Controls.Add(footer, 0, 2);
            Controls.Add(layout);
            barHost.Dispose();
            Shown += (_, _) => Theme.UseDarkTitleBar(this);
        }

        _statusTimer.Tick += (_, _) => FlushStatus();
        _statusTimer.Start();
        FlushStatus();

        if (_owner is not null)
        {
            Owner = _owner;
            _owner.Enabled = false; // block input on the main window while the operation runs
        }
        if (showWindow)
        {
            Show(_owner);
            Refresh();
        }
    }

    /// <summary>Updates the sub-status line (and repaints, for stretches that never yield).</summary>
    public void Report(string detail)
    {
        Interlocked.Exchange(ref _pendingDetail, detail);
        FlushIfOnUiThread();
    }

    /// <summary>Sets the bold headline (e.g. which suit is being processed).</summary>
    public void SetStep(string step)
    {
        Interlocked.Exchange(ref _pendingStep, step);
        FlushIfOnUiThread();
    }

    /// <summary>Mod or numbered content item, separate from the current processing step.</summary>
    public void SetContext(string context)
    {
        Interlocked.Exchange(ref _pendingContext, context);
        FlushIfOnUiThread();
    }

    public void AppendActivity(string message) => _activity?.AppendLog(message);

    private void FlushIfOnUiThread()
    {
        if (Environment.CurrentManagedThreadId == _uiThreadId && !IsDisposed && !Disposing)
        {
            FlushStatus();
            Refresh();
        }
    }

    private void FlushStatus()
    {
        if (IsDisposed || Disposing) return;
        if (Interlocked.Exchange(ref _pendingDetail, null) is { } detail) _detailLabel.Text = detail;
        if (Interlocked.Exchange(ref _pendingStep, null) is { } step) _stepLabel.Text = step;
        if (Interlocked.Exchange(ref _pendingContext, null) is { } context) _contextLabel.Text = context;
        var value = Interlocked.Exchange(ref _pendingValue, -1);
        if (value >= 0 && !_bar.Indeterminate) _bar.Value = value;
        var duration = _elapsed.Elapsed;
        _elapsedLabel.Text = $"Elapsed {(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    /// <summary>Advances a determinate bar and updates the sub-status.</summary>
    public void Advance(int value, string detail)
    {
        Interlocked.Exchange(ref _pendingValue, Math.Max(0, value));
        Report(detail);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // Closing a progress view does not cancel the underlying operation.
        if (_activity is not null && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _elapsed.Stop();
            if (!IsDisposed && _owner is { IsDisposed: false })
            {
                _owner.Enabled = _ownerWasEnabled;
                if (_ownerWasEnabled && _owner.Visible) _owner.Activate();
            }
        }
        base.Dispose(disposing);
    }
}
