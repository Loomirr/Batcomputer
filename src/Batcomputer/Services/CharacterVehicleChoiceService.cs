using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Choices for a character group's single default vehicle. This does not rewrite vehicle ownership.</summary>
internal static class CharacterVehicleChoiceService
{
    internal sealed record Choice(string Label, string Tag)
    {
        public override string ToString() => Label;
    }

    internal static bool IsValidTag(string? tag) => tag is "" or "None" ||
        tag is not null && Regex.IsMatch(tag, @"^Pawns\.Vehicle\.[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<Choice> Native(string content)
    {
        var choices = new Dictionary<string, Choice>(StringComparer.Ordinal);
        var candidates = new List<(string File, string Package)>();
        void AddDirectory(string directory, string mount, bool recursive = false)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "DA_Vehicle_*.uasset", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                candidates.Add((file, mount + "/" + Path.GetRelativePath(directory, file)[..^7].Replace('\\', '/')));
        }
        AddDirectory(Path.Combine(content, "Vehicles"), "/Game/Vehicles");
        AddDirectory(Path.Combine(content, "AdditionalContent"), "/Game/AdditionalContent", recursive: true);
        var plugins = Path.Combine(Directory.GetParent(content)?.FullName ?? "", "Plugins", "GameFeatures");
        if (Directory.Exists(plugins))
            foreach (var feature in Directory.EnumerateDirectories(plugins))
                AddDirectory(Path.Combine(feature, "Content", "Vehicles"), "/" + Path.GetFileName(feature) + "/Vehicles");
        foreach (var (file, package) in candidates)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            try
            {
                var tag = NativeAssetTextPatch.GetGameplayTag(VehicleAssetService.Read(content, package), "PawnTag");
                if (!IsValidTag(tag) || tag is "" or "None" || tag!.Contains(".OnRails", StringComparison.OrdinalIgnoreCase)) continue;
                choices[tag] = new Choice(stem["DA_Vehicle_".Length..].Replace('_', ' ') + " · base game", tag);
            }
            catch { /* An unreadable extracted asset is not a safe choice. */ }
        }
        return choices.Values.OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<Choice> ForCharacter(string projectRoot, string ownerTag, string content)
    {
        var choices = new List<Choice> { new("Use donor's default vehicle", ""), new("No default vehicle", "None") };
        choices.AddRange(Native(content));
        var service = new VehicleProjectService(projectRoot);
        foreach (var summary in service.List().Where(s => s.Error.Length == 0))
        {
            try
            {
                var vehicle = service.Load(summary.Path);
                if (vehicle.OwnerTag == ownerTag)
                    choices.Add(new(vehicle.DisplayName + " · custom vehicle (include in the same mod)", VehicleProjectService.PawnTag(vehicle)));
            }
            catch { /* An unreadable saved vehicle is not offered. */ }
        }
        return choices;
    }
}
