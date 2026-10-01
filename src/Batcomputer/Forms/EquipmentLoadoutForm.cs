namespace Batcomputer;

/// <summary>Declarative equipment removal; original donor indexes never shift in the recipe.</summary>
internal sealed class EquipmentLoadoutForm : AdaptiveForm
{
    private readonly List<string> _donor;
    private readonly List<EquipmentSlotChange> _working;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = false, HideSelection = false, Name = "equipment-loadout-slots" };
    private readonly Button _remove = new() { Text = "Remove equipment", AutoSize = true, Name = "remove-equipment" };
    private readonly Button _restore = new() { Text = "Restore inherited", AutoSize = true, Name = "restore-equipment" };
    internal List<EquipmentSlotChange> Result => _working;

    internal EquipmentLoadoutForm(string character, List<string> donor, IEnumerable<EquipmentSlotChange> changes)
    {
        _donor = donor;
        _working = changes.Select(c => new EquipmentSlotChange { Slot = c.Slot, Gadget = c.Gadget,
            Remove = c.Remove, Custom = c.Custom?.Clone() }).ToList();
        Text = "Batcomputer — Equipment loadout"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(740, 470); MinimumSize = new Size(620, 410);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body; AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18) };
        root.RowStyles.Add(new(SizeType.Absolute, 42)); root.RowStyles.Add(new(SizeType.Absolute, 84));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 56));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = character + " · Equipment loadout", Dock = DockStyle.Fill,
            Font = Theme.Title, ForeColor = Theme.Equipment }, 0, 0);
        root.Controls.Add(new Label { Text = "Remove a gadget from this character/suit's usable loadout and equipment menu.\n" +
            "Held props and melee combat stay separate. Restore inherited brings the donor gadget back.\n" +
            "Changes take effect after Build mod and installation; test equipment switching in-game.", Dock = DockStyle.Fill }, 0, 1);
        _list.BackColor = Theme.CardBg; _list.ForeColor = Theme.OnDark; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Donor slot", 95); _list.Columns.Add("Inherited equipment", 225); _list.Columns.Add("Next build", 300);
        _list.ClientSizeChanged += (_, _) => _list.Columns[2].Width = Math.Max(160, _list.ClientSize.Width - 330);
        root.Controls.Add(_list, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
        Theme.StyleDarkButton(_remove); Theme.StyleDarkButton(_restore); actions.Controls.AddRange([_remove, _restore]);
        root.Controls.Add(actions, 0, 3);
        var save = new Button { Text = "Save loadout", Width = 145, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        Theme.StyleGoldButton(save); Theme.StyleDarkButton(cancel);
        Controls.Add(DialogActionFooter.Create(save, cancel)); AcceptButton = save; CancelButton = cancel;
        _list.SelectedIndexChanged += (_, _) => RefreshButtons();
        Shown += (_, _) => RefreshButtons();
        _remove.Click += (_, _) => {
            if (_list.SelectedItems.Count == 0) return;
            var slot = (int)_list.SelectedItems[0].Tag!;
            _working.RemoveAll(c => c.Slot == slot);
            _working.Add(new() { Slot = slot, Gadget = _donor[slot], Remove = true });
            RefreshRows(slot);
        };
        _restore.Click += (_, _) => {
            if (_list.SelectedItems.Count == 0) return;
            var slot = (int)_list.SelectedItems[0].Tag!;
            _working.RemoveAll(c => c.Slot == slot); RefreshRows(slot);
        };
        RefreshRows(0);
    }

    private void RefreshRows(int selected)
    {
        _list.Items.Clear();
        for (var slot = 0; slot < _donor.Count; slot++)
        {
            var edit = _working.FirstOrDefault(c => c.Slot == slot);
            var row = new ListViewItem((slot + 1).ToString()) { Tag = slot };
            row.SubItems.Add(string.IsNullOrWhiteSpace(_donor[slot]) ? "Empty" : _donor[slot]);
            row.SubItems.Add(edit?.Remove == true ? "Removed" : edit is null ? "Inherited" : edit.Custom?.Name ?? edit.Gadget);
            if (edit?.Remove == true) row.ForeColor = Theme.Warn;
            _list.Items.Add(row); row.Selected = slot == selected;
        }
        RefreshButtons();
    }
    private void RefreshButtons()
    {
        var slot = _list.SelectedItems.Count == 0 ? -1 : (int)_list.SelectedItems[0].Tag!;
        var edit = _working.FirstOrDefault(c => c.Slot == slot);
        _remove.Enabled = slot >= 0 && edit?.Remove != true &&
            (edit is not null || !string.IsNullOrWhiteSpace(_donor[slot]));
        _restore.Enabled = slot >= 0 && edit is not null;
    }
}
