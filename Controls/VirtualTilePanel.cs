using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// An owner-drawn, virtualized tile grid. It paints ONLY the tiles
/// currently in view, so browsing the full ~1,800-part catalog never creates a Win32 control per
/// tile (no handle exhaustion, no lag, no "Load more" paging). Tiles are DATA (<see cref="Tile"/>),
/// not controls; the panel handles scroll, hover, click, right-click menus, tooltips, and drag-start
/// so it is a drop-in replacement for the old FlowLayoutPanel of Button tiles.
/// </summary>
public sealed class VirtualTilePanel : Panel
{
    /// <summary>One tile's data. <see cref="DragPayload"/> makes it draggable; <see cref="OnClick"/>
    /// makes it a clickable action tile; both may be set.</summary>
    public sealed class Tile
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public Color Accent { get; init; } = Color.Gray;
        public object? DragPayload { get; init; }
        public Action? OnClick { get; init; }
        public string? ToolTip { get; init; }
        public Func<ContextMenuStrip?>? MenuFactory { get; init; }
        public Image? Image { get; init; }
        public bool Dashed { get; init; }

        /// <summary>Optional group heading. Tiles are laid out in order; each change of Section
        /// starts a new row under its own heading. Empty = no heading (one ungrouped run).</summary>
        public string Section { get; init; } = "";
    }

    private const int TileW = 154;
    private const int TileH = 96;
    private const int Gap = 6;
    private const int SectionHeadingH = 22;
    private const int SectionSpacing = 10;

    /// <summary>Optional wrapped note painted above the tiles (e.g. a category intro line).</summary>
    public string HeaderText { get; set; } = "";

    /// <summary>
    /// An adaptive hero card + stat chips painted above the tiles (Home/Base "Workbench" header).
    /// When set it replaces <see cref="HeaderText"/>. Purely presentational - actions stay as tiles.
    /// </summary>
    public sealed class HeroModel
    {
        public sealed class WorkflowStep
        {
            public string Label = "";
            public string Detail = "";
            public Color Accent = Theme.Base;
            public bool Complete;
            public bool Current;
        }

        public string Overline = "";
        public string Title = "";
        public string Subtitle = "";
        public string Badge = "";
        public Color BadgeColor = Theme.Good;
        public Image? Thumb;
        public Color ThumbAccent = Theme.Base;
        public IReadOnlyList<(string Text, Color Dot)> Chips = Array.Empty<(string, Color)>();
        public IReadOnlyList<WorkflowStep> Workflow = Array.Empty<WorkflowStep>();
    }

    public HeroModel? Hero { get; private set; }

    private const int HeroCardH = 76;
    private const int HeroChipH = 24;

    private int HeroCardHeight() => Hero?.Workflow.Count > 0 ? 100 : HeroCardH;

    /// <summary>Sets (or clears) the hero header, disposing the previous hero's thumbnail.</summary>
    public void SetHero(HeroModel? hero)
    {
        if (!ReferenceEquals(Hero, hero))
        {
            Hero?.Thumb?.Dispose();
        }
        Hero = hero;
    }

    private int HeroBlockHeight()
    {
        if (Hero is null) return 0;
        var h = HeroCardHeight();
        if (Hero.Chips.Count > 0) h += 8 + HeroChipH;
        return h + Gap;
    }

    /// <summary>Optional message painted when there are no tiles (e.g. "no matches").</summary>
    public string EmptyMessage { get; set; } = "";

    private IReadOnlyList<Tile> _tiles = Array.Empty<Tile>();
    private int _columns = 1;
    private int _headerHeight;
    private int _hovered = -1;
    private int _hoverPaintIndex = -1;   // the tile currently showing the hover effect (survives fade-out)
    private double _hoverT;               // eased 0..1 hover amount for that tile
    private float _tiltX, _tiltY;         // cursor offset from the hovered tile centre, -1..1, for the lean
    private Point _dragStart;
    private bool _leftDown;
    private bool _dragging;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 12000, InitialDelay = 400, ReshowDelay = 100 };

    public VirtualTilePanel()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = Theme.CardBg;
        Theme.StyleTooltip(_toolTip); // readable dark tooltip for tile hints
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>Replaces the tiles and recomputes the virtual layout. Cheap regardless of count.</summary>
    public void SetTiles(IReadOnlyList<Tile> tiles)
    {
        DisposeTileImages(_tiles);
        _tiles = tiles ?? Array.Empty<Tile>();
        // The old hovered index means nothing against a new tile list - cancel any in-flight fade.
        Animator.Cancel(this, "tilehover");
        _hovered = -1;
        _hoverPaintIndex = -1;
        _hoverT = 0;
        RecomputeLayout();
        AutoScrollPosition = new Point(0, 0);
        Invalidate();
    }

    private static readonly Font NoteFont = new(FontFamily.GenericSansSerif, 8f);

    /// <summary>
    /// Positions are precomputed rather than derived from a row/column formula, because tiles may be
    /// grouped into sections: each new <see cref="Tile.Section"/> breaks the row and inserts a
    /// heading, so index → position is no longer uniform.
    /// </summary>
    private readonly List<Rectangle> _positions = new();
    private readonly List<(Rectangle Bounds, string Text)> _headings = new();

    private void RecomputeLayout()
    {
        var usableWidth = Math.Max(TileW + Gap, ClientSize.Width - Gap);
        _columns = Math.Max(1, (usableWidth - Gap) / (TileW + Gap));

        _headerHeight = 0;
        if (Hero is not null)
        {
            _headerHeight = HeroBlockHeight();
        }
        else if (!string.IsNullOrWhiteSpace(HeaderText))
        {
            var measured = TextRenderer.MeasureText(HeaderText, NoteFont,
                new Size(usableWidth - Gap, int.MaxValue), TextFormatFlags.WordBreak);
            _headerHeight = measured.Height + Gap * 2;
        }

        _positions.Clear();
        _headings.Clear();

        var y = Gap + _headerHeight;
        var col = 0;
        string? currentSection = null;

        foreach (var tile in _tiles)
        {
            var section = tile.Section ?? "";
            if (currentSection is null || !section.Equals(currentSection, StringComparison.Ordinal))
            {
                if (col > 0) // finish the partially-filled row of the previous section
                {
                    y += TileH + Gap;
                    col = 0;
                }
                currentSection = section;
                if (section.Length > 0)
                {
                    y += SectionSpacing;
                    _headings.Add((new Rectangle(Gap, y, Math.Max(0, ClientSize.Width - Gap * 2), SectionHeadingH), section));
                    y += SectionHeadingH;
                }
            }

            _positions.Add(new Rectangle(Gap + col * (TileW + Gap), y, TileW, TileH));
            col++;
            if (col >= _columns)
            {
                col = 0;
                y += TileH + Gap;
            }
        }
        if (col > 0)
        {
            y += TileH + Gap;
        }

        // Extra bottom clearance (a full tile) so the last row always scrolls fully into view even
        // when the panel is short (non-maximized) - a few px of slack at the very bottom is fine.
        AutoScrollMinSize = new Size(0, y + TileH);
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        RecomputeLayout();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            // The size may not have settled when SetTiles ran (grid was hidden) - recompute now.
            RecomputeLayout();
            Invalidate();
        }
    }

    private Rectangle TileBounds(int index)
    {
        if (index < 0 || index >= _positions.Count)
        {
            return Rectangle.Empty;
        }
        var r = _positions[index];
        r.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
        return r;
    }

    private int IndexAt(Point p)
    {
        // Content-space hit test (positions are stored unscrolled).
        var local = new Point(p.X - AutoScrollPosition.X, p.Y - AutoScrollPosition.Y);
        for (var i = 0; i < _positions.Count; i++)
        {
            if (_positions[i].Contains(local))
            {
                return i;
            }
        }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        // Tiles draw under a world transform (the hover tilt) and use GDI+ DrawString, which needs an
        // explicit hint - and antialiased smoothing so the sheared card edges stay clean.
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        if (Hero is not null && _headerHeight > 0)
        {
            DrawHero(e.Graphics, Gap + AutoScrollPosition.Y);
        }
        else if (!string.IsNullOrWhiteSpace(HeaderText) && _headerHeight > 0)
        {
            var headerRect = new Rectangle(Gap, Gap + AutoScrollPosition.Y,
                Math.Max(0, ClientSize.Width - Gap * 2), _headerHeight - Gap);
            TextRenderer.DrawText(e.Graphics, HeaderText, NoteFont, headerRect, Theme.OnDarkMuted,
                TextFormatFlags.WordBreak | TextFormatFlags.Left);
        }

        if (_tiles.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(EmptyMessage))
            {
                var msgRect = new Rectangle(Gap, Gap + _headerHeight,
                    Math.Max(0, ClientSize.Width - Gap * 2), Math.Max(40, ClientSize.Height - _headerHeight - Gap * 2));
                TextRenderer.DrawText(e.Graphics, EmptyMessage, NoteFont, msgRect, Theme.OnDarkMuted,
                    TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter);
            }
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var clip = e.ClipRectangle;

        // Section headings (gold label + hairline rule), scrolled with the content.
        foreach (var (bounds, text) in _headings)
        {
            var r = bounds;
            r.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
            if (r.Bottom < clip.Top || r.Top > clip.Bottom)
            {
                continue;
            }
            TextRenderer.DrawText(e.Graphics, text, SectionFont, r, Theme.Gold,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var textWidth = TextRenderer.MeasureText(text, SectionFont).Width;
            var lineY = r.Top + r.Height / 2;
            if (r.Right > r.Left + textWidth + 8)
            {
                using var pen = new Pen(Theme.SlateLight);
                e.Graphics.DrawLine(pen, r.Left + textWidth + 8, lineY, r.Right, lineY);
            }
        }

        for (var i = 0; i < _tiles.Count; i++)
        {
            var bounds = TileBounds(i);
            if (bounds.Bottom < clip.Top || bounds.Top > clip.Bottom)
            {
                continue; // virtualized: skip off-screen tiles
            }
            var tileHover = i == _hoverPaintIndex ? _hoverT : 0.0;
            // No cursor-follow lean when motion is off - it would freeze as a static skew.
            var lean = tileHover > 0 && Animator.Enabled;
            PaintTile(e.Graphics, _tiles[i], bounds, tileHover,
                lean ? _tiltX : 0f, lean ? _tiltY : 0f);
        }
    }

    // Cached hero fonts.
    private static readonly Font HeroTitleFont = new("Segoe UI", 13f, FontStyle.Bold);
    private static readonly Font HeroSubFont = new("Segoe UI", 8.75f);
    private static readonly Font HeroChipFont = new("Segoe UI", 8.25f, FontStyle.Bold);
    private static readonly Font HeroBadgeFont = new("Segoe UI", 8f, FontStyle.Bold);
    private static readonly Font HeroOverlineFont = new("Segoe UI", 7.5f, FontStyle.Bold);
    private static readonly Font HeroWorkflowLabelFont = new("Segoe UI", 7.5f, FontStyle.Bold);
    private static readonly Font HeroWorkflowDetailFont = new("Segoe UI", 7f);

    /// <summary>Paints the adaptive hero card + stat chips at content-space top <paramref name="top"/>.</summary>
    private void DrawHero(Graphics g, int top)
    {
        if (Hero is null) return;
        var usableW = Math.Max(80, ClientSize.Width - Gap * 2);
        var card = new Rectangle(Gap, top, usableW, HeroCardHeight());

        Theme.FillRoundedCard(g, card, Theme.CardHi, Theme.LineSoft, Theme.Radius);
        // Gold accent bar on the left edge.
        using (var bar = new SolidBrush(Theme.Gold))
        using (var barPath = Theme.RoundedRect(new Rectangle(card.Left, card.Top + 10, 3, card.Height - 20), 2))
        {
            g.FillPath(bar, barPath);
        }

        var x = card.Left + 14;
        // Thumbnail (image or accent tile).
        var thumb = new Rectangle(x, card.Top + 12, 52, 52);
        using (var tp = Theme.RoundedRect(thumb, 8))
        {
            if (Hero.Thumb is not null)
            {
                var saved = g.Clip;
                g.SetClip(tp, CombineMode.Replace);
                DrawImageCover(g, Hero.Thumb, thumb);
                g.SetClip(saved, CombineMode.Replace);
                saved.Dispose();
                using var pen = new Pen(Theme.LineSoft);
                g.DrawPath(pen, tp);
            }
            else
            {
                using var b = new SolidBrush(Theme.Blend(Hero.ThumbAccent, Theme.CardBg, 0.35));
                g.FillPath(b, tp);
                using var pen = new Pen(Theme.Blend(Hero.ThumbAccent, Theme.LineSoft, 0.6));
                g.DrawPath(pen, tp);
            }
        }
        x = thumb.Right + 14;

        var titleRight = card.Right - 12;
        var drawWorkflow = false;
        var stepBounds = new List<(Rectangle Bounds, HeroModel.WorkflowStep Step)>();
        if (Hero.Workflow.Count > 0)
        {
            const int stepGap = 5;
            const int minTextW = 138;
            const int minStepW = 72;
            var available = card.Right - 12 - x;
            var gapTotal = stepGap * (Hero.Workflow.Count - 1);
            if (available >= minTextW + minStepW * Hero.Workflow.Count + gapTotal + 10)
            {
                var stepW = Math.Min(108, Math.Max(minStepW,
                    (available - minTextW - gapTotal) / Hero.Workflow.Count));
                var stepsW = stepW * Hero.Workflow.Count + gapTotal;
                var start = card.Right - 12 - stepsW;
                titleRight = start - 10;
                for (var i = 0; i < Hero.Workflow.Count; i++)
                {
                    stepBounds.Add((new Rectangle(start + i * (stepW + stepGap), card.Top + 13, stepW, 54), Hero.Workflow[i]));
                }
                drawWorkflow = true;
            }
        }

        var hasOverline = !string.IsNullOrWhiteSpace(Hero.Overline);
        var overlineTop = card.Top + 10;
        var titleTop = card.Top + (hasOverline ? 24 : 14);
        var subtitleTop = card.Top + (hasOverline ? 48 : 40);
        if (hasOverline)
        {
            TextRenderer.DrawText(g, Hero.Overline, HeroOverlineFont,
                new Rectangle(x, overlineTop, Math.Max(0, titleRight - x), 14), Hero.ThumbAccent,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        // Title + optional badge pill.
        var titleWidth = Math.Max(0, titleRight - x);
        var titleSize = TextRenderer.MeasureText(g, Hero.Title, HeroTitleFont, new Size(titleWidth, 24), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Hero.Title, HeroTitleFont,
            new Rectangle(x, titleTop, titleWidth, 24), Theme.OnDark,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (!string.IsNullOrEmpty(Hero.Badge) && !drawWorkflow)
        {
            var badgeX = x + Math.Min(titleSize.Width, titleWidth - 120) + 10;
            DrawPill(g, ref badgeX, titleTop + 2, Hero.Badge, Hero.BadgeColor, HeroBadgeFont,
                filled: true, dot: false);
        }

        TextRenderer.DrawText(g, Hero.Subtitle, HeroSubFont,
            new Rectangle(x, subtitleTop, titleWidth, 20), Theme.OnDarkMuted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        foreach (var (bounds, step) in stepBounds)
        {
            DrawWorkflowStep(g, bounds, step);
        }

        // Stat chips row.
        if (Hero.Chips.Count > 0)
        {
            var cx = card.Left + 2;
            var cy = card.Bottom + 8;
            foreach (var (text, dot) in Hero.Chips)
            {
                DrawPill(g, ref cx, cy, text, dot, HeroChipFont, filled: false, dot: true);
                cx += 8;
            }
        }
    }

    private static void DrawWorkflowStep(Graphics g, Rectangle bounds, HeroModel.WorkflowStep step)
    {
        var fill = step.Current
            ? Theme.Blend(step.Accent, Theme.CardBg, 0.24)
            : Theme.Slate;
        var border = Theme.Blend(step.Accent, Theme.LineSoft, step.Current || step.Complete ? 0.72 : 0.42);
        Theme.FillRoundedCard(g, bounds, fill, border, 6);

        var accent = step.Complete ? Theme.Good : step.Accent;
        TextRenderer.DrawText(g, step.Label, HeroWorkflowLabelFont,
            new Rectangle(bounds.Left + 8, bounds.Top + 7, bounds.Width - 16, 15), accent,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, step.Detail, HeroWorkflowDetailFont,
            new Rectangle(bounds.Left + 8, bounds.Top + 25, bounds.Width - 16, 20), Theme.OnDarkMuted,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    /// <summary>Draws a rounded pill starting at <paramref name="x"/> and advances x past it.</summary>
    private static void DrawPill(Graphics g, ref int x, int y, string text, Color color, Font font, bool filled, bool dot)
    {
        var tw = TextRenderer.MeasureText(g, text, font, new Size(400, HeroChipH), TextFormatFlags.NoPadding).Width;
        var w = tw + (dot ? 26 : 18);
        var rect = new Rectangle(x, y, w, HeroChipH);
        var fill = filled ? Theme.Blend(color, Theme.CardBg, 0.32) : Theme.Slate;
        var border = filled ? Theme.Blend(color, Theme.LineSoft, 0.7) : Theme.LineSoft;
        Theme.FillRoundedCard(g, rect, fill, border, HeroChipH / 2);
        var tx = rect.Left + 10;
        if (dot)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(color);
            g.FillEllipse(b, rect.Left + 9, rect.Top + HeroChipH / 2 - 3, 6, 6);
            tx = rect.Left + 20;
        }
        TextRenderer.DrawText(g, text, font, new Rectangle(tx, rect.Top, tw + 4, HeroChipH),
            filled ? Theme.OnDark : Theme.OnDarkMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        x += w;
    }

    // Modern type ramp for tiles (Segoe UI, cached - no per-paint allocation).
    private static readonly Font SectionFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private static readonly Font TileTitleFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    private static readonly Font TileSubFont = new("Segoe UI", 8f);

    private static readonly StringFormat TileTextFormat = new(StringFormatFlags.LineLimit)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Near,
        Trimming = StringTrimming.EllipsisCharacter,
    };

    /// <summary>
    /// Rounded modern card. On hover it leans toward the cursor (a sheared "3D" tilt - GDI+ has no
    /// true perspective, so this is an affine approximation) and casts a gold glow. Because the card
    /// is drawn under a world transform and GDI's TextRenderer ignores transforms, tile text uses
    /// GDI+ DrawString here. <paramref name="tiltX"/>/<paramref name="tiltY"/> are the cursor offset
    /// from the tile centre in -1..1.
    /// </summary>
    private static void PaintTile(Graphics g, Tile tile, Rectangle bounds, double hover, float tiltX, float tiltY)
    {
        var r = Rectangle.Inflate(bounds, -1, -1); // room for the stroke
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;

        // Gold glow behind the card - a few translucent haloes, only the margin shows past the
        // opaque card, reading as a soft glow. Drawn untransformed so it stays a steady pool.
        if (hover > 0 && !tile.Dashed)
        {
            for (var j = 3; j >= 1; j--)
            {
                var a = (int)Math.Round(hover * (26 - j * 6));
                if (a <= 0) continue;
                using var halo = new SolidBrush(Color.FromArgb(a, Theme.Gold));
                using var hp = Theme.RoundedRect(Rectangle.Inflate(r, j * 3, j * 3), Theme.RadiusSm + j * 2);
                g.FillPath(halo, hp);
            }
        }

        var state = g.Save();
        if (hover > 0)
        {
            // Lift toward the viewer (scale up) and nudge toward the cursor. A shear was tried and
            // read as a skewed parallelogram - GDI+ can't foreshorten - so the card stays rectangular
            // and just slides a few px within its glow, which reads as leaning without the skew.
            var scale = 1f + 0.05f * (float)hover;
            var shift = 5f * (float)hover;
            using var m = new System.Drawing.Drawing2D.Matrix();
            m.Translate(cx + tiltX * shift, cy + tiltY * shift);
            m.Scale(scale, scale);
            m.Translate(-cx, -cy);
            g.MultiplyTransform(m);
        }

        // Blend weights the first colour by hover, so the hovered colour comes first.
        var fill = tile.Dashed
            ? Theme.Blend(Theme.Slate, Theme.PanelBg, hover)
            : Theme.Blend(Theme.CardHi, Theme.CardBg, hover);

        using var path = Theme.RoundedRect(r, Theme.RadiusSm);
        using (var back = new SolidBrush(fill))
        {
            g.FillPath(back, path);
        }

        if (tile.Image is not null)
        {
            var saved = g.Clip;
            g.SetClip(path, CombineMode.Replace);
            DrawImageCover(g, tile.Image, r);
            // The legibility wash lifts a touch on hover so the artwork reads brighter underneath.
            var washA = (int)Math.Round(150 - 40 * hover);
            using (var wash = new SolidBrush(Color.FromArgb(washA, Theme.CardBg)))
            {
                g.FillPath(wash, path); // text-legibility layer over the artwork
            }
            g.SetClip(saved, CombineMode.Replace);
            saved.Dispose();
        }

        // Dominant category color: border warms toward gold on hover for the glow, else the accent.
        using (var pen = new Pen(tile.Dashed
                   ? tile.Accent
                   : Theme.Blend(Theme.Gold, Theme.Blend(tile.Accent, Theme.LineSoft, 0.55), hover)))
        {
            if (tile.Dashed) pen.DashStyle = DashStyle.Dash;
            g.DrawPath(pen, path);
        }

        // Accent title on flat tiles (strong colour coding); white over artwork for legibility.
        var titleColor = tile.Image is not null ? Theme.OnDark : tile.Accent;
        using (var tb = new SolidBrush(titleColor))
        {
            g.DrawString(tile.Title, TileTitleFont, tb,
                new RectangleF(r.X + 8, r.Y + 8, r.Width - 16, 34), TileTextFormat);
        }
        if (!string.IsNullOrEmpty(tile.Subtitle))
        {
            using var sb = new SolidBrush(Theme.OnDarkMuted);
            g.DrawString(tile.Subtitle, TileSubFont, sb,
                new RectangleF(r.X + 8, r.Y + 44, r.Width - 16, r.Height - 50), TileTextFormat);
        }

        g.Restore(state);
    }

    private static void DrawImageCover(Graphics g, Image image, Rectangle bounds)
    {
        if (image.Width <= 0 || image.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var sourceRatio = (double)image.Width / image.Height;
        var targetRatio = (double)bounds.Width / bounds.Height;
        Rectangle source;
        if (sourceRatio > targetRatio)
        {
            var sourceWidth = (int)Math.Round(image.Height * targetRatio);
            source = new Rectangle((image.Width - sourceWidth) / 2, 0, sourceWidth, image.Height);
        }
        else
        {
            var sourceHeight = (int)Math.Round(image.Width / targetRatio);
            source = new Rectangle(0, (image.Height - sourceHeight) / 2, image.Width, sourceHeight);
        }

        g.DrawImage(image, bounds, source, GraphicsUnit.Pixel);
    }

    private static void DisposeTileImages(IEnumerable<Tile> tiles)
    {
        foreach (var tile in tiles)
        {
            try { tile.Image?.Dispose(); } catch { /* best effort */ }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_leftDown && !_dragging)
        {
            var dragRect = new Rectangle(
                _dragStart.X - SystemInformation.DragSize.Width / 2,
                _dragStart.Y - SystemInformation.DragSize.Height / 2,
                SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
            if (!dragRect.Contains(e.Location))
            {
                var idx = IndexAt(_dragStart);
                if (idx >= 0 && _tiles[idx].DragPayload is { } payload)
                {
                    _dragging = true;
                    DoDragDrop(payload, DragDropEffects.Copy);
                    _dragging = false;
                    _leftDown = false;
                }
            }
            return;
        }

        var hover = IndexAt(e.Location);
        if (hover != _hovered)
        {
            var previous = _hovered;
            _hovered = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            _toolTip.SetToolTip(this, hover >= 0 ? _tiles[hover].ToolTip ?? "" : "");
            if (hover >= 0)
            {
                SeedTilt(hover, e.Location); // start the lean at the entry point, not a stale value
            }
            SetHoverTile(hover, previous);
        }
        else if (hover >= 0 && Animator.Enabled)
        {
            // Same tile, cursor moved: update the lean so it follows, and repaint just this tile.
            UpdateTilt(hover, e.Location);
        }
    }

    /// <summary>Sets the lean for a tile the cursor just entered, without the move threshold.</summary>
    private void SeedTilt(int index, Point location)
    {
        var b = TileBounds(index);
        if (b == Rectangle.Empty) return;
        _tiltX = Math.Clamp((float)((location.X - b.Left) / (double)b.Width * 2 - 1), -1f, 1f);
        _tiltY = Math.Clamp((float)((location.Y - b.Top) / (double)b.Height * 2 - 1), -1f, 1f);
    }

    /// <summary>Recomputes the cursor-relative lean for the hovered tile and repaints it.</summary>
    private void UpdateTilt(int index, Point location)
    {
        var b = TileBounds(index);
        if (b == Rectangle.Empty) return;
        var nx = Math.Clamp((float)((location.X - b.Left) / (double)b.Width * 2 - 1), -1f, 1f);
        var ny = Math.Clamp((float)((location.Y - b.Top) / (double)b.Height * 2 - 1), -1f, 1f);
        if (Math.Abs(nx - _tiltX) < 0.02f && Math.Abs(ny - _tiltY) < 0.02f) return;
        _tiltX = nx;
        _tiltY = ny;
        InvalidateTile(index);
    }

    /// <summary>
    /// Eases the hover effect onto <paramref name="index"/> (or off, when -1). Exactly one tile is
    /// ever elevated: <see cref="_hoverPaintIndex"/> is a single index, and the boundary crossing does
    /// a full repaint so a tile the cursor has left can never stay raised. Per-frame repaints during
    /// the ease are limited to the one animating tile.
    /// </summary>
    private void SetHoverTile(int index, int previous)
    {
        var target = index >= 0 ? 1.0 : 0.0;
        // Entering a tile: it becomes the elevated one immediately (continuing from the current
        // amount so a sweep feels smooth). Leaving: keep the old index so it can ease back down.
        if (index >= 0)
        {
            _hoverPaintIndex = index;
        }
        var painting = _hoverPaintIndex;

        // Repaint the whole panel once on the change itself. Only _hoverPaintIndex draws raised, so
        // this is what guarantees a just-left tile drops - no stale, no two tiles up at once.
        Invalidate();

        Animator.Start(this, "tilehover", _hoverT, target, index >= 0 ? 120 : 150, v =>
        {
            _hoverT = v;
            InvalidateTile(painting);
        }, onDone: () =>
        {
            if (target == 0.0 && _hoverPaintIndex == painting)
            {
                _hoverPaintIndex = -1;
            }
        });
    }

    /// <summary>Repaints one tile plus a margin covering the glow halo and the tilt overflow.</summary>
    private void InvalidateTile(int index)
    {
        var b = TileBounds(index);
        if (b != Rectangle.Empty)
        {
            b.Inflate(14, 14);
            Invalidate(b);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var idx = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            _leftDown = true;
            _dragStart = e.Location;
        }
        else if (e.Button == MouseButtons.Right && idx >= 0)
        {
            var menu = _tiles[idx].MenuFactory?.Invoke();
            menu?.Show(this, e.Location);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _leftDown && !_dragging)
        {
            var idx = IndexAt(e.Location);
            if (idx >= 0)
            {
                _tiles[idx].OnClick?.Invoke();
            }
        }
        _leftDown = false;
    }

    // Owner-drawn content is positioned relative to AutoScrollPosition, so every scroll must
    // repaint the whole surface - otherwise tiles look frozen/cut off while scrolling.
    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered != -1)
        {
            var previous = _hovered;
            _hovered = -1;
            SetHoverTile(-1, previous);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Animator.Cancel(this, "tilehover");
            DisposeTileImages(_tiles);
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
