namespace Batcomputer;

public enum CharacterUtilityKind { Tracking, Healing }

public sealed class CharacterUtilitiesForm : AdaptiveForm
{
    public CharacterUtilitySettings Result { get; private set; }
    public CharacterUtilitiesForm(CharacterUtilitySettings? settings, CharacterUtilityKind? kind = null)
    {
        settings ??= new();
        Result = new() { Tracking = settings.Tracking, Healing = settings.Healing, HealingPercentPerSecond = settings.HealingPercentPerSecond };
        Text = "Batcomputer — " + (kind?.ToString() ?? "Tracking and healing"); StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(720, 540); MinimumSize = new(640, 480); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(18) };
        root.RowStyles.Add(new(SizeType.Absolute, 48)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 60));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = kind?.ToString().ToUpperInvariant() ?? "TRACKING & HEALING", Font = Theme.Title, ForeColor = Theme.Abilities, Dock = DockStyle.Fill }, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.CardBg };
        root.Controls.Add(scroll, 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new(14) };
        scroll.Controls.Add(body);
        void Row(Control control, int height) { var row = body.RowCount++; body.RowStyles.Add(new(SizeType.Absolute, height)); control.Dock = DockStyle.Fill; body.Controls.Add(control, 0, row); }
        var tracking = new CheckBox { Text = "Enable native clue / trail tracking", Name = "utility-tracking", Checked = settings.Tracking, ForeColor = Theme.OnDark };
        if (kind != CharacterUtilityKind.Healing)
        {
            Row(tracking, 36);
            Row(new Label { Text = "Uses the game's existing Spectral Vision clue spots, with native vision effects and tracking animations. Interact at a compatible clue to begin. This does not generate scent trails for arbitrary enemies or replace Focus / held-item controls.", ForeColor = Theme.OnDarkMuted }, 94);
        }
        var healing = new CheckBox { Text = "Enable passive healing", Name = "utility-healing", Checked = settings.Healing, ForeColor = Theme.OnDark };
        if (kind != CharacterUtilityKind.Tracking) Row(healing, 36);
        var rateRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        rateRow.ColumnStyles.Add(new(SizeType.Percent, 100)); rateRow.ColumnStyles.Add(new(SizeType.Absolute, 110));
        rateRow.Controls.Add(new Label { Text = "Maximum health restored per second (%)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var rate = new NumericUpDown { Name = "utility-healing-rate", Minimum = .1m, Maximum = 10, DecimalPlaces = 1, Increment = .1m,
            Value = float.IsFinite(settings.HealingPercentPerSecond) ? (decimal)Math.Clamp(settings.HealingPercentPerSecond, .1f, 10) : 2, Dock = DockStyle.Fill };
        rate.BackColor = Theme.CardHi; rate.ForeColor = Theme.OnDark; rate.Font = Theme.Body;
        rateRow.Controls.Add(rate, 1, 0);
        if (kind != CharacterUtilityKind.Tracking)
        {
            Row(rateRow, 40);
            Row(new Label { Text = "Adds slow regeneration outside combat using the native healing calculation. At 2%, this effect alone takes about 50 seconds to restore a full bar. No instant heal on loading or healing during death states. The game's other healing and difficulty rules still apply.", ForeColor = Theme.OnDarkMuted }, 100);
        }
        Row(new Label { Text = "Saved on this character/suit and replayed by builds. Rebased suits inherit these settings. This ability needs an in-game check on the selected playable donor. Tracking and healing are independent choices.", ForeColor = Theme.Abilities }, 64);
        rate.Enabled = healing.Checked; healing.CheckedChanged += (_, _) => rate.Enabled = healing.Checked;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new(0, 12, 0, 0) };
        root.Controls.Add(footer, 0, 2);
        var use = new Button { Text = "Use settings", Width = 140, Height = 36 };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 36, DialogResult = DialogResult.Cancel };
        Theme.StyleGoldButton(use); Theme.StyleDarkButton(cancel); footer.Controls.AddRange([use, cancel]); AcceptButton = use; CancelButton = cancel;
        use.Click += (_, _) => { Result = new() { Tracking = kind == CharacterUtilityKind.Healing ? settings.Tracking : tracking.Checked, Healing = kind == CharacterUtilityKind.Tracking ? settings.Healing : healing.Checked, HealingPercentPerSecond = kind == CharacterUtilityKind.Tracking ? settings.HealingPercentPerSecond : (float)rate.Value }; DialogResult = DialogResult.OK; Close(); };
    }
}
