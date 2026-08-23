using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// Details view for a mod: what it is, which suits it bundles, and the build/install/delete
/// actions. Opened by clicking a mod tile on Home. The dialog only reports which action was
/// chosen - MainForm owns the operations themselves.
/// </summary>
public sealed class ModDetailsDialog : AdaptiveForm
{
    public enum ModAction { None, EditSuits, Rename, ChangeId, Build, Install, OpenOutput, Delete }

    /// <summary>What the user picked. <see cref="ModAction.None"/> when they just closed it.</summary>
    public ModAction Chosen { get; private set; } = ModAction.None;

    private readonly ToolTip _tips = new();

    public ModDetailsDialog(NativeSuitModProject mod, IReadOnlyList<(string Suit, string Slot)> suits, bool built, string buildPath)
    {
        Text = "Mod";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 470);
        MinimumSize = new Size(536, 380);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;
        Theme.StyleTooltip(_tips);

        const int pad = 18;
        var w = ClientSize.Width - pad * 2;
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.WindowBg,
        };
        Controls.Add(body);

        // --- identity card -----------------------------------------------------
        var card = new RoundedPanel
        {
            Left = pad, Top = pad, Width = w, Height = 92,
            BackColor = Theme.CardHi, BorderColor = Theme.LineSoft, CornerRadius = Theme.RadiusSm,
        };
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Theme.Research);
            using var p = Theme.RoundedRect(new Rectangle(1, 10, 3, card.Height - 20), 2);
            e.Graphics.FillPath(b, p);
        };
        card.Controls.Add(new Label
        {
            Left = 14, Top = 8, Width = w - 24, Height = 24,
            Text = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.ModId : mod.DisplayName,
            Font = AppFonts.Condensed(13f, FontStyle.Bold),
            ForeColor = Theme.OnDark, BackColor = Color.Transparent, AutoEllipsis = true,
        });
        card.Controls.Add(new Label
        {
            Left = 14, Top = 33, Width = w - 24, Height = 16,
            Text = string.IsNullOrWhiteSpace(mod.Description) ? "No description." : mod.Description,
            Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
            BackColor = Color.Transparent, AutoEllipsis = true,
        });

        var chips = new FlowLayoutPanel
        {
            Left = 12, Top = 55, Width = w - 22, Height = 28,
            BackColor = Color.Transparent, WrapContents = false,
        };
        chips.Controls.Add(Chip(built ? "built" : "not built", built ? Theme.Good : Theme.OnDarkMuted));
        chips.Controls.Add(Chip($"{suits.Count} suit{(suits.Count == 1 ? "" : "s")}", Theme.Parts));
        chips.Controls.Add(Chip(mod.PackageBaseName, null));
        card.Controls.Add(chips);
        body.Controls.Add(card);

        // --- fields ------------------------------------------------------------
        var y = card.Bottom + 12;
        foreach (var (label, value) in new[]
                 {
                     ("Mod ID", mod.ModId),
                     ("Content root", mod.ContentRoot),
                     ("String table", mod.StringTablePackage),
                 })
        {
            body.Controls.Add(new Label
            {
                Left = pad, Top = y, Width = 92, Height = 17,
                Text = label, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted,
            });
            var v = new Label
            {
                Left = pad + 96, Top = y, Width = w - 96, Height = 17,
                Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
                Font = Theme.Mono, ForeColor = Theme.OnDark, AutoEllipsis = true,
            };
            _tips.SetToolTip(v, value);
            body.Controls.Add(v);
            y += 19;
        }

        // --- suits -------------------------------------------------------------
        y += 8;
        body.Controls.Add(SectionLabel("SUITS IN THIS MOD", pad, y, w));
        y += 22;

        var list = new ListView
        {
            Left = pad, Top = y, Width = w, Height = 118,
            View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        list.Columns.Add("Suit", w - 150);
        list.Columns.Add("Slot", 120);
        Theme.StyleListView(list);
        if (suits.Count == 0)
        {
            list.Items.Add(new ListViewItem(new[] { "No suits yet - use Edit suits to add some.", "" })
            {
                ForeColor = Theme.OnDarkMuted,
            });
        }
        else
        {
            foreach (var (suit, slot) in suits)
            {
                list.Items.Add(new ListViewItem(new[] { suit, slot }));
            }
        }
        body.Controls.Add(list);
        y = list.Bottom + 10;

        // --- build output ------------------------------------------------------
        var outLabel = new Label
        {
            Left = pad, Top = y, Width = w, Height = 16,
            Text = built ? $"Output: {buildPath}" : "Not built yet - use Build to produce the pak trio.",
            Font = Theme.Caption, ForeColor = built ? Theme.OnDarkMuted : Theme.Warn, AutoEllipsis = true,
        };
        _tips.SetToolTip(outLabel, buildPath);
        body.Controls.Add(outLabel);
        body.AutoScrollMinSize = new Size(ClientSize.Width, outLabel.Bottom + 12);

        // --- actions -----------------------------------------------------------
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 104,
            BackColor = Theme.SlateDark,
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        // Row 1: the things you do while building the mod.
        AddAction(footer, "Edit suits", pad, 14, 94, ModAction.EditSuits, false,
            "Add or remove suits from this mod");
        AddAction(footer, "Rename", pad + 102, 14, 88, ModAction.Rename, false,
            "Change the display name");
        AddAction(footer, "Change ID", pad + 198, 14, 100, ModAction.ChangeId, false,
            "Change the technical pak / registry / StringTable identity before release");
        AddAction(footer, "Output", pad + 306, 14, 94, ModAction.OpenOutput, false,
            "Open the build folder in Explorer");
        AddAction(footer, "Delete", ClientSize.Width - pad - 74, 14, 74, ModAction.Delete, false,
            "Delete the mod project. The suits it references are kept.", Theme.Crit);

        // Row 2: the primary flow.
        AddAction(footer, "Build mod", pad, 56, 150, ModAction.Build, true,
            "Build the pak trio, config and StringTable");
        AddAction(footer, "Install to game", pad + 158, 56, 150, ModAction.Install, false,
            "Copy the built mod into the game's ~mods folder");

        var close = new Button { Text = "Close", Width = 92, Height = 32, Top = 56, DialogResult = DialogResult.Cancel };
        close.Left = ClientSize.Width - pad - close.Width;
        Theme.StyleDarkButton(close);
        footer.Controls.Add(close);
        Controls.Add(footer);
        footer.BringToFront();

        CancelButton = close;
    }

    private void AddAction(Control host, string text, int left, int top, int width,
        ModAction action, bool primary, string tip, Color? danger = null)
    {
        var b = new Button { Text = text, Left = left, Top = top, Width = width, Height = 32 };
        if (primary)
        {
            Theme.StyleGoldButton(b);
        }
        else
        {
            Theme.StyleDarkButton(b);
            if (danger is Color c) b.ForeColor = c;
        }
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) =>
        {
            Chosen = action;
            DialogResult = DialogResult.OK;
            Close();
        };
        host.Controls.Add(b);
    }

    private static Label SectionLabel(string text, int left, int top, int width)
    {
        var lbl = new Label { Left = left, Top = top, Width = width, Height = 20, BackColor = Color.Transparent };
        lbl.Paint += (_, e) =>
        {
            TextRenderer.DrawText(e.Graphics, text, Theme.Eyebrow, new Point(0, 4), Theme.Gold);
            var tw = TextRenderer.MeasureText(text, Theme.Eyebrow).Width;
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, tw + 8, 11, lbl.Width, 11);
        };
        return lbl;
    }

    private static Control Chip(string text, Color? dot)
    {
        var padLeft = dot is null ? 9 : 18;
        var tw = TextRenderer.MeasureText(text, Theme.Caption).Width;
        var chip = new RoundedPanel
        {
            Height = 21, Width = tw + padLeft + 9, Margin = new Padding(0, 1, 6, 1),
            BackColor = Theme.Slate, BorderColor = Theme.LineSoft, CornerRadius = 10,
        };
        chip.Controls.Add(new Label
        {
            Left = padLeft, Top = 3, Width = tw + 2, Height = 15,
            Text = text, Font = Theme.Caption, ForeColor = Theme.OnDarkMuted, BackColor = Color.Transparent,
        });
        if (dot is Color c)
        {
            chip.Controls.Add(new StatusDot { Left = 8, Top = 7, Width = 7, Height = 7, DotColor = c });
        }
        return chip;
    }
}
