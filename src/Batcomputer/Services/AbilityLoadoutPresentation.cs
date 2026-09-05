namespace Batcomputer;

/// <summary>Read-only build-time dependencies shown alongside the editable source loadout.</summary>
internal static class AbilityLoadoutPresentation
{
    internal sealed record Entry(string Label, string Package, string Detail);

    internal static IReadOnlyList<Entry> BundleEntries(AbilityLoadoutProfile profile, string playablePackage)
    {
        var style = FightingStyleProfileService.Find(profile.FightingStyleId);
        if (style is null) return [];
        if (SwordCombatService.Enabled(profile))
        {
            var path = UnrealPathUtil.NormalizePackagePath(playablePackage).Split('/');
            if (path.Length < 5 || path[1] != "Game" || path[2] != "Mods") return [];
            var root = SwordCombatService.Root(path[3]);
            if (HeldItemService.Independent(profile)) return [
                new(PlayerMeleeAdapterService.Label(style.Id) + " ability set", root + "/AS_PlayerSword", "Combat only. Held items are configured independently."),
                new(PlayerMeleeAdapterService.Label(style.Id) + " attacks", root + "/GA_PlayerSword", "Player attack graph, timing and combat montages; does not add an item." +
                    (MeleeStatusEffectService.Enabled(profile.SwordCombat?.HitStatus) ? $" On-hit status: {profile.SwordCombat!.HitStatus.PresetId}, {profile.SwordCombat.HitStatus.DurationSeconds:0.##}s (experimental, goon targets)." : "")) ];
            return [
                new("Sword ability set", root + "/AS_PlayerSword", "Generated at build. Replaces the source melee set for this suit only."),
                new("Sword attacks", root + "/GA_PlayerSword", "Generated attack ability with the player combat graph and configured sword montages."),
                new("Hold / hide sword", root + "/GA_PlayerHeldSword", "Generated held-item ability. Visibility is controlled by Sword settings."),
                new("Weapon actor", root + "/BP_PlayerSword_Weapon", "Suit-local weapon actor; the base-game sword is not overwritten."),
                new("Weapon model", root + "/SM_PlayerSword", profile.SwordCombat?.CustomModel is { } model
                    ? "Custom model: " + model.SourceName : "Cloned from the selected cooked static mesh."),
            ];
        }
        return style.HeldItemAbilityPackages.Select(p => new Entry("Held-item ability", p, "Required by the selected fighting-style bundle."))
            .Concat(style.BridgeHeldItemAbilityPackages.Select(p => new Entry("Held-item bridge", p, "Coordinated held-item support.")))
            .Concat(style.RequiredMontageAnimSetPackages.Select(p => new Entry("Combat animations", p, "Source for the suit-local combat animation bundle.")))
            .Concat(style.RequiredLayerSlices.Select(p => new Entry("Combat pose layer", p.SourcePackage, "Only required context rows are cloned; ordinary locomotion is preserved.")))
            .DistinctBy(e => e.Package, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IReadOnlyList<Entry> HeldEntries(AbilityLoadoutProfile profile, string playablePackage)
    {
        var path = UnrealPathUtil.NormalizePackagePath(playablePackage).Split('/');
        if (!HeldItemService.Independent(profile) || path.Length < 5 || path[1] != "Game" || path[2] != "Mods") return [];
        return profile.HeldItems!.SelectMany(item => {
            var root = HeldItemService.Root(path[3], item);
            var detail = $"{item.Hand} hand · {HeldItemsForm.VisibilityLabel(item.Visibility)}. Independent of fighting style; edit/remove in Held items.";
            return new Entry[] { new(item.Name + " · held-item ability", root + "/GA_HeldItem", detail),
                new(item.Name + " · held actor", HeldItemService.ActorPackage(path[3], item), "Native item template: " + item.TemplateId + $" · {item.Effects.Count} cosmetic effect(s)"),
                new(item.Name + " · model", root + "/SM_HeldItem", item.CustomModel is { } model ? "Custom model: " + model.SourceName : item.MeshPackage) };
        }).ToList();
    }
}
