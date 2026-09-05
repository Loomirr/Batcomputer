namespace Batcomputer;

/// <summary>Private editing session. Only an accepted recipe reaches the suit settings.</summary>
public sealed class WeaponModelEditorForm : AdaptiveForm
{
    private readonly ModelPreviewControl _viewer = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(340, 0) };
    private readonly DataGridView _materials = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false };
    private readonly List<NumericUpDown> _numbers = [];
    private readonly CheckBox _original = new() { Text = "Show original weapon", Checked = true, AutoSize = true };
    private readonly CheckBox _custom = new() { Text = "Show custom model", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 450 };
    private WeaponModelRecipe? _working;
    private string? _folder;
    private int _revision;
    private bool _busy;
    private bool _refreshPending;
    private readonly Button _save;
    public WeaponModelRecipe? Result { get; private set; }

    public WeaponModelEditorForm(string reference, WeaponModelRecipe? existing)
    {
        _working = existing?.Clone();
        Text = "Batcomputer — Weapon workshop";
        ClientSize = new Size(1220, 840); MinimumSize = new Size(980, 720);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(6, 0, 0, 8) };
        header.Controls.Add(new Label { Text = "WEAPON WORKSHOP", ForeColor = Theme.Gold, Font = new Font(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true }, 0, 0);
        header.Controls.Add(new Label { Text = "Import your model · align against the original · validate the game-ready mesh", AutoSize = true, ForeColor = Theme.OnDarkMuted }, 0, 1);
        root.Controls.Add(header, 0, 0); root.SetColumnSpan(header, 2);
        var previewCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBg, Padding = new Padding(1), Margin = new Padding(10, 0, 0, 0) };
        previewCard.Controls.Add(_viewer); root.Controls.Add(previewCard, 1, 1);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.CardBg, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14), Margin = Padding.Empty };
        root.Controls.Add(panel, 0, 1);
        void Section(string title) => panel.Controls.Add(new Label { Text = title, ForeColor = Theme.Gold, AutoSize = true, Margin = new Padding(3, 15, 3, 8) });
        Button Button(string text, EventHandler click) { var b = new Button { Text = text, Width = 340, Height = 38, AutoEllipsis = true }; Theme.StyleDarkButton(b); b.Click += click; panel.Controls.Add(b); return b; }
        Section("01  MODEL & VISIBILITY");
        Button("Import / replace OBJ…", (_, _) => Import());
        panel.Controls.Add(_original); panel.Controls.Add(_custom);
        Section("02  ALIGNMENT");
        panel.Controls.Add(new Label { Text = "Offsets in Unreal cm · rotation in degrees\nOBJ is centered before alignment. Axes mark mesh-local zero, not the hand grip.", ForeColor = Theme.OnDarkMuted, AutoSize = true, MaximumSize = new Size(340, 0) });
        foreach (var (name, min, max, value) in new[] { ("Scale", .001m, 1000m, (decimal)(_working?.Scale ?? 1)),
            ("Offset X", -10000m, 10000m, (decimal)(_working?.X ?? 0)), ("Offset Y", -10000m, 10000m, (decimal)(_working?.Y ?? 0)),
            ("Offset Z", -10000m, 10000m, (decimal)(_working?.Z ?? 0)), ("Pitch", -360m, 360m, (decimal)(_working?.Pitch ?? 0)),
            ("Yaw", -360m, 360m, (decimal)(_working?.Yaw ?? 0)), ("Roll", -360m, 360m, (decimal)(_working?.Roll ?? 0)) })
        {
            var row = new FlowLayoutPanel { Width = 340, Height = 36 };
            row.Controls.Add(new Label { Text = name, Width = 90, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
            var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = 3, Increment = .1m, Value = Math.Clamp(value, min, max), Width = 225,
                BackColor = Theme.PanelBg, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.FixedSingle };
            row.Controls.Add(n); panel.Controls.Add(row); _numbers.Add(n); n.ValueChanged += (_, _) => Queue();
        }
        Section("03  MATERIAL ASSIGNMENTS");
        var gridPanel = new Panel { Width = 340, Height = 165 }; panel.Controls.Add(gridPanel); gridPanel.Controls.Add(_materials);
        _materials.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "OBJ material", ReadOnly = true, Width = 110 });
        _materials.Columns.Add(new DataGridViewTextBoxColumn { Name = "Package", HeaderText = "Material package", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        Theme.StyleGrid(_materials);
        _materials.CellEndEdit += (_, _) => Queue();
        _original.CheckedChanged += (_, _) => Queue(); _custom.CheckedChanged += (_, _) => Queue();
        _save = Button("Validate bake && use model", async (_, _) => await SaveAsync());
        Theme.StyleGoldButton(_save);
        var cancel = Button("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        _status.AutoSize = false; _status.MaximumSize = Size.Empty; _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.ForeColor = Theme.OnDarkMuted;
        cancel.Dock = DockStyle.Fill; _save.Dock = DockStyle.Fill;
        footer.Controls.Add(_status, 0, 0); footer.Controls.Add(cancel, 1, 0); footer.Controls.Add(_save, 2, 0);
        root.Controls.Add(footer, 0, 2); root.SetColumnSpan(footer, 2);
        panel.Controls.Add(new Label { Text = "Collision, hitboxes and damage stay native. Material preview uses slot colors, not final game shaders. Save the parent ability editor, then rebuild your suit to package the model.", AutoSize = true, MaximumSize = new Size(320, 0) });
        FillMaterials();
        _timer.Tick += async (_, _) => { _timer.Stop(); await RefreshAsync(); };
        Shown += async (_, _) =>
        {
            _busy = true; _save.Enabled = false; _status.Text = "Loading native weapon…";
            try { _folder = await Task.Run(() => WeaponModelService.CreateViewer(reference)); if (IsDisposed) return; await _viewer.ShowFolderAsync(_folder); _status.Text = "Import a model or adjust the saved model."; }
            catch (Exception ex) { if (!IsDisposed) _status.Text = "Reference failed: " + ex.Message; }
            finally { _busy = false; if (!IsDisposed) { _save.Enabled = _folder is not null; Queue(); } }
        };
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Please wait for the current operation to finish."; } };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Queue() { if (_busy) { _refreshPending = true; return; } if (_folder is not null) { _timer.Stop(); _timer.Start(); } }
    private void FillMaterials()
    {
        _materials.Rows.Clear();
        if (_working is not null) foreach (var m in _working.Materials) _materials.Rows.Add(m.SourceMaterialName, m.MaterialPath);
    }
    private void Import()
    {
        if (_busy) return;
        using var picker = new OpenFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > WeaponModelService.MaximumSourceLength) throw new InvalidDataException("OBJ must be smaller than 8 MB.");
            var slots = StaticMeshObjProbeService.InspectObjMaterialSlots(picker.FileName);
            var candidate = new WeaponModelRecipe { SourceName = Path.GetFileName(picker.FileName), ObjText = File.ReadAllText(picker.FileName),
                Materials = slots.Select(s => new CustomStaticMeshMaterialSlot { Slot = s.Slot, SourceMaterialName = s.SourceMaterialName,
                    StableSlotName = s.StableSlotName, MaterialPath = "/Game/Models/Props/Materials/Mi_LEGO_Bake_Katana" }).ToList() };
            _working = candidate; FillMaterials(); Queue();
        }
        catch (Exception ex) { Dialog.Info(this, "Model could not be imported", ex.Message); }
    }
    private WeaponModelRecipe ReadRecipe()
    {
        if (_working is null) throw new InvalidDataException("Import an OBJ first.");
        _materials.EndEdit(); var r = _working.Clone();
        var v = _numbers.Select(n => (float)n.Value).ToArray();
        r.Scale = v[0]; r.X = v[1]; r.Y = v[2]; r.Z = v[3]; r.Pitch = v[4]; r.Yaw = v[5]; r.Roll = v[6];
        for (var i = 0; i < r.Materials.Count; i++) r.Materials[i].MaterialPath = _materials.Rows[i].Cells[1].Value?.ToString()?.Trim() ?? "";
        WeaponModelService.Validate(r); return r;
    }
    private async Task RefreshAsync()
    {
        if (_busy || _folder is null) return;
        _busy = true;
        try
        {
            if (_working is null)
                AtomicFileUtil.WriteAllText(Path.Combine(_folder, "weapon.json"), System.Text.Json.JsonSerializer.Serialize(new { original = _original.Checked, custom = _custom.Checked, revision = 0 }));
            else
            {
                var r = ReadRecipe(); var original = _original.Checked; var custom = _custom.Checked; var revision = ++_revision;
                await Task.Run(() => WeaponModelService.Preview(r, _folder, original, custom, revision));
                _status.Text = $"{r.SourceName} · {r.Materials.Count} material slots · preview updated";
            }
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; if (_refreshPending) { _refreshPending = false; Queue(); } }
    }
    private async Task SaveAsync()
    {
        if (_busy || _folder is null) return;
        _timer.Stop();
        try
        {
            var r = ReadRecipe(); _busy = true; _save.Enabled = false; _status.Text = "Validating cooked mesh…";
            var content = Path.Combine(_folder, "Bake", Guid.NewGuid().ToString("N"));
            await Task.Run(() => WeaponModelService.Bake(r, AppSettings.Current.EffectiveExtractedContentRoot(), AppSettings.Current.EffectiveUsmapPath()!, content, "/Game/Mods/WeaponEditorValidation/SM_Weapon"));
            Result = r; _busy = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { _status.Text = "Bake failed: " + ex.Message; }
        finally { _busy = false; if (!IsDisposed) _save.Enabled = true; }
    }
}
