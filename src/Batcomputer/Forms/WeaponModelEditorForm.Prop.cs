namespace Batcomputer;

public partial class WeaponModelEditorForm
{
    private HeldItemSettings? _prop;
    private TextBox? _propName, _propMesh;
    private ThemedDropDown? _propTemplate, _propHand, _propVisibility;
    private CheckBox? _propPreferOverEmptyHands, _propHideWhileGliding;
    internal HeldItemSettings? PropResult { get; private set; }

    private Control BuildPropPage()
    {
        var page = ItemInspectorPages.ScrollPage(); var p = _prop!;
        TextBox Input(string text) { var t = new TextBox { Text = text, Width = 300 }; Theme.StyleDarkInput(t); return t; }
        ThemedDropDown Choice(object[] choices, int index) { var c = new ThemedDropDown { Width = 300, Height = 36 }; c.Items.AddRange(choices); c.SelectedIndex = index; return c; }
        _propName = Input(p.Name);
        _propHand = Choice(Enum.GetValues<HeldItemHand>().Select(h => (object)HeldItemService.HandLabel(h)).ToArray(), Enum.IsDefined(p.Hand) ? (int)p.Hand : -1);
        _propVisibility = Choice(Enum.GetValues<HeldWeaponVisibility>().Select(v => (object)HeldItemsForm.VisibilityLabel(v)).ToArray(), Enum.IsDefined(p.Visibility) ? (int)p.Visibility : -1);
        page.Controls.Add(ItemWorkshopUi.Section("Prop identity", "Name this addition. It is separate from the character's existing ability items.", _propName));
        page.Controls.Add(ItemWorkshopUi.Section("Where to hold it", "Both hands shares the model and alignment; it does not mirror geometry. Use separate props for different grips.", _propHand));
        page.Controls.Add(ItemWorkshopUi.Section("When to show it", "Native gadgets can still take priority over extra hand props.", _propVisibility));
        _propHideWhileGliding = new CheckBox { Text = "Hide while gliding", AutoSize = true, Checked = p.HideWhileGliding };
        page.Controls.Add(ItemWorkshopUi.Section("Traversal visibility", "Temporarily hides this prop during the native gliding state. After gliding, its normal visibility and hand priority apply again.", _propHideWhileGliding));
        _propPreferOverEmptyHands = new CheckBox { Text = "Keep prop over animation empty-hand requests", AutoSize = true,
            MaximumSize = new(300, 0), Checked = p.PreferOverAnimationEmptyHands };
        page.Controls.Add(ItemWorkshopUi.Section("Hand priority · advanced", "Useful for claws: lets this prop beat High-priority empty-hand animations. Native block tags still hide it; other Epic-priority items may compete. Can overlap gadgets or carried objects. Off keeps native prop priority.", _propPreferOverEmptyHands));
        _propTemplate = Choice(HeldItemService.Templates.Select(t => (object)t.Label).ToArray(), Array.FindIndex(HeldItemService.Templates, t => t.Id == p.TemplateId));
        var help = new Label { AutoSize = true, MaximumSize = new(300, 0), ForeColor = Theme.OnDarkMuted };
        void Help() { help.Text = _propTemplate.SelectedIndex >= 0 ? HeldItemService.Templates[_propTemplate.SelectedIndex].Notes : "Choose a native behavior donor."; }
        Help();
        page.Controls.Add(ItemWorkshopUi.Section("Native behavior donor", "This chooses the actor and its native collision behavior, not your character's abilities. Changing it keeps your imported model and name.", _propTemplate, help));
        var effects = ItemWorkshopUi.Button($"Cosmetic effects… · {p.Effects.Count}");
        effects.Click += (_, _) =>
        {
            if (_busy) return;
            try
            {
                var draft = ReadProp(_working is null ? null : ReadRecipe());
                using var editor = new HeldItemEffectsForm(draft);
                if (editor.ShowDialog(this) == DialogResult.OK) { p.Effects = editor.Result.Select(e => e.Clone()).ToList(); effects.Text = $"Cosmetic effects… · {p.Effects.Count}"; }
            }
            catch (Exception ex) { Dialog.Warn(this, "Check prop", ex.Message); }
        };
        page.Controls.Add(ItemWorkshopUi.Section("Optional effects", "Visual effects do not grant damage, electrical attacks or gadget abilities.", effects));
        _propTemplate.SelectedIndexChanged += async (_, _) =>
        {
            if (_propTemplate.SelectedIndex < 0 || _busy) return;
            var t = HeldItemService.Templates[_propTemplate.SelectedIndex]; p.TemplateId = t.Id; p.MeshPackage = _referencePackage = t.Mesh;
            if (_propMesh is not null) _propMesh.Text = t.Mesh; Help();
            if (Visible) await LoadReferenceAsync();
        };
        _nativeMaterials.SetSlots([(0, "Original surface 0", "")], p.MaterialPackage.Length > 0 ? [new() { Slot = 0, Package = p.MaterialPackage }] : []);
        return page;
    }

    private void AddPropAdvancedModelControls(Control page)
    {
        var advanced = new CheckBox { Text = "Advanced · use another cooked model", AutoSize = true };
        _propMesh = new TextBox { Text = _prop!.MeshPackage, Width = 300 }; Theme.StyleDarkInput(_propMesh);
        var load = ItemWorkshopUi.Button("Load original preview");
        var fields = ItemWorkshopUi.Section("Cooked model package", "Only needed for a different existing game mesh. OBJ users can leave this alone.", _propMesh, load); fields.Visible = false;
        advanced.CheckedChanged += (_, _) => fields.Visible = advanced.Checked;
        load.Click += async (_, _) => { if (_busy) return; _prop.MeshPackage = _referencePackage = _propMesh.Text.Trim(); await LoadReferenceAsync(); };
        page.Controls.Add(advanced); page.Controls.Add(fields);
    }

    internal HeldItemSettings ReadProp(WeaponModelRecipe? model)
    {
        var p = _prop!.Clone(); p.Name = _propName!.Text.Trim();
        p.TemplateId = _propTemplate!.SelectedIndex >= 0 ? HeldItemService.Templates[_propTemplate.SelectedIndex].Id : "";
        p.Hand = (HeldItemHand)_propHand!.SelectedIndex; p.Visibility = (HeldWeaponVisibility)_propVisibility!.SelectedIndex;
        p.PreferOverAnimationEmptyHands = _propPreferOverEmptyHands!.Checked;
        p.HideWhileGliding = _propHideWhileGliding!.Checked;
        p.MeshPackage = _propMesh!.Text.Trim(); p.CustomModel = model?.Clone(); return p;
    }
}
