using System.Text;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Reads optional authoring metadata. The rig validator still makes the safety decision.</summary>
internal static class FbxBaseTag
{
    private static readonly byte[] BinaryHeader = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");

    internal static string? Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 128L * 1024 * 1024) throw new InvalidDataException("FBX must be smaller than 128 MB.");
        var header = new byte[BinaryHeader.Length];
        if (stream.Read(header) == header.Length && header.SequenceEqual(BinaryHeader))
            return SkinnedFbxValidator.Inspect(path).BatcomputerBase;
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (reader.ReadLine() is not { } first || !first.StartsWith("; FBX ", StringComparison.Ordinal)) return null;
        var tags = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!Regex.IsMatch(line, "^\\s*P:\\s*\"BatcomputerBase\"\\s*,", RegexOptions.CultureInvariant)) continue;
            var quoted = Regex.Matches(line, "\"([^\"]*)\"");
            if (quoted.Count >= 5 && quoted[^1].Groups[1].Value is { Length: > 0 } value) tags.Add(value);
        }
        if (tags.Count > 1) throw new InvalidDataException("The FBX contains conflicting Batcomputer driving-base tags.");
        return tags.SingleOrDefault();
    }
}
