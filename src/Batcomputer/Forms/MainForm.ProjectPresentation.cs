namespace Batcomputer;

public sealed partial class MainForm
{
    private string CurrentProjectNoun => CustomCharacterProjectService.IsCharacter(_currentProject) ? "character" : "suit";

    internal static string DescribeProjectCounts(int characters, int suits, int missing = 0)
    {
        var parts = new List<string>();
        if (characters > 0) parts.Add($"{characters} character{(characters == 1 ? "" : "s")}");
        if (suits > 0) parts.Add($"{suits} suit{(suits == 1 ? "" : "s")}");
        if (missing > 0) parts.Add($"{missing} missing project{(missing == 1 ? "" : "s")}");
        return parts.Count == 0 ? "no content" : string.Join(" + ", parts);
    }

    private string DescribeModContent(NativeSuitModProject? mod, bool enabledOnly = false,
        IReadOnlyList<SuitProjectService.ProjectSummary>? summaries = null)
    {
        if (mod is null) return "no content";
        summaries ??= new SuitProjectService(_projectRootText.Text.Trim()).ListProjects().ToList();
        var characters = 0;
        var suits = 0;
        var missing = 0;
        foreach (var entry in mod.Suits.Where(entry => !enabledOnly || entry.Enabled))
        {
            var path = ModService.ResolveSuitProjectPath(entry);
            var summary = summaries.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (summary is null) missing++;
            else if (summary.IsCharacter) characters++;
            else suits++;
        }
        return DescribeProjectCounts(characters, suits, missing);
    }
}
