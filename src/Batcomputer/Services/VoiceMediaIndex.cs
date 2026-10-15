namespace Batcomputer;

/// <summary>Read-only shipped media paths; never contains a suit's edited files or assignments.</summary>
internal sealed class VoiceMediaIndex
{
    private readonly Dictionary<string, string[]> paths;
    internal long Bytes { get; }
    private VoiceMediaIndex(Dictionary<string, string[]> paths, long bytes) { this.paths = paths; Bytes = bytes; }

    internal static VoiceMediaIndex? Create(IEnumerable<string> files, long allowance)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in files)
        {
            if (!file.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
            var leaf = file[(file.LastIndexOf('/') + 1)..];
            // Include conservative container/string overhead, not just encoded text length.
            bytes += 128L + file.Length * 2L + leaf.Length * 2L;
            if (bytes > allowance) return null;
            if (!grouped.TryGetValue(leaf, out var list)) grouped.Add(leaf, list = []);
            list.Add(file);
        }
        return new(grouped.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase), bytes);
    }

    internal string[] Find(string media)
    {
        var suffix = '/' + media + ".wem";
        // Retain the old suffix-match behavior even for an unusual multi-segment media name.
        var candidates = media.Contains('/') ? paths.Values.SelectMany(p => p) :
            paths.TryGetValue(media + ".wem", out var matches) ? matches : Enumerable.Empty<string>();
        return candidates.Where(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
