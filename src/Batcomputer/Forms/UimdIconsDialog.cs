namespace Batcomputer;

/// <summary>Edits the four icon paths stored by a suit's UIMD.</summary>
public sealed partial class UimdIconsDialog : Form
{
    private readonly TextBox _menu = new();
    private readonly TextBox _suit = new();
    private readonly TextBox _left = new();
    private readonly TextBox _right = new();

    public string IconMenu => _menu.Text.Trim();
    public string IconSuit => _suit.Text.Trim();
    public string IconLeft => _left.Text.Trim();
    public string IconRight => _right.Text.Trim();

    public UimdIconsDialog()
    {
        InitializeComponent();
    }

    public UimdIconsDialog(
        string donorUimd,
        NativeMetadataDonorService.Icons donorIcons,
        string menu,
        string suit,
        string left,
        string right,
        IEnumerable<GeneratedTextureEntry>? generatedUiTextures = null)
    {
        InitializeComponent();
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();
        Text = "Suit icons";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 478);
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;

        _menu.Text = FirstNonEmpty(menu, donorIcons.Menu);
        _suit.Text = FirstNonEmpty(suit, donorIcons.Suit);
        _left.Text = FirstNonEmpty(left, donorIcons.Left);
        _right.Text = FirstNonEmpty(right, donorIcons.Right);
        var generatedTextures = (generatedUiTextures ?? Enumerable.Empty<GeneratedTextureEntry>()).ToList();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = Theme.WindowBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        header.Controls.Add(new Label
        {
            Text = "UIMD",
            Dock = DockStyle.Top,
            Height = 18,
            Font = Theme.Eyebrow,
            ForeColor = Theme.Textures,
        });
        header.Controls.Add(new Label
        {
            Text = "Suit icons",
            Dock = DockStyle.Top,
            Height = 29,
            Font = Theme.Heading,
            ForeColor = Theme.OnDark,
        });
        header.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(donorUimd)
                ? "No donor UIMD was found. Add only icon textures that your mod ships."
                : $"Starting from {UnrealPathUtil.AssetName(donorUimd)}. Keep a base-game path unless you are replacing that icon.",
            Dock = DockStyle.Fill,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            AutoEllipsis = true,
        });
        root.Controls.Add(header, 0, 0);

        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 0, 0, 12),
        };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent };
        for (var i = 0; i < 4; i++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        }
        fields.Controls.Add(Row("Menu icon", "Character menu portrait", _menu, generatedTextures), 0, 0);
        fields.Controls.Add(Row("Suit icon", "Suit selector tile", _suit, generatedTextures), 0, 1);
        fields.Controls.Add(Row("Left-facing", "Character-card left view", _left, generatedTextures), 0, 2);
        fields.Controls.Add(Row("Right-facing", "Character-card right view", _right, generatedTextures), 0, 3);
        card.Controls.Add(fields);
        root.Controls.Add(card, 0, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.Transparent,
        };
        var save = new Button { Text = "Save icons", Width = 112, Height = 32 };
        Theme.StyleGoldButton(save);
        save.Click += (_, _) =>
        {
            foreach (var path in new[] { IconMenu, IconSuit, IconLeft, IconRight })
            {
                if (!string.IsNullOrWhiteSpace(path) && !path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                {
                    Dialog.Warn(this, "Suit icons", "Each icon path must start with /Game/ or be blank.");
                    return;
                }
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button { Text = "Cancel", Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        root.Controls.Add(footer, 0, 2);
        AcceptButton = save;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(this);
    }

    private static string FirstNonEmpty(string current, string donor) =>
        !string.IsNullOrWhiteSpace(current) ? current : donor;

    private static Control Row(
        string title,
        string detail,
        TextBox input,
        IReadOnlyList<GeneratedTextureEntry> generatedTextures)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        var label = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        label.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 20,
            Font = Theme.BodyStrong,
            ForeColor = Theme.OnDark,
        });
        label.Controls.Add(new Label
        {
            Text = detail,
            Dock = DockStyle.Top,
            Height = 18,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
        });
        row.Controls.Add(label, 0, 0);

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 8, 0, 8);
        Theme.StyleDarkInput(input);
        row.Controls.Add(input, 1, 0);

        var picker = new ThemedDropDown
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 8, 0, 8),
            Placeholder = generatedTextures.Count == 0 ? "No generated UI textures" : "Use generated UI texture",
            Enabled = generatedTextures.Count > 0,
        };
        picker.Items.Add(new GeneratedUiTextureChoice(null));
        foreach (var texture in generatedTextures)
        {
            picker.Items.Add(new GeneratedUiTextureChoice(texture));
        }

        var currentPath = UnrealPathUtil.NormalizePackagePath(input.Text);
        var currentIndex = -1;
        for (var index = 0; index < generatedTextures.Count; index++)
        {
            if (string.Equals(
                    UnrealPathUtil.NormalizePackagePath(generatedTextures[index].PackagePath),
                    currentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = index;
                break;
            }
        }
        picker.SelectedIndex = currentIndex >= 0 ? currentIndex + 1 : -1;
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (picker.SelectedItem is GeneratedUiTextureChoice { Texture: { } texture })
            {
                input.Text = texture.PackagePath;
            }
        };
        row.Controls.Add(picker, 2, 0);
        return row;
    }

    private sealed class GeneratedUiTextureChoice
    {
        public GeneratedTextureEntry? Texture { get; }

        public GeneratedUiTextureChoice(GeneratedTextureEntry? texture)
        {
            Texture = texture;
        }

        public override string ToString()
        {
            if (Texture is null)
            {
                return "Keep current path";
            }

            var name = string.IsNullOrWhiteSpace(Texture.DisplayName)
                ? UnrealPathUtil.AssetName(Texture.PackagePath)
                : Texture.DisplayName;
            return name + " · " + UnrealPathUtil.AssetName(Texture.PackagePath);
        }
    }
}
