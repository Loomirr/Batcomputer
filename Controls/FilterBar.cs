using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>One filter dimension: a title, the "no filter" label, and the values to choose from.</summary>
public sealed class FilterGroup
{
    public FilterGroup(string title, string anyLabel, IEnumerable<string>? items = null)
    {
        Title = title;
        AnyLabel = anyLabel;
        Items = (items ?? Enumerable.Empty<string>()).ToList();
    }

    public string Title { get; }
    public string AnyLabel { get; }
    public List<string> Items { get; }

    /// <summary>The picked value, or null for "any".</summary>
    public string? Selected { get; set; }
}

/// <summary>
/// A single button holding everything that narrows the current browser: the view being browsed (the
/// "scope") plus every optional filter for it. Replaces the row of dropdowns that used to sit in the
/// toolbar. Shows the active choices inline and a gold count badge, so the toolbar still says what is
/// filtered without spending a control per dimension.
/// </summary>
public sealed class FilterBar : Control
{
    private readonly List<FilterGroup> _groups = new();
    private string _scopeTitle = "View";
    private List<string> _scopeItems = new();
    private string? _scopeSelected;
    private bool _hover;
    private bool _open;
    private double _hoverT;
    private DateTime _closedAt = DateTime.MinValue;

    /// <summary>An optional filter changed. The scope has its own event - it reloads the browser.</summary>
    public event EventHandler? FiltersChanged;

    /// <summary>The scope (which view is being browsed) changed.</summary>
    public event Action<string>? ScopeChanged;

    public FilterBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 30;
        Width = 150;
        Font = Theme.Body;
    }

    /// <summary>
    /// Sets the view list. Unlike a filter group this always has a value - it says what is being
    /// browsed, not what is being excluded - so it has no "any" entry and never counts as a filter.
    /// </summary>
    public void SetScope(string title, IEnumerable<string> items, string? selected)
    {
        _scopeTitle = title;
        // A one-entry list is not a choice - the nav rail already said which browser this is, so
        // showing it again would just be a button that repeats itself.
        _scopeItems = items.Count() > 1 ? items.ToList() : new List<string>();
        _scopeSelected = selected is not null && _scopeItems.Contains(selected) ? selected : _scopeItems.FirstOrDefault();
        UpdateVisibility();
    }

    /// <summary>Replaces the filters on offer. Selections are dropped - the new category owns them.</summary>
    public void SetGroups(params FilterGroup[] groups)
    {
        _groups.Clear();
        _groups.AddRange(groups.Where(g => g.Items.Count > 0));
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        Visible = _groups.Count > 0 || _scopeItems.Count > 0;
        RecalcWidth();
        Invalidate();
    }

    /// <summary>The value picked for group <paramref name="index"/>, or null for "any".</summary>
    public string? Value(int index) =>
        index >= 0 && index < _groups.Count ? _groups[index].Selected : null;

    public int ActiveCount => _groups.Count(g => g.Selected is not null);

    private string Caption
    {
        get
        {
            var parts = new List<string>();
            if (_scopeSelected is not null)
            {
                parts.Add(PrettyScope(_scopeSelected));
            }
            parts.AddRange(_groups.Where(g => g.Selected is not null).Select(g => g.Selected!));
            return parts.Count == 0 ? "Filters" : string.Join("  ·  ", parts);
        }
    }

    /// <summary>The type list uses &lt;angle brackets&gt; for its catch-alls; they read badly inline.</summary>
    private static string PrettyScope(string value) =>
        value.StartsWith('<') && value.EndsWith('>')
            ? char.ToUpperInvariant(value[1]) + value[2..^1]
            : value;

    private void RecalcWidth()
    {
        var text = TextRenderer.MeasureText(Caption, Font).Width;
        Width = Math.Clamp(text + 27 + 14 + (ActiveCount > 0 ? 20 : 0) + 8, 150, 340);
    }

    protected override void OnMouseEnter(EventArgs e)
    { _hover = true; Animator.Start(this, "hover", _hoverT, 1, 120, v => { _hoverT = v; Invalidate(); }); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    { _hover = false; Animator.Start(this, "hover", _hoverT, 0, 140, v => { _hoverT = v; Invalidate(); }); base.OnMouseLeave(e); }

    protected override void OnHandleDestroyed(EventArgs e) { Animator.Cancel(this, "hover"); base.OnHandleDestroyed(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // The popup dismisses itself on any click outside, including this button. Whether Closed has
        // fired by the time we get here is a race, so treat a click just after a close as the tail of
        // that dismissal - otherwise clicking the button to close it reopens it instead.
        if (_open || (DateTime.UtcNow - _closedAt).TotalMilliseconds < 250)
        {
            _open = false;
            return;
        }
        ShowPopup();
    }

    private void ShowPopup()
    {
        if (_groups.Count == 0 && _scopeItems.Count == 0)
        {
            return;
        }

        var panel = BuildPopupPanel();
        var host = new ToolStripControlHost(panel)
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoSize = false,
            Size = panel.Size,
        };
        var drop = new ToolStripDropDown
        {
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            AutoSize = false,
            DropShadowEnabled = true,
            BackColor = Theme.SlateDark,
        };
        drop.Items.Add(host);
        drop.Size = panel.Size + new Size(2, 2);
        drop.Closed += (_, _) => { _open = false; _closedAt = DateTime.UtcNow; Invalidate(); };
        _open = true;
        Invalidate();
        drop.Show(this, new Point(0, Height + 4));
    }

    private void Commit()
    {
        RecalcWidth();
        Invalidate();
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    private Panel BuildPopupPanel()
    {
        const int width = 320;
        const int pad = 12;
        var panel = new Panel { BackColor = Theme.SlateDark, Width = width };
        var y = pad;

        void Section(string title, IReadOnlyList<string> items, string? anyLabel,
                     Func<string?> get, Action<string?> set)
        {
            if (y > pad)
            {
                panel.Controls.Add(new Panel
                {
                    Bounds = new Rectangle(pad, y, width - pad * 2, 1),
                    BackColor = Theme.LineSoft,
                });
                y += 1 + pad;
            }

            panel.Controls.Add(new Label
            {
                Text = title.ToUpperInvariant(),
                Font = Theme.Eyebrow,
                ForeColor = Theme.OnDarkMuted,
                BackColor = Color.Transparent,
                AutoSize = false,
                Bounds = new Rectangle(pad, y, width - pad * 2, 16),
                TextAlign = ContentAlignment.MiddleLeft,
            });
            y += 20;

            // A handful of values fit as chips; anything longer (source characters run to hundreds)
            // gets a searchable list instead, which is the only way that stays usable.
            y += items.Count <= 6
                ? AddChips(panel, items, anyLabel, get, set, pad, y, width - pad * 2)
                : AddList(panel, title, items, anyLabel, get, set, pad, y, width - pad * 2);
            y += pad;
        }

        if (_scopeItems.Count > 0)
        {
            Section(_scopeTitle, _scopeItems, null, () => _scopeSelected, value =>
            {
                if (value is null || value == _scopeSelected)
                {
                    return;
                }
                _scopeSelected = value;
                RecalcWidth();
                Invalidate();
                ScopeChanged?.Invoke(value);
            });
        }

        foreach (var group in _groups)
        {
            var g = group;
            Section(g.Title, g.Items, g.AnyLabel, () => g.Selected, value => { g.Selected = value; Commit(); });
        }

        var clear = new LinkLabel
        {
            Text = "Clear all filters",
            Font = Theme.Caption,
            LinkColor = Theme.OnDarkMuted,
            ActiveLinkColor = Theme.Gold,
            LinkBehavior = LinkBehavior.NeverUnderline,
            BackColor = Color.Transparent,
            AutoSize = false,
            Bounds = new Rectangle(pad, y, width - pad * 2, 20),
            TextAlign = ContentAlignment.MiddleLeft,
            Visible = _groups.Count > 0,
        };
        clear.LinkClicked += (_, _) =>
        {
            foreach (var g in _groups)
            {
                g.Selected = null;
            }
            Commit();
            RefreshPopupVisuals(panel);
        };
        panel.Controls.Add(clear);
        y += (clear.Visible ? 20 : 0) + pad;

        panel.Height = y;
        panel.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
            using var pen = new Pen(Theme.SlateLight);
            e.Graphics.DrawRectangle(pen, r);
        };
        return panel;
    }

    /// <summary>Repaints every chip/list in an open popup after a bulk change like "clear all".</summary>
    private static void RefreshPopupVisuals(Control panel)
    {
        foreach (Control c in panel.Controls)
        {
            c.Invalidate();
            RefreshPopupVisuals(c);
        }
    }

    private static int AddChips(Panel panel, IReadOnlyList<string> items, string? anyLabel,
                                Func<string?> get, Action<string?> set, int x, int y, int width)
    {
        var flow = new FlowLayoutPanel
        {
            Bounds = new Rectangle(x, y, width, 0),
            BackColor = Color.Transparent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            MaximumSize = new Size(width, 0),
        };

        void AddChip(string label, string? value)
        {
            var chip = new Chip(label) { Margin = new Padding(0, 0, 6, 6) };
            chip.IsActive = () => get() == value;
            chip.Click += (_, _) =>
            {
                set(value);
                RefreshPopupVisuals(flow);
            };
            flow.Controls.Add(chip);
        }

        if (anyLabel is not null)
        {
            AddChip("Any", null);
        }
        foreach (var item in items)
        {
            AddChip(PrettyScope(item), item);
        }

        panel.Controls.Add(flow);
        return flow.Height;
    }

    private static int AddList(Panel panel, string title, IReadOnlyList<string> items, string? anyLabel,
                               Func<string?> get, Action<string?> set, int x, int y, int width)
    {
        var search = new SearchBox
        {
            Bounds = new Rectangle(x, y, width, 28),
            PlaceholderText = $"Search {title.ToLowerInvariant()}...",
        };
        panel.Controls.Add(search);

        var list = new FilterList(items, anyLabel, get)
        {
            Bounds = new Rectangle(x, y + 34, width, 190),
        };
        list.Picked += value =>
        {
            set(value);
            list.Invalidate();
        };
        panel.Controls.Add(list);

        search.TextChanged += (_, _) => list.ApplySearch(search.Text);
        return 34 + list.Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ControlGround.Resolve(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var count = ActiveCount;
        var on = count > 0;
        var warm = _open ? 1.0 : _hoverT;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(r, Math.Min(8, r.Height / 2)))
        {
            using var fill = new SolidBrush(Theme.Blend(Theme.CardHi, Theme.Slate, warm));
            g.FillPath(fill, path);
            using var pen = new Pen(on ? Theme.GoldDim : Theme.Blend(Theme.GoldDim, Theme.SlateLight, warm * 0.5));
            g.DrawPath(pen, path);
        }

        // Funnel glyph, drawn rather than typed - three tapering bars read as "filter" at any DPI.
        var ink = on ? Theme.Gold : Theme.OnDarkMuted;
        using (var pen = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var cy = Height / 2;
            g.DrawLine(pen, 10, cy - 4, 20, cy - 4);
            g.DrawLine(pen, 12, cy, 18, cy);
            g.DrawLine(pen, 13.5f, cy + 4, 16.5f, cy + 4);
        }

        var badge = on ? 20 : 0;
        var textRect = new Rectangle(27, 0, Width - 27 - 14 - badge, Height);
        TextRenderer.DrawText(g, Caption, Font, textRect, Theme.OnDark,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix);

        if (on)
        {
            var bx = Width - 14 - 18;
            var by = Height / 2 - 8;
            using (var b = new SolidBrush(Theme.Gold))
            {
                g.FillEllipse(b, bx, by, 16, 16);
            }
            TextRenderer.DrawText(g, count.ToString(), Theme.Caption, new Rectangle(bx, by, 16, 16),
                Theme.SlateDark, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        using (var pen = new Pen(Theme.OnDarkMuted, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var cx = Width - 11;
            var cy = Height / 2 - 1;
            g.DrawLine(pen, cx - 3.5f, cy, cx, cy + 3.5f);
            g.DrawLine(pen, cx, cy + 3.5f, cx + 3.5f, cy);
        }
    }

    /// <summary>A pill for one value in a short group. Gold when it is the active choice.</summary>
    private sealed class Chip : Control
    {
        private bool _hover;
        public Func<bool> IsActive { get; set; } = () => false;

        public Chip(string text)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Theme.Caption;
            Text = text;
            Height = 26;
            Width = Math.Min(284, TextRenderer.MeasureText(text, Theme.Caption).Width + 24);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ControlGround.Resolve(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var active = IsActive();
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(r, r.Height / 2))
            {
                using var fill = new SolidBrush(active ? Theme.Gold : _hover ? Theme.CardHi : Theme.Slate);
                g.FillPath(fill, path);
                using var pen = new Pen(active ? Theme.Gold : Theme.SlateLight);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, r, active ? Theme.SlateDark : Theme.OnDark,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Scrolling picker for a long group. Owner-drawn against a plain list rather than a control per
    /// row - source lists run to several hundred entries.
    /// </summary>
    private sealed class FilterList : Control
    {
        private const int RowH = 26;
        private readonly IReadOnlyList<string> _items;
        private readonly string? _anyLabel;
        private readonly Func<string?> _selected;
        private List<string> _view;
        private int _scroll;
        private int _hover = -1;

        public event Action<string?>? Picked;

        public FilterList(IReadOnlyList<string> items, string? anyLabel, Func<string?> selected)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _items = items;
            _anyLabel = anyLabel;
            _selected = selected;
            _view = items.ToList();
            BackColor = Theme.WindowBg;
            Font = Theme.Body;
        }

        public void ApplySearch(string text)
        {
            _view = string.IsNullOrWhiteSpace(text)
                ? _items.ToList()
                : _items.Where(i => i.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            _scroll = 0;
            Invalidate();
        }

        // With an "any" label, row 0 is the reset and the rest are offset by one.
        private int Offset => _anyLabel is null ? 0 : 1;
        private int RowCount => _view.Count + Offset;
        private int MaxScroll => Math.Max(0, RowCount * RowH - Height);
        private string? ValueAt(int row) => _anyLabel is not null && row == 0 ? null : _view[row - Offset];

        private int RowAt(int y)
        {
            var row = (y + _scroll) / RowH;
            return row >= 0 && row < RowCount ? row : -1;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 120 * RowH * 3, 0, MaxScroll);
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var row = RowAt(e.Y);
            if (row != _hover) { _hover = row; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var row = RowAt(e.Y);
            if (row >= 0)
            {
                Picked?.Invoke(ValueAt(row));
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var first = Math.Max(0, _scroll / RowH);
            var last = Math.Min(RowCount - 1, (_scroll + Height) / RowH);
            var current = _selected();

            for (var row = first; row <= last; row++)
            {
                var y = row * RowH - _scroll;
                var value = ValueAt(row);
                var selected = current == value;
                var isAny = _anyLabel is not null && row == 0;
                var rect = new Rectangle(0, y, Width, RowH);

                if (selected || row == _hover)
                {
                    using var b = new SolidBrush(selected ? Theme.Blend(Theme.Gold, Theme.WindowBg, 0.82) : Theme.CardHi);
                    g.FillRectangle(b, rect);
                }
                if (selected)
                {
                    using var b = new SolidBrush(Theme.Gold);
                    g.FillRectangle(b, 0, y, 2, RowH);
                }

                var label = isAny ? _anyLabel! : PrettyScope(value!);
                TextRenderer.DrawText(g, label, Font, new Rectangle(12, y, Width - 24, RowH),
                    selected ? Theme.Gold : isAny ? Theme.OnDarkMuted : Theme.OnDark,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix);
            }

            if (MaxScroll > 0)
            {
                var track = Height;
                var thumb = Math.Max(24, (int)(track * (track / (float)(RowCount * RowH))));
                var pos = (int)((track - thumb) * (_scroll / (float)MaxScroll));
                using var b = new SolidBrush(Theme.SlateLight);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Theme.RoundedRect(new Rectangle(Width - 5, pos, 3, thumb), 1);
                g.FillPath(b, path);
            }
        }
    }
}
