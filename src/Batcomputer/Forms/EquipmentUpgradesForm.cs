namespace Batcomputer;

internal sealed class EquipmentUpgradesForm : AdaptiveForm
{
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    internal List<string>? Result { get; private set; }
    internal List<string> DisabledIds => _choices.Where(pair => !pair.Value.Checked).Select(pair => pair.Key).ToList();
    internal EquipmentUpgradesForm(CustomEquipmentRecipe recipe)
    {
        if (!EquipmentUpgradeService.Supported(recipe)) throw new InvalidDataException("Upgrade choices are currently available for Batarang-based equipment.");
        var disabled = EquipmentUpgradeService.Disabled(recipe);
        Text = "Batcomputer — Equipment upgrades"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(580, 570); MinimumSize = new Size(500, 470); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20) };
        root.RowStyles.Add(new(SizeType.Absolute, 38)); root.RowStyles.Add(new(SizeType.Absolute, 78));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 48)); Controls.Add(root);
        root.Controls.Add(new Label { Text = "Allowed upgrades", Font = Theme.Title, ForeColor = Theme.Equipment, Dock = DockStyle.Fill }, 0, 0);
        root.Controls.Add(new Label { Text = "Checked = available when purchased. Unchecked = excluded.\nPurchases and the global upgrade menu stay unchanged.\nExperimental — test special modes and switching in-game.", Dock = DockStyle.Fill }, 0, 1);
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        root.Controls.Add(list, 0, 2);
        foreach (var option in EquipmentUpgradeService.Options)
        {
            var choice = new CheckBox { Name = "upgrade-" + option.Id, Text = option.Label, Checked = !disabled.Contains(option.Id), AutoSize = true, Margin = new Padding(8, 8, 8, 8) };
            _choices.Add(option.Id, choice); list.Controls.Add(choice);
        }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        root.Controls.Add(footer, 0, 3);
        Button MakeButton(string text) => new() { Text = text, AutoSize = true, Height = 38, Padding = new Padding(12, 4, 12, 4), BackColor = Theme.CardBg, ForeColor = Theme.OnDark, FlatStyle = FlatStyle.Flat };
        var save = MakeButton("Use selection"); var cancel = MakeButton("Cancel"); var all = MakeButton("Allow all");
        save.BackColor = Theme.Gold; save.ForeColor = Color.Black;
        save.Click += (_, _) => { Result = DisabledIds; DialogResult = DialogResult.OK; Close(); };
        cancel.DialogResult = DialogResult.Cancel;
        all.Click += (_, _) => { foreach (var choice in _choices.Values) choice.Checked = true; };
        footer.Controls.Add(save); footer.Controls.Add(cancel); footer.Controls.Add(all); AcceptButton = save; CancelButton = cancel;
    }
}
