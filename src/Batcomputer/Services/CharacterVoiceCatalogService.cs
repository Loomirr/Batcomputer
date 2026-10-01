using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Batcomputer;

/// <summary>Read-only native dialogue discovery. Never edits a shared event or audio bank.</summary>
internal static class CharacterVoiceCatalogService
{
    internal sealed record Line(string Id, string Category, string EventPackage, string SequencePath,
        string Speaker, string Media, bool OwnSpeaker);
    internal sealed record Snapshot(string Donor, string VoiceActor, Line[] Lines, string[] Warnings);

    internal static string Donor(NativeSuitProject project) => new[] { project.PairedCapeAdapter?.GameplayDonorPackage,
        project.BaseProfile?.GameplayDonorPackage, project.MachineryDonorPlayable, project.PlayableTemplate?.PackagePath }
        .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";

    internal static JArray Exports(DefaultFileProvider provider, string package) =>
        JArray.FromObject(provider.LoadPackage(package).GetExports());

    internal static Snapshot Inspect(NativeSuitProject project, CancellationToken cancellation)
    {
        var donor = Donor(project);
        if (string.IsNullOrWhiteSpace(donor)) throw new InvalidDataException("Choose a gameplay donor before inspecting its voice.");
        using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
        var blueprint = Exports(provider, donor);
        var voice = blueprint.OfType<JObject>().Select(e => e["Properties"]?["VoiceActorName"]?.Value<string>())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        var warnings = new List<string>();
        if (voice.Length == 0) warnings.Add("This donor has no explicit voice identity in its exported components; inherited voice registration was not guessed.");
        var entries = blueprint.Descendants().OfType<JProperty>().Where(p => p.Name == "ObjectPath")
            .Select(p => p.Value.Value<string>() ?? "").Where(p => p.StartsWith("/Game/Audio/Tagging/DataEntries/", StringComparison.Ordinal)).Select(Package).Distinct().ToArray();
        var cards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            try { foreach (var card in Exports(provider, entry).Descendants().OfType<JProperty>().Where(p => p.Name == "ObjectPath")
                .Select(p => Package(p.Value.Value<string>() ?? "")).Where(p => p.Contains("/DataCards/Dialogue/", StringComparison.Ordinal))) cards.Add(card); }
            catch (Exception ex) { warnings.Add(entry + ": " + ex.Message); }
        }
        var lines = new List<Line>(); var events = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Exports(provider, card).OfType<JObject>().SelectMany(e => e["Properties"]?["Entries"] as JArray ?? []))
                {
                    var category = entry["Key"]?.Value<string>() ?? "Uncategorized";
                    var eventPackage = Package(entry["Value"]?["ObjectPath"]?.Value<string>() ?? "");
                    if (eventPackage.Length == 0) continue;
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        if (!events.TryGetValue(eventPackage, out var data)) events[eventPackage] = data = Exports(provider, eventPackage);
                        lines.AddRange(ParseLines(data, eventPackage, category, voice));
                    }
                    catch (Exception ex) { warnings.Add(eventPackage + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { warnings.Add(card + ": " + ex.Message); }
        }
        if (cards.Count == 0) warnings.Add("No directly referenced native voice data card was found. This is not proof that the character has no voice lines.");
        return new(donor, voice, lines.DistinctBy(l => (l.Category, l.Id)).ToArray(), warnings.Distinct().ToArray());
    }

    internal static Line[] ParseLines(JArray exports, string package, string category, string voice) => exports
        .OfType<JObject>().SelectMany(e => (e["Sequence"] is JObject sequence ? sequence.DescendantsAndSelf().OfType<JObject>() : []))
        .Where(node => node["WemName"]?.Type == JTokenType.String)
        .Select(node => {
            var speaker = node["Character"]?.Value<string>() ?? "Default";
            var media = node["WemName"]!.Value<string>()!;
            var identity = package + "|" + node.Path + "|" + media;
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            return new Line(id, category, package, node.Path, speaker, media,
                speaker.Equals("Default", StringComparison.OrdinalIgnoreCase) || speaker.Equals(voice, StringComparison.OrdinalIgnoreCase));
        }).ToArray();

    internal static string Package(string path) { var dot = path.IndexOf('.'); return dot < 0 ? path : path[..dot]; }
    internal static string? FindMedia(IEnumerable<string> paths, string media, string language)
    {
        var matches = paths.Where(p => p.EndsWith("/" + media + ".wem", StringComparison.OrdinalIgnoreCase)).ToArray();
        var localized = matches.Where(p => p.Contains("/Wub_Loc_" + language + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (localized.Length == 1) return localized[0];
        if (localized.Length > 1) throw new InvalidDataException("Multiple recordings match this language; the preview will not guess.");
        var shared = matches.Where(p => !p.Contains("/Wub_Loc_", StringComparison.OrdinalIgnoreCase)).ToArray();
        return shared.Length == 1 ? shared[0] : null;
    }
}
