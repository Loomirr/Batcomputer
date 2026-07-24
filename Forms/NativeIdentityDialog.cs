using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>
/// The suit's native identity: its globally-unique pawn tag plus the menu text that lands in the
/// mod StringTable. The pawn tag is the field that actually matters - two suits sharing one cannot
/// be switched between in-game - so it gets live validation and blocks Save, rather than being a
/// plain box labelled "(required)" that accepted anything.
/// </summary>
public sealed class NativeIdentityDialog : Form
{
    /// <summary>The tag the game's own TheBatman2025 character answers to.</summary>
    private const string DonorPawnTag = "Pawns.Playable.Batman.TheBatman2025";

    private const int PadX = 22;
    private const int FieldW = 496;

    private readonly TextBox _tag = new();
    private readonly TextBox _name = new();
    private readonly TextBox _desc = new();
    private readonly TextBox _locked = new();
    private readonly TextBox _progress = new();
    private readonly Label _tagStatus = new();
    private readonly StatusDot _tagDot = new();
    private readonly Button _save = new();
    private readonly string _suggestedTag;

    public string PawnTag => _tag.Text.Trim();
    public string DisplayName => _name.Text.Trim();
    public string Description => _desc.Text.Trim();
    public string LockedDescription => _locked.Text.Trim();
    public string ProgressTag => _progress.Text.Trim();

    public NativeIdentityDialog(NativeSuitProject project, string suggestedTag)
    {
        _suggestedTag = suggestedTag ?? "";

        Text = "Native identity";
        // Positions here are exact pixels; let font auto-scaling nudge them and the right-edge
        // controls drift off the client area (the Suggest button was clipped at >100% DPI).
        AutoScaleMode = AutoScaleMode.None;
        AutoScroll = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark;
        Font = Theme.Body;

        var y = 18;

        // --- header ---------------------------------------------------------
        Controls.Add(new Label
        {
            Text = "Native identity",
            Font = Theme.Heading,
            ForeColor = Theme.OnDark,
            Bounds = new Rectangle(PadX, y, FieldW, 24),
            BackColor = Color.Transparent,
        });
        y += 24;
        Controls.Add(new Label
        {
            Text = "How the game and its menus refer to this suit.",
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            Bounds = new Rectangle(PadX, y, FieldW, 18),
            BackColor = Color.Transparent,
        });
        y += 30;

        // --- pawn tag -------------------------------------------------------
        y = Section("IDENTITY", y);
        y = Field(_tag, "Pawn tag", project.PawnTag, y,
            "Globally unique across every installed mod. This is what the game switches on.");

        _tagDot.Bounds = new Rectangle(PadX, y, 8, 8);
        Controls.Add(_tagDot);
        _tagStatus.Bounds = new Rectangle(PadX + 14, y - 5, FieldW - 90, 18);
        _tagStatus.Font = Theme.Caption;
        _tagStatus.BackColor = Color.Transparent;
        Controls.Add(_tagStatus);

        const int suggestW = 78;
        var suggest = new Button
        {
            // Right edge aligned with the input fields (right edge = PadX + FieldW).
            Text = "Suggest",
            Bounds = new Rectangle(PadX + FieldW - suggestW, y - 9, suggestW, 24),
            Visible = !string.IsNullOrWhiteSpace(_suggestedTag),
        };
        Theme.StyleSmallDarkButton(suggest);
        suggest.Click += (_, _) => { _tag.Text = _suggestedTag; _tag.Focus(); _tag.SelectAll(); };
        Controls.Add(suggest);
        y += 22;

        // --- menu text ------------------------------------------------------
        y = Section("MENU TEXT", y);
        y = Field(_name, "Name", project.DisplayName, y, "Shown as the suit's name in the character menu.");
        y = Field(_desc, "Description", project.Description, y, "The description for your suit.");
        y = Field(_locked, "Locked description", project.LockedDescription, y,
            "Shown while the suit is gated. Leave empty if it never is.");

        // --- advanced -------------------------------------------------------
        y = Section("ADVANCED", y);
        y = Field(_progress, "Progress / unlock tag", project.ProgressTag, y,
            "Which unlock gates this suit. The default keeps it unlocked for everyone - change it only " +
            "if you want the suit gated behind progression.");

        // --- buttons --------------------------------------------------------
        // Size to the content, then place the buttons - not the other way round.
        ClientSize = new Size(FieldW + PadX * 2, y + 60);
        var buttonY = ClientSize.Height - 46;

        _save.Text = "Save";
        _save.DialogResult = DialogResult.OK;
        _save.Bounds = new Rectangle(PadX + FieldW - 178, buttonY, 86, 32);
        Theme.StyleGoldButton(_save);
        Controls.Add(_save);

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(PadX + FieldW - 86, buttonY, 86, 32),
        };
        Theme.StyleDarkButton(cancel);
        Controls.Add(cancel);

        AcceptButton = _save;
        CancelButton = cancel;

        _tag.TextChanged += (_, _) => Revalidate();
        Revalidate();

        // Prefill a suggestion rather than opening empty: a blank tag is how suits used to end up
        // silently sharing the donor tag at package time.
        if (string.IsNullOrWhiteSpace(_tag.Text) && !string.IsNullOrWhiteSpace(_suggestedTag))
        {
            _tag.Text = _suggestedTag;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Directly, not via the app-wide Idle sweep: a modal dialog is visible before the first
        // idle tick, so the sweep alone lets a light caption flash.
        Theme.UseDarkTitleBar(this);
    }

    private int Section(string title, int y)
    {
        var lbl = new Label
        {
            Bounds = new Rectangle(PadX, y, FieldW, 22),
            BackColor = Theme.WindowBg,
        };
        lbl.Paint += (_, e) =>
        {
            var tw = TextRenderer.MeasureText(title, Theme.Eyebrow).Width;
            TextRenderer.DrawText(e.Graphics, title, Theme.Eyebrow, new Point(0, 6), Theme.Gold);
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, tw + 10, lbl.Height / 2 + 1, lbl.Width, lbl.Height / 2 + 1);
        };
        Controls.Add(lbl);
        return y + 30;
    }

    /// <summary>Label, rounded input, and a caption underneath. Returns the next free Y.</summary>
    private int Field(TextBox box, string label, string? value, int y, string hint)
    {
        Controls.Add(new Label
        {
            Text = label,
            Font = Theme.BodyStrong,
            ForeColor = Theme.OnDark,
            Bounds = new Rectangle(PadX, y, FieldW, 18),
            BackColor = Color.Transparent,
        });
        y += 22;

        var frame = new RoundedPanel
        {
            Bounds = new Rectangle(PadX, y, FieldW, 34),
            CornerRadius = Theme.RadiusSm,
            BackColor = Theme.Slate,
            BorderColor = Theme.SlateLight,
        };
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Slate;
        box.ForeColor = Theme.OnDark;
        box.Font = Theme.Body;
        box.Text = value ?? "";
        box.Left = 11;
        box.Width = FieldW - 22;
        box.Top = (34 - box.PreferredHeight) / 2;
        frame.Controls.Add(box);
        Controls.Add(frame);
        y += 38;

        // Measure the wrapped hint rather than guessing from its length - a guess that runs short
        // is how the Settings panel ended up with two controls on the same row.
        var hintH = TextRenderer.MeasureText(hint, Theme.Caption, new Size(FieldW, 0),
            TextFormatFlags.WordBreak).Height;
        Controls.Add(new Label
        {
            Text = hint,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            Bounds = new Rectangle(PadX, y, FieldW, hintH),
            BackColor = Color.Transparent,
            AutoSize = false,
        });
        return y + hintH + 14;
    }

    /// <summary>
    /// Live tag verdict. Blocks Save on the two states that produce a broken suit; the namespace
    /// check only warns, because an unusual tag may still be deliberate.
    /// </summary>
    private void Revalidate()
    {
        var tag = _tag.Text.Trim();
        Color dot;
        string message;
        bool ok;

        if (string.IsNullOrWhiteSpace(tag))
        {
            dot = Theme.Crit;
            message = "Required — without it the suit falls back to the shared donor tag.";
            ok = false;
        }
        else if (tag.Equals(DonorPawnTag, StringComparison.OrdinalIgnoreCase))
        {
            dot = Theme.Crit;
            message = "That tag belongs to the game's own TheBatman2025 character.";
            ok = false;
        }
        else if (!tag.StartsWith("Pawns.Playable.", StringComparison.OrdinalIgnoreCase))
        {
            dot = Theme.Warn;
            message = "Outside Pawns.Playable.* — it will build, but may not resolve in-game.";
            ok = true;
        }
        else
        {
            dot = Theme.Good;
            message = "Unique namespace — good.";
            ok = true;
        }

        _tagDot.DotColor = dot;
        _tagStatus.Text = message;
        _tagStatus.ForeColor = dot;
        _save.Enabled = ok;
    }
}
