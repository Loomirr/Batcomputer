using System.Collections.Concurrent;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Loads a .usmap mappings file ONCE per path and caches the instance for reuse.
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
    private static readonly ConcurrentDictionary<string, Usmap> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoadLock = new();

    /// <summary>Returns the cached Usmap for <paramref name="path"/>, loading it once if needed.</summary>
    public static Usmap Load(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }
        lock (LoadLock)
        {
            if (Cache.TryGetValue(path, out cached))
            {
                return cached;
            }
            var usmap = new Usmap(path);
            Cache[path] = usmap;
            return usmap;
        }
    }
}
