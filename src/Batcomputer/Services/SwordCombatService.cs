using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Shared player weapon adapter. Sword-named serialized outputs remain stable for existing projects; every mutation is suit-local.</summary>
internal static class SwordCombatService
{
    internal const string StyleId = "player-sword";
    internal const string NativeMelee = "/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Batman";
    private const string PlayerGraph = "/Game/Animation/LEGOfig/_Shared/Combat_Martial/GTSM_Combat_Martial";
    private const string PlayerGa = "/Game/Characters/Abilities/MeleeAbilities/GA_MeleeAttackGTSM";
    private const string MartialGa = "/Game/Characters/Abilities/MeleeAbilities/GA_MeleeAttack_MartialGTSM";
    private const string HeldGa = "/Game/Characters/Abilities/LAMManagedAbilities/GA_Item_Batons";
    private const string NativeWeapon = "/Game/Characters/Equipment/Sword/BP_Katana_Weapon";
    internal static bool Enabled(AbilityLoadoutProfile? p) => PlayerMeleeAdapterService.Enabled(p?.FightingStyleId);
    internal static string Root(string mod) => $"/Game/Mods/{mod}/CombatSword";
    internal static string MeleePackage(string mod) => Root(mod) + "/AS_PlayerSword";
    internal static string[] VisibilityTags(HeldWeaponVisibility mode) => mode switch
    {
        HeldWeaponVisibility.WhileAttacking => ["Abilities.Combat.MeleeAttack.GTSM"],
        HeldWeaponVisibility.InCombat => ["Status.InCombat", "Abilities.Combat.MeleeAttack.GTSM"],
        HeldWeaponVisibility.Always => ["Status.Batons.Request"],
        _ => throw new InvalidDataException("Unknown weapon visibility mode."),
    };

    internal static IReadOnlyList<string> ValidateSettings(SwordCombatSettings settings, bool legacyItem = true)
    {
        var errors = new List<string>();
        errors.AddRange(MeleeStatusEffectService.Validate(settings.HitStatus));
        if (legacyItem && settings.CustomModel is not null)
        {
            try { WeaponModelService.Validate(settings.CustomModel); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        if (legacyItem && (!Enum.IsDefined(settings.Visibility) || settings.Visibility == HeldWeaponVisibility.OutsideCombat)) errors.Add("Select a supported legacy sword visibility mode.");
        if (!float.IsFinite(settings.AttackSpeed) || settings.AttackSpeed < 0.5f || settings.AttackSpeed > 3f)
            errors.Add("Sword attack speed must be between 0.5 and 3.0.");
        bool Valid(string? path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') &&
            !path.Contains('\\') && !path.Contains('.') && !path.Contains(':') && !path.EndsWith('/') &&
            path.Split('/').Skip(1).All(segment => segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c == '_')) &&
            ExtractedPackagePathService.IsContentPackagePath(path);
        if (legacyItem && !Valid(settings.MeshPackage)) errors.Add("Sword mesh must be a cooked content package path (not an OBJ filename).");
        if (legacyItem && !string.IsNullOrWhiteSpace(settings.MaterialPackage) && !Valid(settings.MaterialPackage)) errors.Add("Sword material must be a cooked content package path or blank.");
        if (settings.AttackMontages is not { Count: 4 } || settings.AttackMontages.Any(p => !Valid(p)))
            errors.Add("Supply exactly four cooked attack-montage package paths.");
        return errors;
    }

    internal static void Generate(AbilityLoadoutProfile profile, string extracted, string staged, string mod,
        string dprdPath, string sourceMelee, Usmap mappings, IList<string> log)
    {
        var settings = profile.SwordCombat ?? PlayerMeleeAdapterService.Defaults(profile.FightingStyleId);
        var errors = PlayerMeleeAdapterService.Validate(profile);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        using var c = new Context(extracted, staged, mod, mappings);
        var root = Root(mod);
        MeleeStatusEffectService.Generate(c, mod, settings.HitStatus);
        // Preserve legacy builds. Explicit held-item profiles generate items separately.
        if (!HeldItemService.Independent(profile))
        {
        if (settings.CustomModel is not null)
            WeaponModelService.Bake(settings.CustomModel, extracted, AppSettings.Current.EffectiveUsmapPath()!, staged, root + "/SM_PlayerSword");
        var mesh = settings.CustomModel is null ? c.Clone(settings.MeshPackage, root + "/SM_PlayerSword") : c.ReadStaged(root + "/SM_PlayerSword");
        if (!mesh.Exports.Any(e => e.GetExportClassType()?.ToString() == "StaticMesh"))
            throw new InvalidDataException("The selected sword mesh is not a cooked StaticMesh.");
        var weapon = c.Clone(NativeWeapon, root + "/BP_PlayerSword_Weapon",
            new() { ["/Game/Models/Props/SM_Katana"] = root + "/SM_PlayerSword" });
        var weaponMesh = weapon.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "Weapon Mesh");
        if (!string.IsNullOrWhiteSpace(settings.MaterialPackage))
        {
            var material = c.Clone(settings.MaterialPackage, root + "/MI_PlayerSword");
            var type = material.Exports.FirstOrDefault(e => e.GetExportClassType()?.ToString() is "MaterialInstanceConstant" or "Material")?.GetExportClassType()?.ToString();
            if (type is null) throw new InvalidDataException("The selected sword material is not a Material or MaterialInstanceConstant.");
            var index = Obj(weapon, root + "/MI_PlayerSword", "MI_PlayerSword", "/Script/Engine", type);
            weaponMesh.Data.RemoveAll(p => p.Name.ToString() == "OverrideMaterials");
            weaponMesh.Data.Add(new ArrayPropertyData(new FName(weapon, "OverrideMaterials"))
            {
                ArrayType = new FName(weapon, "ObjectProperty"),
                Value = [new ObjectPropertyData(new FName(weapon, "0")) { Value = index }],
            });
            weaponMesh.CreateBeforeSerializationDependencies.Add(index);
            c.Write(weapon, root + "/BP_PlayerSword_Weapon");
        }
        var held = c.Clone(HeldGa, root + "/GA_PlayerHeldSword");
        var heldCdo = held.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
        var items = heldCdo.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ManagedItems");
        items.Value = items.Value.Take(1).ToArray();
        var actor = Obj(held, root + "/BP_PlayerSword_Weapon", "BP_PlayerSword_Weapon_C", "/Script/Engine", "BlueprintGeneratedClass");
        ((StructPropertyData)items.Value[0]).Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "ItemActorClass").Value = actor;
        heldCdo.CreateBeforeSerializationDependencies.Add(actor);
        heldCdo.Data.RemoveAll(p => p.Name.ToString() is "GetOutAnim" or "PutAwayAnim" or "ActivationOwnedTags");
        var request = heldCdo.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ASCOwnedTagsToGetOutItem");
        request.Value.OfType<GameplayTagContainerPropertyData>().Single().Value = VisibilityTags(settings.Visibility).Select(t => new FName(held, t)).ToArray();
        if (settings.Visibility == HeldWeaponVisibility.Always)
        {
            var owned = (StructPropertyData)request.Clone(); owned.Name = new FName(held, "ActivationOwnedTags");
            heldCdo.Data.Add(owned);
        }
        c.Write(held, root + "/GA_PlayerHeldSword");
        }

        var graph = c.Clone(PlayerGraph, root + "/GTSM_PlayerSword");
        var comboDonor = c.Read("/Game/Animation/LEGOfig/_Shared/Combat_Martial/AM_D0_AttackBack_Start_LtoL2_Minifig");
        var states = graph.Exports.OfType<NormalExport>().Where(e => e.Data.Any(p => p.Name.ToString() == "MeleeAttackAnimMontage")).ToList();
        if (states.Count != 89) throw new InvalidDataException("The player combat graph changed; expected 89 attack states. Refresh assets and update Batcomputer.");
        for (var n = 0; n < states.Count; n++)
        {
            var state = states[n];
            var stateMontage = state.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "MeleeAttackAnimMontage");
            var playerMontage = c.Read(Package(graph, stateMontage.Value));
            var playerMeta = playerMontage.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<ObjectPropertyData>()).Single(p => p.Name.ToString() == "MeleeAttackGameplayTagDataAsset");
            var metaPath = root + $"/DA_PlayerSword_{n}";
            var meta = c.Clone(Package(playerMontage, playerMeta.Value), metaPath);
            var conditions = meta.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<StructPropertyData>()).Single(p => p.Name.ToString() == "AnimTriggerConditions");
            var target = conditions.Value.OfType<BoolPropertyData>().SingleOrDefault(p => p.Name.ToString() == "bRequiresCombatTarget");
            // Allow no-target combat through the donor's native no-target states.
            // Clearing every state admits targeted openers into empty-space selection.
            if (settings.RequiresCombatTarget)
            {
                if (target is null) conditions.Value.Add(new BoolPropertyData(new FName(meta, "bRequiresCombatTarget")) { Value = true });
                else target.Value = true;
            }
            c.Write(meta, metaPath);
            var montagePath = root + $"/AM_PlayerSword_{n}";
            var montage = c.Clone(settings.AttackMontages[n % 4], montagePath);
            if (!montage.Exports.Any(e => e.GetExportClassType()?.ToString() == "AnimMontage") ||
                !montage.Imports.Any(i => i.ObjectName.ToString() == "SKEL_LEGOfig"))
                throw new InvalidDataException("Sword attacks require cooked AnimMontages on SKEL_LEGOfig: " + settings.AttackMontages[n % 4]);
            var metadataExport = montage.Exports.OfType<NormalExport>().Single(e => e.Data.Any(p => p.Name.ToString() == "MeleeAttackGameplayTagDataAsset"));
            var metaImport = Obj(montage, metaPath, UnrealPathUtil.AssetName(metaPath), "/Script/MeleeCombat", "DataAsset_MeleeAttackGameplayTag");
            metadataExport.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "MeleeAttackGameplayTagDataAsset").Value = metaImport;
            metadataExport.CreateBeforeSerializationDependencies.Add(metaImport);
            var anim = montage.Exports.OfType<NormalExport>().Single(e => e.Data.Any(p => p.Name.ToString() == "SequenceLength"));
            var rate = anim.Data.OfType<FloatPropertyData>().SingleOrDefault(p => p.Name.ToString() == "RateScale");
            if (rate is null) anim.Data.Add(new FloatPropertyData(new FName(montage, "RateScale")) { Value = settings.AttackSpeed });
            else rate.Value = settings.AttackSpeed;
            var notifies = anim.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Notifies");
            notifies.Value = notifies.Value.Where(p => !((StructPropertyData)p).Value.OfType<NamePropertyData>().Any(v => v.Name.ToString() == "NotifyName" && v.Value.ToString() == "CounterableAttackActive")).ToArray();
            AddPlayerComboHandoff(montage, anim, notifies, comboDonor);
            var reaction = playerMontage.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<StructPropertyData>())
                .FirstOrDefault(p => p.Name.ToString() == "MeleeHitFrameData")?.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "TakeHitTypeGameplayTag");
            if (reaction is not null)
            {
                var index = montage.GetNameMapIndexList().ToList().FindIndex(x => x.ToString() == "Animation.Combat.TakeHit.Light.Player");
                if (index >= 0) montage.SetNameReference(index, new FString(((NamePropertyData)reaction.Value.Single()).Value.ToString()));
            }
            PlayerMeleeAdapterService.Adapt(montage, profile.FightingStyleId, n, c);
            MeleeStatusEffectService.Apply(montage, c, mod, settings.HitStatus);
            c.Write(montage, montagePath);
            stateMontage.Value = Obj(graph, montagePath, UnrealPathUtil.AssetName(montagePath), "/Script/Engine", "AnimMontage");
            state.CreateBeforeSerializationDependencies.Add(stateMontage.Value);
        }
        c.Write(graph, root + "/GTSM_PlayerSword");
        c.Clone(PlayerGa, root + "/GA_PlayerSword", new() { [PlayerGraph] = root + "/GTSM_PlayerSword" });
        c.Clone(sourceMelee, MeleePackage(mod));
        var mutation = new AbilityAssetMutationService();
        var before = mutation.InspectAbilitySet(c.PathFor(MeleePackage(mod)));
        if (!before.Success || before.GameplayAbilities.Count(g => g.PackagePath == MartialGa) != 1)
            throw new InvalidDataException("Sword adapter requires the original martial attack grant; reset conflicting manual melee edits.");
        var edits = new List<AbilityAssetMutationService.GameplayAbilityEdit> {
            new() { Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Replace, TargetPackagePath = MartialGa, ReplacementPackagePath = root + "/GA_PlayerSword" } };
        if (!HeldItemService.Independent(profile)) edits.Add(new() { Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Add, ReplacementPackagePath = root + "/GA_PlayerHeldSword" });
        var edit = mutation.ApplyGameplayAbilityEdits(c.PathFor(MeleePackage(mod)), edits);
        if (!edit.Success) throw new InvalidDataException(edit.Error);
        var sets = mutation.InspectDprdAbilitySets(dprdPath);
        if (!sets.Success || sets.AbilitySets.Count(s => s.PackagePath == sourceMelee) != 1) throw new InvalidDataException("Sword source melee set is not unique in DPRD.");
        var ordered = sets.AbilitySets.Select(s => s.PackagePath == sourceMelee ? MeleePackage(mod) : s.PackagePath).ToList();
        var written = mutation.SetDprdAbilitySets(dprdPath, ordered);
        if (!written.Success) throw new InvalidDataException(written.Error);
        if (!Verify(profile, extracted, staged, mod, mappings, out var error)) throw new InvalidDataException(error);
        log.Add($"Player {PlayerMeleeAdapterService.Label(profile.FightingStyleId)} adapter: {states.Count} local attack states, {settings.AttackSpeed:0.##}x, target required={settings.RequiresCombatTarget}; " +
            (HeldItemService.Independent(profile) ? "held items configured separately." : $"legacy weapon visibility={settings.Visibility}."));
    }

    internal static bool Verify(AbilityLoadoutProfile profile, string extracted, string staged, string mod, Usmap mappings, out string error)
    {
        try
        {
            var s = profile.SwordCombat ?? PlayerMeleeAdapterService.Defaults(profile.FightingStyleId);
            if (PlayerMeleeAdapterService.Validate(profile).Count > 0) throw new InvalidDataException("Invalid player combat configuration.");
            using var c = new Context(extracted, staged, mod, mappings);
            var root = Root(mod);
            if (!HeldItemService.Independent(profile))
            {
            var held = c.ReadStaged(root + "/GA_PlayerHeldSword");
            var cdo = held.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
            var items = cdo.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ManagedItems");
            if (items.Value.Length != 1) throw new InvalidDataException("Expected one managed right-hand weapon.");
            var actor = ((StructPropertyData)items.Value[0]).Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "ItemActorClass");
            if (Package(held, actor.Value) != root + "/BP_PlayerSword_Weapon") throw new InvalidDataException("Wrong managed sword actor.");
            var tags = cdo.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "ASCOwnedTagsToGetOutItem").Value.OfType<GameplayTagContainerPropertyData>().Single().Value.Select(n => n.ToString());
            if (!tags.SequenceEqual(VisibilityTags(s.Visibility))) throw new InvalidDataException("Sword visibility request differs from saved settings.");
            var owned = cdo.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "ActivationOwnedTags")?.Value.OfType<GameplayTagContainerPropertyData>().Single().Value.Select(n => n.ToString()).ToArray() ?? [];
            if (!owned.SequenceEqual(s.Visibility == HeldWeaponVisibility.Always ? ["Status.Batons.Request"] : Array.Empty<string>())) throw new InvalidDataException("Sword has an incorrect persistent visibility request.");
            }
            var graph = c.ReadStaged(root + "/GTSM_PlayerSword");
            var nativeGraph = c.Read(PlayerGraph);
            var nativeStates = nativeGraph.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<ObjectPropertyData>()).Where(p => p.Name.ToString() == "MeleeAttackAnimMontage").ToList();
            var states = graph.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<ObjectPropertyData>()).Where(p => p.Name.ToString() == "MeleeAttackAnimMontage").ToList();
            if (states.Count != 89) throw new InvalidDataException("Expected 89 sword states.");
            for (var n = 0; n < states.Count; n++)
            {
                var montagePath = root + $"/AM_PlayerSword_{n}";
                if (Package(graph, states[n].Value) != montagePath) throw new InvalidDataException("Sword graph points to stale attack data.");
                var montage = c.ReadStaged(montagePath);
                VerifyComboHandoff(montage);
                PlayerMeleeAdapterService.Verify(montage, profile.FightingStyleId, n);
                MeleeStatusEffectService.Verify(montage, c, mod, s.HitStatus);
                if (montage.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<FloatPropertyData>()).Single(p => p.Name.ToString() == "RateScale").Value != s.AttackSpeed) throw new InvalidDataException("Stale sword attack speed.");
                var meta = montage.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<ObjectPropertyData>()).Single(p => p.Name.ToString() == "MeleeAttackGameplayTagDataAsset");
                var metaPath = root + $"/DA_PlayerSword_{n}";
                if (Package(montage, meta.Value) != metaPath) throw new InvalidDataException("Sword montage has incorrect metadata.");
                var asset = c.ReadStaged(metaPath);
                var conditions = asset.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<StructPropertyData>()).Single(p => p.Name.ToString() == "AnimTriggerConditions");
                var nativeMontage = c.Read(Package(nativeGraph, nativeStates[n].Value));
                var nativeMeta = nativeMontage.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<ObjectPropertyData>()).Single(p => p.Name.ToString() == "MeleeAttackGameplayTagDataAsset");
                var nativeAsset = c.Read(Package(nativeMontage, nativeMeta.Value));
                var nativeConditions = nativeAsset.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<StructPropertyData>()).Single(p => p.Name.ToString() == "AnimTriggerConditions");
                var nativeTarget = nativeConditions.Value.OfType<BoolPropertyData>().SingleOrDefault(p => p.Name.ToString() == "bRequiresCombatTarget");
                var actualTarget = conditions.Value.OfType<BoolPropertyData>().SingleOrDefault(p => p.Name.ToString() == "bRequiresCombatTarget");
                if (s.RequiresCombatTarget ? actualTarget?.Value != true :
                    (actualTarget is null) != (nativeTarget is null) || actualTarget?.Value != nativeTarget?.Value)
                    throw new InvalidDataException("Sword target eligibility differs from the native player state.");
            }
            var mutation = new AbilityAssetMutationService();
            var grants = mutation.InspectAbilitySet(c.PathFor(MeleePackage(mod)));
            if (!grants.Success || grants.GameplayAbilities.Count(g => g.PackagePath == root + "/GA_PlayerSword") != 1 || grants.GameplayAbilities.Count(g => g.PackagePath == root + "/GA_PlayerHeldSword") != (HeldItemService.Independent(profile) ? 0 : 1) || grants.GameplayAbilities.Any(g => g.PackagePath == MartialGa)) throw new InvalidDataException("Sword melee/held-item grant mismatch.");
            if (!HeldItemService.Independent(profile)) { c.ReadStaged(root + "/BP_PlayerSword_Weapon"); c.ReadStaged(root + "/SM_PlayerSword"); }
            error = ""; return true;
        }
        catch (Exception ex) { error = "Sword adapter verification: " + ex.Message; return false; }
    }

    // Enemy montages have hit events but no player breakout events. Without these,
    // buffered player input cannot advance the retained player combat state machine.
    private static readonly string[] ComboNotifies = ["BP_BreakoutIntoNextAttack_Notify", "BP_Breakout_Notify"];

    private static void AddPlayerComboHandoff(UAsset montage, NormalExport anim, ArrayPropertyData notifies, UAsset donor)
    {
        var length = anim.Data.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "SequenceLength").Value;
        var hit = notifies.Value.OfType<StructPropertyData>().First(p => p.Value.OfType<NamePropertyData>()
            .Any(n => n.Name.ToString() == "NotifyName" && n.Value.ToString().Contains("DefaultHitFrame")));
        var hitTime = hit.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "LinkValue").Value;
        var hitEnd = notifies.Value.OfType<StructPropertyData>().Where(p => p.Value.OfType<NamePropertyData>()
            .Any(n => n.Name.ToString() == "NotifyName" && n.Value.ToString() == "MeleeHitBox"))
            .Select(p => p.Value.OfType<FloatPropertyData>().Where(f => f.Name.ToString() is "LinkValue" or "Duration" or "duration").Sum(f => f.Value))
            .DefaultIfEmpty(hitTime).Max();
        var nextTime = Math.Max(hitTime, hitEnd) + 0.04f;
        if (!float.IsFinite(length) || nextTime >= length - 0.02f)
            throw new InvalidDataException("Sword montage has no recovery window after its hit; choose a compatible attack.");
        for (var i = 0; i < ComboNotifies.Length; i++)
        {
            var stem = ComboNotifies[i];
            if (montage.Exports.Any(e => e.GetExportClassType()?.ToString() == stem + "_C")) continue;
            var classPath = "/Game/Animation/AnimNotifies/" + stem;
            var classIndex = Obj(montage, classPath, stem + "_C", "/Script/Engine", "BlueprintGeneratedClass");
            var template = Obj(montage, classPath, "Default__" + stem + "_C", classPath, stem + "_C");
            var originalIndex = hit.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "Notify").Value;
            var original = originalIndex.ToExport(montage);
            // These native blueprint notify instances contain only default data. Preserve
            // their cooked payload: the game's mappings omit their blueprint schemas.
            var added = (Export)donor.Exports.Single(e => e.GetExportClassType()?.ToString() == stem + "_C").Clone();
            added.OuterIndex = original.OuterIndex;
            added.Asset = montage;
            added.ObjectName = new FName(montage, stem + "_Sword");
            added.ClassIndex = classIndex;
            added.TemplateIndex = template;
            added.CreateBeforeCreateDependencies = [original.OuterIndex];
            added.SerializationBeforeCreateDependencies = [classIndex, template];
            added.CreateBeforeSerializationDependencies = [];
            added.SerializationBeforeSerializationDependencies = [];
            added.GeneratePublicHash = true;
            montage.Exports.Add(added);
            var index = FPackageIndex.FromExport(montage.Exports.Count - 1);
            var evt = (StructPropertyData)hit.Clone();
            evt.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "NotifyName").Value = new FName(montage, stem + "_C");
            evt.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "Notify").Value = index;
            evt.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "LinkValue").Value =
                i == 0 ? nextTime : nextTime + (length - nextTime) * 0.65f;
            notifies.Value = [.. notifies.Value, evt];
            anim.CreateBeforeSerializationDependencies.Add(index);
        }
        notifies.Value = notifies.Value.OrderBy(p => ((StructPropertyData)p).Value.OfType<FloatPropertyData>()
            .Single(f => f.Name.ToString() == "LinkValue").Value).ToArray();
        VerifyComboHandoff(montage);
    }

    private static void VerifyComboHandoff(UAsset montage)
    {
        var events = montage.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<ArrayPropertyData>())
            .Single(p => p.Name.ToString() == "Notifies").Value.OfType<StructPropertyData>().ToArray();
        float Time(StructPropertyData e) => e.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "LinkValue").Value;
        var last = events.Where(e => e.Value.OfType<NamePropertyData>().Any(p => p.Name.ToString() == "NotifyName" && p.Value.ToString().Contains("DefaultHitFrame"))).Max(Time);
        var length = montage.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<FloatPropertyData>()).Single(p => p.Name.ToString() == "SequenceLength").Value;
        foreach (var stem in ComboNotifies)
        {
            var matches = events.Where(e => e.Value.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "Notify" &&
                p.Value.IsExport() && p.Value.ToExport(montage).GetExportClassType()?.ToString() == stem + "_C")).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("Sword montage is missing player combo handoff: " + stem);
            var time = Time(matches[0]);
            if (!float.IsFinite(time) || time <= last || time >= length)
                throw new InvalidDataException("Sword combo handoff must follow its hit and precede the animation end.");
            last = time;
        }
    }

    internal static string Package(UAsset asset, FPackageIndex index)
    {
        while (index.IsImport()) { var entry = index.ToImport(asset); if (entry.ClassName.ToString() == "Package") return entry.ObjectName.ToString(); index = entry.OuterIndex; }
        throw new InvalidDataException("Expected external cooked package reference.");
    }
    internal static FPackageIndex Obj(UAsset a, string package, string name, string classPackage, string className)
    {
        var pi = a.Imports.FindIndex(i => i.ClassName.ToString() == "Package" && i.ObjectName.ToString() == package);
        var outer = pi >= 0 ? FPackageIndex.FromImport(pi) : a.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), package, false, a));
        var existing = a.Imports.FindIndex(i => i.OuterIndex.Index == outer.Index && i.ObjectName.ToString() == name && i.ClassName.ToString() == className);
        return existing >= 0 ? FPackageIndex.FromImport(existing) : a.AddImport(new Import(classPackage, className, outer, name, false, a));
    }

    internal sealed class Context : IDisposable
    {
        private readonly string _extracted, _staged, _prefix;
        private readonly Usmap _mappings;
        private readonly string _cache = Path.Combine(Path.GetTempPath(), "Batcomputer-sword-read-" + Guid.NewGuid().ToString("N"));
        public Context(string extracted, string staged, string mod, Usmap mappings, string? allowedRoot = null)
        {
            if (string.IsNullOrEmpty(mod) || mod.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_')) throw new InvalidDataException("Invalid suit namespace.");
            if (allowedRoot is not null && (!allowedRoot.StartsWith($"/Game/Mods/{mod}/", StringComparison.Ordinal) || !HeldItemService.ValidPackage(allowedRoot))) throw new InvalidDataException("Invalid held-item namespace.");
            _extracted = extracted; _staged = Path.GetFullPath(staged); _prefix = (allowedRoot ?? Root(mod)) + "/"; _mappings = mappings;
        }
        public string PathFor(string package)
        {
            if (!package.StartsWith(_prefix, StringComparison.Ordinal) || package.Contains("..") || package.Contains('\\')) throw new InvalidDataException("Sword writes must be suit-local.");
            var path = Path.GetFullPath(Path.Combine(_staged, package[6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset");
            if (!path.StartsWith(_staged + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid stage path.");
            return path;
        }
        private string Resolve(string package)
        {
            foreach (var root in new[] { _staged, _extracted })
            {
                var path = ExtractedPackagePathService.ResolvePackageUasset(root, package);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            throw new FileNotFoundException("Sword dependency is missing; run Full refresh or supply a staged cooked asset: " + package);
        }
        private UAsset Load(string path) => new(path, EngineVersion.VER_UE5_6, _mappings, CustomSerializationFlags.None);
        public UAsset Read(string package)
        {
            var source = Resolve(package);
            var target = Path.Combine(_cache, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(package))), Path.GetFileName(source));
            CopyPackage(source, target); return Load(target);
        }
        public UAsset ReadStaged(string package)
        {
            var asset = Load(PathFor(package));
            if (asset.Imports.Any(i => i.ObjectName.ToString() is "UnknownExport" or "UnknownPackage")) throw new InvalidDataException("Unresolved cooked sword import: " + package);
            return asset;
        }
        public void Write(UAsset asset, string package) => asset.Write(PathFor(package));
        public UAsset Clone(string from, string to, Dictionary<string, string>? redirects = null)
        {
            var dest = PathFor(to);
            CopyPackage(Resolve(from), dest);
            var asset = Load(dest); asset.FolderName = new FString(to);
            var replacements = new Dictionary<string, string>(redirects ?? []) { [from] = to };
            var names = new Dictionary<string, string>();
            foreach (var (oldPath, newPath) in replacements)
            {
                var oldStem = UnrealPathUtil.AssetName(oldPath); var newStem = UnrealPathUtil.AssetName(newPath);
                names[oldPath] = newPath; names[oldStem] = newStem;
                names[oldStem + "_C"] = newStem + "_C"; names["Default__" + oldStem + "_C"] = "Default__" + newStem + "_C";
            }
            var exports = asset.Exports.Select(e => e.ObjectName.ToString()).ToArray();
            var map = asset.GetNameMapIndexList();
            for (var i = 0; i < map.Count; i++) if (names.TryGetValue(map[i].ToString(), out var renamed)) asset.SetNameReference(i, new FString(renamed));
            for (var i = 0; i < exports.Length; i++)
            {
                if (names.TryGetValue(exports[i], out var renamed)) asset.Exports[i].ObjectName = new FName(asset, renamed);
                asset.Exports[i].GeneratePublicHash = true;
            }
            Write(asset, to); return asset;
        }
        private static void CopyPackage(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
            {
                var file = Path.ChangeExtension(source, ext);
                if (File.Exists(file)) File.Copy(file, Path.ChangeExtension(target, ext), true);
            }
        }
        public void Dispose() { if (Directory.Exists(_cache)) { try { Directory.Delete(_cache, true); } catch { } } }
    }
}
