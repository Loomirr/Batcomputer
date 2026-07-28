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

    private enum TextureRole
    {
        Unknown,
        BaseColour,
        Normal,
        SurfaceMask,
        ColourMask,
        UiIcon,
    }

    private sealed record TextureAssignmentWarning(
        string Parameter,
        GeneratedTextureEntry Texture,
        TextureRole ExpectedRole,
        string ExpectedKind);

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
        parametersCard.Controls.Add(MakeFieldLabel("MATERIAL PARAMETERS", 14, 13, innerWidth - 28));
        _grid.SetBounds(14, 37, innerWidth - 28, 148);
        StyleParameterGrid();
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "Type", ReadOnly = true, Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Param", HeaderText = "Parameter", ReadOnly = true, Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "Current value", ReadOnly = true, Width = 210 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Override", HeaderText = "Override", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.CellDoubleClick += (_, e) => ChooseColourForRow(e.RowIndex);
        _grid.CellEndEdit += (_, e) => RefreshTextureRowWarning(e.RowIndex, showStatus: true);
        parametersCard.Controls.Add(_grid);
        _status.SetBounds(14, 190, innerWidth - 28, 16);
        _status.Text = "Read a base material to load its parameters.";
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

        if (!string.Equals(row.Cells["Kind"].Value?.ToString(), "Texture", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Select a texture parameter row first.";
            return;
        }

        var parameter = row.Cells["Param"].Value?.ToString() ?? "parameter";
        row.Cells["Override"].Value = texture.PackagePath;
        var warning = DescribeTextureAssignment(parameter, texture);
        SetTextureRowWarning(row, warning);
        _status.Text = warning is null
            ? $"Set {parameter} -> {texture.PackagePath}"
            : $"Check {parameter}: {warning.ExpectedKind} is usually expected, not {texture.Kind}. You can still generate it.";
    }

    private void RefreshTextureRowWarning(int rowIndex, bool showStatus)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count)
        {
            return;
        }

        var row = _grid.Rows[rowIndex];
        if (!string.Equals(row.Cells["Kind"].Value?.ToString(), "Texture", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var parameter = row.Cells["Param"].Value?.ToString() ?? "";
        var overrideValue = row.Cells["Override"].Value?.ToString()?.Trim() ?? "";
        var texture = FindGeneratedTexture(overrideValue);
        var warning = texture is null ? null : DescribeTextureAssignment(parameter, texture);
        SetTextureRowWarning(row, warning);
        if (showStatus && warning is not null)
        {
            _status.Text = $"Check {parameter}: {warning.ExpectedKind} is usually expected, not {texture!.Kind}. You can still generate it.";
        }
    }

    private static void SetTextureRowWarning(DataGridViewRow row, TextureAssignmentWarning? warning)
    {
        var overrideCell = row.Cells["Override"];
        overrideCell.ToolTipText = warning is null
            ? ""
            : $"Usually expects {warning.ExpectedKind}; selected texture is classified as {warning.Texture.Kind}.";
        overrideCell.Style.ForeColor = warning is null ? Theme.OnDark : Theme.Warn;
        overrideCell.Style.SelectionForeColor = Theme.OnDark;
    }

    private GeneratedTextureEntry? FindGeneratedTexture(string packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return _generatedTextures.FirstOrDefault(texture =>
            string.Equals(UnrealPathUtil.NormalizePackagePath(texture.PackagePath), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(UnrealPathUtil.NormalizePackagePath(texture.ObjectPath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static TextureAssignmentWarning? DescribeTextureAssignment(string parameter, GeneratedTextureEntry texture)
    {
        var role = TextureRoleForParameter(parameter);
        var textureKind = TextureKindFor(texture.Kind);
        if (role == TextureRole.Unknown || textureKind == TextureRole.Unknown || TextureKindMatchesRole(textureKind, role))
        {
            return null;
        }

        return new TextureAssignmentWarning(parameter, texture, role, ExpectedTextureKind(role));
    }

    private static TextureRole TextureRoleForParameter(string parameter)
    {
        var compact = new string((parameter ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (compact.Contains("normal", StringComparison.Ordinal) || compact.Contains("nrm", StringComparison.Ordinal))
        {
            return TextureRole.Normal;
        }
        if (compact.Contains("mmr", StringComparison.Ordinal) || compact.Contains("orm", StringComparison.Ordinal) ||
            compact.Contains("rao", StringComparison.Ordinal) || compact.Contains("rough", StringComparison.Ordinal) ||
            compact.Contains("metal", StringComparison.Ordinal) || compact.Contains("spec", StringComparison.Ordinal))
        {
            return TextureRole.SurfaceMask;
        }
        if (compact.Contains("colourmask", StringComparison.Ordinal) || compact.Contains("colormask", StringComparison.Ordinal))
        {
            return TextureRole.ColourMask;
        }
        if (compact.Contains("basecolour", StringComparison.Ordinal) || compact.Contains("basecolor", StringComparison.Ordinal) ||
            compact.Contains("albedo", StringComparison.Ordinal) || compact.Contains("diffuse", StringComparison.Ordinal) ||
            compact.Equals("bc", StringComparison.Ordinal) || compact.EndsWith("bc", StringComparison.Ordinal))
        {
            return TextureRole.BaseColour;
        }

        return TextureRole.Unknown;
    }

    private static TextureRole TextureKindFor(string? textureKind)
    {
        var kind = textureKind ?? "";
        if (kind.Contains("normal", StringComparison.OrdinalIgnoreCase)) return TextureRole.Normal;
        if (kind.Contains("rough", StringComparison.OrdinalIgnoreCase) || kind.Contains("spec", StringComparison.OrdinalIgnoreCase)) return TextureRole.SurfaceMask;
        if (kind.Contains("color mask", StringComparison.OrdinalIgnoreCase) || kind.Contains("colour mask", StringComparison.OrdinalIgnoreCase)) return TextureRole.ColourMask;
        if (kind.Contains("character", StringComparison.OrdinalIgnoreCase)) return TextureRole.BaseColour;
        if (kind.Contains("ui", StringComparison.OrdinalIgnoreCase) || kind.Contains("icon", StringComparison.OrdinalIgnoreCase)) return TextureRole.UiIcon;
        return TextureRole.Unknown;
    }

    private static bool TextureKindMatchesRole(TextureRole textureKind, TextureRole expectedRole) => textureKind == expectedRole;

    private static string ExpectedTextureKind(TextureRole role) => role switch
    {
        TextureRole.BaseColour => "a Character texture",
        TextureRole.Normal => "a Normal map",
        TextureRole.SurfaceMask => "a Roughness/spec mask",
        TextureRole.ColourMask => "a Color mask",
        _ => "a compatible texture",
    };

    private List<TextureAssignmentWarning> FindTextureAssignmentWarnings(IReadOnlyDictionary<string, string> textureMap) =>
        textureMap
            .Select(pair => new { pair.Key, Texture = FindGeneratedTexture(pair.Value) })
            .Where(item => item.Texture is not null)
            .Select(item => DescribeTextureAssignment(item.Key, item.Texture!))
            .Where(warning => warning is not null)
            .Cast<TextureAssignmentWarning>()
            .ToList();

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
            _grid.Rows.Add("Texture", parameter.Name, parameter.CurrentTexturePath, "");
        }
        foreach (var parameter in info.ColorParams)
        {
            _grid.Rows.Add("Colour", parameter.Name, DisplayColour(parameter), "");
        }
        _status.Text = $"{info.TextureParams.Count} texture and {info.ColorParams.Count} colour parameters loaded.";
    }

    private void Generate()
    {
        var basePath = _baseText.Text.Trim();
        var name = _nameText.Text.Trim();
        if (!File.Exists(basePath)) { _status.Text = "Pick a valid base material first."; return; }
        if (string.IsNullOrWhiteSpace(name)) { _status.Text = "Enter a material name."; return; }

        var outputPackage = $"/Game/Mods/{_modFolder}/{name}";
        var textureMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var colourMap = new Dictionary<string, MaterialGenService.ColorParam>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var kind = row.Cells["Kind"].Value?.ToString() ?? "";
            var parameter = row.Cells["Param"].Value?.ToString() ?? "";
            var overrideValue = row.Cells["Override"].Value?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(parameter) || string.IsNullOrWhiteSpace(overrideValue))
            {
                continue;
            }
            if (string.Equals(kind, "Texture", StringComparison.OrdinalIgnoreCase))
            {
                textureMap[parameter] = overrideValue;
            }
            else if (string.Equals(kind, "Colour", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseColour(overrideValue, out var colour))
                {
                    _status.Text = $"{parameter} needs #RRGGBB or linear R,G,B[,A].";
                    return;
                }
                colourMap[parameter] = colour;
            }
        }
        if (textureMap.Count == 0 && colourMap.Count == 0) { _status.Text = "Enter an override before generating."; return; }

        var assignmentWarnings = FindTextureAssignmentWarnings(textureMap);
        if (assignmentWarnings.Count > 0)
        {
            var details = string.Join("\n", assignmentWarnings.Select(warning =>
                $"{warning.Parameter}: {warning.Texture.DisplayName} is {warning.Texture.Kind}; usually expects {warning.ExpectedKind}."));
            if (!Dialog.Confirm(this, "Check material texture roles",
                    $"These generated textures do not match the usual role for their material parameter:\n\n{details}\n\nThis is a warning only. Custom material graphs can legitimately use a different texture role.",
                    confirmText: "Generate anyway", cancelText: "Review assignments", severity: Dialog.Level.Warn))
            {
                return;
            }
        }

        var result = new MaterialGenService(_projectRoot).Generate(new MaterialGenService.GenRequest
        {
            BaseUassetPath = basePath,
            OutputPackagePath = outputPackage,
            ParamToTexture = textureMap,
            ParamToColor = colourMap,
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

    private void ChooseColourForRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count)
        {
            return;
        }

        var row = _grid.Rows[rowIndex];
        if (!string.Equals(row.Cells["Kind"].Value?.ToString(), "Colour", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existing = row.Cells["Override"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(existing))
        {
            existing = row.Cells["Current"].Value?.ToString();
        }
        var initial = TryParseColour(existing ?? "", out var colour) ? ToDisplayColour(colour) : Color.White;
        using var picker = new ColorDialog { Color = initial, FullOpen = true };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        row.Cells["Override"].Value = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        _status.Text = $"Set {row.Cells["Param"].Value} colour.";
    }

    private static string DisplayColour(MaterialGenService.ColorParam colour)
    {
        var display = ToDisplayColour(colour);
        return $"#{display.R:X2}{display.G:X2}{display.B:X2}";
    }

    private static Color ToDisplayColour(MaterialGenService.ColorParam colour) => Color.FromArgb(
        ToSrgbByte(colour.R), ToSrgbByte(colour.G), ToSrgbByte(colour.B));

    private static int ToSrgbByte(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        var srgb = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (int)Math.Clamp(MathF.Round(srgb * 255f), 0f, 255f);
    }

    private static bool TryParseColour(string value, out MaterialGenService.ColorParam colour)
    {
        colour = new MaterialGenService.ColorParam();
        value = value.Trim();
        if (value.Length == 7 && value[0] == '#' &&
            byte.TryParse(value[1..3], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(value[3..5], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(value[5..7], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            colour.R = SrgbToLinear(r / 255f);
            colour.G = SrgbToLinear(g / 255f);
            colour.B = SrgbToLinear(b / 255f);
            colour.A = 1f;
            return true;
        }

        var values = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length is < 3 or > 4 || !float.TryParse(values[0], System.Globalization.CultureInfo.InvariantCulture, out var linearR) ||
            !float.TryParse(values[1], System.Globalization.CultureInfo.InvariantCulture, out var linearG) ||
            !float.TryParse(values[2], System.Globalization.CultureInfo.InvariantCulture, out var linearB))
        {
            return false;
        }

        var linearA = 1f;
        if (values.Length == 4 && !float.TryParse(values[3], System.Globalization.CultureInfo.InvariantCulture, out linearA))
        {
            return false;
        }

        colour.R = Math.Clamp(linearR, 0f, 1f);
        colour.G = Math.Clamp(linearG, 0f, 1f);
        colour.B = Math.Clamp(linearB, 0f, 1f);
        colour.A = Math.Clamp(linearA, 0f, 1f);
        return true;
    }

    private static float SrgbToLinear(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

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
