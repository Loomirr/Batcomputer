using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// Owner-drawn progress bar: a rounded track with a gold gradient fill. Replaces the stock
/// <see cref="ProgressBar"/>, which renders in Windows green regardless of the app theme.
/// Indeterminate mode sweeps a band across the track instead of the Windows marquee blocks.
/// </summary>
public sealed class ThemedProgressBar : Control
{
    private int _value;
    private int _maximum = 100;
    private bool _indeterminate;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private float _phase;

    public ThemedProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.WindowBg;
        Height = 10;
        _timer.Tick += (_, _) =>
        {
            _phase += 0.018f;
            if (_phase > 1f) _phase -= 1f;
            Invalidate();
        };
    }

    public int Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(1, value); Invalidate(); }
    }

    public int Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, _maximum); Invalidate(); }
    }

    /// <summary>Sweeping band for work with no known total.</summary>
    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            if (value) _timer.Start(); else _timer.Stop();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var h = Math.Min(Height, 10);
        var track = new Rectangle(0, (Height - h) / 2, Math.Max(2, Width - 1), h);
        var radius = h / 2;

        using (var path = Theme.RoundedRect(track, radius))
        {
            using var back = new SolidBrush(Theme.Slate);
            g.FillPath(back, path);
            using var pen = new Pen(Theme.LineSoft);
            g.DrawPath(pen, path);
        }

        Rectangle fill;
        if (_indeterminate)
        {
            var bandW = Math.Max(40, track.Width / 4);
            // Travel across the full track and off the far edge, then wrap.
            var x = (int)((track.Width + bandW) * _phase) - bandW;
            var left = Math.Max(track.Left, x);
            var right = Math.Min(track.Right, x + bandW);
            if (right <= left) return;
            fill = new Rectangle(left, track.Top, right - left, track.Height);
        }
        else
        {
            var w = (int)(track.Width * (_value / (float)_maximum));
            if (w <= 1) return;
            fill = new Rectangle(track.Left, track.Top, w, track.Height);
        }

        // Clip the fill to the rounded track so the ends stay round.
        var saved = g.Clip;
        using (var clip = Theme.RoundedRect(track, radius))
        {
            g.SetClip(clip, CombineMode.Replace);
            using var brush = new LinearGradientBrush(
                new Rectangle(fill.Left, fill.Top, Math.Max(1, fill.Width), fill.Height),
                Theme.GoldDim, Theme.Gold, LinearGradientMode.Horizontal);
            g.FillRectangle(brush, fill);
            g.SetClip(saved, CombineMode.Replace);
        }
        saved.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
