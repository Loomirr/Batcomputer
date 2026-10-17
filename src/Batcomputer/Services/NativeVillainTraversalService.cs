namespace Batcomputer;

/// <summary>Recognize native villain identity without baking Character Freedom's normal-mode
/// traversal grants into suits. Mayhem retains the game's native upgrade progression.</summary>
internal static class NativeVillainTraversalService
{
    internal const string GrapplingSet = "/Game/Characters/Abilities/CoreAbilities/Grappling/AS_Grappling";
    internal const string VillainRoot = "/Game/AdditionalContent/VillainMode/Characters/Playables/";

    internal static string Family(string? archetypePackage) => UnrealPathUtil.NormalizePackagePath(archetypePackage) switch
    {
        VillainRoot + "Joker/BP_CAT_Archetype_Joker" => "Joker",
        VillainRoot + "HarleyQuinn/BP_CAT_Archetype_HarleyQuinn" => "HarleyQuinn",
        _ => ""
    };

    internal static bool IsNativePlayable(string? package)
    {
        var path = UnrealPathUtil.NormalizePackagePath(package);
        return path.EndsWith("_Playable", StringComparison.Ordinal) &&
            (path.StartsWith(VillainRoot + "Joker/BP_Joker_", StringComparison.Ordinal) ||
             path.StartsWith(VillainRoot + "HarleyQuinn/BP_HarleyQuinn_", StringComparison.Ordinal));
    }

    internal static bool AppliesTo(NativeSuitProject project) =>
        string.IsNullOrWhiteSpace(project.ReplacementTarget) &&
        (IsNativePlayable(project.PlayableTemplate?.PackagePath) ||
         IsNativePlayable(project.PairedCapeAdapter?.GameplayDonorPackage) ||
         (!BaseEligibilityService.IsGameplayDonorPackage(project.PlayableTemplate?.PackagePath) &&
          IsNativePlayable(project.MachineryDonorPlayable)));

    internal static void EnsureDependencies(NativeSuitProject project, bool pairedCape,
        DonorInfo donor, ICollection<string> requiredSets)
    {
        // Joker/Harley already have native traversal data and upgrade-gated abilities.
        // Character Freedom supplies temporary normal-mode grants, never the suit PAK.
        if (AppliesTo(project) || Family(donor.ArchetypePackage).Length > 0 ||
            !pairedCape || !project.PartGrafts.Any(g => g.IsGlider)) return;
        var file = ExtractedPackagePathService.ResolvePackageUasset(
            AppSettings.Current.EffectiveExtractedContentRoot(), donor.DprdPackage)
            ?? throw new InvalidDataException("The gameplay donor DPRD is missing: " + donor.DprdPackage);
        var inspection = new AbilityAssetMutationService().InspectDprdAbilitySets(file);
        if (!inspection.Success) throw new InvalidDataException(inspection.Error);
        var nativeSets = inspection.AbilitySets.Select(s => s.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string> { GliderService.GlidingAbilitySetPackage };
        foreach (var set in candidates)
        {
            // These are defaults, not mandatory user loadout overrides. Never restore an
            // explicitly disabled set or an ability the user deliberately removed.
            if (ExplicitlyDisabled(project.AbilityLoadout, set))
            {
                requiredSets.Remove(set);
                continue;
            }
            if (!nativeSets.Contains(set) && !requiredSets.Contains(set, StringComparer.OrdinalIgnoreCase))
                requiredSets.Add(set);
        }
    }

    internal static bool ExplicitlyDisabled(AbilityLoadoutProfile? profile, string set)
    {
        var ability = set == GrapplingSet
            ? "/Game/Characters/Abilities/CoreAbilities/Grappling/GA_Grappling"
            : "/Game/Characters/Abilities/CoreAbilities/Gliding/GA_Gliding";
        return profile?.AbilitySets.Any(s =>
            (!s.Enabled && UnrealPathUtil.NormalizePackagePath(s.PackagePath).Equals(set, StringComparison.OrdinalIgnoreCase)) ||
            s.RemovedGameplayAbilities.Any(p => UnrealPathUtil.NormalizePackagePath(p).Equals(ability, StringComparison.OrdinalIgnoreCase))) == true;
    }
}
