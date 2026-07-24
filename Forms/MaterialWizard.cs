namespace Batcomputer;

/// <summary>
/// Modal "create new material" wizard: pick a base game MI, read its texture
/// params, point them at your own texture paths, name it, generate. Reuses
/// MaterialGenService. On OK, <see cref="ResultMiPackagePath"/> holds the new MI.
/// </summary>
public sealed partial class MaterialWizard : Form
{
    private readonly string _projectRoot;
    private readonly string _modFolder;
    private readonly TextBox _baseText = new();
    private readonly TextBox _nameText = new();
    private readonly DataGridView _grid = new();
    private readonly ComboBox _generatedTextureCombo = new();
    private readonly Label _status = new();
    private readonly List<GeneratedTextureEntry> _generatedTextures = new();

    public string? ResultMiPackagePath { get; private set; }

    public MaterialWizard()
    {
        _projectRoot = "";
        _modFolder = "Suit";
        InitializeComponent();
    }

    public MaterialWizard(string projectRoot, string modFolder, string suggestedName, IEnumerable<GeneratedTextureEntry>? generatedTextures = null)
    {
        _projectRoot = projectRoot;
        _modFolder = string.IsNullOrWhiteSpace(modFolder) ? "Suit" : modFolder;
        if (generatedTextures is not null)
        {
            _generatedTextures.AddRange(generatedTextures
                .Where(t => !string.IsNullOrWhiteSpace(t.PackagePath))
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase));
        }

        InitializeComponent();
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();

        Text = "Create new material";
        Width = 720;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        // Base MI row + read.
        var baseRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        baseRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        baseRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        baseRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        baseRow.Controls.Add(new Label { Text = "Base game material (.uasset) to clone:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _baseText.Dock = DockStyle.Fill;
        baseRow.Controls.Add(_baseText, 0, 1);
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => Browse();
        baseRow.Controls.Add(browse, 1, 1);
        var read = new Button { Text = "Read params", Dock = DockStyle.Fill };
        read.Click += (_, _) => ReadParams();
        baseRow.Controls.Add(read, 2, 1);
        root.Controls.Add(baseRow, 0, 0);

        // Name row.
        var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { Text = "New material name:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _nameText.Dock = DockStyle.Fill;
        _nameText.Text = suggestedName;
        nameRow.Controls.Add(_nameText, 1, 0);
        root.Controls.Add(nameRow, 0, 1);

        var generatedTextureRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        generatedTextureRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        generatedTextureRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        generatedTextureRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        generatedTextureRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        generatedTextureRow.Controls.Add(new Label { Text = "Generated texture:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _generatedTextureCombo.Dock = DockStyle.Fill;
        _generatedTextureCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        PopulateGeneratedTextureCombo();
        generatedTextureRow.Controls.Add(_generatedTextureCombo, 1, 0);
        var useTexture = new Button { Text = "Use for selected param", Dock = DockStyle.Fill };
        useTexture.Click += (_, _) => UseGeneratedTextureForSelectedParam();
        generatedTextureRow.Controls.Add(useTexture, 2, 0);
        var copyTexture = new Button { Text = "Copy path", Dock = DockStyle.Fill };
        copyTexture.Click += (_, _) => CopySelectedGeneratedTexturePath();
        generatedTextureRow.Controls.Add(copyTexture, 3, 0);
        root.Controls.Add(generatedTextureRow, 0, 2);

        // Params grid.
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Param", HeaderText = "Parameter", ReadOnly = true, Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "Current texture", ReadOnly = true, Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "YourTexture", HeaderText = "Your texture path (/Game/…)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        root.Controls.Add(_grid, 0, 3);

        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Color.DimGray;
        _status.Text = "Pick a base MI and click Read params, then fill in your texture paths.";
        root.Controls.Add(_status, 0, 4);

        // Buttons.
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var generate = new Button { Text = "Generate", Width = 120 };
        Theme.StyleGoldButton(generate);
        generate.Click += (_, _) => Generate();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(generate);
        root.Controls.Add(buttons, 0, 5);
        CancelButton = cancel;
    }

    /// <summary>
    /// Pre-fills the base MI (.uasset on disk) and the output name, then reads
    /// the base params so the grid is populated. Used by the toybox right-click
    /// "Edit material" / "Use as base for a new material" flows.
    /// </summary>
    public void PrefillBase(string baseUassetDiskPath, string newName, bool editInPlace)
    {
        if (!string.IsNullOrWhiteSpace(baseUassetDiskPath))
        {
            _baseText.Text = baseUassetDiskPath;
        }
        if (!string.IsNullOrWhiteSpace(newName))
        {
            _nameText.Text = newName;
        }
        Text = editInPlace ? "Edit material" : "New material from base";
        if (File.Exists(baseUassetDiskPath))
        {
            try { ReadParams(); } catch { /* leave grid empty; user can re-read */ }
        }
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog { Filter = "Material Instance (*.uasset)|*.uasset|All files|*.*" };
        var start = AppSettings.Current.EffectiveExtractedContentRoot();
        if (Directory.Exists(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) == DialogResult.OK) _baseText.Text = dlg.FileName;
    }

    private void PopulateGeneratedTextureCombo()
    {
        _generatedTextureCombo.Items.Clear();
        if (_generatedTextures.Count == 0)
        {
            _generatedTextureCombo.Items.Add(new GeneratedTextureChoice(null));
            _generatedTextureCombo.SelectedIndex = 0;
            _generatedTextureCombo.Enabled = false;
            return;
        }

        foreach (var texture in _generatedTextures)
        {
            _generatedTextureCombo.Items.Add(new GeneratedTextureChoice(texture));
        }
        _generatedTextureCombo.SelectedIndex = 0;
    }

    private GeneratedTextureEntry? SelectedGeneratedTexture() =>
        _generatedTextureCombo.SelectedItem is GeneratedTextureChoice choice ? choice.Texture : null;

    private void UseGeneratedTextureForSelectedParam()
    {
        var texture = SelectedGeneratedTexture();
        if (texture is null)
        {
            _status.Text = "No generated textures are saved on this suit yet.";
            return;
        }

        var row = _grid.CurrentRow;
        if (row is null || row.IsNewRow)
        {
            _status.Text = "Select a material parameter row first.";
            return;
        }

        row.Cells["YourTexture"].Value = texture.PackagePath;
        var param = row.Cells["Param"].Value?.ToString() ?? "parameter";
        _status.Text = $"Set {param} -> {texture.PackagePath}";
    }

    private void CopySelectedGeneratedTexturePath()
    {
        var texture = SelectedGeneratedTexture();
        if (texture is null)
        {
            _status.Text = "No generated textures are saved on this suit yet.";
            return;
        }

        try
        {
            Clipboard.SetText(texture.PackagePath);
            _status.Text = $"Copied {texture.PackagePath}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void ReadParams()
    {
        var path = _baseText.Text.Trim();
        if (!File.Exists(path))
        {
            _status.Text = $"Base MI not found: {path}";
            return;
        }
        var info = new MaterialGenService(_projectRoot).ReadTemplate(path);
        _grid.Rows.Clear();
        if (info.Status != "ok")
        {
            _status.Text = $"Read failed: {info.Status} {info.Error}";
            return;
        }
        foreach (var p in info.TextureParams)
        {
            _grid.Rows.Add(p.Name, p.CurrentTexturePath, "");
        }
        _status.Text = $"{info.TextureParams.Count} texture params. Fill 'Your texture path' for the ones you want to override.";
    }

    private void Generate()
    {
        var basePath = _baseText.Text.Trim();
        var name = _nameText.Text.Trim();
        if (!File.Exists(basePath)) { _status.Text = "Pick a valid base MI first."; return; }
        if (string.IsNullOrWhiteSpace(name)) { _status.Text = "Enter a material name."; return; }

        var outputPackage = $"/Game/Mods/{_modFolder}/{name}";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var pn = row.Cells["Param"].Value?.ToString() ?? "";
            var tex = row.Cells["YourTexture"].Value?.ToString()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(pn) && !string.IsNullOrWhiteSpace(tex)) map[pn] = tex;
        }
        if (map.Count == 0) { _status.Text = "Enter at least one texture path to override."; return; }

        var result = new MaterialGenService(_projectRoot).Generate(new MaterialGenService.GenRequest
        {
            BaseUassetPath = basePath,
            OutputPackagePath = outputPackage,
            ParamToTexture = map
        });
        if (result.Status != "created")
        {
            _status.Text = $"Generate failed: {result.Status} {result.Error}";
            return;
        }
        ResultMiPackagePath = outputPackage;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed class GeneratedTextureChoice
    {
        public GeneratedTextureEntry? Texture { get; }

        public GeneratedTextureChoice(GeneratedTextureEntry? texture)
        {
            Texture = texture;
        }

        public override string ToString()
        {
            if (Texture is null)
            {
                return "No generated textures on this suit";
            }

            var name = string.IsNullOrWhiteSpace(Texture.DisplayName)
                ? UnrealPathUtil.AssetName(Texture.PackagePath)
                : Texture.DisplayName;
            return $"{name} - {Texture.Kind} - {Texture.PackagePath}";
        }
    }
}
