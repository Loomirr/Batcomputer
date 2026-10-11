namespace Batcomputer;

/// <summary>Body-matched dodge grants, including the native paired combat-dodge sequences.</summary>
internal static class DodgeProfileService
{
    internal enum Preset { Native, Minifig, Smallfig }
    internal const string Root = "/Game/Characters/Abilities/MeleeAbilities/Dodge/";
    internal static string Ability(Preset preset) => Root + (preset == Preset.Smallfig ? "GA_Dodge_Robin_DickGrayson" : "GA_Dodge_Batman");
    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        Root + "GA_Dodge", Root + "GA_Dodge_Batman", Root + "GA_Dodge_CatWoman", Root + "GA_Dodge_Gordon",
        Root + "GA_Dodge_Nightwing", Root + "GA_Dodge_Robin_DickGrayson", Root + "GA_Dodge_TaliaAlGhul",
        "/Game/AdditionalContent/VillainMode/Characters/Abilities/MeleeAbilities/GA_Dodge_Joker",
        "/Game/AdditionalContent/VillainMode/Characters/Abilities/MeleeAbilities/GA_Dodge_HarleyQuinn"
    };
    private static string Normalize(string p) => UnrealPathUtil.NormalizePackagePath(p);
    private static bool IsDodge(string p) => Packages.Contains(Normalize(p));
    internal static string Label(Preset preset) => preset switch
    {
        Preset.Native => "Restore current melee set's dodges",
        Preset.Minifig => "Minifig · Batman dodges / vaults (experimental)",
        Preset.Smallfig => "Smallfig · Robin dodges / vaults (experimental)",
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    internal static void Apply(AbilityLoadoutProfile profile, AbilityEditorCatalog catalog, Preset preset)
    {
        if (!Enum.IsDefined(preset)) throw new InvalidDataException("Unknown dodge preset.");
        if (catalog.SavedLoadoutNeedsRemap) throw new InvalidDataException("Re-select the gameplay donor before editing dodges.");
        var sets = profile.AbilitySets.Where(s => s.Enabled && AbilityDependencyService.IsCombatSet(s.PackagePath)).ToArray();
        if (sets.Length != 1) throw new InvalidDataException("Choose exactly one player melee ability set before editing dodges.");
        var selected = sets[0];
        var source = catalog.InheritedAbilitySets.Concat(catalog.AvailableAbilitySets).FirstOrDefault(s =>
            Normalize(s.PackagePath).Equals(Normalize(selected.PackagePath), StringComparison.OrdinalIgnoreCase) && s.IsAvailable);
        if (source is null || source.IsCore || AbilityLoadoutService.IsProtectedCoreSet(selected.PackagePath) || source.GameplayAbilities.Count(g => IsDodge(g.PackagePath)) != 1)
            throw new InvalidDataException("This melee set has no supported, unambiguous dodge grant. Nothing was changed.");
        var removed = selected.RemovedGameplayAbilities.ToList();
        var added = selected.AddedGameplayAbilities.ToList();
        CustomGameplayAbilityGrant? replacement = null;
        if (preset != Preset.Native)
        {
            var effective = source.GameplayAbilities.Where(g => IsDodge(g.PackagePath) && !removed.Any(r => Normalize(r).Equals(Normalize(g.PackagePath), StringComparison.OrdinalIgnoreCase)))
                .Select(g => new CustomGameplayAbilityGrant { PackagePath = g.PackagePath, AbilityLevel = g.AbilityLevel, InputTag = g.InputTag })
                .Concat(added.Where(g => IsDodge(g.PackagePath))).ToArray();
            if (effective.Length != 1) throw new InvalidDataException("Active dodge grants are removed or ambiguous. Restore the melee set's dodges first.");
            var target = Ability(preset);
            if (!catalog.GameplayAbilities.Any(g => Normalize(g.PackagePath).Equals(target, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The requested dodge ability is unavailable. Refresh game assets first: " + target);
            replacement = new() { PackagePath = target, AbilityLevel = effective[0].AbilityLevel, InputTag = effective[0].InputTag };
        }
        removed.RemoveAll(IsDodge);
        added.RemoveAll(g => IsDodge(g.PackagePath));
        if (replacement is not null)
        {
            removed.AddRange(source.GameplayAbilities.Where(g => IsDodge(g.PackagePath)).Select(g => Normalize(g.PackagePath)));
            added.Add(replacement);
        }
        selected.RemovedGameplayAbilities = removed;
        selected.AddedGameplayAbilities = added;
    }

    internal static string? BodyMismatchWarning(NativeSuitProject project)
    {
        var bodies = project.SkinnedMeshes.Where(m => m.Component.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bodies.Length > 1 || project.AbilityLoadout is not { } profile) return null;
        var family = bodies.Length == 0 ? project.BodyProfile?.GeometryFamily : NativeBodyProfileService.MatchMesh(bodies[0].DonorMeshPackage)?.GeometryFamily;
        if (family is null || !(family.StartsWith("Smallfig", StringComparison.OrdinalIgnoreCase) || family.StartsWith("Minifig", StringComparison.OrdinalIgnoreCase))) return null;
        if (project.GameplayAnimationGraphs.Any(g => g.Abilities.Any(a => IsDodge(a.OriginalPackage)))) return null;
        var sets = profile.AbilitySets.Where(s => s.Enabled && AbilityDependencyService.IsCombatSet(s.PackagePath)).ToArray();
        if (sets.Length != 1) return null;
        var added = sets[0].AddedGameplayAbilities.Where(g => IsDodge(g.PackagePath)).ToArray();
        if (added.Length > 1) return null;
        string? ability = added.SingleOrDefault()?.PackagePath;
        if (ability is null)
        {
            if (sets[0].RemovedGameplayAbilities.Any(IsDodge)) return null;
            var name = UnrealPathUtil.AssetName(sets[0].PackagePath);
            ability = name == "AS_Melee_Robin_DickGrayson" ? Ability(Preset.Smallfig) :
                name is "AS_Melee_Batman" or "AS_Melee_CatWoman" or "AS_Melee_Gordon" or "AS_Melee_NightWing" or "AS_Melee_Batgirl" ? Ability(Preset.Minifig) : null;
        }
        if (ability is null) return null;
        var small = family.StartsWith("Smallfig", StringComparison.OrdinalIgnoreCase);
        return small == Normalize(ability).Equals(Ability(Preset.Smallfig), StringComparison.OrdinalIgnoreCase) ? null :
            $"The {family} body uses a different-sized combat dodge set. Normal-enemy vaults may refuse to start even when takedowns work. In Ability workshop → Dodges, select {(small ? "Smallfig · Robin" : "Minifig · Batman")}, save and rebuild. Test paired dodges in-game.";
    }
}
