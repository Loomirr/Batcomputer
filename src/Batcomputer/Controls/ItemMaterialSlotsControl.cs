namespace Batcomputer;

/// <summary>Friendly slot assignments with exact identities retained for baking.</summary>
internal sealed class ItemMaterialSlotsControl : UserControl
{
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, AutoEllipsis = true };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, ForeColor = Theme.OnDark, AutoEllipsis = true };
    private readonly CheckBox _ownOnly = new() { Name = "OwnMaterialsFilter", Text = "Your materials only", AutoSize = true };
    private readonly Dictionary<int, string> _assignments = [];
    private readonly Dictionary<int, (string Name, string Package)> _slots = [];
    internal event Action? Changed;
    internal IReadOnlyList<NativeHeldMaterialOverride> Overrides => _assignments.OrderBy(p => p.Key).Select(p => new NativeHeldMaterialOverride { Slot = p.Key, Package = p.Value }).ToList();

    internal ItemMaterialSlotsControl(bool allowNative = false)
    {
        Dock = DockStyle.Fill; BackColor = Theme.CardBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new(SizeType.Absolute, 62)); root.RowStyles.Add(new(SizeType.Absolute, 32)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Absolute, 100)); Controls.Add(root);
        root.Controls.Add(new Label { Text = "Materials\nSelect a model surface, then choose its appearance. Changes preview on your model.", ForeColor = Theme.OnDarkMuted, Dock = DockStyle.Fill });
        root.Controls.Add(_summary, 0, 1);
        _list.Columns.Add("Model surface", 130); _list.Columns.Add("Assigned material", 200); Theme.StyleListView(_list); ItemWorkshopUi.FitColumns(_list, .38f, .62f); root.Controls.Add(_list, 0, 2);
        root.Controls.Add(_detail, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        var choose = ItemWorkshopUi.Button("Choose material…", true); var all = ItemWorkshopUi.Button("Apply to all"); var reset = ItemWorkshopUi.Button("Use original");
        actions.Controls.AddRange([choose, all]); if (allowNative) actions.Controls.Add(reset); actions.Controls.Add(_ownOnly); root.Controls.Add(actions, 0, 3);
        void Pick(bool every)
        {
            if (_list.SelectedItems.Count != 1) return; var slot = (int)_list.SelectedItems[0].Tag!;
            using var picker = new ItemMaterialPickerForm(_assignments.GetValueOrDefault(slot) ?? _slots[slot].Package, _ownOnly.Checked);
            if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
            ApplyMaterial(slot, picker.SelectedPackage, every);
        }
        choose.Click += (_, _) => Pick(false); all.Click += (_, _) => Pick(true); _list.DoubleClick += (_, _) => Pick(false);
        reset.Click += (_, _) => { if (_list.SelectedItems.Count != 1) return; var slot = (int)_list.SelectedItems[0].Tag!; _assignments.Remove(slot); RefreshRows(slot); Changed?.Invoke(); };
        _list.SelectedIndexChanged += (_, _) =>
        {
            choose.Enabled = reset.Enabled = _list.SelectedItems.Count == 1; all.Enabled = choose.Enabled && _slots.Count > 1;
            if (_list.SelectedItems.Count == 1) { var s = (int)_list.SelectedItems[0].Tag!; var p = _assignments.GetValueOrDefault(s) ?? _slots[s].Package; _detail.Text = $"Surface {s}: {_slots[s].Name}\n{(p.Length > 0 ? UnrealPathUtil.AssetName(p) : "Original material")}\nPackage: {p}"; }
            else _detail.Text = "Select a surface above. Double-click it to choose a material.";
        };
        choose.Enabled = all.Enabled = reset.Enabled = false;
    }
    internal void SetSlots(IEnumerable<(int Slot, string Name, string Package)> slots, IEnumerable<NativeHeldMaterialOverride> assignments)
    {
        var selected = _list.SelectedItems.Count == 1 ? (int?)_list.SelectedItems[0].Tag! : null;
        _slots.Clear(); _assignments.Clear();
        foreach (var s in slots) _slots[s.Slot] = (s.Name, s.Package);
        foreach (var a in assignments) { _assignments[a.Slot] = a.Package; _slots.TryAdd(a.Slot, ($"Material {a.Slot}", "")); }
        RefreshRows(selected);
    }
    internal void ApplyMaterial(int slot, string package, bool every = false)
    {
        if (!_slots.ContainsKey(slot) || !HeldItemService.ValidPackage(UnrealPathUtil.NormalizePackagePath(package))) throw new InvalidDataException("Choose a valid cooked material for this surface.");
        foreach (var s in every ? _slots.Keys.ToArray() : [slot]) _assignments[s] = package;
        RefreshRows(slot); Changed?.Invoke();
    }
    private void RefreshRows(int? selected = null)
    {
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var s in _slots.OrderBy(s => s.Key))
        {
            var assigned = _assignments.GetValueOrDefault(s.Key);
            var package = assigned ?? s.Value.Package;
            var row = new ListViewItem([string.IsNullOrWhiteSpace(s.Value.Name) ? $"Surface {s.Key}" : s.Value.Name, (package.Length > 0 ? UnrealPathUtil.AssetName(package) : "Original material") + (assigned is null ? " · original" : "")]) { Tag = s.Key };
            _list.Items.Add(row); if (s.Key == selected) row.Selected = true;
        }
        _list.EndUpdate(); if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        _summary.Text = _slots.Count == 1 ? "1 surface · choose one material" : $"{_slots.Count} model surfaces · assignments stay with their slots";
    }
}
