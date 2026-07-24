using System.Text.Json;

namespace Batcomputer;

public sealed class SuitProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public sealed record ProjectSummary(string SlotId, string DisplayName, string Path, DateTime Modified, string CoverImagePath);

    /// <summary>Lists saved suit projects (newest first).</summary>
    public IReadOnlyList<ProjectSummary> ListProjects()
    {
        var results = new List<ProjectSummary>();
        if (!Directory.Exists(GuiOutputRoot))
        {
            return results;
        }

        foreach (var path in Directory.EnumerateFiles(GuiOutputRoot, "*.native-suit-project.json"))
        {
            var slot = System.IO.Path.GetFileName(path).Replace(".native-suit-project.json", "");
            var display = slot;
            try
            {
                var project = JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(path), JsonOptions);
                if (project is not null)
                {
                    // Skip nameless placeholder projects - an accidental save before a
                    // name was chosen shows up as a nameless timestamp entry.
                    if (string.IsNullOrWhiteSpace(project.DisplayName))
                    {
                        continue;
                    }
                    display = project.DisplayName;
                    slot = project.SlotId;
                }
            }
            catch
            {
                // Skip unreadable files but still list them by filename.
            }
            var cover = "";
            try
            {
                var project = JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(path), JsonOptions);
                cover = project?.CoverImagePath ?? "";
            }
            catch
            {
                // The project was already accepted above; a missing cover should
                // never make an otherwise valid suit disappear from Home.
            }
            results.Add(new ProjectSummary(slot, display, path, File.GetLastWriteTime(path), cover));
        }

        return results.OrderByDescending(p => p.Modified).ToList();
    }

    public NativeSuitProject? LoadProject(string path)
    {
        return File.Exists(path)
            ? JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    public string ProjectRoot { get; }
    public string GuiOutputRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitGuiProjects");

    public SuitProjectService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public string SaveProject(NativeSuitProject project)
    {
        Directory.CreateDirectory(GuiOutputRoot);
        var safeSlot = MakeSafeFileName(project.SlotId);
        var path = Path.Combine(GuiOutputRoot, $"{safeSlot}.native-suit-project.json");
        File.WriteAllText(path, JsonSerializer.Serialize(project, JsonOptions));
        return path;
    }

    public string SavePatchPlan(NativeSuitPatchPlan plan)
    {
        Directory.CreateDirectory(GuiOutputRoot);
        var safeSlot = MakeSafeFileName(plan.Project.SlotId);
        var path = Path.Combine(GuiOutputRoot, $"{safeSlot}.patch-plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, JsonOptions));
        return path;
    }

    public string ProjectOutputDirectory(NativeSuitProject project) =>
        Path.Combine(GuiOutputRoot, MakeSafeFileName(project.SlotId));

    /// <summary>
    /// Deletes only the saved project JSON and its project-owned generated
    /// directory. Imported source files outside the project directory are left
    /// alone because they may be shared by another suit.
    /// </summary>
    public void DeleteProjectFromTool(string projectPath, NativeSuitProject project)
    {
        var root = Path.GetFullPath(GuiOutputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!fullProjectPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !fullProjectPath.EndsWith(".native-suit-project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refused to delete a project outside the tool's saved-project folder.");
        }

        if (File.Exists(fullProjectPath))
        {
            File.Delete(fullProjectPath);
        }

        var projectDir = Path.GetFullPath(ProjectOutputDirectory(project));
        if (projectDir.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(projectDir))
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    public string CreateUnpatchedStage(NativeSuitProject project)
    {
        var stageRoot = Path.Combine(GuiOutputRoot, project.SlotId, "UnpatchedStage", "LEGOBatmanLotDK", "Content");
        CopyPackagePair(project.PlayableTemplate, project.TargetPackages.Playable, stageRoot);
        CopyPackagePair(project.CutsceneTemplate, project.TargetPackages.Cutscene, stageRoot);
        CopyPackagePair(project.DcmdTemplate, project.TargetPackages.Dcmd, stageRoot);

        // Reparent PoC: stage the donor archetype at its mod-local clone path so the
        // name-map patch pass can clone+rename it like any other package.
        var customArchetypePkg = UAssetPatchService.CustomArchetypePackage(project);
        if (customArchetypePkg is not null)
        {
            CopyArchetypeDonor(customArchetypePkg, stageRoot);
        }

        return stageRoot;
    }

    private static void CopyArchetypeDonor(string targetPackagePath, string stageContentRoot)
    {
        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var donorRel = GamePackageRelativePath(UAssetPatchService.DonorArchetypePackage);
        if (donorRel is null)
        {
            return;
        }
        var donorBase = Path.Combine(extractedRoot, donorRel);
        if (!File.Exists(donorBase + ".uasset"))
        {
            return; // donor not extracted — request will fail gracefully downstream
        }

        var targetRel = GamePackageRelativePath(targetPackagePath);
        if (targetRel is null)
        {
            return;
        }

        var targetBase = Path.Combine(stageContentRoot, targetRel);
        Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
        File.Copy(donorBase + ".uasset", targetBase + ".uasset", overwrite: true);
        if (File.Exists(donorBase + ".uexp"))
        {
            File.Copy(donorBase + ".uexp", targetBase + ".uexp", overwrite: true);
        }
        if (File.Exists(donorBase + ".ubulk"))
        {
            File.Copy(donorBase + ".ubulk", targetBase + ".ubulk", overwrite: true);
        }
    }

    private static void CopyPackagePair(TemplateRecord? record, string targetPackagePath, string stageContentRoot)
    {
        if (record is null)
        {
            return;
        }

        targetPackagePath = UnrealPathUtil.NormalizePackagePath(targetPackagePath);
        var targetRel = GamePackageRelativePath(targetPackagePath);
        if (targetRel is null)
        {
            throw new InvalidOperationException(
                $"Target package path for {record.Role} must start with /Game/. Current value: '{targetPackagePath}'.");
        }

        var targetBase = Path.Combine(stageContentRoot, targetRel);
        Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
        File.Copy(record.Uasset, targetBase + ".uasset", overwrite: true);
        if (!string.IsNullOrWhiteSpace(record.Uexp) && File.Exists(record.Uexp))
        {
            File.Copy(record.Uexp, targetBase + ".uexp", overwrite: true);
        }
        if (!string.IsNullOrWhiteSpace(record.Ubulk) && File.Exists(record.Ubulk))
        {
            File.Copy(record.Ubulk, targetBase + ".ubulk", overwrite: true);
        }
    }

    private static string? GamePackageRelativePath(string? packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (string.IsNullOrWhiteSpace(normalized) ||
            !normalized.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
