namespace Batcomputer;

/// <summary>
/// Modal material forge: clone a base MI, map its texture parameters, then generate the new MI.
/// The dialog stays focused while matching the rest of Batcomputer.
/// </summary>
public sealed partial class MaterialWizard : AdaptiveForm
{
    private const string ClearTextureOverrideDisplay = "(None - clear texture)";

    private readonly string _projectRoot;
    private readonly string _modFolder;
    private readonly MaterialTemplateCatalogService.Target? _templateTarget;
    private readonly MaterialTemplateCatalogService _templateCatalog = new();
    private readonly TextBox _baseText = new();
    private readonly TextBox _nameText = new();
    private readonly DataGridView _grid = new();
    private readonly ThemedDropDown _generatedTextureCombo = new();
    private readonly Label _status = new();
    private readonly Label _titleLabel = new();
    private readonly List<GeneratedTextureEntry> _generatedTextures = new();
    private readonly ToolTip _toolTips = new();
    private readonly HashSet<string> _faceHelperAuthoredRows = new(StringComparer.OrdinalIgnoreCase);
    private RoundedPanel? _faceHelpersCard;
    private RoundedPanel? _parametersCard;
    private Panel? _dialogFooter;
    private bool _faceHelpersEnabled;
    private MaterialGenService.MaterialTemplateInfo? _lastTemplateInfo;
    private MaterialTemplateCatalogService.Recipe? _selectedRecipe;

    // These are the same shipped placeholders used by the game's LEGO face defaults.
    // A disabled face layer should point at an inert texture of the correct map type;
    // a null texture reference is less representative of native cooked materials.
    private const string FaceDummyBaseColour = "/Game/Characters/Textures/Shared/EoM/T_Dummy_Alpha_Off.T_Dummy_Alpha_Off";
    private const string FaceDummyNormal = "/Game/Characters/Textures/Shared/T_Dummy_NML.T_Dummy_NML";
    private const string FaceDummySurfaceMask = "/Game/Characters/Textures/Shared/EoM/T_Dummy_MMR.T_Dummy_MMR";

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
    public string? ResultSourceMaterialPackagePath { get; private set; }
    public string? ResultParentMaterialPath { get; private set; }
    public bool ResultIsFaceMaterial { get; private set; }
    public List<GeneratedMaterialResult> ResultGeneratedMaterials { get; } = new();

    public sealed class GeneratedMaterialResult
    {
        public string PackagePath { get; init; } = "";
        public string SourceMaterialPackagePath { get; init; } = "";
        public string ParentMaterialPath { get; init; } = "";
        public bool IsFaceMaterial { get; init; }
        public List<string> CompatibleFaceMeshPackagePaths { get; init; } = new();
        public string TemplateRecipeId { get; init; } = "";
        public string TemplateOutputRole { get; init; } = "";
        public string TemplateGroupId { get; init; } = "";
    }

    public MaterialWizard()
    {
        _projectRoot = "";
        _modFolder = "Suit";
        _templateTarget = null;
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    internal MaterialWizard(
        string projectRoot,
        string modFolder,
        string suggestedName,
        IEnumerable<GeneratedTextureEntry>? generatedTextures = null,
        MaterialTemplateCatalogService.Target? templateTarget = null)
    {
        _projectRoot = projectRoot;
        _modFolder = string.IsNullOrWhiteSpace(modFolder) ? "Suit" : modFolder;
        _templateTarget = templateTarget;
        if (generatedTextures is not null)
        {
            _generatedTextures.AddRange(generatedTextures
                .Where(texture => !string.IsNullOrWhiteSpace(texture.PackagePath))
                .OrderBy(texture => texture.DisplayName, StringComparer.OrdinalIgnoreCase));
        }

        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
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
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Shown += (_, _) => Theme.UseDarkTitleBar(this);

        var header = new Panel
        {
            Left = 0, Top = 0, Width = width, Height = 68, BackColor = Theme.WindowBg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
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
        var baseSurface = MakeInputSurface(_baseText, new Rectangle(14, 31, 314, 34));
        baseSurface.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        sourceCard.Controls.Add(baseSurface);
        var templates = new Button
        {
            Text = "Templates…", Left = 336, Top = 31, Width = 116, Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        Theme.StyleGoldButton(templates);
        templates.Click += (_, _) => SelectTemplate();
        _toolTips.SetToolTip(templates, "Choose a template that matches the target and uses a tested game Material Instance.");
        sourceCard.Controls.Add(templates);
        var browse = new Button
        {
            Text = "Advanced…", Left = 460, Top = 31, Width = 116, Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        Theme.StyleDarkButton(browse);
        browse.Click += (_, _) => Browse();
        _toolTips.SetToolTip(browse, "Clone any extracted game Material Instance directly. Compatibility is your responsibility.");
        sourceCard.Controls.Add(browse);
        var read = new Button
        {
            Text = "Read parameters", Left = 580, Top = 31, Width = 152, Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        Theme.StyleDarkButton(read);
        read.Click += (_, _) => ReadParams();
        sourceCard.Controls.Add(read);

        sourceCard.Controls.Add(MakeFieldLabel("MATERIAL NAME", 14, 74, innerWidth - 28));
        _nameText.Text = suggestedName;
        var nameSurface = MakeInputSurface(_nameText, new Rectangle(14, 92, innerWidth - 28, 34));
        nameSurface.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        sourceCard.Controls.Add(nameSurface);
        sourceCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var textureCard = new RoundedPanel
        {
            Left = padding, Top = 218, Width = innerWidth, Height = 84,
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm
        };
        textureCard.Controls.Add(MakeFieldLabel("GENERATED TEXTURE", 14, 13, innerWidth - 28));
        _generatedTextureCombo.SetBounds(14, 31, 300, 34);
        _generatedTextureCombo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _generatedTextureCombo.Placeholder = "Select generated texture";
        PopulateGeneratedTextureCombo();
        textureCard.Controls.Add(_generatedTextureCombo);
        var useTexture = new Button
        {
            Text = "Use selected", Left = 322, Top = 31, Width = 130, Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        Theme.StyleDarkButton(useTexture);
        useTexture.Click += (_, _) => UseGeneratedTextureForSelectedParam();
        textureCard.Controls.Add(useTexture);
        var clearTexture = new Button
        {
            Text = "Set None",
            Left = 460,
            Top = 31,
            Width = 116,
            Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AccessibleDescription = "Explicitly writes a null texture reference for the selected texture parameter."
        };
        Theme.StyleDarkButton(clearTexture);
        clearTexture.Click += (_, _) => ClearSelectedTextureParam();
        textureCard.Controls.Add(clearTexture);
        var copyTexture = new Button
        {
            Text = "Copy path", Left = 584, Top = 31, Width = 138, Height = 34,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        Theme.StyleDarkButton(copyTexture);
        copyTexture.Click += (_, _) => CopySelectedGeneratedTexturePath();
        textureCard.Controls.Add(copyTexture);
        textureCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _faceHelpersCard = BuildFaceHelpersCard(padding, innerWidth);

        _parametersCard = new RoundedPanel
        {
            Left = padding, Top = 314, Width = innerWidth, Height = 210,
            BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm
        };
        _parametersCard.Controls.Add(MakeFieldLabel("MATERIAL PARAMETERS", 14, 13, innerWidth - 28));
        _grid.SetBounds(14, 37, innerWidth - 28, 148);
        StyleParameterGrid();
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "Type", ReadOnly = true, Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Param", HeaderText = "Parameter", ReadOnly = true, Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "Current value", ReadOnly = true, Width = 210 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Override",
            HeaderText = "Override (blank = inherit)",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _grid.CellDoubleClick += (_, e) => ChooseColourForRow(e.RowIndex);
        _grid.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _grid.Rows.Count)
            {
                _faceHelperAuthoredRows.Remove(FaceHelperRowKey(_grid.Rows[e.RowIndex]));
            }
            RefreshTextureRowWarning(e.RowIndex, showStatus: true);
        };
        _parametersCard.Controls.Add(_grid);
        _status.SetBounds(14, 190, innerWidth - 28, 16);
        _status.Text = "Read a base material to load its parameters.";
        _status.Font = Theme.Caption;
        _status.ForeColor = Theme.OnDarkMuted;
        _status.AutoEllipsis = true;
        _parametersCard.Controls.Add(_status);

        var generate = new Button { Text = "Generate", Width = 104 };
        Theme.StyleGoldButton(generate);
        generate.Click += (_, _) => Generate();
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        _dialogFooter = DialogActionFooter.Create(generate, cancel);

        Controls.Add(header);
        Controls.Add(sourceCard);
        Controls.Add(textureCard);
        Controls.Add(_faceHelpersCard);
        Controls.Add(_parametersCard);
        Controls.Add(_dialogFooter);
        AcceptButton = generate;
        CancelButton = cancel;
        Resize += (_, _) => LayoutResponsiveSections();
        LayoutResponsiveSections();
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
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
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

    private RoundedPanel BuildFaceHelpersCard(int left, int width)
    {
        var card = new RoundedPanel
        {
            Left = left,
            Top = 314,
            Width = width,
            Height = 74,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm,
            Visible = false,
        };
        var helperLabel = MakeFieldLabel("FACE HELPERS", 14, 9, 100);
        helperLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        card.Controls.Add(helperLabel);
        card.Controls.Add(new Label
        {
            Left = 116,
            Top = 8,
            Width = width - 130,
            Height = 17,
            Text = "Uses native visibility controls or the game's inert face textures. Reset restores inherited values.",
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });

        var actions = new FlowLayoutPanel
        {
            Left = 14,
            Top = 30,
            Width = width - 28,
            Height = 34,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        actions.Controls.Add(MakeFaceHelperButton("Hide eyes", 94, FaceHelperAction.Eyes,
            "Hides both eye layers with native controls when available, otherwise with the game's inert face textures."));
        actions.Controls.Add(MakeFaceHelperButton("Hide brows", 110, FaceHelperAction.Brows,
            "Hides both brow layers, including their base-colour, normal, and surface-map variants."));
        actions.Controls.Add(MakeFaceHelperButton("Hide lids / lashes", 158, FaceHelperAction.Eyelids,
            "Hides eyelid and eyelash layers while preserving unrelated eye textures."));
        actions.Controls.Add(MakeFaceHelperButton("Hide mouth", 104, FaceHelperAction.Mouth,
            "Hides mouth, lip, teeth, and tongue layers."));
        actions.Controls.Add(MakeFaceHelperButton("Reset helpers", 128, FaceHelperAction.Reset,
            "Clears only the face-helper changes so those values inherit from the game material again."));
        card.Controls.Add(actions);
        return card;
    }

    private Button MakeFaceHelperButton(string text, int width, FaceHelperAction action, string help)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 32,
            Margin = new Padding(0, 0, 8, 0),
        };
        Theme.StyleDarkButton(button);
        button.Click += (_, _) => ApplyFaceHelper(action);
        _toolTips.SetToolTip(button, help);
        return button;
    }

    private void LayoutResponsiveSections()
    {
        if (_parametersCard is null || _faceHelpersCard is null || _dialogFooter is null)
        {
            return;
        }

        const int padding = 18;
        const int parametersTopWithoutHelpers = 314;
        const int parametersTopWithHelpers = 400;
        const int bottomGap = 12;
        var innerWidth = Math.Max(120, ClientSize.Width - padding * 2);
        var parametersTop = _faceHelpersEnabled ? parametersTopWithHelpers : parametersTopWithoutHelpers;
        var footerTop = Math.Max(parametersTop + 130, ClientSize.Height - DialogActionFooter.StandardHeight);

        _faceHelpersCard.SetBounds(padding, 314, innerWidth, 74);
        _parametersCard.SetBounds(
            padding,
            parametersTop,
            innerWidth,
            Math.Max(130, footerTop - bottomGap - parametersTop));
        _grid.SetBounds(14, 37, Math.Max(120, _parametersCard.Width - 28), Math.Max(68, _parametersCard.Height - 62));
        _status.SetBounds(14, Math.Max(37, _parametersCard.Height - 20), Math.Max(120, _parametersCard.Width - 28), 16);
    }

    internal void ConfigureFaceHelpersForUiAudit()
    {
        var info = new MaterialGenService.MaterialTemplateInfo();
        info.TextureParams.AddRange(new[]
        {
            new MaterialGenService.TextureParam { Name = "BrowL BC", CurrentTexturePath = "T_BROWLEFT_Audit_BC", ObjectPath = "/Game/Audit/T_BROWLEFT_Audit_BC.T_BROWLEFT_Audit_BC" },
            new MaterialGenService.TextureParam { Name = "BrowL NML", CurrentTexturePath = "T_Dummy_NML", ObjectPath = FaceDummyNormal },
            new MaterialGenService.TextureParam { Name = "Eye L BC", CurrentTexturePath = "T_EYELEFT_Audit_BC", ObjectPath = "/Game/Audit/T_EYELEFT_Audit_BC.T_EYELEFT_Audit_BC" },
            new MaterialGenService.TextureParam { Name = "EyelidUpperL BC", CurrentTexturePath = "T_Dummy_Alpha_Off", ObjectPath = FaceDummyBaseColour },
            new MaterialGenService.TextureParam { Name = "Mouth BC", CurrentTexturePath = "T_MOUTH_Audit_BC", ObjectPath = "/Game/Audit/T_MOUTH_Audit_BC.T_MOUTH_Audit_BC" },
            new MaterialGenService.TextureParam { Name = "Tongue BC", CurrentTexturePath = "T_TONGUE_Audit_BC", ObjectPath = "/Game/Audit/T_TONGUE_Audit_BC.T_TONGUE_Audit_BC" },
        });
        info.ColorParams.Add(new MaterialGenService.ColorParam { Name = "BrowR Tint", R = 0.38f, G = 0.33f, B = 0.33f, A = 1f });

        _grid.Rows.Clear();
        _faceHelperAuthoredRows.Clear();
        foreach (var parameter in info.TextureParams)
        {
            _grid.Rows.Add("Texture", parameter.Name, parameter.CurrentTexturePath, "");
        }
        foreach (var parameter in info.ColorParams)
        {
            _grid.Rows.Add("Colour", parameter.Name, DisplayColour(parameter), "");
        }

        UpdateFaceHelpersVisibility(info);
        _status.Text = "6 texture and 1 colour parameters loaded (UI audit sample).";
        ClientSize = new Size(Math.Max(ClientSize.Width, 980), Math.Max(ClientSize.Height, 760));
        LayoutResponsiveSections();
    }

    private enum FaceHelperAction
    {
        Eyes,
        Brows,
        Eyelids,
        Mouth,
        Reset,
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
            var activeRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var supportingAsset = picker.SelectedPackagePath.StartsWith(
                "/Game/Models/Gadgets/",
                StringComparison.OrdinalIgnoreCase);
            var guidance = supportingAsset
                ? "This is a character equipment/glider material stored under Content\\Models\\Gadgets. " +
                  "Run Build > Refresh game assets > Refresh all character assets with the current Batcomputer build, then choose it again."
                : "Run Build > Refresh game assets > Refresh all character assets, then choose it again.";
            Dialog.Warn(this, "Material not extracted",
                $"{picker.SelectedPackagePath} is in the game material catalog, but its cooked .uasset was not found in the active extraction.\n\n" +
                guidance + "\n\n" +
                $"Active extracted Content:\n{activeRoot}\n\n" +
                "An ExtractedPakData folder beside Batcomputer is not used unless it is explicitly selected in Setup.");
            return;
        }

        _selectedRecipe = null;
        _titleLabel.Text = "Create material (advanced clone)";
        _baseText.Text = diskPath;
        ReadParams();
    }

    private void SelectTemplate()
    {
        using var picker = new MaterialTemplatePicker(_templateTarget);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedRecipe is null)
        {
            return;
        }

        var compatibility = _templateCatalog.Evaluate(picker.SelectedRecipe, _templateTarget);
        if (!compatibility.CanUse || compatibility.ResolvedOutputs.Count == 0)
        {
            Dialog.Warn(this, "Template unavailable", compatibility.Detail);
            return;
        }

        _selectedRecipe = picker.SelectedRecipe;
        var primary = compatibility.ResolvedOutputs.FirstOrDefault(output => output.Definition.Primary)
                      ?? compatibility.ResolvedOutputs[0];
        _baseText.Text = primary.DiskPath;
        _titleLabel.Text = $"Create {_selectedRecipe.DisplayName}";
        ReadParams();
        var count = _selectedRecipe.Outputs.Count;
        _status.Text = count == 1
            ? $"Template loaded: {_selectedRecipe.DisplayName}. Edit any overrides, then generate."
            : $"Template loaded: {_selectedRecipe.DisplayName}. Generate will create {count} synchronized outputs.";
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
        _faceHelperAuthoredRows.Remove(FaceHelperRowKey(row));
        row.Cells["Override"].Value = texture.PackagePath;
        var warning = DescribeTextureAssignment(parameter, texture);
        SetTextureRowWarning(row, warning);
        _status.Text = warning is null
            ? $"Set {parameter} -> {texture.PackagePath}"
            : $"Check {parameter}: {warning.ExpectedKind} is usually expected, not {texture.Kind}. You can still generate it.";
    }

    private void ClearSelectedTextureParam()
    {
        var row = _grid.CurrentRow;
        if (row is null || row.IsNewRow)
        {
            _status.Text = "Select a texture parameter row first.";
            return;
        }

        if (!string.Equals(row.Cells["Kind"].Value?.ToString(), "Texture", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Set None is only available for texture parameters.";
            return;
        }

        var parameter = row.Cells["Param"].Value?.ToString() ?? "parameter";
        _faceHelperAuthoredRows.Remove(FaceHelperRowKey(row));
        row.Cells["Override"].Value = ClearTextureOverrideDisplay;
        SetTextureRowClearState(row);
        _status.Text = $"Set {parameter} -> None. The generated material will explicitly clear this texture.";
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
        if (IsClearTextureOverride(overrideValue))
        {
            row.Cells["Override"].Value = ClearTextureOverrideDisplay;
            SetTextureRowClearState(row);
            if (showStatus)
            {
                _status.Text = $"Set {parameter} -> None. The generated material will explicitly clear this texture.";
            }
            return;
        }

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

    private static void SetTextureRowClearState(DataGridViewRow row)
    {
        var overrideCell = row.Cells["Override"];
        overrideCell.ToolTipText = "Writes a true null object reference for this texture parameter. Clear the cell instead to inherit the base material value.";
        overrideCell.Style.ForeColor = Theme.Info;
        overrideCell.Style.SelectionForeColor = Theme.OnDark;
    }

    private static bool IsClearTextureOverride(string? value)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Equals(ClearTextureOverrideDisplay, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("none", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("null", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("(none)", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("<none>", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("clear", StringComparison.OrdinalIgnoreCase);
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
        if (compact.Contains("normal", StringComparison.Ordinal) ||
            compact.Contains("nrm", StringComparison.Ordinal) ||
            compact.Contains("nml", StringComparison.Ordinal) ||
            compact.Contains("dnrm", StringComparison.Ordinal))
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

    private void ApplyFaceHelper(FaceHelperAction action)
    {
        if (action == FaceHelperAction.Reset)
        {
            var reset = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var key = FaceHelperRowKey(row);
                if (!_faceHelperAuthoredRows.Contains(key))
                {
                    continue;
                }

                row.Cells["Override"].Value = "";
                row.Cells["Override"].ToolTipText = "";
                row.Cells["Override"].Style.ForeColor = Theme.OnDark;
                reset++;
            }

            _faceHelperAuthoredRows.Clear();
            _status.Text = reset == 0
                ? "No face-helper overrides need resetting."
                : $"Reset {reset} face-helper override{(reset == 1 ? "" : "s")}; those values now inherit from the donor.";
            return;
        }

        var changed = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (!string.Equals(row.Cells["Kind"].Value?.ToString(), "Scalar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameter = row.Cells["Param"].Value?.ToString() ?? "";
            if (!TryGetFaceHelperValue(parameter, action, out var value))
            {
                continue;
            }

            row.Cells["Override"].Value = value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            row.Cells["Override"].Style.ForeColor = Theme.Info;
            row.Cells["Override"].ToolTipText = "Set by a face helper using this material's native visibility control.";
            _faceHelperAuthoredRows.Add(FaceHelperRowKey(row));
            changed++;
        }

        // Many cooked face instances do not author the runtime visibility scalars even
        // though their parent graph supports them. In that case, mirror the shipped
        // MI_LEGOface defaults by replacing only this feature's texture layers with
        // inert game textures of the matching map type.
        if (changed == 0)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(row.Cells["Kind"].Value?.ToString(), "Texture", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parameter = row.Cells["Param"].Value?.ToString() ?? "";
                if (!IsFaceTextureForAction(parameter, action))
                {
                    continue;
                }

                var dummyPath = FaceDummyTextureFor(parameter);
                row.Cells["Override"].Value = dummyPath;
                row.Cells["Override"].Style.ForeColor = Theme.Info;
                row.Cells["Override"].ToolTipText = "Set by a face helper using a built-in blank texture of the matching map type.";
                _faceHelperAuthoredRows.Add(FaceHelperRowKey(row));
                changed++;
            }
        }

        _status.Text = changed == 0
            ? "This material does not expose recognizable layers for that face helper."
            : $"Applied {FaceHelperDescription(action)} to {changed} face parameter{(changed == 1 ? "" : "s")}.";
    }

    private static string FaceHelperRowKey(DataGridViewRow row) =>
        $"{row.Cells["Kind"].Value?.ToString() ?? ""}|{row.Cells["Param"].Value?.ToString() ?? ""}";

    private static bool IsFaceTextureForAction(string parameter, FaceHelperAction action)
    {
        var compact = new string((parameter ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var isEyelidOrLash = compact.Contains("eyelid", StringComparison.Ordinal) ||
                             compact.Contains("lash", StringComparison.Ordinal);
        var isBrows = compact.Contains("brow", StringComparison.Ordinal);
        var isEyes = compact.Contains("eye", StringComparison.Ordinal) && !isEyelidOrLash && !isBrows;
        var isMouth = compact.Contains("mouth", StringComparison.Ordinal) ||
                      compact.Contains("lip", StringComparison.Ordinal) ||
                      compact.Contains("teeth", StringComparison.Ordinal) ||
                      compact.Contains("tooth", StringComparison.Ordinal) ||
                      compact.Contains("tongue", StringComparison.Ordinal);

        return action switch
        {
            FaceHelperAction.Eyes => isEyes,
            FaceHelperAction.Brows => isBrows,
            FaceHelperAction.Eyelids => isEyelidOrLash,
            FaceHelperAction.Mouth => isMouth,
            FaceHelperAction.Reset => isEyes || isBrows || isEyelidOrLash || isMouth,
            _ => false,
        };
    }

    private static string FaceDummyTextureFor(string parameter) => TextureRoleForParameter(parameter) switch
    {
        TextureRole.Normal => FaceDummyNormal,
        TextureRole.SurfaceMask => FaceDummySurfaceMask,
        _ => FaceDummyBaseColour,
    };

    private static bool TryGetFaceHelperValue(string parameter, FaceHelperAction action, out float value)
    {
        value = 0f;
        var compact = new string((parameter ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var isEyes = compact is "eyelhide" or "eyerhide";
        var isBrows = compact is "browlhide" or "browrhide";
        var isMouth = compact == "mouthhide";
        var isEyelidOrLash = (compact.Contains("eyelid", StringComparison.Ordinal) || compact.Contains("lash", StringComparison.Ordinal)) &&
                             (compact.EndsWith("show", StringComparison.Ordinal) || compact.EndsWith("hide", StringComparison.Ordinal));

        switch (action)
        {
            case FaceHelperAction.Eyes when isEyes:
            case FaceHelperAction.Brows when isBrows:
            case FaceHelperAction.Mouth when isMouth:
                value = 1f;
                return true;
            case FaceHelperAction.Eyelids when isEyelidOrLash:
                value = compact.EndsWith("hide", StringComparison.Ordinal) ? 1f : 0f;
                return true;
            case FaceHelperAction.Reset when isEyes || isBrows || isMouth || isEyelidOrLash:
                return true;
            default:
                return false;
        }
    }

    private static string FaceHelperDescription(FaceHelperAction action) => action switch
    {
        FaceHelperAction.Eyes => "Hide eyes",
        FaceHelperAction.Brows => "Hide brows",
        FaceHelperAction.Eyelids => "Hide eyelids / lashes",
        FaceHelperAction.Mouth => "Hide mouth",
        _ => "face helper",
    };

    private void UpdateFaceHelpersVisibility(MaterialGenService.MaterialTemplateInfo info)
    {
        if (_faceHelpersCard is null)
        {
            return;
        }

        var visible = info.ScalarParams.Any(parameter =>
                          TryGetFaceHelperValue(parameter.Name, FaceHelperAction.Reset, out _)) ||
                      info.TextureParams.Any(parameter =>
                          IsFaceTextureForAction(parameter.Name, FaceHelperAction.Reset));
        if (_faceHelpersEnabled == visible)
        {
            LayoutResponsiveSections();
            return;
        }

        _faceHelpersEnabled = visible;
        _faceHelpersCard.Visible = visible;
        if (visible && ClientSize.Height < 676)
        {
            ClientSize = new Size(ClientSize.Width, 676);
        }
        LayoutResponsiveSections();
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
        _lastTemplateInfo = info.Status == "ok" ? info : null;
        _grid.Rows.Clear();
        _faceHelperAuthoredRows.Clear();
        if (info.Status != "ok")
        {
            UpdateFaceHelpersVisibility(new MaterialGenService.MaterialTemplateInfo());
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
        foreach (var parameter in info.ScalarParams)
        {
            _grid.Rows.Add(
                "Scalar",
                parameter.Name,
                parameter.Value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture),
                "");
        }
        ApplyTemplateDefaults();
        UpdateFaceHelpersVisibility(info);
        _status.Text = $"{info.TextureParams.Count} texture, {info.ColorParams.Count} colour, and {info.ScalarParams.Count} scalar parameters loaded.";
    }

    private void ApplyTemplateDefaults()
    {
        if (_selectedRecipe?.DefaultTextureOverrides.Count is not > 0)
        {
            return;
        }

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (!string.Equals(row.Cells["Kind"].Value?.ToString(), "Texture", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var parameter = row.Cells["Param"].Value?.ToString() ?? "";
            if (!_selectedRecipe.DefaultTextureOverrides.TryGetValue(parameter, out var texturePath))
            {
                continue;
            }
            row.Cells["Override"].Value = texturePath;
            row.Cells["Override"].Style.ForeColor = Theme.Info;
            row.Cells["Override"].ToolTipText = "Set by the selected game-material template.";
        }
    }

    private void Generate()
    {
        var basePath = _baseText.Text.Trim();
        var recipeIsFace = _selectedRecipe?.IsFace == true;
        var name = NormalizeMaterialAssetName(_nameText.Text, recipeIsFace || _faceHelpersEnabled);
        if (!File.Exists(basePath)) { _status.Text = "Pick a valid base material first."; return; }
        if (string.IsNullOrWhiteSpace(name)) { _status.Text = "Enter a material name."; return; }

        _nameText.Text = name;

        var textureMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clearedTextureParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colourMap = new Dictionary<string, MaterialGenService.ColorParam>(StringComparer.OrdinalIgnoreCase);
        var scalarMap = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
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
                if (IsClearTextureOverride(overrideValue))
                {
                    clearedTextureParams.Add(parameter);
                }
                else
                {
                    textureMap[parameter] = overrideValue;
                }
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
            else if (string.Equals(kind, "Scalar", StringComparison.OrdinalIgnoreCase))
            {
                if (!float.TryParse(overrideValue, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var scalar) || !float.IsFinite(scalar))
                {
                    _status.Text = $"{parameter} needs a finite numeric scalar value.";
                    return;
                }
                scalarMap[parameter] = scalar;
            }
        }
        if (_selectedRecipe is null && textureMap.Count == 0 && clearedTextureParams.Count == 0 && colourMap.Count == 0 && scalarMap.Count == 0)
        {
            _status.Text = "Enter an override before generating.";
            return;
        }

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

        var catalogCompatibility = _selectedRecipe is null
            ? null
            : _templateCatalog.Evaluate(_selectedRecipe, _templateTarget);
        if (catalogCompatibility is { CanUse: false })
        {
            _status.Text = $"Generate blocked: {catalogCompatibility.Status}. {catalogCompatibility.Detail}";
            return;
        }

        var outputs = catalogCompatibility?.ResolvedOutputs?.Count > 0
            ? catalogCompatibility.ResolvedOutputs
            : new[]
            {
                new MaterialTemplateCatalogService.ResolvedOutput(
                    new MaterialTemplateCatalogService.Output("custom clone", "", _lastTemplateInfo?.SourcePackagePath ?? "", true),
                    basePath),
            };
        var generator = new MaterialGenService(_projectRoot);
        var generatedPackages = new List<string>();
        var templateGroupId = outputs.Count > 1 ? Guid.NewGuid().ToString("N") : "";
        ResultGeneratedMaterials.Clear();

        foreach (var output in outputs)
        {
            var outputName = MaterialOutputName(name, output.Definition.NameSuffix);
            var outputPackage = $"/Game/Mods/{_modFolder}/{outputName}";
            var outputInfo = generator.ReadTemplate(output.DiskPath);
            if (!outputInfo.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                RemoveGeneratedOutputs(generatedPackages.Append(outputPackage));
                _status.Text = $"Generate failed: could not read {output.Definition.Role} donor ({outputInfo.Status}).";
                return;
            }

            var textureNames = outputInfo.TextureParams.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var colourNames = outputInfo.ColorParams.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scalarNames = outputInfo.ScalarParams.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outputTextureMap = textureMap
                .Where(pair => textureNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var outputClears = clearedTextureParams
                .Where(textureNames.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outputColourMap = colourMap
                .Where(pair => colourNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var outputScalarMap = scalarMap
                .Where(pair => scalarNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            var result = generator.Generate(new MaterialGenService.GenRequest
            {
                BaseUassetPath = output.DiskPath,
                OutputPackagePath = outputPackage,
                ParamToTexture = outputTextureMap,
                TextureParamsToClear = outputClears,
                ParamToColor = outputColourMap,
                ParamToScalar = outputScalarMap,
            });
            if (result.Status != "created")
            {
                RemoveGeneratedOutputs(generatedPackages.Append(outputPackage));
                ResultGeneratedMaterials.Clear();
                _status.Text = $"Generate failed for {output.Definition.Role}: {result.Status} {result.Error}";
                return;
            }

            generatedPackages.Add(outputPackage);
            var isFace = recipeIsFace ||
                         outputInfo.ParentMaterialPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase) ||
                         outputName.StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase);
            ResultGeneratedMaterials.Add(new GeneratedMaterialResult
            {
                PackagePath = outputPackage,
                SourceMaterialPackagePath = UnrealPathUtil.NormalizePackagePath(outputInfo.SourcePackagePath),
                ParentMaterialPath = UnrealPathUtil.NormalizePackagePath(outputInfo.ParentMaterialPath),
                IsFaceMaterial = isFace,
                CompatibleFaceMeshPackagePaths = isFace
                    ? (_selectedRecipe?.CompatibleMeshPackagePaths ?? Array.Empty<string>())
                        .Select(UnrealPathUtil.NormalizePackagePath)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>(),
                TemplateRecipeId = _selectedRecipe?.Id ?? "",
                TemplateOutputRole = output.Definition.Role,
                TemplateGroupId = templateGroupId,
            });
        }

        var primary = ResultGeneratedMaterials
                          .Where((_, index) => outputs[index].Definition.Primary)
                          .FirstOrDefault()
                      ?? ResultGeneratedMaterials.First();
        ResultMiPackagePath = primary.PackagePath;
        ResultSourceMaterialPackagePath = primary.SourceMaterialPackagePath;
        ResultParentMaterialPath = primary.ParentMaterialPath;
        ResultIsFaceMaterial = primary.IsFaceMaterial;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string MaterialOutputName(string baseName, string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix) || baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return baseName;
        }
        return baseName + suffix;
    }

    private static void RemoveGeneratedOutputs(IEnumerable<string> packagePaths)
    {
        var configuredRoot = AppSettings.Current.EffectiveExportContentRoot();
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return;
        }
        var root = Path.GetFullPath(configuredRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var packagePath in packagePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var package = UnrealPathUtil.NormalizePackagePath(packagePath);
            if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var relative = package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var outputBase = Path.GetFullPath(Path.Combine(root, relative));
            if (!outputBase.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                try { File.Delete(outputBase + extension); }
                catch { /* Best-effort rollback; the failure is reported by the generator. */ }
            }
        }
    }

    private static string NormalizeMaterialAssetName(string? value, bool faceMaterial)
    {
        var raw = (value ?? "").Trim();
        if (raw.Contains('/') || raw.Contains('\\'))
        {
            raw = raw.Replace('\\', '/');
            raw = raw[(raw.LastIndexOf('/') + 1)..];
        }

        var token = new string(raw
            .Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')
            .ToArray());
        while (token.Contains("__", StringComparison.Ordinal))
        {
            token = token.Replace("__", "_", StringComparison.Ordinal);
        }
        token = token.Trim('_');
        if (string.IsNullOrWhiteSpace(token))
        {
            return "";
        }

        if (faceMaterial)
        {
            if (token.StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
            if (token.StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
            {
                token = token[3..];
            }
            return "MI_FACE_" + token;
        }

        return token.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
            ? token
            : "MI_" + token;
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
