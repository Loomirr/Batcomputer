namespace Batcomputer;

public sealed class CharacterMovementForm : AdaptiveForm
{
    public CharacterMovementSettings? Result { get; private set; }

    public CharacterMovementForm(CharacterMovementSettings? settings)
    {
        settings ??= new();
        Text = "Batcomputer — Movement attributes";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(760, 660); MinimumSize = new(680, 580);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(20) };
        root.RowStyles.Add(new(SizeType.Absolute, 70)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 54));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "MOVEMENT ATTRIBUTES\nChoose which values to override for this character or suit.", Dock = DockStyle.Fill,
            Font = Theme.BodyStrong, ForeColor = Theme.Abilities }, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.CardBg };
        root.Controls.Add(scroll, 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 0, Padding = new(16) };
        scroll.Controls.Add(body);
        var choices = new List<(CheckBox Check, NumericUpDown Value, Action<CharacterMovementSettings, float?> Set, float Scale)>();
        void Add(string name, string title, string detail, float? saved, decimal initial, decimal min, decimal max,
            decimal increment, Action<CharacterMovementSettings, float?> set, float scale = 1)
        {
            var row = body.RowCount++; body.RowStyles.Add(new(SizeType.Absolute, 84));
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new(0, 0, 0, 10) };
            card.ColumnStyles.Add(new(SizeType.Percent, 100)); card.ColumnStyles.Add(new(SizeType.Absolute, 130));
            card.RowStyles.Add(new(SizeType.Absolute, 34)); card.RowStyles.Add(new(SizeType.Percent, 100));
            var check = new CheckBox { Text = title, Checked = saved.HasValue, Dock = DockStyle.Fill, Name = name + "-enabled", Font = Theme.BodyStrong };
            var value = new NumericUpDown { Name = name, Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = 0,
                Increment = increment, BackColor = Theme.CardHi, ForeColor = Theme.OnDark, Font = Theme.Body,
                Value = saved is { } f && float.IsFinite(f) ? (decimal)Math.Clamp(f * scale, (float)min, (float)max) : initial, Enabled = check.Checked };
            check.CheckedChanged += (_, _) => value.Enabled = check.Checked;
            card.Controls.Add(check, 0, 0); card.Controls.Add(value, 1, 0);
            var label = new Label { Text = detail, Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted };
            card.Controls.Add(label, 0, 1); card.SetColumnSpan(label, 2);
            body.Controls.Add(card, 0, row); choices.Add((check, value, set, scale));
        }
        Add("movement-run", "Run speed · %", "Scales ordinary ground speed; 100% keeps the baseline. Native sprint overrides keep their own speed.", settings.RunSpeedMultiplier, 100, 25, 300, 10, (s, v) => s.RunSpeedMultiplier = v, 100);
        Add("movement-sprint", "Sprint speed · cm/s", "Sets sprint speed, including boosted sprint variants. Requires an active native Sprint ability.", settings.SprintSpeed, 900, 50, 7500, 25, (s, v) => s.SprintSpeed = v);
        Add("movement-jump", "Jump height · %", "100% keeps the usual height; 200% targets twice the height under the same gravity. Special scripted jumps can differ.", settings.JumpHeightMultiplier, 100, 25, 400, 10, (s, v) => s.JumpHeightMultiplier = v, 100);
        Add("movement-glide", "Glide speed · %", "Scales the existing minimum, cruising and maximum forward speeds together. Requires native Gliding defaults.", settings.GlideSpeedMultiplier, 100, 25, 300, 10, (s, v) => s.GlideSpeedMultiplier = v, 100);
        Add("movement-descent", "Glide descent rate · %", "Lower values descend more slowly. Keeps the native conditions for the descent effect; does not grant gliding.", settings.GlideDescentMultiplier, 100, 10, 300, 10, (s, v) => s.GlideDescentMultiplier = v, 100);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new(0, 12, 0, 0) };
        root.Controls.Add(footer, 0, 2);
        var save = new Button { Text = "Save attributes", Width = 144, Height = 36 };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 36, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "Reset to defaults", Width = 145, Height = 36 };
        Theme.StyleGoldButton(save); Theme.StyleDarkButton(cancel); Theme.StyleDarkButton(reset);
        footer.Controls.AddRange([save, cancel, reset]); AcceptButton = save; CancelButton = cancel;
        reset.Click += (_, _) => { foreach (var choice in choices) choice.Check.Checked = false; };
        save.Click += (_, _) =>
        {
            var result = new CharacterMovementSettings();
            foreach (var choice in choices) choice.Set(result, choice.Check.Checked ? (float)choice.Value.Value / choice.Scale : null);
            if (CharacterMovementService.ValidationError(result) is { } error) { Dialog.Warn(this, "Check movement values", error); return; }
            Result = CharacterMovementService.Enabled(result) ? result : null;
            DialogResult = DialogResult.OK; Close();
        };
    }
}
