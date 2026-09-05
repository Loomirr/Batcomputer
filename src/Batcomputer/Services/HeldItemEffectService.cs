using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Native cosmetic components only. No blueprint class fields, attacks or owner gameplay tags are added.</summary>
internal static class HeldItemEffectService
{
    internal const string Donor = "/Game/Characters/Equipment/StunBaton/BP_StunBaton_Weapon";
    internal sealed record Preset(string Id, string Label, string Package, string Shape, string Color);
    internal static readonly Preset[] Presets = [
        new("electric-trail", "Electric baton trail", "/Game/VFX/Character/Combat/BatonGoon/Emitters/NS_StunBatonGoon_Trail", "trail", "#77ccff"),
        new("electric-idle", "Idle sparks / electricity", "/Game/VFX/Character/Combat/BatonGoon/Emitters/NS_StunBatonGoon_WeaponIdle", "sparks", "#88ddff"),
        new("blade-trail", "Blade swing trail", "/Game/VFX/Character/Combat/BladeGoon/Emitters/NS_BladeGoon_WeaponTrail", "trail", "#d4eaff"),
        new("baton-trail", "Baton swing smear", "/Game/VFX/Character/Combat/BatonGoon/Emitters/NS_BatonGoon_Trail", "trail", "#ffcd7b"),
        new("umbrella-trail", "Penguin umbrella smear", "/Game/VFX/Character/Combat/Penguin/Emitters/NS_PenguinSmear", "trail", "#b7a4ed"),
        new("baseball-trail", "Baseball projectile trail", "/Game/VFX/Character/Combat/BluntGoon/Emitters/NS_BaseballProjectile_trail", "trail", "#f7efdc"),
        new("smoke-trail", "Smoke trail", "/Game/VFX/Character/Combat/BruteGoon/Emitters/NS_BruteGoon_SmokeTrail", "cloud", "#b6b6c3"),
        new("frost-trail", "Frost weapon trail", "/Game/VFX/Character/Combat/MrFreeze/Emitters/Phase01/NS_Melee_WeaponTrail", "trail", "#95ecff"),
        new("snow-trail", "Falling snow", "/Game/VFX/Character/Combat/MrFreeze/Emitters/Phase01/NS_SnowFallTrail", "sparks", "#ffffff"),
        new("fire-sparks", "Damaged duck fire / sparks", "/Game/VFX/Character/Combat/Penguin/Emitters/NS_PenguinDuck_Damaged_FireAndSparks", "cloud", "#ff993c"),
        new("venom-trail", "Green venom trail", "/Game/VFX/Character/Combat/Bane/Emitters/Bane_Melee/NS_BaneVenom_Trail_Melee", "trail", "#a8ff49"),
        new("ivy-fumes", "Poison Ivy fumes", "/Game/VFX/Character/Combat/PoisonIvy/Emitters/Ivy_Intro/NS_PoisonIvyIntro_Fumes", "cloud", "#b1da6c"),
    ];
    internal static IEnumerable<string> ExtractionFilters => Presets.Select(p => "Content/" + p.Package[6..]);
    internal static IReadOnlyList<string> Validate(IReadOnlyList<HeldItemEffectSettings>? effects)
    {
        var errors = new List<string>();
        if (effects is null) { errors.Add("Invalid effect list."); return errors; }
        if (effects.Count > 3) errors.Add("Use at most three cosmetic effects per item.");
        foreach (var e in effects) {
            if (e is null) { errors.Add("Invalid effect entry."); continue; }
            if (!Presets.Any(p => p.Id == e.PresetId)) errors.Add("Unknown item effect preset.");
            if (new[] { e.X, e.Y, e.Z, e.Pitch, e.Yaw, e.Roll, e.Scale }.Any(v => !float.IsFinite(v)) ||
                e.Scale < .01f || e.Scale > 10 || new[] { e.X, e.Y, e.Z }.Any(v => Math.Abs(v) > 1000) ||
                new[] { e.Pitch, e.Yaw, e.Roll }.Any(v => Math.Abs(v) > 360)) errors.Add("Effect placement must be finite: scale 0.01–10, offsets ±1000 cm, angles ±360°.");
        }
        return errors;
    }
    internal static StructPropertyData Struct(UAsset a, string name, string type, params PropertyData[] value) =>
        new(new FName(a, name)) { StructType = new FName(a, type), Value = value.ToList() };
    internal static ObjectPropertyData Object(UAsset a, string name, FPackageIndex value) => new(new FName(a, name)) { Value = value };
    internal static void Replace(List<PropertyData> data, PropertyData value) { data.RemoveAll(p => p.Name.ToString() == value.Name.ToString()); data.Add(value); }
    internal static void Append(UAsset a, NormalExport export, string name, FPackageIndex index)
    {
        var arr = export.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == name);
        if (arr is null) { arr = new(new FName(a, name)) { ArrayType = new FName(a, "ObjectProperty"), Value = [] }; export.Data.Add(arr); }
        arr.Value = [..arr.Value, Object(a, arr.Value.Length.ToString(), index)];
        export.CreateBeforeSerializationDependencies.Add(index);
    }
    internal static FPackageIndex AddExport(UAsset a, string name, string script, string type, FPackageIndex outer, List<PropertyData> data)
    {
        var cls = SwordCombatService.Obj(a, script, type, "/Script/CoreUObject", "Class");
        var e = new NormalExport { Asset = a, ObjectName = new FName(a, name), ClassIndex = cls, OuterIndex = outer,
            SuperIndex = new(0), TemplateIndex = new(0), ObjectFlags = EObjectFlags.RF_Public | EObjectFlags.RF_ArchetypeObject,
            Data = data, Extras = [], GeneratePublicHash = true, CreateBeforeCreateDependencies = [outer], SerializationBeforeCreateDependencies = [cls],
            CreateBeforeSerializationDependencies = [], SerializationBeforeSerializationDependencies = [] };
        a.Exports.Add(e); a.DependsMap.Add([]); return FPackageIndex.FromExport(a.Exports.Count - 1);
    }
    internal static void Generate(UAsset actor, NormalExport mesh, HeldItemSettings item, SwordCombatService.Context c)
    {
        if (item.Effects.Count == 0) return;
        var errors = Validate(item.Effects); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        // An explicitly edited effects list replaces native decorative emitters, not weapon logic.
        foreach (var native in actor.Exports.OfType<NormalExport>().Where(e => e.GetExportClassType()?.ToString() == "NiagaraComponent")) {
            Replace(native.Data, new BoolPropertyData(new FName(actor, "bAutoActivate")) { Value = false });
            Replace(native.Data, new BoolPropertyData(new FName(actor, "bVisible")) { Value = false });
        }
        var scs = actor.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "SimpleConstructionScript");
        var meshIndex = FPackageIndex.FromExport(actor.Exports.IndexOf(mesh));
        var parent = actor.Exports.OfType<NormalExport>().SingleOrDefault(e => e.GetExportClassType()?.ToString() == "SCS_Node" &&
            e.Data.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "ComponentTemplate" && p.Value.Index == meshIndex.Index));
        var classIndex = FPackageIndex.FromExport(actor.Exports.FindIndex(e => e.GetExportClassType()?.ToString() == "BlueprintGeneratedClass"));
        if (parent is null && mesh.ObjectName.ToString() != "Weapon Mesh") throw new InvalidDataException("Effect mesh has no verified native or SCS attachment route.");
        for (int i = 0; i < item.Effects.Count; i++) {
            var e = item.Effects[i]; var preset = Presets.Single(p => p.Id == e.PresetId);
            var source = c.Read(preset.Package);
            if (!source.Exports.Any(x => x.GetExportClassType()?.ToString() == "NiagaraSystem")) throw new InvalidDataException("Effect is not a native NiagaraSystem: " + preset.Package);
            var fx = SwordCombatService.Obj(actor, preset.Package, UnrealPathUtil.AssetName(preset.Package), "/Script/Niagara", "NiagaraSystem");
            var stem = "ItemEffect_" + i;
            var component = AddExport(actor, stem + "_GEN_VARIABLE", "/Script/Niagara", "NiagaraComponent", classIndex, [
                Object(actor, "Asset", fx), new BoolPropertyData(new FName(actor, "bAutoActivate")) { Value = true },
                new BoolPropertyData(new FName(actor, "bVisible")) { Value = true },
                Struct(actor, "RelativeLocation", "Vector", new VectorPropertyData(new FName(actor, "RelativeLocation")) { Value = new FVector(e.X, e.Y, e.Z) }),
                Struct(actor, "RelativeRotation", "Rotator", new RotatorPropertyData(new FName(actor, "RelativeRotation")) { Value = new FRotator(e.Pitch, e.Yaw, e.Roll) }),
                Struct(actor, "RelativeScale3D", "Vector", new VectorPropertyData(new FName(actor, "RelativeScale3D")) { Value = new FVector(e.Scale, e.Scale, e.Scale) })]);
            component.ToExport(actor).CreateBeforeSerializationDependencies.Add(fx);
            var node = AddExport(actor, "SCS_" + stem, "/Script/Engine", "SCS_Node", FPackageIndex.FromExport(actor.Exports.IndexOf(scs)), [
                Object(actor, "ComponentClass", component.ToExport(actor).ClassIndex), Object(actor, "ComponentTemplate", component),
                new NamePropertyData(new FName(actor, "InternalVariableName")) { Value = new FName(actor, stem) },
                Struct(actor, "VariableGuid", "Guid", new GuidPropertyData(new FName(actor, "VariableGuid")) { Value = Guid.NewGuid() })]);
            node.ToExport(actor).CreateBeforeSerializationDependencies.Add(component);
            if (parent is not null) Append(actor, parent, "ChildNodes", node);
            else {
                var data = ((NormalExport)node.ToExport(actor)).Data;
                data.Add(new NamePropertyData(new FName(actor,"ParentComponentOrVariableName")) { Value = new FName(actor,"Weapon Mesh") });
                data.Add(new BoolPropertyData(new FName(actor,"bIsParentComponentNative")) { Value = true });
                Append(actor,scs,"RootNodes",node);
            }
            Append(actor, scs, "AllNodes", node);
        }
    }
    internal static void Verify(UAsset a, HeldItemSettings item)
    {
        var generated = a.Exports.OfType<NormalExport>().Where(e => e.ObjectName.ToString().StartsWith("ItemEffect_")).ToArray();
        if (generated.Length != item.Effects.Count) throw new InvalidDataException("Stale held-item effects.");
        foreach (var (effect, i) in item.Effects.Select((e, i) => (e, i))) {
            var component = generated.Single(e => e.ObjectName.ToString() == $"ItemEffect_{i}_GEN_VARIABLE");
            if (SwordCombatService.Package(a, component.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "Asset").Value) != Presets.Single(p => p.Id == effect.PresetId).Package ||
                component.Data.OfType<BoolPropertyData>().Single(p => p.Name.ToString() == "bAutoActivate").Value != true) throw new InvalidDataException("Wrong item effect or activation.");
            var loc = component.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "RelativeLocation").Value.OfType<VectorPropertyData>().Single().Value;
            var scale = component.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "RelativeScale3D").Value.OfType<VectorPropertyData>().Single().Value;
            var rotation = component.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "RelativeRotation").Value.OfType<RotatorPropertyData>().Single().Value;
            if (loc.X != effect.X || loc.Y != effect.Y || loc.Z != effect.Z || scale.X != effect.Scale || scale.Y != effect.Scale || scale.Z != effect.Scale ||
                rotation.Pitch != effect.Pitch || rotation.Yaw != effect.Yaw || rotation.Roll != effect.Roll) throw new InvalidDataException("Effect placement changed during cooking.");
            var node = a.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "SCS_ItemEffect_" + i);
            var ni = FPackageIndex.FromExport(a.Exports.IndexOf(node));
            var native = node.Data.OfType<BoolPropertyData>().Any(p=>p.Name.ToString()=="bIsParentComponentNative"&&p.Value) && node.Data.OfType<NamePropertyData>().Any(p=>p.Name.ToString()=="ParentComponentOrVariableName"&&p.Value.ToString()=="Weapon Mesh");
            var linked = a.Exports.OfType<NormalExport>().Any(e => e.GetExportClassType()?.ToString() == (native?"SimpleConstructionScript":"SCS_Node") && e.Data.OfType<ArrayPropertyData>().Any(p => p.Name.ToString() == (native?"RootNodes":"ChildNodes") && p.Value.OfType<ObjectPropertyData>().Any(v => v.Value.Index == ni.Index)));
            if (!linked) throw new InvalidDataException("Effect not attached to item.");
        }
    }
}
