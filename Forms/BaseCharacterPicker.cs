using System.Windows.Forms;

namespace Batcomputer;

/// <summary>
/// A searchable list of base characters (every BP_*_Playable in the shipped
/// catalog) so users pick a character instead of hand-browsing .uasset paths.
/// The caller resolves the cutscene/DCMD siblings from the returned playable
/// package (same folder, predictable naming).
/// </summary>
public sealed partial class BaseCharacterPicker : Form
{
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private List<GameDataAsset> _all = new();
    private List<GameDataAsset> _view = new();

    /// <summary>Selected playable /Game package path (no extension), or null.</summary>
    public string? SelectedPlayablePackage { get; private set; }

    /// <summary>True when the user asked to browse files manually instead.</summary>
    public bool BrowseManuallyRequested { get; private set; }

    private readonly bool _playablesOnly;

    public BaseCharacterPicker() : this(false) { }

    /// <param name="playablesOnly">When true, only proven _Playable heroes are shown -
    /// used when picking a machinery donor (villains have no machinery to donate).</param>
    public BaseCharacterPicker(bool playablesOnly)
    {
        _playablesOnly = playablesOnly;
        InitializeComponent();
        if (WinFormsDesignerSupport.IsInDesigner())
        {
            return;
        }

        Controls.Clear();

        Text = playablesOnly ? "Pick a character to inherit machinery from" : "Pick base character";
        Width = 560;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;

        // Every character pawn BP - not just the ready-to-play _Playable heroes, but
        // villains/NPCs (_Quest/_Boss/_Goon/_Civilian) too, so you can build on ANY
        // character. Non-Playable bases are EXPERIMENTAL: they may lack the playable
        // machinery (cutscene, ability archetype) the tool expects, and some villains are
        // bigfigs (different skeleton → parts won't graft). Playables are ranked first.
        _all = GameDataService.Instance.AssetsOfClass("BlueprintGeneratedClass")
            .Where(a => IsCharacterPawnBp(a.Path) &&
                        (!_playablesOnly || CharType(a.Path) == "Playable"))
            .OrderBy(a => CharTypeRank(a.Path))
            .ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lbl = new Label
        {
            Text = playablesOnly
                ? "Pick a hero — your villain/NPC base will inherit its abilities, equipment, animation, and cutscene."
                : "Search characters (name, family, or type). Playables are proven; villains/NPCs are experimental.",
            Left = 14, Top = 12, Width = 520, ForeColor = Theme.OnDark
        };
        _search.Left = 14; _search.Top = 34; _search.Width = 520;
        _search.BackColor = Theme.SlateDark; _search.ForeColor = Theme.OnDark;
        _search.TextChanged += (_, _) => ApplyFilter();

        _list.Left = 14; _list.Top = 64; _list.Width = 520; _list.Height = 400;
        _list.BackColor = Theme.SlateDark; _list.ForeColor = Theme.OnDark;
        _list.DoubleClick += (_, _) => Accept();

        var browse = new Button { Text = "Browse files…", Left = 14, Top = 474, Width = 120, Height = 30 };
        Theme.StyleDarkButton(browse);
        browse.Click += (_, _) => { BrowseManuallyRequested = true; DialogResult = DialogResult.OK; Close(); };

        var ok = new Button { Text = "Use as base", Left = 344, Top = 474, Width = 110, Height = 30 };
        Theme.StyleGoldButton(ok);
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", Left = 458, Top = 474, Width = 76, Height = 30, DialogResult = DialogResult.Cancel };
        Theme.StyleDarkButton(cancel);

        Controls.AddRange(new Control[] { lbl, _search, _list, browse, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = _search.Text.Trim();
        _view = string.IsNullOrWhiteSpace(q)
            ? _all
            : _all.Where(a => a.Path.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              DisplayName(a.Path).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _view)
        {
            _list.Items.Add(DisplayName(a.Path));
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _view.Count)
        {
            return;
        }
        SelectedPlayablePackage = _view[i].Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    // "Batman · The Batman 2025  [Playable]" - family, name, and the base TYPE so the
    // user knows whether it's a proven playable or an experimental villain/NPC.
    private static string DisplayName(string packagePath)
    {
        var name = AssetName(packagePath).Replace("BP_", "");
        foreach (var suffix in new[] { "_Playable", "_Quest", "_Boss", "_Goon", "_Civilian", "_Batcave" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        var segs = packagePath.Split('/');
        var family = segs.Length >= 2 ? segs[^2] : "";
        var type = CharType(packagePath);
        var label = string.IsNullOrWhiteSpace(family) ? name.Replace('_', ' ') : $"{family}  ·  {name.Replace('_', ' ')}";
        return type == "Playable" ? $"{label}   [Playable]" : $"{label}   [{type} — experimental]";
    }

    /// <summary>Is this a character pawn blueprint (not an archetype, equipment def, cutscene,
    /// projectile, data asset, etc.)?</summary>
    private static bool IsCharacterPawnBp(string path)
    {
        var n = AssetName(path);
        if (!n.StartsWith("BP_", StringComparison.OrdinalIgnoreCase)) return false;
        if (!path.Contains("/Characters/", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("/BP_Master/", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var bad in new[]
        {
            "Archetype", "_ED", "_Inst", "_Cutscene", "_CUT", "HoverData", "Projectile",
            "Weapon", "_Data", "Upgrades", "_Ability", "Effect", "_Default_Civilian_",
            "_Batcave" // batcave display-only variants — not useful as playable bases
        })
        {
            if (n.Contains(bad, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string CharType(string path)
    {
        var n = AssetName(path);
        if (n.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase)) return "Playable";
        if (n.EndsWith("_Boss", StringComparison.OrdinalIgnoreCase)) return "Boss";
        if (n.EndsWith("_Goon", StringComparison.OrdinalIgnoreCase)) return "Goon";
        if (n.EndsWith("_Quest", StringComparison.OrdinalIgnoreCase)) return "Quest NPC";
        if (n.EndsWith("_Civilian", StringComparison.OrdinalIgnoreCase)) return "Civilian";
        if (n.EndsWith("_Batcave", StringComparison.OrdinalIgnoreCase)) return "Batcave";
        return "Other";
    }

    private static int CharTypeRank(string path) => CharType(path) switch
    {
        "Playable" => 0,
        "Batcave" => 1,
        "Quest NPC" => 2,
        "Boss" => 3,
        "Civilian" => 4,
        "Goon" => 5,
        _ => 6
    };

    private static string AssetName(string packagePath) =>
        packagePath.Contains('/') ? packagePath[(packagePath.LastIndexOf('/') + 1)..] : packagePath;
}
