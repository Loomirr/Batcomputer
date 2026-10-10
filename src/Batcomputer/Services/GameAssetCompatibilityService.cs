using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Prevents silently building against donors from a different installed game revision.</summary>
internal static class GameAssetCompatibilityService
{
    internal const string ReceiptName = ".batcomputer-game-source.json";
    internal sealed record Source(string GameBuild, string ContainerFingerprint);
    internal sealed record Receipt(int Version, Source Source, string MappingsHash);

    internal static Source? Capture(string paksRoot)
    {
        if (!Directory.Exists(paksRoot)) return null;
        var gameRoot = Directory.GetParent(paksRoot)?.Parent?.FullName;
        if (gameRoot is null) return null;
        var exe = Path.Combine(gameRoot, "Binaries", "Win64", "LEGOBatmanLotDK-Win64-Shipping.exe");
        if (!File.Exists(exe)) return null;
        var build = FileVersionInfo.GetVersionInfo(exe).FileVersion?.Trim() ?? "";
        if (!Regex.IsMatch(build, @"^\d+$")) return null;
        var roots = new[] { paksRoot, Path.Combine(gameRoot, "Content", "DLC") };
        var files = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            .Where(path => Path.GetExtension(path).Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".ucas", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".pak", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var identity = new StringBuilder();
        foreach (var path in files)
        {
            var info = new FileInfo(path);
            identity.Append(Path.GetRelativePath(gameRoot, path).Replace('\\', '/').ToLowerInvariant())
                .Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            // TOCs identify package contents; do not read multi-GB bulk containers or author mods.
            if (info.Extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase))
                identity.Append('|').Append(Hash(path));
            identity.Append('\n');
        }
        return new Source(build, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))));
    }

    internal static string? MappingBuild(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), @"^Dinner-\d+\.\d+\.\d+-(\d+)\+\+\+", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static void RequireMatchingMapping(Source? source, string mappingPath)
    {
        if (source is not null && MappingBuild(mappingPath) is { } build && build != source.GameBuild)
            throw new InvalidDataException($"The installed game is build {source.GameBuild}, but the selected mappings are from build {build}. " +
                "Dump/select mappings from the current game, then run a fresh character extraction before rebuilding mods. " +
                "Old cloned Blueprints can crash the game even when packaging succeeds.");
    }

    internal static void Record(string contentRoot, Source source, string mappingsPath) =>
        File.WriteAllText(Path.Combine(contentRoot, ReceiptName),
            JsonSerializer.Serialize(new Receipt(1, source, Hash(mappingsPath)), new JsonSerializerOptions { WriteIndented = true }));

    internal static void Validate(Source? installed, string contentRoot, string mappingPath, ModReleaseValidationService.Result result)
    {
        try
        {
            RequireMatchingMapping(installed, mappingPath);
            var path = Path.Combine(contentRoot, ReceiptName);
            if (!File.Exists(path))
            {
                result.AddWarning("game compatibility", "This extraction predates game-build tracking. Its compatibility is unverified. After a game update, select current mappings and run a fresh character extraction; rebuild native-derived assets before installing.");
                return;
            }
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
            if (receipt is null || receipt.Version != 1 || receipt.Source is null || string.IsNullOrWhiteSpace(receipt.MappingsHash))
                throw new InvalidDataException("The game-extraction compatibility record is invalid. Run a fresh character extraction.");
            if (installed is not null && installed != receipt.Source)
                throw new InvalidDataException($"The game changed since this extraction (extracted build {receipt.Source.GameBuild}, installed build {installed.GameBuild}). " +
                    "Run a fresh character extraction with current mappings and rebuild native-derived assets. Existing authoring projects are preserved.");
            if (!File.Exists(mappingPath) || !Hash(mappingPath).Equals(receipt.MappingsHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected mappings differ from those used to validate this extraction. Select the matching mappings or run a fresh character extraction.");
            if (installed is null)
                result.AddWarning("game compatibility", "The extraction has a source record, but the installed game build could not be verified. Configure the game's original Content/Paks folder.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            result.AddError("game compatibility", ex.Message);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
