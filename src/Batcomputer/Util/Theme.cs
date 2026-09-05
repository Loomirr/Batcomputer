using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>Batman slate palette with a selectable header and primary accent.</summary>
internal static class Theme
{
    internal readonly record struct VisualThemeDefinition(
        string Name,
        string HeaderAsset,
        string IconAsset,
        Color Accent,
        Color AccentDim,
        Color AccentHover,
        Color SecondaryAccent,
        string Description);

    private static readonly VisualThemeDefinition ClassicTheme = new(
        "Classic",
        "Header.png",
        "Icon.ico",
        Color.FromArgb(240, 194, 48),
        Color.FromArgb(199, 154, 30),
        Color.FromArgb(246, 205, 71),
        Color.FromArgb(240, 194, 48),
        "The original header with Batcomputer's gold highlights.");

    private static readonly VisualThemeDefinition AlternateTheme = new(
        "Alternate",
        "header2.png",
        "Icon.ico",
        Color.FromArgb(74, 174, 242),
        Color.FromArgb(43, 126, 184),
        Color.FromArgb(103, 195, 250),
        Color.FromArgb(74, 174, 242),
        "The alternate header with cool blue highlights.");

    private static readonly VisualThemeDefinition MayhemTheme = new(
        "Mayhem Mode",
        "HeaderMayhem.png",
        "Mayhem.ico",
        Color.FromArgb(178, 82, 255),
        Color.FromArgb(112, 45, 181),
        Color.FromArgb(211, 255, 99),
        Color.FromArgb(188, 255, 54),
        "The Mayhem header and icon with purple and lime highlights.");

    public static IReadOnlyList<VisualThemeDefinition> VisualThemes { get; } =
        Array.AsReadOnly([ClassicTheme, AlternateTheme, MayhemTheme]);

    public static VisualThemeDefinition ResolveVisualTheme(string? name)
    {
        if (string.Equals(name, "Alternate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Batcompuper", StringComparison.OrdinalIgnoreCase))
        {
            return AlternateTheme;
        }
        if (string.Equals(name, "Mayhem Mode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Mayhem", StringComparison.OrdinalIgnoreCase))
        {
            return MayhemTheme;
        }

        return ClassicTheme;
    }

    public static VisualThemeDefinition CurrentVisualTheme =>
        ResolveVisualTheme(AppSettings.Current.VisualTheme);

    public static readonly Color WindowBg = Color.FromArgb(26, 29, 34);
    public static readonly Color SlateDark = Color.FromArgb(30, 33, 39);
    public static readonly Color Slate = Color.FromArgb(43, 47, 54);
    public static readonly Color SlateLight = Color.FromArgb(60, 65, 74);
    // Compatibility names used throughout the UI. They now resolve to the active theme's
    // accent so existing screens participate without changing their category/status colors.
    public static Color Gold => CurrentVisualTheme.Accent;
    public static Color GoldDim => CurrentVisualTheme.AccentDim;
    public static Color GoldHover => CurrentVisualTheme.AccentHover;
    public static Color SecondaryAccent => CurrentVisualTheme.SecondaryAccent;
    public static readonly Color OnDark = Color.FromArgb(236, 238, 242);
    public static readonly Color OnDarkMuted = Color.FromArgb(158, 166, 178);

    // Card surfaces on the dark window.
    public static readonly Color CardBg = Color.FromArgb(46, 50, 58);
    public static readonly Color PanelBg = Color.FromArgb(26, 29, 34);

    // Vivid category accents (readable on dark).
    public static readonly Color Mods = Color.FromArgb(242, 137, 55);      // orange
    public static readonly Color Base = Color.FromArgb(70, 152, 240);      // blue
    public static readonly Color Materials = Color.FromArgb(42, 206, 152); // teal
    public static readonly Color Parts = Color.FromArgb(158, 144, 250);    // purple
    public static readonly Color Equipment = Color.FromArgb(244, 176, 62); // amber
    public static readonly Color Abilities = Color.FromArgb(224, 105, 238); // magenta
    public static readonly Color Animations = Color.FromArgb(236, 110, 173); // pink
    public static readonly Color Faces = Color.FromArgb(240, 138, 96);     // coral
    public static readonly Color Gliders = Color.FromArgb(96, 200, 226);   // sky
    public static readonly Color Textures = Color.FromArgb(100, 230, 245); // cyan
    public static readonly Color Research = Color.FromArgb(188, 160, 255); // lavender
    public static readonly Color Inspector = Color.FromArgb(150, 156, 166);// gray

    public static readonly Color CustomDot = Color.FromArgb(46, 210, 156);
    public static readonly Color DefaultDot = Color.FromArgb(108, 114, 124);

    /// <summary>Blends an accent toward the card surface so it reads as a subtle fill on dark.</summary>
    public static Color Tint(Color accent) => Blend(accent, CardBg, 0.24);

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R * t + b.R * (1 - t)),
        (int)(a.G * t + b.G * (1 - t)),
        (int)(a.B * t + b.B * (1 - t)));

    public static Color CategoryColor(string category) => category switch
    {
        "Mods" => Mods,
        "Base" => Base,
        "Materials" => Materials,
        "Parts" => Parts,
        "Equipment" => Equipment,
        "Abilities" => Abilities,
        "Animations" => Animations,
        "Faces" => Faces,
        "Gliders" => Gliders,
        "Textures" => Textures,
        "Build mod" => Gold,
        "Research" => Research,
        _ => Inspector
    };

    public static void PaintHeaderGradient(Graphics g, Rectangle rect)
    {
        using var brush = new LinearGradientBrush(rect, SlateDark, Slate, LinearGradientMode.Horizontal);
        g.FillRectangle(brush, rect);
        using var accent = new LinearGradientBrush(
            new Rectangle(rect.Left, rect.Bottom - 2, Math.Max(1, rect.Width), 2),
            Gold,
            SecondaryAccent,
            LinearGradientMode.Horizontal);
        g.FillRectangle(accent, rect.Left, rect.Bottom - 2, rect.Width, 2);
    }

    /// <summary>Clips a control to rounded corners (re-applied on resize) - cheap modern buttons.</summary>
    public static void RoundControl(Control c, int radius = RadiusSm)
    {
        void Apply()
        {
            if (c.Width <= 1 || c.Height <= 1) { return; }
            using var p = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
            c.Region = new Region(p);
        }
        Apply();
        c.Resize += (_, _) => Apply();
    }

    /// <summary>Styles a button as a flat primary action using the active accent.</summary>
    public static void StyleGoldButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = GoldHover;
        b.FlatAppearance.MouseDownBackColor = GoldDim;
        b.BackColor = Gold;
        b.ForeColor = SlateDark;
        b.Font = new Font(b.Font, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
    }

    /// <summary>Styles a button as a flat dark chrome button (rounded, with hover).</summary>
    public static void StyleDarkButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = LineSoft;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = CardHi;
        b.BackColor = Slate;
        b.ForeColor = OnDark;
        b.Cursor = Cursors.Hand;
    }

    public static void StyleDarkCombo(ComboBox c)
    {
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Slate; // redesign: recessed field, less "raised 3D" than SlateLight
        c.ForeColor = OnDark;
    }

    public static void StyleDarkInput(TextBoxBase t)
    {
        t.BackColor = SlateLight;
        t.ForeColor = OnDark;
        t.BorderStyle = BorderStyle.FixedSingle;
    }

    private static readonly Font TooltipFont = AppFonts.Condensed(9f, FontStyle.Bold);

    /// <summary>Dark, high-contrast tooltip (owner-drawn) so multi-line hints read clearly.</summary>
    public static void StyleTooltip(ToolTip t)
    {
        t.OwnerDraw = true;
        t.BackColor = Slate;
        t.ForeColor = OnDark;
        t.Draw -= TooltipDraw;
        t.Draw += TooltipDraw;
    }

    private static void TooltipDraw(object? sender, DrawToolTipEventArgs e)
    {
        using (var b = new SolidBrush(Slate))
        {
            e.Graphics.FillRectangle(b, e.Bounds);
        }
        using (var p = new Pen(SlateLight))
        {
            e.Graphics.DrawRectangle(p, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, TooltipFont, e.Bounds, OnDark,
            TextFormatFlags.Left | TextFormatFlags.Top);
    }

    public static void StyleSmallDarkButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = LineSoft;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = CardHi;
        b.BackColor = Slate;
        b.ForeColor = OnDark;
        b.Cursor = Cursors.Hand;
    }

    /// <summary>
    /// Recolors controls which captured the previous primary accent when they were constructed.
    /// Owner-drawn controls read <see cref="Gold"/> on every paint, so invalidating the tree handles
    /// those automatically. New dialogs always start directly on the selected palette.
    /// </summary>
    public static void RefreshAccentTheme(Control root, VisualThemeDefinition previousTheme)
    {
        var currentTheme = CurrentVisualTheme;
        RefreshAccentTheme(root, previousTheme, currentTheme);
        root.Invalidate(true);
    }

    private static void RefreshAccentTheme(
        Control control,
        VisualThemeDefinition previousTheme,
        VisualThemeDefinition currentTheme)
    {
        control.BackColor = RemapAccent(control.BackColor, previousTheme, currentTheme);
        control.ForeColor = RemapAccent(control.ForeColor, previousTheme, currentTheme);

        if (control is Button button)
        {
            button.FlatAppearance.BorderColor = RemapAccent(
                button.FlatAppearance.BorderColor,
                previousTheme,
                currentTheme);
            button.FlatAppearance.MouseOverBackColor = RemapAccent(
                button.FlatAppearance.MouseOverBackColor,
                previousTheme,
                currentTheme);
            button.FlatAppearance.MouseDownBackColor = RemapAccent(
                button.FlatAppearance.MouseDownBackColor,
                previousTheme,
                currentTheme);
        }

        if (control is RoundedPanel rounded && rounded.BorderColor is Color border)
        {
            rounded.BorderColor = RemapAccent(border, previousTheme, currentTheme);
        }

        if (control is StatusDot dot)
        {
            dot.DotColor = RemapAccent(dot.DotColor, previousTheme, currentTheme);
        }

        if (control is DataGridView grid)
        {
            StyleGrid(grid);
        }

        foreach (Control child in control.Controls)
        {
            RefreshAccentTheme(child, previousTheme, currentTheme);
        }
    }

    internal static Color RemapAccent(
        Color value,
        VisualThemeDefinition previousTheme,
        VisualThemeDefinition currentTheme)
    {
        if (SameRgb(value, previousTheme.Accent))
        {
            return Color.FromArgb(value.A, currentTheme.Accent);
        }
        if (SameRgb(value, previousTheme.AccentDim))
        {
            return Color.FromArgb(value.A, currentTheme.AccentDim);
        }
        if (SameRgb(value, previousTheme.AccentHover))
        {
            return Color.FromArgb(value.A, currentTheme.AccentHover);
        }
        if (SameRgb(value, previousTheme.SecondaryAccent))
        {
            return Color.FromArgb(value.A, currentTheme.SecondaryAccent);
        }
        return value;
    }

    private static bool SameRgb(Color left, Color right) =>
        left.R == right.R && left.G == right.G && left.B == right.B;

    /// <summary>
    /// Applies a readable dark theme to a whole WinForms subtree. This is mainly
    /// for the Advanced/fallback window, where many controls are built with
    /// vanilla WinForms defaults and otherwise become white-on-light-gray ghosts.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly HashSet<IntPtr> DarkTitleBars = new();

    /// <summary>
    /// Asks DWM to draw a form's title bar dark. Without it the caption uses the user's Windows accent
    /// colour, which lands anywhere from purple to lime against this UI. Silently does nothing on
    /// Windows 10 builds older than 1809, where the attribute does not exist.
    /// </summary>
    public static void UseDarkTitleBar(Form form)
    {
        if (!form.IsHandleCreated || !DarkTitleBars.Add(form.Handle))
        {
            return;
        }
        try
        {
            var on = 1;
            // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE; it was 19 before Windows 10 20H1.
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
            }
        }
        catch
        {
            // No dwmapi (or an OS that doesn't know the attribute) - the light caption is cosmetic.
        }
    }

    /// <summary>
    /// Darkens the caption of every window the app opens. WinForms has no "form created" event, so
    /// this rides Application.Idle and sweeps the open forms; already-darkened handles are skipped.
    /// </summary>
    public static void ApplyDarkTitleBarsAppWide()
    {
        Application.Idle += (_, _) =>
        {
            foreach (Form form in Application.OpenForms)
            {
                UseDarkTitleBar(form);
            }
        };
    }

    public static void ApplyReadableTheme(Control root)
    {
        ApplyReadableTheme(root, isRoot: true);
    }

    private static void ApplyReadableTheme(Control c, bool isRoot)
    {
        switch (c)
        {
            case Form form:
                form.BackColor = WindowBg;
                form.ForeColor = OnDark;
                break;

            case TabControl tab:
                tab.BackColor = WindowBg;
                tab.ForeColor = OnDark;
                break;

            case TabPage page:
                page.UseVisualStyleBackColor = false;
                page.BackColor = WindowBg;
                page.ForeColor = OnDark;
                break;

            case GroupBox group:
                group.BackColor = WindowBg;
                group.ForeColor = OnDark;
                break;

            case Panel or TableLayoutPanel or FlowLayoutPanel:
                c.BackColor = isRoot ? WindowBg : PanelBg;
                c.ForeColor = OnDark;
                break;

            case SplitContainer split:
                split.BackColor = PanelBg;
                split.ForeColor = OnDark;
                split.Panel1.BackColor = PanelBg;
                split.Panel1.ForeColor = OnDark;
                split.Panel2.BackColor = PanelBg;
                split.Panel2.ForeColor = OnDark;
                break;

            case Label label:
                if (label.ForeColor == SystemColors.ControlText ||
                    label.ForeColor == Color.Black ||
                    label.ForeColor == Color.DimGray ||
                    label.ForeColor.ToArgb() == Color.FromArgb(240, 240, 240).ToArgb())
                {
                    label.ForeColor = OnDark;
                }
                if (label.BackColor == SystemColors.Control ||
                    label.BackColor == SystemColors.ControlLight ||
                    label.BackColor == Color.Transparent)
                {
                    label.BackColor = Color.Transparent;
                }
                break;

            case TextBoxBase text:
                StyleDarkInput(text);
                break;

            case ComboBox combo:
                StyleDarkCombo(combo);
                break;

            case Button button:
                if (button.BackColor != Gold)
                {
                    StyleSmallDarkButton(button);
                }
                break;

            case DataGridView grid:
                StyleGrid(grid);
                break;

            case TreeView tree:
                tree.BackColor = SlateDark;
                tree.ForeColor = OnDark;
                tree.LineColor = SlateLight;
                break;

            default:
                if (c.BackColor == SystemColors.Control ||
                    c.BackColor == SystemColors.ControlLight ||
                    c.BackColor == SystemColors.Window)
                {
                    c.BackColor = PanelBg;
                }
                if (c.ForeColor == SystemColors.ControlText ||
                    c.ForeColor == Color.Black)
                {
                    c.ForeColor = OnDark;
                }
                break;
        }

        foreach (Control child in c.Controls)
        {
            ApplyReadableTheme(child, isRoot: false);
        }
    }

    /// <summary>
    /// Dark <see cref="ListView"/>. Setting BackColor alone isn't enough - the column header is
    /// drawn by the OS and stays light, so the list has to be owner-drawn.
    /// </summary>
    public static void StyleListView(ListView list)
    {
        list.BackColor = SlateDark;
        list.ForeColor = OnDark;
        list.BorderStyle = BorderStyle.None;
        list.OwnerDraw = true;

        list.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Slate))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }
            using (var p = new Pen(LineSoft))
            {
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            }
            var r = e.Bounds;
            r.X += 6; r.Width -= 8;
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Eyebrow, r, OnDarkMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        // In Details view the row is painted by DrawSubItem; DrawItem just has to opt out.
        list.DrawItem += (_, e) => e.DrawDefault = false;

        list.DrawSubItem += (_, e) =>
        {
            var selected = e.Item?.Selected == true;
            using (var b = new SolidBrush(selected ? CardHi : SlateDark))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }
            if (selected)
            {
                using var p = new Pen(Gold);
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom - 1);
            }
            var fore = e.Item?.ForeColor is { } c && c != SystemColors.WindowText ? c : OnDark;
            var r = e.Bounds;
            r.X += 6; r.Width -= 8;
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Body, r, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    public static void StyleListBox(ListBox list)
    {
        list.BackColor = SlateDark;
        list.ForeColor = OnDark;
        list.BorderStyle = BorderStyle.None;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = Math.Max(26, TextRenderer.MeasureText("Ag", Body).Height + 10);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= list.Items.Count)
            {
                return;
            }

            var selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? CardHi : SlateDark);
            e.Graphics.FillRectangle(fill, e.Bounds);
            if (selected)
            {
                using var accent = new Pen(Gold);
                e.Graphics.DrawLine(accent, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom - 1);
            }

            var textBounds = e.Bounds;
            textBounds.X += 10;
            textBounds.Width -= 14;
            TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), Body, textBounds, OnDark,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = SlateDark;
        grid.GridColor = SlateLight;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;

        grid.DefaultCellStyle.BackColor = SlateDark;
        grid.DefaultCellStyle.ForeColor = OnDark;
        grid.DefaultCellStyle.SelectionBackColor = Tint(Gold);
        grid.DefaultCellStyle.SelectionForeColor = OnDark;

        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(35, 38, 45);
        grid.AlternatingRowsDefaultCellStyle.ForeColor = OnDark;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Tint(Gold);
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = OnDark;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Slate;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = OnDark;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Slate;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = OnDark;

        grid.RowHeadersDefaultCellStyle.BackColor = Slate;
        grid.RowHeadersDefaultCellStyle.ForeColor = OnDarkMuted;
    }

    // ---------------------------------------------------------------------
    // Design tokens + owner-draw helpers.
    // Additive: existing members above are untouched so current screens keep working
    // While controls are restyled one at a time. See docs/ui-2026-07-21.md.
    // ---------------------------------------------------------------------

    // Extra surfaces for depth (hover / elevated cards) on the slate ground.
    public static readonly Color Surface = Color.FromArgb(30, 33, 39);   // = SlateDark, semantic alias
    public static readonly Color CardHi = Color.FromArgb(52, 57, 67);    // card hover
    public static readonly Color LineSoft = Color.FromArgb(42, 46, 53);  // hairline separators
    public static readonly Color FrameLine = Color.FromArgb(74, 81, 93); // workspace and section boundaries

    // Semantic status colors (kept separate from the gold accent + category hues).
    public static readonly Color Good = Color.FromArgb(42, 206, 152);
    public static readonly Color Warn = Color.FromArgb(244, 176, 62);
    public static readonly Color Crit = Color.FromArgb(229, 83, 75);
    public static readonly Color Info = Color.FromArgb(96, 168, 226);

    // Spacing scale (px) - lay out on these, not magic numbers.
    public const int PadXs = 4, PadSm = 8, Pad = 12, PadLg = 16, PadXl = 24;
    // Corner radii.
    public const int Radius = 10, RadiusSm = 7, RadiusLg = 14;

    private const string MonoFamily = "Consolas";
    public static readonly Font Title = AppFonts.Condensed(15f, FontStyle.Bold);
    public static readonly Font Heading = AppFonts.Condensed(12f, FontStyle.Bold);
    public static readonly Font Body = AppFonts.Condensed(10f, FontStyle.Bold);
    public static readonly Font BodyStrong = AppFonts.Condensed(10f, FontStyle.Bold);
    public static readonly Font Caption = AppFonts.Condensed(9f, FontStyle.Bold);
    /// <summary>Uppercase section eyebrow - pair with letter-spacing drawn manually where needed.</summary>
    public static readonly Font Eyebrow = AppFonts.Condensed(8.75f, FontStyle.Bold);
    public static readonly Font Mono = new(MonoFamily, 9f);

    /// <summary>Builds a rounded-rectangle path (px radius). radius&lt;=0 yields a plain rectangle.</summary>
    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0 || d > r.Width || d > r.Height)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Fills a rounded card (optional 1px border) with anti-aliasing, restoring smoothing mode.</summary>
    public static void FillRoundedCard(Graphics g, Rectangle r, Color fill, Color? border = null, int radius = Radius)
    {
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundedRect(r, radius))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            if (border is Color bc)
            {
                using var pen = new Pen(bc);
                g.DrawPath(pen, path);
            }
        }
        g.SmoothingMode = prev;
    }

    /// <summary>Draws a category accent chip/dot commonly used on tiles and nav items.</summary>
    public static void DrawAccentDot(Graphics g, Point center, int radius, Color color)
    {
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(color);
        g.FillEllipse(b, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        g.SmoothingMode = prev;
    }
}
