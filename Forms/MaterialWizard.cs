namespace Batcomputer;

/// <summary>
/// Modal material forge: clone a base MI, map its texture parameters, then generate the new MI.
/// The workflow remains deliberately small, while the surface matches the rest of Batcomputer.
/// </summary>
public sealed partial class MaterialWizard : Form
{
    private readonly string _projectRoot;
    private readonly string _modFolder;
    private readonly TextBox _baseText = new();
    private readonly TextBox _nameText = new();
    private readonly DataGridView _grid = new();
    private readonly ThemedDropDown _generatedTextureCombo = new();
    private readonly Label _status = new();
    private readonly Label _titleLabel = new();
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
                .Where(texture => !string.IsNullOrWhiteSpace(texture.PackagePath))
                .OrderBy(texture => texture.DisplayName, StringComparer.OrdinalIgnoreCase));
        }

        InitializeComponent();
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        BuildModernLayout(suggestedName);
    }

    private void BuildModernLayout(string suggestedName)
    {
        const int width = 780;
        const int padding = 18;
        const int innerWidth = width - padding * 2;
        Controls.Clear();

        Text = "Batcomputer - Material forge";
        ClientSize = new Size(width, 590);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Shown += (_, _) => Theme.UseDarkTitleBar(this);

        var header = new Panel { Left = 0, Top = 0, Width = width, Height = 68, BackColor = Theme.WindowBg };
        header.Controls.Add(new Panel { Left = padding, Top = 17, Width = 3, Height = 36, BackColor = Theme.Materials });
        header.Controls.Add(new Label
        {
            Left = padding + 12, Top = 13, Width = innerWidth - 12, Height = 16,
            Text = "MATERIALS", Font = Theme.Eyebrow, ForeColor = Theme.Materials
        });
        _titleLabel.SetBounds(padding + 12, 30, innerWidth - 12, 22);
        _titleLabel.Text = "Create material";
        _titleLabel.Font = Theme.Heading;
        _titleLabel.ForeColor = Theme.OnDark;
        header.Controls.Add(_titleLabel);

        var sourceCard = new RoundedPanel
        {
            Left = padding, Top = 76, Width = innerWidth, Height = 130,
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm
        };
        sourceCard.Controls.Add(MakeFieldLabel("BASE MATERIAL", 14, 13, innerWidth - 28));
        sourceCard.Controls.Add(MakeInputSurface(_baseText, new Rectangle(14, 31, 500, 34)));
        var browse = new Button { Text = "Browse...", Left = 522, Top = 31, Width = 88, Height = 34 };
        Theme.StyleDarkButton(browse);
        browse.Click += (_, _) => Browse();
        sourceCard.Controls.Add(browse);
        var read = new Button { Text = "Read parameters", Left = 618, Top = 31, Width = 104, Height = 34 };
        Theme.StyleDarkButton(read);
        read.Click += (_, _) => ReadParams();
        sourceCard.Controls.Add(read);

        sourceCard.Controls.Add(MakeFieldLabel("MATERIAL NAME", 14, 74, innerWidth - 28));
        _nameText.Text = suggestedName;
        sourceCard.Controls.Add(MakeInputSurface(_nameText, new Rectangle(14, 92, innerWidth - 28, 34)));

        var textureCard = new RoundedPanel
        {
            Left = padding, Top = 218, Width = innerWidth, Height = 84,
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm
        };
        textureCard.Controls.Add(MakeFieldLabel("GENERATED TEXTURE", 14, 13, innerWidth - 28));
        _generatedTextureCombo.SetBounds(14, 31, 370, 34);
        _generatedTextureCombo.Placeholder = "Select generated texture";
        PopulateGeneratedTextureCombo();
        textureCard.Controls.Add(_generatedTextureCombo);
        var useTexture = new Button { Text = "Use selected", Left = 392, Top = 31, Width = 170, Height = 34 };
        Theme.StyleDarkButton(useTexture);
        useTexture.Click += (_, _) => UseGeneratedTextureForSelectedParam();
        textureCard.Controls.Add(useTexture);
        var copyTexture = new Button { Text = "Copy path", Left = 570, Top = 31, Width = 152, Height = 34 };
        Theme.StyleDarkButton(copyTexture);
        copyTexture.Click += (_, _) => CopySelectedGeneratedTexturePath();
        textureCard.Controls.Add(copyTexture);

        var parametersCard = new RoundedPanel
        {
            Left = padding, Top = 314, Width = innerWidth, Height = 210,
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm
        };
        parametersCard.Controls.Add(MakeFieldLabel("TEXTURE PARAMETERS", 14, 13, innerWidth - 28));
        _grid.SetBounds(14, 37, innerWidth - 28, 148);
        StyleParameterGrid();
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Param", HeaderText = "Parameter", ReadOnly = true, Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "Current texture", ReadOnly = true, Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "YourTexture", HeaderText = "Your texture path (/Game/...)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        parametersCard.Controls.Add(_grid);
        _status.SetBounds(14, 190, innerWidth - 28, 16);
        _status.Text = "Read a base material to load its texture parameters.";
        _status.Font = Theme.Caption;
        _status.ForeColor = Theme.OnDarkMuted;
        _status.AutoEllipsis = true;
        parametersCard.Controls.Add(_status);

        var footer = new Panel { Left = 0, Top = 536, Width = width, Height = 54, BackColor = Theme.SlateDark };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var generate = new Button { Text = "Generate", Width = 104, Height = 32, Left = width - padding - 104, Top = 11 };
        Theme.StyleGoldButton(generate);
        generate.Click += (_, _) => Generate();
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 32, Left = width - padding - 104 - 98, Top = 11, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        footer.Controls.Add(generate);
        footer.Controls.Add(cancel);

        Controls.Add(header);
        Controls.Add(sourceCard);
        Controls.Add(textureCard);
        Controls.Add(parametersCard);
        Controls.Add(footer);
        AcceptButton = generate;
        CancelButton = cancel;
        _baseText.Select();
    }

    private static Label MakeFieldLabel(string text, int left, int top, int width) => new()
    {
        Left = left,
        Top = top,
        Width = width,
        Height = 15,
        Text = text,
        Font = Theme.Eyebrow,
        ForeColor = Theme.OnDarkMuted,
        BackColor = Color.Transparent,
    };

    private static RoundedPanel MakeInputSurface(TextBox input, Rectangle bounds)
    {
        var surface = new RoundedPanel
        {
            Bounds = bounds,
            BackColor = Theme.Slate,
            BorderColor = Theme.SlateLight,
            CornerRadius = Theme.RadiusSm,
        };
        input.BorderStyle = BorderStyle.None;
        input.BackColor = Theme.Slate;
        input.ForeColor = Theme.OnDark;
        input.Font = Theme.Body;
        input.Left = 10;
        input.Top = (surface.Height - input.Height) / 2;
        input.Width = surface.Width - 20;
        input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        surface.Controls.Add(input);
        return surface;
    }

    private void StyleParameterGrid()
    {
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoGenerateColumns = false;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _grid.EnableHeadersVisualStyles = false;
        _grid.BackgroundColor = Theme.SlateDark;
        _grid.GridColor = Theme.LineSoft;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Slate,
            ForeColor = Theme.OnDarkMuted,
            SelectionBackColor = Theme.Slate,
            SelectionForeColor = Theme.OnDarkMuted,
            Font = Theme.Caption,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.CardBg,
            ForeColor = Theme.OnDark,
            SelectionBackColor = Theme.Tint(Theme.Materials),
            SelectionForeColor = Theme.OnDark,
            Font = Theme.Body,
            Padding = new Padding(4, 0, 4, 0),
        };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Slate,
            ForeColor = Theme.OnDark,
        };
        _grid.RowTemplate.Height = 28;
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

        Text = editInPlace ? "Batcomputer - Edit material" : "Batcomputer - New material from base";
        _titleLabel.Text = editInPlace ? "Edit material" : "New material from base";
        if (File.Exists(baseUassetDiskPath))
        {
            try { ReadParams(); } catch { /* Leave the grid empty; user can re-read. */ }
        }
    }

    private void Browse()
    {
        using var picker = new MaterialCatalogPicker();
        if (picker.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedPackagePath))
        {
            return;
        }

        var diskPath = MainForm.ResolveMiDiskPath(picker.SelectedPackagePath, preferExport: false);
        if (diskPath is null)
        {
            Dialog.Warn(this, "Material not extracted",
                $"{picker.SelectedPackagePath} is in the shipped catalog, but its cooked .uasset was not found under your extracted content root. Extract that character's content, then choose the material again.");
            return;
        }

        _baseText.Text = diskPath;
        ReadParams();
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

        _generatedTextureCombo.Enabled = true;
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
        var parameter = row.Cells["Param"].Value?.ToString() ?? "parameter";
        _status.Text = $"Set {parameter} -> {texture.PackagePath}";
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

        foreach (var parameter in info.TextureParams)
        {
            _grid.Rows.Add(parameter.Name, parameter.CurrentTexturePath, "");
        }
        _status.Text = $"{info.TextureParams.Count} texture parameters loaded. Set paths only for the parameters you want to override.";
    }

    private void Generate()
    {
        var basePath = _baseText.Text.Trim();
        var name = _nameText.Text.Trim();
        if (!File.Exists(basePath)) { _status.Text = "Pick a valid base material first."; return; }
        if (string.IsNullOrWhiteSpace(name)) { _status.Text = "Enter a material name."; return; }

        var outputPackage = $"/Game/Mods/{_modFolder}/{name}";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var parameter = row.Cells["Param"].Value?.ToString() ?? "";
            var texture = row.Cells["YourTexture"].Value?.ToString()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(parameter) && !string.IsNullOrWhiteSpace(texture))
            {
                map[parameter] = texture;
            }
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
            var cook = Texture.CookWidth > 0 && Texture.CookHeight > 0 && !string.IsNullOrWhiteSpace(Texture.CookPixelFormat)
                ? $"{Texture.CookWidth}x{Texture.CookHeight} {Texture.CookPixelFormat}"
                : Texture.Kind;
            return $"{name} - {cook}";
        }
    }
}
