namespace Batcomputer;

/// <summary>
/// Repoints a suit project's gameplay, visual, cutscene, and DCMD template source paths at the
/// currently active extracted game/DLC dump.
///
/// Why this exists: a game update changes cooked Blueprint layouts, so assets generated from a
/// pre-update dump can parse fine in tools yet crash in-game. After a refresh, every suit must be
/// re-based - previously a manual re-pick per suit, which is easy to forget and hard to verify.
/// Only the SOURCE template paths move; the suit's own /Game/Mods/... output paths are untouched.
/// </summary>
public sealed class RebaseSuitService
{
    /// <summary>Status: "ok" (found in new dump) | "unchanged" (already pointing there) | "missing" (not in new dump) | "skipped" (no template set).</summary>
    public sealed record Change(string Role, string OldPath, string NewPath, string Status);

    /// <summary>
    /// Computes (and optionally applies) the rebase. Nothing is written when
    /// <paramref name="apply"/> is false, so the caller can show a before/after report first.
    /// </summary>
    public IReadOnlyList<Change> Rebase(NativeSuitProject project, string newContentRoot, bool apply)
    {
        var changes = new List<Change>();
        foreach (var (role, template) in new (string, TemplateRecord?)[]
                 {
                      ("playable", project.PlayableTemplate),
                      ("cutscene", project.CutsceneTemplate),
                      ("dcmd", project.DcmdTemplate),
                      ("visual-playable", project.VisualSourceTemplate),
                      ("visual-cutscene", project.VisualCutsceneSourceTemplate),
                  })
        {
            changes.Add(RebaseOne(role, template, newContentRoot, apply));
        }
        return changes;
    }

    private static Change RebaseOne(string role, TemplateRecord? template, string newContentRoot, bool apply)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Uasset))
        {
            return new Change(role, "", "", "skipped");
        }

        var newBase = ExtractedPackagePathService.ResolvePackageBase(newContentRoot, template.PackagePath);
        if (string.IsNullOrWhiteSpace(newBase))
        {
            var package = UnrealPathUtil.NormalizePackagePath(template.PackagePath);
            if (!string.IsNullOrWhiteSpace(package) &&
                !package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                // ContentRelative has no mount identity. Never let an unavailable Game Feature
                // record collide with a same-relative-path asset under /Game.
                return new Change(role, template.Uasset, "", "missing");
            }
            var rel = RelativeFor(template);
            if (rel is null)
            {
                return new Change(role, template.Uasset, "", "missing");
            }

            newBase = Path.Combine(newContentRoot, rel);
        }
        var newUasset = newBase + ".uasset";

        if (!File.Exists(newUasset))
        {
            return new Change(role, template.Uasset, newUasset, "missing");
        }

        if (PathsEqual(template.Uasset, newUasset))
        {
            return new Change(role, template.Uasset, newUasset, "unchanged");
        }

        var change = new Change(role, template.Uasset, newUasset, "ok");
        if (apply)
        {
            template.Uasset = newUasset;
            template.Uexp = File.Exists(newBase + ".uexp") ? newBase + ".uexp" : null;
            template.Ubulk = File.Exists(newBase + ".ubulk") ? newBase + ".ubulk" : null;
            template.PackagePath = ExtractedPackagePathService.PackagePathFromFile(newContentRoot, newUasset)
                                   ?? template.PackagePath;
            template.ContentRelative = ExtractedPackagePathService.ContentRelativeFromFile(newContentRoot, newUasset)
                                       ?? template.ContentRelative;
        }
        return change;
    }

    /// <summary>The template's path relative to a Content root, WITHOUT extension.</summary>
    private static string? RelativeFor(TemplateRecord template)
    {
        if (!string.IsNullOrWhiteSpace(template.ContentRelative))
        {
            var rel = template.ContentRelative.Replace('/', Path.DirectorySeparatorChar).Trim();
            return StripKnownExtension(rel);
        }

        var pkg = UnrealPathUtil.NormalizePackagePath(template.PackagePath);
        if (!string.IsNullOrWhiteSpace(pkg) && pkg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return pkg["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
        }

        return null;
    }

    private static string StripKnownExtension(string path)
    {
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^ext.Length];
            }
        }
        return path;
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
