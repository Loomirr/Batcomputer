using System.Diagnostics;

namespace Batcomputer;

/// <summary>Readable release receipts; paths are never shortened or used as shell commands.</summary>
internal sealed class ReleasePathsForm : AdaptiveForm
{
    internal ReleasePathsForm(Dialog.Model model)
    {
        Text = model.WindowTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, Math.Clamp(350 + model.Fields.Count * 120, 500, 740));
        MinimumSize = new Size(620, 500);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = ReleaseUi.Body;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var accent = model.Severity switch { Dialog.Level.Good => Theme.Good, Dialog.Level.Warn => Theme.Warn, Dialog.Level.Crit => Theme.Crit, _ => Theme.Info };
        AddText(header, model.Severity == Dialog.Level.Good ? "RELEASE COMPLETE" : "RELEASE STATUS", ReleaseUi.Small, accent);
        AddText(header, model.Title, ReleaseUi.Title, Theme.OnDark);
        AddText(header, model.Subtitle, ReleaseUi.Body, Theme.OnDarkMuted);
        AddText(header, model.Message, ReleaseUi.Body, Theme.OnDarkMuted);
        AddText(header, string.Join(" · ", model.Chips.Select(c => c.Text)), ReleaseUi.Small, accent);
        if (!string.IsNullOrWhiteSpace(model.CalloutTitle) || !string.IsNullOrWhiteSpace(model.CalloutDetail))
        {
            var summary = new RoundedPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.CardBg,
                CornerRadius = 10, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 6, 0, 2) };
            var summaryText = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, BackColor = Color.Transparent };
            summaryText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddText(summaryText, model.CalloutTitle, ReleaseUi.Emphasis, accent);
            AddText(summaryText, model.CalloutDetail, ReleaseUi.Small, Theme.OnDarkMuted);
            summary.Controls.Add(summaryText);
            header.Controls.Add(summary);
        }
        layout.Controls.Add(header, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 16, 0, 16) };
        var paths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 4, 0) };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, value) in model.Fields)
        {
            var card = new RoundedPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14, 10, 14, 12),
                BackColor = Theme.CardBg, BorderColor = Theme.LineSoft, CornerRadius = 10, Margin = new Padding(0, 0, 0, 12) };
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, Font = ReleaseUi.Emphasis, ForeColor = Theme.OnDark,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 8, 0) };
            row.Controls.Add(caption, 0, 0);
            var text = new TextBox { Text = value, ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.None,
                Dock = DockStyle.Top, Height = 40, Font = Theme.Mono, Margin = new Padding(0, 10, 0, 0),
                BackColor = Theme.CardBg, ForeColor = Theme.OnDarkMuted, BorderStyle = BorderStyle.None, AccessibleName = label };
            text.SizeChanged += (_, _) =>
            {
                var measured = TextRenderer.MeasureText(value, text.Font, new Size(Math.Max(80, text.Width - 24), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 6;
                var height = Math.Clamp(measured, 26, 100);
                if (text.Height != height) text.Height = height;
                var bars = measured > 100 ? ScrollBars.Vertical : ScrollBars.None;
                if (text.ScrollBars != bars) text.ScrollBars = bars;
            };
            row.Controls.Add(text, 0, 1);
            row.SetColumnSpan(text, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent };
            var copy = ReleaseUi.Button("Copy path", 92);
            copy.Click += (_, _) => Copy(value, copy);
            var open = ReleaseUi.Button("Open folder", 108);
            open.Enabled = FolderFor(value) is not null;
            open.Click += (_, _) =>
            {
                try
                {
                    var folder = FolderFor(value) ?? throw new DirectoryNotFoundException("This location no longer exists.");
                    Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                }
                catch (Exception ex) { Dialog.Warn(this, "Could not open folder", ex.Message); }
            };
            actions.Controls.Add(copy);
            actions.Controls.Add(open);
            row.Controls.Add(actions, 1, 0);
            card.Controls.Add(row);
            paths.Controls.Add(card);
        }
        scroll.Controls.Add(paths);
        layout.Controls.Add(scroll, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var done = ReleaseUi.Button(model.PrimaryText, 110, primary: true);
        done.DialogResult = DialogResult.OK;
        var copyAll = ReleaseUi.Button("Copy all paths", 140);
        copyAll.Click += (_, _) => Copy(string.Join(Environment.NewLine, model.Fields.Select(f => f.Label + ": " + f.Value)), copyAll);
        footer.Controls.Add(done);
        footer.Controls.Add(copyAll);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        AcceptButton = done;
        CancelButton = done;
        Shown += (_, _) => Theme.UseDarkTitleBar(this);
    }

    internal static string? FolderFor(string value)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value)) return null;
            if (Directory.Exists(value)) return Path.GetFullPath(value);
            if (File.Exists(value)) return Path.GetDirectoryName(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }

    private static void Copy(string text, Button button)
    {
        try { Clipboard.SetText(text); button.Text = "Copied"; }
        catch { button.Text = "Copy failed; retry"; }
    }

    private static void AddText(TableLayoutPanel host, string text, Font font, Color color)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        host.Controls.Add(new Label { Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 6) });
    }
}

internal static class ReleaseUi
{
    internal static readonly Font Body = AppFonts.Ui(10f);
    internal static readonly Font Small = AppFonts.Ui(9f);
    internal static readonly Font Emphasis = AppFonts.Ui(10f, FontStyle.Bold);
    internal static readonly Font Title = AppFonts.Ui(19f, FontStyle.Bold);

    internal static Button Button(string text, int width, bool primary = false)
    {
        var button = new Button { Text = text, Width = width, Height = 34, Font = Small, Margin = new Padding(6, 0, 0, 0) };
        if (primary) Theme.StyleGoldButton(button); else Theme.StyleDarkButton(button);
        Theme.RoundControl(button, 7);
        return button;
    }
}
