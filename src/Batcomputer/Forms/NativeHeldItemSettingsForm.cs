namespace Batcomputer;

internal sealed class NativeHeldItemSettingsForm : AdaptiveForm
{
    public NativeHeldItemEdit Result { get; private set; }
    private readonly ModelPreviewControl _viewer = new() { Dock = DockStyle.Fill };
    private readonly ItemMaterialSlotsControl _materials = new(true);
    private readonly ItemMaterialSlotsControl _customMaterials = new();
    private readonly List<NumericUpDown> _alignment = [];
    private readonly CheckBox _reference = new() { Text = "Original model · faded comparison", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 350 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private string? _folder;
    private ModelPreviewService.ItemMaterialPreviewSession? _session;
    private bool _busy, _pending, _bindingAlignment, _saving;
    private int _revision;
    private WeaponModelRecipe? _model;
    private IReadOnlyList<(int Slot, string Name, string Package)> _nativeSlots = [];

    internal NativeHeldItemSettingsForm(NativeHeldItemService.Binding binding, NativeHeldItemEdit? saved)
    {
        Result = saved?.Clone() ?? binding.Identity.Clone(); _model = Result.CustomModel?.Clone();
        Text = "Batcomputer — Native held item"; StartPosition = FormStartPosition.CenterParent; ClientSize = new(1300, 850); MinimumSize = new(1040, 680);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body; Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 84)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(root);
        root.Controls.Add(ItemWorkshopUi.Header(HeldItemsForm.Friendly(binding.Identity.ActorPackage), $"{binding.Slot.Replace("LAM.", "")} · native held item. Customize appearance without changing attacks, timing or collision."));
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1268, 680), SplitterDistance = 810, SplitterWidth = 8, Panel1MinSize = 380, Panel2MinSize = 410 };
        split.Panel1.Controls.Add(_viewer); var pages = new ItemInspectorPages(); split.Panel2.Controls.Add(pages); root.Controls.Add(split, 0, 1);
        var appearance = ItemInspectorPages.ScrollPage(); var placementPage = ItemInspectorPages.ScrollPage();
        pages.Add("Model", appearance);
        var materialPage = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        materialPage.RowStyles.Add(new(SizeType.Absolute, 90)); materialPage.RowStyles.Add(new(SizeType.Percent, 100));
        var materialHelp = new Label { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted };
        var materialHost = new Panel { Dock = DockStyle.Fill }; materialHost.Controls.Add(_materials); materialHost.Controls.Add(_customMaterials);
        materialPage.Controls.Add(materialHelp); materialPage.Controls.Add(materialHost, 0, 1);
        pages.Add("Materials", materialPage); pages.Add("Placement", placementPage);
        var hide = new CheckBox { Text = "Hide this item's visuals", Checked = Result.Hide, AutoSize = true, Margin = new(4, 12, 4, 8) }; appearance.Controls.Add(hide);
        appearance.Controls.Add(ItemWorkshopUi.Note("The native behavior is retained even when hidden. Left and right hands are independent—edit both for matching claws."));
        var import = ItemWorkshopUi.Button("Import custom OBJ…", true); import.Width = 340; appearance.Controls.Add(import);
        var source = new Label { AutoSize = true, ForeColor = Theme.OnDark, MaximumSize = new(340, 0) }; appearance.Controls.Add(source);
        appearance.Controls.Add(_reference);
        var clear = ItemWorkshopUi.Button("Use cooked mesh instead"); appearance.Controls.Add(clear);
        appearance.Controls.Add(ItemWorkshopUi.Note("Advanced · replace with a cooked static mesh\nLeave blank to keep the original mesh. Enter its exact /Game or DLC package, then load the preview."));
        var mesh = new TextBox { Text = Result.ReplacementMeshPackage, Width = 340 }; Theme.StyleDarkInput(mesh); appearance.Controls.Add(mesh);
        var load = ItemWorkshopUi.Button("Load cooked mesh preview"); appearance.Controls.Add(load);
        var nativeInfo = new TextBox { Text = binding.Identity.OriginalMeshPackage, Multiline = true, ReadOnly = true, Width = 340, Height = 75, ScrollBars = ScrollBars.Vertical }; Theme.StyleDarkInput(nativeInfo);
        appearance.Controls.Add(ItemWorkshopUi.Note("Original mesh (read-only)")); appearance.Controls.Add(nativeInfo);
        appearance.Controls.Add(ItemWorkshopUi.Note("This is an isolated mesh/material preview. Actual grip, component placement and visibility during attacks must be checked on the character or in-game."));
        var alignmentGroup = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 340 };
        alignmentGroup.Controls.Add(ItemWorkshopUi.Note("Custom OBJ alignment · live preview\nDrag the 3D handles: W move · R rotate · E size. World / Local axes and Snap are in the viewer; sizing is uniform.\nThe gizmo follows the centered OBJ, not the hand grip. Fields use Unreal centimeters. Native component placement below is separate."));
        foreach (var (name, min, max) in new[] { ("Uniform scale", .001m, 1000m), ("Position X (cm)", -10000m, 10000m), ("Position Y (cm)", -10000m, 10000m), ("Position Z (cm)", -10000m, 10000m), ("Pitch (°)", -360m, 360m), ("Yaw (°)", -360m, 360m), ("Roll (°)", -360m, 360m) })
        {
            var row = new TableLayoutPanel { Width = 330, Height = 42, ColumnCount = 2 }; row.ColumnStyles.Add(new(SizeType.Percent, 45)); row.ColumnStyles.Add(new(SizeType.Percent, 55));
            row.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            var n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = 3, Increment = .1m, BackColor = Theme.PanelBg, ForeColor = Theme.OnDark, Value = min > 0 ? 1 : 0 };
            row.Controls.Add(n, 1, 0); alignmentGroup.Controls.Add(row); _alignment.Add(n);
        }
        var resetAlignment = ItemWorkshopUi.Button("Reset OBJ alignment"); alignmentGroup.Controls.Add(resetAlignment); placementPage.Controls.Add(alignmentGroup);
        placementPage.Controls.Add(ItemWorkshopUi.Note("Advanced · Native component placement"));
        var placement = new CheckBox { Text = "Override native component placement", Checked = Result.Transform is not null, AutoSize = true }; placementPage.Controls.Add(placement);
        placementPage.Controls.Add(ItemWorkshopUi.Note("Relative to the item's native parent, not world space. This does not change the OBJ's baked alignment. Placement is not applied to this isolated mesh preview."));
        var t = Result.Transform ?? binding.Placement; var values = new[] { t.X, t.Y, t.Z, t.Pitch, t.Yaw, t.Roll, t.ScaleX, t.ScaleY, t.ScaleZ };
        var labels = new[] { "Position X (cm)", "Position Y (cm)", "Position Z (cm)", "Pitch (°)", "Yaw (°)", "Roll (°)", "Scale X", "Scale Y", "Scale Z" }; var numbers = new List<NumericUpDown>();
        for (int i = 0; i < values.Length; i++)
        {
            var row = new TableLayoutPanel { Width = 340, Height = 42, ColumnCount = 2 }; row.ColumnStyles.Add(new(SizeType.Percent, 45)); row.ColumnStyles.Add(new(SizeType.Percent, 55));
            row.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            var min = i >= 6 ? .001m : -10000m; var n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = 10000, DecimalPlaces = 3, Increment = .1m, Value = Math.Clamp((decimal)values[i], min, 10000), BackColor = Theme.PanelBg, ForeColor = Theme.OnDark };
            row.Controls.Add(n, 1, 0); numbers.Add(n); placementPage.Controls.Add(row);
        }
        var restore = ItemWorkshopUi.Button("Restore native placement"); placementPage.Controls.Add(restore);
        restore.Click += (_, _) => { placement.Checked = false; var p = binding.Placement; var v = new[] { p.X, p.Y, p.Z, p.Pitch, p.Yaw, p.Roll, p.ScaleX, p.ScaleY, p.ScaleZ }; for (var i = 0; i < v.Length; i++) numbers[i].Value = Math.Clamp((decimal)v[i], numbers[i].Minimum, numbers[i].Maximum); };
        var use = ItemWorkshopUi.Button("Use native item", true); var cancel = ItemWorkshopUi.Button("Cancel"); use.Dock = cancel.Dock = DockStyle.Fill; cancel.DialogResult = DialogResult.Cancel;
        root.Controls.Add(ItemWorkshopUi.Footer(_status, cancel, use), 0, 2); CancelButton = cancel;
        _materials.SetSlots(Result.Materials.Select(m => (m.Slot, $"Material {m.Slot}", "")), Result.Materials);
        SetCustomControls(_model);
        void Enable()
        {
            hide.Enabled = !_busy; restore.Enabled = !_busy && !hide.Checked;
            foreach (var n in numbers) n.Enabled = !_busy && placement.Checked && !hide.Checked;
            placement.Enabled = !_busy && !hide.Checked; import.Enabled = clear.Enabled = !_busy && !hide.Checked;
            import.Text = _model is null ? "Import custom OBJ…" : "Replace custom OBJ…";
            load.Enabled = mesh.Enabled = !_busy && !hide.Checked && _model is null;
            _materials.Visible = _model is null; _customMaterials.Visible = _model is not null;
            _materials.Enabled = _customMaterials.Enabled = !_busy && !hide.Checked;
            materialHelp.Text = _model is null ? "Choose a material for each native mesh slot. Use native clears only that override." : "Choose a material for each imported OBJ slot. These assignments preview here and are baked with your custom model.";
            alignmentGroup.Visible = _reference.Visible = _model is not null;
            alignmentGroup.Enabled = _reference.Enabled = !_busy && !hide.Checked;
            source.Text = _model is null ? "Using a cooked mesh" : $"Custom model: {_model.SourceName}\nAlign in Placement · assign materials in Materials.";
            use.Text = _model is null ? "Use native item" : "Validate & use item"; use.Enabled = !_busy;
        }
        void Queue() { if (_busy) { _pending = true; return; } if (_folder is not null) { _timer.Stop(); _timer.Start(); } }
        _viewer.ItemWorkshopTransformChanged += change => { if (!_saving && _model is not null && !change.Effect) { ApplyViewerTransform(change); Queue(); } };
        async Task RefreshPreview()
        {
            if (_busy || _folder is null || _session is null) return; _busy = true; Enable();
            try
            {
                var model = ReadCustomModel(); var overrides = _materials.Overrides; var visible = !hide.Checked; var original = _reference.Checked && visible; var revision = ++_revision;
                if (model is null) await Task.Run(() => WeaponModelService.PreviewNative(_folder, overrides, visible, revision, _session));
                else await Task.Run(() => WeaponModelService.Preview(model, _folder, original, visible, revision, _session));
                _status.Text = hide.Checked ? "Visuals hidden; native behavior is preserved." : "Material preview updated. Save the parent editor and rebuild to apply in-game.";
            }
            catch (Exception ex) { _status.Text = "Preview unavailable; settings remain editable: " + ex.Message; await _viewer.NotifyItemWorkshopPreviewFailedAsync(_status.Text); }
            finally { _busy = false; Enable(); if (_pending) { _pending = false; Queue(); } }
        }
        async Task LoadPreview()
        {
            if (_busy) return; _timer.Stop(); _busy = true; Enable(); _status.Text = "Loading native mesh and materials…";
            try
            {
                var package = mesh.Text.Trim().Length > 0 ? mesh.Text.Trim() : binding.Identity.OriginalMeshPackage;
                var assignments = _materials.Overrides; _folder = await Task.Run(() => WeaponModelService.CreateViewer(package, binding.NativeMaterials));
                _session?.Dispose(); _session = new(_folder); _nativeSlots = WeaponModelService.NativeSlots(_folder); _materials.SetSlots(_nativeSlots, assignments);
                await _viewer.ShowFolderAsync(_folder);
            }
            catch (Exception ex) { _folder = null; _status.Text = "Preview unavailable; settings remain editable: " + ex.Message; }
            finally { _busy = false; Enable(); Queue(); }
        }
        import.Click += (_, _) =>
        {
            if (_busy) return;
            // Import into this editing session; there is no second workshop or preview export.
            using var picker = new OpenFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj", CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                _model = WeaponModelService.ImportObj(picker.FileName, ReadCustomModel(), _nativeSlots);
                SetCustomControls(_model); Enable(); Queue();
            }
            catch (Exception ex) { Dialog.Info(this, "Model could not be imported", ex.Message); }
        };
        clear.Click += (_, _) => { _model = null; SetCustomControls(null); Enable(); Queue(); }; load.Click += async (_, _) => await LoadPreview();
        resetAlignment.Click += (_, _) =>
        {
            var model = ReadCustomModel(); if (model is null) return;
            model.Scale = 1; model.X = model.Y = model.Z = model.Pitch = model.Yaw = model.Roll = 0;
            _model = model; SetCustomControls(model); Queue();
        };
        foreach (var n in _alignment) n.ValueChanged += (_, _) => { if (!_bindingAlignment) Queue(); };
        _customMaterials.Changed += () => Queue(); _reference.CheckedChanged += (_, _) => Queue();
        placement.CheckedChanged += (_, _) => Enable(); hide.CheckedChanged += (_, _) => { Enable(); Queue(); }; _materials.Changed += () => Queue();
        _timer.Tick += async (_, _) => { _timer.Stop(); await RefreshPreview(); };
        use.Click += async (_, _) =>
        {
            if (_busy) return;
            try
            {
                var e = Result.Clone(); e.Hide = hide.Checked; e.ReplacementMeshPackage = mesh.Text.Trim(); e.CustomModel = ReadCustomModel();
                e.Materials = _model is null ? _materials.Overrides.ToList() : [];
                var v = numbers.Select(n => (float)n.Value).ToArray(); e.Transform = placement.Checked ? new() { X = v[0], Y = v[1], Z = v[2], Pitch = v[3], Yaw = v[4], Roll = v[5], ScaleX = v[6], ScaleY = v[7], ScaleZ = v[8] } : null;
                var errors = NativeHeldItemService.Validate([e]); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
                if (e.CustomModel is { } model)
                {
                    _timer.Stop(); _busy = _saving = true; Enable(); _status.Text = "Validating cooked custom mesh…";
                    await _viewer.SetItemWorkshopEditingAsync(false);
                    var content = Path.Combine(_folder ?? Path.GetTempPath(), "Batcomputer-HeldBake-" + Guid.NewGuid().ToString("N"));
                    try { await Task.Run(() => WeaponModelService.Bake(model, AppSettings.Current.EffectiveExtractedContentRoot(), AppSettings.Current.EffectiveUsmapPath()!, content, "/Game/Mods/WeaponEditorValidation/SM_Weapon")); }
                    finally { if (Directory.Exists(content)) Directory.Delete(content, true); }
                }
                Result = e; _busy = false; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { Dialog.Warn(this, "Check native held item", ex.Message); }
            finally { _busy = _saving = false; if (!IsDisposed && DialogResult != DialogResult.OK) { await _viewer.SetItemWorkshopEditingAsync(true); Enable(); if (_pending) { _pending = false; Queue(); } } }
        };
        Shown += async (_, _) => { Theme.UseDarkTitleBar(this); await LoadPreview(); };
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        FormClosed += (_, _) => { _timer.Dispose(); _session?.Dispose(); }; Enable();
    }

    internal void ApplyViewerTransform(ItemWorkshopTransform change)
    {
        if (_model is null || _saving || change.Effect || change.Revision != _revision) return;
        _bindingAlignment = true;
        try { ItemWorkshopTransform.Bind(_alignment, new[] { change.Scale }.Concat(change.Offset).Concat(change.Rotation)); }
        finally { _bindingAlignment = false; }
    }

    private void SetCustomControls(WeaponModelRecipe? model)
    {
        _bindingAlignment = true;
        try
        {
            var r = model ?? new WeaponModelRecipe(); var values = new[] { r.Scale, r.X, r.Y, r.Z, r.Pitch, r.Yaw, r.Roll };
            for (var i = 0; i < values.Length; i++) _alignment[i].Value = float.IsFinite(values[i]) ? (decimal)Math.Clamp(values[i], (float)_alignment[i].Minimum, (float)_alignment[i].Maximum) : _alignment[i].Minimum;
            var slots = model?.Materials ?? [];
            _customMaterials.SetSlots(slots.Select(m => (m.Slot, m.SourceMaterialName, m.MaterialPath)), slots.Select(m => new NativeHeldMaterialOverride { Slot = m.Slot, Package = m.MaterialPath }));
        }
        finally { _bindingAlignment = false; }
    }

    internal WeaponModelRecipe? ReadCustomModel()
    {
        if (_model is null) return null;
        var r = _model.Clone(); var values = _alignment.Select(n => (float)n.Value).ToArray();
        r.Scale = values[0]; r.X = values[1]; r.Y = values[2]; r.Z = values[3]; r.Pitch = values[4]; r.Yaw = values[5]; r.Roll = values[6];
        var assignments = _customMaterials.Overrides.ToDictionary(m => m.Slot, m => m.Package);
        foreach (var material in r.Materials) material.MaterialPath = assignments.GetValueOrDefault(material.Slot) ?? "";
        WeaponModelService.Validate(r); return r;
    }
}
