using System.Reflection;
using System.Text.RegularExpressions;

namespace Batcomputer;

internal static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    public static string Display => "v" + Current;

    // Accept the old v1.0-Beta tags too. Compare numeric prerelease identifiers numerically.
    public static int Compare(string left, string right)
    {
        var a = Parse(left); var b = Parse(right);
        for (var i = 0; i < 3; i++)
        {
            var result = a.Core[i].CompareTo(b.Core[i]);
            if (result != 0) return result;
        }
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
            return (a.Pre.Length == 0 ? 1 : 0).CompareTo(b.Pre.Length == 0 ? 1 : 0);
        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var an = long.TryParse(a.Pre[i], out var av); var bn = long.TryParse(b.Pre[i], out var bv);
            var result = an && bn ? av.CompareTo(bv) : an != bn ? (an ? -1 : 1)
                : StringComparer.OrdinalIgnoreCase.Compare(a.Pre[i], b.Pre[i]);
            if (result != 0) return result;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }

    public static bool IsPrerelease(string value) => Parse(value).Pre.Length != 0;

    private static (long[] Core, string[] Pre) Parse(string value)
    {
        var match = Regex.Match(value, @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success) throw new InvalidDataException("Unrecognized release version: " + value);
        return (new[] { long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value),
            match.Groups[3].Success ? long.Parse(match.Groups[3].Value) : 0 },
            match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>());
    }
}
