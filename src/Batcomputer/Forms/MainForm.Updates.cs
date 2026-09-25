namespace Batcomputer;

public sealed partial class MainForm
{
    private LinkLabel? _appVersionLabel;
    private AppUpdateRelease? _availableAppUpdate;

    private void AddAppVersionLabel(Panel brand)
    {
        _appVersionLabel = new LinkLabel { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleCenter,
            Text = AppVersion.Display, Font = Theme.Caption, LinkColor = Theme.OnDarkMuted, ActiveLinkColor = Theme.Gold,
            LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent };
        _appVersionLabel.LinkClicked += (_, _) =>
        {
            using var updates = new AppUpdatesForm(knownRelease: _availableAppUpdate);
            updates.ShowDialog(this);
        };
        _toyboxToolTip.SetToolTip(_appVersionLabel, "Installed version " + AppVersion.Current + ". Click for updates.");
        brand.Controls.Add(_appVersionLabel);
    }

    internal void NotifyAppUpdate(AppUpdateRelease release)
    {
        _availableAppUpdate = release;
        if (_appVersionLabel != null)
        {
            _appVersionLabel.Text = AppVersion.Display + " · Update available";
            _appVersionLabel.LinkColor = Theme.Gold;
            _toyboxToolTip.SetToolTip(_appVersionLabel, "Installed: " + AppVersion.Current + "\nAvailable: " + release.Version + "\nClick to review the update.");
        }
    }
}
