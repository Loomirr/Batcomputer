using System.Text.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace Batcomputer;

/// <summary>
/// Lists the characters the 3D viewer can load: the game's playable and cutscene blueprints, plus
/// the user's own suit projects.
///
/// Scanning the paks means walking ~355k mounted entries, so the result is cached to disk and only
/// rebuilt when asked - the viewer should open instantly on the second run.
/// </summary>
internal static class CharacterCatalogService
{
    /// <summary>Where a character came from, which is also how the viewer groups them.</summary>
    public enum Source
    {
        CustomSuit,
        Playable,
        Cutscene,
    }

    /// <param name="Name">Display name, e.g. "Batman 1989".</param>
    /// <param name="ObjectPath">Blueprint path to hand to the preview builder (empty for custom suits).</param>
    /// <param name="ProjectPath">Suit project json, for custom suits.</param>
    public sealed record Entry(string Name, Source Origin, string ObjectPath, string? ProjectPath = null);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string CachePath =>
        Path.Combine(AppSettings.CacheRoot, "characters.json");

    private static string LegacyCachePath =>
        Path.Combine(AppSettings.ToolRoot, "Batcomputer.characters.json");

    /// <summary>Loads the cached catalogue, or scans the paks when there is no cache yet.</summary>
    public static List<Entry> Load(string paksDir, string usmapPath, bool forceRescan = false)
    {
        if (!forceRescan && TryReadCache() is { Count: > 0 } cached)
        {
            return cached;
        }
        var scanned = Scan(paksDir, usmapPath);
        try
        {
            Directory.CreateDirectory(AppSettings.CacheRoot);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(scanned, Json));
        }
        catch
        {
            // A read-only install just means we rescan next time.
        }
        return scanned;
    }

    private static List<Entry>? TryReadCache()
    {
        try
        {
            var path = File.Exists(CachePath) ? CachePath : LegacyCachePath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks the mounted paks for character blueprints.</summary>
    private static List<Entry> Scan(string paksDir, string usmapPath)
    {
        var found = new List<Entry>();
        if (!Directory.Exists(paksDir) || !File.Exists(usmapPath))
        {
            return found;
        }

        var provider = new DefaultFileProvider(
            paksDir, SearchOption.AllDirectories,
            versions: new VersionContainer(EGame.GAME_UE5_6),
            pathComparer: StringComparer.OrdinalIgnoreCase);
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(new string('0', 64)));

        foreach (var key in provider.Files.Keys)
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var file = Path.GetFileNameWithoutExtension(key);
            if (!file.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only character blueprints - the game also ships BP_ assets for props, UI and gameplay.
            if (!key.Contains("/Characters/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isPlayable = file.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase);
            var isCutscene = file.Contains("Cutscene", StringComparison.OrdinalIgnoreCase)
                             || file.Contains("_CUT", StringComparison.OrdinalIgnoreCase);
            if (!isPlayable && !isCutscene)
            {
                continue;
            }

            found.Add(new Entry(
                Pretty(file),
                isPlayable ? Source.Playable : Source.Cutscene,
                key[..^".uasset".Length]));
        }

        return found
            .GroupBy(e => e.ObjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"BP_Batman_TheBatman2025_Playable" -> "Batman TheBatman2025".</summary>
    private static string Pretty(string file)
    {
        var s = file;
        if (s.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..];
        }
        foreach (var suffix in new[] { "_Playable", "_Default_Cutscene", "_Cutscene", "_Default_Batcave", "_CUT" })
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^suffix.Length];
                break;
            }
        }
        return s.Replace('_', ' ').Trim();
    }

    /// <summary>The user's own suit projects, read from the project root.</summary>
    public static List<Entry> CustomSuits(string projectRoot)
    {
        try
        {
            // Saved projects belong to SuitProjectService's workspace.
            return new SuitProjectService(projectRoot)
                .ListProjects()
                .Select(project => new Entry(
                    project.DisplayName,
                    Source.CustomSuit,
                    string.Empty,
                    project.Path))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Missing or unreadable project root - just show nothing under custom suits.
            return new List<Entry>();
        }
    }
}
