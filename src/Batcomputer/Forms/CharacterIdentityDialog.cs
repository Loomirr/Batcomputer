namespace Batcomputer;

/// <summary>Guided character/variant identity with live validation and an ownership preview.</summary>
public sealed class CharacterIdentityDialog : AdaptiveForm
{
    private readonly TextBox _name = new(), _id = new(), _description = new();
    private readonly TextBox _preview = CharacterMenuStyle.DetailText();
    private readonly Label _validation = CharacterMenuStyle.Label("");
    private readonly Button _save = new();
    private bool _idEdited, _suggestingId;
    public string DisplayNameValue => _name.Text.Trim();
    public string TechnicalId => _id.Text.Trim();
    public string DescriptionValue => _description.Text.Trim();

    public CharacterIdentityDialog(string title, string idLabel, string initialName = "", string initialId = "",
        string description = "", bool lockedId = false, string note = "", string? ownerId = null)
    {
        Text = "Batcomputer — " + title; ClientSize = new Size(860, 610); MinimumSize = new Size(680, 550);
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body;
        MinimizeBox = false; ShowInTaskbar = false;
        var root = CharacterMenuStyle.Grid(1, 2); root.Padding = new Padding(16);
        root.RowStyles.Add(new(SizeType.Absolute, 100)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(CharacterMenuStyle.Header(title, lockedId ? "Update the display details. Permanent IDs keep saved selections and dependent suits stable." :
            "1  Name your creation   →   2  Review its identity   →   3  Customize and build"), 0, 0);
        var body = CharacterMenuStyle.Grid(2, 1); body.RowStyles.Add(new(SizeType.Percent, 100));
        body.ColumnStyles.Add(new(SizeType.Percent, 56)); body.ColumnStyles.Add(new(SizeType.Percent, 44)); root.Controls.Add(body, 0, 1);
        var edit = CharacterMenuStyle.Card(); edit.Margin = new Padding(0, 0, 12, 0); body.Controls.Add(edit, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; edit.Controls.Add(scroll);
        var fields = CharacterMenuStyle.Grid(1, 7); fields.Dock = DockStyle.Top; fields.Height = 370;
        foreach (var height in new[] { 24, 44, 44, 44, 28, 98, 88 }) fields.RowStyles.Add(new(SizeType.Absolute, height));
        scroll.Controls.Add(fields);
        _name.Text = initialName; _id.Text = initialId; _id.ReadOnly = lockedId; _id.MaxLength = 64;
        _idEdited = lockedId || initialId.Length > 0;
        _name.PlaceholderText = ownerId is null ? "e.g. Ragman" : "e.g. Unmasked";
        _id.PlaceholderText = ownerId is null ? "Ragman" : "Unmasked";
        _description.Text = description; _description.Multiline = true; _description.ScrollBars = ScrollBars.Vertical;
        _description.PlaceholderText = "Optional description shown in the suit menu";
        fields.Controls.Add(CharacterMenuStyle.Label("DISPLAY NAME", Theme.Eyebrow), 0, 0);
        fields.Controls.Add(CharacterMenuStyle.Input(_name), 0, 1);
        fields.Controls.Add(CharacterMenuStyle.Label(idLabel, Theme.Caption), 0, 2);
        fields.Controls.Add(CharacterMenuStyle.Input(_id), 0, 3);
        fields.Controls.Add(CharacterMenuStyle.Label("DESCRIPTION · OPTIONAL", Theme.Eyebrow), 0, 4);
        fields.Controls.Add(CharacterMenuStyle.Input(_description), 0, 5); fields.Controls.Add(_validation, 0, 6);
        var detail = CharacterMenuStyle.Card(); body.Controls.Add(detail, 1, 0);
        var right = CharacterMenuStyle.Grid(1, 3); right.RowStyles.Add(new(SizeType.Absolute, 32));
        right.RowStyles.Add(new(SizeType.Absolute, 26)); right.RowStyles.Add(new(SizeType.Percent, 100)); detail.Controls.Add(right);
        right.Controls.Add(CharacterMenuStyle.Label("Identity preview", Theme.Heading, Theme.Materials), 0, 0);
        right.Controls.Add(CharacterMenuStyle.Label("UNLOCKED BY DEFAULT", Theme.Eyebrow, Theme.Base), 0, 1); right.Controls.Add(_preview, 0, 2);
        _save.Text = lockedId ? "Save details" : ownerId is null ? "Create character" : "Create suit"; _save.Width = 156;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        Theme.StyleGoldButton(_save); Theme.StyleDarkButton(cancel);
        _save.Click += (_, _) => { if (_save.Enabled) { DialogResult = DialogResult.OK; Close(); } };
        Controls.Add(root); Controls.Add(DialogActionFooter.Create(_save, cancel)); AcceptButton = _save; CancelButton = cancel;
        void RefreshIdentity()
        {
            if (!_idEdited && !lockedId)
            {
                _suggestingId = true;
                _id.Text = new string(DisplayNameValue.Where(c => char.IsAsciiLetterOrDigit(c)).Take(64).ToArray());
                _suggestingId = false;
            }
            var valid = CustomCharacterProjectService.IsIdentifier(TechnicalId);
            _save.Enabled = valid && DisplayNameValue.Length > 0;
            _validation.Text = DisplayNameValue.Length == 0 ? "Enter a display name to continue." : !valid ?
                "The ID must start with a letter and contain only letters and numbers (up to 64)." : lockedId ?
                "Your permanent identity stays unchanged." : "This ID is permanent. Availability is checked when you create.";
            _validation.ForeColor = _save.Enabled ? Theme.OnDarkMuted : Theme.Materials;
            var owner = ownerId ?? TechnicalId;
            _preview.Text = $"{(ownerId is null ? "CHARACTER OWNER" : "SUIT OWNER")}\r\n{owner}\r\n\r\nPAWN TAG\r\n" +
                (valid ? $"Pawns.Playable.{owner}.{TechnicalId}" : "Enter a valid ID to preview") +
                "\r\n\r\n" + (ownerId is null ? "Independent roster entry with its own default suit. The native gameplay donor remains separate." :
                    "A separate suit for this character. Build Mod includes its required default character automatically.") + "\r\n\r\n" + note;
        }
        _name.TextChanged += (_, _) => RefreshIdentity();
        _id.TextChanged += (_, _) => { if (_suggestingId) return; _idEdited = true; RefreshIdentity(); };
        RefreshIdentity();
        scroll.ClientSizeChanged += (_, _) => fields.Height = Math.Max(scroll.ClientSize.Height, 370 * DeviceDpi / 96);
    }
}
