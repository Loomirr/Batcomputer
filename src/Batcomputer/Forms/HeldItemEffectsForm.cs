namespace Batcomputer;

/// <summary>Private-copy effect placement with an explicitly approximate, offline viewer.</summary>
public sealed class HeldItemEffectsForm : AdaptiveForm
{
    public List<HeldItemEffectSettings> Result { get; private set; }
    private readonly ModelPreviewControl _viewer = new() { Dock = DockStyle.Fill };
    private readonly ListBox _list = new() { Width = 338, Height = 92 };
    private readonly List<NumericUpDown> _numbers = [];
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ThemedDropDown _preset = new() { Width = 338, Height = 38 };
    private string? _folder;
    private bool _binding, _loading;
    public HeldItemEffectsForm(HeldItemSettings item)
    {
        Result = (item.Effects ?? []).Select(e => e.Clone()).ToList();
        Text = "Batcomputer — Item effects"; ClientSize = new(1220, 820); MinimumSize = new(960, 650);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(14), ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new(SizeType.Absolute, 380)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 88)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 64)); Controls.Add(root);
        var header = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Abilities, Text = "ITEM EFFECTS\nPlacement markers + approximate animated previews — not the game's final Niagara rendering.\nEffects follow the item's visibility. Use an attack-only item for attack-only effects. Visuals do not cause damage or statuses." };
        root.Controls.Add(header, 0, 0); root.SetColumnSpan(header, 2);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(12), BackColor = Theme.CardBg };
        root.Controls.Add(panel, 0, 1); root.Controls.Add(_viewer, 1, 1);
        _list.BackColor = Theme.PanelBg; _list.ForeColor = Theme.OnDark; panel.Controls.Add(_list);
        var buttons = new FlowLayoutPanel { Width = 338, Height = 42 };
        var add = new Button { Text = "+ Add effect", Width = 158, Height = 34 }; var remove = new Button { Text = "Remove effect", Width = 158, Height = 34 };
        Theme.StyleDarkButton(add); Theme.StyleDarkButton(remove); buttons.Controls.AddRange([add, remove]); panel.Controls.Add(buttons);
        _preset.Items.AddRange(HeldItemEffectService.Presets.Select(p => (object)p.Label).ToArray()); panel.Controls.Add(_preset);
        panel.Controls.Add(new Label { Text = "Native presets: experimental on other items. Some need motion, owner parameters or game context. No promise of universal compatibility. Max 3 effects per item.", Width = 338, Height = 82, ForeColor = Theme.OnDarkMuted });
        foreach (var (name, min, max, initial) in new[] { ("Offset X (cm)", -1000m,1000m,0m), ("Offset Y (cm)",-1000m,1000m,0m), ("Offset Z (cm)",-1000m,1000m,0m),
            ("Pitch (deg)",-360m,360m,0m), ("Yaw (deg)",-360m,360m,0m), ("Roll (deg)",-360m,360m,0m), ("Scale",.01m,10m,1m) }) {
            var row = new FlowLayoutPanel { Width = 338, Height = 38 }; row.Controls.Add(new Label { Text = name, Width = 128, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
            var n = new NumericUpDown { Width = 180, Minimum = min, Maximum = max, Value = initial, DecimalPlaces = 2, Increment = .1m, BackColor = Theme.PanelBg, ForeColor = Theme.OnDark };
            row.Controls.Add(n); panel.Controls.Add(row); _numbers.Add(n); n.ValueChanged += (_, _) => Changed();
        }
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new(0, 12, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 110)); footer.ColumnStyles.Add(new(SizeType.Absolute, 150));
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel }; var save = new Button { Text = "Use effects", Dock = DockStyle.Fill };
        Theme.StyleDarkButton(cancel); Theme.StyleGoldButton(save); footer.Controls.Add(_status); footer.Controls.Add(cancel,1,0); footer.Controls.Add(save,2,0); root.Controls.Add(footer,0,2); root.SetColumnSpan(footer,2);
        save.Click += (_, _) => { var errors = HeldItemEffectService.Validate(Result); if (errors.Count > 0) { Dialog.Warn(this,"Check effects",string.Join("\n",errors)); return; } DialogResult = DialogResult.OK; Close(); };
        CancelButton = cancel; AcceptButton = save;
        void RefreshList(int selected) { _binding = true; _list.Items.Clear(); foreach (var e in Result) _list.Items.Add(HeldItemEffectService.Presets.FirstOrDefault(p => p.Id == e.PresetId)?.Label ?? e.PresetId); _binding = false; _list.SelectedIndex = selected; add.Enabled = Result.Count < 3; Bind(); Publish(); }
        add.Click += (_, _) => { if (Result.Count < 3) { Result.Add(new() { Z = 30 }); RefreshList(Result.Count - 1); } };
        remove.Click += (_, _) => { var i = _list.SelectedIndex; if (i >= 0) { Result.RemoveAt(i); RefreshList(Math.Min(i,Result.Count-1)); } };
        _list.SelectedIndexChanged += (_, _) => { if (!_binding) Bind(); };
        _preset.SelectedIndexChanged += (_, _) => Changed();
        RefreshList(Result.Count > 0 ? 0 : -1);
        Shown += async (_, _) => {
            _loading = true; save.Enabled = cancel.Enabled = false; _status.Text = "Loading item placement preview…";
            try {
                _folder = await Task.Run(() => WeaponModelService.CreateViewer(item.MeshPackage));
                if (item.CustomModel is { } model) await Task.Run(() => WeaponModelService.Preview(model, _folder, false, true, 1));
                Publish(); await _viewer.ShowFolderAsync(_folder); _status.Text = "Placement in mesh-local cm. Preview colors / particles are illustrative, not editable game parameters.";
            } catch(Exception ex) { _status.Text = "Preview unavailable; settings still editable: " + ex.Message; }
            finally { _loading = false; if (!IsDisposed) save.Enabled = cancel.Enabled = true; }
        };
        FormClosing += (_, e) => { if (_loading) e.Cancel = true; };
    }
    private void Bind()
    {
        _binding = true; var i = _list.SelectedIndex; _preset.Enabled = i >= 0; foreach(var n in _numbers) n.Enabled = i >= 0;
        if (i >= 0) { var e = Result[i]; _preset.SelectedIndex = Array.FindIndex(HeldItemEffectService.Presets,p=>p.Id==e.PresetId);
            var v = new[] {e.X,e.Y,e.Z,e.Pitch,e.Yaw,e.Roll,e.Scale}; for(int n=0;n<v.Length;n++) _numbers[n].Value = float.IsFinite(v[n]) ? Math.Clamp((decimal)v[n],_numbers[n].Minimum,_numbers[n].Maximum) : _numbers[n].Minimum; }
        _binding = false;
    }
    private void Changed()
    {
        if(_binding || _list.SelectedIndex < 0 || _preset.SelectedIndex < 0) return;
        var e = Result[_list.SelectedIndex]; e.PresetId = HeldItemEffectService.Presets[_preset.SelectedIndex].Id;
        var v = _numbers.Select(n=>(float)n.Value).ToArray(); e.X=v[0];e.Y=v[1];e.Z=v[2];e.Pitch=v[3];e.Yaw=v[4];e.Roll=v[5];e.Scale=v[6];
        _binding=true; _list.Items[_list.SelectedIndex]=HeldItemEffectService.Presets[_preset.SelectedIndex].Label; _binding=false; Publish();
    }
    private void Publish()
    {
        if (_folder is null) return;
        var effects = Result.Where(e=>HeldItemEffectService.Presets.Any(p=>p.Id==e.PresetId)).Select(e=> {var p=HeldItemEffectService.Presets.Single(p=>p.Id==e.PresetId);return new {e.X,e.Y,e.Z,e.Pitch,e.Yaw,e.Roll,e.Scale,p.Shape,p.Color};});
        AtomicFileUtil.WriteAllText(Path.Combine(_folder,"effects.json"),System.Text.Json.JsonSerializer.Serialize(effects));
    }
}
