using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
/// Only the shipped top-level Paks and DLC containers are mounted; nested ~mods folders are
/// deliberately excluded. The result is cached to disk so the viewer opens instantly on later runs.
/// </summary>
internal static class CharacterCatalogService
{
    private const int CacheSchemaVersion = 5;

    /// <summary>Where a character came from, which is also how the viewer groups them.</summary>
    public enum Source
    {
        CustomSuit,
        Playable,
        Cutscene,
        Quest,
        Other,
    }

    /// <param name="Name">Display name, e.g. "Batman 1989".</param>
    /// <param name="ObjectPath">Blueprint path to hand to the preview builder (empty for custom suits).</param>
    /// <param name="ProjectPath">Suit project json, for custom suits.</param>
    public sealed record Entry(string Name, Source Origin, string ObjectPath, string? ProjectPath = null,
        bool IsCharacter = false, string ProjectId = "", string CharacterId = "");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record CacheDocument(
        int SchemaVersion,
        string ContainerSignature,
        List<Entry> Entries);

    private static string CachePath =>
        Path.Combine(AppSettings.CacheRoot, "characters.base-game.json");

    /// <summary>Loads the cached catalogue, or scans the paks when there is no cache yet.</summary>
    public static List<Entry> Load(string paksDir, string usmapPath, bool forceRescan = false)
    {
        var containerSignature = ContainerSignature(paksDir);
        if (!forceRescan && TryReadCache(containerSignature) is { Count: > 0 } cached)
        {
            return cached;
        }
        var scanned = Scan(paksDir, usmapPath);
        try
        {
            Directory.CreateDirectory(AppSettings.CacheRoot);
            File.WriteAllText(
                CachePath,
                JsonSerializer.Serialize(
                    new CacheDocument(CacheSchemaVersion, containerSignature, scanned),
                    Json));
        }
        catch
        {
            // A read-only install just means we rescan next time.
        }
        return scanned;
    }

    private static List<Entry>? TryReadCache(string expectedContainerSignature)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            // Earlier builds wrote a bare Entry array here. Deserializing only the versioned
            // envelope intentionally invalidates that catalogue, because it can never contain DLC.
            var cached = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(CachePath));
            return cached is
                {
                    SchemaVersion: CacheSchemaVersion,
                    Entries.Count: > 0,
                } && string.Equals(
                    cached.ContainerSignature,
                    expectedContainerSignature,
                    StringComparison.Ordinal)
                ? cached.Entries
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Keys the on-disk catalogue to this user's installed top-level game and DLC containers.
    /// Installing/removing a DLC therefore refreshes the viewer without requiring a manual cache
    /// delete, while files below Paks\~mods remain deliberately invisible to the signature.
    /// </summary>
    private static string ContainerSignature(string paksDir)
    {
        var identity = new StringBuilder();
        identity.Append("viewer-character-catalog|").Append(CacheSchemaVersion);
        foreach (var root in ContainerRoots(paksDir))
        {
            identity.Append('|').Append(root.FullName.ToUpperInvariant());
            if (!root.Exists)
            {
                identity.Append("|missing");
                continue;
            }

            foreach (var file in root.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                         .Where(IsContainerFile)
                         .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            {
                identity.Append('|')
                    .Append(file.Name.ToUpperInvariant()).Append(':')
                    .Append(file.Length).Append(':')
                    .Append(file.LastWriteTimeUtc.Ticks);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    /// <summary>Walks the mounted paks for character blueprints.</summary>
    private static List<Entry> Scan(string paksDir, string usmapPath)
    {
        var found = new List<Entry>();
        if (!Directory.Exists(paksDir) || !File.Exists(usmapPath))
        {
            return found;
        }

        using var provider = new DefaultFileProvider(
            new DirectoryInfo(paksDir),
            DlcContainerRoots(paksDir),
            BaseGamePakSource.ShippedContainerSearchOption,
            versions: new VersionContainer(EGame.GAME_UE5_6),
            pathComparer: StringComparer.OrdinalIgnoreCase);
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(new string('0', 64)));

        foreach (var key in provider.Files.Keys)
        {
            if (EntryFromPackageKey(key) is { } entry) found.Add(entry);
        }

        return found
            .GroupBy(e => e.ObjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static Entry? EntryFromPackageKey(string key)
    {
        key = key.Replace('\\', '/');
        if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
            !key.Contains("/Characters/", StringComparison.OrdinalIgnoreCase)) return null;
        var file = Path.GetFileNameWithoutExtension(key);
        if (!file.StartsWith("BP_", StringComparison.OrdinalIgnoreCase)) return null;
        if (!BaseEligibilityService.IsVisualCharacterPackage("/" + key.TrimStart('/'))) return null;
        Source? source = file.EndsWith("_Playable", StringComparison.OrdinalIgnoreCase) ? Source.Playable
            : file.EndsWith("_Quest", StringComparison.OrdinalIgnoreCase) ? Source.Quest
            : file.Contains("Cutscene", StringComparison.OrdinalIgnoreCase) || file.Contains("_CUT", StringComparison.OrdinalIgnoreCase)
                ? Source.Cutscene
                : new[] { "_Batcave", "_Boss", "_Goon", "_Civilian" }.Any(suffix => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    ? Source.Other : null;
        return source is { } origin ? new Entry(Pretty(file), origin, key[..^".uasset".Length]) : null;
    }

    internal static bool MatchesSearch(Entry entry, string query)
    {
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var aliases = entry.Name.Contains("TAS", StringComparison.OrdinalIgnoreCase) ? " animated series" : "";
        if (IsBatgirlAnimatedDefault(entry.ObjectPath)) aliases += " animated series the new batman adventures TNBA default";
        var text = Normalize(entry.Name + " " + entry.ObjectPath + " " + entry.ProjectId + " " + entry.CharacterId + aliases);
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(token => text.Contains(Normalize(token), StringComparison.Ordinal));
    }

    // Confirmed through Default's DCMD -> DA_UIMD_BatgirlAnimatedSeries and its
    // AnimatedSeries body/cowl/cape references. TASGrey is a separate variant.
    private static bool IsBatgirlAnimatedDefault(string path) =>
        path.Replace('\\', '/').Contains("/Characters/Minifig/Batgirl/BP_Batgirl_Default_", StringComparison.OrdinalIgnoreCase);

    private static DirectoryInfo[] DlcContainerRoots(string paksDir)
    {
        var roots = ContainerRoots(paksDir);
        return roots.Length > 1 && roots[1].Exists
            ? [roots[1]]
            : [];
    }

    private static DirectoryInfo[] ContainerRoots(string paksDir)
    {
        if (string.IsNullOrWhiteSpace(paksDir))
        {
            return [];
        }

        var paks = new DirectoryInfo(Path.GetFullPath(paksDir.Trim()));
        var dlc = new DirectoryInfo(GameAssetRefreshService.DlcRootForPaksRoot(paks.FullName));
        return [paks, dlc];
    }

    private static bool IsContainerFile(FileInfo file) =>
        file.Extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
        file.Extension.Equals(".ucas", StringComparison.OrdinalIgnoreCase) ||
        file.Extension.Equals(".pak", StringComparison.OrdinalIgnoreCase);

    /// <summary>"BP_Batman_TheBatman2025_Playable" -> "Batman TheBatman2025".</summary>
    private static string Pretty(string file)
    {
        if (new[] { "BP_Batgirl_Default_Playable", "BP_Batgirl_Default_Cutscene", "BP_Batgirl_Default_Batcave" }
            .Contains(file, StringComparer.OrdinalIgnoreCase))
            return "Batgirl Animated Series (Default)";
        var s = file;
        if (s.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..];
        }
        foreach (var suffix in new[] { "_Playable", "_Default_Cutscene", "_Cutscene", "_Default_Quest", "_Quest", "_Default_Batcave", "_Batcave", "_Boss", "_Goon", "_Civilian", "_CUT" })
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
                    project.Path, project.IsCharacter, project.SlotId, project.CharacterId))
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
