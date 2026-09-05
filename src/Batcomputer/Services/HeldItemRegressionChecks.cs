namespace Batcomputer;

internal static class HeldItemRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        void Check(bool ok, string title) { output.WriteLine((ok ? "PASS: " : "FAIL: ") + title); if (!ok) failures.Add(title); }
        var legacy = new AbilityLoadoutProfile { FightingStyleId = "player-sword", SwordCombat = new() { Visibility = HeldWeaponVisibility.Always,
            MeshPackage = "/Game/Test/SM_Custom", MaterialPackage = "/Game/Test/MI_Custom", CustomModel = new() { SourceName = "sword.obj", ObjText = "v 0 0 0" } } };
        var snapshot = System.Text.Json.JsonSerializer.Serialize(legacy);
        Check(HeldItemService.Resolve(legacy).Count == 1 && System.Text.Json.JsonSerializer.Serialize(legacy) == snapshot, "legacy held-item resolution does not mutate the saved profile");
        HeldItemService.Migrate(legacy);
        var item = legacy.HeldItems!.Single(); var fingerprint = AbilityLoadoutService.ConfigurationFingerprint(legacy);
        HeldItemService.Migrate(legacy);
        Check(legacy.HeldItems!.Count == 1 && item.Id == "legacy_sword" && item.Visibility == HeldWeaponVisibility.Always && item.MaterialPackage == "/Game/Test/MI_Custom" && item.CustomModel!.SourceName == "sword.obj" && fingerprint == AbilityLoadoutService.ConfigurationFingerprint(legacy), "legacy sword item migration is complete and idempotent");
        var copy = AbilityExplorerForm.CloneProfile(legacy); copy.HeldItems![0].CustomModel!.Scale = 2;
        Check(legacy.HeldItems[0].CustomModel!.Scale == 1 && fingerprint != AbilityLoadoutService.ConfigurationFingerprint(copy), "held-item models deep-clone and invalidate generated assets");
        foreach (var change in new Action<HeldItemSettings>[] { i => i.Name += " edited", i => i.Id = "other", i => i.Hand = HeldItemHand.Left,
            i => i.Visibility = HeldWeaponVisibility.OutsideCombat, i => i.TemplateId = "baseball-bat", i => i.MeshPackage = "/Game/Test/SM_Other", i => i.MaterialPackage = "/Game/Test/MI_Other" }) {
            var changed = AbilityExplorerForm.CloneProfile(legacy); change(changed.HeldItems![0]);
            Check(fingerprint != AbilityLoadoutService.ConfigurationFingerprint(changed), "held-item edits participate in cache invalidation");
        }
        AbilityDependencyService.ApplyFightingStyle(legacy, FightingStyleProfileService.Find("batman-martial-arts")!);
        Check(legacy.HeldItems.Count == 1 && legacy.SwordCombat is null, "switching away from sword preserves independent held item");
        AbilityDependencyService.ClearFightingStyle(legacy);
        Check(legacy.HeldItems.Count == 1, "clearing fighting style preserves held item");
        var fresh = new AbilityLoadoutProfile(); AbilityDependencyService.ApplyFightingStyle(fresh, FightingStyleProfileService.Find("player-sword")!);
        Check(fresh.HeldItems is { Count: 0 }, "selecting sword combat does not silently grant a held item");
        var empty = new AbilityLoadoutProfile { FightingStyleId = "player-sword", HeldItems = [] }; HeldItemService.Migrate(empty);
        Check(HeldItemService.Resolve(empty).Count == 0, "an intentionally removed item is not recreated by migration");
        var serialized = System.Text.Json.JsonSerializer.Deserialize<AbilityLoadoutProfile>(System.Text.Json.JsonSerializer.Serialize(legacy))!;
        Check(AbilityLoadoutService.ConfigurationFingerprint(serialized) == AbilityLoadoutService.ConfigurationFingerprint(legacy), "independent held items survive JSON roundtrip");
        Check(HeldItemService.Validate([new() { Id = "../escape" }]).Count > 0 && HeldItemService.Validate([new() { MeshPackage = "/Game/../escape" }]).Count > 0 && HeldItemService.Validate([new(), new()]).Count > 0, "held-item paths, identities and duplicate hands are rejected");
        Check(HeldItemService.Validate([new() { Hand = HeldItemHand.Right }, new() { Hand = HeldItemHand.Left }]).Count == 0, "one independent item per hand is supported");
        Check(HeldItemService.RequestTags(HeldWeaponVisibility.OutsideCombat, "Status.Test.Item").SequenceEqual(["Status.Test.Item"]) && HeldItemService.BlockTags(HeldWeaponVisibility.OutsideCombat).Contains("Status.InCombat") && HeldItemService.BlockTags(HeldWeaponVisibility.OutsideCombat).Contains("Abilities.Combat.MeleeAttack"), "outside-combat visibility blocks combat/empty-space attacks without using native baton request tags");
        Check(!HeldItemService.SupportsSword(new() { Hand = HeldItemHand.Left }) && !HeldItemService.SupportsSword(new() { Visibility = HeldWeaponVisibility.OutsideCombat }) && HeldItemService.SupportsSword(new()), "sword compatibility requires a visible right-hand melee item");
        var presentation = new AbilityLoadoutProfile { HeldItems = [new() { Id = "prop", Name = "My prop" }] };
        var tagProject = new NativeSuitProject { AbilityLoadout = presentation, TargetPackages = new() { Playable = "/Game/Mods/ActualNamespace/Characters/BP_Test" } };
        Check(HeldItemService.TagRows(tagProject).Single().PawnTag == "Status.Batcomputer.HeldItem.ActualNamespace.prop" && PawnTagConfigService.Render(HeldItemService.TagRows(tagProject)).Contains("Status.Batcomputer.HeldItem.ActualNamespace.prop"), "persistent item requests are registered as independent suit-local gameplay tags");
        Check(AbilityLoadoutPresentation.HeldEntries(presentation, "/Game/Mods/ActualNamespace/Characters/BP_Test").Count == 3 &&
            AbilityLoadoutPresentation.HeldEntries(presentation, "/Game/Mods/ActualNamespace/Characters/BP_Test").All(e => e.Package.StartsWith("/Game/Mods/ActualNamespace/HeldItems/prop/")), "held items appear in current loadout without a fighting-style selection");
        Check(GameAssetRefreshService.HeldItemFilters.All(f => GameAssetRefreshService.AllCharacterFilters.Contains(f) && GameAssetRefreshService.DeveloperResearchFilters.Contains(f)), "full/first-time extraction includes held-item donors");
        Check(HeldItemService.Templates.Length == 14 && HeldItemService.Templates.Select(t => t.Id).Distinct().Count() == 14,
            "four melee and ten cosmetic held-item examples have stable unique identities");
        foreach (var template in HeldItemService.Templates) {
            Check(HeldItemService.SupportsSword(new() { TemplateId = template.Id }) == template.Melee,
                "cosmetic props cannot satisfy a melee-weapon dependency: " + template.Id);
            Check(new[] { template.Actor, template.Mesh, template.ActorMesh ?? template.Mesh }.All(p =>
                new[] { GameAssetRefreshService.AllCharacterFilters, GameAssetRefreshService.DeveloperResearchFilters }.All(filters =>
                    filters.Any(f => ("Content/" + p[6..]).StartsWith(f, StringComparison.OrdinalIgnoreCase)))), "extraction covers actor and mesh for " + template.Id);
        }
        Check(HeldItemService.Templates.Where(t => !t.Melee).All(t => t.Actor == HeldItemService.PassiveActor && t.ActorMesh == HeldItemService.PassiveMesh),
            "cosmetic examples do not clone live gadget/projectile controllers");
        Check(HeldItemService.Templates.Select(t => UnrealPathUtil.AssetName(HeldItemService.ActorPackage("Test", new() { TemplateId = t.Id }))).Distinct().Count() == HeldItemService.Templates.Length,
            "different native held actors have distinct blueprint class names in multi-suit builds");
        var fx = new HeldItemSettings { Effects = [new() { X=3,Y=-4,Z=12,Scale=.5f }] };
        var fxCopy = fx.Clone(); fxCopy.Effects[0].X = 90;
        Check(fx.Effects[0].X == 3 && HeldItemEffectService.Validate(fx.Effects).Count == 0, "effect editor clones placements privately");
        Check(HeldItemEffectService.Validate([new(){Scale=float.NaN}]).Count > 0 && HeldItemEffectService.Validate([new(){PresetId="unknown"}]).Count > 0 &&
            HeldItemEffectService.Validate([new(),new(),new(),new()]).Count > 0, "invalid effects and excessive effect counts are rejected");
        Check(HeldItemEffectService.Presets.Length == 12 && HeldItemEffectService.Presets.Select(p=>p.Id).Distinct().Count()==12 &&
            HeldItemEffectService.ExtractionFilters.All(f => GameAssetRefreshService.AllCharacterFilters.Contains(f) && GameAssetRefreshService.DeveloperResearchFilters.Contains(f)), "twelve VFX presets are included in first-time/full/research extraction");
        var fxProfile = new AbilityLoadoutProfile { HeldItems=[fx], FightingStyleId="player-sword", SwordCombat=new() };
        var beforeFx = AbilityLoadoutService.ConfigurationFingerprint(fxProfile); fx.Effects[0].Yaw=45;
        Check(beforeFx != AbilityLoadoutService.ConfigurationFingerprint(fxProfile), "effect placement changes invalidate generated assets");
        var statusBefore = AbilityLoadoutService.ConfigurationFingerprint(fxProfile); fxProfile.SwordCombat.HitStatus = new(){PresetId="stun",DurationSeconds=3};
        var roundtrip=System.Text.Json.JsonSerializer.Deserialize<AbilityLoadoutProfile>(System.Text.Json.JsonSerializer.Serialize(fxProfile))!;
        Check(statusBefore != AbilityLoadoutService.ConfigurationFingerprint(fxProfile) && AbilityLoadoutService.ConfigurationFingerprint(roundtrip)==AbilityLoadoutService.ConfigurationFingerprint(fxProfile), "hit status and effects survive save/reload and invalidate generated assets");
        Check(MeleeStatusEffectService.Validate(new(){PresetId="stun",DurationSeconds=0}).Count>0 && MeleeStatusEffectService.Validate(new(){PresetId="freeze"}).Count>0 &&
            MeleeStatusEffectService.Validate(new(){PresetId="smoke",DurationSeconds=2}).Count==0, "status settings reject untraced effects and unbounded duration");
        var combatCopy = fxProfile.SwordCombat.Clone(); combatCopy.HitStatus.DurationSeconds=8;
        Check(fxProfile.SwordCombat.HitStatus.DurationSeconds==3, "combat status edits do not mutate the original settings");
    }
}
