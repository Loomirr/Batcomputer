using System.Drawing.Drawing2D;

namespace Batcomputer;

/// <summary>Edits a suit's pawn tag and menu text.</summary>
public sealed class NativeIdentityDialog : Form
{
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

        y = Section("IDENTITY", y);
        y = Field(_tag, "Pawn tag", project.PawnTag, y,
            "Use the tag family your character needs. It can be any valid pawn tag, but must be unique.");

        _tagDot.Bounds = new Rectangle(PadX, y, 8, 8);
        Controls.Add(_tagDot);
        _tagStatus.Bounds = new Rectangle(PadX + 14, y - 5, FieldW - 90, 18);
        _tagStatus.Font = Theme.Caption;
        _tagStatus.BackColor = Color.Transparent;
        Controls.Add(_tagStatus);

        const int suggestW = 78;
        var suggest = new Button
        {
            Text = "Suggest",
            Bounds = new Rectangle(PadX + FieldW - suggestW, y - 9, suggestW, 24),
            Visible = !string.IsNullOrWhiteSpace(_suggestedTag),
        };
        Theme.StyleSmallDarkButton(suggest);
        suggest.Click += (_, _) => { _tag.Text = _suggestedTag; _tag.Focus(); _tag.SelectAll(); };
        Controls.Add(suggest);
        y += 22;

        y = Section("MENU TEXT", y);
        y = Field(_name, "Name", project.DisplayName, y, "Shown as the suit name in the character menu.");
        y = Field(_desc, "Description", project.Description, y, "Shown beneath the suit name.");
        y = Field(_locked, "Locked description", project.LockedDescription, y,
            "Shown while this suit is gated. Leave blank when it is never gated.");

        y = Section("ADVANCED", y);
        y = Field(_progress, "Progress / unlock tag", project.ProgressTag, y,
            "Copied from the playable base. Change it only when this suit uses a different unlock gate.");

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
        if (string.IsNullOrWhiteSpace(_tag.Text) && !string.IsNullOrWhiteSpace(_suggestedTag))
        {
            _tag.Text = _suggestedTag;
        }
        Revalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(this);
    }

    private int Section(string title, int y)
    {
        var label = new Label
        {
            Bounds = new Rectangle(PadX, y, FieldW, 22),
            BackColor = Theme.WindowBg,
        };
        label.Paint += (_, e) =>
        {
            var width = TextRenderer.MeasureText(title, Theme.Eyebrow).Width;
            TextRenderer.DrawText(e.Graphics, title, Theme.Eyebrow, new Point(0, 6), Theme.Gold);
            using var pen = new Pen(Theme.LineSoft);
            e.Graphics.DrawLine(pen, width + 10, label.Height / 2 + 1, label.Width, label.Height / 2 + 1);
        };
        Controls.Add(label);
        return y + 30;
    }

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

        var hintHeight = TextRenderer.MeasureText(hint, Theme.Caption, new Size(FieldW, 0),
            TextFormatFlags.WordBreak).Height;
        Controls.Add(new Label
        {
            Text = hint,
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
            Bounds = new Rectangle(PadX, y, FieldW, hintHeight),
            BackColor = Color.Transparent,
        });
        return y + hintHeight + 14;
    }

    private void Revalidate()
    {
        var hasTag = !string.IsNullOrWhiteSpace(_tag.Text);
        _tagDot.DotColor = hasTag ? Theme.Good : Theme.Crit;
        _tagStatus.ForeColor = hasTag ? Theme.Good : Theme.Crit;
        _tagStatus.Text = hasTag
            ? "Saved as entered. Release validation checks for duplicates."
            : "Required - every suit needs its own pawn tag.";
        _save.Enabled = hasTag;
    }
}
