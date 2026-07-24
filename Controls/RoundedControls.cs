using System.Drawing.Drawing2D;

namespace Batcomputer;

internal static class ControlGround
{
    /// <summary>
    /// The nearest opaque colour behind a control. Owner-drawn controls have to clear themselves,
    /// and clearing with Color.Transparent paints BLACK, so a transparent parent has to be skipped
    /// until a real colour is found.
    /// </summary>
    public static Color Resolve(Control control)
    {
        for (var p = control.Parent; p is not null; p = p.Parent)
        {
            if (p.BackColor.A > 0)
            {
                return p.BackColor;
            }
        }
        return control.BackColor.A > 0 ? control.BackColor : Theme.PanelBg;
    }
}

/// <summary>
/// Panel with an ANTI-ALIASED rounded-rectangle background. Fills <see cref="Control.BackColor"/>
/// over the parent's ground so the corners are clean (unlike Region clipping, which is jagged).
/// Child controls should set <c>BackColor = Color.Transparent</c> to sit on the rounded fill.
/// </summary>
public sealed class RoundedPanel : Panel
{
    private int _radius = Theme.RadiusSm;
    public int CornerRadius { get => _radius; set { _radius = value; Invalidate(); } }
    public Color? BorderColor { get; set; }

    public RoundedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Invalidate(); // selection/hover recolors via BackColor → repaint the rounded fill
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ControlGround.Resolve(this)); // clean corners against the ground
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(r, _radius);
        using (var b = new SolidBrush(BackColor))
        {
            g.FillPath(b, path);
        }
        if (BorderColor is Color bc)
        {
            using var p = new Pen(bc);
            g.DrawPath(p, path);
        }
    }
}

/// <summary>
/// A modern pill toggle switch (owner-drawn, anti-aliased). Behaves like a <see cref="CheckBox"/>
/// (<see cref="CheckBox.Checked"/> + <see cref="CheckBox.CheckedChanged"/>) but renders as a
/// track + knob that slides, gold when on - the FModel/settings look.
/// </summary>
public sealed class ToggleSwitch : CheckBox
{
    // 0 = off, 1 = on. Animated so the knob slides and the track cross-fades instead of snapping.
    private double _t;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(46, 24);
        AutoSize = false;
        _t = Checked ? 1 : 0;
    }

    // A CheckBox draws a dotted focus rectangle when it has focus - the faint box around the pill.
    // We own the whole look, so suppress it.
    protected override bool ShowFocusCues => false;

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        // Slide from wherever the knob currently is to the new state.
        Animator.Start(this, "toggle", _t, Checked ? 1 : 0, 150, v => { _t = v; Invalidate(); });
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Animator.Cancel(this, "toggle");
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ControlGround.Resolve(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var h = Math.Min(Height, 24);
        var track = new Rectangle(0, (Height - h) / 2, Width - 1, h - 1);
        var t = _t;   // Blend weights the first colour by t, so the "on" colour comes first.
        using (var b = new SolidBrush(Theme.Blend(Theme.Gold, Theme.Slate, t)))
        using (var path = Theme.RoundedRect(track, h / 2))
        {
            g.FillPath(b, path);
            using var pen = new Pen(Theme.Blend(Theme.GoldDim, Theme.SlateLight, t));
            g.DrawPath(pen, path);
        }
        var d = h - 6;
        var offX = track.Left + 3;
        var onX = track.Right - d - 2;
        var kx = offX + (onX - offX) * (float)t;
        using (var kb = new SolidBrush(Theme.Blend(Theme.SlateDark, Theme.OnDarkMuted, t)))
        {
            g.FillEllipse(kb, kx, track.Top + 3, d, d);
        }
    }
}

/// <summary>
/// Square icon button with a drawn glyph, matching the rounded pills in the toolbar. The glyph is
/// drawn rather than typed - "↻" renders at wildly different weights across the fonts Windows falls
/// back to, and sits off-centre in most of them.
/// </summary>
public sealed class IconButton : Control
{
    public enum Glyph { Refresh }

    private double _hoverT;   // eased hover amount
    private double _spin;     // 0..1 one rotation, played when clicked

    public Glyph Icon { get; set; } = Glyph.Refresh;

    public IconButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(30, 30);
    }

    protected override void OnMouseEnter(EventArgs e)
    { Animator.Start(this, "hover", _hoverT, 1, 120, v => { _hoverT = v; Invalidate(); }); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    { Animator.Start(this, "hover", _hoverT, 0, 140, v => { _hoverT = v; Invalidate(); }); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        // A single spin of the arrow acknowledges the click - the refresh action is instant, so
        // without it there is no feedback that anything happened.
        Animator.Start(this, "spin", 0, 1, 380, v => { _spin = v; Invalidate(); }, Easing.InOutCubic);
        base.OnClick(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Animator.Cancel(this, "hover");
        Animator.Cancel(this, "spin");
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ControlGround.Resolve(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(r, Math.Min(8, r.Height / 2)))
        {
            using var fill = new SolidBrush(Theme.Blend(Theme.CardHi, Theme.Slate, _hoverT));
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Blend(Theme.GoldDim, Theme.SlateLight, _hoverT));
            g.DrawPath(pen, path);
        }

        var ink = Theme.Blend(Theme.Gold, Theme.OnDarkMuted, _hoverT);
        var state = g.Save();
        if (_spin > 0)
        {
            g.TranslateTransform(Width / 2f, Height / 2f);
            g.RotateTransform((float)(_spin * 360));
            g.TranslateTransform(-Width / 2f, -Height / 2f);
        }
        // Circular arrow: an arc with a gap, plus a solid arrowhead on the leading end.
        var box = new RectangleF(Width / 2f - 6f, Height / 2f - 6f, 12f, 12f);
        using (var pen = new Pen(ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen, box, 20, 300);
        }
        using (var b = new SolidBrush(ink))
        {
            var tip = new PointF(box.Right - 1f, box.Top + 2.5f);
            g.FillPolygon(b, new[]
            {
                tip,
                new PointF(tip.X - 4.5f, tip.Y - 1.5f),
                new PointF(tip.X + 0.5f, tip.Y - 5f),
            });
        }
        g.Restore(state);
    }
}

/// <summary>A small anti-aliased status dot (clean circle of <see cref="DotColor"/> on a transparent ground).</summary>
public sealed class StatusDot : Control
{
    private Color _dot = Theme.DefaultDot;
    public Color DotColor { get => _dot; set { _dot = value; Invalidate(); } }

    public StatusDot()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Paint the ground explicitly. WinForms transparency only resolves one level up, so a dot
        // inside a transparent container (a chip in a FlowLayoutPanel) would otherwise show black.
        g.Clear(ControlGround.Resolve(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var d = Math.Min(Width, Height) - 1;
        using var b = new SolidBrush(_dot);
        g.FillEllipse(b, (Width - d) / 2f, (Height - d) / 2f, d, d);
    }
}
