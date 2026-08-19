using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// A small, owner-drawn selector for the tool's dark surfaces. Native ComboBox controls retain
/// Windows' square edit field and arrow button, which clashes with the rounded Batcomputer chrome.
/// </summary>
public sealed class ThemedDropDown : Control
{
    private int _selectedIndex = -1;
    private bool _open;
    private double _hoverT;
    private DateTime _closedAt = DateTime.MinValue;

    public List<object> Items { get; } = new();

    public string Placeholder { get; set; } = "Select…";

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var next = value >= 0 && value < Items.Count ? value : -1;
            if (_selectedIndex == next)
            {
                return;
            }

            _selectedIndex = next;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem
    {
        get => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
        set => SelectedIndex = value is null ? -1 : Items.FindIndex(item => Equals(item, value));
    }

    public event EventHandler? SelectedIndexChanged;

    public ThemedDropDown()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        Height = 34;
        TabStop = true;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        Animator.Start(this, "dropdown-hover", _hoverT, 1, 120, value => { _hoverT = value; Invalidate(); });
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        Animator.Start(this, "dropdown-hover", _hoverT, 0, 140, value => { _hoverT = value; Invalidate(); });
        base.OnMouseLeave(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Animator.Cancel(this, "dropdown-hover");
        base.OnHandleDestroyed(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || Items.Count == 0 || _open || (DateTime.UtcNow - _closedAt).TotalMilliseconds < 220)
        {
            return;
        }

        ShowPopup();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4)
        {
            if (!_open && Items.Count > 0)
            {
                ShowPopup();
            }
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Down && Items.Count > 0)
        {
            SelectedIndex = Math.Min(Items.Count - 1, _selectedIndex + 1);
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Up && Items.Count > 0)
        {
            SelectedIndex = Math.Max(0, _selectedIndex - 1);
            e.Handled = true;
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Space or Keys.Enter or Keys.F4 || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var ground = ControlGround.Resolve(this);
        e.Graphics.Clear(ground);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var fill = !Enabled
            ? Theme.Blend(Theme.Slate, ground, 0.55)
            : Theme.Blend(Theme.CardHi, Theme.Slate, _hoverT);
        var border = !Enabled
            ? Theme.Blend(Theme.LineSoft, ground, 0.5)
            : _open || Focused ? Theme.Textures : Theme.Blend(Theme.SlateLight, Theme.LineSoft, _hoverT);
        using (var path = Theme.RoundedRect(rect, Theme.RadiusSm))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }

        var text = SelectedItem?.ToString();
        var textColor = !Enabled ? Theme.OnDarkMuted : string.IsNullOrWhiteSpace(text) ? Theme.OnDarkMuted : Theme.OnDark;
        TextRenderer.DrawText(e.Graphics, string.IsNullOrWhiteSpace(text) ? Placeholder : text, Font,
            new Rectangle(11, 0, Math.Max(0, Width - 42), Height), textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var arrowCenterX = Width - 18;
        var arrowCenterY = Height / 2 + 1;
        using var arrow = new Pen(!Enabled ? Theme.OnDarkMuted : (_open ? Theme.Textures : Theme.OnDarkMuted), 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        e.Graphics.DrawLine(arrow, arrowCenterX - 4, arrowCenterY - 2, arrowCenterX, arrowCenterY + 2);
        e.Graphics.DrawLine(arrow, arrowCenterX, arrowCenterY + 2, arrowCenterX + 4, arrowCenterY - 2);
    }

    private void ShowPopup()
    {
        var options = CreatePopupOptions();
        ToolStripDropDown? popup = null;
        options.Selected += index =>
        {
            SelectedIndex = index;
            popup?.Close(ToolStripDropDownCloseReason.ItemClicked);
        };

        var host = new ToolStripControlHost(options)
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoSize = false,
            Size = options.Size,
        };
        popup = new ToolStripDropDown
        {
            Padding = new Padding(1),
            Margin = Padding.Empty,
            AutoSize = false,
            DropShadowEnabled = true,
            BackColor = Theme.SlateDark,
        };
        popup.Items.Add(host);
        popup.Size = options.Size + new Size(2, 2);
        popup.Closed += (_, _) =>
        {
            _open = false;
            _closedAt = DateTime.UtcNow;
            Invalidate();
        };
        _open = true;
        Invalidate();
        popup.Show(this, new Point(0, Height + 4));
    }

    // Kept separate so the self-test covers the ToolStrip-hosted option controls too.
    internal Control CreatePopupOptionsForTest() => CreatePopupOptions();

    private DropDownOptions CreatePopupOptions() => new(Items, _selectedIndex, Width);

    private sealed class DropDownOptions : Panel
    {
        public event Action<int>? Selected;

        public DropDownOptions(IReadOnlyList<object> items, int selectedIndex, int width)
        {
            BackColor = Theme.SlateDark;
            Width = Math.Max(180, width);
            var visibleRows = Math.Min(8, Math.Max(1, items.Count));
            Height = visibleRows * 34 + 12;

            var flow = new FlowLayoutPanel
            {
                Left = 6,
                Top = 6,
                Width = Width - 12,
                Height = Height - 12,
                BackColor = Theme.SlateDark,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = items.Count > visibleRows,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
            };
            foreach (var pair in items.Select((item, index) => new { item, index }))
            {
                var option = new DropDownOption(pair.item?.ToString() ?? "", pair.index == selectedIndex)
                {
                    Width = flow.ClientSize.Width - (flow.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0),
                    Height = 32,
                    Margin = new Padding(0, 0, 0, 2),
                };
                option.Clicked += () => Selected?.Invoke(pair.index);
                flow.Controls.Add(option);
            }
            Controls.Add(flow);
        }
    }

    private sealed class DropDownOption : Control
    {
        private readonly string _text;
        private readonly bool _selected;
        private bool _hover;

        public event Action? Clicked;

        public DropDownOption(string text, bool selected)
        {
            _text = text;
            _selected = selected;
            // ToolStripControlHost does not support transparent child controls.
            BackColor = Theme.SlateDark;
            Cursor = Cursors.Hand;
            Font = Theme.Body;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (ClientRectangle.Contains(e.Location))
            {
                Clicked?.Invoke();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.SlateDark);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover || _selected)
            {
                using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 5);
                using var fill = new SolidBrush(_selected ? Theme.Tint(Theme.Textures) : Theme.CardHi);
                e.Graphics.FillPath(fill, path);
            }
            if (_selected)
            {
                using var accent = new SolidBrush(Theme.Textures);
                e.Graphics.FillRectangle(accent, 7, 9, 3, Math.Max(6, Height - 18));
            }
            TextRenderer.DrawText(e.Graphics, _text, Font,
                new Rectangle(_selected ? 18 : 12, 0, Math.Max(0, Width - 26), Height),
                _hover || _selected ? Theme.OnDark : Theme.OnDarkMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
