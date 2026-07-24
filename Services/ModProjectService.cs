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
        return File.Exists(path)
            ? JsonSerializer.Deserialize<NativeSuitModProject>(File.ReadAllText(path), JsonOptions)
            : null;
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
}
