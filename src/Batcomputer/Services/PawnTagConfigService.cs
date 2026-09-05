using System.Text;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>
/// Generates the loose Gameplay Tag source that makes a mod's custom gameplay tags valid
/// in Unreal's tag tree. Proven working via an independently-named file under
/// <c>Config/Tags</c> -
/// the game's own <c>PawnTags.ini</c> is never edited.
///
/// One file per mod, with rows for suits and other mod-owned assets:
/// <code>
/// [/Script/GameplayTags.GameplayTagsList]
/// GameplayTagList=(Tag="Pawns.Playable.Batman.Electric",DevComment="MyBatmanPack: Electric Suit")
/// </code>
///
/// The same generator serves a standalone suit build and a combined mod release.
/// </summary>
public sealed class PawnTagConfigService
{
    private const string PlayablePrefix = "Pawns.Playable.";

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
        $"LEGOBatmanLotDK/Config/Tags/{modOrSuitId}Tags.ini";

    /// <summary>
    /// Returns the native runtime's per-character owner for a full pawn identity, preserving the
    /// exact spelling/casing serialized by the game. For example, the shipped Catwoman owner is
    /// <c>Pawns.Playable.CatWoman</c>, not a folder-name-derived approximation.
    /// </summary>
    public static string CharacterScopeForPawnTag(string? pawnTag)
    {
        var normalized = pawnTag?.Trim() ?? "";
        if (!normalized.StartsWith(PlayablePrefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.Length <= PlayablePrefix.Length)
        {
            return "";
        }

        var characterEnd = normalized.IndexOf('.', PlayablePrefix.Length);
        return characterEnd < 0 ? normalized : normalized[..characterEnd];
    }

    /// <summary>
    /// Repairs only the character-owner casing of a custom PawnTag when it names the same owner as
    /// the selected donor. This lets saved projects created from folder-derived family names adopt
    /// the donor DCMD's authoritative spelling without changing their unique leaf or moving a suit
    /// to a different character family.
    /// </summary>
    public static string CanonicalizeCharacterOwner(string? pawnTag, string? donorPawnTag)
    {
        var normalized = pawnTag?.Trim() ?? "";
        var currentScope = CharacterScopeForPawnTag(normalized);
        var donorScope = CharacterScopeForPawnTag(donorPawnTag);
        if (currentScope.Length == 0 || donorScope.Length == 0 ||
            !currentScope.Equals(donorScope, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return donorScope + normalized[currentScope.Length..];
    }

    /// <summary>
    /// Returns a release-blocking explanation when a custom PawnTag belongs to a genuinely
    /// different playable-character owner than the selected gameplay donor. Casing-only
    /// differences are valid here and are repaired separately by <see cref="CanonicalizeCharacterOwner"/>.
    /// Empty or malformed tags remain the responsibility of the normal PawnTag validation.
    /// </summary>
    public static string? CharacterOwnerMismatchError(string? pawnTag, string? donorPawnTag)
    {
        var currentScope = CharacterScopeForPawnTag(pawnTag);
        var donorScope = CharacterScopeForPawnTag(donorPawnTag);
        if (currentScope.Length == 0 || donorScope.Length == 0 ||
            currentScope.Equals(donorScope, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"PawnTag owner '{currentScope}' does not match the selected gameplay donor owner '{donorScope}'. " +
               "Choose a PawnTag under the donor's character owner before packaging.";
    }

    /// <summary>
    /// Renders the ini text deterministically: rows sorted by tag, duplicates rejected.
    /// Pure/string-only so it is trivially unit-testable and reproducible.
    /// </summary>
    public static string Render(IEnumerable<TagRow> rows)
    {
        var ordered = NormalizeRows(rows);

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

    /// <summary>Returns generated tags that are absent from an installed loose tag file.</summary>
    public static IReadOnlyList<string> FindMissingTags(string path, IEnumerable<TagRow> rows)
    {
        var present = File.Exists(path) ? ReadTagNames(File.ReadAllText(path)) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return NormalizeRows(rows)
            .Select(row => row.PawnTag)
            .Where(tag => !present.Contains(tag))
            .ToArray();
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

    private static List<TagRow> NormalizeRows(IEnumerable<TagRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<TagRow>();
        foreach (var row in rows)
        {
            var tag = (row.PawnTag ?? "").Trim();
            if (tag.Length == 0)
            {
                throw new InvalidOperationException("A generated gameplay tag is empty; every release tag must be unique before packaging.");
            }
            if (!seen.Add(tag))
            {
                throw new InvalidOperationException($"Duplicate gameplay tag '{tag}' in this build; release tags must be globally unique.");
            }
            ordered.Add(new TagRow(tag, row.DevComment ?? ""));
        }

        ordered.Sort((a, b) => string.CompareOrdinal(a.PawnTag, b.PawnTag));
        return ordered;
    }

    private static HashSet<string> ReadTagNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "Tag\\s*=\\s*\\\"(?<tag>[^\\\"]+)\\\"", RegexOptions.CultureInvariant))
        {
            var tag = match.Groups["tag"].Value.Trim();
            if (tag.Length > 0) names.Add(tag);
        }
        return names;
    }

    // DevComment is a quoted ini value; keep it single-line and un-quote-broken.
    private static string EscapeDevComment(string comment) =>
        comment.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
}
