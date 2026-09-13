namespace Batcomputer;

/// <summary>Private authoring session; the caller commits only an accepted, validated recipe.</summary>
internal sealed class SkinnedMeshWorkshopForm : AdaptiveForm
{
    private readonly string _directory;
    private readonly ThemedDropDown _target = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _scale = new() { Minimum = .0001m, Maximum = 1000m, DecimalPlaces = 4, Value = 1, Dock = DockStyle.Fill };
    private readonly DataGridView _materials = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false };
    private readonly CheckedListBox _hidden = new() { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.None };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly ModelPreviewControl _viewer = new() { Dock = DockStyle.Fill };
    private readonly Button _save;
    private readonly CancellationTokenSource _cancel = new();
    private SkinnedMeshImport _working;
    private bool _busy;
    internal SkinnedMeshImport? Result { get; private set; }
    internal bool RemoveRequested { get; private set; }

    internal SkinnedMeshWorkshopForm(string directory, IReadOnlyList<SkinnedMeshStageService.Target> targets,
        IReadOnlyList<string> components, SkinnedMeshImport recipe, bool existing, bool vehicle = false)
    {
        _directory = directory; _working = recipe.Clone();
        Text = "Batcomputer — Skinned mesh workshop (experimental)"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1280, 860); MinimumSize = new Size(1020, 740); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(16) };
        root.ColumnStyles.Add(new(SizeType.Absolute, 430)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 115)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 76)); Controls.Add(root);
        var intro = new Label { Text = vehicle ? "VEHICLE BODY\n\nKeep the donor rig and wheel pivots. Import a weighted FBX; assign a cooked material to every slot." : "EXISTING-RIG SKINNED MESHES\n\n" + SkinnedMeshCookService.Warning, Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, Padding = new Padding(4) };
        root.Controls.Add(intro, 0, 0); root.SetColumnSpan(intro, 2);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.CardBg, Padding = new Padding(12) }; root.Controls.Add(scroll, 0, 1);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 }; fields.ColumnStyles.Add(new(SizeType.Percent, 100)); scroll.Controls.Add(fields);
        void Add(string label, Control control, int height)
        {
            int row = fields.RowCount++; fields.RowStyles.Add(new(SizeType.Absolute, 26)); fields.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Parts, Margin = new Padding(0, 5, 0, 0) }, 0, row);
            row = fields.RowCount++; fields.RowStyles.Add(new(SizeType.Absolute, height)); fields.Controls.Add(control, 0, row);
        }
        Button Button(string title, EventHandler action) { var b = new Button { Text = title, Dock = DockStyle.Fill }; Theme.StyleDarkButton(b); b.Click += action; return b; }
        foreach (var target in targets) _target.Items.Add(target);
        _target.SelectedItem = targets.FirstOrDefault(t => t.Component == recipe.Component) ?? targets.FirstOrDefault();
        _target.Enabled = !existing; _name.Text = recipe.Name; _scale.Value = Math.Clamp((decimal)recipe.ImportScale, _scale.Minimum, _scale.Maximum);
        Add("1  Which native rig did you use? (component / reference mesh)", _target, 40); Add("Name", _name, 32); Add("FBX unit correction (keep the native bone rest pose)", _scale, 32);
        Add("2  Reference and source", Button("Export native reference + rig (GLB)…", async (_, _) => await ExportAsync()), 40);
        Add("", Button("Import / replace weighted FBX…", async (_, _) => await ImportAsync(false)), 40);
        Add("", Button("Reimport saved FBX", async (_, _) => await ImportAsync(true)), 40);
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "FBX slot", ReadOnly = true, Width = 125 });
        _materials.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cooked material package", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill }); Theme.StyleGrid(_materials);
        Add("3  Materials (use the Materials / Textures toyboxes)", _materials, 180);
        _hidden.BackColor = Theme.PanelBg; _hidden.ForeColor = Theme.OnDark;
        foreach (var component in components.Where(c => c != recipe.Component && c != "CharacterMesh0")) _hidden.Items.Add(component, recipe.HiddenComponents.Contains(component));
        if (!vehicle) Add("4  Hide existing visual components (optional)", _hidden, 105);
        Add("", Button("Refresh deformation preview", async (_, _) => await PreviewAsync()), 40);
        if (existing) Add("", Button("Remove replacement / restore native", (_, _) => { if (!_busy) { RemoveRequested = true; Close(); } }), 40);
        root.Controls.Add(_viewer, 1, 1);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(0, 12, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 120)); footer.ColumnStyles.Add(new(SizeType.Absolute, 185));
        _save = Button("Use skinned mesh", (_, _) => Save()); Theme.StyleGoldButton(_save);
        var cancel = Button("Cancel", (_, _) => { if (_busy) { _cancel.Cancel(); _status.Text = "Cancelling the cook…"; } else Close(); });
        footer.Controls.Add(_status, 0, 0); footer.Controls.Add(cancel, 1, 0); footer.Controls.Add(_save, 2, 0);
        root.Controls.Add(footer, 0, 2); root.SetColumnSpan(footer, 2);
        _status.Text = "Rig preparation is external. Baking requires the UE 5.6 installation configured in Settings.";
        Fill();
        _target.SelectedIndexChanged += (_, _) => { if (_target.SelectedItem is SkinnedMeshStageService.Target target) { _working.Component = target.Component; _working.DonorMeshPackage = target.DonorMesh; _working.CacheRelativePath = ""; Fill(); } };
        FormClosing += (_, e) => { if (_busy) { _cancel.Cancel(); e.Cancel = true; _status.Text = "Waiting for the import to stop safely…"; } };
        Shown += async (_, _) => { if (existing) await PreviewAsync(); };
        FormClosed += (_, _) => _cancel.Dispose();
    }
    private void Status(string message) { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) _status.Text = message; }); }
    private void Fill() { _materials.Rows.Clear(); foreach (var m in _working.Materials) _materials.Rows.Add(m.SourceMaterialName, m.MaterialPath); _save.Enabled = !_busy && !string.IsNullOrEmpty(_working.CacheRelativePath); }
    private SkinnedMeshImport Read()
    {
        _materials.EndEdit(); var result = _working.Clone(); result.Name = _name.Text.Trim();
        if (result.Name.Length == 0) throw new InvalidDataException("Give the mesh a name.");
        for (int i = 0; i < result.Materials.Count; i++) result.Materials[i].MaterialPath = UnrealPathUtil.NormalizePackagePath(_materials.Rows[i].Cells[1].Value?.ToString());
        result.HiddenComponents = _hidden.CheckedItems.Cast<string>().ToList(); return result;
    }
    private async Task ImportAsync(bool saved)
    {
        if (_busy || _cancel.IsCancellationRequested) return;
        string source;
        if (saved) { if (string.IsNullOrEmpty(_working.SourceRelativePath)) { _status.Text = "Import an FBX first."; return; } source = SkinnedMeshCookService.SafePath(_directory, _working.SourceRelativePath); }
        else { using var picker = new OpenFileDialog { Filter = "Weighted binary FBX (*.fbx)|*.fbx", CheckFileExists = true }; if (picker.ShowDialog(this) != DialogResult.OK) return; source = picker.FileName; }
        try
        {
            var request = Read(); request.ImportScale = (float)_scale.Value;
            if (_target.SelectedItem is not SkinnedMeshStageService.Target target) throw new InvalidDataException("Select a compatible native component.");
            request.Component = target.Component; request.DonorMeshPackage = target.DonorMesh;
            _busy = true; _save.Enabled = false; _target.Enabled = false;
            var imported = await Task.Run(() => SkinnedMeshCookService.ImportAsync(_directory, source, request, Status, _cancel.Token));
            _working = imported; Fill(); _status.Text = "Cook validated. Assign materials, inspect the mesh, then save.";
        }
        catch (OperationCanceledException) { _status.Text = "Import cancelled. Previous working mesh was not replaced."; }
        catch (Exception ex) { _status.Text = ex.Message; Dialog.Error(this, "Skinned mesh was not imported", ex.Message); }
        finally { _busy = false; _save.Enabled = !string.IsNullOrEmpty(_working.CacheRelativePath); _target.Enabled = string.IsNullOrEmpty(_working.CacheRelativePath); }
        if (!_cancel.IsCancellationRequested && !string.IsNullOrEmpty(_working.CacheRelativePath)) await PreviewAsync();
    }
    private async Task ExportAsync()
    {
        if (_busy || _target.SelectedItem is not SkinnedMeshStageService.Target target) return;
        using var picker = new FolderBrowserDialog { Description = "Choose a folder for the native reference mesh and skeleton" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _busy = true;
        try { var file = await Task.Run(() => SkinnedMeshCookService.ExportReference(target.DonorMesh, Path.Combine(picker.SelectedPath, "Reference-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")))); _status.Text = "Reference exported: " + file; }
        catch (Exception ex) { Dialog.Error(this, "Reference export failed", ex.Message); }
        finally { _busy = false; }
    }
    private async Task PreviewAsync()
    {
        if (_busy || string.IsNullOrEmpty(_working.CacheRelativePath)) return;
        _busy = true;
        try { var recipe = Read(); var folder = await Task.Run(() => SkinnedMeshPreviewService.Create(_directory, recipe)); await _viewer.ShowFolderAsync(folder); _status.Text = "Pose-check preview. Slot colors identify regions; final game shaders are not simulated."; }
        catch (Exception ex) { _status.Text = "Preview: " + ex.Message; }
        finally { _busy = false; }
    }
    private void Save()
    {
        if (_busy) return;
        try { var result = Read(); SkinnedMeshStageService.ValidateRecipe(result); SkinnedMeshStageService.ReadManifest(_directory, result); Result = result; DialogResult = DialogResult.OK; Close(); }
        catch (Exception ex) { Dialog.Error(this, "Mesh cannot be saved", ex.Message); }
    }
}
