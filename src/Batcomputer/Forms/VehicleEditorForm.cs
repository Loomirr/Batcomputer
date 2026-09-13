namespace Batcomputer;

internal sealed class VehicleEditorForm : AdaptiveForm
{
    private readonly VehicleProjectService _service;
    private readonly VehicleProject _working;
    private readonly string _native;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ThemedDropDown _owner = new() { Dock = DockStyle.Fill };
    private readonly ListBox _components = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly NumericUpDown[] _transform = new NumericUpDown[9];
    private readonly Label _modelStatus = new() { Dock = DockStyle.Fill };
    private readonly Label _attachmentStatus = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _materials = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false };
    private bool _busy;
    private VehicleAssetService.Component? _displayedComponent;
    private bool _loadingTransform;
    private bool _transformDirty;
    private readonly List<VehicleAssetService.Component> _availableComponents = [];
    internal VehicleProject? Result { get; private set; }
    private sealed record OwnerChoice(string Label, string Tag, string ProjectPath = "") { public override string ToString() => Label; }

    internal VehicleEditorForm(string projectRoot, VehicleProject project, bool settingsOnly = false)
    {
        _service = new(projectRoot); _working = project.Clone(); _native = AppSettings.Current.EffectiveExtractedContentRoot();
        Text = "Batcomputer — Vehicle setup"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1160, 780); MinimumSize = new Size(940, 650); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 60)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 56)); Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "VEHICLE WORKSHOP\n" + project.DisplayName + " · separate selectable vehicle", Dock = DockStyle.Fill, ForeColor = Theme.Gold }, 0, 0);
        var tabs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        tabs.ColumnStyles.Add(new(SizeType.Percent, 100)); tabs.RowStyles.Add(new(SizeType.Absolute, 48)); tabs.RowStyles.Add(new(SizeType.Percent, 100)); layout.Controls.Add(tabs, 0, 1);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; tabs.Controls.Add(navigation, 0, 0); tabs.Controls.Add(pages, 0, 1);
        var sections = new List<(Panel Page, Button Button)>();
        Panel Page(string title)
        {
            var p = new Panel { Name = title, Dock = DockStyle.Fill, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, Padding = new Padding(14), AutoScroll = true, Visible = sections.Count == 0 };
            var button = ActionButton(title, (_, _) => { foreach (var section in sections) { section.Page.Visible = section.Page == p; section.Button.ForeColor = section.Page == p ? Theme.Gold : Theme.OnDark; } p.BringToFront(); });
            button.Width = 190; button.ForeColor = sections.Count == 0 ? Theme.Gold : Theme.OnDark;
            sections.Add((p, button)); navigation.Controls.Add(button); pages.Controls.Add(p); return p;
        }
        var identity = Page("Vehicle"); var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 150)); fields.ColumnStyles.Add(new(SizeType.Percent, 100)); identity.Controls.Add(fields);
        void Field(string label, Control control, int height = 46) { var row = fields.RowCount++; fields.RowStyles.Add(new(SizeType.Absolute, height)); fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); fields.Controls.Add(control, 1, row); }
        _name.Text = project.DisplayName; Field("Name", _name);
        foreach (var tag in VehicleAssetService.NativeOwners) _owner.Items.Add(new OwnerChoice(tag.Split('.').Last() + (tag.EndsWith(".Batman", StringComparison.Ordinal) ? "" : " (untested owner)"), tag));
        var suits = new SuitProjectService(projectRoot);
        foreach (var summary in suits.ListProjects().Where(s => s.IsCharacter))
        {
            try { var character = suits.LoadProject(summary.Path); if (character?.CustomCharacter is { IsDefinition: true } c) _owner.Items.Add(new OwnerChoice(summary.DisplayName + " (custom)", CustomCharacterProjectService.Scope(c), Path.GetRelativePath(projectRoot, summary.Path))); }
            catch { /* Unreadable character recipes must not be offered as owners. */ }
        }
        _owner.SelectedItem = _owner.Items.Cast<OwnerChoice>().FirstOrDefault(o => o.Tag == project.OwnerTag && o.ProjectPath == project.OwnerCharacterProjectPath);
        Field("Character", _owner); Field("Driving base", new Label { Text = "1995 Batman Forever Batmobile", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        Field("Vehicle ID", new TextBox { Text = project.Id, ReadOnly = true, Dock = DockStyle.Fill });
        Field("", new Label { Text = "The name can change; the saved ID stays the same.\nCustom owners are included automatically when the mod builds.", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted }, 65);

        var model = Page("Body & materials");
        var modelLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        modelLayout.ColumnStyles.Add(new(SizeType.Percent, 100)); modelLayout.RowStyles.Add(new(SizeType.Absolute, 62)); modelLayout.RowStyles.Add(new(SizeType.Absolute, 46)); modelLayout.RowStyles.Add(new(SizeType.Percent, 100)); modelLayout.RowStyles.Add(new(SizeType.Absolute, 50)); model.Controls.Add(modelLayout);
        modelLayout.Controls.Add(_modelStatus, 0, 0);
        modelLayout.Controls.Add(Button("Import / edit rigged FBX…", (_, _) => EditModel()), 0, 1);
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", ReadOnly = true, Width = 160 });
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cooked material package", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Color override", ReadOnly = true, Width = 125 }); Theme.StyleGrid(_materials); modelLayout.Controls.Add(_materials, 0, 2);
        var materialActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        materialActions.Controls.Add(ActionButton("Set slot color…", (_, _) => SetColor()));
        materialActions.Controls.Add(ActionButton("Use slot material", (_, _) => { if (_materials.CurrentRow is { } row) { ReadMaterials(); _working.Palette.RemoveAll(c => c.Slot == row.Index); FillModel(); } }));
        materialActions.Controls.Add(ActionButton("Restore native body", (_, _) => { if (Dialog.Confirm(this, "Restore native body?", "This removes the model replacement and its light-surface assignments. Imported source files are kept.", confirmText: "Restore")) { _working.Model = null; _working.LightSurfaces.Clear(); _working.Palette.Clear(); _working.MaterialOverrides.RemoveAll(m => m.Component == "body"); FillModel(); } }));
        modelLayout.Controls.Add(materialActions, 0, 3); FillModel();

        var attachments = Page("Exact offsets");
        if (settingsOnly) { sections[^1].Button.Visible = false; }
        var assembly = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        assembly.ColumnStyles.Add(new(SizeType.Percent, 100)); assembly.RowStyles.Add(new(SizeType.Absolute, 54)); assembly.RowStyles.Add(new(SizeType.Absolute, 35)); assembly.RowStyles.Add(new(SizeType.Percent, 100)); attachments.Controls.Add(assembly);
        var open3d = Button("Open 3D assembly workshop…", (_, _) => EditAssembly()); Theme.StyleGoldButton(open3d); assembly.Controls.Add(open3d, 0, 0);
        assembly.Controls.Add(new Label { Text = "Position the native glow pieces in 3D, or use the exact offsets below.", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        var attachmentLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        attachmentLayout.ColumnStyles.Add(new(SizeType.Percent, 46)); attachmentLayout.ColumnStyles.Add(new(SizeType.Percent, 54)); attachmentLayout.RowStyles.Add(new(SizeType.Percent, 100)); attachmentLayout.RowStyles.Add(new(SizeType.Absolute, 62)); assembly.Controls.Add(attachmentLayout, 0, 2);
        var partList = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; partList.ColumnStyles.Add(new(SizeType.Percent, 100)); partList.RowStyles.Add(new(SizeType.Absolute, 36)); partList.RowStyles.Add(new(SizeType.Percent, 100)); attachmentLayout.Controls.Add(partList, 0, 0);
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Search attachments…" }; partList.Controls.Add(search, 0, 0);
        _components.BackColor = Theme.PanelBg; _components.ForeColor = Theme.OnDark; _components.BorderStyle = BorderStyle.None; _components.FormattingEnabled = true;
        _components.Format += (_, e) => { if (e.ListItem is VehicleAssetService.Component c) e.Value = VehicleWorkshopService.Label(c.Name) + "  ·  " + VehicleWorkshopService.Group(c.Name); };
        partList.Controls.Add(_components, 0, 1);
        search.TextChanged += (_, _) =>
        {
            if (_transformDirty) ApplyComponent(); var selected = (_components.SelectedItem as VehicleAssetService.Component)?.Name;
            _components.BeginUpdate(); _components.Items.Clear();
            foreach (var c in _availableComponents.Where(c => (c.Name + " " + VehicleWorkshopService.Group(c.Name)).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))) _components.Items.Add(c);
            _components.SelectedItem = _components.Items.Cast<VehicleAssetService.Component>().FirstOrDefault(c => c.Name == selected);
            if (_components.SelectedIndex < 0 && _components.Items.Count > 0) _components.SelectedIndex = 0;
            _components.EndUpdate(); FillComponent();
        };
        var editScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; attachmentLayout.Controls.Add(editScroll, 1, 0);
        var edit = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) }; edit.ColumnStyles.Add(new(SizeType.Percent, 50)); edit.ColumnStyles.Add(new(SizeType.Percent, 50)); editScroll.Controls.Add(edit);
        var labels = new[] { "X (cm)", "Y (cm)", "Z (cm)", "Pitch (degrees)", "Yaw (degrees)", "Roll (degrees)", "Scale X", "Scale Y", "Scale Z" };
        for (int i = 0; i < 9; i++) { var number = new NumericUpDown { Dock = DockStyle.Fill, DecimalPlaces = 3, Minimum = i >= 6 ? .01m : i >= 3 ? -360 : -10000, Maximum = i >= 6 ? 100 : i >= 3 ? 360 : 10000, Value = i >= 6 ? 1 : 0 }; _transform[i] = number; edit.RowStyles.Add(new(SizeType.Absolute, 35)); edit.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill }, 0, i); edit.Controls.Add(number, 1, i); }
        edit.RowStyles.Add(new(SizeType.Absolute, 44)); edit.Controls.Add(Button("Apply to component", (_, _) => ApplyComponent()), 0, 9); edit.Controls.Add(Button("Reset to native", (_, _) => { if (_components.SelectedItem is VehicleAssetService.Component c) { _working.Transforms.RemoveAll(t => t.Component == c.Name); FillComponent(); } }), 1, 9);
        _attachmentStatus.ForeColor = Theme.OnDarkMuted; attachmentLayout.Controls.Add(_attachmentStatus, 0, 1); attachmentLayout.SetColumnSpan(_attachmentStatus, 2);
        foreach (var number in _transform) number.ValueChanged += (_, _) => { if (!_loadingTransform) _transformDirty = true; };
        _components.SelectedIndexChanged += (_, _) => { if (_transformDirty) ApplyComponent(); FillComponent(); };
        Shown += async (_, _) => { _busy = true; try { var components = await Task.Run(() => VehicleAssetService.Components(_native)); _availableComponents.AddRange(components); foreach (var c in components) _components.Items.Add(c); if (_components.Items.Count > 0) _components.SelectedIndex = 0; } catch (Exception ex) { _attachmentStatus.Text = ex.Message; } finally { _busy = false; } };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var save = ActionButton(settingsOnly ? "Apply setup" : "Save vehicle", (_, _) => Save()); Theme.StyleGoldButton(save);
        var cancel = ActionButton("Cancel", (_, _) => Close()); cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(save); footer.Controls.Add(cancel); layout.Controls.Add(footer, 0, 2); CancelButton = cancel;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        void StyleFields(Control parent) { foreach (Control c in parent.Controls) { if (c is TextBoxBase or NumericUpDown or ComboBox) { c.BackColor = Theme.PanelBg; c.ForeColor = Theme.OnDark; } if (c is TextBox text) text.BorderStyle = BorderStyle.FixedSingle; if (c is NumericUpDown numeric) numeric.BorderStyle = BorderStyle.FixedSingle; StyleFields(c); } } StyleFields(this);
    }
    private static Button Button(string text, EventHandler click) { var b = new Button { Text = text, Dock = DockStyle.Fill, UseMnemonic = false }; Theme.StyleDarkButton(b); b.Click += click; return b; }
    private static Button ActionButton(string text, EventHandler click) { var b = Button(text, click); b.Dock = DockStyle.None; b.Size = new Size(155, 36); return b; }
    private void FillModel()
    {
        _modelStatus.Text = _working.Model is null ? "Native body · original LEGO materials\nImport a weighted FBX to replace it." : $"{_working.Model.Name} · {_working.Model.Materials.Count} material slots\nDefault materials and colors. Choose surface overrides in the 3D workshop.";
        _materials.Rows.Clear();
        if (_working.Model is { } model) foreach (var slot in model.Materials) _materials.Rows.Add(slot.SourceMaterialName, slot.MaterialPath, _working.Palette.Any(c => c.Slot == slot.Slot) ? "Custom color" : "Material");
    }
    private void ReadMaterials()
    {
        _materials.EndEdit();
        if (_working.Model is { } model) for (int i = 0; i < model.Materials.Count; i++) model.Materials[i].MaterialPath = UnrealPathUtil.NormalizePackagePath(_materials.Rows[i].Cells[1].Value?.ToString());
    }
    private void EditModel()
    {
        if (_busy) return; ReadMaterials();
        var recipe = _working.Model?.Clone() ?? new SkinnedMeshImport { Name = _name.Text + " body", Component = "SkeletalMeshComponent", DonorMeshPackage = VehicleAssetService.NativeMesh, MeshPackage = VehicleProjectService.Mesh(_working) };
        using var workshop = new SkinnedMeshWorkshopForm(_service.DirectoryFor(_working), [new("SkeletalMeshComponent", VehicleAssetService.NativeMesh)], [], recipe, _working.Model is not null, vehicle: true);
        if (workshop.ShowDialog(this) == DialogResult.OK && workshop.Result is { } result)
        {
            if (_working.Model is null || !_working.Model.Materials.Select(m => m.SourceMaterialName).SequenceEqual(result.Materials.Select(m => m.SourceMaterialName))) { _working.Palette.Clear(); _working.MaterialOverrides.RemoveAll(m => m.Component == "body"); }
            // Match light sections by their imported names, never by a possibly reordered slot index.
            var lightNames = _working.LightSurfaces.Select(s => (Name: _working.Model?.Materials.FirstOrDefault(m => m.Slot == s.Slot)?.SourceMaterialName, s.Role)).ToArray();
            _working.LightSurfaces = lightNames.Select(s => (Material: result.Materials.FirstOrDefault(m => m.SourceMaterialName == s.Name), s.Role))
                .Where(s => s.Material is not null).Select(s => new VehicleLightSurface { Slot = s.Material!.Slot, Role = s.Role }).ToList();
            _working.Model = result; FillModel();
        }
        else if (workshop.RemoveRequested) { _working.Model = null; _working.LightSurfaces.Clear(); _working.Palette.Clear(); _working.MaterialOverrides.RemoveAll(m => m.Component == "body"); FillModel(); }
    }
    private void EditAssembly()
    {
        if (_busy) return;
        try
        {
            if (_transformDirty) ApplyComponent(); ReadMaterials();
            var preview = _working.Clone(); preview.DisplayName = _name.Text.Trim();
            using var workshop = new VehicleWorkshopForm(_service.DirectoryFor(_working), preview);
            if (workshop.ShowDialog(this) == DialogResult.OK && workshop.Result is { } result)
            {
                _working.Transforms = result.Transforms; _working.MaterialOverrides = result.MaterialOverrides; _working.DisabledParts = result.DisabledParts; FillComponent();
                _attachmentStatus.Text = "3D placements applied. Save vehicle to keep them.";
            }
        }
        catch (Exception ex) { Dialog.Error(this, "Workshop could not be opened", ex.Message); }
    }
    private void SetColor()
    {
        if (_materials.CurrentRow is not { } row) return; ReadMaterials();
        using var color = new ColorDialog { FullOpen = true };
        if (color.ShowDialog(this) != DialogResult.OK) return;
        static float Linear(byte v) { var n = v / 255f; return n <= .04045f ? n / 12.92f : MathF.Pow((n + .055f) / 1.055f, 2.4f); }
        _working.MaterialOverrides.RemoveAll(m => m.Component == "body" && m.Slot == row.Index);
        _working.Palette.RemoveAll(c => c.Slot == row.Index); _working.Palette.Add(new() { Slot = row.Index, R = Linear(color.Color.R), G = Linear(color.Color.G), B = Linear(color.Color.B) }); FillModel();
    }
    private void FillComponent()
    {
        if (_components.SelectedItem is not VehicleAssetService.Component c) { _displayedComponent = null; foreach (var n in _transform) n.Enabled = false; _attachmentStatus.Text = "No matching attachments."; return; }
        foreach (var n in _transform) n.Enabled = true;
        _displayedComponent = c;
        var t = _working.Transforms.FirstOrDefault(t => t.Component == c.Name) ?? c.Transform;
        var values = new[] { t.X, t.Y, t.Z, t.Pitch, t.Yaw, t.Roll, t.ScaleX, t.ScaleY, t.ScaleZ };
        _loadingTransform = true;
        try { for (int i = 0; i < values.Length; i++) _transform[i].Value = Math.Clamp((decimal)values[i], _transform[i].Minimum, _transform[i].Maximum); }
        finally { _loadingTransform = false; _transformDirty = false; }
        for (int i = 6; i < 9; i++) _transform[i].Enabled = !VehicleCustomizationService.IsSeat(c.Name);
        _attachmentStatus.Text = VehicleCustomizationService.IsSeat(c.Name) ? "Experimental seat offset. Test seating and entry/exit in-game." : $"{VehicleWorkshopService.Label(c.Name)} · {c.Kind}\nSocket-local offsets. Glow scale may also be controlled by the game.";
    }
    private void ApplyComponent()
    {
        if (_displayedComponent is not { } c) return;
        var v = _transform.Select(n => (float)n.Value).ToArray(); _working.Transforms.RemoveAll(t => t.Component == c.Name);
        _working.Transforms.Add(new() { Component = c.Name, X = v[0], Y = v[1], Z = v[2], Pitch = v[3], Yaw = v[4], Roll = v[5], ScaleX = v[6], ScaleY = v[7], ScaleZ = v[8] }); _transformDirty = false; _attachmentStatus.Text = "Component settings applied. Save the vehicle to keep them.";
    }
    private void Save()
    {
        if (_busy) return;
        if (_transformDirty) ApplyComponent();
        try { ReadMaterials(); _working.DisplayName = _name.Text.Trim(); if (_owner.SelectedItem is not OwnerChoice owner) throw new InvalidDataException("Choose an available character owner."); _working.OwnerTag = owner.Tag; _working.OwnerCharacterProjectPath = owner.ProjectPath; VehicleProjectService.ValidateIdentity(_working); if (_working.Model is { } model) { SkinnedMeshStageService.ValidateRecipe(model); SkinnedMeshStageService.ReadManifest(_service.DirectoryFor(_working), model); } Result = _working; DialogResult = DialogResult.OK; Close(); }
        catch (Exception ex) { Dialog.Error(this, "Vehicle not saved", ex.Message); }
    }
}
