namespace Batcomputer;

/// <summary>
/// Catalog-backed picker for a material instance to clone. Package paths merge
/// the active extracted Content tree with the bundled fallback catalog, while
/// MaterialWizard resolves the selected asset to disk when it reads parameters.
/// </summary>
public sealed class MaterialCatalogPicker : AdaptiveForm
{
    private readonly ListView _list = new();
    private readonly TextBox _search = new();
    private readonly ThemedDropDown _scope = new() { Placeholder = "Character materials" };
    private readonly ThemedDropDown _partType = new() { Placeholder = "All material types" };
    private readonly ThemedDropDown _character = new() { Placeholder = "All characters" };
    private readonly Label _count = new();
    private readonly Label _selection = new();
    private readonly Button _use = new();
    private List<GameDataAsset> _all = new();
    private List<GameDataAsset> _view = new();
    private HashSet<string> _colourParameterPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _colourParameterIndexBuilt;
    private bool _colourParameterIndexBuilding;

    /// <summary>Selected /Game material package path, without an object suffix.</summary>
    public string? SelectedPackagePath { get; private set; }

    public MaterialCatalogPicker()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Batcomputer - Base material library";
        ClientSize = new Size(880, 650);
        MinimumSize = new Size(780, 560);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Shown += (_, _) => Theme.UseDarkTitleBar(this);

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
            Text = "Choose an in-game material instance from the active extraction or bundled fallback. The forge reads its real texture and colour parameters.",
            Left = 18,
            Top = 72,
            Width = ClientSize.Width - 36,
            Height = 24,
            ForeColor = Theme.OnDarkMuted,
        };

        var filters = MakeCard(new Rectangle(16, 108, ClientSize.Width - 32, 122));
        var scopeLabel = MakeLabel("SHOW", 14, 12, 180);
        _scope.Left = 14;
        _scope.Top = 31;
        _scope.Width = 185;
        _scope.Height = 34;
        _scope.Items.Add("Character materials");
        _scope.Items.Add("All game materials");
        _scope.SelectedIndex = 0;
        _scope.SelectedIndexChanged += (_, _) => ApplyFilter();

        var partTypeLabel = MakeLabel("PART TYPE", 214, 12, 190);
        _partType.Left = 214;
        _partType.Top = 31;
        _partType.Width = 210;
        _partType.Height = 34;
        _partType.Items.AddRange(new object[]
        {
            "All material types",
            "Colour parameter",
            "Cape",
            "Cowl / hat",
            "Hair",
            "Face / head",
            "Torso / body",
            "Arms / hands",
            "Legs",
            "Equipment / attachments",
        });
        _partType.SelectedIndex = 0;
        _partType.SelectedIndexChanged += (_, _) => ApplyFilter();

        var characterLabel = MakeLabel("CHARACTER", 438, 12, 190);
        _character.Left = 438;
        _character.Top = 31;
        _character.Width = 205;
        _character.Height = 34;
        _character.Items.Add("All characters");
        _character.SelectedIndex = 0;
        _character.SelectedIndexChanged += (_, _) => ApplyFilter();

        var searchLabel = MakeLabel("SEARCH", 14, 76, 240);
        _search.Left = 14;
        _search.Top = 91;
        _search.Width = filters.Width - 28;
        _search.Height = 34;
        _search.PlaceholderText = "Search material name or game path…";
        Theme.StyleDarkInput(_search);
        _search.TextChanged += (_, _) => ApplyFilter();

        filters.Controls.AddRange(new Control[]
        {
            scopeLabel, _scope,
            partTypeLabel, _partType,
            characterLabel, _character,
            searchLabel, _search,
        });

        var library = MakeCard(new Rectangle(16, 242, ClientSize.Width - 32, ClientSize.Height - 324));
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
        _selection.Width = ClientSize.Width - 258;
        _selection.Height = 32;
        _selection.ForeColor = Theme.OnDarkMuted;
        _selection.TextAlign = ContentAlignment.MiddleLeft;

        var cancel = new Button
        {
            Text = "Cancel",
            Left = ClientSize.Width - 228,
            Top = ClientSize.Height - 56,
            Width = 88,
            Height = 34,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            DialogResult = DialogResult.Cancel,
        };
        Theme.StyleDarkButton(cancel);

        _use.Text = "Use material";
        _use.Left = ClientSize.Width - 132;
        _use.Top = ClientSize.Height - 56;
        _use.Width = 116;
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
        foreach (var family in CharacterFamilies(_all))
        {
            _character.Items.Add(family);
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (IsColourParameterFilter() && !_colourParameterIndexBuilt)
        {
            BeginColourParameterIndexBuild();
            return;
        }

        var query = _search.Text.Trim();
        var charactersOnly = _scope.SelectedIndex != 1;
        var partType = _partType.SelectedItem?.ToString() ?? "All material types";
        var character = _character.SelectedItem?.ToString() ?? "All characters";
        _view = _all
            .Where(a => !charactersOnly || IsCharacterMaterial(a.Path))
            .Where(a => MatchesPartType(a, partType))
            .Where(a => MatchesCharacter(a.Path, character))
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
            ? "No material instances were found in the active extraction or bundled fallback."
            : $"{_view.Count:n0} material{(_view.Count == 1 ? "" : "s")}";
        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }
        UpdateSelection();
    }

    private bool IsColourParameterFilter() =>
        string.Equals(_partType.SelectedItem?.ToString(), "Colour parameter", StringComparison.OrdinalIgnoreCase);

    private async void BeginColourParameterIndexBuild()
    {
        if (_colourParameterIndexBuilding)
        {
            return;
        }

        _colourParameterIndexBuilding = true;
        _list.Items.Clear();
        _use.Enabled = false;
        _selection.Text = "Checking extracted material instances for vector colour parameters…";
        _selection.ForeColor = Theme.OnDarkMuted;
        _count.Text = "Building colour-parameter index…";
        try
        {
            var assets = _all.ToArray();
            var materialService = new MaterialGenService(AppSettings.Current.EffectiveProjectRoot());
            _colourParameterPaths = await Task.Run(() => assets
                .Where(a => MaterialHasVectorColourParameter(materialService, a.Path))
                .Select(a => a.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
            _colourParameterIndexBuilt = true;
        }
        finally
        {
            _colourParameterIndexBuilding = false;
        }

        if (!IsDisposed)
        {
            ApplyFilter();
        }
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

    private bool MatchesPartType(GameDataAsset asset, string partType)
    {
        var path = asset.Path;
        var name = AssetName(path);
        return partType switch
        {
            "All material types" => true,
            "Colour parameter" => _colourParameterPaths.Contains(path),
            "Cape" => path.Contains("/Attachments/Cape/", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Cape", StringComparison.OrdinalIgnoreCase),
            "Cowl / hat" => path.Contains("/Attachments/Hat/", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Cowl", StringComparison.OrdinalIgnoreCase) ||
                             name.StartsWith("MI_HAT", StringComparison.OrdinalIgnoreCase),
            "Hair" => path.Contains("/Hair/", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Hair", StringComparison.OrdinalIgnoreCase),
            "Face / head" => path.Contains("/Face/", StringComparison.OrdinalIgnoreCase) ||
                             path.Contains("/Head/", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Face", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Head", StringComparison.OrdinalIgnoreCase),
            "Torso / body" => path.Contains("/Torso/", StringComparison.OrdinalIgnoreCase) ||
                              path.Contains("/Body/", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Torso", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Body", StringComparison.OrdinalIgnoreCase),
            "Arms / hands" => path.Contains("/Arm/", StringComparison.OrdinalIgnoreCase) ||
                              path.Contains("/Hand/", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Arm", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Hand", StringComparison.OrdinalIgnoreCase),
            "Legs" => path.Contains("/Leg/", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Leg", StringComparison.OrdinalIgnoreCase),
            "Equipment / attachments" => path.Contains("/Attachments/", StringComparison.OrdinalIgnoreCase) ||
                                         path.Contains("/Equipment/", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    internal static bool MatchesCharacter(string packagePath, string character)
    {
        if (string.Equals(character, "All characters", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return packagePath.Contains($"/Minifig/{character}/", StringComparison.OrdinalIgnoreCase) ||
               packagePath.Contains(character, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CharacterFamilies(IEnumerable<GameDataAsset> assets) => assets
        .Select(a => ExtractCharacterFamily(a.Path))
        .Where(family => !string.IsNullOrWhiteSpace(family))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(family => family, StringComparer.OrdinalIgnoreCase)!;

    private static string? ExtractCharacterFamily(string packagePath)
    {
        const string marker = "/Minifig/";
        var index = packagePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = packagePath.IndexOf('/', start);
        return end > start ? packagePath[start..end] : null;
    }

    private static bool MaterialHasVectorColourParameter(MaterialGenService materialService, string packagePath)
    {
        var diskPath = MainForm.ResolveMiDiskPath(packagePath, preferExport: false);
        if (string.IsNullOrWhiteSpace(diskPath))
        {
            return false;
        }

        var info = materialService.ReadTemplate(diskPath);
        return info.Status == "ok" && info.ColorParams.Count > 0;
    }

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
        _search.Width = filters.Width - 28;
        library.Width = ClientSize.Width - 32;
        library.Top = filters.Bottom + 12;
        library.Height = ClientSize.Height - library.Top - 82;
        _count.Width = library.Width - 28;
        _list.Width = library.Width - 28;
        _list.Height = library.Height - 52;
        if (_list.Columns.Count > 1)
        {
            _list.Columns[1].Width = Math.Max(180, _list.Width - _list.Columns[0].Width - 20);
        }
        selection.Top = ClientSize.Height - 68;
        selection.Width = ClientSize.Width - 258;
        cancel.Left = ClientSize.Width - 228;
        cancel.Top = ClientSize.Height - 56;
        _use.Left = ClientSize.Width - 132;
        _use.Top = ClientSize.Height - 56;
    }
}
