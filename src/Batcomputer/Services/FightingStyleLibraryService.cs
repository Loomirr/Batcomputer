using System.Text;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>
/// Discovery is deliberately separate from the certified preset catalog. Finding an AI AbilitySet
/// proves that assets exist, not that its controller can accept player input.
/// </summary>
internal static class FightingStyleLibraryService
{
    internal sealed record Entry(string Id, string Label, FightingStyleProfile? Profile,
        AbilitySetCatalogEntry? Source)
    {
        public override string ToString() => Label;
    }

    public static IReadOnlyList<Entry> Build(IEnumerable<AbilitySetCatalogEntry> sources)
    {
        var sets = sources.DistinctBy(x => x.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.PackagePath, StringComparer.OrdinalIgnoreCase);
        var result = FightingStyleProfileService.Catalog().Select(style =>
        {
            sets.TryGetValue(style.MeleeAbilitySetPackage, out var source);
            return new Entry(style.Id, style.DisplayName +
                (source?.IsAvailable == true ? "" : " [not extracted]"), style, source);
        }).ToList();
        var known = result.Select(x => x.Profile!.MeleeAbilitySetPackage).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(sets.Values.Where(x => !known.Contains(x.PackagePath) && IsCandidate(x))
            .OrderBy(x => x.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Entry(x.PackagePath, "Enemy / other: " + FriendlyName(x.PackagePath) +
                (x.IsAvailable ? " [inspect; needs player adapter]" : " [not extracted]"), null, x)));
        return result;
    }

    internal static bool IsCandidate(AbilitySetCatalogEntry source)
    {
        // A DLC can mount at its own root instead of /Game. Follow its serialized melee grants
        // as well as known base-game folders, without treating every utility set as a style.
        if (source.GameplayAbilities.Any(grant => Regex.IsMatch(UnrealPathUtil.AssetName(grant.PackagePath),
                "^GA_(?:MeleeAttack|AI_.*Melee)", RegexOptions.IgnoreCase))) return true;
        if (source.PackagePath.Contains("/Characters/Abilities/MeleeAbilities/", StringComparison.OrdinalIgnoreCase))
            return !source.PackagePath.EndsWith("AS_NPC_Quest", StringComparison.OrdinalIgnoreCase);
        return (source.PackagePath.Contains("/Global/AI/Abilities/", StringComparison.OrdinalIgnoreCase) ||
                source.PackagePath.Contains("/Characters/Bosses/", StringComparison.OrdinalIgnoreCase)) &&
               source.GameplayAbilities.Any(grant => Regex.IsMatch(UnrealPathUtil.AssetName(grant.PackagePath),
                   "Attack|Melee|Charge|Throw|Fire|Shoot|Strike|Slam|Stomp|Punch|Smash|Blade", RegexOptions.IgnoreCase));
    }

    private static string FriendlyName(string package) => UnrealPathUtil.AssetName(package) switch
    {
        "AS_BladeGoon" => "Sword / katana enemy",
        "AS_Bruiser" => "Bruiser — hammer",
        "AS_Bulwark" => "Bulwark — large shield",
        "AS_Blunt" => "Blunt enemy — baseball bat",
        var name => Regex.Replace(name.Replace("AS_Melee_", "").Replace("AS_", "").Replace('_', ' '),
            "(?<=[a-z])(?=[A-Z])", " ")
    };

    public static string Describe(Entry entry)
    {
        var text = new StringBuilder();
        if (entry.Profile is { } style)
        {
            text.AppendLine(style.SafetySummary);
            foreach (var note in style.SafetyNotes) text.AppendLine("• " + note);
            text.AppendLine();
            text.AppendLine("Melee set: " + style.MeleeAbilitySetPackage);
            text.AppendLine("Combat effect: " + (style.CombatTypeEffectPackage.Length == 0 ? "none (outgoing combat effect is removed)" : style.CombatTypeEffectPackage));
            foreach (var package in style.SupportingAbilitySetPackages.Concat(style.HeldItemAbilityPackages)
                         .Concat(style.RequiredStateMachinePackages)) text.AppendLine(package);
        }
        else
        {
            text.AppendLine("DISCOVERED SOURCE — NOT A PLAYER-CONTROLLED PRESET YET");
            text.AppendLine("You can reuse its weapon and animations, but the AI attack/input/controller logic must be adapted. Inspecting this entry does not modify your suit.");
            text.AppendLine("Do not replace the player's core, input profile or entire character AbilitySet with an enemy's.");
            if (entry.Source?.PackagePath.EndsWith("AS_BladeGoon", StringComparison.OrdinalIgnoreCase) == true)
            {
                text.AppendLine();
                text.AppendLine("Traced sword chain:");
                text.AppendLine("For the implemented player adapter, choose Sword — player adapter from the preset list, then open Combat settings. This raw enemy AbilitySet remains inspection-only.");
                text.AppendLine("DPRD: /Game/Characters/Enemies/Archetypes/DA_DPRD_BladeGoonCharacterData");
                text.AppendLine("Equipment: /Game/Characters/Equipment/Sword/BP_Katana_ED");
                text.AppendLine("Weapon actor: /Game/Characters/Equipment/Sword/BP_Katana_Weapon");
                text.AppendLine("Mesh: /Game/Models/Props/SM_Katana");
                text.AppendLine("Tags: Equipment.Katana; Animation.Equipment.Blade");
                text.AppendLine("Attachment slots: LAM.RightHand / LAM.RightStow");
                text.AppendLine("The enemy uses InputData_Goon and GA_AI_BladeMeleeGTSM, not the player attack controller.");
            }
        }
        if (entry.Source is { } source)
        {
            text.AppendLine();
            text.AppendLine("Source: " + source.PackagePath);
            text.AppendLine(source.IsAvailable ? "Available in this user's active extraction." : "Unavailable in the active extraction. Run Full refresh.");
            text.AppendLine("Gameplay ability grants:");
            foreach (var grant in source.GameplayAbilities)
                text.AppendLine(grant.PackagePath + (string.IsNullOrWhiteSpace(grant.InputTag) ? "" : " | " + grant.InputTag));
        }
        return text.ToString();
    }
}
