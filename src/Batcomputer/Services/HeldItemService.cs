using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Independent OnSpawn held-item grants, with no combat/style mutation.</summary>
internal static class HeldItemService
{
    internal const string PassiveActor = "/Game/Models/Gadgets/GA_GelApplicator/BP_ExplosiveGelItem";
    internal const string PassiveMesh = "/Game/Models/Gadgets/GA_GelApplicator/SM_GelApplicator";
    internal sealed record Template(string Id, string Label, string Actor, string Mesh,
        string MeshComponent = "Weapon Mesh", string? ActorMesh = null, bool Melee = true,
        string Notes = "Native melee actor and hitbox. Select a fighting style separately.", float[]? PrimitiveData = null);
    private static Template Prop(string id, string label, string mesh, string notes, float[]? primitiveData = null) =>
        new(id, label, PassiveActor, mesh, "StaticMesh_GEN_VARIABLE", PassiveMesh, false, notes, primitiveData);
    internal static readonly Template[] Templates = [
        new("sword", "Sword / katana", "/Game/Characters/Equipment/Sword/BP_Katana_Weapon", "/Game/Models/Props/SM_Katana"),
        new("baseball-bat", "Baseball bat", "/Game/Characters/Equipment/BaseballBat/BP_BaseballBat_Weapon", "/Game/Models/Props/SM_BaseBallBat"),
        new("stun-baton", "Stun baton (native visuals)", "/Game/Characters/Equipment/StunBaton/BP_StunBaton_Weapon", "/Game/Models/Props/SM_StunBaton"),
        new("closed-umbrella", "Closed umbrella", "/Game/Characters/Equipment/PenguinUmbrella/ClosedUmbrella/BP_UmbrellaClosed_Weapon", "/Game/Models/Props/SM_UmbrellaClosed",
            Notes: "Penguin's closed melee umbrella. No umbrella gadgets or special attacks are granted."),
        Prop("gel-applicator", "Small prop · gel spray can", PassiveMesh, "Native held spray-can actor. Cosmetic only; does not spray or place explosive gel."),
        Prop("plant-spray", "Small prop · plant spray", "/Game/Models/Gadgets/GA_PlantSpray/SM_PlantSpray", "Native plant-spray model on a passive held actor. Does not grant plant interactions."),
        Prop("laser-pointer", "Small prop · Catwoman laser pointer", "/Game/Models/Gadgets/GA_LaserPointer_CatWoman/SM_GA_LaserPointer_CatWoman", "Passive laser-pointer model. Does not spawn a kitten or activate a beam.", [0,0,0,0,0,0,1110]),
        Prop("batarang", "Small prop · batarang", "/Game/Models/Gadgets/GA_Batarang/SM_GA_Batarang", "Passive batarang model. Does not replace your equipped batarang or grant throwing.", [0,.291667f,.291667f,.291667f,1,0,1110]),
        Prop("birdarang", "Small prop · birdarang", "/Game/Models/Gadgets/GA_Birdarang/SM_GA_Birdarang", "Passive Robin birdarang model. Does not grant a projectile or equipment slot.", [0,0,0,0,0,0,1110]),
        Prop("ninja-star", "Small prop · ninja star", "/Game/Models/Gadgets/GA_NinjaStar/SM_GA_NinjaStar", "Passive throwing-star model. No enemy projectile/controller is copied."),
        Prop("smoke-bomb", "Small prop · smoke bomb", "/Game/Models/Props/SM_SmokeBomb", "Passive smoke-bomb model. Does not explode or create smoke."),
        Prop("baseball", "Small prop · baseball", "/Game/Models/Props/SM_BaseBall", "Passive baseball model. Does not grant throwing or projectile behavior."),
        Prop("robin-baton-prop", "Prop · Robin baton (static)", "/Game/Models/Gadgets/GA_Baton_Robin/SM_GA_Baton_Robin", "Static cosmetic version of Robin's baton. No skeletal folding, cape-collision code or melee hitbox. Use a melee template for player weapon attacks."),
        Prop("goggles-prop", "Prop · Gray Ghost goggles", "/Game/Characters/Attachments/Hat/GrayGhost/SM_Hat_GrayGhostGoggles", "Cosmetic goggles held in the hand, not worn on the head. Native shape/origin; grip needs in-game testing."),
    ];
    internal static string Root(string mod, HeldItemSettings item) => $"/Game/Mods/{mod}/HeldItems/{item.Id}";
    internal static string SetPackage(string mod, HeldItemSettings item) => Root(mod, item) + "/AS_HeldItem";
    // UAssetAPI's blueprint schema cache is keyed by class name, not full package. Different
    // native actors must not all be renamed BP_HeldItem_C within one multi-suit build.
    internal static string ActorPackage(string mod, HeldItemSettings item) => Root(mod, item) + "/BP_HeldItem_" + item.TemplateId.Replace('-', '_');
    internal static bool Independent(AbilityLoadoutProfile? profile) => profile?.HeldItems is not null;
    internal static IReadOnlyList<HeldItemSettings> Resolve(AbilityLoadoutProfile? profile)
    {
        if (profile?.HeldItems is { } items) return items;
        if (profile?.FightingStyleId != SwordCombatService.StyleId) return [];
        var legacy = profile!.SwordCombat ?? new();
        return [new() { Id = "legacy_sword", Name = "Sword", Visibility = legacy.Visibility,
            MeshPackage = legacy.MeshPackage, MaterialPackage = legacy.MaterialPackage, CustomModel = legacy.CustomModel?.Clone() }];
    }
    // Migration is explicit and idempotent. Called on a private editor copy or a loaded project,
    // never from a read-only validation/presentation pass.
    internal static void Migrate(AbilityLoadoutProfile? profile)
    {
        if (profile is null || profile.HeldItems is not null) return;
        profile.HeldItems = Resolve(profile).Select(i => i.Clone()).ToList();
    }
    internal static string RequestTag(string mod, HeldItemSettings item) => $"Status.Batcomputer.HeldItem.{mod}.{item.Id}";
    internal static IEnumerable<PawnTagConfigService.TagRow> TagRows(NativeSuitProject project)
    {
        var segments = UnrealPathUtil.NormalizePackagePath(project.TargetPackages.Playable).Split('/');
        if (!Independent(project.AbilityLoadout) || segments.Length < 5 || segments[1] != "Game" || segments[2] != "Mods") return [];
        return project.AbilityLoadout!.HeldItems!.Where(i => Persistent(i.Visibility)).Select(i =>
            new PawnTagConfigService.TagRow(RequestTag(segments[3], i), $"{project.DisplayName}: held item {i.Name}"));
    }
    internal static string[] RequestTags(HeldWeaponVisibility mode, string requestTag = "Status.Batcomputer.HeldItem") => mode switch {
        HeldWeaponVisibility.WhileAttacking => ["Abilities.Combat.MeleeAttack"],
        HeldWeaponVisibility.InCombat => ["Status.InCombat", "Abilities.Combat.MeleeAttack"],
        HeldWeaponVisibility.Always or HeldWeaponVisibility.OutsideCombat => [requestTag],
        _ => throw new InvalidDataException("Unknown held-item visibility")
    };
    internal static string[] BlockTags(HeldWeaponVisibility mode) => mode == HeldWeaponVisibility.OutsideCombat
        ? ["Status.BlockItemGA", "Status.InCombat", "Abilities.Combat.MeleeAttack"] : ["Status.BlockItemGA"];
    internal static bool Persistent(HeldWeaponVisibility mode) => mode is HeldWeaponVisibility.Always or HeldWeaponVisibility.OutsideCombat;
    internal static bool ValidPackage(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') &&
        path.Split('/').Skip(1).All(p => p.Length > 0 && p.All(c => char.IsLetterOrDigit(c) || c == '_')) &&
        ExtractedPackagePathService.IsContentPackagePath(path);
    internal static IReadOnlyList<string> Validate(IReadOnlyList<HeldItemSettings> items)
    {
        var errors = new List<string>();
        if (items.Count > 2 || items.GroupBy(i => i.Hand).Any(g => g.Count() > 1)) errors.Add("Use at most one independent held item per hand.");
        if (items.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) errors.Add("Held-item IDs must be unique.");
        foreach (var item in items) {
            errors.AddRange(HeldItemEffectService.Validate(item.Effects));
            if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64 || item.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) errors.Add("Invalid held-item identity.");
            if (!Templates.Any(t => t.Id == item.TemplateId)) errors.Add($"Unsupported held-item template: {item.TemplateId}");
            if (!Enum.IsDefined(item.Hand) || !Enum.IsDefined(item.Visibility)) errors.Add("Select a supported hand and visibility rule.");
            if (!ValidPackage(item.MeshPackage) || (!string.IsNullOrWhiteSpace(item.MaterialPackage) && !ValidPackage(item.MaterialPackage))) errors.Add("Held-item mesh/material must be cooked package paths.");
            if (item.CustomModel is { } model) { try { WeaponModelService.Validate(model); } catch (Exception ex) { errors.Add(ex.Message); } }
        }
        return errors;
    }
    internal static bool SupportsSword(HeldItemSettings item) => item.Hand == HeldItemHand.Right &&
        item.Visibility != HeldWeaponVisibility.OutsideCombat && Templates.Any(t => t.Id == item.TemplateId && t.Melee);

    internal static void Generate(AbilityLoadoutProfile profile, string extracted, string staged, string mod,
        string dprdPath, Usmap mappings, IList<string> log)
    {
        var items = profile.HeldItems ?? [];
        var errors = Validate(items); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var mutation = new AbilityAssetMutationService();
        var sets = mutation.InspectDprdAbilitySets(dprdPath);
        if (!sets.Success) throw new InvalidDataException(sets.Error);
        var ordered = sets.AbilitySets.Select(s => s.PackagePath).ToList();
        foreach (var item in items) {
            var root = Root(mod, item); var template = Templates.Single(t => t.Id == item.TemplateId);
            var actorPackage = ActorPackage(mod, item); var actorName = UnrealPathUtil.AssetName(actorPackage);
            using var c = new SwordCombatService.Context(extracted, staged, mod, mappings, root);
            if (item.CustomModel is not null) WeaponModelService.Bake(item.CustomModel, extracted, AppSettings.Current.EffectiveUsmapPath()!, staged, root + "/SM_HeldItem");
            var mesh = item.CustomModel is null ? c.Clone(item.MeshPackage, root + "/SM_HeldItem") : c.ReadStaged(root + "/SM_HeldItem");
            if (!mesh.Exports.Any(e => e.GetExportClassType()?.ToString() == "StaticMesh")) throw new InvalidDataException("Held item requires a StaticMesh.");
            var weapon = c.Clone(template.Actor, actorPackage, new() { [template.ActorMesh ?? template.Mesh] = root + "/SM_HeldItem" });
            var weaponMesh = weapon.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == template.MeshComponent);
            if (!template.Melee) VerifyPassiveActor(weapon, weaponMesh);
            if (template.PrimitiveData is { } primitive) {
                weaponMesh.Data.RemoveAll(p => p.Name.ToString() == "CustomPrimitiveData");
                weaponMesh.Data.Add(new StructPropertyData(new FName(weapon, "CustomPrimitiveData")) {
                    StructType = new FName(weapon, "CustomPrimitiveData"), Value = [new ArrayPropertyData(new FName(weapon, "Data")) {
                        ArrayType = new FName(weapon, "FloatProperty"), Value = primitive.Select((v, i) => (PropertyData)new FloatPropertyData(new FName(weapon, i.ToString())) { Value = v }).ToArray() }]
                });
                c.Write(weapon, actorPackage);
            }
            if (!string.IsNullOrWhiteSpace(item.MaterialPackage)) {
                var material = c.Clone(item.MaterialPackage, root + "/MI_HeldItem");
                var type = material.Exports.FirstOrDefault(e => e.GetExportClassType()?.ToString() is "Material" or "MaterialInstanceConstant")?.GetExportClassType()?.ToString();
                if (type is null) throw new InvalidDataException("Held-item material is not a Material/MaterialInstanceConstant.");
                var index = SwordCombatService.Obj(weapon, root + "/MI_HeldItem", "MI_HeldItem", "/Script/Engine", type);
                weaponMesh.Data.RemoveAll(p => p.Name.ToString() == "OverrideMaterials");
                weaponMesh.Data.Add(new ArrayPropertyData(new FName(weapon, "OverrideMaterials")) { ArrayType = new FName(weapon, "ObjectProperty"), Value = [new ObjectPropertyData(new FName(weapon, "0")) { Value = index }] });
                weaponMesh.CreateBeforeSerializationDependencies.Add(index); c.Write(weapon, actorPackage);
            }
            HeldItemEffectService.Generate(weapon, weaponMesh, item, c);
            c.Write(weapon, actorPackage);
            var held = c.Clone("/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_Batons", root + "/GA_HeldItem");
            var cdo = held.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
            var managed = cdo.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ManagedItems");
            if (managed.Value.Length != 2) throw new InvalidDataException("Native managed hand slots changed; refresh assets and update Batcomputer.");
            managed.Value = [managed.Value[(int)item.Hand]];
            var actor = SwordCombatService.Obj(held, actorPackage, actorName + "_C", "/Script/Engine", "BlueprintGeneratedClass");
            ((StructPropertyData)managed.Value[0]).Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "ItemActorClass").Value = actor;
            cdo.CreateBeforeSerializationDependencies.Add(actor);
            cdo.Data.RemoveAll(p => p.Name.ToString() is "GetOutAnim" or "PutAwayAnim" or "ActivationOwnedTags");
            // A decorative prop must not set baton pose tags or switch the character's animation context.
            foreach (var export in held.Exports.OfType<NormalExport>()) {
                foreach (var property in export.Data.OfType<StructPropertyData>().Where(p => p.Name.ToString() is "TagsToPushToOwnerWhenAttached" or "AbilityTags"))
                    foreach (var tags in property.Value.OfType<GameplayTagContainerPropertyData>()) tags.Value = tags.Value.Where(t => t.ToString() != "Animation.Equipment.Batons").ToArray();
            }
            SetTags(held, cdo, "ASCOwnedTagsToGetOutItem", RequestTags(item.Visibility, RequestTag(mod, item)));
            SetTags(held, cdo, "ASCOwnedTagsToBlockItem", BlockTags(item.Visibility));
            if (Persistent(item.Visibility)) {
                var owned = (StructPropertyData)cdo.Data.Single(p => p.Name.ToString() == "ASCOwnedTagsToGetOutItem").Clone();
                owned.Name = new FName(held, "ActivationOwnedTags"); cdo.Data.Add(owned);
            }
            c.Write(held, root + "/GA_HeldItem");
            // Use the native set's serialized grant structure, but keep ONLY this OnSpawn held-item grant.
            var set = c.Clone(SwordCombatService.NativeMelee, SetPackage(mod, item));
            var data = set.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "TtAbilitySet");
            var grants = data.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "GrantedGameplayAbilities");
            grants.Value = [grants.Value[0]]; data.Data.RemoveAll(p => p.Name.ToString() != "GrantedGameplayAbilities");
            var grant = (StructPropertyData)grants.Value[0];
            var ga = SwordCombatService.Obj(set, root + "/GA_HeldItem", "GA_HeldItem_C", "/Script/Engine", "BlueprintGeneratedClass");
            grant.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "Ability").Value = ga;
            grant.Value.OfType<IntPropertyData>().Single(p => p.Name.ToString() == "AbilityLevel").Value = 1;
            grant.Value.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "InputTag").Value.OfType<NamePropertyData>().Single().Value = new FName(set, "None");
            data.CreateBeforeSerializationDependencies.Add(ga); c.Write(set, SetPackage(mod, item));
            ordered.Add(SetPackage(mod, item));
            log.Add($"Held item: {item.Name}, {item.Hand} hand, {item.Visibility}; independent of fighting style.");
        }
        if (items.Count > 0) { var written = mutation.SetDprdAbilitySets(dprdPath, ordered); if (!written.Success) throw new InvalidDataException(written.Error); }
        if (!Verify(profile, extracted, staged, mod, mappings, out var error)) throw new InvalidDataException(error);
    }
    private static void SetTags(UAsset asset, NormalExport cdo, string property, string[] values) =>
        cdo.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == property).Value.OfType<GameplayTagContainerPropertyData>().Single().Value = values.Select(t => new FName(asset, t)).ToArray();

    internal static bool Verify(AbilityLoadoutProfile profile, string extracted, string staged, string mod, Usmap mappings, out string error)
    {
        try {
            var items = profile.HeldItems ?? [];
            if (Validate(items).Count > 0) throw new InvalidDataException("Invalid held-item configuration.");
            foreach (var item in items) {
                var root = Root(mod, item); using var c = new SwordCombatService.Context(extracted, staged, mod, mappings, root);
                var held = c.ReadStaged(root + "/GA_HeldItem");
                var cdo = held.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
                var managed = cdo.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ManagedItems");
                if (managed.Value.Length != 1) throw new InvalidDataException("Expected one managed item per grant.");
                var entry = (StructPropertyData)managed.Value[0];
                if (SwordCombatService.Package(held, entry.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "ItemActorClass").Value) != ActorPackage(mod, item)) throw new InvalidDataException("Wrong held actor.");
                var request = (NormalExport)entry.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "SlotRequestData").Value.ToExport(held);
                var location = (NormalExport)request.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "PrimarySlotData").Value.ToExport(held);
                var tag = location.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "SlotTag").Value.OfType<NamePropertyData>().Single().Value.ToString();
                if (tag != (item.Hand == HeldItemHand.Right ? "LAM.RightHand" : "LAM.LeftHand")) throw new InvalidDataException("Wrong held hand.");
                string[] Tags(string prop) => cdo.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == prop)?.Value.OfType<GameplayTagContainerPropertyData>().Single().Value.Select(t => t.ToString()).ToArray() ?? [];
                if (!Tags("ASCOwnedTagsToGetOutItem").SequenceEqual(RequestTags(item.Visibility, RequestTag(mod, item))) || !Tags("ASCOwnedTagsToBlockItem").SequenceEqual(BlockTags(item.Visibility)) ||
                    !Tags("ActivationOwnedTags").SequenceEqual(Persistent(item.Visibility) ? RequestTags(item.Visibility, RequestTag(mod, item)) : [])) throw new InvalidDataException("Held-item visibility mismatch.");
                if (Tags("AbilityTags").Contains("Animation.Equipment.Batons") || request.Data.OfType<StructPropertyData>().Where(p => p.Name.ToString() == "TagsToPushToOwnerWhenAttached").SelectMany(p => p.Value.OfType<GameplayTagContainerPropertyData>()).SelectMany(p => p.Value).Any(t => t.ToString() == "Animation.Equipment.Batons")) throw new InvalidDataException("Held prop changes baton animation context.");
                var template = Templates.Single(t => t.Id == item.TemplateId);
                var actor = c.ReadStaged(ActorPackage(mod, item)); var mesh = actor.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == template.MeshComponent);
                HeldItemEffectService.Verify(actor, item);
                if (SwordCombatService.Package(actor, mesh.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "StaticMesh").Value) != root + "/SM_HeldItem") throw new InvalidDataException("Wrong held mesh.");
                if (!template.Melee) VerifyPassiveActor(actor, mesh);
                if (template.PrimitiveData is { } expected && !(mesh.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "CustomPrimitiveData").Value
                    .OfType<ArrayPropertyData>().Single().Value.OfType<FloatPropertyData>().Select(p => p.Value).SequenceEqual(expected))) throw new InvalidDataException("Held prop lost native material primitive data.");
                c.ReadStaged(root + "/SM_HeldItem");
                var grants = new AbilityAssetMutationService().InspectAbilitySet(c.PathFor(SetPackage(mod, item)));
                if (!grants.Success || grants.GameplayAbilities.Count != 1 || grants.GameplayAbilities[0].PackagePath != root + "/GA_HeldItem") throw new InvalidDataException("Held item must grant only its own OnSpawn ability.");
            }
            error = ""; return true;
        } catch (Exception ex) { error = "Held-item verification: " + ex.Message; return false; }
    }

    private static void VerifyPassiveActor(UAsset actor, NormalExport mesh)
    {
        var cls = actor.Exports.Single(e => e.GetExportClassType()?.ToString() == "BlueprintGeneratedClass");
        if (!cls.SuperIndex.IsImport() || cls.SuperIndex.ToImport(actor).ObjectName.ToString() != "Actor" ||
            actor.Exports.Any(e => e.GetExportClassType()?.ToString() is "Function" or "HitBoxComponent"))
            throw new InvalidDataException("Cosmetic held props require the native passive Actor shell without gadget or combat scripts.");
        var collision = mesh.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "BodyInstance");
        if (collision.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "CollisionProfileName").Value.ToString() != "NoCollision")
            throw new InvalidDataException("Passive held prop must preserve NoCollision.");
    }
}
