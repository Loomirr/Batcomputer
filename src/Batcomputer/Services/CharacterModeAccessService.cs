using System.Security.Cryptography;
using System.Text.Json;

namespace Batcomputer;

/// <summary>Per-plugin policies merge in the optional native helper; never replace the game's villain container.</summary>
internal static class CharacterModeAccessService
{
    internal const string Module = "LOTDKModeAccess";
    internal const string PolicyName = "BatcomputerCharacterModes.ini";
    internal const string MarkerName = "mode-access-dependency.json";
    internal static string BundledHelper => Path.Combine(AppContext.BaseDirectory, "Data", "ModeAccess", "main.dll");
    internal static bool RequiresHelper(IEnumerable<NativeSuitProject> projects) => projects.Any(p =>
        p.CustomCharacter is { IsDefinition: true, ModeAvailability: not CharacterModeAvailability.Normal });

    internal static void ValidateBundledHelper(string? directory = null)
    {
        directory ??= Path.GetDirectoryName(BundledHelper)!;
        foreach (var name in new[] { "main.dll", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "README.md" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new FileNotFoundException($"Mayhem/Both requires a complete Mode Access helper (missing or empty {name}). Update/rebuild Batcomputer before building the mod.", path);
        }
    }

    internal static string Render(IEnumerable<NativeSuitProject> projects)
    {
        var definitions = projects.Where(CustomCharacterProjectService.IsCharacter).ToArray();
        var rows = new SortedDictionary<string, CharacterModeAvailability>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in definitions)
        {
            if (CustomCharacterProjectService.IdentityError(project) is { } error) throw new InvalidDataException(error);
            var id = project.CustomCharacter!;
            var tag = CustomCharacterProjectService.Scope(id);
            if (rows.TryGetValue(tag, out var old) && old != id.ModeAvailability)
                throw new InvalidDataException("Conflicting game modes for " + tag);
            rows[tag] = id.ModeAvailability;
        }
        return "[Batcomputer.CharacterModes.v1]\n" + string.Concat(rows.Select(p => p.Key + "=" + p.Value.ToString().ToLowerInvariant() + "\n"));
    }

    internal static void Stage(string outputRoot, string pluginDirectory, IReadOnlyList<NativeSuitProject> projects)
    {
        // Check all runtime/help/license inputs before writing an incomplete release policy.
        var needsHelper = RequiresHelper(projects);
        if (needsHelper) ValidateBundledHelper();
        if (projects.Any(CustomCharacterProjectService.IsCharacter))
        {
            Directory.CreateDirectory(Path.Combine(pluginDirectory, "Config"));
            File.WriteAllText(Path.Combine(pluginDirectory, "Config", PolicyName), Render(projects));
        }
        if (!needsHelper) return;
        var directory = Path.Combine(outputRoot, "RuntimeDependencies", Module);
        Directory.CreateDirectory(Path.Combine(directory, "dlls"));
        File.Copy(BundledHelper, Path.Combine(directory, "dlls", "main.dll"), true);
        foreach (var name in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "README.md" })
            File.Copy(Path.Combine(Path.GetDirectoryName(BundledHelper)!, name), Path.Combine(directory, name), true);
        File.WriteAllText(Path.Combine(directory, "enabled.txt"), "enabled\n");
        File.WriteAllText(Path.Combine(outputRoot, MarkerName), JsonSerializer.Serialize(new Dependency(1,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BundledHelper)))), new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Dependency(int Schema, string Sha256);
    internal static IReadOnlyList<(string Source, string Relative)> DependencyFiles(string outputRoot)
    {
        var marker = Path.Combine(outputRoot, MarkerName);
        var dir = Path.Combine(outputRoot, "RuntimeDependencies", Module);
        if (!File.Exists(marker))
        {
            // Do not silently omit a helper if its receipt was lost.
            if (Directory.Exists(dir)) throw new InvalidDataException("Mode Access dependency receipt is missing; rebuild the mod.");
            var policyFiles = Directory.Exists(outputRoot) ? Directory.EnumerateFiles(outputRoot, PolicyName, SearchOption.AllDirectories) : [];
            if (policyFiles.Any(p => File.ReadLines(p).Any(line => line.EndsWith("=mayhem") || line.EndsWith("=both"))))
                throw new InvalidDataException("This mod's game-mode policy needs a missing runtime dependency. Rebuild the mod.");
            return [];
        }
        var receipt = JsonSerializer.Deserialize<Dependency>(File.ReadAllText(marker));
        var dll = Path.Combine(dir, "dlls", "main.dll"); var enabled = Path.Combine(dir, "enabled.txt");
        if (receipt?.Schema != 1 || !File.Exists(dll) || !File.Exists(enabled) || new FileInfo(enabled).Length == 0 ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mode Access runtime dependency is incomplete or changed; rebuild the mod.");
        var files = new List<(string Source, string Relative)> {
            (dll, "Binaries/Win64/ue4ss/Mods/" + Module + "/dlls/main.dll"),
            (enabled, "Binaries/Win64/ue4ss/Mods/" + Module + "/enabled.txt") };
        foreach (var name in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "README.md" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) throw new FileNotFoundException("Mode Access license/help file missing; rebuild the mod.", path);
            files.Add((path, "Binaries/Win64/ue4ss/Mods/" + Module + "/" + name));
        }
        return files;
    }
}
