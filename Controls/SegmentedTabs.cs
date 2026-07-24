namespace Batcomputer;

/// <summary>
/// A themed replacement for <see cref="TabControl"/>: a <see cref="SegmentedSwitch"/> over a content
/// host. WinForms tab strips can't be fully owner-drawn (the strip ground and page border stay
/// Windows-drawn), so the inspector uses this instead to stay on-theme.
/// </summary>
public sealed class SegmentedTabs : Panel
{
    private readonly SegmentedSwitch _switch = new();
    private readonly Panel _host = new();
    private readonly List<(string Name, Control Content)> _tabs = new();

    public SegmentedTabs()
    {
        BackColor = Theme.CardBg;
        Padding = new Padding(3, 3, 3, 0);

        _host.Dock = DockStyle.Fill;
        _host.BackColor = Theme.CardBg;

        _switch.Dock = DockStyle.Top;
        _switch.Height = 28;
        _switch.BackColor = Theme.CardBg;
        _switch.SelectedIndexChanged += (_, _) => ShowSelected();

        Controls.Add(_host);
        Controls.Add(_switch);
    }

    public int Count => _tabs.Count;

    public string? SelectedName =>
        _switch.SelectedIndex >= 0 && _switch.SelectedIndex < _tabs.Count ? _tabs[_switch.SelectedIndex].Name : null;

    public bool ContainsTab(string name) =>
        _tabs.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void AddTab(string name, Control content)
    {
        if (ContainsTab(name)) return;
        content.Dock = DockStyle.Fill;
        content.Visible = false;
        _tabs.Add((name, content));
        _host.Controls.Add(content);
        SyncSegments();
        if (_tabs.Count == 1) SelectTab(name);
    }

    public void RemoveTab(string name)
    {
        var i = _tabs.FindIndex(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        _host.Controls.Remove(_tabs[i].Content);
        _tabs.RemoveAt(i);
        SyncSegments();
        if (_tabs.Count > 0) SelectTab(_tabs[0].Name);
    }

    public void SelectTab(string name)
    {
        var i = _tabs.FindIndex(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        if (_switch.SelectedIndex == i) ShowSelected();   // already there: just ensure visibility
        else _switch.SelectedIndex = i;                    // raises the event, which shows it
    }

    /// <summary>A single tab needs no switcher - hide it so the panel isn't wearing a dead control.</summary>
    private void SyncSegments()
    {
        _switch.Segments = _tabs.Select(t => t.Name).ToArray();
        _switch.Visible = _tabs.Count > 1;
        if (_switch.SelectedIndex >= _tabs.Count) _switch.SelectedIndex = Math.Max(0, _tabs.Count - 1);
        ShowSelected();
    }

    private void ShowSelected()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].Content.Visible = i == _switch.SelectedIndex;
        }
    }
}
