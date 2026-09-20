namespace Batcomputer;

/// <summary>Explicit, reversible takedown-only grant edits using the existing suit-local AbilitySet pipeline.</summary>
internal static class TakedownProfileService
{
    internal enum Preset { Native, Minifig, Smallfig }
    internal const string Root = "/Game/Characters/Abilities/MeleeAbilities/TakeDownAttack/";
    internal static string MainAbility(Preset preset) => Root + (preset == Preset.Smallfig ? "GA_TakeDownAttack_Robin_DickGrayson" : "GA_TakeDownAttack_Batman");
    internal static string End(Preset preset) => Root + (preset == Preset.Smallfig ? "GA_EndOfEncounterTakeDownAttack_Robin" : "GA_EndOfEncounterTakeDownAttack_Batman");
    private static readonly HashSet<string> MainPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        Root + "GA_TakeDownAttack", Root + "GA_TakeDownAttack_Batman", Root + "GA_TakeDownAttack_Batgirl",
        Root + "GA_TakeDownAttack_Catwoman", Root + "GA_TakeDownAttack_Gordon", Root + "GA_TakeDownAttack_Robin_DickGrayson",
        Root + "GA_TakeDownAttack_TaliaAlGhul", Root + "GA_TakeDownAttack_TaliaAlGhul_TeleportTakedown",
        "/Game/Characters/Abilities/MeleeAbilities/GA_TakeDownAttack_NightWing"
    };
    private static readonly HashSet<string> EndPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        Root + "GA_EndOfEncounterTakeDownAttack", Root + "GA_EndOfEncounterTakeDownAttack_Batman",
        Root + "GA_EndOfEncounterTakeDownAttack_Nightwing", Root + "GA_EndOfEncounterTakeDownAttack_Robin"
    };
    private static string Normalize(string path) => UnrealPathUtil.NormalizePackagePath(path);
    private static bool IsTakedown(string path) => MainPackages.Contains(Normalize(path)) || EndPackages.Contains(Normalize(path));

    internal static string Label(Preset preset) => preset switch
    {
        Preset.Native => "Restore current melee set's takedowns",
        Preset.Minifig => "Minifig · Batman takedowns (experimental)",
        Preset.Smallfig => "Smallfig · Robin takedowns (experimental)",
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    internal static void Apply(AbilityLoadoutProfile profile, AbilityEditorCatalog catalog, Preset preset)
    {
        if (!Enum.IsDefined(preset)) throw new InvalidDataException("Unknown takedown preset.");
        if (catalog.SavedLoadoutNeedsRemap) throw new InvalidDataException("Re-select the gameplay donor before editing takedowns.");
        var selected = profile.AbilitySets.Where(s => s.Enabled && AbilityDependencyService.IsCombatSet(s.PackagePath)).ToArray();
        if (selected.Length != 1) throw new InvalidDataException("Choose exactly one player melee ability set before editing takedowns.");
        var selection = selected[0];
        var source = catalog.InheritedAbilitySets.Concat(catalog.AvailableAbilitySets)
            .FirstOrDefault(s => Normalize(s.PackagePath).Equals(Normalize(selection.PackagePath), StringComparison.OrdinalIgnoreCase) && s.IsAvailable);
        if (source is null || source.IsCore || AbilityLoadoutService.IsProtectedCoreSet(selection.PackagePath))
            throw new InvalidDataException("The selected melee set could not be inspected safely. Refresh game assets first.");
        var native = source.GameplayAbilities;
        if (native.Count(g => MainPackages.Contains(Normalize(g.PackagePath))) != 1 ||
            native.Count(g => EndPackages.Contains(Normalize(g.PackagePath))) > 1)
            throw new InvalidDataException("This melee set has no supported, unambiguous takedown pair. Its abilities were not changed.");

        var removed = selection.RemovedGameplayAbilities.ToList();
        var added = selection.AddedGameplayAbilities.Select(g => new CustomGameplayAbilityGrant
            { PackagePath = g.PackagePath, AbilityLevel = g.AbilityLevel, InputTag = g.InputTag }).ToList();
        if (preset == Preset.Native)
        {
            removed.RemoveAll(IsTakedown);
            added.RemoveAll(g => IsTakedown(g.PackagePath));
        }
        else
        {
            var effective = native.Where(g => !removed.Any(r => Normalize(r).Equals(Normalize(g.PackagePath), StringComparison.OrdinalIgnoreCase)))
                .Select(g => new CustomGameplayAbilityGrant { PackagePath = g.PackagePath, AbilityLevel = g.AbilityLevel, InputTag = g.InputTag })
                .Concat(added).ToArray();
            var main = effective.Where(g => MainPackages.Contains(Normalize(g.PackagePath))).ToArray();
            var end = effective.Where(g => EndPackages.Contains(Normalize(g.PackagePath))).ToArray();
            if (main.Length != 1 || end.Length > 1)
                throw new InvalidDataException("The active takedown grants are removed or ambiguous. Restore the melee set's takedowns first.");
            var targets = new List<(CustomGameplayAbilityGrant Grant, string Package)> { (main[0], MainAbility(preset)) };
            if (end.Length == 1) targets.Add((end[0], End(preset)));
            foreach (var target in targets)
                if (!catalog.GameplayAbilities.Any(g => Normalize(g.PackagePath).Equals(target.Package, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The requested takedown ability is not available in the extracted catalog: " + target.Package);
            removed.RemoveAll(IsTakedown);
            removed.AddRange(native.Where(g => IsTakedown(g.PackagePath)).Select(g => Normalize(g.PackagePath)));
            added.RemoveAll(g => IsTakedown(g.PackagePath));
            foreach (var (grant, package) in targets)
                added.Add(new CustomGameplayAbilityGrant { PackagePath = package, AbilityLevel = grant.AbilityLevel, InputTag = grant.InputTag });
        }
        // Commit only after every source and replacement passed validation. Unrelated edits,
        // core sets, combat style, counters, equipment, movement and held props stay untouched.
        selection.RemovedGameplayAbilities = removed;
        selection.AddedGameplayAbilities = added;
    }
}
