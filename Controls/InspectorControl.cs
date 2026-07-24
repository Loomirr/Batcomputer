using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// The suit inspector: "what is actually on this suit, and what will ship?".
///
/// Summary first, detail on demand. Replaces the raw TreeView with:
/// identity card → role switch → filter → collapsible component cards → issues → footer actions.
/// The component/slot rows use the same visual language as the materials panel under the minifig,
/// so the two surfaces read as one system, and slot rows are material drop targets.
/// </summary>
public sealed class InspectorControl : UserControl
{
    // ---- models -------------------------------------------------------------
    public sealed class SlotRow
    {
        public required int Slot;
        public string Material = "";
        public bool Overridden;
    }

    public sealed class ComponentRow
    {
        public required string Name;
        public string Class = "";
        public string Mesh = "";
        public IReadOnlyList<SlotRow> Slots = Array.Empty<SlotRow>();
        public bool Customized => Slots.Any(s => s.Overridden);
    }

    public enum Severity { Info, Warn, Crit }

    public sealed class IssueRow
    {
        public required string Title;
        public string Detail = "";
        public Severity Level = Severity.Warn;
    }

    // ---- events -------------------------------------------------------------
    public event EventHandler? RefreshRequested;
    public event EventHandler? PreflightRequested;
    /// <summary>The full text breakdown for the currently-viewed role.</summary>
    public event EventHandler? BreakdownRequested;
    public event EventHandler? RoleChanged;
    public event Action<string>? ComponentSelected;
    public event Action<string, int>? SlotSelected;
    public event Action<string, int, string>? SlotMaterialDropped;

    /// <summary>Set by the host: pulls a material path out of a drag payload, or null if not one.</summary>
    public Func<IDataObject?, string?>? ResolveMaterialPath { get; set; }

    /// <summary>"playable" or "cutscene".</summary>
    public string Role { get; private set; } = "playable";

    // ---- chrome -------------------------------------------------------------
    private readonly TableLayoutPanel _root = new();
    private readonly RoundedPanel _identity = new();
    private readonly Label _suitLabel = new();
    private readonly FlowLayoutPanel _chips = new();
    private readonly SegmentedSwitch _roleSeg = new();
    private readonly RoundedPanel _searchWrap = new();
    private readonly TextBox _search = new();
    private readonly ToggleSwitch _changedOnly = new();
    private readonly FlowLayoutPanel _content = new();
    private readonly Button _refresh = new();
    private readonly Button _copy = new();
    private readonly Button _preflight = new();
    private readonly ToolTip _tips = new();

    private IReadOnlyList<ComponentRow> _components = Array.Empty<ComponentRow>();
    private IReadOnlyList<IssueRow> _issues = Array.Empty<IssueRow>();
    private string _message = "";
    private string? _expanded;

    public InspectorControl()
    {
        BackColor = Theme.CardBg;
        Padding = new Padding(6, 8, 6, 6);
        Theme.StyleTooltip(_tips);

        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 1;
        _root.RowCount = 5;
        _root.BackColor = Theme.CardBg;
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62f));   // identity
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));   // role
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));   // filter
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // content
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));   // footer

        BuildIdentity();
        BuildRoleSwitch();
        BuildFilter();
        BuildContent();
        BuildFooter();

        Controls.Add(_root);
    }

    // ---- identity -----------------------------------------------------------
    private void BuildIdentity()
    {
        _identity.Dock = DockStyle.Fill;
        _identity.Margin = new Padding(0, 0, 0, 6);
        _identity.BackColor = Theme.CardHi;
        _identity.CornerRadius = Theme.RadiusSm;
        _identity.BorderColor = Theme.LineSoft;
        // Gold rail, tying the panel to the command bar.
        _identity.Paint += (_, e) =>
        {
            using var b = new SolidBrush(Theme.Gold);
            using var p = Theme.RoundedRect(new Rectangle(1, 9, 3, Math.Max(4, _identity.Height - 18)), 2);
            e.Graphics.FillPath(b, p);
        };

        _suitLabel.AutoSize = false;
        _suitLabel.Left = 12; _suitLabel.Top = 7; _suitLabel.Height = 20;
        _suitLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _suitLabel.ForeColor = Theme.OnDark;
        _suitLabel.BackColor = Color.Transparent;
        _suitLabel.AutoEllipsis = true;

        _chips.Left = 10; _chips.Top = 30; _chips.Height = 24;
        _chips.BackColor = Color.Transparent;
        _chips.WrapContents = false;
        _chips.AutoScroll = false;

        _identity.Controls.Add(_suitLabel);
        _identity.Controls.Add(_chips);
        _identity.Resize += (_, _) =>
        {
            var w = Math.Max(20, _identity.Width - 20);
            _suitLabel.Width = w;
            _chips.Width = w;
            LayoutChips();
        };
        _root.Controls.Add(_identity, 0, 0);
    }

    private Control MakeChip(string text, Color? dot, int maxWidth)
    {
        var chip = new RoundedPanel
        {
            Height = 20,
            Margin = new Padding(0, 2, 5, 2),
            BackColor = Theme.Slate,
            BorderColor = Theme.LineSoft,
            CornerRadius = 10,
        };
        var padLeft = dot is null ? 8 : 17;
        var natural = TextRenderer.MeasureText(text, Theme.Caption).Width;
        var textW = Math.Min(natural, Math.Max(14, maxWidth - padLeft - 8));
        var label = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            Text = text,
            Left = padLeft,
            Top = 3,
            Width = textW,
            Height = 15,
        };
        chip.Controls.Add(label);
        if (dot is Color c)
        {
            chip.Controls.Add(new StatusDot { Left = 7, Top = 7, Width = 7, Height = 7, DotColor = c });
        }
        chip.Width = textW + padLeft + 8;
        _tips.SetToolTip(label, text);
        return chip;
    }

    private (string Text, Color? Dot)[] _chipData = Array.Empty<(string, Color?)>();

    /// <summary>
    /// Rebuilds the chip row to fit the current width. Chips share the space evenly and ellipsize -
    /// a fixed-width row ran the last chip off the edge of the card.
    /// </summary>
    private void LayoutChips()
    {
        _chips.SuspendLayout();
        foreach (Control c in _chips.Controls.Cast<Control>().ToList())
        {
            _chips.Controls.Remove(c);
            c.Dispose();
        }
        if (_chipData.Length > 0)
        {
            var avail = Math.Max(60, _chips.Width);
            var per = (avail - 5 * (_chipData.Length - 1)) / _chipData.Length;
            foreach (var (text, dot) in _chipData)
            {
                _chips.Controls.Add(MakeChip(text, dot, per));
            }
        }
        _chips.ResumeLayout();
    }

    /// <summary>Suit identity shown at the top of the panel.</summary>
    public void SetIdentity(string suit, string mod, string slotId, bool packaged)
    {
        _suitLabel.Text = string.IsNullOrWhiteSpace(suit) ? "No suit loaded" : suit;
        _chipData = new (string, Color?)[]
        {
            (packaged ? "packaged" : "not packaged", packaged ? Theme.Good : Theme.OnDarkMuted),
            ($"mod {(string.IsNullOrWhiteSpace(mod) ? "—" : mod)}", null),
            ($"slot {(string.IsNullOrWhiteSpace(slotId) ? "—" : slotId)}", null),
        };
        LayoutChips();
    }

    // ---- role switch --------------------------------------------------------
    private void BuildRoleSwitch()
    {
        _roleSeg.Dock = DockStyle.Fill;
        _roleSeg.Margin = new Padding(0, 0, 0, 6);
        _roleSeg.Segments = new[] { "Playable", "Cutscene" };
        _roleSeg.SelectedIndex = 0;
        _roleSeg.SelectedIndexChanged += (_, _) =>
        {
            Role = _roleSeg.SelectedIndex == 1 ? "cutscene" : "playable";
            RoleChanged?.Invoke(this, EventArgs.Empty);
        };
        _root.Controls.Add(_roleSeg, 0, 1);
    }

    // ---- filter -------------------------------------------------------------
    private void BuildFilter()
    {
        var row = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBg, Margin = new Padding(0, 0, 0, 6) };

        _searchWrap.BackColor = Theme.Slate;
        _searchWrap.BorderColor = Theme.LineSoft;
        _searchWrap.CornerRadius = Theme.RadiusSm;
        _searchWrap.Height = 28;
        _searchWrap.Top = 0;
        _searchWrap.Left = 0;

        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = Theme.Slate;
        _search.ForeColor = Theme.OnDark;
        _search.Font = Theme.Body;
        _search.PlaceholderText = "Filter components…";
        _search.Left = 9;
        _search.Width = 10;
        _search.TextChanged += (_, _) => Rebuild();
        _searchWrap.Controls.Add(_search);
        _searchWrap.Layout += (_, _) => _search.Top = (_searchWrap.Height - _search.Height) / 2;

        var toggleLabel = new Label
        {
            Text = "Changed",
            AutoSize = false,
            Width = 56,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent,
        };
        _changedOnly.Size = new Size(38, 20);
        _changedOnly.CheckedChanged += (_, _) => Rebuild();
        _tips.SetToolTip(_changedOnly, "Show only components with an overridden material");

        row.Controls.Add(_searchWrap);
        row.Controls.Add(toggleLabel);
        row.Controls.Add(_changedOnly);
        row.Resize += (_, _) =>
        {
            var toggleW = 56 + 38 + 6;
            _searchWrap.Width = Math.Max(40, row.Width - toggleW - 6);
            _search.Width = Math.Max(20, _searchWrap.Width - 18);
            toggleLabel.Left = _searchWrap.Right + 6;
            _changedOnly.Left = toggleLabel.Right;
            _changedOnly.Top = 4;
        };

        _root.Controls.Add(row, 0, 2);
    }

    // ---- content ------------------------------------------------------------
    private void BuildContent()
    {
        _content.Dock = DockStyle.Fill;
        _content.FlowDirection = FlowDirection.TopDown;
        _content.WrapContents = false;
        _content.AutoScroll = true;
        _content.BackColor = Theme.CardBg;
        _content.Padding = new Padding(0, 0, 0, 4);
        _root.Controls.Add(_content, 0, 3);
    }

    // ---- footer -------------------------------------------------------------
    private void BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardBg };

        foreach (var (b, text) in new[] { (_refresh, "↻ Refresh"), (_copy, "Breakdown") })
        {
            b.Text = text;
            b.Height = 30;
            Theme.StyleDarkButton(b);
        }
        _preflight.Text = "Preflight";
        _preflight.Height = 30;
        Theme.StyleGoldButton(_preflight);

        _refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _copy.Click += (_, _) => BreakdownRequested?.Invoke(this, EventArgs.Empty);
        _preflight.Click += (_, _) => PreflightRequested?.Invoke(this, EventArgs.Empty);

        footer.Controls.Add(_refresh);
        footer.Controls.Add(_copy);
        footer.Controls.Add(_preflight);
        footer.Resize += (_, _) =>
        {
            // Three even columns - scales down instead of clipping off the right edge.
            var gap = 5;
            var w = Math.Max(40, (footer.Width - gap * 2) / 3);
            _refresh.SetBounds(0, 2, w, 30);
            _copy.SetBounds(w + gap, 2, w, 30);
            _preflight.SetBounds((w + gap) * 2, 2, footer.Width - (w + gap) * 2, 30);
        };

        _root.Controls.Add(footer, 0, 4);
    }

    // ---- population ---------------------------------------------------------
    public void SetComponents(IReadOnlyList<ComponentRow> components)
    {
        _components = components;
        _message = "";
        Rebuild();
    }

    public void SetIssues(IReadOnlyList<IssueRow> issues)
    {
        _issues = issues;
        Rebuild();
    }

    /// <summary>Empty state - one line instead of an empty tree.</summary>
    public void SetMessage(string message)
    {
        _message = message;
        _components = Array.Empty<ComponentRow>();
        _issues = Array.Empty<IssueRow>();
        Rebuild();
    }

    /// <summary>Expands the card for a component (and scrolls to it) - figure → inspector sync.</summary>
    public void FocusComponent(string component, int slot)
    {
        _expanded = component;
        Rebuild();
        foreach (Control c in _content.Controls)
        {
            if (c.Tag as string == component)
            {
                _content.ScrollControlIntoView(c);
                break;
            }
        }
    }

    private void Rebuild()
    {
        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList())
        {
            _content.Controls.Remove(c);
            c.Dispose();
        }

        var width = Math.Max(120, _content.ClientSize.Width - 4);

        if (!string.IsNullOrEmpty(_message))
        {
            _content.Controls.Add(new Label
            {
                Text = _message,
                Width = width,
                Height = 40,
                Font = Theme.Body,
                ForeColor = Theme.OnDarkMuted,
                BackColor = Color.Transparent,
            });
            _content.ResumeLayout();
            return;
        }

        var filter = _search.Text.Trim();
        var shown = _components.Where(c =>
            (!_changedOnly.Checked || c.Customized) &&
            (filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || c.Mesh.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();

        _content.Controls.Add(SectionLabel($"Components", shown.Count, width));
        foreach (var comp in shown)
        {
            _content.Controls.Add(BuildComponentCard(comp, width));
        }
        if (shown.Count == 0)
        {
            _content.Controls.Add(new Label
            {
                Text = _components.Count == 0 ? "No components found." : "Nothing matches this filter.",
                Width = width, Height = 30, Font = Theme.Caption,
                ForeColor = Theme.OnDarkMuted, BackColor = Color.Transparent,
            });
        }

        if (_issues.Count > 0)
        {
            _content.Controls.Add(SectionLabel("Needs attention", _issues.Count, width));
            foreach (var issue in _issues)
            {
                _content.Controls.Add(BuildIssueCard(issue, width));
            }
        }

        _content.ResumeLayout();
    }

    private static Control SectionLabel(string text, int count, int width)
    {
        var lbl = new Label
        {
            Width = width,
            Height = 22,
            Margin = new Padding(0, 8, 0, 3),
            BackColor = Color.Transparent,
        };
        lbl.Paint += (_, e) =>
        {
            var label = $"{text}".ToUpperInvariant();
            TextRenderer.DrawText(e.Graphics, label, Theme.Eyebrow, new Point(0, 6), Theme.Gold);
            var tw = TextRenderer.MeasureText(label, Theme.Eyebrow).Width;
            var cw = TextRenderer.MeasureText(count.ToString(), Theme.Eyebrow).Width;
            TextRenderer.DrawText(e.Graphics, count.ToString(), Theme.Eyebrow,
                new Point(lbl.Width - cw - 2, 6), Theme.OnDarkMuted);
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, tw + 8, 12, lbl.Width - cw - 8, 12);
        };
        return lbl;
    }

    private Control BuildComponentCard(ComponentRow comp, int width)
    {
        var expanded = string.Equals(_expanded, comp.Name, StringComparison.OrdinalIgnoreCase);
        var slotsH = expanded ? 7 + comp.Slots.Count * 26 : 0;

        var card = new RoundedPanel
        {
            Width = width,
            Height = 42 + slotsH,
            Margin = new Padding(0, 0, 0, 5),
            BackColor = Theme.CardHi,
            BorderColor = expanded ? Theme.Gold : Theme.LineSoft,
            CornerRadius = Theme.RadiusSm,
            Tag = comp.Name,
            Cursor = Cursors.Hand,
        };

        var caret = new Label
        {
            Text = expanded ? "▾" : "▸",
            Left = 8, Top = 13, Width = 12, Height = 14,
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted, BackColor = Color.Transparent,
        };
        var name = new Label
        {
            Text = comp.Name,
            Left = 22, Top = 5, Width = width - 60, Height = 16,
            Font = Theme.BodyStrong, ForeColor = Theme.OnDark,
            BackColor = Color.Transparent, AutoEllipsis = true,
        };
        var mesh = new Label
        {
            Text = string.IsNullOrWhiteSpace(comp.Mesh) ? "(mesh default materials)" : comp.Mesh,
            Left = 22, Top = 21, Width = width - 60, Height = 14,
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, AutoEllipsis = true,
        };
        _tips.SetToolTip(mesh, comp.Mesh);

        var count = new Label
        {
            Text = comp.Slots.Count.ToString(),
            Width = 22, Height = 20, Left = width - 30, Top = 11,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Eyebrow,
            ForeColor = comp.Customized ? Theme.SlateDark : Theme.OnDarkMuted,
            BackColor = Color.Transparent,
        };
        count.Paint += (_, e) =>
        {
            var r = new Rectangle(0, 0, count.Width - 1, count.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(comp.Customized ? Theme.Gold : Theme.Slate);
            using var p = new Pen(comp.Customized ? Theme.GoldDim : Theme.LineSoft);
            e.Graphics.FillEllipse(b, r);
            e.Graphics.DrawEllipse(p, r);
            TextRenderer.DrawText(e.Graphics, count.Text, Theme.Eyebrow, r,
                comp.Customized ? Theme.SlateDark : Theme.OnDarkMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        void Toggle(object? s, EventArgs e)
        {
            _expanded = expanded ? null : comp.Name;
            ComponentSelected?.Invoke(comp.Name);
            Rebuild();
        }
        card.Click += Toggle; caret.Click += Toggle; name.Click += Toggle; mesh.Click += Toggle;

        card.Controls.Add(caret);
        card.Controls.Add(name);
        card.Controls.Add(mesh);
        card.Controls.Add(count);

        if (expanded)
        {
            var y = 42;
            foreach (var slot in comp.Slots)
            {
                card.Controls.Add(BuildSlotRow(comp.Name, slot, width, ref y));
            }
        }

        return card;
    }

    private Control BuildSlotRow(string component, SlotRow slot, int width, ref int y)
    {
        var has = !string.IsNullOrWhiteSpace(slot.Material);
        var row = new RoundedPanel
        {
            Left = 7, Top = y, Width = width - 14, Height = 23,
            BackColor = Theme.SlateDark,
            BorderColor = slot.Overridden ? Theme.GoldDim : Theme.LineSoft,
            CornerRadius = 6,
            Cursor = Cursors.Hand,
            AllowDrop = true,
        };

        var idx = new Label
        {
            Text = slot.Slot.ToString(),
            Left = 5, Top = 4, Width = 14, Height = 15,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Eyebrow,
            ForeColor = slot.Overridden ? Theme.Gold : Theme.OnDarkMuted,
            BackColor = Color.Transparent,
        };
        var mat = new Label
        {
            Text = has ? slot.Material : "empty — drop a material",
            Left = 22, Top = 4, Width = row.Width - 30, Height = 15,
            Font = Theme.Caption,
            ForeColor = has ? Theme.OnDark : Theme.OnDarkMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        _tips.SetToolTip(mat, has ? slot.Material : "No material override on this slot");

        void Select(object? s, EventArgs e) => SlotSelected?.Invoke(component, slot.Slot);
        row.Click += Select; idx.Click += Select; mat.Click += Select;

        // Slot rows are material drop targets, exactly like the ones under the minifig.
        void Over(object? s, DragEventArgs e)
        {
            var path = ResolveMaterialPath?.Invoke(e.Data);
            e.Effect = string.IsNullOrWhiteSpace(path) ? DragDropEffects.None : DragDropEffects.Copy;
            row.BorderColor = e.Effect == DragDropEffects.Copy ? Theme.Gold : row.BorderColor;
            row.Invalidate();
        }
        row.DragEnter += Over;
        row.DragOver += Over;
        row.DragLeave += (_, _) =>
        {
            row.BorderColor = slot.Overridden ? Theme.GoldDim : Theme.LineSoft;
            row.Invalidate();
        };
        row.DragDrop += (_, e) =>
        {
            row.BorderColor = slot.Overridden ? Theme.GoldDim : Theme.LineSoft;
            var path = ResolveMaterialPath?.Invoke(e.Data);
            if (!string.IsNullOrWhiteSpace(path))
            {
                SlotMaterialDropped?.Invoke(component, slot.Slot, path!);
            }
        };

        row.Controls.Add(idx);
        row.Controls.Add(mat);
        y += 26;
        return row;
    }

    private static Control BuildIssueCard(IssueRow issue, int width)
    {
        var color = issue.Level switch
        {
            Severity.Crit => Theme.Crit,
            Severity.Info => Theme.Info,
            _ => Theme.Warn,
        };
        var hasDetail = !string.IsNullOrWhiteSpace(issue.Detail);
        var card = new RoundedPanel
        {
            Width = width,
            Height = hasDetail ? 42 : 28,
            Margin = new Padding(0, 0, 0, 5),
            BackColor = Theme.CardHi,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm,
        };
        card.Paint += (_, e) =>
        {
            using var b = new SolidBrush(color);
            using var p = Theme.RoundedRect(new Rectangle(1, 6, 3, Math.Max(4, card.Height - 12)), 2);
            e.Graphics.FillPath(b, p);
        };
        card.Controls.Add(new Label
        {
            Text = issue.Title,
            Left = 12, Top = 5, Width = width - 20, Height = 15,
            Font = Theme.Caption, ForeColor = Theme.OnDark,
            BackColor = Color.Transparent, AutoEllipsis = true,
        });
        if (hasDetail)
        {
            card.Controls.Add(new Label
            {
                Text = issue.Detail,
                Left = 12, Top = 21, Width = width - 20, Height = 16,
                Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
                BackColor = Color.Transparent, AutoEllipsis = true,
            });
        }
        return card;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
    }
}

/// <summary>A small segmented control (pill of mutually-exclusive options).</summary>
public sealed class SegmentedSwitch : Control
{
    private string[] _segments = Array.Empty<string>();
    private int _index;
    private int _hover = -1;
    private double _slide;   // eases toward _index so the active pill glides between segments

    public event EventHandler? SelectedIndexChanged;

    public string[] Segments { get => _segments; set { _segments = value; Invalidate(); } }

    public int SelectedIndex
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            Animator.Start(this, "slide", _slide, _index, 160, v => { _slide = v; Invalidate(); });
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Animator.Cancel(this, "slide");
        base.OnHandleDestroyed(e);
    }

    public SegmentedSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.CardBg;
        Cursor = Cursors.Hand;
        Height = 28;
    }

    private int SegmentAt(int x) => _segments.Length == 0 ? -1 : Math.Min(_segments.Length - 1, Math.Max(0, x * _segments.Length / Math.Max(1, Width)));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = SegmentAt(e.X);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1; Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var i = SegmentAt(e.X);
        if (i >= 0) SelectedIndex = i;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_segments.Length == 0) return;

        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRoundedCard(g, track, Theme.Slate, Theme.LineSoft, Theme.RadiusSm);

        var segW = (float)(Width - 4) / _segments.Length;

        // The active-segment pill is drawn once at the eased position, not per-segment, so it glides.
        var pill = new RectangleF(2 + (float)_slide * segW, 2, segW, Height - 5);
        Theme.FillRoundedCard(g, Rectangle.Round(pill), Theme.CardHi, null, 5);

        for (var i = 0; i < _segments.Length; i++)
        {
            var r = new RectangleF(2 + i * segW, 2, segW, Height - 5);
            var color = i == _index ? Theme.OnDark : i == _hover ? Theme.OnDark : Theme.OnDarkMuted;
            TextRenderer.DrawText(g, _segments[i], i == _index ? Theme.BodyStrong : Theme.Body,
                Rectangle.Round(r), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
