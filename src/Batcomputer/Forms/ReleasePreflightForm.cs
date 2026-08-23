namespace Batcomputer;

/// <summary>
/// In-app release readiness review. Findings stay with the current action instead
/// of creating report files that immediately become stale after the next build.
/// </summary>
public sealed class ReleasePreflightForm : AdaptiveForm
{
    private const int WidthPx = 760;
    private const int HeightPx = 620;

    private ReleasePreflightForm(string modName, ModReleaseValidationService.Result result)
    {
        Text = "Batcomputer - Build check";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        ClientSize = new Size(WidthPx, HeightPx);
        MinimumSize = new Size(680, 520);

        var passed = result.Passed;
        var headerAccent = passed ? Theme.Good : Theme.Crit;

        var done = new Button
        {
            Text = "Done",
            Width = 98,
            DialogResult = DialogResult.OK,
        };
        Theme.StyleGoldButton(done);
        Theme.RoundControl(done, Theme.RadiusSm);
        var footer = DialogActionFooter.Create(done);
        footer.Height = 58;
        if (footer.Controls.OfType<FlowLayoutPanel>().FirstOrDefault() is { } actions)
        {
            actions.Padding = new Padding(18, 13, 18, 13);
        }

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 116,
            BackColor = Theme.WindowBg,
        };
        var rail = new Panel
        {
            Left = 18,
            Top = 20,
            Width = 3,
            Height = 49,
            BackColor = headerAccent,
        };
        Theme.RoundControl(rail, 2);
        header.Controls.Add(rail);
        header.Controls.Add(new Label
        {
            Left = 32,
            Top = 17,
            Width = WidthPx - 50,
            Height = 24,
            Text = passed ? "Build check passed" : "Build check found errors",
            Font = AppFonts.Condensed(12f, FontStyle.Bold),
            ForeColor = Theme.OnDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });
        header.Controls.Add(new Label
        {
            Left = 32,
            Top = 43,
            Width = WidthPx - 50,
            Height = 18,
            Text = modName,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });
        var chips = new FlowLayoutPanel
        {
            Left = 18,
            Top = 76,
            Width = WidthPx - 36,
            Height = 27,
            BackColor = Theme.WindowBg,
            WrapContents = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        chips.Controls.Add(MakeChip(
            $"{result.ErrorCount} {(result.ErrorCount == 1 ? "error" : "errors")}",
            result.ErrorCount == 0 ? Theme.Good : Theme.Crit));
        chips.Controls.Add(MakeChip(
            $"{result.WarningCount} {(result.WarningCount == 1 ? "warning" : "warnings")}",
            result.WarningCount == 0 ? Theme.Good : Theme.Warn));
        header.Controls.Add(chips);

        var summary = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 62,
            Margin = new Padding(18, 0, 18, 0),
            BackColor = Theme.CardBg,
            BorderColor = Theme.LineSoft,
            CornerRadius = Theme.RadiusSm,
        };
        summary.Controls.Add(new Label
        {
            Left = 16,
            Top = 10,
            Width = WidthPx - 68,
            Height = 19,
            Text = passed ? "Ready to build" : "Build is blocked",
            Font = Theme.BodyStrong,
            ForeColor = Theme.OnDark,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });
        summary.Controls.Add(new Label
        {
            Left = 16,
            Top = 31,
            Width = WidthPx - 68,
            Height = 18,
            Text = passed
                ? result.WarningCount == 0
                    ? "No issues found."
                    : "Warnings do not stop the build, but they are listed below for review."
                : "Resolve every error below, then run the check again.",
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });
        var summaryHost = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(18, 0, 18, 16), BackColor = Theme.WindowBg };
        summary.Dock = DockStyle.Fill;
        summaryHost.Controls.Add(summary);

        var findingsLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(18, 8, 0, 0),
            Text = result.Findings.Count == 0 ? "FINDINGS" : $"FINDINGS ({result.Findings.Count})",
            Font = Theme.Eyebrow,
            ForeColor = Theme.Gold,
            BackColor = Theme.WindowBg,
        };

        var findings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18, 0, 18, 10),
            BackColor = Theme.WindowBg,
        };
        var ordered = result.Findings
            .OrderBy(f => SeverityOrder(f.Severity))
            .ThenBy(f => f.SuitId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Area, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count == 0)
        {
            ordered.Add(new ModReleaseValidationService.Finding(
                "INFO", "build", "No issues found. This mod is ready to build."));
        }

        var lastFindingsWidth = -1;
        void PopulateFindings()
        {
            var width = Math.Max(320,
                findings.ClientSize.Width - findings.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
            if (width == lastFindingsWidth)
            {
                return;
            }

            lastFindingsWidth = width;
            findings.SuspendLayout();
            findings.Controls.Clear();
            foreach (var finding in ordered)
            {
                findings.Controls.Add(CreateFindingRow(finding, width));
            }
            findings.ResumeLayout();
        }
        findings.ClientSizeChanged += (_, _) => PopulateFindings();
        PopulateFindings();

        Controls.Add(findings);
        Controls.Add(findingsLabel);
        Controls.Add(summaryHost);
        Controls.Add(header);
        Controls.Add(footer);
        AcceptButton = done;
        Shown += (_, _) =>
        {
            Theme.UseDarkTitleBar(this);
            PopulateFindings();
        };
    }

    public static void Show(IWin32Window owner, string modName, ModReleaseValidationService.Result result)
    {
        using var form = new ReleasePreflightForm(modName, result);
        form.ShowDialog(owner);
    }

    internal static Form CreateForUiAudit(string modName, ModReleaseValidationService.Result result) =>
        new ReleasePreflightForm(modName, result);

    private static Control MakeChip(string text, Color dot)
    {
        var width = TextRenderer.MeasureText(text, Theme.Caption).Width + 37;
        var chip = new RoundedPanel
        {
            Width = width,
            Height = 23,
            Margin = new Padding(0, 1, 7, 1),
            BackColor = Theme.Slate,
            BorderColor = Theme.LineSoft,
            CornerRadius = 11,
        };
        chip.Controls.Add(new StatusDot { Left = 9, Top = 8, Width = 7, Height = 7, DotColor = dot });
        chip.Controls.Add(new Label
        {
            Left = 21,
            Top = 3,
            Width = width - 27,
            Height = 17,
            Text = text,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent,
        });
        return chip;
    }

    private static Control CreateFindingRow(ModReleaseValidationService.Finding finding, int width)
    {
        var (accent, label) = finding.Severity.ToUpperInvariant() switch
        {
            "ERROR" => (Theme.Crit, "ERROR"),
            "WARN" => (Theme.Warn, "WARNING"),
            _ => (Theme.Info, "INFO"),
        };
        var meta = string.IsNullOrWhiteSpace(finding.SuitId)
            ? finding.Area
            : $"{finding.SuitId}  |  {finding.Area}";
        var messageHeight = TextRenderer.MeasureText(
            finding.Message,
            Theme.Body,
            new Size(width - 58, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        var height = Math.Max(62, 38 + messageHeight);
        var row = new RoundedPanel
        {
            Width = width,
            Height = height,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Theme.CardBg,
            BorderColor = Theme.Blend(accent, Theme.LineSoft, 0.55),
            CornerRadius = Theme.RadiusSm,
        };
        var rail = new Panel { Left = 0, Top = 8, Width = 3, Height = height - 16, BackColor = accent };
        Theme.RoundControl(rail, 2);
        row.Controls.Add(rail);
        row.Controls.Add(new Label
        {
            Left = 14,
            Top = 8,
            Width = 82,
            Height = 16,
            Text = label,
            Font = Theme.Eyebrow,
            ForeColor = accent,
            BackColor = Color.Transparent,
        });
        row.Controls.Add(new Label
        {
            Left = 96,
            Top = 8,
            Width = width - 110,
            Height = 16,
            Text = meta,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        });
        var message = new Label
        {
            Left = 14,
            Top = 28,
            Width = width - 28,
            Height = messageHeight + 2,
            Text = finding.Message,
            Font = Theme.Body,
            ForeColor = Theme.OnDark,
            BackColor = Color.Transparent,
        };
        var copy = new ContextMenuStrip();
        copy.Items.Add("Copy finding", null, (_, _) =>
        {
            try { Clipboard.SetText($"{label} [{meta}] {finding.Message}"); }
            catch { /* clipboard may be unavailable */ }
        });
        row.ContextMenuStrip = copy;
        message.ContextMenuStrip = copy;
        row.Controls.Add(message);
        return row;
    }

    private static int SeverityOrder(string severity) => severity.ToUpperInvariant() switch
    {
        "ERROR" => 0,
        "WARN" => 1,
        _ => 2,
    };
}
