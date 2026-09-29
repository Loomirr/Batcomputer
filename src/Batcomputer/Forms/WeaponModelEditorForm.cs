namespace Batcomputer;

/// <summary>Private editing session. Only an accepted, baked recipe reaches suit settings.</summary>
public partial class WeaponModelEditorForm : AdaptiveForm
{
    private readonly ModelPreviewControl _viewer = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label _source = new() { AutoSize = true, MaximumSize = new(340, 0), ForeColor = Theme.OnDark };
    private readonly ItemMaterialSlotsControl _materials = new();
    private readonly ItemInspectorPages _inspector = new();
    private readonly ItemMaterialSlotsControl _nativeMaterials = new(true);
    private string _referencePackage;
    private readonly List<NumericUpDown> _numbers = [];
    private readonly CheckBox _original = new() { Text = "Show native reference", Checked = true, AutoSize = true };
    private readonly CheckBox _custom = new() { Text = "Show custom model", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 350 };
    private readonly Button _save;
    private WeaponModelRecipe? _working;
    private string? _folder;
    private ModelPreviewService.ItemMaterialPreviewSession? _materialSession;
    private IReadOnlyList<(int Slot, string Name, string Package)> _nativeSlots = [];
    private int _revision;
    private bool _busy, _refreshPending, _binding, _saving;
    public WeaponModelRecipe? Result { get; private set; }

    public WeaponModelEditorForm(string reference, WeaponModelRecipe? existing, bool equipment = false, HeldItemSettings? extraProp = null)
    {
        _referencePackage = reference; _working = existing?.Clone(); _prop = extraProp?.Clone(); PropResult = extraProp?.Clone();
        // Older prop drafts could override slot 0 after baking an OBJ. Move that effective
        // assignment into this private model draft so the Materials panel edits what is built.
        if (_prop is { MaterialPackage.Length: > 0 } && _working?.Materials.FirstOrDefault(m => m.Slot == 0) is { } surface)
        { surface.MaterialPath = _prop.MaterialPackage; _prop.MaterialPackage = ""; }
        Text = extraProp is not null ? "Batcomputer — Extra held prop" : equipment ? "Batcomputer — Equipment model workshop" : "Batcomputer — Weapon workshop";
        ClientSize = new(1280, 850); MinimumSize = new(1000, 680); StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        Icon = EmbeddedAssets.LoadIcon(Theme.CurrentVisualTheme.IconAsset) ?? Icon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 78)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(root);
        root.Controls.Add(ItemWorkshopUi.Header(extraProp is not null ? "Extra held prop" : equipment ? "Equipment model workshop" : "Weapon workshop", "Set up your model, placement and materials in one workspace. Nothing changes in-game until you save and rebuild."));
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1248, 680), SplitterDistance = 840, SplitterWidth = 8, Panel1MinSize = 380, Panel2MinSize = 360, BackColor = Theme.WindowBg };
        split.Panel1.Controls.Add(_viewer); var pages = _inspector; split.Panel2.Controls.Add(pages); root.Controls.Add(split, 0, 1);
        var model = ItemInspectorPages.ScrollPage(); var alignment = ItemInspectorPages.ScrollPage();
        if (_prop is not null) pages.Add("Prop", BuildPropPage());
        var materialHost = new Panel { Dock = DockStyle.Fill }; materialHost.Controls.Add(_materials); materialHost.Controls.Add(_nativeMaterials);
        pages.Add("Model", model); pages.Add("Align", alignment); pages.Add("Materials", materialHost);
        var import = ItemWorkshopUi.Button("Import OBJ…", true); import.Width = 305; import.Click += (_, _) => Import();
        var remove = ItemWorkshopUi.Button("Use original model"); remove.Click += (_, _) => { _working = null; FillMaterials(); Queue(); };
        model.Controls.Add(ItemWorkshopUi.Section("Model source", "Keep the original or import your own OBJ. Importing never changes the original's size or hides it.", import, _source, remove));
        _original.Text = "Original model"; _custom.Text = "Your imported model";
        model.Controls.Add(ItemWorkshopUi.Section("Compare models", "The original starts faded so the overlap is readable. Change its appearance in the viewer. Visibility choices stay as you set them.", _original, _custom));
        model.Controls.Add(ItemWorkshopUi.Section("Camera ≠ model size", "Frame custom / original / both only changes the camera. Size · E changes the mesh that will be baked. Match original size is optional—not automatic."));
        if (_prop is not null) AddPropAdvancedModelControls(model);
        model.Controls.Add(ItemWorkshopUi.Section("Apply when ready", equipment ? "Collision and projectile behavior remain native. Save the parent editor and rebuild after applying." : "Attack timing, sockets and hitboxes remain native. Save the parent editor and rebuild after applying."));
        alignment.Controls.Add(ItemWorkshopUi.Note("Mesh alignment\nDrag the 3D handles: W move · R rotate · E size. Switch World / Local axes or enable Snap in the viewer. Sizing is uniform.\nFields use Unreal centimeters and degrees. The gizmo follows the centered OBJ, not the hand grip; native attack sockets and collision are unchanged."));
        foreach (var (name, min, max) in new[] { ("Uniform scale", .001m, 1000m), ("Position X (cm)", -10000m, 10000m), ("Position Y (cm)", -10000m, 10000m), ("Position Z (cm)", -10000m, 10000m), ("Pitch (°)", -360m, 360m), ("Yaw (°)", -360m, 360m), ("Roll (°)", -360m, 360m) })
        {
            var row = new TableLayoutPanel { Width = 330, Height = 44, ColumnCount = 2 }; row.ColumnStyles.Add(new(SizeType.Percent, 45)); row.ColumnStyles.Add(new(SizeType.Percent, 55));
            row.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            var n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = 3, Increment = .1m, BackColor = Theme.PanelBg, ForeColor = Theme.OnDark, Value = min > 0 ? 1 : 0 };
            row.Controls.Add(n, 1, 0); alignment.Controls.Add(row); _numbers.Add(n); n.ValueChanged += (_, _) => { if (!_binding) Queue(); };
        }
        var reset = ItemWorkshopUi.Button("Reset alignment"); alignment.Controls.Add(reset);
        var units = new ThemedDropDown { Name = "ImportUnits", Width = 305, Height = 36 };
        units.Items.AddRange(["Centimeters · scale 1", "Meters / Blender units · scale 100", "Millimeters · scale 0.1", "Inches · scale 2.54"]); units.SelectedIndex = 0;
        var applyUnits = ItemWorkshopUi.Button("Set import scale"); applyUnits.Name = "ImportUnitsApply";
        applyUnits.Click += (_, _) => { if (_working is not null && !_busy && units.SelectedIndex >= 0) _numbers[0].Value = new[] { 1m, 100m, .1m, 2.54m }[units.SelectedIndex]; };
        alignment.Controls.Add(ItemWorkshopUi.Section("Import size help", "OBJ files do not store units. If a Blender model appears tiny, use meters as a starting scale. This button replaces your size multiplier; camera framing never does.", units, applyUnits));
        reset.Click += (_, _) => { SetNumbers(new()); Queue(); }; SetNumbers(_working ?? new());
        _materials.Changed += () => Queue(); _original.CheckedChanged += (_, _) => Queue(); _custom.CheckedChanged += (_, _) => Queue();
        _save = ItemWorkshopUi.Button(_prop is not null ? "Use prop" : "Validate & use model", true); var cancel = ItemWorkshopUi.Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        if (_prop is not null) { _save.Name = "PropApplyButton"; AcceptButton = _save; }
        root.Controls.Add(ItemWorkshopUi.Footer(_status, cancel, _save), 0, 2);
        CancelButton = cancel; _save.Click += async (_, _) => await SaveAsync(); FillMaterials();
        _timer.Tick += async (_, _) => { _timer.Stop(); await RefreshAsync(); };
        _viewer.ItemWorkshopTransformChanged += ApplyViewerTransform;
        _nativeMaterials.Changed += () => { if (_prop is not null) { _prop.MaterialPackage = _nativeMaterials.Overrides.FirstOrDefault(m => m.Slot == 0)?.Package ?? ""; Queue(); } };
        Shown += async (_, _) => { Theme.UseDarkTitleBar(this); await LoadReferenceAsync(); };
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Please wait for the current operation to finish."; } };
        FormClosed += (_, _) => { _timer.Dispose(); _materialSession?.Dispose(); };
    }
    private async Task LoadReferenceAsync()
    {
        if (_busy) return; _timer.Stop(); _busy = true; _save.Enabled = _inspector.Enabled = false; _status.Text = "Loading original model and materials…";
        try
        {
            var reference = _referencePackage; var overrides = _prop is { MaterialPackage.Length: > 0 } ? new[] { new NativeHeldMaterialOverride { Slot = 0, Package = _prop.MaterialPackage } } : null;
            var folder = await Task.Run(() => WeaponModelService.CreateViewer(reference));
            _materialSession?.Dispose(); _folder = folder; _nativeSlots = WeaponModelService.NativeSlots(folder); _materialSession = new(folder);
            _nativeMaterials.SetSlots(_nativeSlots.Where(s => s.Slot == 0), overrides ?? []);
            await _viewer.ShowFolderAsync(folder); _status.Text = "Original stays at game size. Import an OBJ, then compare and align it.";
        }
        catch (Exception ex)
        {
            _folder = null; _materialSession?.Dispose(); _materialSession = null; _nativeSlots = [];
            _status.Text = "Original preview unavailable; settings stay editable: " + ex.Message;
            _viewer.ShowMessage(_status.Text); // Never display the previous donor as the newly chosen model.
        }
        finally { _busy = false; _save.Enabled = _prop is not null || (_folder is not null && _working is not null); _inspector.Enabled = true; FillMaterials(); Queue(); }
    }
    internal void ApplyViewerTransform(ItemWorkshopTransform change)
    {
        if (_saving || _working is null || change.Effect) return;
        if (change.Revision == _revision)
        {
            _binding = true;
            try { ItemWorkshopTransform.Bind(_numbers, new[] { change.Scale }.Concat(change.Offset).Concat(change.Rotation)); }
            finally { _binding = false; }
        }
        // A rejected stale gesture also republishes the current fields to unlock/resync the viewer.
        Queue();
    }
    private void SetNumbers(WeaponModelRecipe r)
    {
        _binding = true; var v = new[] { r.Scale, r.X, r.Y, r.Z, r.Pitch, r.Yaw, r.Roll };
        for (var i = 0; i < v.Length; i++) _numbers[i].Value = float.IsFinite(v[i]) ? (decimal)Math.Clamp(v[i], (float)_numbers[i].Minimum, (float)_numbers[i].Maximum) : _numbers[i].Minimum;
        _binding = false;
    }
    private void Queue() { if (_busy) { _refreshPending = true; return; } if (_folder is not null) { _timer.Stop(); _timer.Start(); } }
    private void FillMaterials()
    {
        var slots = _working?.Materials ?? [];
        _materials.SetSlots(slots.Select(m => (m.Slot, m.SourceMaterialName, m.MaterialPath)), slots.Select(m => new NativeHeldMaterialOverride { Slot = m.Slot, Package = m.MaterialPath }));
        _materials.Visible = _prop is null || _working is not null; _nativeMaterials.Visible = _prop is not null && _working is null;
        _source.Text = _working is null ? $"Original: {UnrealPathUtil.AssetName(_referencePackage)}" : $"Your model: {_working.SourceName}\nOriginal: {UnrealPathUtil.AssetName(_referencePackage)}\n{slots.Count} model surface(s) · check import units in Align";
        _save.Text = _prop is not null ? (_working is null ? "Use prop" : "Validate & use prop") : "Validate & use model";
    }
    private void Import()
    {
        if (_busy) return;
        using var picker = new OpenFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var defaults = _nativeSlots.Select(s => (s.Slot, s.Name, Package: _nativeMaterials.Overrides.FirstOrDefault(m => m.Slot == s.Slot)?.Package ?? s.Package)).ToArray();
            _working = WeaponModelService.ImportObj(picker.FileName, _working is null ? null : ReadRecipe(), defaults);
            if (_prop is not null)
            {
                _prop.MaterialPackage = ""; // Imported slots now own their material assignments.
                _nativeMaterials.SetSlots(_nativeSlots.Where(s => s.Slot == 0), []);
            }
            FillMaterials(); SetNumbers(_working); _inspector.Select("Align"); Queue();
        }
        catch (Exception ex) { Dialog.Info(this, "Model could not be imported", ex.Message); }
    }
    internal WeaponModelRecipe ReadRecipe()
    {
        if (_working is null) throw new InvalidOperationException("Import an OBJ first.");
        var r = _working.Clone(); var v = _numbers.Select(n => (float)n.Value).ToArray();
        r.Scale = v[0]; r.X = v[1]; r.Y = v[2]; r.Z = v[3]; r.Pitch = v[4]; r.Yaw = v[5]; r.Roll = v[6];
        var assignments = _materials.Overrides.ToDictionary(m => m.Slot, m => m.Package);
        foreach (var m in r.Materials) m.MaterialPath = assignments.GetValueOrDefault(m.Slot) ?? "";
        WeaponModelService.Validate(r); return r;
    }
    private async Task RefreshAsync()
    {
        if (_busy || _folder is null) return; _busy = true; _save.Enabled = false;
        try
        {
            var original = _original.Checked; var custom = _custom.Checked; var revision = ++_revision;
            if (_working is null && _prop is not null && _materialSession is not null) await Task.Run(() => WeaponModelService.PreviewNative(_folder, _nativeMaterials.Overrides, original, revision, _materialSession));
            else if (_working is null) AtomicFileUtil.WriteAllText(Path.Combine(_folder, "weapon.json"), System.Text.Json.JsonSerializer.Serialize(new { original, custom, revision }));
            else { var r = ReadRecipe(); await Task.Run(() => WeaponModelService.Preview(r, _folder, original, custom, revision, _materialSession)); _status.Text = $"{r.SourceName} · materials and alignment updated"; }
        }
        catch (Exception ex) { _status.Text = "Preview: " + ex.Message; await _viewer.NotifyItemWorkshopPreviewFailedAsync(_status.Text); }
        finally { _busy = false; _save.Enabled = _working is not null || _prop is not null; if (_refreshPending) { _refreshPending = false; Queue(); } }
    }
    private async Task SaveAsync()
    {
        if (_busy || (_folder is null && _prop is null)) return; _timer.Stop();
        try
        {
            var r = _working is null ? null : ReadRecipe();
            var prop = _prop is null ? null : ReadProp(r); if (prop is not null) { var errors = HeldItemService.Validate([prop]); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors)); }
            if (r is null && prop is null) throw new InvalidOperationException("Import a model first.");
            _busy = _saving = true; _save.Enabled = _inspector.Enabled = false; _status.Text = "Validating model and settings…";
            await _viewer.SetItemWorkshopEditingAsync(false);
            if (r is not null) { var content = Path.Combine(_folder ?? Path.GetTempPath(), "Batcomputer-ItemBake", Guid.NewGuid().ToString("N")); await Task.Run(() => WeaponModelService.Bake(r, AppSettings.Current.EffectiveExtractedContentRoot(), AppSettings.Current.EffectiveUsmapPath()!, content, "/Game/Mods/WeaponEditorValidation/SM_Weapon")); }
            Result = r; if (prop is not null) PropResult = prop; _busy = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { _status.Text = "Bake failed: " + ex.Message; }
        finally { _busy = _saving = false; if (!IsDisposed) { _save.Enabled = _inspector.Enabled = true; await _viewer.SetItemWorkshopEditingAsync(true); if (_refreshPending) { _refreshPending = false; Queue(); } } }
    }
}
