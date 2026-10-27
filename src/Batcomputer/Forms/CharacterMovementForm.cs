namespace Batcomputer;

public sealed class CharacterMovementForm : AdaptiveForm
{
    public CharacterMovementSettings? Result { get; private set; }

    public CharacterMovementForm(CharacterMovementSettings? settings)
    {
        settings ??= new();
        Text = "Batcomputer — Character attributes";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(1000, 740); MinimumSize = new(860, 640); ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(20) };
        root.RowStyles.Add(new(SizeType.Absolute, 96)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 64));
        Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        header.RowStyles.Add(new(SizeType.Absolute, 22)); header.RowStyles.Add(new(SizeType.Absolute, 34)); header.RowStyles.Add(new(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "ABILITIES  /  TUNING", Dock = DockStyle.Fill, Font = Theme.Eyebrow, ForeColor = Theme.Abilities }, 0, 0);
        header.Controls.Add(new Label { Text = "Character attributes", Dock = DockStyle.Fill, Font = Theme.Title }, 0, 1);
        header.Controls.Add(new Label { Text = "Enable an override to tune it. Other values keep the character's existing setup.", Dock = DockStyle.Fill,
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted }, 0, 2);
        root.Controls.Add(header, 0, 0);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Absolute, 190)); columns.ColumnStyles.Add(new(SizeType.Percent, 100)); root.Controls.Add(columns, 0, 1);
        var sidebar = new RoundedPanel { Dock = DockStyle.Fill, Padding = new(10), Margin = new(0, 0, 16, 0), BackColor = Theme.SlateDark, CornerRadius = Theme.Radius };
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        sidebar.Controls.Add(navigation); columns.Controls.Add(sidebar, 0, 0);
        navigation.Controls.Add(new Label { Text = "CATEGORIES", Font = Theme.Eyebrow, ForeColor = Theme.OnDarkMuted, Width = 140, Height = 32, Padding = new(8, 7, 0, 0) });
        var content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; columns.Controls.Add(content, 1, 0);
        var groups = new Dictionary<string, (Panel Page, Button Button)>();
        var summary = new Label { Text = "All values inherit the character setup", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted };
        var groupCounts = new Dictionary<string, List<CheckBox>>();
        var activeGroup = "Movement";
        void UpdateSummary()
        {
            var count = groupCounts.Values.SelectMany(checks => checks).Count(check => check.Checked);
            summary.Text = count == 0 ? "All values inherit the character setup" : $"{count} overrides enabled · saved with this character or suit";
            foreach (var (title, group) in groups)
            {
                var enabled = groupCounts[title].Count(check => check.Checked);
                group.Button.Text = title + (enabled > 0 ? "  · " + enabled : "");
                group.Button.BackColor = title == activeGroup ? Theme.Tint(Theme.Abilities) : Theme.SlateDark;
                group.Button.ForeColor = title == activeGroup ? Theme.Abilities : Theme.OnDarkMuted;
                group.Page.Visible = title == activeGroup;
            }
        }
        TableLayoutPanel Group(string title)
        {
            var page = new Panel { Dock = DockStyle.Fill, Name = "attributes-page-" + title, Visible = false }; content.Controls.Add(page);
            var heading = new TableLayoutPanel { Dock = DockStyle.Top, Height = 76, ColumnCount = 1, RowCount = 2 };
            heading.RowStyles.Add(new(SizeType.Absolute, 30)); heading.RowStyles.Add(new(SizeType.Percent, 100));
            heading.Controls.Add(new Label { Text = title, Font = Theme.Title, Dock = DockStyle.Fill, UseMnemonic = false }, 0, 0);
            heading.Controls.Add(new Label { Text = title == "Movement" ? "Core movement tuning. Scripted moves can use separate rules." : "Experimental · Requires matching native attributes. Test changes in-game.",
                Font = Theme.Caption, ForeColor = Theme.OnDarkMuted, Dock = DockStyle.Fill }, 0, 1);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            page.Controls.Add(scroll); page.Controls.Add(heading);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 0, Padding = new(0, 0, 6, 0) };
            table.ColumnStyles.Add(new(SizeType.Percent, 100));
            scroll.Controls.Add(table);
            var button = new Button { Name = "attributes-nav-" + title, Text = title, Width = 150, Height = 44, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new(8, 0, 0, 0), Margin = new(0, 0, 0, 5), Font = Theme.Caption, UseMnemonic = false };
            Theme.StyleDarkButton(button); button.FlatAppearance.BorderSize = 0; Theme.RoundControl(button);
            groupCounts[title] = []; groups[title] = (page, button); navigation.Controls.Add(button);
            button.Click += (_, _) => { activeGroup = title; UpdateSummary(); };
            table.Tag = title;
            return table;
        }
        var body = Group("Movement");
        var choices = new List<(CheckBox Check, NumericUpDown Value, Action<CharacterMovementSettings, float?> Set, float Scale)>();
        void Add(string name, string title, string detail, float? saved, decimal initial, decimal min, decimal max,
            decimal increment, Action<CharacterMovementSettings, float?> set, float scale = 1, int decimals = 0)
        {
            var row = body.RowCount++; body.RowStyles.Add(new(SizeType.Absolute, 118));
            var surface = new RoundedPanel { Dock = DockStyle.Fill, Padding = new(14), Margin = new(0, 0, 0, 10), BackColor = Theme.CardBg,
                BorderColor = Theme.LineSoft, CornerRadius = Theme.Radius };
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, BackColor = Color.Transparent };
            surface.Controls.Add(card);
            card.ColumnStyles.Add(new(SizeType.Percent, 100)); card.ColumnStyles.Add(new(SizeType.Absolute, 130));
            card.RowStyles.Add(new(SizeType.Absolute, 34)); card.RowStyles.Add(new(SizeType.Percent, 100));
            var check = new CheckBox { Text = title, Checked = saved.HasValue, Dock = DockStyle.Fill, Name = name + "-enabled", Font = Theme.BodyStrong, Cursor = Cursors.Hand };
            groupCounts[(string)body.Tag!].Add(check);
            var value = new NumericUpDown { Name = name, Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = decimals,
                Increment = increment, BackColor = Theme.CardHi, ForeColor = Theme.OnDark, Font = Theme.Body,
                Value = saved is { } f && float.IsFinite(f) ? (decimal)Math.Clamp(f * scale, (float)min, (float)max) : initial, Enabled = check.Checked };
            void RefreshState() { value.Enabled = check.Checked; surface.BorderColor = check.Checked ? Theme.Blend(Theme.Abilities, Theme.LineSoft, .5) : Theme.LineSoft; surface.Invalidate(); UpdateSummary(); }
            check.CheckedChanged += (_, _) => RefreshState();
            card.Controls.Add(check, 0, 0); card.Controls.Add(value, 1, 0);
            var label = new Label { Text = detail, Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, Font = Theme.Caption, Padding = new(0, 8, 0, 0) };
            card.Controls.Add(label, 0, 1); card.SetColumnSpan(label, 2);
            body.Controls.Add(surface, 0, row); choices.Add((check, value, set, scale)); RefreshState();
        }
        Add("movement-run", "Run speed · %", "Scales ordinary ground speed; 100% keeps the baseline. Native sprint overrides keep their own speed.", settings.RunSpeedMultiplier, 100, 25, 300, 10, (s, v) => s.RunSpeedMultiplier = v, 100);
        Add("movement-sprint", "Sprint speed · cm/s", "Sets sprint speed, including boosted sprint variants. Requires an active native Sprint ability.", settings.SprintSpeed, 900, 50, 7500, 25, (s, v) => s.SprintSpeed = v);
        Add("movement-jump", "Jump height · %", "100% keeps the usual height; 200% targets twice the height under the same gravity. Special scripted jumps can differ.", settings.JumpHeightMultiplier, 100, 25, 400, 10, (s, v) => s.JumpHeightMultiplier = v, 100);
        Add("movement-glide", "Glide speed · %", "Scales the existing minimum, cruising and maximum forward speeds together. Requires native Gliding defaults.", settings.GlideSpeedMultiplier, 100, 25, 300, 10, (s, v) => s.GlideSpeedMultiplier = v, 100);
        Add("movement-descent", "Glide descent rate · %", "Lower values descend more slowly. Keeps the native conditions for the descent effect; does not grant gliding.", settings.GlideDescentMultiplier, 100, 10, 300, 10, (s, v) => s.GlideDescentMultiplier = v, 100);
        foreach (var group in CharacterAttributeCatalog.All.GroupBy(definition => definition.Group))
        {
            body = Group(group.Key);
            foreach (var definition in group)
            {
                float? saved = settings.AttributeOverrides?.TryGetValue(definition.Id, out var value) == true ? value : null;
                var fractional = definition.Kind is CharacterAttributeCatalog.ValueKind.Seconds or CharacterAttributeCatalog.ValueKind.FocusUnits;
                Add("attribute-" + definition.Id, definition.Label + " · " + definition.Units, definition.Detail, saved,
                    (decimal)(definition.Initial * definition.DisplayScale), (decimal)(definition.Minimum * definition.DisplayScale),
                    (decimal)(definition.Maximum * definition.DisplayScale), fractional ? .1m : definition.Kind == CharacterAttributeCatalog.ValueKind.Multiplier ? 10 : 5,
                    (s, v) => { if (v is { } chosen) (s.AttributeOverrides ??= new(StringComparer.Ordinal))[definition.Id] = chosen; },
                    definition.DisplayScale, fractional ? 1 : 0);
            }
        }
        var footerRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new(0, 16, 0, 0) };
        footerRow.ColumnStyles.Add(new(SizeType.Percent, 100)); footerRow.ColumnStyles.Add(new(SizeType.AutoSize));
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        footerRow.Controls.Add(summary, 0, 0); footerRow.Controls.Add(footer, 1, 0); root.Controls.Add(footerRow, 0, 2);
        var save = new Button { Text = "Save attributes", Width = 144, Height = 36 };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 36, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "Reset all", Width = 104, Height = 36 };
        Theme.StyleGoldButton(save); Theme.StyleDarkButton(cancel); Theme.StyleDarkButton(reset);
        foreach (var button in new[] { save, cancel, reset }) Theme.RoundControl(button);
        footer.Controls.AddRange([save, cancel, reset]); AcceptButton = save; CancelButton = cancel;
        reset.Click += (_, _) => { foreach (var choice in choices) choice.Check.Checked = false; };
        save.Click += (_, _) =>
        {
            var result = new CharacterMovementSettings();
            foreach (var choice in choices) choice.Set(result, choice.Check.Checked ? (float)choice.Value.Value / choice.Scale : null);
            if (CharacterMovementService.ValidationError(result) is { } error) { Dialog.Warn(this, "Check attribute values", error); return; }
            Result = CharacterMovementService.Enabled(result) ? result : null;
            DialogResult = DialogResult.OK; Close();
        };
        UpdateSummary(); Shown += (_, _) => Theme.UseDarkTitleBar(this);
    }
}
