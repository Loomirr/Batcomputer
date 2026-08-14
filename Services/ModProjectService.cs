using System.Text;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Create / open / save / list / delete for <see cref="NativeSuitModProject"/>.
/// Mirrors <see cref="SuitProjectService"/> but for the mod-level composition object.
/// A mod references suit projects by relative path; it never copies their authoring
/// data. Everything a mod produces at build time derives from <c>ModId</c>.
/// </summary>
public sealed class ModProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public sealed record ModSummary(string ModId, string DisplayName, string Path, DateTime Modified, string CoverImagePath, int SuitCount);
    public sealed record ModIdChangeResult(
        string PreviousModId,
        string ModId,
        string ProjectPath,
        string ArchivedBuildPath);

    public string ProjectRoot { get; }
    public string ModOutputRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitModProjects");

    public ModProjectService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    /// <summary>
    /// Derives a stable, filesystem- and Unreal-package-safe Mod ID from a display
    /// name: keep [A-Za-z0-9], drop everything else, ensure it does not start with a
    /// digit. e.g. "My Batman Pack!" -> "MyBatmanPack".
    /// </summary>
    public static string DeriveModId(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "";
        }

        var sb = new StringBuilder(displayName.Length);
        foreach (var c in displayName)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
            }
        }

        var id = sb.ToString();
        if (id.Length > 0 && id[0] >= '0' && id[0] <= '9')
        {
            id = "M" + id;
        }
        return id;
    }

    /// <summary>Fills the derived, ModId-keyed fields. Call after ModId is set/edited.</summary>
    public static void ApplyDerivedFields(NativeSuitModProject mod)
    {
        mod.PackageBaseName = $"{mod.ModId}_P";
        mod.ContentRoot = $"/Game/Mods/{mod.ModId}";
        mod.StringTablePackage = $"/Game/Mods/{mod.ModId}/Localization/ST_{mod.ModId}.ST_{mod.ModId}";
    }

    /// <summary>Lists saved mod projects (newest first).</summary>
    public IReadOnlyList<ModSummary> ListMods()
    {
        var results = new List<ModSummary>();
        if (!Directory.Exists(ModOutputRoot))
        {
            return results;
        }

        foreach (var path in Directory.EnumerateFiles(ModOutputRoot, "*.native-suit-mod-project.json"))
        {
            var modId = Path.GetFileName(path).Replace(".native-suit-mod-project.json", "");
            var display = modId;
            var cover = "";
            var suitCount = 0;
            try
            {
                var mod = JsonSerializer.Deserialize<NativeSuitModProject>(File.ReadAllText(path), JsonOptions);
                if (mod is not null)
                {
                    // Skip nameless placeholder mods (accidental save before naming).
                    if (string.IsNullOrWhiteSpace(mod.DisplayName) && string.IsNullOrWhiteSpace(mod.ModId))
                    {
                        continue;
                    }
                    display = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.ModId : mod.DisplayName;
                    modId = string.IsNullOrWhiteSpace(mod.ModId) ? modId : mod.ModId;
                    cover = mod.CoverImagePath ?? "";
                    suitCount = mod.Suits?.Count ?? 0;
                }
            }
            catch
            {
                // List unreadable files by filename rather than dropping them.
            }
            results.Add(new ModSummary(modId, display, path, File.GetLastWriteTime(path), cover, suitCount));
        }

        return results.OrderByDescending(m => m.Modified).ToList();
    }

    public NativeSuitModProject? LoadMod(string path)
    {
        if (!File.Exists(path)) return null;
        var mod = JsonSerializer.Deserialize<NativeSuitModProject>(File.ReadAllText(path), JsonOptions);
        if (mod is not null) ApplyDerivedFields(mod);
        return mod;
    }

    public string SaveMod(NativeSuitModProject mod)
    {
        Directory.CreateDirectory(ModOutputRoot);
        ApplyDerivedFields(mod);
        var safe = DeriveModId(mod.ModId);
        if (string.IsNullOrWhiteSpace(safe))
        {
            throw new InvalidOperationException("Mod ID is empty or has no valid characters.");
        }
        var path = Path.Combine(ModOutputRoot, $"{safe}.native-suit-mod-project.json");
        File.WriteAllText(path, JsonSerializer.Serialize(mod, JsonOptions));
        return path;
    }

    /// <summary>
    /// Changes a mod's technical ID as one authoring transaction. The project
    /// filename and every derived identity field move to the new ID. Existing
    /// build output is deliberately archived, not renamed: cooked StringTable,
    /// registry and package internals still contain the old ID and must be rebuilt.
    /// Suit-owned package roots are references and are intentionally unchanged.
    /// </summary>
    public ModIdChangeResult ChangeModId(string currentProjectPath, string requestedModId)
    {
        var sourcePath = Path.GetFullPath(currentProjectPath);
        var projectRoot = Path.GetFullPath(ModOutputRoot);
        var projectRootPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!sourcePath.StartsWith(projectRootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !sourcePath.EndsWith(".native-suit-mod-project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected file is not a saved Batcomputer mod project.");
        }

        var mod = LoadMod(sourcePath)
            ?? throw new InvalidOperationException("The selected mod project could not be loaded.");
        var previousModId = mod.ModId?.Trim() ?? "";
        var newModId = DeriveModId(requestedModId);
        if (string.IsNullOrWhiteSpace(newModId))
        {
            throw new InvalidOperationException("The new Mod ID is empty or has no valid characters.");
        }
        if (string.Equals(previousModId, newModId, StringComparison.Ordinal))
        {
            return new ModIdChangeResult(previousModId, newModId, sourcePath, "");
        }

        var destinationPath = Path.Combine(projectRoot, $"{newModId}.native-suit-mod-project.json");
        var destinationIsSource = string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
        if (!destinationIsSource && File.Exists(destinationPath))
        {
            throw new InvalidOperationException($"A saved mod project already uses the ID '{newModId}'.");
        }
        if (ListMods().Any(summary =>
                !string.Equals(summary.Path, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(summary.ModId, newModId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Another saved mod already uses the ID '{newModId}'.");
        }

        mod.PreviousModIds ??= new List<string>();
        if (!string.IsNullOrWhiteSpace(previousModId) &&
            !mod.PreviousModIds.Contains(previousModId, StringComparer.OrdinalIgnoreCase))
        {
            mod.PreviousModIds.Add(previousModId);
        }
        mod.PreviousModIds = mod.PreviousModIds
            .Where(id => !string.IsNullOrWhiteSpace(id) &&
                         !string.Equals(id, newModId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        mod.ModId = newModId;
        ApplyDerivedFields(mod);

        Directory.CreateDirectory(projectRoot);
        var temporaryPath = Path.Combine(projectRoot, $".{newModId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(mod, JsonOptions));

        string archivedBuildPath = "";
        var destinationCreated = false;
        var buildsRoot = Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitModBuilds");
        var oldBuildPath = Path.Combine(buildsRoot, previousModId);
        try
        {
            if (!string.IsNullOrWhiteSpace(previousModId) &&
                !string.Equals(previousModId, newModId, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(oldBuildPath))
            {
                var archiveRoot = Path.Combine(buildsRoot, "_ModIdBackups");
                Directory.CreateDirectory(archiveRoot);
                archivedBuildPath = Path.Combine(
                    archiveRoot,
                    $"{previousModId}_to_{newModId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
                var suffix = 1;
                while (Directory.Exists(archivedBuildPath))
                {
                    archivedBuildPath = Path.Combine(
                        archiveRoot,
                        $"{previousModId}_to_{newModId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{suffix++}");
                }
                Directory.Move(oldBuildPath, archivedBuildPath);
            }

            if (destinationIsSource)
            {
                var intermediatePath = sourcePath + ".id-change";
                File.Move(sourcePath, intermediatePath, overwrite: true);
                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    File.Delete(intermediatePath);
                }
                catch
                {
                    if (File.Exists(intermediatePath) && !File.Exists(sourcePath))
                    {
                        File.Move(intermediatePath, sourcePath);
                    }
                    throw;
                }
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
                destinationCreated = true;
                File.Delete(sourcePath);
            }

            return new ModIdChangeResult(previousModId, newModId, destinationPath, archivedBuildPath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (!destinationIsSource && destinationCreated &&
                File.Exists(sourcePath) && File.Exists(destinationPath))
            {
                // The new JSON was written but the old one could not be removed.
                // Keep the original project as the source of truth and remove the
                // duplicate new-ID project before reporting the failed migration.
                File.Delete(destinationPath);
            }
            if (!string.IsNullOrWhiteSpace(archivedBuildPath) &&
                Directory.Exists(archivedBuildPath) && !Directory.Exists(oldBuildPath))
            {
                Directory.Move(archivedBuildPath, oldBuildPath);
            }
            throw;
        }
    }

    /// <summary>Deletes the mod project JSON only. Never touches referenced suit projects.</summary>
    public void DeleteMod(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Resolves a mod entry's suit-project path to an absolute path. Entries store a
    /// path relative to the project root for portability; a legacy absolute path is
    /// returned as-is.
    /// </summary>
    public string ResolveSuitProjectPath(ModSuitEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SuitProjectPath))
        {
            return "";
        }
        return Path.IsPathRooted(entry.SuitProjectPath)
            ? entry.SuitProjectPath
            : Path.Combine(ProjectRoot, entry.SuitProjectPath);
    }

    /// <summary>Stores a suit-project path relative to the project root when possible.</summary>
    public string MakeRelativeSuitProjectPath(string absoluteSuitProjectPath)
    {
        try
        {
            var rel = Path.GetRelativePath(ProjectRoot, absoluteSuitProjectPath);
            // GetRelativePath returns the input unchanged if it can't relativize
            // (e.g. different drive) - keep the absolute path in that case.
            return rel.StartsWith("..", StringComparison.Ordinal) ? absoluteSuitProjectPath : rel;
        }
        catch
        {
            return absoluteSuitProjectPath;
        }
    }

    /// <summary>Updates mod entries after a suit receives a new derived slot ID.</summary>
    public int RelinkSuitReferences(
        string previousSuitId,
        string previousProjectPath,
        NativeSuitProject currentSuit,
        string currentProjectPath)
    {
        var updated = 0;
        var suitService = new SuitProjectService(ProjectRoot);
        foreach (var summary in ListMods())
        {
            var mod = LoadMod(summary.Path);
            if (mod is null)
            {
                continue;
            }

            var direct = mod.Suits.Where(entry =>
                entry.SuitId.Equals(previousSuitId, StringComparison.OrdinalIgnoreCase) ||
                ResolveSuitProjectPath(entry).Equals(previousProjectPath, StringComparison.OrdinalIgnoreCase)).ToList();
            var entries = direct;
            if (entries.Count == 0)
            {
                var staleSameName = mod.Suits.Where(entry =>
                {
                    var saved = suitService.LoadProject(ResolveSuitProjectPath(entry));
                    return saved is not null &&
                           saved.DisplayName.Equals(currentSuit.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                           (string.IsNullOrWhiteSpace(saved.PawnTag) || !BaseEligibilityService.Evaluate(saved).IsReady);
                }).ToList();
                if (staleSameName.Count == 1)
                {
                    entries = staleSameName;
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                entry.SuitId = currentSuit.SlotId;
                entry.SuitProjectPath = MakeRelativeSuitProjectPath(currentProjectPath);
                updated++;
            }
            SaveMod(mod);
        }
        return updated;
    }

    /// <summary>Removes every mod entry that points at a suit being deleted from the tool.</summary>
    public int RemoveSuitReferences(NativeSuitProject deletedSuit, IEnumerable<string> deletedProjectPaths)
    {
        var deletedPaths = new HashSet<string>(
            deletedProjectPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeProjectPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var deletedSuitId = deletedSuit.SlotId?.Trim() ?? "";
        var removed = 0;

        foreach (var summary in ListMods())
        {
            var mod = LoadMod(summary.Path);
            if (mod?.Suits is null || mod.Suits.Count == 0)
            {
                continue;
            }

            var removedHere = mod.Suits.RemoveAll(entry =>
            {
                var sameId = !string.IsNullOrWhiteSpace(deletedSuitId) &&
                             string.Equals(entry.SuitId, deletedSuitId, StringComparison.OrdinalIgnoreCase);
                var samePath = deletedPaths.Contains(NormalizeProjectPath(ResolveSuitProjectPath(entry)));
                return sameId || samePath;
            });
            if (removedHere == 0)
            {
                continue;
            }

            SaveMod(mod);
            removed += removedHere;
        }

        return removed;
    }

    private static string NormalizeProjectPath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
        }
        catch
        {
            return path ?? "";
        }
    }
}
