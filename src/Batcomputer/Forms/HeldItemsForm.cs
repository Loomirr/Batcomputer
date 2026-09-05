namespace Batcomputer;

/// <summary>Private-copy held-item editor. Does not mutate fighting styles or save projects.</summary>
public sealed class HeldItemsForm : AdaptiveForm
{
    public List<HeldItemSettings> Result { get; private set; }
    private readonly ListView _items = new();
    public HeldItemsForm(IEnumerable<HeldItemSettings> items)
    {
        Result = items.Select(i => i.Clone()).ToList();
        Text = "Batcomputer — Held items"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(950, 610); MinimumSize = new(760, 500); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute, 100)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 68)); layout.RowStyles.Add(new(SizeType.Absolute, 68)); Controls.Add(layout);
        var header = new Label { Text = "HELD ITEMS\n\nAdd a prop without changing combat. Choose its hand, visibility and appearance here; select attacks separately in Fighting style.", Dock = DockStyle.Fill, ForeColor = Theme.Abilities };
        layout.Controls.Add(header);
        _items.Dock = DockStyle.Fill; _items.View = View.Details; _items.FullRowSelect = true; _items.MultiSelect = false; _items.HideSelection = false;
        _items.BackColor = Theme.CardBg; _items.ForeColor = Theme.OnDark; _items.BorderStyle = BorderStyle.None;
        _items.Columns.Add("Item", 260); _items.Columns.Add("Hand", 90); _items.Columns.Add("Visibility", 240);
        layout.Controls.Add(_items, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 10, 0, 0), WrapContents = true };
        var add = Button("+ Add held item", true); var edit = Button("Edit item"); var remove = Button("Remove item");
        actions.Controls.AddRange([add, edit, remove]); layout.Controls.Add(actions, 0, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new(0, 14, 0, 0), WrapContents = false };
        var use = Button("Use held items", true); var cancel = Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange([use, cancel]); layout.Controls.Add(footer, 0, 3); AcceptButton = use; CancelButton = cancel;
        void RefreshItems(string? id = null) {
            _items.Items.Clear(); foreach (var item in Result) { var row = new ListViewItem([item.Name, item.Hand.ToString(), VisibilityLabel(item.Visibility)]) { Tag = item }; _items.Items.Add(row); if (id == item.Id) row.Selected = true; }
            add.Enabled = Result.Count < 2; edit.Enabled = remove.Enabled = _items.SelectedItems.Count == 1;
        }
        void Edit(bool create) {
            var item = create ? new HeldItemSettings { Hand = Result.Any(i => i.Hand == HeldItemHand.Right) ? HeldItemHand.Left : HeldItemHand.Right } : _items.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as HeldItemSettings;
            if (item is null) return;
            using var dialog = new HeldItemSettingsForm(item);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var candidates = Result.Where(i => i.Id != item.Id).Append(dialog.Result).ToList();
            var errors = HeldItemService.Validate(candidates);
            if (errors.Count > 0) { Dialog.Warn(this, "Check held items", string.Join("\n", errors)); return; }
            Result = candidates; RefreshItems(dialog.Result.Id);
        }
        add.Click += (_, _) => Edit(true); edit.Click += (_, _) => Edit(false); _items.DoubleClick += (_, _) => Edit(false);
        remove.Click += (_, _) => { if (_items.SelectedItems.Count == 1) { Result.Remove((HeldItemSettings)_items.SelectedItems[0].Tag!); RefreshItems(); } };
        _items.SelectedIndexChanged += (_, _) => edit.Enabled = remove.Enabled = _items.SelectedItems.Count == 1;
        use.Click += (_, _) => { var errors = HeldItemService.Validate(Result); if (errors.Count > 0) { Dialog.Warn(this, "Check held items", string.Join("\n", errors)); return; } DialogResult = DialogResult.OK; Close(); };
        RefreshItems();
    }
    private static Button Button(string text, bool primary = false) { var b = new Button { Text = text, AutoSize = true, MinimumSize = new(120, 36), Margin = new(0, 0, 10, 0) }; if (primary) Theme.StyleGoldButton(b); else Theme.StyleDarkButton(b); return b; }
    internal static string VisibilityLabel(HeldWeaponVisibility mode) => mode switch { HeldWeaponVisibility.WhileAttacking => "Only while attacking", HeldWeaponVisibility.InCombat => "During combat or attacks", HeldWeaponVisibility.Always => "Always held", HeldWeaponVisibility.OutsideCombat => "Hide during combat / attacks", _ => "Invalid" };
}

public sealed class HeldItemSettingsForm : AdaptiveForm
{
    public HeldItemSettings Result { get; private set; }
    public HeldItemSettingsForm(HeldItemSettings settings)
    {
        Result = settings.Clone(); Text = "Batcomputer — Held item settings"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(960, 700); MinimumSize = new(780, 590); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), RowCount = 3, ColumnCount = 1 };
        root.RowStyles.Add(new(SizeType.Absolute, 72)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 64)); Controls.Add(root);
        root.Controls.Add(new Label { Text = "HELD ITEM\nAppearance and visibility only · no automatic combat or electrical ability grants", Dock = DockStyle.Fill, ForeColor = Theme.Abilities });
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.CardBg, Padding = new(12) };
        root.Controls.Add(scroll, 0, 1);
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 }; table.ColumnStyles.Add(new(SizeType.Absolute, 172)); table.ColumnStyles.Add(new(SizeType.Percent, 100)); scroll.Controls.Add(table);
        void Add(string label, Control c, int height = 48) { var n = table.RowCount++; table.RowStyles.Add(new(SizeType.Absolute, height)); table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft }, 0, n); c.Dock = DockStyle.Fill; c.Margin = new(5, 5, 5, 5); table.Controls.Add(c, 1, n); }
        TextBox Input(string value) { var b = new TextBox { Text = value }; Theme.StyleDarkInput(b); return b; }
        var name = Input(settings.Name); Add("Item name", name);
        var template = new ThemedDropDown(); template.Items.AddRange(HeldItemService.Templates.Select(t => (object)t.Label).ToArray());
        template.SelectedIndex = Array.FindIndex(HeldItemService.Templates, t => t.Id == settings.TemplateId); Add("Native item example", template);
        var templateInfo = new Label { Text = template.SelectedIndex >= 0 ? HeldItemService.Templates[template.SelectedIndex].Notes : "Select a native example.", ForeColor = Theme.OnDarkMuted };
        Add("Example details", templateInfo, 110);
        var hand = new ThemedDropDown(); hand.Items.AddRange(["Right hand", "Left hand"]); hand.SelectedIndex = (int)settings.Hand; Add("Hand", hand);
        var visibility = new ThemedDropDown(); visibility.Items.AddRange(Enum.GetValues<HeldWeaponVisibility>().Select(v => (object)HeldItemsForm.VisibilityLabel(v)).ToArray()); visibility.SelectedIndex = (int)settings.Visibility; Add("Visibility", visibility);
        var mesh = Input(settings.MeshPackage); Add("Cooked static mesh", mesh);
        var material = Input(settings.MaterialPackage); Add("Material slot 0", material);
        var model = settings.CustomModel?.Clone(); var workshop = new Button { Text = "Open model editor…" }; Theme.StyleGoldButton(workshop); Add("Custom model / alignment", workshop);
        var clear = new Button { Text = model is null ? "Using native mesh" : "Remove custom model: " + model.SourceName, AutoEllipsis = true }; Theme.StyleDarkButton(clear); Add("Model source", clear);
        workshop.Click += (_, _) => { using var editor = new WeaponModelEditorForm(mesh.Text.Trim(), model); if (editor.ShowDialog(this) == DialogResult.OK) { model = editor.Result?.Clone(); clear.Text = model is null ? "Using native mesh" : "Remove custom model: " + model.SourceName; } };
        clear.Click += (_, _) => { model = null; clear.Text = "Using native mesh"; };
        var effects = (settings.Effects ?? []).Select(e => e.Clone()).ToList();
        var effectButton = new Button { Text = $"Edit effects / placement… ({effects.Count})" }; Theme.StyleDarkButton(effectButton); Add("Cosmetic effects", effectButton);
        effectButton.Click += (_, _) => { var draft = settings.Clone(); draft.MeshPackage = mesh.Text.Trim(); draft.CustomModel = model?.Clone(); draft.Effects = effects;
            using var editor = new HeldItemEffectsForm(draft); if (editor.ShowDialog(this) == DialogResult.OK) { effects = editor.Result.Select(e => e.Clone()).ToList(); effectButton.Text = $"Edit effects / placement… ({effects.Count})"; } };
        template.SelectedIndexChanged += (_, _) => { if (template.SelectedIndex < 0) return; var t = HeldItemService.Templates[template.SelectedIndex]; mesh.Text = t.Mesh; name.Text = t.Label; templateInfo.Text = t.Notes; material.Clear(); model = null; clear.Text = "Using native mesh"; };
        Add("Compatibility", new Label { Text = "One item per hand. Native slot priority and gadget blocking are retained. Hide-during-combat also hides during empty-space attacks.\n\nMelee examples retain native hitboxes; cosmetic examples have no collision or attack hitbox. Model size does not resize collision. Choose combat separately; props do not grant gadget or stun abilities.", ForeColor = Theme.OnDarkMuted }, 146);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new(0, 12, 0, 0), WrapContents = false };
        root.Controls.Add(footer, 0, 2); var use = new Button { Text = "Use item", Width = 130, Height = 36 }; var cancel = new Button { Text = "Cancel", Width = 105, Height = 36, DialogResult = DialogResult.Cancel }; Theme.StyleGoldButton(use); Theme.StyleDarkButton(cancel); footer.Controls.AddRange([use, cancel]); AcceptButton = use; CancelButton = cancel;
        use.Click += (_, _) => { var item = settings.Clone(); item.Name = name.Text.Trim(); item.TemplateId = template.SelectedIndex >= 0 ? HeldItemService.Templates[template.SelectedIndex].Id : ""; item.Hand = (HeldItemHand)hand.SelectedIndex; item.Visibility = (HeldWeaponVisibility)visibility.SelectedIndex; item.MeshPackage = mesh.Text.Trim(); item.MaterialPackage = material.Text.Trim(); item.CustomModel = model?.Clone(); item.Effects = effects.Select(e => e.Clone()).ToList(); var errors = HeldItemService.Validate([item]); if (errors.Count > 0) { Dialog.Warn(this, "Check held item", string.Join("\n", errors)); return; } Result = item; DialogResult = DialogResult.OK; Close(); };
    }
}
