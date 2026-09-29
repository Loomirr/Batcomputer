using System.Text.Json;

namespace Batcomputer;

internal static class AnimationDraftLibraryRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "Batcomputer-DraftLibrary-" + Guid.NewGuid().ToString("N"));
        const string draft = """
            {"schema":"batcomputer.animation-draft.v1","name":"Wave","fps":30,"durationFrames":60,"loop":true,
             "rigSignature":"Root:|Hand:Root","tracks":[{"bone":"Hand","keys":[
             {"frame":0,"p":[0,0,0],"q":[0,0,0,1]},{"frame":30,"p":[0,0,0],"q":[0,0.1,0,0.99498744]}]}]}
            """;
        void Check(bool pass, string name) { output.WriteLine((pass ? "PASS" : "FAIL") + "  " + name); if (!pass) failures.Add(name); }
        try
        {
            var service = new AnimationDraftLibraryService(workspace);
            var project = new NativeSuitProject { SlotId = "Test", DisplayName = "Test character" };
            var before = JsonSerializer.Serialize(project);
            var first = service.Save(draft, project, cookedPackage:"/Game/Mods/Test/A_Wave");
            var second = service.Save(draft, project);
            Check(first.Id == second.Id && service.Load().Count == 1 && File.ReadAllText(service.DraftPath(first)) == draft && before == JsonSerializer.Serialize(project),
                "your animations keeps one owned copy of identical imported drafts without modifying the suit");
            var library = new AnimLibraryService(workspace);
            // Missing-byte external records must never be advertised as a ready authored cook.
            library.Save(new AnimLibrary { Entries = [new AnimLibraryEntry { Id = "external", PackagePath = "/Game/Mods/Test/A_Wave", SourceMode = "external" }] });
            project.LocomotionOverrides.Add(new AnimSequenceOverride { DonorSequence = "A_Idle", ReplacementPackage = "/Game/Mods/Test/A_Wave.A_Wave" });
            project.AnimationSlotOverrides.Add(new AnimationSlotOverride { ActionTag = "Animation.Action.Wave", ReplacementPackage = "/Game/Mods/Test/A_Wave" });
            var view = service.Views(project).Single();
            Check(view.UsedBy.Length == 2 && view.UsedBy.Contains("A_Idle") && view.Packages.Single() == "/Game/Mods/Test/A_Wave",
                "your animations reports exact current-character sequence and action assignments using normalized package identities");
            var edited = draft.Replace("\"name\":\"Wave\"", "\"name\":\"Edited wave\"");
            var edit = service.Save(edited, project, first.Id);
            Check(edit.Id == first.Id && service.Load().Count == 1 && service.Views(project).Single().Status.Contains("cook again"),
                "editing a managed draft keeps its identity and existing assignments but flags the need to cook again");
            var indexBefore = File.ReadAllText(service.IndexPath);
            var bytesBefore = File.ReadAllText(service.DraftPath(edit));
            bool rejected = false;
            try { service.Save(edited.Replace("Root:|Hand:Root", "Root:|Other:Root").Replace("\"bone\":\"Hand\"", "\"bone\":\"Other\""), project, first.Id); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(service.IndexPath) == indexBefore && File.ReadAllText(service.DraftPath(edit)) == bytesBefore,
                "a mismatched-rig edit cannot overwrite an existing animation draft or index");
            var preview = Path.Combine(workspace, "Preview"); Directory.CreateDirectory(preview);
            service.WritePreview(preview, project);
            Check(File.ReadAllText(Path.Combine(preview, "drafts", edit.Id + ".json")) == edited &&
                  File.ReadAllText(Path.Combine(preview, "models.js")).Contains("PREVIEW_USER_ANIMATIONS"),
                "draft previews copy only private authoring sources and metadata without changing saved-suit format");
            File.Delete(service.DraftPath(edit));
            Check(!service.Views(project).Single().Available && service.Load().Count == 1,
                "a missing draft remains discoverable as unavailable without silently discarding its assignments");
            var source = Path.Combine(workspace, "original.json"); File.WriteAllText(source, draft);
            service.Import(source, project);
            Check(File.ReadAllText(source) == draft, "importing a draft leaves its original authoring file untouched");
        }
        catch (Exception ex) { Check(false, "animation draft library checks: " + ex.Message); }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
    }
}
