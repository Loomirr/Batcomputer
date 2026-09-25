using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>One primary action, separate release notes and live activity, and a session surviving dialog closure.</summary>
internal sealed class AppUpdatesForm : AdaptiveForm
{
    private static StagedAppUpdate? Pending;
    private static AppUpdateRelease? PendingRelease;
    private static bool Scheduled;
    private readonly AppUpdateService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private readonly System.Windows.Forms.Timer _activityTimer = new() { Interval = 1000 };
    private readonly Label _headline, _status, _target, _activitySummary;
    private readonly TextBox _notes, _activity;
    private readonly Button _primary, _cancel, _restart, _stable, _beta, _releaseTab, _activityTab;
    private readonly CheckBox _startup;
    private readonly UpdateTrack _track;
    private readonly bool _sandbox;
    private AppUpdateRelease? _release;
    private StagedAppUpdate? _staged;
    private bool _busy, _closing, _startupConfirmed;
    private string _activityFingerprint = "", _lastError = "";
    private static readonly Color Surface = Color.FromArgb(32, 36, 44);
    private static readonly Color Muted = Color.FromArgb(172, 181, 197);
    private UpdateCenterView? _cinematic;

    internal AppUpdatesForm(Uri? testFeed = null, AppUpdateRelease? knownRelease = null)
    {
        testFeed ??= AppUpdateTestEnvironment.Feed;
        _sandbox = testFeed != null;
        if (_sandbox && !File.Exists(Path.Combine(AppSettings.ToolRoot, AppUpdateInstaller.SandboxMarker)))
            throw new InvalidOperationException("Local updater testing requires a disposable marked app folder.");
        _service = new(testFeed);
        Text = "Batcomputer — Update center" + (_sandbox ? " · LOCAL TEST" : "");
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(960, 690); MinimumSize = new Size(840, 650);
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(21, 24, 30); ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "UPDATE CENTER", Bounds = new Rectangle(0, 0, 480, 22), Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Theme.Gold });
        header.Controls.Add(new Label { Text = "Keep your workshop current.", Bounds = new Rectangle(0, 24, 700, 44), Font = new Font("Segoe UI", 21, FontStyle.Bold), ForeColor = Color.White });
        root.Controls.Add(header, 0, 0);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var side = new UpdateCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 18, 0), Padding = new Padding(18) };
        var sideLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
        sideLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var h in new[] { 24f, 54f, 36f, 30f, 40f, 46f, 56f, 0f, 38f, 38f })
            sideLayout.RowStyles.Add(new RowStyle(h == 0 ? SizeType.Percent : SizeType.Absolute, h == 0 ? 100 : h));
        sideLayout.Controls.Add(TextLabel("INSTALLED VERSION", Muted), 0, 0);
        sideLayout.Controls.Add(TextLabel(AppVersion.Display, Color.White, 14, true), 0, 1);
        sideLayout.Controls.Add(TextLabel(_sandbox ? "LOCAL TEST • isolated copy" : "Windows • 64-bit", _sandbox ? Theme.Gold : Muted, 9), 0, 2);
        sideLayout.Controls.Add(TextLabel("RELEASE CHANNEL", Muted, 9, true), 0, 3);
        var channels = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        channels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); channels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _stable = MakeButton("Stable"); _beta = MakeButton("Beta");
        channels.Controls.Add(_stable, 0, 0); channels.Controls.Add(_beta, 1, 0); sideLayout.Controls.Add(channels, 0, 4);
        sideLayout.Controls.Add(TextLabel("Beta includes stable releases\nand early fixes.", Muted, 9), 0, 5);
        _startup = new CheckBox { Dock = DockStyle.Fill, Text = "Check on startup\nNotify only; never auto-install", Checked = AppSettings.Current.CheckForAppUpdatesOnStartup,
            ForeColor = Muted, Font = new Font("Segoe UI", 9), AutoSize = false };
        sideLayout.Controls.Add(_startup, 0, 6);
        var releases = MakeButton("Release history ↗"); var recovery = MakeButton("Backups & recovery ↗");
        sideLayout.Controls.Add(releases, 0, 8); sideLayout.Controls.Add(recovery, 0, 9);
        side.Controls.Add(sideLayout); columns.Controls.Add(side, 0, 0);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 240)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var hero = new UpdateCard { Dock = DockStyle.Fill, Padding = new Padding(20), Margin = new Padding(0, 0, 0, 14) };
        var heroLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var h in new[] { 34f, 25f, 58f, 32f, 30f }) heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        _headline = TextLabel("Ready when you are", Color.White, 18, true);
        _target = TextLabel("Find the latest fixes and improvements.", Muted, 9);
        _status = TextLabel(_sandbox ? "Connected to your local test feed. Only this copy will be updated." : "Check for a newer compatible release from Loomirr/Batcomputer.", Muted, 10);
        _track = new UpdateTrack { Dock = DockStyle.Fill };
        _activitySummary = TextLabel("Your projects, settings and installed mods stay in place.", Muted, 9);
        heroLayout.Controls.Add(_headline, 0, 0); heroLayout.Controls.Add(_target, 0, 1); heroLayout.Controls.Add(_status, 0, 2);
        heroLayout.Controls.Add(_track, 0, 3); heroLayout.Controls.Add(_activitySummary, 0, 4); hero.Controls.Add(heroLayout); content.Controls.Add(hero, 0, 0);
        var detailCard = new UpdateCard { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 12) };
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _releaseTab = MakeButton("What's new", 132); _activityTab = MakeButton("Live activity", 132);
        tabs.Controls.AddRange(new Control[] { _releaseTab, _activityTab }); details.Controls.Add(tabs, 0, 0);
        var textHost = new Panel { Dock = DockStyle.Fill };
        _notes = DetailBox("Release notes"); _activity = DetailBox("Live update activity");
        _notes.Text = "Release notes will appear here after a check.\r\n\r\nYou choose when to download and install.";
        textHost.Controls.Add(_activity); textHost.Controls.Add(_notes); details.Controls.Add(textHost, 0, 1);
        detailCard.Controls.Add(details); content.Controls.Add(detailCard, 0, 1);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        _primary = MakeButton("Check for updates"); _cancel = MakeButton("Cancel");
        _restart = MakeButton("Restart now");
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); actions.Controls.Add(_restart, 2, 0);
        actions.Controls.Add(_primary, 0, 0); actions.Controls.Add(_cancel, 1, 0); content.Controls.Add(actions, 0, 2);
        columns.Controls.Add(content, 1, 0); root.Controls.Add(columns, 0, 1);
        root.Controls.Add(TextLabel("No forced restarts. Verified downloads. Previous application files retained for recovery.", Muted, 9), 0, 2);
        Controls.Add(root);
        // Never expose the legacy layout during asynchronous WebView initialization.
        root.Visible = false;
        var loading = new Label { Dock = DockStyle.Fill, Text = "Opening Update center…", TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(25, 28, 35), ForeColor = Muted };
        Controls.Add(loading);
        _primary.Click += async (_, _) => { if (_staged != null) Schedule(); else if (_release != null) await DownloadAsync(); else await CheckAsync(); };
        _cancel.Click += (_, _) => _operation?.Cancel();
        _restart.Click += (_, _) => RestartNow();
        _stable.Click += (_, _) => ChangeChannel(false); _beta.Click += (_, _) => ChangeChannel(true);
        _startup.CheckedChanged += (_, _) => SavePreferences();
        _releaseTab.Click += (_, _) => SelectDetails(false); _activityTab.Click += (_, _) => SelectDetails(true);
        releases.Click += (_, _) => OpenLocation(AppUpdateService.ReleasesPage);
        recovery.Click += (_, _) => { var folder = Path.Combine(AppSettings.ToolRoot, AppUpdateInstaller.WorkFolder); Directory.CreateDirectory(folder); OpenLocation(folder); };
        _activityTimer.Tick += (_, _) => RefreshActivity();
        Shown += (_, _) => { RefreshActivity(); _activityTimer.Start(); };
        FormClosed += (_, _) => { _closing = true; _activityTimer.Stop(); _activityTimer.Dispose(); _lifetime.Cancel(); _operation?.Cancel(); _service.Dispose(); };
        SelectDetails(false);
        if (Pending != null) { _staged = Pending; _release = PendingRelease; _notes.Text = _release?.Notes ?? "Verified package retained for this session."; SetReady(); }
        else if (knownRelease != null) ShowRelease(knownRelease);
        UpdateButtons();
        // Keep the native screen as a functional fallback when WebView2 cannot start.
        Shown += async (_, _) =>
        {
            if (_cinematic != null) return;
            var view = _cinematic = new UpdateCenterView();
            view.Ready += () => { loading.Visible = false; root.Visible = false; view.Visible = true; view.BringToFront(); PresentCinematic(); };
            view.Failed += () => { loading.Visible = false; view.Visible = false; root.Visible = true; root.BringToFront(); };
            view.Command += async (command, value) =>
            {
                if (_closing) return;
                switch (command)
                {
                    case "primary" when !_busy && !Scheduled:
                        if (_staged != null) Schedule(); else if (_release != null) await DownloadAsync(); else await CheckAsync(); break;
                    case "cancel": _operation?.Cancel(); break;
                    case "restart" when !_busy: RestartNow(); break;
                    case "channel": ChangeChannel(value); break;
                    case "startup": _startup.Checked = value; break;
                    case "releases": OpenLocation(AppUpdateService.ReleasesPage); break;
                    case "recovery":
                        var folder = Path.Combine(AppSettings.ToolRoot, AppUpdateInstaller.WorkFolder);
                        Directory.CreateDirectory(folder); OpenLocation(folder); break;
                }
                PresentCinematic();
            };
            Controls.Add(view); view.SendToBack();
            await view.InitializeAsync();
        };
        _activityTimer.Tick += (_, _) => PresentCinematic();
    }

    private void PresentCinematic() => _cinematic?.Present(new
    {
        installed = AppVersion.Display, headline = _headline.Text, target = _target.Text, status = _status.Text,
        notes = _notes.Text, activity = _activity.Text, sandbox = _sandbox, beta = AppSettings.Current.IncludeBetaAppUpdates,
        startup = _startup.Checked, action = Scheduled ? "Scheduled · close Batcomputer to install" : _busy ? (_track.Step == 2 ? "Verifying…" : "Working…")
            : _staged != null ? "Install when I exit" : _release != null ? "Download update" : "Check for updates",
        disabled = _busy || Scheduled, busy = _busy, scheduled = Scheduled, ready = _staged != null,
        step = _track.Step, error = _lastError.Length > 0, accent = ColorTranslator.ToHtml(Theme.Gold)
    });

    private static Label TextLabel(string text, Color color, float size = 10, bool bold = false) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = color,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
    private static Button MakeButton(string text, int width = 0)
    {
        var button = new Button { Text = text, Dock = width == 0 ? DockStyle.Fill : DockStyle.None, Width = width == 0 ? 100 : width, Height = 32,
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Surface, Font = new Font("Segoe UI", 9, FontStyle.Bold), Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 6, 0), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 74, 91); button.FlatAppearance.MouseOverBackColor = Color.FromArgb(51, 58, 72); return button;
    }
    private static TextBox DetailBox(string name) => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
        BackColor = Surface, ForeColor = Color.FromArgb(218, 224, 234), Font = new Font("Segoe UI", 10), AccessibleName = name };
    private void SelectDetails(bool activity)
    {
        _activity.Visible = activity; _notes.Visible = !activity;
        _releaseTab.ForeColor = activity ? Muted : Theme.Gold; _activityTab.ForeColor = activity ? Theme.Gold : Muted;
    }
    private void ChangeChannel(bool beta)
    {
        if (_busy || Scheduled) return;
        AppSettings.Current.IncludeBetaAppUpdates = beta; SavePreferences();
        _release = null; _staged = null; Pending = null; PendingRelease = null;
        _headline.Text = "Channel changed"; _status.Text = "Check again to see releases in this channel."; _target.Text = beta ? "Stable + beta releases" : "Stable releases only";
        _track.Step = 0; UpdateButtons();
    }
    private void SavePreferences()
    {
        try { AppSettings.Current.CheckForAppUpdatesOnStartup = _startup.Checked; AppSettings.Current.Save(); }
        catch (Exception ex) { ShowError("Preferences could not be saved", ex); }
    }
    private void UpdateButtons()
    {
        _stable.Enabled = _beta.Enabled = !_busy && !Scheduled;
        _stable.BackColor = !AppSettings.Current.IncludeBetaAppUpdates ? Color.FromArgb(68, 61, 37) : Surface;
        _beta.BackColor = AppSettings.Current.IncludeBetaAppUpdates ? Color.FromArgb(68, 61, 37) : Surface;
        _cancel.Visible = _busy; _primary.Enabled = !_busy && !Scheduled;
        _restart.Visible = _staged != null; _restart.Enabled = !_busy && _staged != null;
        _primary.Text = Scheduled ? "Scheduled · exit Batcomputer to install" : _busy ? "Working…" : _staged != null ? "Install on exit →" : _release != null ? "Download update →" : "Check for updates →";
        _primary.BackColor = _primary.Enabled ? Theme.Gold : Color.FromArgb(66, 64, 50); _primary.ForeColor = _primary.Enabled ? Color.FromArgb(24, 26, 31) : Muted;
        PresentCinematic();
    }
    private void ShowRelease(AppUpdateRelease? release)
    {
        _release = release; _staged = null; _lastError = "";
        _headline.Text = release == null ? "You're all set" : "An update is available";
        _target.Text = release == null ? "Installed " + AppVersion.Display : AppVersion.Display + "  →  " + release.Version;
        _notes.Text = release?.Notes ?? "No newer compatible updater package was found in this channel. Older manual-only packages remain available in Release history.";
        _status.Text = release == null ? "No newer compatible update was found for this channel."
            : release.FilePlan is { } plan ? $"{release.Size / 1048576d:0.00} MB to download · reusing {plan.ReusedFiles} unchanged files. Full file payloads: {plan.FullSize / 1048576d:0.00} MB."
            : $"{release.Size / 1048576d:0.0} MB · Download now, install when you're ready.";
        _track.Step = release == null ? 0 : 1; UpdateButtons();
    }
    private void SetReady()
    {
        _headline.Text = Scheduled ? "Restart on your terms" : "Ready to install";
        _target.Text = AppVersion.Display + "  →  " + _staged?.Manifest.Version;
        _status.Text = Scheduled ? "Save your work and exit Batcomputer within ten minutes. Closing only this menu will not install the update."
            : "Every application file has been verified. You can close this menu and return later without downloading again.";
        _track.Step = 3; UpdateButtons();
    }
    private async Task CheckAsync()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(45)); _operation = operation; _busy = true;
        _headline.Text = "Looking for updates"; _status.Text = "Checking " + (_sandbox ? "the local test feed…" : "GitHub releases…"); _track.Step = 0; UpdateButtons();
        try { var release = await _service.CheckAsync(AppSettings.Current.IncludeBetaAppUpdates, operation.Token); if (!_closing) ShowRelease(release); }
        catch (OperationCanceledException) { if (!_closing) { _headline.Text = "Check stopped"; _status.Text = "Cancelled or timed out. Nothing was changed; you can try again."; } }
        catch (Exception ex) { if (!_closing) ShowError("Couldn't check for updates", ex); }
        finally { _operation = null; if (!_closing) { _busy = false; UpdateButtons(); } }
    }
    private async Task DownloadAsync()
    {
        if (_release == null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(10)); _operation = operation; _busy = true; UpdateButtons();
        _headline.Text = "Downloading your update"; _track.Step = 1; string? transaction = null;
        try
        {
            transaction = AppUpdateInstaller.NewTransaction(AppSettings.ToolRoot);
            _staged = await _service.DownloadAsync(_release, transaction, new Progress<string>(message =>
            {
                if (_closing || !_busy) return;
                _status.Text = message;
                if (message.StartsWith("Verifying", StringComparison.Ordinal)) { _track.Step = 2; _headline.Text = "Checking every file"; }
                PresentCinematic();
            }), operation.Token);
            Pending = _staged; PendingRelease = _release;
            if (!_closing) SetReady();
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException;
            if (transaction != null) try { File.WriteAllText(Path.Combine(transaction, "status.txt"), cancelled ? "Download cancelled. Application files unchanged." : "Download failed. Application files unchanged. " + ex.Message); } catch { }
            if (!_closing)
            {
                if (cancelled) { _headline.Text = "Download stopped"; _status.Text = "Your installed app is unchanged. Download again whenever you're ready."; }
                else ShowError("Download couldn't be verified", ex);
            }
        }
        finally { _operation = null; if (!_closing) { _busy = false; UpdateButtons(); } }
    }
    private void Schedule()
    {
        if (_staged == null) return;
        if (MessageBox.Show(this, "Install this verified update after you exit Batcomputer?\n\nSave your work, then close every Batcomputer window from this folder within ten minutes. Closing just the Update center is not enough. Your editor will restart automatically.",
            "Install on exit", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        try { AppUpdateInstaller.Schedule(_staged); Scheduled = true; SetReady(); SelectDetails(true); }
        catch (Exception ex) { ShowError("Couldn't schedule the update", ex); }
    }
    private void RestartNow()
    {
        if (_busy || _staged == null || _closing) return;
        if (MessageBox.Show(this, "Save your work before continuing. Restart now will close all windows in this Batcomputer instance, install the verified update, then reopen Batcomputer.\n\nOther instances from this folder must also be closed before installation can start. If an editor blocks closing, no files will be replaced; the update will remain scheduled for normal exit.\n\nHave you saved your work and are you ready to restart?",
            "Restart and update", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        try
        {
            if (!Scheduled) { AppUpdateInstaller.Schedule(_staged); Scheduled = true; }
            SetReady();
            // Defer until the WebView/native click callback has returned. Application.Exit
            // raises every FormClosing guard, including owned/modal editors. The helper,
            // not Application.Restart or a forced process exit, handles the relaunch.
            BeginInvoke(new Action(() =>
            {
                var closing = new System.ComponentModel.CancelEventArgs();
                Application.Exit(closing);
                if (closing.Cancel && !_closing && !IsDisposed)
                {
                    _headline.Text = "Restart paused";
                    _status.Text = "An editor kept Batcomputer open. Finish or save your work, then restart within ten minutes. The update is still scheduled for exit.";
                    UpdateButtons();
                }
            }));
        }
        catch (Exception ex) { ShowError("Couldn't restart for the update", ex); }
    }
    private void ShowError(string title, Exception ex)
    {
        _headline.Text = title; _status.Text = ex.Message; _lastError = title + "\r\n" + ex.Message;
        _activityFingerprint = ""; RefreshActivity(); SelectDetails(true);
    }
    private void OpenLocation(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Couldn't open that location", ex); }
    }
    private void RefreshActivity()
    {
        var snapshot = AppUpdateActivity.ReadLatest(AppSettings.ToolRoot);
        var fingerprint = snapshot.Text + _lastError;
        if (_activityFingerprint == fingerprint) return;
        _activityFingerprint = fingerprint;
        _activity.Text = (_lastError.Length > 0 ? _lastError + "\r\n\r\n" : "") + snapshot.Text;
        if (Scheduled && snapshot.ScheduleFailed && snapshot.Transaction == _staged?.Directory)
        {
            Scheduled = false; Pending = null; PendingRelease = null; _staged = null; _release = null;
            _headline.Text = "Installation didn't finish"; _status.Text = "See Live activity for details. You can check again to prepare a fresh update.";
            SelectDetails(true); UpdateButtons();
        }
        if (snapshot.StartupConfirmed && !_startupConfirmed && _release == null && !_busy)
        {
            _startupConfirmed = true; _headline.Text = "Update complete"; _target.Text = "Running " + AppVersion.Display;
            _status.Text = "Update installed and startup confirmed. Your previous application files are available in Backups & recovery.";
            _activitySummary.Text = "Startup confirmed · backup retained"; _activitySummary.ForeColor = Color.FromArgb(92, 218, 166); _track.Step = 4;
        }
    }
    private sealed class UpdateCard : Panel
    {
        internal UpdateCard() { DoubleBuffered = true; BackColor = Surface; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Native fallback uses honest square corners, not a rounded stroke over a square fill.
            using var pen = new Pen(Color.FromArgb(61, 69, 83));
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
    }
    private sealed class UpdateTrack : Control
    {
        private int _step;
        internal int Step { get => _step; set { _step = value; Invalidate(); } }
        internal UpdateTrack() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var labels = new[] { "01  CHECK", "02  DOWNLOAD", "03  VERIFY", "04  RESTART" };
            using var font = new Font("Segoe UI", 8, FontStyle.Bold);
            for (var i = 0; i < 4; i++)
            {
                var width = ClientSize.Width / 4; var color = i < Step ? Color.FromArgb(92, 218, 166) : i == Step ? Theme.Gold : Color.FromArgb(96, 106, 122);
                using var brush = new SolidBrush(color); e.Graphics.FillRectangle(brush, i * width, 0, Math.Max(1, width - 6), 3);
                TextRenderer.DrawText(e.Graphics, labels[i], font, new Rectangle(i * width, 7, width - 4, 22), color, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
