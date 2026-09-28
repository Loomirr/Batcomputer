using System.Text.Json;

namespace Batcomputer;

/// <summary>Asset-free draft and viewer-download checks; the real UE cook is tested separately.</summary>
internal static class AnimationDraftRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        const string signature = "Root:|Head:Root";
        string[] bones = ["Root", "Head"];
        const string valid = """
            {"schema":"batcomputer.animation-draft.v1","name":"Wave","fps":30,"durationFrames":60,
             "loop":true,"rigSignature":"Root:|Head:Root","tracks":[{"bone":"Head","keys":[
             {"frame":0,"p":[0,0,0],"q":[0,0,0,1],"s":[1,1,1]},
             {"frame":30,"p":[0,0,0],"q":[0,0,0,1],"s":[1.1,1,1]}]}]}
            """;
        static bool Accepted(string json, string rig, string[] nativeBones)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return AnimationDraftCookService.ValidateDraft(document.RootElement, rig, nativeBones) == "Wave";
            }
            catch (Exception) { return false; }
        }
        Check(Accepted(valid, signature, bones) &&
              Accepted(valid.Replace("\"loop\":true,", "").Replace(",\"s\":[1,1,1]", "").Replace(",\"s\":[1.1,1,1]", ""), signature, bones),
            "animation draft cook accepts scaled keys and legacy keys without scale/loop", failures, output);
        Check(!Accepted(valid, "Root:|Other:Root", bones) &&
              !Accepted(valid.Replace("\"s\":[1.1,1,1]", "\"s\":[-1,1,1]"), signature, bones) &&
              !Accepted(valid.Replace("\"q\":[0,0,0,1]", "\"q\":[0,0,0,0]"), signature, bones),
            "animation draft cook rejects mismatched rigs and invalid bone transforms", failures, output);
        var path = Path.Combine("C:", "test", "Wave.animation-draft.json");
        Check(ModelPreviewControl.IsAnimationDraftDownload("blob:https://p1.batcomputer/abc", "p1.batcomputer", path) &&
              !ModelPreviewControl.IsAnimationDraftDownload("blob:https://untrusted.example/abc", "p1.batcomputer", path) &&
              !ModelPreviewControl.IsAnimationDraftDownload("blob:https://p1.batcomputer/abc", "p1.batcomputer", "Wave.json"),
            "embedded viewer saves only same-origin animation draft JSON downloads", failures, output);
    }

    private static void Check(bool passed, string description, List<string> failures, TextWriter output)
    {
        output.WriteLine((passed ? "PASS" : "FAIL") + "  " + description);
        if (!passed) failures.Add(description);
    }
}
