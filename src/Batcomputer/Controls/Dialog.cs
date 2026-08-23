using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// The app's themed replacement for <see cref="MessageBox"/>. A severity rail instead of a system
/// icon, optional labelled fields for asset paths, an optional callout, and buttons named for the
/// action rather than "OK".
///
/// Use the static helpers for the common cases; use <see cref="Show"/> with a <see cref="Model"/>
/// when a dialog carries structured detail (e.g. the equipment compatibility report).
/// </summary>
public static class Dialog
{
    public enum Level { Info, Good, Warn, Crit }

    public sealed class Model
    {
        public string WindowTitle = "Batcomputer";
        public string Title = "";
        public string Subtitle = "";
        public string Message = "";
        public Level Severity = Level.Info;
        public List<(string Label, string Value)> Fields = new();
        public List<(string Text, Color? Dot)> Chips = new();
        public string CalloutTitle = "";
        public string CalloutDetail = "";
        public string PrimaryText = "OK";
        /// <summary>Null renders a single-button (acknowledge) dialog.</summary>
        public string? SecondaryText;
    }

    private static Color Accent(Level level) => level switch
    {
        Level.Crit => Theme.Crit,
        Level.Warn => Theme.Warn,
        Level.Good => Theme.Good,
        _ => Theme.Info,
    };

    // ---- helpers ------------------------------------------------------------
    public static void Info(IWin32Window? owner, string title, string message, string? windowTitle = null) =>
        Show(owner, new Model { Title = title, Message = message, Severity = Level.Info, WindowTitle = windowTitle ?? "Batcomputer" });

    public static void Success(IWin32Window? owner, string title, string message, string? windowTitle = null) =>
        Show(owner, new Model { Title = title, Message = message, Severity = Level.Good, WindowTitle = windowTitle ?? "Batcomputer" });

    public static void Warn(IWin32Window? owner, string title, string message, string? windowTitle = null) =>
        Show(owner, new Model { Title = title, Message = message, Severity = Level.Warn, WindowTitle = windowTitle ?? "Batcomputer" });

    public static void Error(IWin32Window? owner, string title, string message, string? windowTitle = null) =>
        Show(owner, new Model { Title = title, Message = message, Severity = Level.Crit, WindowTitle = windowTitle ?? "Batcomputer" });

    /// <summary>Asks a question. Returns true when the primary (gold) button is chosen.</summary>
    public static bool Confirm(IWin32Window? owner, string title, string message,
        string confirmText = "Continue", string cancelText = "Cancel", Level severity = Level.Warn,
        string? windowTitle = null) =>
        Show(owner, new Model
        {
            Title = title,
            Message = message,
            Severity = severity,
            PrimaryText = confirmText,
            SecondaryText = cancelText,
            WindowTitle = windowTitle ?? "Batcomputer",
        });

    // ---- the dialog ---------------------------------------------------------
    public static bool Show(IWin32Window? owner, Model model)
    {
        using var form = CreateForm(owner, model);
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>
    /// Builds a themed dialog without showing it. Keeping construction separate from the modal
    /// call lets the release UI audit render every dialog state and verify that its actions remain
    /// inside the visible footer at every supported DPI.
    /// </summary>
    internal static Form CreateForm(IWin32Window? owner, Model model)
    {
        const int W = 480, Pad = 18;
        var accent = Accent(model.Severity);

        var form = new AdaptiveDialogForm
        {
            Text = model.WindowTitle,
            AutoScaleMode = AutoScaleMode.Dpi,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
            Font = Theme.Body,
            ClientSize = new Size(W, 200),
        };

        var body = new Panel { Dock = DockStyle.Top, Width = W, BackColor = Theme.WindowBg };
        var y = Pad;

        // Header: severity rail + title (+ subtitle).
        var railTop = y;
        var title = new Label
        {
            Left = Pad + 12, Top = y, Width = W - Pad * 2 - 12, Height = 22,
            Text = model.Title,
            Font = AppFonts.Condensed(12f, FontStyle.Bold),
            ForeColor = Theme.OnDark,
            AutoEllipsis = true,
        };
        body.Controls.Add(title);
        y += 23;

        if (!string.IsNullOrWhiteSpace(model.Subtitle))
        {
            var sub = new Label
            {
                Left = Pad + 12, Top = y, Width = W - Pad * 2 - 12, Height = 17,
                Text = model.Subtitle, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted, AutoEllipsis = true,
            };
            body.Controls.Add(sub);
            y += 18;
        }

        var railBottom = y;
        var rail = new Panel { Left = Pad, Top = railTop + 2, Width = 3, Height = Math.Max(18, railBottom - railTop - 4), BackColor = Theme.WindowBg };
        rail.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(accent);
            using var p = Theme.RoundedRect(new Rectangle(0, 0, 3, rail.Height), 2);
            e.Graphics.FillPath(b, p);
        };
        body.Controls.Add(rail);
        y += 6;

        // Chips.
        if (model.Chips.Count > 0)
        {
            var flow = new FlowLayoutPanel
            {
                Left = Pad, Top = y, Width = W - Pad * 2, Height = 26,
                BackColor = Theme.WindowBg, WrapContents = true, AutoScroll = false,
            };
            foreach (var (text, dot) in model.Chips)
            {
                flow.Controls.Add(MakeChip(text, dot));
            }
            body.Controls.Add(flow);
            y += 30;
        }

        // Message paragraph.
        if (!string.IsNullOrWhiteSpace(model.Message))
        {
            var width = W - Pad * 2;
            var measured = TextRenderer.MeasureText(model.Message, Theme.Body,
                new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
            var msg = new Label
            {
                Left = Pad, Top = y, Width = width, Height = measured.Height + 4,
                Text = model.Message, Font = Theme.Body, ForeColor = Theme.OnDark,
            };
            body.Controls.Add(msg);
            y += msg.Height + 10;
        }

        // Labelled fields (asset paths etc.) - mono, middle-truncated, copyable via tooltip.
        if (model.Fields.Count > 0)
        {
            var tips = new ToolTip();
            Theme.StyleTooltip(tips);
            foreach (var (label, value) in model.Fields)
            {
                var l = new Label
                {
                    Left = Pad, Top = y, Width = 82, Height = 17,
                    Text = label, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
                };
                var v = new Label
                {
                    Left = Pad + 86, Top = y, Width = W - Pad * 2 - 86, Height = 17,
                    Text = value, Font = Theme.Mono, ForeColor = Theme.OnDark, AutoEllipsis = true,
                };
                tips.SetToolTip(v, value + "\n\nRight-click to copy path");
                var copyMenu = new ContextMenuStrip();
                copyMenu.Items.Add("Copy path", null, (_, _) =>
                {
                    try { Clipboard.SetText(value); }
                    catch { /* clipboard may be busy */ }
                });
                l.ContextMenuStrip = copyMenu;
                v.ContextMenuStrip = copyMenu;
                body.Controls.Add(l);
                body.Controls.Add(v);
                y += 19;
            }
            y += 8;
        }

        // Callout.
        if (!string.IsNullOrWhiteSpace(model.CalloutTitle) || !string.IsNullOrWhiteSpace(model.CalloutDetail))
        {
            var width = W - Pad * 2;
            var detailH = string.IsNullOrWhiteSpace(model.CalloutDetail) ? 0 :
                TextRenderer.MeasureText(model.CalloutDetail, Theme.Caption,
                    new Size(width - 22, int.MaxValue), TextFormatFlags.WordBreak).Height + 3;
            var card = new RoundedPanel
            {
                Left = Pad, Top = y, Width = width, Height = 26 + detailH,
                BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm,
            };
            card.Paint += (_, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var b = new SolidBrush(accent);
                using var p = Theme.RoundedRect(new Rectangle(1, 7, 3, Math.Max(6, card.Height - 14)), 2);
                e.Graphics.FillPath(b, p);
            };
            if (!string.IsNullOrWhiteSpace(model.CalloutTitle))
            {
                card.Controls.Add(new Label
                {
                    Left = 13, Top = 5, Width = width - 22, Height = 16,
                    Text = model.CalloutTitle, Font = Theme.BodyStrong, ForeColor = Theme.OnDark,
                    BackColor = Color.Transparent, AutoEllipsis = true,
                });
            }
            if (detailH > 0)
            {
                card.Controls.Add(new Label
                {
                    Left = 13, Top = 22, Width = width - 22, Height = detailH,
                    Text = model.CalloutDetail, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
                    BackColor = Color.Transparent,
                });
            }
            body.Controls.Add(card);
            y += card.Height + 10;
        }

        body.Height = y;

        // Footer. Keep button placement under a layout manager. Absolute coordinates calculated
        // before WinForms performs DPI autoscaling can place otherwise-valid buttons beyond the
        // final client edge (the former cause of the blank Delete texture footer).
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SlateDark };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        var footerActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(Pad, 11, Pad, 10),
            Margin = Padding.Empty,
            BackColor = Theme.SlateDark,
        };
        footer.Controls.Add(footerActions);

        var primary = new Button
        {
            Text = model.PrimaryText,
            Width = 0,
            Height = 32,
            DialogResult = DialogResult.OK,
            Margin = Padding.Empty,
        };
        Theme.StyleGoldButton(primary);
        primary.Width = Math.Max(96, TextRenderer.MeasureText(primary.Text, primary.Font).Width + 34);
        footerActions.Controls.Add(primary);

        Button? secondary = null;
        if (model.SecondaryText is not null)
        {
            secondary = new Button
            {
                Text = model.SecondaryText,
                Height = 32,
                DialogResult = DialogResult.Cancel,
                Margin = new Padding(8, 0, 0, 0),
            };
            Theme.StyleDarkButton(secondary);
            secondary.Width = Math.Max(88, TextRenderer.MeasureText(secondary.Text, secondary.Font).Width + 30);
            footerActions.Controls.Add(secondary);
        }

        var bodyHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            BackColor = Theme.WindowBg,
        };
        bodyHost.Controls.Add(body);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Theme.WindowBg,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
        root.Controls.Add(bodyHost, 0, 0);
        root.Controls.Add(footer, 0, 1);
        form.Controls.Add(root);
        form.AcceptButton = primary;
        if (secondary is not null) form.CancelButton = secondary;
        form.Load += (_, _) =>
        {
            // AutoScale runs when the form creates its handle. Fit against those final scaled
            // dimensions rather than the design-time pixel values; otherwise a short dialog can
            // incorrectly grow both scrollbars at 125%/150% DPI and hide its last field.
            form.PerformLayout();
            var workingArea = owner is Control ownerControl
                ? Screen.FromControl(ownerControl).WorkingArea
                : Screen.FromControl(form).WorkingArea;
            var nonClientHeight = Math.Max(0, form.Height - form.ClientSize.Height);
            var maximumClientHeight = Math.Max(260, workingArea.Height - nonClientHeight - 96);
            var desiredClientHeight = body.Height + footer.Height;
            var targetClientHeight = Math.Min(desiredClientHeight, maximumClientHeight);
            form.ClientSize = new Size(form.ClientSize.Width, targetClientHeight);
            bodyHost.AutoScroll = desiredClientHeight > targetClientHeight;
            bodyHost.AutoScrollMinSize = bodyHost.AutoScroll ? new Size(0, body.Height) : Size.Empty;
            body.Width = bodyHost.ClientSize.Width;
        };
        form.Shown += (_, _) => Theme.UseDarkTitleBar(form);

        return form;
    }

    private static Control MakeChip(string text, Color? dot)
    {
        var padLeft = dot is null ? 9 : 18;
        var w = TextRenderer.MeasureText(text, Theme.Caption).Width;
        var chip = new RoundedPanel
        {
            Height = 21, Width = w + padLeft + 9,
            Margin = new Padding(0, 1, 6, 1),
            BackColor = Theme.Slate, BorderColor = Theme.LineSoft, CornerRadius = 10,
        };
        chip.Controls.Add(new Label
        {
            Left = padLeft, Top = 3, Width = w + 2, Height = 15,
            Text = text, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted, BackColor = Color.Transparent,
        });
        if (dot is Color c)
        {
            chip.Controls.Add(new StatusDot { Left = 8, Top = 7, Width = 7, Height = 7, DotColor = c });
        }
        return chip;
    }
}
