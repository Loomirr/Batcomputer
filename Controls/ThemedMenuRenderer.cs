using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// Dark renderer for every <see cref="ToolStrip"/>/<see cref="ContextMenuStrip"/> in the app.
/// Applied once at startup via <c>ToolStripManager.Renderer</c>, so all menus pick it up without
/// per-menu changes - the app's context menus were otherwise stock light grey on a dark window.
/// </summary>
public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new ThemedColorTable())
    {
        RoundedEdges = false;
    }

    /// <summary>Applies the renderer process-wide.</summary>
    public static void Apply() => ToolStripManager.Renderer = new ThemedMenuRenderer();

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var path = Theme.RoundedRect(r, Theme.RadiusSm);
            using var b = new SolidBrush(Theme.SlateDark);
            e.Graphics.FillPath(b, path);
            return;
        }
        using var flat = new SolidBrush(Theme.Slate);
        e.Graphics.FillRectangle(flat, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = Theme.RoundedRect(r, Theme.RadiusSm);
        using var pen = new Pen(Theme.LineSoft);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (!item.Selected || !item.Enabled)
        {
            return;
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(3, 0, item.Width - 7, item.Height - 1);
        using var path = Theme.RoundedRect(r, 5);
        using var b = new SolidBrush(Theme.CardHi);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.Blend(Theme.OnDarkMuted, Theme.SlateDark, 0.55)
            : e.Item.Selected ? Theme.Gold
            : Theme.OnDark;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.LineSoft);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Selected == true ? Theme.Gold : Theme.OnDarkMuted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // The stock image gutter is a lighter vertical band - flatten it into the menu ground.
        using var b = new SolidBrush(Theme.SlateDark);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    private sealed class ThemedColorTable : ProfessionalColorTable
    {
        public ThemedColorTable() { UseSystemColors = false; }

        public override Color ToolStripDropDownBackground => Theme.SlateDark;
        public override Color MenuBorder => Theme.LineSoft;
        public override Color MenuItemBorder => Theme.CardHi;
        public override Color MenuItemSelected => Theme.CardHi;
        public override Color MenuItemSelectedGradientBegin => Theme.CardHi;
        public override Color MenuItemSelectedGradientEnd => Theme.CardHi;
        public override Color MenuItemPressedGradientBegin => Theme.Slate;
        public override Color MenuItemPressedGradientMiddle => Theme.Slate;
        public override Color MenuItemPressedGradientEnd => Theme.Slate;
        public override Color ImageMarginGradientBegin => Theme.SlateDark;
        public override Color ImageMarginGradientMiddle => Theme.SlateDark;
        public override Color ImageMarginGradientEnd => Theme.SlateDark;
        public override Color SeparatorDark => Theme.LineSoft;
        public override Color SeparatorLight => Theme.LineSoft;
        public override Color ToolStripBorder => Theme.LineSoft;
        public override Color ToolStripGradientBegin => Theme.Slate;
        public override Color ToolStripGradientMiddle => Theme.Slate;
        public override Color ToolStripGradientEnd => Theme.Slate;
        public override Color CheckBackground => Theme.Slate;
        public override Color CheckSelectedBackground => Theme.GoldDim;
        public override Color ButtonSelectedHighlight => Theme.CardHi;
        public override Color ButtonSelectedGradientBegin => Theme.CardHi;
        public override Color ButtonSelectedGradientEnd => Theme.CardHi;
    }
}
