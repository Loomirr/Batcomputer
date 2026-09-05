namespace Batcomputer;

internal static class PlayerMeleeAdapterRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        void Check(bool ok, string title) { output.WriteLine((ok ? "PASS: " : "FAIL: ") + title); if (!ok) failures.Add(title); }
        foreach (var id in new[] { PlayerMeleeAdapterService.Bat, PlayerMeleeAdapterService.Baton }) {
            var style = FightingStyleProfileService.Find(id)!;
            Check(style is not null && style.MeleeAbilitySetPackage == SwordCombatService.NativeMelee &&
                style.HeldItemAbilityPackages.Count == 0, "player weapon preset keeps combat separate from held items: " + id);
            var profile = new AbilityLoadoutProfile();
            AbilityDependencyService.ApplyFightingStyle(profile, style!);
            Check(SwordCombatService.Enabled(profile) && profile.HeldItems is { Count: 0 } &&
                PlayerMeleeAdapterService.Validate(profile).Count == 0, "new adapter gets verified defaults without silently adding a prop: " + id);
            Check(AbilityDependencyService.Build(new() { AbilityLoadout = profile }, equipmentCatalog: []).Issues.Any(i =>
                i.Severity == AbilityDependencySeverity.Error && i.Message.Contains("right-hand")), "adapter blocks missing held item: " + id);
            profile.HeldItems!.Add(new() { Id = "kept", Name = "Custom held prop" });
            profile.SwordCombat!.AttackSpeed = 1.2f; profile.SwordCombat.RequiresCombatTarget = true;
            AbilityDependencyService.ApplyFightingStyle(profile, style!);
            Check(profile.SwordCombat.AttackSpeed == 1.2f && profile.SwordCombat.RequiresCombatTarget && profile.HeldItems.Single().Id == "kept", "reapplying same adapter retains user settings: " + id);
            var fingerprint = AbilityLoadoutService.ConfigurationFingerprint(profile);
            foreach (var change in new Action<SwordCombatSettings>[] { s => s.AttackSpeed = 2, s => s.RequiresCombatTarget = false }) {
                var edited = AbilityExplorerForm.CloneProfile(profile); change(edited.SwordCombat!);
                Check(AbilityLoadoutService.ConfigurationFingerprint(edited) != fingerprint, "adapter tuning invalidates cached generated assets: " + id);
            }
            var json = System.Text.Json.JsonSerializer.Serialize(profile);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<AbilityLoadoutProfile>(json)!;
            Check(AbilityLoadoutService.ConfigurationFingerprint(loaded) == fingerprint, "adapter style and tuning survive JSON: " + id);
            var rows = AbilityLoadoutPresentation.BundleEntries(loaded, "/Game/Mods/ActualIdentity/Characters/BP_Test");
            Check(rows.Count == 2 && rows.All(r => r.Label.StartsWith(PlayerMeleeAdapterService.Label(id)) && r.Package.StartsWith("/Game/Mods/ActualIdentity/")), "current loadout shows chosen adapter in actual namespace: " + id);
            loaded.SwordCombat!.AttackMontages[0] = "/Game/Test/AM_Unsafe";
            Check(PlayerMeleeAdapterService.Validate(loaded).Count > 0, "unverified enemy montage substitution is rejected: " + id);
            AbilityDependencyService.ApplyFightingStyle(profile, FightingStyleProfileService.Find("player-sword")!);
            Check(profile.HeldItems.Single().Id == "kept" && profile.SwordCombat!.AttackMontages[1] != PlayerMeleeAdapterService.Shell &&
                profile.AbilitySets.Count(s => s.Enabled && AbilityDependencyService.IsCombatSet(s.PackagePath)) == 1 &&
                fingerprint != AbilityLoadoutService.ConfigurationFingerprint(profile), "changing adapter replaces attack setup, preserves item and sole melee controller: " + id);
        }
        Check(PlayerMeleeAdapterService.Attacks(PlayerMeleeAdapterService.Bat).Count == 2 && PlayerMeleeAdapterService.Attacks(PlayerMeleeAdapterService.Baton).Count == 1,
            "bat has two proven variations; baton has one deliberate slam");
        Check(PlayerMeleeAdapterService.RequiredPackages.All(p => new[] { GameAssetRefreshService.AllCharacterFilters, GameAssetRefreshService.DeveloperResearchFilters }
            .All(filters => filters.Any(f => ("Content/" + p[6..]).StartsWith(f, StringComparison.OrdinalIgnoreCase)))), "first-time/full refresh include every player-adapter animation donor");
        Check(HeldItemService.Resolve(new() { FightingStyleId = PlayerMeleeAdapterService.Bat }).Count == 0, "new adapter identities cannot trigger legacy sword migration");
    }
}
