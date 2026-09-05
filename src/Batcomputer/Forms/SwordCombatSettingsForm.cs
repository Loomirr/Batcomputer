namespace Batcomputer;

/// <summary>Combat-only editor. Held-item appearance/visibility lives in HeldItemsForm.</summary>
public sealed class SwordCombatSettingsForm : AdaptiveForm
{
    public SwordCombatSettings Result { get; private set; }
    public SwordCombatSettingsForm(SwordCombatSettings settings, string styleId = "player-sword")
    {
        var label = PlayerMeleeAdapterService.Label(styleId);
        var adapted = PlayerMeleeAdapterService.IsSequenceAdapter(styleId);
        Result = settings.Clone(); Text = "Batcomputer — " + label + " combat";
        StartPosition = FormStartPosition.CenterParent; ClientSize = new(960, 670); MinimumSize = new(780, 560);
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new(16) };
        root.RowStyles.Add(new(SizeType.Absolute, 82)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 64)); Controls.Add(root);
        root.Controls.Add(new Label { Text = label.ToUpperInvariant() + " COMBAT\n\nAttacks and timing only · choose the weapon separately in Abilities → Held items", Dock = DockStyle.Fill, ForeColor = Theme.Abilities });
        var tabs = new SegmentedTabs { Dock = DockStyle.Fill, Name = "SwordSettingsTabs" }; root.Controls.Add(tabs, 0, 1);
        TableLayoutPanel Page(string title) { var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(14), BackColor = Theme.CardBg }; var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 }; table.ColumnStyles.Add(new(SizeType.Absolute, 180)); table.ColumnStyles.Add(new(SizeType.Percent, 100)); scroll.Controls.Add(table); tabs.AddTab(title, scroll); return table; }
        var fields = Page("Behavior");
        void Add(string label, Control c, int height = 48) { var row = fields.RowCount++; fields.RowStyles.Add(new(SizeType.Absolute, height)); fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft }, 0, row); c.Dock = DockStyle.Fill; c.Margin = new(4, 5, 4, 5); fields.Controls.Add(c, 1, row); }
        var speed = new NumericUpDown { Minimum = .5m, Maximum = 3m, DecimalPlaces = 2, Increment = .1m, Value = float.IsFinite(settings.AttackSpeed) ? (decimal)Math.Clamp(settings.AttackSpeed, .5f, 3f) : 1.5m, BackColor = Theme.Slate, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.FixedSingle };
        Add("Attack speed", speed);
        var target = new CheckBox { Text = "Require a nearby combat target", Checked = settings.RequiresCombatTarget, AutoSize = true }; Add("Targeting", target);
        var hitStatus = new ThemedDropDown(); hitStatus.Items.AddRange(MeleeStatusEffectService.Presets.Select(p => (object)p.Label).ToArray());
        hitStatus.SelectedIndex = Array.FindIndex(MeleeStatusEffectService.Presets, p => p.Id == (settings.HitStatus?.PresetId ?? "none")); Add("On-hit status", hitStatus);
        var duration = new NumericUpDown { Minimum = .25m, Maximum = 10m, DecimalPlaces = 2, Increment = .25m, Value = Math.Clamp((decimal)(settings.HitStatus?.DurationSeconds ?? 2), .25m, 10m), BackColor = Theme.Slate, ForeColor = Theme.OnDark };
        Add("Status duration (sec)", duration);
        Add("Status limits", new Label { Text = "Experimental · successful melee hits only, goon targets only. Native damage / target checks remain. Stun interruption and smoke distraction use native AI reactions; bosses, players and arbitrary enemies are not guaranteed. This does not electrify the weapon or modify gadget abilities.\n\nCosmetic props alone cannot inflict a status. Repeated hits may renew the effect. Test on a duplicate suit.", ForeColor = Theme.OnDarkMuted }, 170);
        Add("Player behavior", new Label { Text = "1.5× is the tested timing. Leave targeting unchecked to preserve native player rules, including attacks in empty space.\n\nThis style does not add a held item. Add a right-hand melee item that is visible during attacks. Counters, takedowns and gadgets remain player defaults." +
            (styleId == PlayerMeleeAdapterService.Baton ? "\n\nBaton uses one slam variation. Electrical visuals and stun/shockwave abilities are not included." : ""), ForeColor = Theme.OnDarkMuted }, 228);
        fields = Page("Attack sources"); var defaults = PlayerMeleeAdapterService.Defaults(styleId);
        var montages = Enumerable.Range(0, 4).Select(i => { var box = new TextBox { Text = settings.AttackMontages?.ElementAtOrDefault(i) ?? defaults.AttackMontages[i] }; Theme.StyleDarkInput(box); return box; }).ToArray();
        if (adapted) {
            var attacks = PlayerMeleeAdapterService.Attacks(styleId);
            for (int i = 0; i < attacks.Count; i++) {
                var attack = attacks[i];
                Add($"Verified attack {i + 1}", new Label { Text = attack.Sequence + $"\nSource range: {attack.Start:0.###}–{attack.End:0.###}s · contact: {attack.Impact - attack.Start:0.###}s into attack", ForeColor = Theme.OnDarkMuted }, 110);
            }
            Add("Adaptation", new Label { Text = "These source clips use verified player hit, chain and recovery events. Native enemy AI/area damage/status events are not imported.\n\nSpeed and targeting remain editable. Arbitrary source replacement is unavailable for these adapters until its contact timing can be validated. Choose the held-item template separately; selecting a model alone does not select these attacks.", ForeColor = Theme.OnDarkMuted }, 186);
        } else {
            for (int i = 0; i < 4; i++) Add($"Attack montage {i + 1}", montages[i]);
            Add("Compatibility", new Label { Text = "Advanced · Requires compatible LEGOfig montages with melee metadata and hit events. Variations are assigned across the player combat graph, not a fixed four-click sequence.\n\nFor the tested bat/baton attacks, choose their player-adapter fighting style instead of pasting raw enemy montages here.", ForeColor = Theme.OnDarkMuted }, 164);
        }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new(0, 12, 0, 0) }; root.Controls.Add(footer, 0, 2);
        var save = new Button { Text = "Use combat settings", Width = 174, Height = 36 }; var cancel = new Button { Text = "Cancel", Width = 100, Height = 36, DialogResult = DialogResult.Cancel }; var reset = new Button { Text = "Restore defaults", Width = 146, Height = 36 };
        Theme.StyleGoldButton(save); Theme.StyleDarkButton(cancel); Theme.StyleDarkButton(reset); footer.Controls.AddRange([save, cancel, reset]); AcceptButton = save; CancelButton = cancel;
        reset.Click += (_, _) => { speed.Value = 1.5m; target.Checked = false; hitStatus.SelectedIndex = 0; duration.Value = 2; for (int i = 0; i < 4; i++) montages[i].Text = defaults.AttackMontages[i]; };
        save.Click += (_, _) => { var candidate = settings.Clone(); candidate.AttackSpeed = (float)speed.Value; candidate.RequiresCombatTarget = target.Checked; candidate.HitStatus = new() { PresetId = hitStatus.SelectedIndex >= 0 ? MeleeStatusEffectService.Presets[hitStatus.SelectedIndex].Id : "", DurationSeconds = (float)duration.Value }; candidate.AttackMontages = adapted ? defaults.AttackMontages.ToList() : montages.Select(b => b.Text.Trim()).ToList(); var errors = PlayerMeleeAdapterService.Validate(new() { FightingStyleId = styleId, SwordCombat = candidate, HeldItems = [] }); if (errors.Count > 0) { Dialog.Info(this, "Check combat settings", string.Join("\n", errors)); return; } Result = candidate; DialogResult = DialogResult.OK; Close(); };
        FormClosed += (_, _) => { if (adapted) foreach (var box in montages) box.Dispose(); };
    }
}
