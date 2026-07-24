using System.Text;

namespace Batcomputer;

/// <summary>
/// Generates the loose Gameplay Tag source that makes a mod's custom pawn tags valid
/// in Unreal's tag tree. Proven working via an independently-named file under
/// <c>Config/Tags</c> -
/// the game's own <c>PawnTags.ini</c> is never edited.
///
/// One file per mod, one row per suit tag:
/// <code>
/// [/Script/GameplayTags.GameplayTagsList]
/// GameplayTagList=(Tag="Pawns.Playable.Batman.Electric",DevComment="MyBatmanPack: Electric Suit")
/// </code>
///
/// The same generator serves a standalone suit build (one tag, named after the suit)
/// and a mod build (all tags, named after the ModId) - only the file name and the
/// set of rows differ.
/// </summary>
public sealed class PawnTagConfigService
{
    public sealed record TagRow(string PawnTag, string DevComment);

    public sealed class GenResult
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string OutputPath { get; set; } = "";
        public int RowCount { get; set; }
    }

    /// <summary>Loose-file install location under the merge-ready game folder.</summary>
    public static string RelativeConfigPath(string modOrSuitId) =>
        $"LEGOBatmanLotDK/Config/Tags/{modOrSuitId}PawnTags.ini";

    /// <summary>
    /// Renders the ini text deterministically: rows sorted by tag, duplicates rejected.
    /// Pure/string-only so it is trivially unit-testable and reproducible.
    /// </summary>
    public static string Render(IEnumerable<TagRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<TagRow>();
        foreach (var row in rows)
        {
            var tag = (row.PawnTag ?? "").Trim();
            if (tag.Length == 0)
            {
                throw new InvalidOperationException("A suit has an empty PawnTag; every suit needs a unique pawn tag before packaging.");
            }
            if (!seen.Add(tag))
            {
                throw new InvalidOperationException($"Duplicate PawnTag '{tag}' in this build; pawn tags must be globally unique.");
            }
            ordered.Add(new TagRow(tag, row.DevComment ?? ""));
        }

        ordered.Sort((a, b) => string.CompareOrdinal(a.PawnTag, b.PawnTag));

        var sb = new StringBuilder();
        sb.Append("[/Script/GameplayTags.GameplayTagsList]\r\n");
        foreach (var row in ordered)
        {
            sb.Append("GameplayTagList=(Tag=\"")
              .Append(row.PawnTag)
              .Append("\",DevComment=\"")
              .Append(EscapeDevComment(row.DevComment))
              .Append("\")\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Writes the ini into a staged LooseFiles tree; returns the file path.</summary>
    public GenResult Generate(string looseFilesRoot, string modOrSuitId, IEnumerable<TagRow> rows)
    {
        var result = new GenResult();
        try
        {
            var text = Render(rows);
            var outPath = Path.Combine(
                looseFilesRoot,
                RelativeConfigPath(modOrSuitId).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            // Loose Unreal config: ASCII, CRLF; no BOM (matches the proven test file).
            File.WriteAllText(outPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            result.OutputPath = outPath;
            result.RowCount = text.Split("GameplayTagList=").Length - 1;
            result.Status = "created";
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
        }
        return result;
    }

    // DevComment is a quoted ini value; keep it single-line and un-quote-broken.
    private static string EscapeDevComment(string comment) =>
        comment.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
}
