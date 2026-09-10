using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Batcomputer;

public sealed partial class MainForm
{
    internal static string DeclarativeRecipeFingerprint(NativeSuitProject project)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(project))!.AsObject();
        document.Remove(nameof(NativeSuitProject.Changes)); // The journal does not change generated assets.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    private bool CanReuseMaterialStage(NativeSuitProject project, string projectRoot, string content, string component)
    {
        // Older stages have no recipe fingerprint and take the full rebuild once. A changed
        // recipe, base, failed stage, or OBJ material edit must also replay the full declaration.
        if (FindCustomStaticMeshForComponent(project, component) is not null ||
            project.PairedCapeAdapter is not null || IsIncompleteDeclarativeGraftStage(project, content, projectRoot)) return false;
        var marker = Path.Combine(Directory.GetParent(content)!.Parent!.FullName, "completed-recipe.sha256");
        try
        {
            return File.Exists(marker) && File.ReadAllText(marker) == DeclarativeRecipeFingerprint(project) &&
                HasCookedPackagePair(content, project.TargetPackages.Playable) &&
                HasCookedPackagePair(content, project.TargetPackages.Cutscene);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
