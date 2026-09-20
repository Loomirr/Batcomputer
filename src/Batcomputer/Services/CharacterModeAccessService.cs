using System.Security.Cryptography;
using System.Text.Json;

namespace Batcomputer;

/// <summary>Character policies are consumed by LOTDKExpanded; content packs never ship its DLL.</summary>
internal static class CharacterModeAccessService
{
    internal const string PolicyName = "BatcomputerCharacterModes.ini";
    internal static bool RequiresModeRuntime(IEnumerable<NativeSuitProject> projects) => projects.Any(p =>
        p.CustomCharacter is { IsDefinition: true, ModeAvailability: not CharacterModeAvailability.Normal });

    internal static void ValidateInstalledRuntime(string? moduleDirectory = null)
    {
        var gameRoot = LotdkExpandedLayout.TryFindGameRoot(AppSettings.Current.EffectiveGamePaksRoot());
        moduleDirectory ??= gameRoot is null ? "" : Path.Combine(LotdkExpandedLayout.Ue4ssRoot(gameRoot), "Mods", "LOTDKExpanded");
        var dll = Path.Combine(moduleDirectory, "dlls", "main.dll");
        var receipt = Path.Combine(moduleDirectory, "capabilities.json");
        if (!File.Exists(dll) || !File.Exists(receipt))
            throw new InvalidDataException("Update LOTDK UE4SS/LOTDKExpanded: Mayhem/Both needs integrated character modes API 1. The standalone Mode Access helper is no longer packaged.");
        var capability = JsonSerializer.Deserialize<RuntimeCapability>(File.ReadAllText(receipt));
        if (capability?.CharacterModesApi != 1 ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).Equals(capability.DllSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LOTDKExpanded character-mode capability does not match its DLL. Reinstall the complete updated runtime.");
        if (File.Exists(Path.Combine(moduleDirectory, "..", "LOTDKModeAccess", "dlls", "main.dll")))
            throw new InvalidDataException("Move the old LOTDKModeAccess folder outside ue4ss/Mods and restart. Character modes are now integrated into LOTDKExpanded.");
    }
    internal sealed record RuntimeCapability(int CharacterModesApi, string DllSha256);

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
        if (!projects.Any(CustomCharacterProjectService.IsCharacter)) return;
        Directory.CreateDirectory(Path.Combine(pluginDirectory, "Config"));
        File.WriteAllText(Path.Combine(pluginDirectory, "Config", PolicyName), Render(projects));
    }

    internal static IReadOnlyList<(string Source, string Relative)> DependencyFiles(string outputRoot)
    {
        if (File.Exists(Path.Combine(outputRoot, "mode-access-dependency.json")) ||
            Directory.Exists(Path.Combine(outputRoot, "RuntimeDependencies", "LOTDKModeAccess")))
            throw new InvalidDataException("This old build bundles standalone Mode Access. Rebuild it with the current Batcomputer and updated LOTDKExpanded.");
        return [];
    }
}
