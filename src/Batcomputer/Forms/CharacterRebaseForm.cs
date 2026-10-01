namespace Batcomputer;

internal sealed class CharacterRebaseForm : AdaptiveForm
{
    private readonly Dictionary<string, CheckBox> _choices = new();
    internal HashSet<string> SelectedSections => _choices.Where(p => p.Value.Checked).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    internal CharacterRebaseForm(string characterName, IReadOnlyList<CharacterRebaseService.Row> rows)
    {
        Text = "Batcomputer — Rebase from character";
        ClientSize = new(820, 620); MinimumSize = new(680, 540);
        StartPosition = FormStartPosition.CenterParent; BackColor = Theme.WindowBg;
        ForeColor = Theme.OnDark; Font = Theme.Body; Padding = new(18);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute, 80));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 110));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Use the latest saved settings from " + characterName + ".\nChecked sections inherit the character; unchecked sections keep this suit's settings. Your suit name and roster identity stay unchanged.", ForeColor = Theme.OnDark }, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        void Action(string text, Action click)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 34 };
            Theme.StyleDarkButton(button); button.Click += (_, _) => click(); actions.Controls.Add(button);
        }
        Action("Inherit everything", () => { foreach (var box in _choices.Values) box.Checked = true; });
        Action("Keep suit edits", () => { foreach (var row in rows) _choices[row.Section.Id].Checked = Default(row); });
        Action("Keep all", () => { foreach (var box in _choices.Values) box.Checked = false; });
        layout.Controls.Add(actions, 0, 1);
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var row in rows)
        {
            var status = !row.HasBaseline ? "older suit · review manually" : row.SuitEdited ? "suit override · keep or reset" : row.CharacterEdited ? "character has changed" : "no recipe changes";
            var box = new CheckBox { AutoSize = true, Text = row.Section.Label + "\n" + status, Checked = Default(row), Margin = new(4, 6, 4, 8) };
            _choices.Add(row.Section.Id, box); list.Controls.Add(box);
        }
        layout.Controls.Add(list, 0, 2);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted,
            Text = "Overrides are kept by section, not by individual field. Model/material sections start unchecked because external file edits cannot be detected reliably from the recipe. Review these explicitly.\n\nThis refreshes saved Batcomputer settings—not experimental installed PAKs or voice-workshop drafts. Character-wide vehicle, modes and symbol still come from the character when built. Rebuild your mod afterward." }, 0, 3);
        var apply = new Button { Text = "Rebase suit", Width = 140, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        Theme.StyleGoldButton(apply); Theme.StyleDarkButton(cancel);
        Controls.Add(layout); Controls.Add(DialogActionFooter.Create(apply, cancel)); AcceptButton = apply; CancelButton = cancel;
    }
    private static bool Default(CharacterRebaseService.Row row) => row.TakeCharacterByDefault && row.Section.Id is not ("appearance" or "abilities" or "equipment" or "icons");
}
