using System.Drawing.Imaging;
using System.Text.Json;

namespace Batcomputer;

internal static class CharacterIconAssignmentRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        byte[] Png(int size) { using var bitmap = new Bitmap(size, size); using var bytes = new MemoryStream(); bitmap.Save(bytes, ImageFormat.Png); return bytes.ToArray(); }
        var small = Png(256); var large = Png(512);
        var icons = CharacterIconAssignmentService.Roles.ToDictionary(role => role, role => role == "suit" ? small : large);
        CharacterIconAssignmentService.Validate(icons);
        bool Reject(Action action) { try { action(); return false; } catch { return true; } }
        results.Add((Reject(() => CharacterIconAssignmentService.Validate(new Dictionary<string, byte[]> { ["menu"] = small })) &&
            Reject(() => CharacterIconAssignmentService.Validate(new Dictionary<string, byte[]> { ["suit"] = large })),
            "icon studio enforces 512px character portraits and the native 256px suit tile"));
        var payload = JsonSerializer.Serialize(new { layout = "saved-suit", icons = icons.ToDictionary(p => p.Key, p => "data:image/png;base64," + Convert.ToBase64String(p.Value)) });
        var parsed = PreviewCharacterIconsRequestedEventArgs.Parse(payload);
        results.Add((parsed.LayoutKey == "saved-suit" && parsed.Icons.Count == 4 &&
            Reject(() => PreviewCharacterIconsRequestedEventArgs.Parse("{\"layout\":\"saved\",\"icons\":{\"suit\":\"data:image/png;base64," + Convert.ToBase64String(small) + "\",\"suit\":\"data:image/png;base64," + Convert.ToBase64String(small) + "\"}}")),
            "icon requests round-trip all four roles and reject duplicate roles"));
        results.Add((Reject(() => CharacterIconAssignmentService.Validate(new Dictionary<string, byte[]> { ["menu"] = large, ["left"] = large })) &&
            Reject(() => CharacterIconAssignmentService.Validate(new Dictionary<string, byte[]> { ["unknown"] = large })),
            "partial batches and unknown icon roles fail before cooking or assignment"));
        NativeSuitProject Project() => new() { SlotId = "character_project_alias", TargetPackages = new() { Playable = "/Game/Mods/Example/Characters/BP_Example" },
            IconMenu = "old-menu", IconLeft = "old-left", IconRight = "old-right", IconSuit = "old-suit" };
        var project = Project();
        var entries = icons.ToDictionary(pair => pair.Key, pair => new GeneratedTextureEntry { PackagePath = "/Game/Mods/Example/Textures/T_" + pair.Key });
        results.Add((CharacterIconAssignmentService.TextureModFolder(project) == "Example" &&
            Reject(() => CharacterIconAssignmentService.TextureModFolder(new NativeSuitProject { SlotId = "not_a_mod_folder" })),
            "icon studio derives the texture owner from the playable target, never the saved project slot ID"));
        var invalidOwner = new Dictionary<string, GeneratedTextureEntry> { ["suit"] = new() { PackagePath = "/Game/Mods/character_project_alias/Textures/T_suit" } };
        var invalidSaveCalls = 0;
        results.Add((Reject(() => CharacterIconAssignmentService.AssignAndSave(project, invalidOwner, () => invalidSaveCalls++)) &&
            invalidSaveCalls == 0 && project.GeneratedTextures.Count == 0 && project.IconSuit == "old-suit",
            "icon assignment refuses a mismatched texture owner before changing or saving a project"));
        var calls = 0;
        var failure = Reject(() => CharacterIconAssignmentService.AssignAndSave(project, entries, () => { calls++; throw new IOException("test save failure"); }));
        results.Add((failure && calls == 1 && project.GeneratedTextures.Count == 0 && project.IconMenu == "old-menu" && project.IconLeft == "old-left" && project.IconRight == "old-right" && project.IconSuit == "old-suit",
            "a failed icon save rolls back all four paths and added texture recipes"));
        CharacterIconAssignmentService.AssignAndSave(project, new Dictionary<string, GeneratedTextureEntry> { ["left"] = entries["left"] }, () => calls++);
        results.Add((project.GeneratedTextures.Count == 1 && project.IconLeft == entries["left"].PackagePath && project.IconMenu == "old-menu" && project.IconRight == "old-right" && project.IconSuit == "old-suit",
            "single icon assignment preserves every unrelated icon"));
        results.Add((Reject(() => CharacterIconAssignmentService.AssignAndSave(project, entries, () => calls++)),
            "icon assignment rejects conflicting texture recipes without saving"));
        var all = Project(); calls = 0;
        CharacterIconAssignmentService.AssignAndSave(all, entries, () => calls++);
        results.Add((calls == 1 && all.GeneratedTextures.Count == 4 && all.IconMenu == entries["menu"].PackagePath && all.IconLeft == entries["left"].PackagePath && all.IconRight == entries["right"].PackagePath && all.IconSuit == entries["suit"].PackagePath,
            "all four icons are assigned in one project save"));
        var checkTextures = typeof(ModReleaseValidationService).GetMethod("ValidateGeneratedTextures", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var validRelease = new ModReleaseValidationService.Result();
        checkTextures.Invoke(null, [all, new Dictionary<string, string>(), validRelease, all.SlotId]);
        var wrongOwnerProject = Project(); wrongOwnerProject.GeneratedTextures.Add(invalidOwner["suit"]);
        var invalidRelease = new ModReleaseValidationService.Result();
        checkTextures.Invoke(null, [wrongOwnerProject, new Dictionary<string, string>(), invalidRelease, wrongOwnerProject.SlotId]);
        results.Add((!validRelease.Findings.Any(f => f.Message.Contains("must live under")) &&
            invalidRelease.Findings.Any(f => f.Message.Contains("must live under /Game/Mods/Example/Textures/")),
            "single and batch studio icon ownership matches the real release build-check rule"));
        return results;
    }
}
