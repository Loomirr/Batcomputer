namespace Batcomputer;

/// <summary>
/// Catalog-backed picker for a material instance to clone. This deliberately
/// stays inside Batcomputer: package paths come from the shipped game catalog,
/// while MaterialWizard resolves the selected asset to the extracted copy when
/// it needs to read its native parameters.
/// </summary>
public sealed class MaterialCatalogPicker : Form
{
    private readonly ListView _list = new();
    private readonly TextBox _search = new();
    private readonly ThemedDropDown _scope = new() { Placeholder = "Character materials" };
    private readonly Label _count = new();
    private readonly Label _selection = new();
    private readonly Button _use = new();
    private List<GameDataAsset> _all = new();
    private List<GameDataAsset> _view = new();

    /// <summary>Selected /Game material package path, without an object suffix.</summary>
    public string? SelectedPackagePath { get; private set; }

    public MaterialCatalogPicker()
    {
        Text = "Batcomputer - Base material library";
        ClientSize = new Size(820, 610);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;

        BuildSurface();
        LoadCatalog();
    }

    private void BuildSurface()
    {
        var accent = new Panel { Left = 16, Top = 18, Width = 3, Height = 36, BackColor = Theme.Materials };
        var eyebrow = new Label
        {
            Text = "MATERIALS",
            Left = 30,
            Top = 17,
            Width = 250,
            Height = 18,
            Font = Theme.Eyebrow,
            ForeColor = Theme.Materials,
        };
        var heading = new Label
        {
            Text = "Pick a base material",
            Left = 30,
            Top = 35,
            Width = 480,
            Height = 28,
            Font = Theme.Heading,
            ForeColor = Theme.OnDark,
        };
        var intro = new Label
        {
            Text = "Choose an in-game material instance to clone. The forge reads the selected material's real texture parameters.",
            Left = 18,
            Top = 72,
            Width = ClientSize.Width - 36,
            Height = 24,
            ForeColor = Theme.OnDarkMuted,
        };

        var filters = MakeCard(new Rectangle(16, 108, ClientSize.Width - 32, 78));
        var scopeLabel = MakeLabel("SHOW", 14, 12, 180);
        _scope.Left = 14;
        _scope.Top = 31;
        _scope.Width = 205;
        _scope.Height = 34;
        _scope.Items.Add("Character materials");
        _scope.Items.Add("All game materials");
        _scope.SelectedIndex = 0;
        _scope.SelectedIndexChanged += (_, _) => ApplyFilter();

        var searchLabel = MakeLabel("SEARCH", 238, 12, 240);
        _search.Left = 238;
        _search.Top = 31;
        _search.Width = filters.Width - 252;
        _search.Height = 34;
        _search.PlaceholderText = "Filter by material name or game path...";
        Theme.StyleDarkInput(_search);
        _search.TextChanged += (_, _) => ApplyFilter();

        filters.Controls.AddRange(new Control[] { scopeLabel, _scope, searchLabel, _search });

        var library = MakeCard(new Rectangle(16, 198, ClientSize.Width - 32, ClientSize.Height - 280));
        _count.Left = 14;
        _count.Top = 12;
        _count.Width = library.Width - 28;
        _count.Height = 18;
        _count.Font = Theme.Caption;
        _count.ForeColor = Theme.OnDarkMuted;

        _list.Left = 14;
        _list.Top = 38;
        _list.Width = library.Width - 28;
        _list.Height = library.Height - 52;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("MATERIAL", 260);
        _list.Columns.Add("GAME PATH", _list.Width - 280);
        Theme.StyleListView(_list);
        _list.SelectedIndexChanged += (_, _) => UpdateSelection();
        _list.DoubleClick += (_, _) => AcceptSelection();
        library.Controls.AddRange(new Control[] { _count, _list });

        _selection.Left = 18;
        _selection.Top = ClientSize.Height - 68;
        _selection.Width = ClientSize.Width - 288;
        _selection.Height = 32;
        _selection.ForeColor = Theme.OnDarkMuted;
        _selection.TextAlign = ContentAlignment.MiddleLeft;

        var cancel = new Button
        {
            Text = "Cancel",
            Left = ClientSize.Width - 212,
            Top = ClientSize.Height - 56,
            Width = 88,
            Height = 34,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            DialogResult = DialogResult.Cancel,
        };
        Theme.StyleDarkButton(cancel);

        _use.Text = "Use material";
        _use.Left = ClientSize.Width - 116;
        _use.Top = ClientSize.Height - 56;
        _use.Width = 100;
        _use.Height = 34;
        _use.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _use.Enabled = false;
        Theme.StyleGoldButton(_use);
        _use.Click += (_, _) => AcceptSelection();

        Controls.AddRange(new Control[] { accent, eyebrow, heading, intro, filters, library, _selection, cancel, _use });
        AcceptButton = _use;
        CancelButton = cancel;

        Resize += (_, _) => ResizeSurface(filters, library, intro, _selection, cancel);
    }

    private void LoadCatalog()
    {
        var gd = GameDataService.Instance;
        _all = gd.AssetsOfClass("MaterialInstanceConstant")
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        var charactersOnly = _scope.SelectedIndex != 1;
        _view = _all
            .Where(a => !charactersOnly || IsCharacterMaterial(a.Path))
            .Where(a => string.IsNullOrWhiteSpace(query) ||
                        a.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        AssetName(a.Path).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var asset in _view)
        {
            var item = new ListViewItem(AssetName(asset.Path).Replace("MI_", "", StringComparison.OrdinalIgnoreCase))
            {
                Tag = asset,
            };
            item.SubItems.Add(asset.Path);
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        _count.Text = _all.Count == 0
            ? "The shipped material catalog is not available."
            : $"{_view.Count:n0} material{(_view.Count == 1 ? "" : "s")}";
        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var asset = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as GameDataAsset : null;
        _selection.Text = asset is null ? "Choose a material to use as the forge base." : asset.Path;
        _selection.ForeColor = asset is null ? Theme.OnDarkMuted : Theme.OnDark;
        _use.Enabled = asset is not null;
    }

    private void AcceptSelection()
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not GameDataAsset asset)
        {
            return;
        }

        SelectedPackagePath = asset.Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool IsCharacterMaterial(string packagePath) =>
        packagePath.StartsWith("/Game/Characters/", StringComparison.OrdinalIgnoreCase);

    private static string AssetName(string packagePath) =>
        packagePath.Contains('/') ? packagePath[(packagePath.LastIndexOf('/') + 1)..] : packagePath;

    private static Panel MakeCard(Rectangle bounds) => new()
    {
        Bounds = bounds,
        BackColor = Theme.CardBg,
    };

    private static Label MakeLabel(string text, int left, int top, int width) => new()
    {
        Text = text,
        Left = left,
        Top = top,
        Width = width,
        Height = 16,
        Font = Theme.Eyebrow,
        ForeColor = Theme.OnDarkMuted,
    };

    private void ResizeSurface(Panel filters, Panel library, Label intro, Label selection, Button cancel)
    {
        intro.Width = ClientSize.Width - 36;
        filters.Width = ClientSize.Width - 32;
        _search.Width = filters.Width - 252;
        library.Width = ClientSize.Width - 32;
        library.Height = ClientSize.Height - 280;
        _count.Width = library.Width - 28;
        _list.Width = library.Width - 28;
        _list.Height = library.Height - 52;
        if (_list.Columns.Count > 1)
        {
            _list.Columns[1].Width = Math.Max(180, _list.Width - _list.Columns[0].Width - 20);
        }
        selection.Top = ClientSize.Height - 68;
        selection.Width = ClientSize.Width - 288;
        cancel.Left = ClientSize.Width - 212;
        cancel.Top = ClientSize.Height - 56;
        _use.Left = ClientSize.Width - 116;
        _use.Top = ClientSize.Height - 56;
    }
}
