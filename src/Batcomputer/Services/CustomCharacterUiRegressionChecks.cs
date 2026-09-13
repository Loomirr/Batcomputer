using System.Reflection;

namespace Batcomputer;

internal static class CustomCharacterUiRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var thread = new Thread(() =>
        {
            try
            {
                // Test code-built controls only; no shown windows, desktop interaction or game.
                using var identity = new CharacterIdentityDialog("Character identity", "Character ID (fixed)", "Moon Knight", "MoonKnight", lockedId: true,
                    note: "Pawns.Playable.MoonKnight.MoonKnight\nUnlocked by default; owner and IDs remain fixed.");
                _ = identity.Handle; identity.ClientSize = new Size(680, 550); identity.PerformLayout();
                var boxes = Descendants(identity).OfType<TextBox>().ToArray();
                results.Add((boxes.Length == 4 && boxes.All(box => box.Width >= 180 && box.Height >= (box.Multiline ? 50 : box.PreferredHeight)) && boxes.Single(box => box.Text == "MoonKnight").ReadOnly,
                    "character identity fields remain usable at minimum width and permanent IDs are read-only"));
                using var create = new CharacterIdentityDialog("New suit", "Suit ID", "Unhooded", ownerId: "MoonKnight");
                var idBox = (TextBox)typeof(CharacterIdentityDialog).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)!;
                var preview = (TextBox)typeof(CharacterIdentityDialog).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)!;
                results.Add((create.TechnicalId == "Unhooded" && preview.Text.Contains("Pawns.Playable.MoonKnight.Unhooded"),
                    "new suit menu previews its actual character owner and suggests a valid ID"));
                idBox.Text = "../bad";
                results.Add((!((Button)create.AcceptButton!).Enabled, "invalid character IDs disable creation inline"));
                using var empty = new LoadSuitDialog([], libraryTitle: "Your characters");
                _ = empty.Handle; empty.ClientSize = new Size(760, 510); empty.PerformLayout();
                results.Add((!((Button)empty.AcceptButton!).Enabled && Descendants(empty).OfType<TextBox>().Any(box => box.Text.Contains("Nothing has been saved")),
                    "empty character library explains recovery and disables the selection action"));
                var summary = new SuitProjectService.ProjectSummary("character_moonknight_moonknight", "Moon Knight", "character-fixture.json", DateTime.UtcNow, "", "", true, "MoonKnight");
                using var picker = new BaseCharacterPicker(false, customCharacters: [summary]);
                var list = (ListBox)typeof(BaseCharacterPicker).GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
                list.SelectedIndex = 0;
                var filter = (ThemedDropDown)typeof(BaseCharacterPicker).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
                filter.SelectedIndex = 2;
                results.Add((list.Items.Count == 1 && ((Button)picker.AcceptButton!).Text == "Create a suit", "your-character filter excludes native donors and clearly labels suit creation"));
                typeof(BaseCharacterPicker).GetMethod("Accept", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
                results.Add((picker.SelectedCharacterProjectPath == summary.Path && picker.SelectedVisualPackage is null,
                    "base picker returns a saved character project separately from native visual packages"));
                using var donor = new BaseCharacterPicker(true, customCharacters: [summary]);
                var characters = (System.Collections.ICollection)typeof(BaseCharacterPicker).GetField("_characters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(donor)!;
                results.Add((characters.Count == 0, "custom character definitions cannot masquerade as native machinery donors"));
                results.Add((MainForm.DescribeProjectCounts(1, 1) == "1 character + 1 suit" &&
                    MainForm.DescribeProjectCounts(2, 0) == "2 characters" &&
                    MainForm.DescribeProjectCounts(0, 2, 1) == "2 suits + 1 missing project" &&
                    MainForm.DescribeProjectCounts(0, 0) == "no content",
                    "mod presentation distinguishes definitions, suit variants, missing projects and empty selections"));
                using var split = new SplitContainer { Width = 2000, Height = 700, FixedPanel = FixedPanel.Panel2 };
                // These assertions measure layout, not painting. An unparented native handle
                // can make SplitContainer repaint an invalid DC during a headless collapse.
                foreach (var dpi in new[] { 96, 144, 192 })
                {
                    split.Panel2Collapsed = true;
                    split.Width = 2500;
                    MainForm.FitWorkspaceInspector(split, dpi, true);
                    split.Panel2Collapsed = false;
                    MainForm.FitWorkspaceInspector(split, dpi, true);
                    var width = split.ClientSize.Width - split.SplitterWidth - split.SplitterDistance;
                    results.Add((width == 360 * dpi / 96 && split.Panel1.Width > width,
                        $"inspector restores a bounded width after Home at {dpi} DPI"));
                    split.Width = 650;
                    MainForm.FitWorkspaceInspector(split, dpi, false);
                    results.Add((split.Panel1.Width >= 260 && split.Panel2.Width <= split.Width / 2,
                        $"inspector keeps space for the toybox after narrowing at {dpi} DPI"));
                }
            }
            catch (Exception ex) { results.Add((false, "character UI regression: " + ex)); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) return [(false, "character UI fixtures complete without deadlock")];
        return results;
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        { yield return control; foreach (var descendant in Descendants(control)) yield return descendant; }
    }
}
