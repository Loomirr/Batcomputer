namespace Batcomputer;

/// <summary>Edits a project-owned OBJ static attachment before it is staged.</summary>
public sealed class CustomStaticMeshImportDialog : AdaptiveForm
{
    private readonly TextBox _name = new();
    private readonly TextBox _source = new() { ReadOnly = true };
    private readonly ThemedDropDown _target = new() { Placeholder = "Choose game attachment slot" };
    private readonly NumericUpDown _scale = Number(150m, 1m, 1000m, 2, 1m);
    private readonly NumericUpDown _offsetX = Number(0m, -100000m, 100000m, 3, 0.1m);
    private readonly NumericUpDown _offsetY = Number(0m, -100000m, 100000m, 3, 0.1m);
    private readonly NumericUpDown _offsetZ = Number(0m, -100000m, 100000m, 3, 0.1m);
    private readonly NumericUpDown _rotationPitch = Number(0m, -360m, 360m, 2, 1m);
    private readonly NumericUpDown _rotationYaw = Number(0m, -360m, 360m, 2, 1m);
    private readonly NumericUpDown _rotationRoll = Number(0m, -360m, 360m, 2, 1m);
    private readonly CheckBox _hideBaseHead = new();

    public string SourceObjPath => _source.Text.Trim();
    public string DisplayName => _name.Text.Trim();
    public CustomStaticMeshImportService.AttachmentSlotDefinition AttachmentSlot =>
        _target.SelectedItem as CustomStaticMeshImportService.AttachmentSlotDefinition
        ?? CustomStaticMeshImportService.ResolveAttachmentSlot("Head");
    public float ImportScale => (float)_scale.Value;
    public float OffsetX => (float)_offsetX.Value;
    public float OffsetY => (float)_offsetY.Value;
    public float OffsetZ => (float)_offsetZ.Value;
    public float RotationPitch => (float)_rotationPitch.Value;
    public float RotationYaw => (float)_rotationYaw.Value;
    public float RotationRoll => (float)_rotationRoll.Value;
    public bool HideBaseHead => _hideBaseHead.Checked;
    public bool DeleteRequested { get; private set; }

    public CustomStaticMeshImportDialog(CustomStaticMeshImport? existing = null, string? sourcePath = null)
    {
        Text = existing is null ? "Import custom mesh" : "Edit custom mesh";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        ClientSize = new Size(780, 730);
        MinimumSize = new Size(720, 620);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = Theme.WindowBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 3,
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Text = "CUSTOM MESH",
            Dock = DockStyle.Fill,
            Font = Theme.Eyebrow,
            ForeColor = Theme.Parts,
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = existing is null ? "Import static mesh" : "Edit static mesh",
            Dock = DockStyle.Fill,
            Font = Theme.Heading,
            ForeColor = Theme.OnDark,
        }, 0, 1);
        header.Controls.Add(new Label
        {
            Text = "Choose an OBJ, select its real game attachment slot, then tune scale and local placement. You can refine the same values again in the 3D viewer after import.",
            Dock = DockStyle.Fill,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
        }, 0, 2);
        root.Controls.Add(header, 0, 0);

        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12),
            AutoScroll = true,
        };
        root.Controls.Add(card, 0, 1);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 12,
            BackColor = Color.Transparent,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(fields);

        _name.Text = existing?.DisplayName ?? "";
        _source.Text = sourcePath ?? "";
        _scale.Value = Clamp(existing?.Scale ?? 150f, _scale.Minimum, _scale.Maximum);
        _offsetX.Value = Clamp(existing?.OffsetX ?? 0f, _offsetX.Minimum, _offsetX.Maximum);
        _offsetY.Value = Clamp(existing?.OffsetY ?? 0f, _offsetY.Minimum, _offsetY.Maximum);
        _offsetZ.Value = Clamp(existing?.OffsetZ ?? 0f, _offsetZ.Minimum, _offsetZ.Maximum);
        _rotationPitch.Value = Clamp(existing?.RotationPitch ?? 0f, _rotationPitch.Minimum, _rotationPitch.Maximum);
        _rotationYaw.Value = Clamp(existing?.RotationYaw ?? 0f, _rotationYaw.Minimum, _rotationYaw.Maximum);
        _rotationRoll.Value = Clamp(existing?.RotationRoll ?? 0f, _rotationRoll.Minimum, _rotationRoll.Maximum);
        _hideBaseHead.Checked = existing?.HideBaseHead ?? true;

        foreach (var attachment in CustomStaticMeshImportService.AttachmentSlots)
        {
            _target.Items.Add(attachment);
        }
        _target.SelectedItem = CustomStaticMeshImportService.ResolveAttachmentSlot(existing?.Target, existing?.AttachSocket);
        _target.SelectedIndexChanged += (_, _) => SyncBaseHeadOption();

        AddTextRow(fields, 0, "Mesh name", _name, "Shown in the suit and Parts list.");
        AddSourceRow(fields, 1);
        AddDropDownRow(fields, 2, "Attach to", _target, "From the game's CAE_Default_AttachmentDef asset.");
        AddNumberRow(fields, 3, "Uniform scale", _scale, "Game scale in Unreal centimetres. This exact value is baked into the mesh; edit it again from the 3D viewer when checking the character.");
        AddNumberRow(fields, 4, "Offset X", _offsetX, "Local Unreal-centimetre offset after the OBJ is centered.");
        AddNumberRow(fields, 5, "Offset Y", _offsetY, "Local Unreal-centimetre offset. Small changes are easiest to judge in the 3D viewer.");
        AddNumberRow(fields, 6, "Offset Z", _offsetZ, "Local Unreal-centimetre offset, saved with this custom mesh.");
        AddNumberRow(fields, 7, "Rotation pitch", _rotationPitch, "Unreal pitch in degrees, baked into both the game mesh and 3D preview.");
        AddNumberRow(fields, 8, "Rotation yaw", _rotationYaw, "Unreal yaw in degrees. Use this to turn an imported head attachment left or right.");
        AddNumberRow(fields, 9, "Rotation roll", _rotationRoll, "Unreal roll in degrees, baked into both the game mesh and 3D preview.");

        _hideBaseHead.Text = "Hide the base Head component";
        _hideBaseHead.AutoSize = true;
        _hideBaseHead.ForeColor = Theme.OnDark;
        _hideBaseHead.Margin = new Padding(0, 9, 0, 0);
        fields.Controls.Add(_hideBaseHead, 1, 10);
        fields.SetColumnSpan(_hideBaseHead, 2);
        SyncBaseHeadOption();

        var note = new Label
        {
            Text = "Static OBJ only for now. Export triangles, UVs, and one material section. The 3D viewer saves custom-mesh scale, XYZ placement, and rotation into this suit.",
            Dock = DockStyle.Fill,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            Padding = new Padding(0, 8, 0, 0),
        };
        fields.Controls.Add(note, 0, 11);
        fields.SetColumnSpan(note, 3);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.Transparent,
        };
        var save = new Button { Text = existing is null ? "Import mesh" : "Save mesh", Width = 112, Height = 32 };
        Theme.StyleGoldButton(save);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                Dialog.Warn(this, "Custom mesh", "Give this imported mesh a name.");
                return;
            }
            if (!File.Exists(SourceObjPath) || !Path.GetExtension(SourceObjPath).Equals(".obj", StringComparison.OrdinalIgnoreCase))
            {
                Dialog.Warn(this, "Custom mesh", "Choose an existing Wavefront OBJ file.");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button { Text = "Cancel", Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        if (existing is not null)
        {
            var remove = new Button { Text = "Remove", Width = 94, Height = 32 };
            Theme.StyleDarkButton(remove);
            remove.ForeColor = Color.FromArgb(232, 96, 96);
            remove.Click += (_, _) =>
            {
                DeleteRequested = true;
                DialogResult = DialogResult.Cancel;
                Close();
            };
            footer.Controls.Add(remove);
        }
        root.Controls.Add(footer, 0, 2);
        AcceptButton = save;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(this);
    }

    private void AddSourceRow(TableLayoutPanel fields, int row)
    {
        AddLabel(fields, row, "OBJ file");
        Theme.StyleDarkInput(_source);
        _source.Dock = DockStyle.Fill;
        _source.Margin = new Padding(0, 6, 8, 6);
        fields.Controls.Add(_source, 1, row);
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        Theme.StyleDarkButton(browse);
        browse.Click += (_, _) => BrowseForObj();
        fields.Controls.Add(browse, 2, row);
    }

    private static void AddTextRow(TableLayoutPanel fields, int row, string title, TextBox input, string hint)
    {
        AddLabel(fields, row, title);
        Theme.StyleDarkInput(input);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 6, 0, 6);
        fields.Controls.Add(input, 1, row);
        fields.SetColumnSpan(input, 2);
        AddHint(input, hint);
    }

    private static void AddDropDownRow(TableLayoutPanel fields, int row, string title, ThemedDropDown input, string hint)
    {
        AddLabel(fields, row, title);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 3, 0, 3);
        fields.Controls.Add(input, 1, row);
        fields.SetColumnSpan(input, 2);
        AddHint(input, hint);
    }

    private static void AddNumberRow(TableLayoutPanel fields, int row, string title, NumericUpDown input, string hint)
    {
        AddLabel(fields, row, title);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 0, 4);
        fields.Controls.Add(input, 1, row);
        fields.SetColumnSpan(input, 2);
        AddHint(input, hint);
    }

    private static void AddLabel(TableLayoutPanel fields, int row, string title)
    {
        fields.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.BodyStrong,
            ForeColor = Theme.OnDark,
        }, 0, row);
    }

    private static void AddHint(Control control, string hint)
    {
        var tips = new ToolTip();
        Theme.StyleTooltip(tips);
        tips.SetToolTip(control, hint);
    }

    private void BrowseForObj()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a static OBJ mesh",
            Filter = "Wavefront OBJ (*.obj)|*.obj",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        _source.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void SyncBaseHeadOption()
    {
        var canHideBaseHead = AttachmentSlot.CanHideBaseHead;
        _hideBaseHead.Enabled = canHideBaseHead;
        if (!canHideBaseHead)
        {
            _hideBaseHead.Checked = false;
            _hideBaseHead.Text = "Base Head hiding only applies to the Head slot";
        }
        else
        {
            _hideBaseHead.Text = "Hide the base Head component";
        }
    }

    private static NumericUpDown Number(decimal value, decimal minimum, decimal maximum, int decimalPlaces, decimal increment)
    {
        var input = new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            DecimalPlaces = decimalPlaces,
            Increment = increment,
            Value = value,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Slate,
            ForeColor = Theme.OnDark,
            Font = Theme.Mono,
        };
        return input;
    }

    private static decimal Clamp(float value, decimal minimum, decimal maximum) =>
        Math.Min(maximum, Math.Max(minimum, (decimal)value));
}
