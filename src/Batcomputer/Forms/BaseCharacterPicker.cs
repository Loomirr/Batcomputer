using System.Windows.Forms;

namespace Batcomputer;

/// <summary>
/// A searchable visual-base picker. Any character cutscene can supply a suit's
/// appearance; a separate real playable supplies movement, equipment, and the
/// runtime archetype when the visual source does not have its own machinery.
/// </summary>
public sealed partial class BaseCharacterPicker : AdaptiveForm
{
    private readonly ListBox _list = new();
    private readonly SearchBox _search = new();
    private readonly ThemedDropDown _source = new() { Dock = DockStyle.Fill };
    private readonly TextBox _detail = CharacterMenuStyle.DetailText();
    private readonly Label _count = CharacterMenuStyle.Label("");
    private readonly Button _accept = new();
    private List<GameDataAsset> _all = new();
    private List<GameDataAsset> _view = new();
    private readonly List<SuitProjectService.ProjectSummary> _characters = new();
    private List<SuitProjectService.ProjectSummary> _characterView = new();
    public string? SelectedCharacterProjectPath { get; private set; }

    /// <summary>Selected visual package path (no extension), or null.</summary>
    public string? SelectedVisualPackage { get; private set; }

    /// <summary>
    /// Compatibility alias for the gameplay-donor flow. In playables-only mode,
    /// this is guaranteed to be a real _Playable package.
    /// </summary>
    public string? SelectedPlayablePackage => SelectedVisualPackage;

    /// <summary>True when the user asked to browse files manually instead.</summary>
    public bool BrowseManuallyRequested { get; private set; }

    private readonly bool _playablesOnly;
    private readonly string _preferredPackage;

    public BaseCharacterPicker() : this(false, null) { }

    /// <param name="playablesOnly">
    /// When true, show only real _Playable characters for gameplay inheritance.
    /// </param>
    public BaseCharacterPicker(bool playablesOnly, string? preferredPackage = null,
        IEnumerable<SuitProjectService.ProjectSummary>? customCharacters = null)
    {
        _playablesOnly = playablesOnly;
        if (!playablesOnly && customCharacters is not null) _characters.AddRange(customCharacters.Where(project => project.IsCharacter));
        _preferredPackage = UnrealPathUtil.NormalizePackagePath(preferredPackage);
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();
        Text = playablesOnly ? "Pick a gameplay donor" : "Pick visual base";
        ClientSize = new Size(1020, 670);
        MinimumSize = new Size(780, 530);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body; ShowInTaskbar = false;

        _all = BuildVisualAssetList(
            GameDataService.Instance.AssetsOfClass("BlueprintGeneratedClass"),
            AppSettings.Current.EffectiveExtractedContentRoot(),
            _playablesOnly);

        var root = CharacterMenuStyle.Grid(1, 3); root.Padding = new Padding(16);
        root.RowStyles.Add(new(SizeType.Absolute, 110)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 30));
        root.Controls.Add(CharacterMenuStyle.Header(playablesOnly ? "Choose gameplay donor" : "Choose a character base", playablesOnly ?
            "Native playables supply movement, equipment, animation and runtime behavior. This does not change your character's owner." :
            "Use a native visual starting point, or create a new suit for one of your saved characters."), 0, 0);
        var body = CharacterMenuStyle.Grid(2, 1); body.RowStyles.Add(new(SizeType.Percent, 100));
        body.ColumnStyles.Add(new(SizeType.Percent, 61)); body.ColumnStyles.Add(new(SizeType.Percent, 39)); root.Controls.Add(body, 0, 1);
        var browser = CharacterMenuStyle.Card(); browser.Margin = new Padding(0, 0, 12, 0); body.Controls.Add(browser, 0, 0);
        var left = CharacterMenuStyle.Grid(1, 2); left.RowStyles.Add(new(SizeType.Absolute, 44)); left.RowStyles.Add(new(SizeType.Percent, 100)); browser.Controls.Add(left);
        var filters = CharacterMenuStyle.Grid(2, 1); filters.RowStyles.Add(new(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new(SizeType.Percent, 45)); filters.ColumnStyles.Add(new(SizeType.Percent, 55)); left.Controls.Add(filters, 0, 0);
        _source.Items.AddRange(playablesOnly ? ["Native playables"] : new object[] { "All sources", "Native characters", "Your characters" });
        _source.SelectedIndex = 0; _source.Enabled = !playablesOnly; filters.Controls.Add(_source, 0, 0);
        _search.Dock = DockStyle.Fill; _search.Margin = new Padding(10, 0, 0, 0); filters.Controls.Add(_search, 1, 0);
        _search.PlaceholderText = "Search characters…";
        _search.TextChanged += (_, _) => ApplyFilter();
        _source.SelectedIndexChanged += (_, _) => ApplyFilter();
        _list.Dock = DockStyle.Fill; _list.IntegralHeight = false; _list.BorderStyle = BorderStyle.None;
        _list.DrawMode = DrawMode.OwnerDrawFixed; _list.ItemHeight = 66; _list.BackColor = Theme.CardBg; _list.ForeColor = Theme.OnDark;
        _list.DrawItem += DrawEntry; _list.Resize += (_, _) => _list.ItemHeight = 66 * DeviceDpi / 96;
        left.Controls.Add(_list, 0, 1); _list.SelectedIndexChanged += (_, _) => RefreshSelection();
        _list.DoubleClick += (_, _) => Accept();
        var details = CharacterMenuStyle.Card(); body.Controls.Add(details, 1, 0);
        var right = CharacterMenuStyle.Grid(1, 2); right.RowStyles.Add(new(SizeType.Absolute, 36)); right.RowStyles.Add(new(SizeType.Percent, 100)); details.Controls.Add(right);
        right.Controls.Add(CharacterMenuStyle.Label("Selection details", Theme.Heading, Theme.Materials), 0, 0); right.Controls.Add(_detail, 0, 1);
        root.Controls.Add(_count, 0, 2);

        var browse = new Button { Text = "Browse files…", Left = 14, Top = 11, Width = 130, Height = 32 };
        Theme.StyleDarkButton(browse);
        browse.Click += (_, _) => { BrowseManuallyRequested = true; DialogResult = DialogResult.OK; Close(); };

        var accept = _accept; accept.Text = playablesOnly ? "Use donor" : "Use visual base"; accept.Width = 156;
        Theme.StyleGoldButton(accept);
        accept.Click += (_, _) => Accept();

        var cancel = new Button { Text = "Cancel", Width = 84, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);

        var footer = DialogActionFooter.Create(accept, cancel, browse);

        Controls.AddRange(new Control[] { root, footer });
        AcceptButton = accept;
        CancelButton = cancel;
        ApplyFilter();
    }

    /// <summary>
    /// Merges the shipped path catalog with every compatible extracted character Blueprint.
    /// This adds new base-game discoveries as well as DLC visual bases stored below
    /// /Game/AdditionalContent and installed Game Feature mounts such as
    /// /DLC_BeyondPack without requiring a hard-coded DLC list.
    /// </summary>
    internal static List<GameDataAsset> BuildVisualAssetList(
        IEnumerable<GameDataAsset> catalogAssets,
        string extractedContentRoot,
        bool playablesOnly)
    {
        var assetsByPath = new Dictionary<string, GameDataAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in catalogAssets)
        {
            var package = UnrealPathUtil.NormalizePackagePath(asset.Path);
            if (BaseEligibilityService.IsVisualCharacterPackage(package) &&
                (!playablesOnly || BaseEligibilityService.IsGameplayDonorPackage(package)))
            {
                assetsByPath.TryAdd(package, asset);
            }
        }

        foreach (var package in EnumerateExtractedVisualPackages(extractedContentRoot, playablesOnly))
        {
            assetsByPath.TryAdd(package, new GameDataAsset
            {
                Path = package,
                Class = "BlueprintGeneratedClass"
            });
        }

        return assetsByPath.Values
            .OrderBy(asset => CharacterTypeRank(asset.Path))
            .ThenBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<string> EnumerateExtractedQuestVisualPackages(string extractedContentRoot)
        => EnumerateExtractedVisualPackages(extractedContentRoot, playablesOnly: false)
            .Where(package => UnrealPathUtil.AssetName(package)
                .EndsWith("_Quest", StringComparison.OrdinalIgnoreCase))
            .ToList();

    internal static IReadOnlyList<string> EnumerateExtractedVisualPackages(
        string extractedContentRoot,
        bool playablesOnly)
    {
        try
        {
            var contentRoot = AppSettings.NormalizeContentRoot(extractedContentRoot);
            if (!Directory.Exists(contentRoot))
            {
                return Array.Empty<string>();
            }

            return CharacterContentRootService.Enumerate(contentRoot)
                .SelectMany(charactersRoot => Directory.EnumerateFiles(
                    charactersRoot,
                    "BP_*.uasset",
                    SearchOption.AllDirectories))
                .Select(path => ExtractedPackagePathService.PackagePathFromFile(contentRoot, path))
                .Where(package => !string.IsNullOrWhiteSpace(package))
                .Select(package => package!)
                .Where(package => BaseEligibilityService.IsVisualCharacterPackage(package) &&
                                  (!playablesOnly || BaseEligibilityService.IsGameplayDonorPackage(package)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // A refresh can replace the extraction while the picker is opening. Keep the
            // shipped catalog usable and let the next open discover the new files.
            return Array.Empty<string>();
        }
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        _view = string.IsNullOrWhiteSpace(query)
            ? _all
            : _all.Where(asset => asset.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                  DisplayName(asset.Path).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!_playablesOnly && _source.SelectedIndex == 2) _view = [];

        _list.BeginUpdate();
        _list.Items.Clear();
        _characterView = _characters.Where(character => string.IsNullOrWhiteSpace(query) ||
            character.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || character.CharacterId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!_playablesOnly && _source.SelectedIndex == 1) _characterView = [];
        foreach (var character in _characterView) _list.Items.Add($"{character.DisplayName}   [Your character · create a suit]");
        foreach (var asset in _view)
        {
            _list.Items.Add(DisplayName(asset.Path));
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0)
        {
            var preferredIndex = string.IsNullOrWhiteSpace(_preferredPackage)
                ? -1
                : _view.FindIndex(asset =>
                    UnrealPathUtil.NormalizePackagePath(asset.Path).Equals(
                        _preferredPackage,
                        StringComparison.OrdinalIgnoreCase));
            _list.SelectedIndex = preferredIndex >= 0 ? preferredIndex + _characterView.Count : 0;
        }
        _count.Text = $"{_characterView.Count} saved character(s) · {_view.Count} native base(s)";
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var index = _list.SelectedIndex; _accept.Enabled = index >= 0;
        _accept.Text = index >= 0 && index < _characterView.Count ? "Create a suit" : _playablesOnly ? "Use donor" : "Use visual base";
        if (index < 0) { _detail.Text = "No matching characters.\r\n\r\nTry another search or source filter. New native assets may need a Full character extraction in Settings."; return; }
        if (index < _characterView.Count)
        {
            var character = _characterView[index];
            _detail.Text = character.DisplayName + "\r\n\r\nYOUR CHARACTER\r\n" + character.CharacterId +
                "\r\n\r\nCreates a separately editable suit for this character, with its owner and pawn tags assigned automatically. The default character is included when building." +
                "\r\n\r\nThis is not a native gameplay donor.\r\n\r\nPROJECT ID\r\n" + character.SlotId;
        }
        else
        {
            var package = _view[index - _characterView.Count].Path;
            _detail.Text = DisplayName(package) + "\r\n\r\n" + (_playablesOnly ? "NATIVE GAMEPLAY DONOR\r\n\r\nSupplies runtime machinery, movement and default abilities. Your character identity stays independent." :
                "NATIVE VISUAL BASE\r\n\r\nSupplies the starting appearance. If it lacks playable machinery, you will choose a gameplay donor next.") +
                "\r\n\r\nPACKAGE\r\n" + package;
        }
    }

    private void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? Theme.CardHi : Theme.CardBg); e.Graphics.FillRectangle(fill, e.Bounds);
        var custom = e.Index < _characterView.Count;
        var title = custom ? _characterView[e.Index].DisplayName : DisplayName(_view[e.Index - _characterView.Count].Path);
        var subtitle = custom ? "Your character · create a new suit" : _playablesOnly ? "Native playable · gameplay donor" : "Native asset · visual starting point";
        var inset = 10 * DeviceDpi / 96; var line = e.Bounds.Height / 2;
        TextRenderer.DrawText(e.Graphics, title, Theme.BodyStrong, new Rectangle(e.Bounds.X + inset, e.Bounds.Y + 4, e.Bounds.Width - 2 * inset, line),
            Theme.OnDark, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, subtitle, Theme.Caption, new Rectangle(e.Bounds.X + inset, e.Bounds.Y + line, e.Bounds.Width - 2 * inset, line),
            custom ? Theme.Materials : Theme.OnDarkMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (selected) { using var pen = new Pen(Theme.Materials, 2); e.Graphics.DrawLine(pen, e.Bounds.Left + 1, e.Bounds.Top + 6, e.Bounds.Left + 1, e.Bounds.Bottom - 6); }
    }

    private void Accept()
    {
        var index = _list.SelectedIndex;
        if (index >= 0 && index < _characterView.Count)
        {
            SelectedCharacterProjectPath = _characterView[index].Path;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        index -= _characterView.Count;
        if (index < 0 || index >= _view.Count)
        {
            return;
        }

        SelectedVisualPackage = _view[index].Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string DisplayName(string packagePath)
    {
        var name = AssetName(packagePath).Replace("BP_", "");
        foreach (var suffix in new[] { "_Default_Cutscene", "_Cutscene", "_Playable", "_Quest", "_Boss", "_Goon", "_Civilian", "_Batcave" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        var segments = packagePath.Split('/');
        var family = segments.Length >= 2 ? segments[^2] : "";
        var type = CharacterType(packagePath);
        var label = string.IsNullOrWhiteSpace(family)
            ? name.Replace('_', ' ')
            : $"{family} - {name.Replace('_', ' ')}";
        var role = type switch
        {
            "Playable" => "Gameplay donor",
            "Cutscene" => "Cutscene visual",
            _ => type + " visual"
        };
        return $"{label}   [{role}]";
    }

    private static string CharacterType(string packagePath)
    {
        var name = AssetName(packagePath);
        if (name.Contains("_Cutscene", StringComparison.OrdinalIgnoreCase)) return "Cutscene";
        if (name.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase)) return "Playable";
        if (name.EndsWith("_Boss", StringComparison.OrdinalIgnoreCase)) return "Boss";
        if (name.EndsWith("_Goon", StringComparison.OrdinalIgnoreCase)) return "Goon";
        if (name.EndsWith("_Quest", StringComparison.OrdinalIgnoreCase)) return "Quest NPC";
        if (name.EndsWith("_Civilian", StringComparison.OrdinalIgnoreCase)) return "Civilian";
        if (name.EndsWith("_Batcave", StringComparison.OrdinalIgnoreCase)) return "Batcave";
        return "Character";
    }

    private static int CharacterTypeRank(string packagePath) => CharacterType(packagePath) switch
    {
        "Cutscene" => 0,
        "Playable" => 1,
        "Batcave" => 2,
        "Quest NPC" => 3,
        "Boss" => 4,
        "Civilian" => 5,
        "Goon" => 6,
        _ => 7
    };

    private static string AssetName(string packagePath) =>
        packagePath.Contains('/') ? packagePath[(packagePath.LastIndexOf('/') + 1)..] : packagePath;
}
