namespace Batcomputer;

internal sealed class VehicleEditorForm : AdaptiveForm
{
    private readonly VehicleProjectService _service;
    private readonly string _projectRoot;
    private readonly VehicleProject _working;
    private readonly string _native;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly ThemedDropDown _owner = new() { Dock = DockStyle.Fill };
    private ThemedDropDown _donorSelector = null!;
    private readonly ListBox _components = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly NumericUpDown[] _transform = new NumericUpDown[9];
    private readonly Label _modelStatus = new() { Dock = DockStyle.Fill };
    private readonly Label _attachmentStatus = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _iconPreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.PanelBg };
    private readonly Label _iconStatus = new() { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted };
    private readonly DataGridView _materials = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false };
    private bool _busy;
    private VehicleAssetService.Component? _displayedComponent;
    private bool _loadingTransform;
    private bool _transformDirty;
    private readonly List<VehicleAssetService.Component> _availableComponents = [];
    private System.Media.SoundPlayer? _soundPreviewPlayer;
    internal VehicleProject? Result { get; private set; }
    private sealed record OwnerChoice(string Label, string Tag, string ProjectPath = "") { public override string ToString() => Label; }

    internal VehicleEditorForm(string projectRoot, VehicleProject project, bool settingsOnly = false)
    {
        _service = new(projectRoot); _projectRoot = projectRoot; _working = project.Clone(); _native = AppSettings.Current.EffectiveExtractedContentRoot();
        Text = "Batcomputer — Vehicle setup"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1100, 720); MinimumSize = new Size(900, 620); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        FormClosed += (_, _) => _soundPreviewPlayer?.Dispose();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 60)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 56)); Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "VEHICLE SETUP\n" + project.DisplayName, Dock = DockStyle.Fill, ForeColor = Theme.Gold }, 0, 0);
        var tabs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        tabs.ColumnStyles.Add(new(SizeType.Absolute, 190)); tabs.ColumnStyles.Add(new(SizeType.Percent, 100)); tabs.RowStyles.Add(new(SizeType.Percent, 100)); layout.Controls.Add(tabs, 0, 1);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 0, 12, 0) };
        var pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; tabs.Controls.Add(navigation, 0, 0); tabs.Controls.Add(pages, 1, 0);
        var sections = new List<(Panel Page, Button Button)>();
        Panel Page(string title)
        {
            var p = new Panel { Name = title, Dock = DockStyle.Fill, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, Padding = new Padding(14), AutoScroll = true, Visible = sections.Count == 0 };
            var button = ActionButton(title, (_, _) => { foreach (var section in sections) { section.Page.Visible = section.Page == p; section.Button.ForeColor = section.Page == p ? Theme.Gold : Theme.OnDark; } p.BringToFront(); });
            button.Width = 170; button.Height = 46; button.TextAlign = ContentAlignment.MiddleLeft; button.Padding = new Padding(10, 0, 0, 0); button.ForeColor = sections.Count == 0 ? Theme.Gold : Theme.OnDark;
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
        Field("Character", _owner);
        var drivingBase = new ThemedDropDown { Dock = DockStyle.Fill, Enabled = _working.Model is null && _working.SummonModel is null && _working.Transforms.Count == 0 && _working.MaterialOverrides.Count == 0 && VehicleLightService.HasOnlyDefaultDisabledLamps(_working, _native) && _working.Lights.Count == 0 && _working.AccentColor is null && _working.BoostColor is null && _working.BoostStyle.Length == 0 && !_working.TwinExhausts };
        _donorSelector = drivingBase; drivingBase.Enabled &= _working.ToyboxParts.Count == 0;
        foreach (var donor in VehicleDonorService.All) drivingBase.Items.Add(donor);
        drivingBase.SelectedItem = VehicleDonorService.Get(_working);
        var donorNote = new Label { Text = VehicleDonorService.Get(_working).Notes, Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted };
        drivingBase.SelectedIndexChanged += async (_, _) => {
            if (drivingBase.SelectedItem is not VehicleDonorService.Donor donor || donor.Id == _working.DonorId) return;
            if (_busy) { drivingBase.SelectedItem = VehicleDonorService.Get(_working); return; }
            if (!donor.IsAvailable(AppSettings.Current.EffectiveExtractedContentRoot())) {
                Dialog.Info(this, "Driving base unavailable", donor.UnavailableMessage);
                drivingBase.SelectedItem = VehicleDonorService.Get(_working); return;
            }
            _working.DonorId = donor.Id; donorNote.Text = donor.Notes;
            _working.DisabledParts.Clear();
            VehicleLightService.DisableLampMeshes(_working, _native);
            if (!settingsOnly) await LoadDonorComponents();
        };
        Field("Driving base", drivingBase);
        Field("", donorNote, 60);
        Field("Vehicle ID", new TextBox { Text = project.Id, ReadOnly = true, Dock = DockStyle.Fill });
        var size = new NumericUpDown { Minimum = 50, Maximum = 200, Value = (decimal)_working.SizeMultiplier * 100, DecimalPlaces = 0, Increment = 5, Dock = DockStyle.Fill };
        size.ValueChanged += (_, _) => _working.SizeMultiplier = (float)size.Value / 100;
        Field("Size % (experimental)", size);
        Field("", new Label { Text = "Scales the vehicle and attached parts. Check wheels, seats and collision in game.", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted }, 44);
        var boostStyles = new ThemedDropDown { Dock = DockStyle.Fill };
        boostStyles.Items.Add("Driving base boost");
        foreach (var style in VehicleExhaustService.Styles) boostStyles.Items.Add(style);
        boostStyles.SelectedItem = VehicleExhaustService.FindStyle(_working.BoostStyle) ?? (object)"Driving base boost";
        var twin = new CheckBox { Text = "Two boost outlets (second outlet can be moved in the workshop)", Dock = DockStyle.Fill, Checked = _working.TwinExhausts, ForeColor = Theme.OnDark };
        twin.CheckedChanged += (_, _) => {
            if (VehicleExhaustService.FindStyle(_working.BoostStyle)?.Twin == true && !twin.Checked) { twin.Checked = true; return; }
            _working.TwinExhausts = twin.Checked;
            if (!twin.Checked) _working.Transforms.RemoveAll(t => t.Component == "socket:" + VehicleExhaustService.SecondSocket);
            drivingBase.Enabled = false;
        };
        boostStyles.SelectedIndexChanged += (_, _) => {
            _working.BoostStyle = boostStyles.SelectedItem is VehicleExhaustService.Style style ? style.Id : "";
            if (VehicleExhaustService.FindStyle(_working.BoostStyle)?.Twin == true) twin.Checked = true;
            twin.Enabled = VehicleExhaustService.FindStyle(_working.BoostStyle)?.Twin != true;
            drivingBase.Enabled = false;
        };
        twin.Enabled = VehicleExhaustService.FindStyle(_working.BoostStyle)?.Twin != true;
        Field("Boost style", boostStyles);
        Field("", twin, 44);
        void BoostSpeedField(string label, float? current, decimal native, decimal maximum, Action<float?> set)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var custom = new CheckBox { Text = "Custom", AutoSize = true, Checked = current is not null, ForeColor = Theme.OnDark, Margin = new Padding(0, 12, 14, 0) };
            var value = new NumericUpDown { Minimum = 1, Maximum = maximum, DecimalPlaces = 1, Increment = 1, Width = 110,
                Value = Math.Clamp((decimal)(current ?? (float)native), 1, maximum), Enabled = current is not null };
            custom.CheckedChanged += (_, _) => { value.Enabled = custom.Checked; set(custom.Checked ? (float)value.Value : null); };
            value.ValueChanged += (_, _) => { if (custom.Checked) set((float)value.Value); };
            row.Controls.Add(custom); row.Controls.Add(value);
            row.Controls.Add(new Label { Text = custom.Checked ? "Applies only while boosting" : "Native boost curve", AutoSize = true, ForeColor = Theme.OnDarkMuted, Margin = new Padding(12, 12, 0, 0) });
            Field(label, row, 46);
        }
        BoostSpeedField("Boost speed (mph)", _working.BoostTopSpeedMph, 90, 500, value => _working.BoostTopSpeedMph = value);
        BoostSpeedField("Boost acceleration (G)", _working.BoostAccelerationGs, 6, 50, value => _working.BoostAccelerationGs = value);
        var boostRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var boostStatus = new Label { AutoSize = true, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(12, 10, 0, 0) };
        var boostChoose = ActionButton("Choose color…", (_, _) =>
        {
            using var picker = new ColorDialog { FullOpen = true, Color = _working.BoostColor is { } c ? Color.FromArgb(c.R, c.G, c.B) : Color.Cyan };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            _working.BoostColor = new VehicleRgbColor { R = picker.Color.R, G = picker.Color.G, B = picker.Color.B };
            boostStatus.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2} · test in game";
            drivingBase.Enabled = false;
        });
        boostRow.Controls.Add(boostChoose);
        boostRow.Controls.Add(ActionButton("Use native", (_, _) => { _working.BoostColor = null; boostStatus.Text = "Native flame"; }));
        boostStatus.Text = _working.BoostColor is { } boost ? $"#{boost.R:X2}{boost.G:X2}{boost.B:X2} · test in game" : "Native flame";
        boostRow.Controls.Add(boostStatus);
        boostChoose.Enabled = _working.DonorId == VehicleBoostColorService.SupportedDonor && _working.BoostStyle.Length == 0 && !_working.TwinExhausts;
        Field("Boost flame", boostRow, 50);
        Field("", new Label { Text = "Forever Batmobile only. Gives this custom car its own boost flame, start and cancel effects. Check the result in game.", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted }, 52);
        drivingBase.SelectedIndexChanged += (_, _) => boostChoose.Enabled = _working.DonorId == VehicleBoostColorService.SupportedDonor && _working.BoostStyle.Length == 0 && !_working.TwinExhausts;
        boostStyles.SelectedIndexChanged += (_, _) => boostChoose.Enabled = _working.DonorId == VehicleBoostColorService.SupportedDonor && _working.BoostStyle.Length == 0 && !_working.TwinExhausts;
        twin.CheckedChanged += (_, _) => boostChoose.Enabled = _working.DonorId == VehicleBoostColorService.SupportedDonor && _working.BoostStyle.Length == 0 && !_working.TwinExhausts;
        var sounds = new ThemedDropDown { Dock = DockStyle.Fill };
        sounds.Items.Add("Driving base sounds");
        foreach (var style in VehicleSoundService.Styles) sounds.Items.Add(style);
        sounds.SelectedItem = VehicleSoundService.FindStyle(_working.SoundStyle) ?? (object)"Driving base sounds";
        sounds.SelectedIndexChanged += (_, _) => _working.SoundStyle = sounds.SelectedItem is VehicleSoundService.Style style ? style.Id : "";
        Field("Sound style", sounds);
        var previewRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var soundSlots = new ThemedDropDown { Width = 220 };
        Button listen = null!;
        listen = ActionButton("▶ Listen", async (_, _) => {
            if (soundSlots.SelectedItem is not VehicleSoundService.Slot slot) return;
            try
            {
                listen.Enabled = false;
                var result = await Task.Run(() => {
                    using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
                    return VehicleSoundPreviewService.Wav(provider, slot.Event, Path.Combine(AppSettings.GeneratedRootFor(_projectRoot), "SoundPreview"));
                });
                if (result.Wav is null) Dialog.Info(this, "Sound preview", result.Problem ?? "No playable audio was found.");
                else { _soundPreviewPlayer?.Dispose(); _soundPreviewPlayer = new System.Media.SoundPlayer(result.Wav); _soundPreviewPlayer.Play(); }
            }
            catch (Exception ex) { Dialog.Info(this, "Sound preview", ex.Message); }
            finally { listen.Enabled = true; }
        });
        void RefreshSoundSlots()
        {
            var selected = (soundSlots.SelectedItem as VehicleSoundService.Slot)?.Label;
            soundSlots.Items.Clear();
            try
            {
                var slots = VehicleSoundService.FindStyle(_working.SoundStyle) is { } style
                    ? VehicleSoundService.Slots(style)
                    : VehicleSoundService.DonorSlots(VehicleAssetService.Read(_native, VehicleDonorService.Get(_working).Blueprint));
                foreach (var slot in slots) soundSlots.Items.Add(slot);
                soundSlots.SelectedItem = soundSlots.Items.Cast<VehicleSoundService.Slot>().FirstOrDefault(s => s.Label == selected)
                    ?? soundSlots.Items.Cast<VehicleSoundService.Slot>().FirstOrDefault();
            }
            catch { /* Missing extraction does not prevent editing the sound style. */ }
            listen.Enabled = soundSlots.Items.Count > 0;
        }
        previewRow.Controls.Add(soundSlots); previewRow.Controls.Add(listen);
        Field("Preview sound", previewRow);
        sounds.SelectedIndexChanged += (_, _) => RefreshSoundSlots();
        drivingBase.SelectedIndexChanged += (_, _) => RefreshSoundSlots();
        RefreshSoundSlots();
        Field("", new Label { Text = "Swaps engine, ignition, shutdown and gear sounds together. Boost and spawn audio stay native when the style has no replacement.", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted }, 52);
        Field("", new Label { Text = "Stable ID. Custom character owners are included when building.", Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted }, 52);

        var model = Page("Body & materials");
        var modelLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        modelLayout.ColumnStyles.Add(new(SizeType.Percent, 100)); modelLayout.RowStyles.Add(new(SizeType.Absolute, 62)); modelLayout.RowStyles.Add(new(SizeType.Absolute, 46)); modelLayout.RowStyles.Add(new(SizeType.Percent, 100)); modelLayout.RowStyles.Add(new(SizeType.Absolute, 50)); model.Controls.Add(modelLayout);
        modelLayout.Controls.Add(_modelStatus, 0, 0);
        modelLayout.Controls.Add(Button("Import / edit rigged FBX…", (_, _) => EditModel()), 0, 1);
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", ReadOnly = true, Width = 160 });
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Material / paint shader", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Color override", ReadOnly = true, Width = 125 }); Theme.StyleGrid(_materials); modelLayout.Controls.Add(_materials, 0, 2);
        var finishColumn = new DataGridViewComboBoxColumn { HeaderText = "Paint finish", Width = 126, FlatStyle = FlatStyle.Flat };
        finishColumn.Items.AddRange("Material", "Flat", "Solid", "Metallic", "Transparent"); _materials.Columns.Add(finishColumn);
        _materials.CellEndEdit += (_, e) => {
            if (e.ColumnIndex != 3 || e.RowIndex < 0) return;
            var finish = _materials.Rows[e.RowIndex].Cells[3].Value?.ToString() ?? "";
            if (VehiclePaintService.ValidFinish(finish)) _materials.Rows[e.RowIndex].Cells[1].Value = PaintShader(finish);
        };
        var materialActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        modelLayout.RowStyles[3].Height = 88;
        materialActions.Controls.Add(ActionButton("Set slot color…", (_, _) => SetColor()));
        materialActions.Controls.Add(ActionButton("Make editable copies", async (_, _) => await MakeMaterialCopies()));
        materialActions.Controls.Add(ActionButton("Use slot material", (_, _) => { if (_materials.CurrentRow is { } row) { ReadMaterials(); _working.Palette.RemoveAll(c => c.Slot == row.Index); FillModel(); } }));
        materialActions.Controls.Add(ActionButton("Upgrade simple colors…", (_, _) => {
            if (!Dialog.Confirm(this, "Use native LEGO paint?", "Convert simple color overrides to the native solid LEGO shader. Colors stay the same. Choose Metallic or Transparent per slot afterward. Red Brick behavior still needs an in-game check.", confirmText: "Convert colors")) return;
            ReadMaterials(); foreach (var color in _working.Palette.Where(c => c.Finish == "Flat")) color.Finish = "Solid"; FillModel();
        }));
        materialActions.Controls.Add(ActionButton("Restore native body", (_, _) => { if (Dialog.Confirm(this, "Restore native body?", "This removes the model replacement and its light-surface assignments. Imported source files are kept.", confirmText: "Restore")) { _working.Model = null; _working.LightSurfaces.Clear(); _working.Palette.Clear(); _working.MaterialOverrides.RemoveAll(m => m.Component == "body"); FillModel(); } }));
        modelLayout.Controls.Add(materialActions, 0, 3); FillModel();

        var summonPage = Page("Assembly model");
        var summonStatus = new Label { Dock = DockStyle.Top, Height = 70, Text = _working.SummonModel is null ? "Optional. Use the donor's separate summon rig for the build-up / parked model." : _working.SummonModel.Name };
        var summonImport = ActionButton("Import / edit assembly FBX…", (_, _) => {
            var donor = VehicleDonorService.Get(_working);
            var recipe = _working.SummonModel?.Clone() ?? new SkinnedMeshImport { Name = _name.Text + " assembly", Component = "Summon", DonorMeshPackage = donor.SummonMesh, MeshPackage = VehicleProjectService.Mesh(_working) + "_Summon" };
            using var workshop = new SkinnedMeshWorkshopForm(_service.DirectoryFor(_working), [new("Summon", donor.SummonMesh)], [], recipe, _working.SummonModel is not null, vehicle: true);
            if (workshop.ShowDialog(this) == DialogResult.OK && workshop.Result is { } imported) { _working.SummonModel = imported; summonStatus.Text = imported.Name; drivingBase.Enabled = false; }
        });
        summonImport.Dock = DockStyle.Top; summonPage.Controls.Add(summonImport); summonPage.Controls.Add(summonStatus);
        var matchBody = new CheckBox { Text = "Use matching body materials", Checked = _working.MatchBuildUpToBody, Dock = DockStyle.Top, Height = 40, ForeColor = Theme.OnDark };
        matchBody.CheckedChanged += (_, _) => _working.MatchBuildUpToBody = matchBody.Checked;
        var nativeAssembly = ActionButton("Use native assembly", (_, _) => {
            if (_working.SummonModel is null || !Dialog.Confirm(this, "Use native assembly?", "Remove this assembly replacement? Its imported files are kept.", confirmText: "Use native")) return;
            _working.SummonModel = null; summonStatus.Text = "Using the driving base's native build-up / parked model.";
        });
        nativeAssembly.Dock = DockStyle.Top; summonPage.Controls.Add(nativeAssembly); nativeAssembly.BringToFront();
        summonPage.Controls.Add(matchBody); matchBody.BringToFront();
        var icon = Page("Menu icon");
        var iconLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        iconLayout.ColumnStyles.Add(new(SizeType.Percent, 100)); iconLayout.RowStyles.Add(new(SizeType.Absolute, 55)); iconLayout.RowStyles.Add(new(SizeType.Percent, 100)); iconLayout.RowStyles.Add(new(SizeType.Absolute, 50)); icon.Controls.Add(iconLayout);
        iconLayout.Controls.Add(_iconStatus, 0, 0); iconLayout.Controls.Add(_iconPreview, 0, 1);
        var iconActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var importIcon = ActionButton("Import PNG…", async (_, _) => await ImportIcon()); Theme.StyleGoldButton(importIcon);
        iconActions.Controls.Add(importIcon); iconActions.Controls.Add(ActionButton("Use donor icon", (_, _) => { if (!_busy) { _working.MenuIcon = null; FillIcon(); } }));
        iconLayout.Controls.Add(iconActions, 0, 2); FillIcon();
        FormClosed += (_, _) => { _iconPreview.Image?.Dispose(); _iconPreview.Image = null; };

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
        if (!settingsOnly) Shown += async (_, _) => await LoadDonorComponents();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var save = ActionButton(settingsOnly ? "Apply setup" : "Save vehicle", (_, _) => Save()); Theme.StyleGoldButton(save);
        var cancel = ActionButton("Cancel", (_, _) => Close()); cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(save); footer.Controls.Add(cancel); layout.Controls.Add(footer, 0, 2); CancelButton = cancel;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        void StyleFields(Control parent) { foreach (Control c in parent.Controls) { if (c is TextBoxBase or NumericUpDown or ComboBox) { c.BackColor = Theme.PanelBg; c.ForeColor = Theme.OnDark; } if (c is TextBox text) text.BorderStyle = BorderStyle.FixedSingle; if (c is NumericUpDown numeric) numeric.BorderStyle = BorderStyle.FixedSingle; StyleFields(c); } } StyleFields(this);
    }
    private async Task LoadDonorComponents()
    {
        _busy = true;
        var enabled = _donorSelector?.Enabled ?? false;
        if (_donorSelector is not null) _donorSelector.Enabled = false;
        _transformDirty = false; _displayedComponent = null;
        _availableComponents.Clear(); _components.Items.Clear();
        _attachmentStatus.Text = "Reading driving-base attachments…";
        try
        {
            var snapshot = _working.Clone();
            var components = await Task.Run(() => VehicleAssetService.Components(_native, snapshot));
            _availableComponents.AddRange(components);
            foreach (var component in components) _components.Items.Add(component);
            if (_components.Items.Count > 0) _components.SelectedIndex = 0;
            else FillComponent();
        }
        catch (Exception ex) { _attachmentStatus.Text = ex.Message; }
        finally { _busy = false; if (_donorSelector is not null) _donorSelector.Enabled = enabled; }
    }
    private static Button Button(string text, EventHandler click) { var b = new Button { Text = text, Dock = DockStyle.Fill, UseMnemonic = false }; Theme.StyleDarkButton(b); b.Click += click; return b; }
    private static Button ActionButton(string text, EventHandler click) { var b = Button(text, click); b.Dock = DockStyle.None; b.Size = new Size(155, 36); return b; }
    private void FillIcon()
    {
        _iconPreview.Image?.Dispose(); _iconPreview.Image = null;
        _iconStatus.Text = _working.MenuIcon is null ? "Using the donor icon.\nImport a 512 × 340 PNG with a transparent background." : "Custom vehicle selection icon · 512 × 340\nKeep the full vehicle inside the image with a little padding.";
        try { var path = VehicleIconService.PreviewPath(_working, _service.DirectoryFor(_working)); if (path is not null && File.Exists(path)) { using var image = Image.FromFile(path); _iconPreview.Image = new Bitmap(image); } }
        catch (Exception ex) { _iconStatus.Text = "Icon preview unavailable: " + ex.Message; }
    }
    private async Task ImportIcon()
    {
        if (_busy) return;
        using var file = new OpenFileDialog { Title = "Vehicle menu icon · 512 × 340", Filter = "PNG image|*.png" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        _busy = true; _iconStatus.Text = "Importing and cooking vehicle icon…";
        try { var snapshot = _working.Clone(); _working.MenuIcon = await Task.Run(() => VehicleIconService.Import(_projectRoot, _service.DirectoryFor(snapshot), snapshot, file.FileName)); FillIcon(); }
        catch (Exception ex) { FillIcon(); Dialog.Error(this, "Vehicle icon not imported", ex.Message); }
        finally { _busy = false; }
    }
    private void FillModel()
    {
        _modelStatus.Text = _working.Model is null ? "Native body · original LEGO materials\nImport a weighted FBX to replace it." : $"{_working.Model.Name} · {_working.Model.Materials.Count} material slots\nDefault materials and colors. Choose surface overrides in the 3D workshop.";
        _materials.Rows.Clear();
        if (_working.Model is { } model) foreach (var slot in model.Materials) {
            var color = _working.Palette.FirstOrDefault(c => c.Slot == slot.Slot);
            var row = _materials.Rows[_materials.Rows.Add(slot.SourceMaterialName, color is null ? slot.MaterialPath : PaintShader(color.Finish), color is null ? "—" : ColorTranslator.ToHtml(PaintColor(color)), color?.Finish ?? "Material")];
            row.Cells[1].ReadOnly = color is not null;
            row.Cells[1].ToolTipText = color is null ? slot.MaterialPath : "A private copy of this shader uses the selected color. Use slot material restores the original assignment.";
            row.Cells[3].ReadOnly = color is null;
            if (color is not null) {
                ((DataGridViewComboBoxCell)row.Cells[3]).Items.Remove("Material");
                var swatch = PaintColor(color); var ink = swatch.GetBrightness() > .55f ? Color.Black : Color.White;
                row.Cells[2].Style.BackColor = row.Cells[2].Style.SelectionBackColor = swatch;
                row.Cells[2].Style.ForeColor = row.Cells[2].Style.SelectionForeColor = ink;
            }
        }
    }
    private void ReadMaterials()
    {
        _materials.EndEdit();
        foreach (var color in _working.Palette) { var finish = _materials.Rows[color.Slot].Cells[3].Value?.ToString() ?? ""; if (VehiclePaintService.ValidFinish(finish)) color.Finish = finish; }
        if (_working.Model is { } model) for (int i = 0; i < model.Materials.Count; i++)
            if (!_working.Palette.Any(c => c.Slot == i)) model.Materials[i].MaterialPath = UnrealPathUtil.NormalizePackagePath(_materials.Rows[i].Cells[1].Value?.ToString());
    }
    private async void EditModel()
    {
        if (_busy) return; ReadMaterials();
        try
        {
        var recipe = _working.Model?.Clone() ?? new SkinnedMeshImport { Name = _name.Text + " body", Component = "SkeletalMeshComponent", DonorMeshPackage = VehicleDonorService.Get(_working).Mesh, MeshPackage = VehicleProjectService.Mesh(_working) };
        using var workshop = new SkinnedMeshWorkshopForm(_service.DirectoryFor(_working), [new("SkeletalMeshComponent", VehicleDonorService.Get(_working).Mesh)], [], recipe, _working.Model is not null, vehicle: true);
        if (workshop.ShowDialog(this) == DialogResult.OK && workshop.Result is { } result)
        {
            var next = _working.Clone();
            if (next.Model is null || !next.Model.Materials.Select(m => m.SourceMaterialName).SequenceEqual(result.Materials.Select(m => m.SourceMaterialName))) { next.Palette.Clear(); next.MaterialOverrides.RemoveAll(m => m.Component == "body"); }
            // Match light sections by their imported names, never by a possibly reordered slot index.
            var lightNames = _working.LightSurfaces.Select(s => (Name: _working.Model?.Materials.FirstOrDefault(m => m.Slot == s.Slot)?.SourceMaterialName, s.Role)).ToArray();
            next.LightSurfaces = lightNames.Select(s => (Material: result.Materials.FirstOrDefault(m => m.SourceMaterialName == s.Name), s.Role))
                .Where(s => s.Material is not null).Select(s => new VehicleLightSurface { Slot = s.Material!.Slot, Role = s.Role }).ToList();
            next.Model = result; next.DisplayName = _name.Text.Trim();
            _busy = true;
            await Task.Run(() => VehicleMaterialService.EnsureSlots(_projectRoot, next, Path.Combine(_service.DirectoryFor(_working), "MaterialSources"), CancellationToken.None));
            _working.Model = next.Model; _working.Palette = next.Palette; _working.MaterialOverrides = next.MaterialOverrides; _working.LightSurfaces = next.LightSurfaces; _donorSelector.Enabled = false; FillModel();
        }
        else if (workshop.RemoveRequested) { _working.Model = null; _working.LightSurfaces.Clear(); _working.Palette.Clear(); _working.MaterialOverrides.RemoveAll(m => m.Component == "body"); FillModel(); }
        }
        catch (Exception ex) { Dialog.Error(this, "Vehicle model could not be applied", ex.Message); }
        finally { _busy = false; }
    }
    private async Task MakeMaterialCopies()
    {
        if (_busy) return;
        try
        {
            ReadMaterials(); var next = _working.Clone(); next.DisplayName = _name.Text.Trim(); _busy = true;
            _modelStatus.Text = "Creating editable material copies…";
            await Task.Run(() => VehicleMaterialService.EnsureSlots(_projectRoot, next, Path.Combine(_service.DirectoryFor(_working), "MaterialSources"), CancellationToken.None));
            _working.Model = next.Model; _working.Palette = next.Palette; FillModel();
        }
        catch (Exception ex) { Dialog.Error(this, "Material copies were not applied", ex.Message); FillModel(); }
        finally { _busy = false; }
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
                _working.Transforms = result.Transforms; _working.MaterialOverrides = result.MaterialOverrides; _working.DisabledParts = result.DisabledParts;
                _working.Model = result.Model; _working.Palette = result.Palette; _working.Lights = result.Lights; _working.AccentColor = result.AccentColor; _working.LightSurfaces = result.LightSurfaces; _working.ToyboxParts = result.ToyboxParts; FillModel(); FillComponent();
                _attachmentStatus.Text = "3D placements applied. Save vehicle to keep them.";
            }
        }
        catch (Exception ex) { Dialog.Error(this, "Workshop could not be opened", ex.Message); }
    }
    private void SetColor()
    {
        if (_materials.CurrentRow is not { } row) return; ReadMaterials();
        var current = _working.Palette.FirstOrDefault(c => c.Slot == row.Index);
        current ??= new ToolMaterialLibraryService(_projectRoot).LoadMetadataSnapshot().FirstOrDefault(m => m.PackagePath == _working.Model?.Materials[row.Index].MaterialPath)?.VehiclePaint;
        using var color = new ColorDialog { FullOpen = true, Color = current is null ? Color.Black : PaintColor(current) };
        if (color.ShowDialog(this) != DialogResult.OK) return;
        static float Linear(byte v) { var n = v / 255f; return n <= .04045f ? n / 12.92f : MathF.Pow((n + .055f) / 1.055f, 2.4f); }
        _working.MaterialOverrides.RemoveAll(m => m.Component == "body" && m.Slot == row.Index);
        var finish = current?.Finish ?? "Solid";
        _working.Palette.RemoveAll(c => c.Slot == row.Index); _working.Palette.Add(new() { Slot = row.Index, Finish = finish, R = Linear(color.Color.R), G = Linear(color.Color.G), B = Linear(color.Color.B) }); FillModel();
    }
    private static string PaintShader(string finish) => finish == "Flat" ? VehicleAssetService.PaletteTemplate : VehiclePaintService.Material(finish);
    private static Color PaintColor(VehiclePaletteColor color)
    {
        static int Srgb(float value) => (int)Math.Clamp(MathF.Round(255 * (value <= .0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - .055f)), 0, 255);
        return Color.FromArgb(Srgb(color.R), Srgb(color.G), Srgb(color.B));
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
    private async void Save()
    {
        if (_busy) return;
        if (_transformDirty) ApplyComponent();
        try
        {
            ReadMaterials(); _working.DisplayName = _name.Text.Trim();
            if (_owner.SelectedItem is not OwnerChoice owner) throw new InvalidDataException("Choose an available character owner.");
            _working.OwnerTag = owner.Tag; _working.OwnerCharacterProjectPath = owner.ProjectPath;
            VehicleProjectService.ValidateIdentity(_working);
            var directory = _service.DirectoryFor(_working);
            VehicleIconService.Validate(_working, directory);
            if (_working.Model is { } model) { SkinnedMeshStageService.ValidateRecipe(model); SkinnedMeshStageService.ReadManifest(directory, model); }
            var next = _working.Clone(); _busy = true;
            await Task.Run(() => VehicleMaterialService.EnsureSlots(_projectRoot, next, Path.Combine(directory, "MaterialSources"), CancellationToken.None));
            _working.Model = next.Model; _working.Palette = next.Palette; _busy = false;
            Result = _working; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { Dialog.Error(this, "Vehicle not saved", ex.Message); }
        finally { _busy = false; }
    }
}
