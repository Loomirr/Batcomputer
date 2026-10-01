namespace Batcomputer;

/// <summary>Private-copy browser of active native items and optional extra props.</summary>
public sealed partial class HeldItemsForm : AdaptiveForm
{
    public List<HeldItemSettings> Result { get; private set; }
    public List<NativeHeldItemEdit> NativeResult { get; private set; } = [];
    public List<AnimationSpawnedItemEdit> AnimationResult { get; private set; } = [];
    internal bool ToggleEnabled { get; private set; }
    public HeldItemsForm(IEnumerable<HeldItemSettings> items) : this(items, [], new([], [])) { }
    internal HeldItemsForm(IEnumerable<HeldItemSettings> items, IEnumerable<NativeHeldItemEdit> edits, NativeHeldItemService.Inspection native,
        IEnumerable<AnimationSpawnedItemEdit>? animationEdits = null, AnimationSpawnedItemService.Inspection? animationInspection = null,
        Func<AnimationSpawnedItemService.Inspection>? animationLoader = null, HeldItemToggleProfile? toggle = null)
    {
        Result = items.Select(i => i.Clone()).ToList(); NativeResult = edits.Select(i => i.Clone()).ToList();
        AnimationResult = (animationEdits ?? []).Select(i => i.Clone()).ToList();
        ToggleEnabled = toggle?.Enabled == true;
        Text = "Batcomputer — Held items"; StartPosition = FormStartPosition.CenterParent; ClientSize = new(1120, 760); MinimumSize = new(940, 630);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body; Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 88)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(root);
        root.Controls.Add(ItemWorkshopUi.Header("Held items", "Existing ability items: replace or hide their visuals. Extra props: add a separate model to one or both hands."));
        var pages = new ItemInspectorPages(); root.Controls.Add(pages, 0, 1);
        pages.Add($"Native items · {native.Items.Count}", NativePage(native)); pages.Add("Extra props", ExtraPage());
        if (toggle is not null)
        {
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new(18), AutoScroll = true };
            var enabled = new CheckBox { Text = "Use saved held-item toggle instead of Focus", AutoSize = true, Checked = ToggleEnabled, Name = "held-item-toggle-enabled" };
            enabled.CheckedChanged += (_, _) => ToggleEnabled = enabled.Checked;
            page.Controls.Add(enabled);
            page.Controls.Add(new Label { AutoSize = true, MaximumSize = new(720,0), Text = "Uses the native Focus binding, including controller/keyboard remapping. Starts extended; attacks auto-extend. Unarmed idle and forward movement, gestures and private sounds are rebuilt from this project's saved bundle.\n\nTurning this off restores normal Focus on the next build. The mesh still snaps visible/hidden; this bundle does not animate the blades.\n\nEdit the prop's model, materials, hand placement and glide hiding under Extra props. Save this screen, apply Abilities, then rebuild." });
            pages.Add("Item toggle",page);
        }
        if (animationInspection is not null) pages.Add($"Animation items · {animationInspection.Items.Count}", AnimationPage(animationInspection));
        else if (animationLoader is not null) pages.Add("Animation items", LazyAnimationPage(animationLoader));
        var use = ItemWorkshopUi.Button("Use held items", true); var cancel = ItemWorkshopUi.Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        root.Controls.Add(ItemWorkshopUi.Footer(new Label { Text = "Private changes · save the Ability editor and rebuild after applying.", ForeColor = Theme.OnDarkMuted }, cancel, use), 0, 2); AcceptButton = use; CancelButton = cancel;
        use.Click += (_, _) => { var errors = HeldItemService.Validate(Result).Concat(NativeHeldItemService.Validate(NativeResult)).Concat(AnimationSpawnedItemService.Validate(new() { HeldItems = Result, AnimationSpawnedItems = AnimationResult })).ToList(); if (errors.Count > 0) { Dialog.Warn(this, "Check held items", string.Join("\n", errors)); return; } DialogResult = DialogResult.OK; Close(); };
        Shown += (_, _) => Theme.UseDarkTitleBar(this);
    }
    private Control NativePage(NativeHeldItemService.Inspection inspection)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new(SizeType.Absolute, 44)); root.RowStyles.Add(new(SizeType.Percent, 100));
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Search item, attachment slot or ability…" }; Theme.StyleDarkInput(search); root.Controls.Add(search);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1000, 480), SplitterDistance = 630, SplitterWidth = 8, Panel1MinSize = 330, Panel2MinSize = 310 };
        root.Controls.Add(split, 0, 1);
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        list.Columns.Add("Item", 265); list.Columns.Add("Attached to", 130); list.Columns.Add("Appearance", 160); Theme.StyleListView(list); split.Panel1.Controls.Add(list);
        ItemWorkshopUi.FitColumns(list, .47f, .23f, .30f);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new(12), BackColor = Theme.PanelBg };
        details.RowStyles.Add(new(SizeType.Absolute, 40)); details.RowStyles.Add(new(SizeType.Absolute, 62)); details.RowStyles.Add(new(SizeType.Percent, 100));
        details.RowStyles.Add(new(SizeType.AutoSize)); details.RowStyles.Add(new(SizeType.AutoSize)); details.RowStyles.Add(new(SizeType.Absolute, 0)); split.Panel2.Controls.Add(details);
        var title = new Label { Dock = DockStyle.Fill, Font = Theme.Heading, ForeColor = Theme.OnDark, AutoEllipsis = true };
        var detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }; Theme.StyleDarkInput(detail);
        var appearance = new Label { Dock = DockStyle.Fill, ForeColor = Theme.OnDark, AutoEllipsis = true };
        var explanation = new Label { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted };
        details.Controls.Add(title); details.Controls.Add(appearance, 0, 1); details.Controls.Add(explanation, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var edit = ItemWorkshopUi.Button("Edit appearance…", true); var hide = ItemWorkshopUi.Button("Hide visuals"); var restore = ItemWorkshopUi.Button("Reset changes"); var warnings = ItemWorkshopUi.Button($"Scan report · {inspection.Warnings.Count}");
        actions.Controls.AddRange([edit, hide, restore, warnings]); details.Controls.Add(actions, 0, 3);
        var advanced = new CheckBox { Text = "Technical details", AutoSize = true }; details.Controls.Add(advanced, 0, 4); details.Controls.Add(detail, 0, 5); detail.Visible = false;
        advanced.CheckedChanged += (_, _) => { detail.Visible = advanced.Checked; details.RowStyles[5].Height = advanced.Checked ? 180 : 0; };
        var bindings = inspection.Items.ToList();
        foreach (var stale in NativeResult.Where(e => !bindings.Any(b => b.Identity.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase))))
            bindings.Add(new(stale.Clone(), "Not active", "", false, "Saved edit is not present in this loadout. Restore it or reselect its original abilities before building."));
        NativeHeldItemService.Binding? Selected() => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as NativeHeldItemService.Binding : null;
        NativeHeldItemEdit? Current(NativeHeldItemService.Binding b) => NativeResult.FirstOrDefault(e => e.Key.Equals(b.Identity.Key, StringComparison.OrdinalIgnoreCase));
        void Selection()
        {
            var b = Selected(); edit.Enabled = hide.Enabled = b?.Editable == true; restore.Enabled = b is not null && Current(b) is not null;
            title.Text = b is null ? "Choose an item" : Friendly(b.Identity.ActorPackage);
            var current = b is null ? null : Current(b); hide.Text = current?.Hide == true ? "Show visuals" : "Hide visuals";
            appearance.Text = b is null ? "No item selected" : $"{AttachmentLabel(b.Slot)} · {(current?.Hide == true ? "Visuals hidden" : current is not null ? "Custom appearance" : "Original appearance")}\nModel: {current?.CustomModel?.SourceName ?? UnrealPathUtil.AssetName(current?.ReplacementMeshPackage is { Length: > 0 } replacement ? replacement : b.Identity.OriginalMeshPackage)}";
            explanation.Text = b is null ? "Select an ability item to change its model and materials. If nothing is listed, check the scan report." :
                b.Editable ? "Edit the model, materials and alignment in one 3D workspace. Attacks, draw/hide timing and sockets remain native.\n\nLeft and right hands are separate ability items. Edit both for a matched pair.\n\nUse Extra props to add an independent model instead of changing this item." : "This item's visuals are inherited or specialized. It is listed for inspection, but editing it is not safely supported yet.\n\n" + b.Note;
            detail.Text = b is null ? "Select a native item to customize its model and materials.\r\n\r\nNo items? Check Scan details. Dynamic or inherited visuals are not guessed." :
                $"Attachment: {b.Slot}\r\n\r\n{b.Note}\r\n\r\n{b.Timing.Replace("\n", "\r\n")}\r\n\r\nTechnical details\r\nActor: {b.Identity.ActorPackage}\r\nMesh: {b.Identity.OriginalMeshPackage}\r\nComponent: {b.Identity.ExportName}\r\nAbility: {b.Identity.AbilityPackage}\r\nItem index: {b.Identity.ManagedItemIndex}";
        }
        void Refresh(string? key = null)
        {
            list.BeginUpdate(); list.Items.Clear();
            foreach (var b in bindings.Where(b => search.Text.Trim().Length == 0 || (b.Identity.ActorPackage + b.Identity.AbilityPackage + b.Slot).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var e = Current(b); var row = new ListViewItem([Friendly(b.Identity.ActorPackage), AttachmentLabel(b.Slot), e?.Hide == true ? "Hidden" : e is not null ? "Customized" : b.Editable ? "Original" : "Inspect only"]) { Tag = b };
                list.Items.Add(row); if (b.Identity.Key == key) row.Selected = true;
            }
            list.EndUpdate(); if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true; Selection();
        }
        void Edit()
        {
            var b = Selected(); if (b?.Editable != true) return;
            using var form = new NativeHeldItemSettingsForm(b, Current(b)); if (form.ShowDialog(this) != DialogResult.OK) return;
            NativeResult.RemoveAll(e => e.Key.Equals(b.Identity.Key, StringComparison.OrdinalIgnoreCase)); NativeResult.Add(form.Result); Refresh(b.Identity.Key);
        }
        edit.Click += (_, _) => Edit(); list.DoubleClick += (_, _) => Edit(); list.SelectedIndexChanged += (_, _) => Selection(); search.TextChanged += (_, _) => Refresh();
        hide.Click += (_, _) => { var b = Selected(); if (b?.Editable != true) return; var e = Current(b)?.Clone() ?? b.Identity.Clone(); e.Hide = !e.Hide; NativeResult.RemoveAll(x => x.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase)); NativeResult.Add(e); Refresh(e.Key); };
        restore.Click += (_, _) => { var b = Selected(); if (b is null) return; NativeResult.RemoveAll(e => e.Key.Equals(b.Identity.Key, StringComparison.OrdinalIgnoreCase)); Refresh(b.Identity.Key); };
        warnings.Click += (_, _) => Dialog.Info(this, "Native held-item scan", inspection.Warnings.Count == 0 ? "Active abilities were inspected. Direct managed items are listed; empty-hand requests are excluded. Specialized/inherited visuals are inspect-only. Temporary actors spawned by gameplay are not exhaustively covered." : string.Join("\n\n", inspection.Warnings));
        Refresh(); return root;
    }
    private Control ExtraPage()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 62)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 48));
        root.Controls.Add(new Label { Text = "Optional props · right, left, or both hands (one prop per hand)\nThese do not replace native attack items. Native hand-slot priority still applies.", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted });
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        list.Columns.Add("Item", 330); list.Columns.Add("Hand", 130); list.Columns.Add("When visible", 350); Theme.StyleListView(list); root.Controls.Add(list, 0, 1);
        ItemWorkshopUi.FitColumns(list, .4f, .2f, .4f);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill }; var add = ItemWorkshopUi.Button("+ Add prop", true); var edit = ItemWorkshopUi.Button("Edit prop"); var remove = ItemWorkshopUi.Button("Remove prop");
        actions.Controls.AddRange([add, edit, remove]); root.Controls.Add(actions, 0, 2);
        void Refresh(string? id = null) { list.Items.Clear(); foreach (var item in Result) { var row = new ListViewItem([item.Name, HeldItemService.HandLabel(item.Hand), VisibilityLabel(item.Visibility)]) { Tag = item }; list.Items.Add(row); if (id == item.Id) row.Selected = true; } add.Enabled = Result.SelectMany(i => HeldItemService.Hands(i.Hand)).Distinct().Count() < 2; edit.Enabled = remove.Enabled = list.SelectedItems.Count == 1; }
        void Edit(bool create)
        {
            var item = create ? new HeldItemSettings { Hand = Result.Any(i => HeldItemService.Hands(i.Hand).Contains(HeldItemHand.Right)) ? HeldItemHand.Left : HeldItemHand.Right } : list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as HeldItemSettings;
            if (item is null) return; using var form = new HeldItemSettingsForm(item); if (form.ShowDialog(this) != DialogResult.OK) return;
            var candidates = Result.Where(i => i.Id != item.Id).Append(form.Result).ToList(); var errors = HeldItemService.Validate(candidates);
            if (errors.Count > 0) { Dialog.Warn(this, "Check held items", string.Join("\n", errors)); return; } Result = candidates; Refresh(form.Result.Id);
        }
        add.Click += (_, _) => Edit(true); edit.Click += (_, _) => Edit(false); list.DoubleClick += (_, _) => Edit(false);
        remove.Click += (_, _) => { if (list.SelectedItems.Count == 1) { Result.Remove((HeldItemSettings)list.SelectedItems[0].Tag!); Refresh(); } };
        list.SelectedIndexChanged += (_, _) => edit.Enabled = remove.Enabled = list.SelectedItems.Count == 1; Refresh(); return root;
    }
    internal static string AttachmentLabel(string slot) => slot switch { "LAM.RightHand" => "Right hand", "LAM.LeftHand" => "Left hand", "LAM.Head" => "Head", _ => slot.Replace("LAM.", "") };
    internal static string Friendly(string package) => UnrealPathUtil.AssetName(package).Replace("BP_", "").Replace("GA_", "").Replace("_LAMManagedItem", "").Replace('_', ' ').Trim();
    internal static string VisibilityLabel(HeldWeaponVisibility mode) => mode switch { HeldWeaponVisibility.WhileAttacking => "Only while attacking", HeldWeaponVisibility.InCombat => "During combat or attacks", HeldWeaponVisibility.Always => "Always held", HeldWeaponVisibility.OutsideCombat => "Hide during combat / attacks", _ => "Invalid" };
}
