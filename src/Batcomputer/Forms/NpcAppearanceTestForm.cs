using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

internal sealed class NpcAppearanceTestForm : Form
{
    internal NpcAppearanceTestForm(string package)
    {
        Text = "Experimental NPC material replacer"; Width = 760; Height = 380;
        StartPosition = FormStartPosition.CenterParent;
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoScroll = true };
        Controls.Add(root);
        root.Controls.Add(new Label { Width = 690, Height = 76, Text = package + "\n\nChanges every instance of this exact NPC. Conflicts with other replacers of this Blueprint. Material-only test: no new attachments, AI or quest edits. Not installed automatically." });
        var material = new ComboBox { Width = 690, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Original material" };
        root.Controls.Add(material);
        var replacement = new TextBox { Width = 690, PlaceholderText = "Replacement /Game/.../MI_... package", AccessibleName = "Replacement material package" };
        root.Controls.Add(replacement);
        var browse = new Button { Text = "Choose extracted material…", AutoSize = true };
        root.Controls.Add(browse);
        var build = new Button { Text = "Build experimental test PAK", AutoSize = true };
        root.Controls.Add(build);
        var status = new TextBox { Width = 690, Height = 65, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        root.Controls.Add(status);
        var settings = AppSettings.Current;
        var content = settings.EffectiveExtractedContentRoot();
        var maps = settings.EffectiveUsmapPath() ?? "";
        try
        {
            var source = Path.Combine(content, package[6..] + ".uasset");
            var asset = new UAsset(source, EngineVersion.VER_UE5_6, new Usmap(maps), CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
            foreach (var name in asset.GetNameMapIndexList().Select(n => n.ToString()).Where(n => n.StartsWith("/Game/", StringComparison.Ordinal) && Path.GetFileName(n).StartsWith("MI_", StringComparison.Ordinal)).Distinct()) material.Items.Add(name);
            if (material.Items.Count > 0) material.SelectedIndex = 0;
            else { build.Enabled = false; status.Text = "No direct native materials found. Inherited material editing is not supported by this prototype."; }
        }
        catch (Exception ex) { build.Enabled = false; status.Text = ex.Message; }
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Extracted material instance|MI_*.uasset", InitialDirectory = content };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            if (!FileSystemPathUtil.IsWithinDirectory(picker.FileName, content)) { status.Text = "Choose an existing material inside the configured base-game extraction."; return; }
            replacement.Text = "/Game/" + Path.ChangeExtension(Path.GetRelativePath(content, picker.FileName), null).Replace('\\', '/');
        };
        build.Click += async (_, _) =>
        {
            build.Enabled = false; browse.Enabled = false; material.Enabled = false; replacement.Enabled = false;
            try
            {
                var recipe = new NpcAppearanceTestService.Recipe(package, material.Text, replacement.Text.Trim());
                NpcAppearanceTestService.Validate(recipe);
                var work = Path.Combine(AppSettings.RuntimeRoot, "NpcAppearanceTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                var recipeFile = Path.Combine(work, "recipe.json"); File.WriteAllText(recipeFile, JsonSerializer.Serialize(recipe));
                status.Text = "Building and verifying test PAK…";
                var pak = await Task.Run(() => NpcAppearanceTestService.Build(recipeFile, content, maps,
                    Path.Combine(settings.UnrealEngineRoot ?? "", "Engine/Binaries/Win64/UnrealPak.exe"), Path.Combine(work, "Output")));
                status.Text = "Built, not installed. Test before sharing:\r\n" + pak;
            }
            catch (Exception ex) { status.Text = "Build failed: " + ex.Message; }
            finally { build.Enabled = true; browse.Enabled = true; material.Enabled = true; replacement.Enabled = true; }
        };
    }
}
