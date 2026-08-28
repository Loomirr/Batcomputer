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

    public sealed record ProjectSummary(
        string SlotId,
        string DisplayName,
        string Path,
        DateTime Modified,
        string CoverImagePath,
        string TargetPlayablePath);

    /// <summary>Lists saved suit projects (newest first).</summary>
    public IReadOnlyList<ProjectSummary> ListProjects()
    {
        return ListProjectFiles()
            .OrderByDescending(project => project.Modified)
            .GroupBy(project => string.IsNullOrWhiteSpace(project.TargetPlayablePath)
                    ? "slot:" + project.SlotId
                    : "target:" + project.TargetPlayablePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// Lists every readable project file without collapsing stale aliases. The normal
    /// project picker uses <see cref="ListProjects"/>; deletion uses this list so an
    /// older file cannot reappear after its newer replacement is removed.
    /// </summary>
    public IReadOnlyList<ProjectSummary> ListProjectFiles()
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
            var cover = "";
            var targetPlayable = "";
            try
            {
                var project = JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(path), JsonOptions);
                if (project is not null && string.IsNullOrWhiteSpace(project.DisplayName))
                {
                    continue;
                }
                if (project is not null)
                {
                    display = project.DisplayName;
                    slot = project.SlotId;
                    cover = project.CoverImagePath ?? "";
                    targetPlayable = UnrealPathUtil.NormalizePackagePath(project.TargetPackages?.Playable);
                }
            }
            catch
            {
                // Keep a corrupt file visible by filename so it can still be removed from Home.
            }
            results.Add(new ProjectSummary(slot, display, path, File.GetLastWriteTime(path), cover, targetPlayable));
        }

        return results;
    }

    /// <summary>Finds every saved alias that generates the same playable package.</summary>
    public IReadOnlyList<ProjectSummary> FindProjectAliases(NativeSuitProject project)
    {
        var target = UnrealPathUtil.NormalizePackagePath(project.TargetPackages?.Playable);
        if (string.IsNullOrWhiteSpace(target))
        {
            return Array.Empty<ProjectSummary>();
        }

        return ListProjectFiles()
            .Where(summary => target.Equals(summary.TargetPlayablePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public NativeSuitProject? LoadProject(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var project = JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(path), JsonOptions);
        if (project is not null && RefreshSavedTemplateSources(
                project,
                AppSettings.Current.EffectiveExtractedContentRoot()))
        {
            // Absolute extract paths are local cache details, not part of a suit's identity. When
            // an old dump has been replaced, keep the same /Game packages and migrate only their
            // disk locations so opening the suit does not require a manual JSON repair.
            try
            {
                AtomicFileUtil.WriteAllText(path, JsonSerializer.Serialize(project, JsonOptions));
            }
            catch
            {
                // The in-memory project is already repaired for this session. A read-only folder
                // or brief file lock should not turn that successful migration into a load error.
            }
        }
        return project;
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
        var path = ProjectPathForSlot(project.SlotId);
        AtomicFileUtil.WriteAllText(path, JsonSerializer.Serialize(project, JsonOptions));
        return path;
    }

    public string ProjectPathForSlot(string slotId) =>
        Path.Combine(GuiOutputRoot, $"{MakeSafeFileName(slotId)}.native-suit-project.json");

    public void DeleteSavedProjectFile(string projectPath)
    {
        var root = Path.GetFullPath(GuiOutputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(projectPath);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".native-suit-project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refused to delete a project outside the tool's saved-project folder.");
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    public string SavePatchPlan(NativeSuitPatchPlan plan)
    {
        Directory.CreateDirectory(GuiOutputRoot);
        var safeSlot = MakeSafeFileName(plan.Project.SlotId);
        var path = Path.Combine(GuiOutputRoot, $"{safeSlot}.patch-plan.json");
        AtomicFileUtil.WriteAllText(path, JsonSerializer.Serialize(plan, JsonOptions));
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
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!FileSystemPathUtil.IsWithinDirectory(fullProjectPath, GuiOutputRoot) ||
            !fullProjectPath.EndsWith(".native-suit-project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refused to delete a project outside the tool's saved-project folder.");
        }

        if (File.Exists(fullProjectPath))
        {
            File.Delete(fullProjectPath);
        }

        var projectDir = Path.GetFullPath(ProjectOutputDirectory(project));
        if (FileSystemPathUtil.IsWithinDirectory(projectDir, GuiOutputRoot) && Directory.Exists(projectDir))
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    public string CreateUnpatchedStage(NativeSuitProject project)
    {
        var stageRoot = Path.Combine(ProjectOutputDirectory(project), "UnpatchedStage", "LEGOBatmanLotDK", "Content");
        CopyPackagePair(EffectiveCharacterTemplate(project, playable: true), project.TargetPackages.Playable, stageRoot);
        CopyPackagePair(EffectiveCharacterTemplate(project, playable: false), project.TargetPackages.Cutscene, stageRoot);
        CopyPackagePair(project.DcmdTemplate, project.TargetPackages.Dcmd, stageRoot);

        // Stage the donor archetype for the name-map clone pass.
        var customArchetypePkg = UAssetPatchService.CustomArchetypePackage(project);
        if (customArchetypePkg is not null)
        {
            CopyArchetypeDonor(
                UAssetPatchService.StageArchetypeDonorPackage(project),
                customArchetypePkg,
                stageRoot);
        }

        return stageRoot;
    }

    private static TemplateRecord? EffectiveCharacterTemplate(NativeSuitProject project, bool playable)
    {
        var fallback = playable ? project.PlayableTemplate : project.CutsceneTemplate;
        if (!GliderService.TryGetAuthoredPairedCapeShell(
                project,
                out var shellPlayable,
                out var shellCutscene,
                out var shellDetail))
        {
            if (project.PairedCapeAdapter is not null)
            {
                throw new InvalidOperationException(
                    "The declared paired-cape adapter could not resolve its certified authored scaffold. " +
                    "Batcomputer refused to stage the glide-only base as a fallback because its cooked component layout cannot safely host the Cape + Torso pair. " +
                    shellDetail);
            }
            return fallback;
        }

        var package = playable ? shellPlayable : shellCutscene;
        if (UnrealPathUtil.NormalizePackagePath(fallback?.PackagePath ?? "").Equals(
                package,
                StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var sourceBase = ExtractedPackagePathService.ResolvePackageBase(extractedRoot, package)
            ?? throw new InvalidOperationException(
                $"Authored paired-cape shell is not available in the active game or installed DLC extract: '{package}'.");
        var uasset = sourceBase + ".uasset";
        if (!File.Exists(uasset))
        {
            throw new FileNotFoundException(
                $"The authored paired-cape { (playable ? "playable" : "cutscene") } shell is not present in the active extract.",
                uasset);
        }

        var uexp = sourceBase + ".uexp";
        var ubulk = sourceBase + ".ubulk";
        return new TemplateRecord
        {
            PackagePath = package,
            ContentRelative = ExtractedPackagePathService.ContentRelativeFromFile(extractedRoot, uasset) ?? "",
            Stem = UnrealPathUtil.AssetName(package),
            Character = fallback?.Character ?? "",
            Role = playable ? "playable" : "cutscene",
            Uasset = uasset,
            Uexp = File.Exists(uexp) ? uexp : null,
            Ubulk = File.Exists(ubulk) ? ubulk : null,
            UassetLength = new FileInfo(uasset).Length,
            UexpLength = File.Exists(uexp) ? new FileInfo(uexp).Length : 0,
            HasSplitPair = File.Exists(uexp),
            HasPair = File.Exists(uexp),
        };
    }

    private static void CopyArchetypeDonor(
        string sourcePackagePath,
        string targetPackagePath,
        string stageContentRoot)
    {
        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var donorBase = ExtractedPackagePathService.ResolvePackageBase(extractedRoot, sourcePackagePath);
        if (string.IsNullOrWhiteSpace(donorBase))
        {
            return;
        }
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

        if (!TryRefreshTemplateSource(
                record,
                AppSettings.Current.EffectiveExtractedContentRoot(),
                out _))
        {
            var role = string.IsNullOrWhiteSpace(record.Role) ? "base" : record.Role;
            var package = UnrealPathUtil.NormalizePackagePath(record.PackagePath);
            var identity = string.IsNullOrWhiteSpace(package)
                ? (string.IsNullOrWhiteSpace(record.ContentRelative) ? record.Stem : record.ContentRelative)
                : package;
            throw new FileNotFoundException(
                $"The saved {role} package '{identity}' is not present in the active extracted Content folder. " +
                "Refresh character assets, then open Base and re-select this suit's visual base and gameplay donor. " +
                "The saved project has not been replaced.");
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

    /// <summary>
    /// Moves saved template records from a retired extract folder to the currently configured
    /// Content root. The Unreal package path remains authoritative, so this never guesses a
    /// different character merely because its old absolute path disappeared.
    /// </summary>
    private static bool RefreshSavedTemplateSources(NativeSuitProject project, string activeContentRoot)
    {
        var changed = false;
        foreach (var record in new[]
                 {
                     project.PlayableTemplate,
                     project.CutsceneTemplate,
                     project.DcmdTemplate,
                     project.VisualSourceTemplate,
                     project.VisualCutsceneSourceTemplate,
                     project.StaticMeshComponentShapeTemplate,
                 })
        {
            if (record is not null && TryRefreshTemplateSource(record, activeContentRoot, out var recordChanged))
            {
                changed |= recordChanged;
            }
        }
        return changed;
    }

    internal static bool RefreshTemplateSourceForTest(TemplateRecord record, string activeContentRoot) =>
        TryRefreshTemplateSource(record, activeContentRoot, out _);

    private static bool TryRefreshTemplateSource(
        TemplateRecord record,
        string activeContentRoot,
        out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(activeContentRoot) || !Directory.Exists(activeContentRoot))
        {
            return !string.IsNullOrWhiteSpace(record.Uasset) && File.Exists(record.Uasset);
        }

        var contentRoot = Path.GetFullPath(activeContentRoot);
        var savedPackage = UnrealPathUtil.NormalizePackagePath(record.PackagePath);
        var sourceBase = ExtractedPackagePathService.ResolvePackageBase(contentRoot, savedPackage);
        if (string.IsNullOrWhiteSpace(sourceBase))
        {
            // ContentRelative predates mount-aware records. It is safe only when the record has no
            // package identity (legacy) or explicitly belongs to /Game. Falling back by this
            // mountless relative path for a missing Game Feature could silently bind the suit to a
            // different base-game asset with the same Characters/... path.
            if (!string.IsNullOrWhiteSpace(savedPackage) &&
                !savedPackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var relative = NormalizeContentRelative(record.ContentRelative) ??
                           ContentRelativeFromSavedPath(record.Uasset);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return false;
            }

            sourceBase = Path.GetFullPath(Path.Combine(contentRoot, relative));
            if (!FileSystemPathUtil.IsWithinDirectory(sourceBase, contentRoot))
            {
                return false;
            }
        }

        var uasset = sourceBase + ".uasset";
        if (!File.Exists(uasset))
        {
            return false;
        }

        var uexp = sourceBase + ".uexp";
        var ubulk = sourceBase + ".ubulk";
        var normalizedRelative = ExtractedPackagePathService.ContentRelativeFromFile(contentRoot, uasset) ?? "";
        var package = ExtractedPackagePathService.PackagePathFromFile(contentRoot, uasset);
        if (string.IsNullOrWhiteSpace(package))
        {
            return false;
        }
        var resolvedUexp = File.Exists(uexp) ? uexp : null;
        var resolvedUbulk = File.Exists(ubulk) ? ubulk : null;

        changed = !string.Equals(record.Uasset, uasset, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.Uexp, resolvedUexp, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.Ubulk, resolvedUbulk, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.PackagePath, package, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.ContentRelative, normalizedRelative, StringComparison.OrdinalIgnoreCase);

        record.Uasset = uasset;
        record.Uexp = resolvedUexp;
        record.Ubulk = resolvedUbulk;
        record.PackagePath = package;
        record.ContentRelative = normalizedRelative;
        record.Stem = Path.GetFileName(sourceBase);
        record.UassetLength = new FileInfo(uasset).Length;
        record.UexpLength = resolvedUexp is null ? 0 : new FileInfo(resolvedUexp).Length;
        record.HasSplitPair = resolvedUexp is not null;
        record.HasPair = resolvedUexp is not null;
        return true;
    }

    private static string? NormalizeContentRelative(string? contentRelative)
    {
        if (string.IsNullOrWhiteSpace(contentRelative))
        {
            return null;
        }

        var relative = contentRelative.Trim().Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative["Content/".Length..];
        }
        if (relative.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[..^".uasset".Length];
        }
        return string.IsNullOrWhiteSpace(relative)
            ? null
            : relative.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? ContentRelativeFromSavedPath(string? savedUasset)
    {
        if (string.IsNullOrWhiteSpace(savedUasset))
        {
            return null;
        }

        var normalized = savedUasset.Replace('\\', '/');
        var markerIndex = normalized.LastIndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }
        return NormalizeContentRelative(normalized[(markerIndex + "/Content/".Length)..]);
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
