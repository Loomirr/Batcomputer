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
    private List<GameDataAsset> _all = new();
    private List<GameDataAsset> _view = new();

    /// <summary>Selected visual /Game package path (no extension), or null.</summary>
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
    public BaseCharacterPicker(bool playablesOnly, string? preferredPackage = null)
    {
        _playablesOnly = playablesOnly;
        _preferredPackage = UnrealPathUtil.NormalizePackagePath(preferredPackage);
        InitializeComponent();
        AutoScaleMode = AutoScaleMode.Dpi;
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();
        Text = playablesOnly ? "Pick a gameplay donor" : "Pick visual base";
        Width = 600;
        Height = 570;
        MinimumSize = new Size(420, 360);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;

        _all = BuildVisualAssetList(
            GameDataService.Instance.AssetsOfClass("BlueprintGeneratedClass"),
            AppSettings.Current.EffectiveExtractedContentRoot(),
            _playablesOnly);

        var prompt = new Label
        {
            Text = playablesOnly
                ? "Pick a real playable. It supplies movement, equipment, animation, and runtime behavior."
                : "Pick any character or cutscene for the visual starting point. Batcomputer asks for a playable donor when the visual source needs one.",
            Left = 14,
            Top = 12,
            Width = 556,
            Height = 60,
            ForeColor = Theme.OnDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _search.Left = 14;
        _search.Top = 76;
        _search.Width = 556;
        _search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _search.PlaceholderText = "Search characters…";
        _search.TextChanged += (_, _) => ApplyFilter();

        _list.Left = 14;
        _list.Top = 106;
        _list.Width = 556;
        _list.Height = 358;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Theme.StyleListBox(_list);
        _list.DoubleClick += (_, _) => Accept();

        var browse = new Button { Text = "Browse files…", Left = 14, Top = 11, Width = 130, Height = 32 };
        Theme.StyleDarkButton(browse);
        browse.Click += (_, _) => { BrowseManuallyRequested = true; DialogResult = DialogResult.OK; Close(); };

        var accept = new Button { Text = playablesOnly ? "Use donor" : "Use visual base", Width = 140 };
        Theme.StyleGoldButton(accept);
        accept.Click += (_, _) => Accept();

        var cancel = new Button { Text = "Cancel", Width = 84, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);

        var footer = DialogActionFooter.Create(accept, cancel);
        footer.Controls.Add(browse);
        browse.BringToFront();

        Controls.AddRange(new Control[] { prompt, _search, _list, footer });
        AcceptButton = accept;
        CancelButton = cancel;
        ApplyFilter();
    }

    /// <summary>
    /// Merges the shipped path catalog with extracted quest-character Blueprints. Some native
    /// characters, including Batmite, live under Characters/Smallfig and are absent from older
    /// shipped catalogs even though the normal refresh extracted them successfully.
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

        if (!playablesOnly)
        {
            foreach (var package in EnumerateExtractedQuestVisualPackages(extractedContentRoot))
            {
                assetsByPath.TryAdd(package, new GameDataAsset
                {
                    Path = package,
                    Class = "BlueprintGeneratedClass"
                });
            }
        }

        return assetsByPath.Values
            .OrderBy(asset => CharacterTypeRank(asset.Path))
            .ThenBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<string> EnumerateExtractedQuestVisualPackages(string extractedContentRoot)
    {
        try
        {
            var contentRoot = AppSettings.NormalizeContentRoot(extractedContentRoot);
            var charactersRoot = Path.Combine(contentRoot, "Characters");
            if (!Directory.Exists(charactersRoot))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(charactersRoot, "BP_*.uasset", SearchOption.AllDirectories)
                .Where(path => Path.GetFileNameWithoutExtension(path)
                    .EndsWith("_Quest", StringComparison.OrdinalIgnoreCase))
                .Select(path => "/Game/" + Path.ChangeExtension(
                    Path.GetRelativePath(contentRoot, path), null)!.Replace('\\', '/'))
                .Where(BaseEligibilityService.IsVisualCharacterPackage)
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

        _list.BeginUpdate();
        _list.Items.Clear();
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
            _list.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
        }
    }

    private void Accept()
    {
        var index = _list.SelectedIndex;
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
