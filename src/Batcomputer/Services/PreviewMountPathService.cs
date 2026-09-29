using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;

namespace Batcomputer;

/// <summary>Preserves preview file identities for loose content and observed Game Feature mounts.</summary>
internal static class PreviewMountPathService
{
    internal const string GameContentFilePrefix = "LEGOBatmanLotDK/Content/";

    internal static Dictionary<string, GameFile> LooseContentFiles(
        DirectoryInfo directory, IEnumerable<KeyValuePair<string, GameFile>> files, VersionContainer versions)
    {
        bool canonical = directory.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) &&
            directory.Parent?.Name.Equals("LEGOBatmanLotDK", StringComparison.OrdinalIgnoreCase) == true;
        var mounted = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, original) in files)
        {
            if (canonical)
            {
                // These files already carry the correct mount identity, including any split payloads.
                if (key.StartsWith(GameContentFilePrefix, StringComparison.OrdinalIgnoreCase))
                    mounted.Add(key, original);
                continue;
            }

            // A dictionary alias alone leaves GameFile.Path unchanged. CUE looks up .uexp/.ubulk
            // using that path, so remount the OS file itself while retaining its real disk source.
            // Register companions the same way; avoid global raw-path aliases that can collide
            // between separate loose roots or with native/plugin files.
            if (original is not OsGameFile os)
                throw new InvalidDataException($"Loose preview content contains a non-OS file: {key}");
            var path = GameContentFilePrefix + key.TrimStart('/', '\\').Replace('\\', '/');
            var file = new MountedLooseFile(os, path, versions);
            mounted.Add(file.Path, file);
        }
        return mounted;
    }

    private sealed class MountedLooseFile : OsGameFile
    {
        internal MountedLooseFile(OsGameFile source, string mountedPath, VersionContainer versions)
            : base(source.ActualFile, versions)
        {
            Path = mountedPath;
        }
    }

    internal static IReadOnlyDictionary<string, string> GameFeatureMounts(IEnumerable<string> filePaths)
    {
        const string prefix = "LEGOBatmanLotDK/Plugins/GameFeatures/";
        var mounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in filePaths)
        {
            var path = raw.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = path[prefix.Length..];
            var separator = relative.IndexOf('/');
            if (separator <= 0 || !relative[(separator + 1)..].StartsWith("Content/", StringComparison.OrdinalIgnoreCase)) continue;
            var plugin = relative[..separator];
            if (!UnrealPathUtil.IsValidIdentifier(plugin) || plugin.Equals("Game", StringComparison.OrdinalIgnoreCase) ||
                plugin.Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
            mounts.TryAdd(plugin, prefix + plugin);
        }
        return mounts;
    }

    internal static void RegisterGameFeatureMounts(DefaultFileProvider provider)
    {
        // /DLC_ArkhamPack/... must resolve to the file's original mounted path. An alias in
        // Files alone is insufficient for split payloads and dependencies loaded by CUE.
        foreach (var (plugin, mount) in GameFeatureMounts(provider.Files.Keys))
            if (!provider.VirtualPaths.ContainsKey(plugin)) provider.VirtualPaths.Add(plugin, mount);
    }
}
