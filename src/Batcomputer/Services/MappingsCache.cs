using System.Collections.Concurrent;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Loads a .usmap mappings file once per path/file revision and caches it for reuse.
///
/// UAssetAPI's <c>new Usmap(path)</c> opens the file, and the graft/rebuild/material flows
/// re-loaded the same Dinner.usmap dozens of times - often from concurrent Task.Run threads
/// (a declarative part rebuild overlapping a material re-apply, etc.). Concurrent opens raced
/// on the file handle and threw "The process cannot access the file … because it is being used
/// by another process." Mappings are immutable read-only data after load, so a single shared
/// instance is safe to reuse across every UAsset load, and caching removes the repeated file I/O
/// entirely (also faster). The lock serializes the first load per path so our own threads never
/// collide.
/// </summary>
internal static class MappingsCache
{
    private sealed record Entry(Usmap Mappings, long Length, long WriteTicks, string ContentRoot);
    private static readonly ConcurrentDictionary<string, Entry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoadLock = new();

    /// <summary>Returns cached mappings, reloading when an updated dump replaces the file.</summary>
    public static Usmap Load(string path)
    {
        path = Path.GetFullPath(path);
        // UAssetAPI can augment a dump with schemas loaded from native Blueprints.
        // Those schemas belong to the extraction, not just to the .usmap filename.
        var contentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var info = new FileInfo(path);
        if (Cache.TryGetValue(path, out var cached) && info.Exists && cached.Length == info.Length && cached.WriteTicks == info.LastWriteTimeUtc.Ticks &&
            cached.ContentRoot.Equals(contentRoot, StringComparison.OrdinalIgnoreCase))
        {
            return cached.Mappings;
        }
        lock (LoadLock)
        {
            info.Refresh();
            if (Cache.TryGetValue(path, out cached) && info.Exists && cached.Length == info.Length && cached.WriteTicks == info.LastWriteTimeUtc.Ticks &&
                cached.ContentRoot.Equals(contentRoot, StringComparison.OrdinalIgnoreCase))
            {
                return cached.Mappings;
            }
            var usmap = new Usmap(path);
            Cache[path] = new Entry(usmap, info.Length, info.LastWriteTimeUtc.Ticks, contentRoot);
            return usmap;
        }
    }
}
