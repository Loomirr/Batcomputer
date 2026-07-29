using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;

namespace Batcomputer;

/// <summary>
/// Rounded search field with a magnifier and a clear button. Wraps a borderless TextBox rather than
/// owner-drawing the text, so caret, selection, and IME all behave normally. The inner box is filled
/// with the same solid colour as the pill (TextBox cannot be transparent).
/// </summary>
public sealed class SearchBox : Control
{
    private readonly TextBox _input = new();
    private bool _hoverClear;
    private double _focusT;

    public SearchBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 30;
        Width = 220;

        _input.BorderStyle = BorderStyle.None;
        _input.BackColor = FieldFill;
        _input.ForeColor = Theme.OnDark;
        _input.Font = Theme.Body;
        // The clear button changes the usable text width.
        _input.TextChanged += (_, _) => { PerformLayout(); Invalidate(); OnTextChanged(EventArgs.Empty); };
        _input.GotFocus += (_, _) => Animator.Start(this, "focus", _focusT, 1, 130, v => { _focusT = v; Invalidate(); });
        _input.LostFocus += (_, _) => Animator.Start(this, "focus", _focusT, 0, 150, v => { _focusT = v; Invalidate(); });
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _input.Text.Length > 0)
            {
                _input.Clear();
                e.SuppressKeyPress = true;
            }
        };
        Controls.Add(_input);
    }

    private static Color FieldFill => Theme.WindowBg;

    [AllowNull]
    public override string Text
    {
        get => _input.Text;
        set => _input.Text = value ?? string.Empty;
    }

    public string PlaceholderText
    {
        get => _input.PlaceholderText;
        set => _input.PlaceholderText = value;
    }

    public new void Focus() => _input.Focus();

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var clear = _input.Text.Length > 0 ? 26 : 10;
        _input.SetBounds(30, (Height - _input.PreferredHeight) / 2 + 1, Math.Max(10, Width - 30 - clear), _input.PreferredHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hot = _input.Text.Length > 0 && e.X > Width - 26;
        if (hot != _hoverClear) { _hoverClear = hot; Invalidate(); }
        Cursor = hot ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hoverClear = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_input.Text.Length > 0 && e.X > Width - 26)
        {
            _input.Clear();
        }
        else
        {
            _input.Focus();
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ControlGround.Resolve(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(r, r.Height / 2))
        {
            using var fill = new SolidBrush(FieldFill);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Blend(Theme.GoldDim, Theme.SlateLight, _focusT));
            g.DrawPath(pen, path);
        }

        // Magnifier: a circle plus a handle, so it scales cleanly instead of relying on a font glyph.
        using (var pen = new Pen(Theme.Blend(Theme.Gold, Theme.OnDarkMuted, _focusT), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var cy = Height / 2f;
            g.DrawEllipse(pen, 11f, cy - 5f, 8f, 8f);
            g.DrawLine(pen, 18.5f, cy + 2.5f, 21.5f, cy + 5.5f);
        }

        if (_input.Text.Length > 0)
        {
            using var pen = new Pen(_hoverClear ? Theme.OnDark : Theme.OnDarkMuted, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var cx = Width - 16f;
            var cy = Height / 2f;
            g.DrawLine(pen, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f);
            g.DrawLine(pen, cx + 3.5f, cy - 3.5f, cx - 3.5f, cy + 3.5f);
        }
    }
}
