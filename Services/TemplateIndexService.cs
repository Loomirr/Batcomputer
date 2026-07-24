using System.Text.Json;

namespace Batcomputer;

public sealed class TemplateIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ProjectRoot { get; }
    public string IndexOutputRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitTemplates");
    public string TemplateIndexPath => Path.Combine(IndexOutputRoot, "template-index.json");
    public string PlayableCandidatesPath => Path.Combine(IndexOutputRoot, "playable-candidates.json");
    public string CutsceneCandidatesPath => Path.Combine(IndexOutputRoot, "cutscene-candidates.json");
    // Filename and the plan's Thomas* property names are an on-disk contract with
    // Build-NativeSuitTemplateIndex.ps1 - renaming either side alone silently reads back nulls.
    // The C# type and method are named for what they are (a donor plan); the wire format is not.
    public string RecommendedPlanPath => Path.Combine(IndexOutputRoot, "recommended-thomas-template-plan.json");

    public TemplateIndexService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public List<TemplateRecord> LoadPlayableCandidates()
    {
        return LoadTemplateList(PlayableCandidatesPath);
    }

    public List<TemplateRecord> LoadCutsceneCandidates()
    {
        return LoadTemplateList(CutsceneCandidatesPath);
    }

    public List<TemplateRecord> LoadAllTemplates()
    {
        return LoadTemplateList(TemplateIndexPath);
    }

    public RecommendedDonorPlan? LoadRecommendedDonorPlan()
    {
        if (!File.Exists(RecommendedPlanPath))
        {
            return null;
        }

        var json = File.ReadAllText(RecommendedPlanPath);
        return JsonSerializer.Deserialize<RecommendedDonorPlan>(json, JsonOptions);
    }

    private static List<TemplateRecord> LoadTemplateList(string path)
    {
        if (!File.Exists(path))
        {
            return new List<TemplateRecord>();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<TemplateRecord>>(json, JsonOptions) ?? new List<TemplateRecord>();
    }
}
