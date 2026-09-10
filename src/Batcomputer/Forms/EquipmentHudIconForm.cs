namespace Batcomputer;

/// <summary>Edits a private icon recipe; only the workshop's Save applies it to equipment.</summary>
internal sealed class EquipmentHudIconForm : AdaptiveForm
{
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _glow = Number(4, 2, .1m);
    private readonly NumericUpDown _fillOpacity = Number(100, 0, 5);
    private readonly NumericUpDown _outlineOpacity = Number(100, 0, 5);
    private readonly NumericUpDown _outlineWidth = Number(1, 2, .05m);
    private readonly NumericUpDown _grain = Number(1, 2, .05m);
    private readonly NumericUpDown _sharpness = Number(32, 1, 1, 1);
    private readonly Button _fill = new(), _outline = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000 };
    internal EquipmentHudIconRecipe? Result { get; private set; }

    internal EquipmentHudIconForm(EquipmentHudIconRecipe? existing, IReadOnlyList<EquipmentUiTextureCatalog.Entry> icons, string suggestedPath)
    {
        Text = "Batcomputer — HUD icon material"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(740, 526); MinimumSize = new Size(660, 568); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 46)); Controls.Add(root);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        root.Controls.Add(scroll, 0, 0);
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        content.ColumnStyles.Add(new(SizeType.Percent, 100)); scroll.Controls.Add(content);

        var heading = new Panel { Dock = DockStyle.Top, Height = 65, Margin = Padding.Empty };
        heading.Controls.Add(new Label { Text = "HUD icon material", Font = Theme.Title, ForeColor = Theme.Equipment, AutoSize = true, Location = Point.Empty });
        heading.Controls.Add(new Label { Text = "HUD + character menu · shared across equipment modes", Font = Theme.Body, ForeColor = Theme.OnDarkMuted, AutoSize = true, Location = new Point(0, 33) });
        var help = Action("Help", false); help.Width = 64;
        var helpHost = new Panel { Dock = DockStyle.Right, Width = 68, Padding = new Padding(0, 0, 0, 31) };
        helpHost.Controls.Add(help); heading.Controls.Add(helpHost); content.Controls.Add(heading);
        help.Click += (_, _) => Dialog.Info(this, "Equipment icon tips",
            EquipmentHudIconService.ArtworkHelp + "\n\nImport as Equipment SDF icon (white + green PNG), then choose that cook here. Black outline pixels in older artwork still count towards the silhouette. BCA is only for parts labelled Suggested: BCA; it preserves the painted image.\n\n" +
            "Green marks the accent area, not its final colour. Glow strength controls the accent; the native shader controls its colour. Fill and outline colours are separate. Width and sharpness use shader units, not pixels.\n\n" +
            "The material overrides individual HUD edits across equipment modes, but leaves gameplay and upgrade-tree icons alone. Remove material restores those saved individual edits. Save equipment, then Build mod.");

        var source = Card(104); content.Controls.Add(source);
        var sourceHeader = new Panel { Dock = DockStyle.Top, Height = 30 };
        sourceHeader.Controls.Add(Label("SDF ICON", Theme.Eyebrow, Theme.Equipment));
        var manual = new CheckBox { Text = "Package path", AutoSize = true, Dock = DockStyle.Right, ForeColor = Theme.OnDarkMuted };
        sourceHeader.Controls.Add(manual);
        var input = new Panel { Dock = DockStyle.Top, Height = 36 };
        var picker = new ThemedDropDown { Dock = DockStyle.Fill, AccessibleName = "Cooked SDF icon" };
        picker.Items.Add("Choose a cooked SDF icon…");
        picker.Items.AddRange(icons.Where(i => i.Profile == "SDF").Cast<object>().ToArray()); picker.SelectedIndex = 0;
        _path.Text = existing?.SdfPackage ?? suggestedPath;
        _path.PlaceholderText = "/Game/Mods/…/Textures/T_YourIcon_SDF";
        _path.BackColor = Theme.WindowBg; _path.ForeColor = Theme.OnDark; _path.AccessibleName = "SDF package path";
        var match = picker.Items.OfType<EquipmentUiTextureCatalog.Entry>().FirstOrDefault(i => i.Package.Equals(_path.Text, StringComparison.OrdinalIgnoreCase));
        if (match is not null) picker.SelectedItem = match;
        picker.SelectedIndexChanged += (_, _) => { if (picker.SelectedItem is EquipmentUiTextureCatalog.Entry entry) _path.Text = entry.Package; };
        input.Controls.Add(picker); input.Controls.Add(_path);
        void InputMode() { _path.Visible = manual.Checked; picker.Visible = !manual.Checked; }
        manual.CheckedChanged += (_, _) => InputMode();
        manual.Checked = match is null && !string.IsNullOrWhiteSpace(_path.Text); InputMode();
        source.Controls.Add(input); source.Controls.Add(sourceHeader);
        _tips.SetToolTip(picker, "Import a white silhouette with optional green accents. No painted outline needed.");

        var appearance = Card(234); content.Controls.Add(appearance);
        var appearanceHeader = new Panel { Dock = DockStyle.Top, Height = 34 };
        appearanceHeader.Controls.Add(Label("APPEARANCE", Theme.Eyebrow, Theme.Equipment));
        var defaults = Action("Native defaults", false); defaults.Width = 126; defaults.Dock = DockStyle.Right;
        appearanceHeader.Controls.Add(defaults); defaults.Click += (_, _) => LoadAppearance(new());
        _tips.SetToolTip(defaults, "Reset appearance only. Keeps the selected icon.");
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Percent, 50)); columns.ColumnStyles.Add(new(SizeType.Percent, 50));
        var left = Fields(); var right = Fields(); left.Margin = new Padding(0, 0, 12, 0); right.Margin = new Padding(12, 0, 0, 0);
        columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0);
        Add(left, "Fill colour", _fill, "Colour of the main silhouette. Accent colour stays controlled by the native shader.");
        Add(left, "Fill opacity %", _fillOpacity, "Opacity of the main fill. Native default: 100%.");
        Add(left, "Glow strength", _glow, "Strength of the green-marked accent. 0 disables it; native default: 0.5.");
        Add(left, "Texture grain", _grain, "Native noise amount. 0 for no grain; native default: 0.5.");
        Add(right, "Outline colour", _outline, "The material draws this border around the SDF silhouette.");
        Add(right, "Outline opacity %", _outlineOpacity, "Opacity of the material's outline. Native default: 80%.");
        Add(right, "Outline width", _outlineWidth, "Border thickness in shader units, not pixels. Native default: 0.2.");
        Add(right, "Edge sharpness", _sharpness, "Native edge sharpness control. Default: 8. Test appearance changes in-game.");
        appearance.Controls.Add(columns); appearance.Controls.Add(appearanceHeader);
        foreach (var button in new[] { _fill, _outline })
        {
            button.Click += (_, _) =>
            {
                using var dialog = new ColorDialog { Color = button.BackColor, FullOpen = true };
                if (dialog.ShowDialog(this) == DialogResult.OK) SetSwatch(button, dialog.Color);
            };
        }
        LoadAppearance(existing ?? new());

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new(SizeType.Absolute, 142)); footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new(SizeType.Absolute, 88)); footer.ColumnStyles.Add(new(SizeType.Absolute, 132)); root.Controls.Add(footer, 0, 1);
        var remove = Action("Remove material", false); remove.Enabled = existing is not null;
        var cancel = Action("Cancel", false); cancel.DialogResult = DialogResult.Cancel;
        var save = Action("Use material", true);
        footer.Controls.Add(remove, 0, 0); footer.Controls.Add(cancel, 2, 0); footer.Controls.Add(save, 3, 0);
        CancelButton = cancel; AcceptButton = save;
        _tips.SetToolTip(remove, "Restore saved individual HUD texture/material edits.");
        remove.Click += (_, _) => { Result = null; DialogResult = DialogResult.OK; Close(); };
        save.Click += (_, _) =>
        {
            var recipe = new EquipmentHudIconRecipe {
                SdfPackage = UnrealPathUtil.NormalizePackagePath(manual.Checked ? _path.Text : (picker.SelectedItem as EquipmentUiTextureCatalog.Entry)?.Package ?? ""), GlowPower = (float)_glow.Value,
                FillColor = _fill.Text, FillOpacity = (float)_fillOpacity.Value / 100,
                OutlineColor = _outline.Text, OutlineOpacity = (float)_outlineOpacity.Value / 100,
                OutlineWidth = (float)_outlineWidth.Value, Grain = (float)_grain.Value, Sharpness = (float)_sharpness.Value
            };
            try
            {
                EquipmentHudIconService.Validate(recipe);
                if (icons.Any(i => i.Package.Equals(recipe.SdfPackage, StringComparison.OrdinalIgnoreCase) && i.Profile == "BCA"))
                    throw new InvalidDataException("Choose this icon's SDF cook, not its BCA output.");
                Result = recipe; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { Dialog.Info(this, "Check the icon settings", ex.Message); }
        };
    }

    private static Label Label(string text, Font font, Color color) => new() { Text = text, Font = font, ForeColor = color, AutoSize = true, Location = new Point(0, 5) };
    private static RoundedPanel Card(int height) => new() { Dock = DockStyle.Top, Height = height, BackColor = Theme.CardBg, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 12) };
    private static Button Action(string text, bool primary)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill };
        if (primary) Theme.StyleGoldButton(button); else Theme.StyleDarkButton(button);
        return button;
    }
    private static NumericUpDown Number(decimal max, int decimals, decimal increment, decimal min = 0) => new() {
        Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = increment, Dock = DockStyle.Fill,
        BackColor = Theme.WindowBg, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right
    };
    private static TableLayoutPanel Fields()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100)); panel.ColumnStyles.Add(new(SizeType.Absolute, 98));
        for (var i = 0; i < 4; i++) panel.RowStyles.Add(new(SizeType.Percent, 25));
        return panel;
    }
    private void Add(TableLayoutPanel panel, string label, Control editor, string tip)
    {
        var row = panel.Controls.Count / 2;
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        editor.Dock = DockStyle.Fill; editor.Margin = new Padding(0, 5, 0, 5); editor.AccessibleName = label;
        if (editor is Button button) Theme.StyleDarkButton(button);
        panel.Controls.Add(editor, 1, row); _tips.SetToolTip(editor, tip);
    }
    private void LoadAppearance(EquipmentHudIconRecipe recipe)
    {
        static void Set(NumericUpDown control, float value, float fallback, float scale = 1) =>
            control.Value = Math.Clamp((decimal)Math.Clamp(float.IsFinite(value) ? value : fallback, (float)control.Minimum / scale, (float)control.Maximum / scale) * (decimal)scale, control.Minimum, control.Maximum);
        Set(_glow, recipe.GlowPower, .5f); Set(_fillOpacity, recipe.FillOpacity, 1, 100); Set(_outlineOpacity, recipe.OutlineOpacity, .8f, 100);
        Set(_outlineWidth, recipe.OutlineWidth, .2f); Set(_grain, recipe.Grain, .5f); Set(_sharpness, recipe.Sharpness, 8);
        static Color ColorOr(string hex, Color fallback) { try { return EquipmentHudIconService.ReadColor(hex); } catch (InvalidDataException) { return fallback; } }
        SetSwatch(_fill, ColorOr(recipe.FillColor, Color.White)); SetSwatch(_outline, ColorOr(recipe.OutlineColor, Color.Black));
    }
    private static void SetSwatch(Button button, Color color)
    {
        button.BackColor = color; button.ForeColor = color.GetBrightness() > .5f ? Color.Black : Color.White;
        button.FlatAppearance.MouseOverBackColor = color; button.FlatAppearance.MouseDownBackColor = color;
        button.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
    protected override void Dispose(bool disposing) { if (disposing) _tips.Dispose(); base.Dispose(disposing); }
}
