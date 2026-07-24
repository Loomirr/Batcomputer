using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Batcomputer;

/// <summary>
/// The "Your Character" figure: a minifig built from the part silhouettes in Assets/Parts.
/// Each PNG is a black shape centred on its own canvas, so they are cropped to their opaque
/// bounds and positioned here. Parts are recoloured per state and clickable;
/// <see cref="RegionActivated"/> fires the region key so MainForm can select the matching slot.
///
/// Regions follow how the game splits a character: Body is CharacterMesh0 (torso, arms, hands,
/// legs, feet), with Head, Cape, Belt and Shoulders as separate components. Face has no geometry
/// of its own - it is a chip beside the head that acts as the face drop target.
/// </summary>
public sealed class MinifigDiagram : Control
{
    public enum RegionState { Absent, Present, Customized }

    /// <summary>Canonical region keys. MainForm classifies component names onto these.</summary>
    public static readonly string[] Regions =
        { "Head", "Face", "Cape", "Body", "Belt", "Shoulders", "Glider", "Equipment" };

    /// <summary>Accessory slots shown in the tray under the figure (drag targets, not body parts).</summary>
    public static readonly string[] GearRegions = { "Glider", "Equipment" };

    private readonly Dictionary<string, RegionState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RectangleF> _hit = new(StringComparer.OrdinalIgnoreCase);
    private string? _selected;
    private string? _hovered;
    private RectangleF _figure;
    private float _trayTop, _readoutTop;

    /// <summary>Left-click on a region (region key).</summary>
    public event Action<string>? RegionActivated;

    /// <summary>Right-click on a region - host shows the slot actions menu (region key).</summary>
    public event Action<string>? RegionContextRequested;

    /// <summary>One material slot on a component, as shown in the materials panel.</summary>
    public sealed class SlotEntry
    {
        public required string Component;
        public required int Slot;
        public string Material = "";
        public bool Overridden;
    }

    /// <summary>What the materials panel shows for a region.</summary>
    public sealed class RegionInfo
    {
        public string Title = "";
        /// <summary>One-line summary - used for the gear slots and for "not on this base".</summary>
        public string Detail = "";
        public string Mesh = "";
        public IReadOnlyList<SlotEntry> Slots = Array.Empty<SlotEntry>();
    }

    /// <summary>Supplies the materials panel content for a region. Set by the host.</summary>
    public Func<string, RegionInfo>? RegionDescriber { get; set; }

    /// <summary>Raised when a material slot row is clicked (component, slot).</summary>
    public event Action<string, int>? SlotActivated;

    public MinifigDiagram()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.PanelBg;
        Cursor = Cursors.Hand;
    }

    /// <summary>Replaces per-region state, the material-slot counts shown as badges, and the selection.</summary>
    public void SetStates(IReadOnlyDictionary<string, RegionState> states,
                          IReadOnlyDictionary<string, int> slotCounts,
                          string? selected)
    {
        _states.Clear();
        foreach (var kv in states) _states[kv.Key] = kv.Value;
        _slotCounts.Clear();
        foreach (var kv in slotCounts) _slotCounts[kv.Key] = kv.Value;
        _selected = selected;
        Invalidate();
    }

    private readonly Dictionary<string, int> _slotCounts = new(StringComparer.OrdinalIgnoreCase);

    public void SetSelected(string? key)
    {
        if (!string.Equals(_selected, key, StringComparison.OrdinalIgnoreCase))
        {
            _selected = key;
            Invalidate();
        }
    }

    private string? _dropHint;

    /// <summary>
    /// Shows a drop cue over the whole panel while a part is being dragged. A part applies to its
    /// own native slot wherever it lands, so the target is the panel, not a specific region.
    /// </summary>
    public void SetPartDropHint(string? partName)
    {
        if (_dropHint != partName)
        {
            _dropHint = partName;
            Invalidate();
        }
    }

    private void DrawDropHint(Graphics g)
    {
        if (_dropHint is null) return;

        var r = new Rectangle(3, 3, Width - 7, Height - 7);
        using (var pen = new Pen(Theme.Gold, 2) { DashStyle = DashStyle.Dash })
        using (var path = Theme.RoundedRect(r, Theme.Radius))
        {
            g.DrawPath(pen, path);
        }

        var text = $"Drop to add {_dropHint}";
        var size = TextRenderer.MeasureText(g, text, Theme.BodyStrong);
        var pill = new Rectangle((Width - size.Width - 26) / 2, 10, size.Width + 26, 26);
        Theme.FillRoundedCard(g, pill, Theme.Gold, Theme.GoldDim, 13);
        TextRenderer.DrawText(g, text, Theme.BodyStrong, pill, Theme.SlateDark,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Highlights a region from outside (used to light up the drop target while dragging).</summary>
    public void SetHoverRegion(string? region)
    {
        if (!string.Equals(region, _hovered, StringComparison.OrdinalIgnoreCase))
        {
            _hovered = region;
            Invalidate();
        }
    }

    private RegionState State(string key) => _states.TryGetValue(key, out var s) ? s : RegionState.Absent;

    // ---- part art -----------------------------------------------------------
    private sealed class PartArt
    {
        public required Bitmap Bmp;
        public float Aspect;
    }

    private static readonly object ArtLock = new();
    private static Dictionary<string, PartArt?>? _art;

    private static readonly string[] ArtFiles =
    {
        "Head", "Torso", "LeftArm", "RightArm", "Hips", "Legs", "Feet",
        "Cape", "Belt", "Shoulders", "Glider", "Equipment"
    };

    /// <summary>
    /// Part placement. X/Y/W are ALL fractions of the figure WIDTH; each part's height comes from its
    /// own art aspect, so pieces keep their proportions. Tuned so the parts read as connected but
    /// minorly separated. Drawn in order: cape furthest back, then limbs/lower body, torso, the
    /// add-on pieces (shoulders/belt) over it, and the head last.
    ///
    /// <c>Conditional</c> parts are ONLY drawn when the suit actually has that component - a suit with
    /// no cape/belt/pauldrons shows a plain minifig instead of ghosted extras.
    /// </summary>
    private static readonly (string File, string Region, float X, float Y, float W, bool Conditional)[] Placements =
    {
        ("Cape",      "Cape",      0.160f, 0.301f, 0.680f, true),
        // Arms sit far enough in that their shoulders tuck BEHIND the torso edge - that overlap is
        // what makes them read as attached rather than floating beside the body.
        ("LeftArm",   "Body",      0.130f, 0.341f, 0.175f, false),
        ("RightArm",  "Body",      0.695f, 0.341f, 0.175f, false),
        ("Feet",      "Body",      0.270f, 1.125f, 0.460f, false),
        ("Legs",      "Body",      0.280f, 0.810f, 0.440f, false),
        ("Hips",      "Body",      0.290f, 0.740f, 0.420f, false),
        ("Torso",     "Body",      0.270f, 0.321f, 0.460f, false),
        // The shoulders art is a yoke: a thin collar bar with pauldron pads at each END, so it has
        // to be wide enough that those pads land on the arm joints rather than mid-chest.
        ("Shoulders", "Shoulders", 0.150f, 0.305f, 0.700f, true),
        ("Belt",      "Belt",      0.250f, 0.718f, 0.500f, true),
        ("Head",      "Head",      0.3625f, 0.000f, 0.275f, false),
    };

    /// <summary>Total assembled height, in the same figure-width units as <see cref="Placements"/>.</summary>
    private const float TotalHeightUnits = 1.23f;

    /// <summary>Parts that light up together - Body is one mesh (CharacterMesh0), so it lights all of them.</summary>
    private static IEnumerable<string> HighlightParts(string region) => region.ToLowerInvariant() switch
    {
        "body" => new[] { "Torso", "LeftArm", "RightArm", "Legs", "Feet", "Hips" },
        "head" or "face" => new[] { "Head" },
        "cape" => new[] { "Cape" },
        "belt" => new[] { "Belt" },
        "shoulders" => new[] { "Shoulders" },
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// Regions that carry a material-slot count badge, and where on the part it sits.
    /// X/Y are fractions of the anchor part's rect - 0.5,0.5 is dead centre, which is where the
    /// badge belongs. The cape is the exception: its centre is hidden behind the torso, so its badge
    /// drops to the visible skirt.
    /// </summary>
    private static readonly (string Region, string AnchorPart, float FX, float FY)[] Badges =
    {
        ("Head",      "Head",      0.5f, 0.30f), // forehead, above the eyes — dead centre covers the face
        ("Body",      "Torso",     0.5f, 0.5f),
        ("Shoulders", "Shoulders", 0.90f, 0.55f), // on the right pauldron pad — dead centre is just the thin collar bar
        ("Belt",      "Belt",      0.5f, 0.5f),
        ("Cape",      "Cape",      0.84f, 0.90f), // the flare beside the legs — the only part of the cape not hidden by the body
        ("Face",      "Face",      1.0f, 0.5f), // just outside the chip, so it doesn't cover the label
    };

    /// <summary>
    /// True when the part silhouettes loaded. Assets/ is not kept in the repo, so a build made
    /// without it has no figure to draw and the host falls back to the slot list.
    /// </summary>
    public static bool HasArt => Art().Values.Any(a => a is not null);

    private static Dictionary<string, PartArt?> Art()
    {
        lock (ArtLock)
        {
            if (_art is not null) return _art;
            _art = new Dictionary<string, PartArt?>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ArtFiles)
            {
                _art[file] = LoadCropped($"Parts/{file}.png");
            }
            return _art;
        }
    }

    /// <param name="assetPath">Logical asset path, e.g. <c>"Parts/Head.png"</c>.</param>
    private static PartArt? LoadCropped(string assetPath)
    {
        try
        {
            using var src = EmbeddedAssets.Load(assetPath);
            if (src is null) return null;
            var box = OpaqueBounds(src);
            if (box.Width <= 0 || box.Height <= 0) return null;

            const int MaxEdge = 384;
            var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(box.Width, box.Height));
            var w = Math.Max(1, (int)Math.Round(box.Width * scale));
            var h = Math.Max(1, (int)Math.Round(box.Height * scale));

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), box, GraphicsUnit.Pixel);
            }
            return new PartArt { Bmp = bmp, Aspect = (float)w / h };
        }
        catch
        {
            return null;
        }
    }

    private static Rectangle OpaqueBounds(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData? data = null;
        try
        {
            data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var stride = data.Stride;
            var bytes = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            const int Step = 2;
            for (var y = 0; y < bmp.Height; y += Step)
            {
                var row = y * stride;
                for (var x = 0; x < bmp.Width; x += Step)
                {
                    if (bytes[row + x * 4 + 3] > 20)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return Rectangle.Empty;
            minX = Math.Max(0, minX - Step); minY = Math.Max(0, minY - Step);
            maxX = Math.Min(bmp.Width - 1, maxX + Step); maxY = Math.Min(bmp.Height - 1, maxY + Step);
            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        catch
        {
            return rect;
        }
        finally
        {
            if (data is not null) { try { bmp.UnlockBits(data); } catch { } }
        }
    }

    // ---- layout -------------------------------------------------------------
    private const float Pad = 10f;
    private const float GearRowH = 46f, GearGap = 6f, TrayGap = 12f, MaxTrayW = 300f;
    private const float FigureWidthFraction = 0.86f;
    private const int MaxReadoutLines = 6;
    private const float LineH = 15f;

    private static float TrayHeight => GearRegions.Length * GearRowH + (GearRegions.Length - 1) * GearGap;

    /// <summary>
    /// Figure + tray + readout lay out as ONE block so no void opens up between them. The figure is
    /// as large as the panel width allows (its height follows its width), and the block sits high in
    /// the panel rather than dead-centre.
    /// </summary>
    private void LayoutFigure(float readoutH)
    {
        _hit.Clear();
        var availW = Width - Pad * 2;
        var availH = Height - Pad * 2;
        if (availW <= 8 || availH <= 8) { _figure = RectangleF.Empty; return; }

        var extras = TrayGap + TrayHeight + 6f + readoutH;
        var fw = Math.Min(availW * FigureWidthFraction,
                          Math.Max(40f, (availH - extras) / TotalHeightUnits));
        var fh = fw * TotalHeightUnits;

        // 0.40 biases the block above centre so the character sits high in the panel.
        var top = Pad + Math.Max(0f, (availH - (fh + extras)) * 0.40f);
        _figure = new RectangleF((Width - fw) / 2f, top, fw, fh);
        _trayTop = _figure.Bottom + TrayGap;
        _readoutTop = _trayTop + TrayHeight + 6f;

        var art = Art();
        foreach (var (file, region, x, y, w, conditional) in Placements)
        {
            if (!art.TryGetValue(file, out var a) || a is null) continue;
            if (conditional && State(region) == RegionState.Absent) continue; // suit doesn't have it
            var dw = w * fw;
            _hit[file] = new RectangleF(_figure.X + x * fw, _figure.Y + y * fw, dw, dw / a.Aspect);
        }

        // Face chip: a labelled drop target beside the head (the head art itself is the Head slot).
        if (_hit.TryGetValue("Head", out var head))
        {
            var size = TextRenderer.MeasureText("Face", Theme.Caption);
            var chipW = size.Width + 16;
            var chipH = 20f;
            var chipX = Math.Min(Width - Pad - chipW, head.Right + 26f);
            _hit["Face"] = new RectangleF(chipX, head.Top + head.Height * 0.52f - chipH / 2f, chipW, chipH);
        }

        var trayW = Math.Min(availW, MaxTrayW);
        var trayX = (Width - trayW) / 2f;
        var trayY = _trayTop;
        foreach (var region in GearRegions)
        {
            _hit[region] = new RectangleF(trayX, trayY, trayW, GearRowH);
            trayY += GearRowH + GearGap;
        }
    }

    // ---- paint --------------------------------------------------------------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Selection (not hover) drives the highlight and the materials panel.
        var active = _selected;
        RegionInfo? info = null;
        if (active is not null)
        {
            info = RegionDescriber?.Invoke(active) ?? new RegionInfo { Title = active };
        }
        var readoutH = MaterialsPanelHeight(info);

        LayoutFigure(readoutH);
        if (_figure.Width <= 0) return;

        var art = Art();
        var drewAny = false;
        foreach (var (file, region, _, _, _, _) in Placements)
        {
            if (!_hit.TryGetValue(file, out var dest)) continue;
            if (!art.TryGetValue(file, out var a) || a is null) continue;

            var isActive = active is not null && HighlightParts(active).Contains(file, StringComparer.OrdinalIgnoreCase);
            var (color, alpha) = PartColor(file, State(region), isActive);
            DrawRecolored(g, a.Bmp, dest, color, alpha);
            drewAny = true;
        }

        if (!drewAny)
        {
            TextRenderer.DrawText(g, "Part art not found\n(Assets/Parts)", Theme.Caption,
                new Rectangle(0, 0, Width, Height), Theme.OnDarkMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        DrawBadges(g);
        DrawFaceChip(g);
        DrawGearTray(g);
        DrawMaterialsPanel(g, active, info);
        DrawDropHint(g);
    }

    /// <summary>
    /// Only the hovered/selected part is coloured - everything else stays a neutral silhouette.
    /// A whole figure tinted gold (every part customized) was unreadable, and the per-part
    /// "is it customized" signal now lives on the badges instead.
    /// </summary>
    private static (Color Color, float Alpha) PartColor(string file, RegionState state, bool isActive)
    {
        if (isActive)
        {
            return (Theme.Gold, 1f);
        }

        if (state == RegionState.Absent)
        {
            return (Theme.Blend(Theme.SlateLight, Theme.PanelBg, 0.55), 0.55f);
        }

        // The cape sits behind the body, so it's shaded darker as a depth cue.
        var isCape = file.Equals("Cape", StringComparison.OrdinalIgnoreCase);
        return (isCape ? Theme.Blend(Theme.SlateLight, Theme.PanelBg, 0.62) : Theme.SlateLight, 1f);
    }

    /// <summary>Draws a black silhouette recoloured to <paramref name="color"/> (alpha preserved).</summary>
    private static void DrawRecolored(Graphics g, Bitmap bmp, RectangleF dest, Color color, float alpha)
    {
        var matrix = new ColorMatrix(new[]
        {
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, alpha, 0 },
            new[] { color.R / 255f, color.G / 255f, color.B / 255f, 0, 1 },
        });
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);
        g.DrawImage(bmp, Rectangle.Round(dest), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
    }

    // ---- badges -------------------------------------------------------------
    private static readonly Font BadgeFont = new("Segoe UI", 7.5f, FontStyle.Bold);

    /// <summary>
    /// Small material-slot-count badges on the figure. These replace the old callout labels - the
    /// figure reads cleaner, and the count is the information the labels were not carrying.
    /// </summary>
    private void DrawBadges(Graphics g)
    {
        const float d = 19f;
        var active = _hovered ?? _selected;
        foreach (var (region, anchorPart, fx, fy) in Badges)
        {
            if (!_hit.TryGetValue(anchorPart, out var r)) continue;
            if (!_slotCounts.TryGetValue(region, out var count) || count <= 0) continue;

            var state = State(region);
            if (state == RegionState.Absent) continue;

            var isActive = string.Equals(active, region, StringComparison.OrdinalIgnoreCase);
            var customized = state == RegionState.Customized;
            var fill = customized ? Theme.Gold : Theme.Slate;
            var text = customized ? Theme.SlateDark : Theme.OnDark;
            var border = isActive ? Theme.OnDark : customized ? Theme.GoldDim : Theme.SlateLight;

            var cx = r.Left + r.Width * fx + (fx >= 1f ? d * 0.75f : 0f);
            var cy = r.Top + r.Height * fy;
            var box = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);

            using (var b = new SolidBrush(fill)) g.FillEllipse(b, box);
            using (var p = new Pen(border)) g.DrawEllipse(p, box);
            TextRenderer.DrawText(g, count.ToString(), BadgeFont, Rectangle.Round(box), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>The Face chip - a boxed label beside the head that is the face drop target.</summary>
    private void DrawFaceChip(Graphics g)
    {
        if (!_hit.TryGetValue("Face", out var chip) || !_hit.TryGetValue("Head", out var head)) return;

        var state = State("Face");
        var active = _hovered ?? _selected;
        var isActive = string.Equals(active, "Face", StringComparison.OrdinalIgnoreCase);
        var set = state == RegionState.Customized;
        var color = isActive ? Theme.OnDark : set ? Theme.Gold : Theme.OnDarkMuted;

        // Leader from the face area of the head across to the chip.
        var anchor = new PointF(head.Right - head.Width * 0.12f, chip.Top + chip.Height / 2f);
        using (var pen = new Pen(Theme.Blend(color, Theme.PanelBg, 0.55f)))
        {
            g.DrawLine(pen, anchor.X, anchor.Y, chip.Left, anchor.Y);
        }
        using (var b = new SolidBrush(color))
        {
            g.FillEllipse(b, anchor.X - 2f, anchor.Y - 2f, 4, 4);
        }

        var rect = Rectangle.Round(chip);
        Theme.FillRoundedCard(g, rect, isActive ? Theme.CardHi : Theme.CardBg, null, 6);
        using (var pen = new Pen(set || isActive ? Theme.Gold : Theme.LineSoft))
        {
            if (!set && !isActive) pen.DashStyle = DashStyle.Dash; // empty = "drop a face here"
            using var path = Theme.RoundedRect(rect, 6);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, "Face", Theme.Caption, rect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // ---- gear tray ----------------------------------------------------------
    private void DrawGearTray(Graphics g)
    {
        var art = Art();
        var active = _hovered ?? _selected;
        foreach (var region in GearRegions)
        {
            if (!_hit.TryGetValue(region, out var r)) continue;

            var state = State(region);
            var isActive = string.Equals(active, region, StringComparison.OrdinalIgnoreCase);
            var set = state == RegionState.Customized;

            var fill = isActive ? Theme.CardHi : Theme.CardBg;
            var border = set || isActive ? Theme.Gold : Theme.LineSoft;

            var rect = Rectangle.Round(r);
            Theme.FillRoundedCard(g, rect, fill, null, Theme.RadiusSm);
            using (var pen = new Pen(border))
            {
                if (!set && !isActive) pen.DashStyle = DashStyle.Dash;
                using var path = Theme.RoundedRect(rect, Theme.RadiusSm);
                g.DrawPath(pen, path);
            }

            if (art.TryGetValue(region, out var a) && a is not null)
            {
                const float boxH = 28f;
                var rotate = region.Equals("Equipment", StringComparison.OrdinalIgnoreCase);
                var iw = (rotate ? 26f : boxH) * a.Aspect;
                var iconColor = set || isActive ? Theme.Gold : Theme.OnDarkMuted;
                var cx = r.Left + 26f;
                var cy = r.Top + r.Height / 2f;
                var dest = new RectangleF(cx - iw / 2f, cy - boxH / 2f, iw, boxH);

                var saved = g.Save();
                if (rotate)
                {
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(-40f);
                    g.TranslateTransform(-cx, -cy);
                }
                DrawRecolored(g, a.Bmp, dest, iconColor, 1f);
                g.Restore(saved);
            }

            var gearInfo = RegionDescriber?.Invoke(region);
            var detail = string.IsNullOrEmpty(gearInfo?.Detail) ? "drag here" : gearInfo!.Detail;
            var textX = (int)r.Left + 48;
            var textW = (int)r.Width - 56;
            TextRenderer.DrawText(g, region, Theme.BodyStrong,
                new Rectangle(textX, (int)r.Top + 7, textW, 16),
                set || isActive ? Theme.Gold : Theme.OnDark,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, detail, Theme.Caption,
                new Rectangle(textX, (int)r.Top + 24, textW, 15), Theme.OnDarkMuted,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // ---- materials panel ----------------------------------------------------
    private const float SlotRowH = 24f, SlotRowGap = 3f, HintH = 16f;

    /// <summary>Slot rows hit-tested for clicks and drops, in draw order.</summary>
    private readonly List<(RectangleF Rect, string Component, int Slot)> _slotHit = new();
    private (string Component, int Slot)? _hoveredSlot;

    private float MaterialsPanelHeight(RegionInfo? info)
    {
        if (info is null) return 34f;
        var h = 20f;                                        // title
        if (!string.IsNullOrEmpty(info.Mesh)) h += LineH;   // mesh line
        var shown = Math.Min(info.Slots.Count, MaxReadoutLines);
        if (shown > 0) h += 4f + shown * (SlotRowH + SlotRowGap);
        if (info.Slots.Count > MaxReadoutLines) h += LineH;
        if (!string.IsNullOrEmpty(info.Detail)) h += LineH;
        h += HintH;
        return h;
    }

    /// <summary>
    /// The materials panel under the tray: the selected part's mesh plus a row per material slot.
    /// Each row is its own drop target - dropping a material on a row applies it to that slot,
    /// dropping on the figure part applies it to every slot on the part.
    /// </summary>
    private void DrawMaterialsPanel(Graphics g, string? active, RegionInfo? info)
    {
        _slotHit.Clear();
        var top = (int)_readoutTop;

        if (active is null || info is null)
        {
            TextRenderer.DrawText(g, "Click a part to see its materials", Theme.Caption,
                new Rectangle(4, top, Width - 8, 30), Theme.OnDarkMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        var customized = State(active) == RegionState.Customized;
        TextRenderer.DrawText(g, info.Title, Theme.BodyStrong,
            new Rectangle(6, top, Width - 12, 18), customized ? Theme.Gold : Theme.OnDark,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);

        var y = top + 20f;

        if (!string.IsNullOrEmpty(info.Mesh))
        {
            TextRenderer.DrawText(g, info.Mesh, Theme.Caption,
                new Rectangle(6, (int)y, Width - 12, (int)LineH), Theme.OnDarkMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            y += LineH;
        }

        if (!string.IsNullOrEmpty(info.Detail))
        {
            TextRenderer.DrawText(g, info.Detail, Theme.Caption,
                new Rectangle(6, (int)y, Width - 12, (int)LineH), Theme.OnDarkMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            y += LineH;
        }

        var shown = Math.Min(info.Slots.Count, MaxReadoutLines);
        if (shown > 0)
        {
            y += 4f;
            var rowW = Math.Min(Width - Pad * 2, MaxTrayW);
            var rowX = (Width - rowW) / 2f;

            for (var i = 0; i < shown; i++)
            {
                var entry = info.Slots[i];
                var rect = new RectangleF(rowX, y, rowW, SlotRowH);
                _slotHit.Add((rect, entry.Component, entry.Slot));

                var hovered = _hoveredSlot is { } hs
                              && hs.Slot == entry.Slot
                              && hs.Component.Equals(entry.Component, StringComparison.OrdinalIgnoreCase);
                var has = !string.IsNullOrWhiteSpace(entry.Material);

                var r = Rectangle.Round(rect);
                Theme.FillRoundedCard(g, r, hovered ? Theme.CardHi : Theme.CardBg, null, 6);
                using (var pen = new Pen(hovered ? Theme.Gold : entry.Overridden ? Theme.GoldDim : Theme.LineSoft))
                {
                    if (!has) pen.DashStyle = DashStyle.Dash; // empty slot reads as "drop here"
                    using var path = Theme.RoundedRect(r, 6);
                    g.DrawPath(pen, path);
                }

                // Slot index chip.
                TextRenderer.DrawText(g, entry.Slot.ToString(), BadgeFont,
                    new Rectangle(r.Left + 6, r.Top, 18, r.Height),
                    entry.Overridden ? Theme.Gold : Theme.OnDarkMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                var label = has ? entry.Material : "empty — drop a material";
                TextRenderer.DrawText(g, label, Theme.Caption,
                    new Rectangle(r.Left + 26, r.Top, r.Width - 44, r.Height),
                    has ? Theme.OnDark : Theme.OnDarkMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                if (entry.Overridden)
                {
                    TextRenderer.DrawText(g, "✎", Theme.Caption,
                        new Rectangle(r.Right - 18, r.Top, 14, r.Height), Theme.Gold,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                y += SlotRowH + SlotRowGap;
            }

            if (info.Slots.Count > MaxReadoutLines)
            {
                TextRenderer.DrawText(g, $"+{info.Slots.Count - MaxReadoutLines} more slot(s)", Theme.Caption,
                    new Rectangle(6, (int)y, Width - 12, (int)LineH), Theme.OnDarkMuted,
                    TextFormatFlags.HorizontalCenter);
                y += LineH;
            }
        }

        TextRenderer.DrawText(g, shown > 0 ? "drop on a slot, or on the part for all slots" : "no material slots",
            Theme.Caption, new Rectangle(6, (int)y, Width - 12, (int)HintH), Theme.OnDarkMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>The material slot row under a client-space point, or null.</summary>
    public (string Component, int Slot)? SlotAtPoint(Point p)
    {
        foreach (var (rect, component, slot) in _slotHit)
        {
            if (rect.Contains(p)) return (component, slot);
        }
        return null;
    }

    /// <summary>Highlights a slot row from outside (drag hover).</summary>
    public void SetHoverSlot((string Component, int Slot)? slot)
    {
        if (_hoveredSlot?.Component != slot?.Component || _hoveredSlot?.Slot != slot?.Slot)
        {
            _hoveredSlot = slot;
            Invalidate();
        }
    }

    // ---- interaction --------------------------------------------------------
    /// <summary>The region under a client-space point, or null. Public so the host can route drops.</summary>
    public string? RegionAtPoint(Point p) => RegionAt(p);

    private string? RegionAt(Point p)
    {
        foreach (var region in GearRegions)
        {
            if (_hit.TryGetValue(region, out var gr) && gr.Contains(p)) return region;
        }
        if (_hit.TryGetValue("Face", out var face) && face.Contains(p)) return "Face";
        if (_hit.TryGetValue("Head", out var head) && head.Contains(p)) return "Head";
        // Add-on pieces sit on top of the body, so they win the hit before Body.
        if (_hit.TryGetValue("Shoulders", out var sh) && sh.Contains(p)) return "Shoulders";
        if (_hit.TryGetValue("Belt", out var belt) && belt.Contains(p)) return "Belt";
        foreach (var part in new[] { "Torso", "LeftArm", "RightArm", "Hips", "Legs", "Feet" })
        {
            if (_hit.TryGetValue(part, out var br) && br.Contains(p)) return "Body";
        }
        if (_hit.TryGetValue("Cape", out var cape) && cape.Contains(p)) return "Cape";
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHoverRegion(RegionAt(e.Location));
        SetHoverSlot(SlotAtPoint(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverRegion(null);
        SetHoverSlot(null);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        // A material slot row takes precedence over the region underneath it.
        if (SlotAtPoint(e.Location) is { } s && e.Button == MouseButtons.Left)
        {
            SlotActivated?.Invoke(s.Component, s.Slot);
            return;
        }

        var hit = RegionAt(e.Location);
        if (hit is null) return;
        if (e.Button == MouseButtons.Right) RegionContextRequested?.Invoke(hit);
        else if (e.Button == MouseButtons.Left) RegionActivated?.Invoke(hit);
    }
}
