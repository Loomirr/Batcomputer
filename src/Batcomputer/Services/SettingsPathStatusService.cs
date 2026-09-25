using System.Text.Json;

namespace Batcomputer;

internal static class SettingsPathStatusService
{
    internal sealed record Status(bool Valid, bool Automatic, bool Pending, string Detail);
    internal static Status Check(string key, string entered, AppSettings settings)
    {
        var automatic = string.IsNullOrWhiteSpace(entered);
        var path = automatic ? key switch
        {
            "ProjectRoot" => settings.EffectiveProjectRoot(),
            "AssetExtractRoot" => settings.EffectiveAssetExtractRoot(),
            "ExportContentRoot" => settings.EffectiveExportContentRoot(),
            "ExtractedContentRoot" => settings.EffectiveExtractedContentRoot(),
            "GamePaksRoot" => settings.EffectiveGamePaksRoot(),
            "UnrealEngineRoot" => settings.EffectiveUnrealEngineRoot(),
            "UsmapPath" => settings.EffectiveUsmapPath(),
            "OodleRuntimeDllPath" => settings.EffectiveOodleRuntimeDllPath(),
            _ => entered
        } : entered;
        Status Result(bool valid, string reason, bool pending = false) => new(valid, automatic, pending,
            (automatic ? "Automatic: " : "Configured: ") + (path ?? "not available") + "\n" + reason);
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return Result(false, "No usable path was found.");
            path = Path.GetFullPath(path);
            if (key is "AssetExtractRoot" or "ProjectRoot")
            {
                if (File.Exists(path)) return Result(false, "This is a file, not an output folder.");
                if (Directory.Exists(path)) return Result(true, "Folder found. Write access is checked when saving.");
                var parent = Directory.GetParent(path);
                while (parent is not null && !parent.Exists) parent = parent.Parent;
                return parent is null ? Result(false, "The output drive or parent location is unavailable.") : Result(false, "Output folder will be created when needed; write access has not been tested.", true);
            }
            if (key == "ExportContentRoot" && automatic && !Directory.Exists(path))
                return Result(false, "No separate staging source is selected. Generated project stages are created during builds.", true);
            if (key is "UsmapPath" or "OodleRuntimeDllPath")
            {
                if (!File.Exists(path)) return Result(false, "File not found.");
                if (new FileInfo(path).Length == 0) return Result(false, "File is empty.");
                var extension = key == "UsmapPath" ? ".usmap" : ".dll";
                return Result(Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase),
                    "Expected " + extension + " file. Runtime compatibility is checked when the tool uses it.");
            }
            if (!Directory.Exists(path)) return Result(false, "Folder not found.");
            if (key == "UnrealEngineRoot")
            {
                var version = Path.Combine(path, "Engine", "Build", "Build.version");
                if (!File.Exists(version)) return Result(false, "Not an Unreal Engine installation: Engine/Build/Build.version is missing.");
                using var json = JsonDocument.Parse(File.ReadAllText(version));
                var compatible = json.RootElement.GetProperty("MajorVersion").GetInt32() == 5 && json.RootElement.GetProperty("MinorVersion").GetInt32() == 6;
                return Result(compatible && File.Exists(Path.Combine(path, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe")),
                    compatible ? "UE 5.6 detected; UnrealEditor-Cmd.exe is required." : "The registry writer requires Unreal Engine 5.6.");
            }
            if (key == "GamePaksRoot") return Result(File.Exists(Path.Combine(path, "global.utoc")) && Directory.EnumerateFiles(path, "pakchunk*.utoc").Any(), "Expected global.utoc and game pakchunk containers. DLC ownership is not determined by this indicator.");
            if (key == "ExtractedContentRoot") return Result(Directory.Exists(Path.Combine(path, "Characters")), "Select the extracted Content folder containing Characters. Extraction completeness is checked by each workflow.");
            return Result(true, "Folder found. This is a path check, not a successful build certificate.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException)
        { return Result(false, "Could not validate: " + ex.Message); }
    }
}
