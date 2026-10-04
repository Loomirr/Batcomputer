using System.Security.Cryptography;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Source-derived managed-item inspection and isolated visual overrides. Never guesses items from character names.</summary>
internal static class NativeHeldItemService
{
    internal sealed record Binding(NativeHeldItemEdit Identity, string Slot, string Timing, bool Editable, string Note)
    {
        public NativeHeldTransform Placement { get; init; } = new();
        public IReadOnlyList<NativeHeldMaterialOverride> NativeMaterials { get; init; } = [];
    }
    internal sealed record Inspection(IReadOnlyList<Binding> Items, IReadOnlyList<string> Warnings);
    internal static string Root(string mod) => $"/Game/Mods/{mod}/NativeHeldItems";
    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..16];
    internal static string Ability(string mod, string source) => Root(mod) + "/GA_NativeHeld_" + Hash(source);
    internal static string Set(string mod, string source) => Root(mod) + "/AS_NativeHeld_" + Hash(source);
    private static string Actor(string mod, string ga, int index) => Root(mod) + "/BP_NativeHeld_" + Hash(ga + "|" + index);
    internal static string RedirectAbility(AbilityLoadoutProfile? profile, string mod, string package) =>
        profile?.NativeHeldItems.Any(e => e.AbilityPackage.Equals(package, StringComparison.OrdinalIgnoreCase)) == true ? Ability(mod, package) : package;

    // Preserve dormant saved visual edits when equipment removal intentionally removes their GA.
    // This service consumes only NativeHeldItems; the owning project's other sections stay intact.
    internal static AbilityLoadoutProfile ForEquipmentClosure(AbilityLoadoutProfile profile, IEnumerable<string> removedAbilities)
    {
        var removed = removedAbilities.Select(UnrealPathUtil.NormalizePackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new() { NativeHeldItems = profile.NativeHeldItems
            .Where(edit => !removed.Contains(UnrealPathUtil.NormalizePackagePath(edit.AbilityPackage)))
            .Select(edit => edit.Clone()).ToList() };
    }

    internal static IReadOnlyList<string> ActiveAbilities(AbilityLoadoutProfile profile, AbilityEditorCatalog catalog)
    {
        var lookup = catalog.InheritedAbilitySets.Concat(catalog.AvailableAbilitySets)
            .GroupBy(s => s.PackagePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var grants = profile.AbilitySets.Where(s => s.Enabled).SelectMany(s =>
            (lookup.GetValueOrDefault(s.PackagePath)?.GameplayAbilities ?? []).Select(g => g.PackagePath)
            .Where(p => !s.RemovedGameplayAbilities.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Concat(s.AddedGameplayAbilities.Select(g => g.PackagePath))).ToList();
        var style = FightingStyleProfileService.Find(profile.FightingStyleId);
        if (style is not null) grants.AddRange(style.HeldItemAbilityPackages.Concat(style.BridgeHeldItemAbilityPackages));
        return grants.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static Inspection Inspect(AbilityLoadoutProfile profile, AbilityEditorCatalog catalog)
    {
        var items = new List<Binding>(); var warnings = catalog.Warnings.ToList();
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        var mappings = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath() ?? throw new InvalidDataException("Mappings are required to inspect native held items."));
        foreach (var package in ActiveAbilities(profile, catalog))
        {
            try { items.AddRange(InspectAbility(root, package, mappings)); }
            catch (Exception ex) { warnings.Add(UnrealPathUtil.AssetName(package) + ": " + ex.Message); }
        }
        return new(items.DistinctBy(i => i.Identity.Key, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    internal static IReadOnlyList<Binding> InspectAbility(string extracted, string package, Usmap mappings)
    {
        var ga = EquipmentAssetService.Read(extracted, package, mappings);
        var cdo = ga.Exports.OfType<NormalExport>().FirstOrDefault(e => e.ObjectName.ToString().StartsWith("Default__"));
        var managed = cdo?.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "ManagedItems");
        if (managed is null) return [];
        string Tags(string name) => string.Join(", ", cdo!.Data.OfType<StructPropertyData>().Where(p => p.Name.ToString() == name)
            .SelectMany(p => p.Value.OfType<GameplayTagContainerPropertyData>()).SelectMany(p => p.Value).Select(n => n.ToString()));
        var timing = "Native draw: " + Tags("ASCOwnedTagsToGetOutItem") + "\nNative block: " + Tags("ASCOwnedTagsToBlockItem");
        var result = new List<Binding>();
        for (var i = 0; i < managed.Value.Length; i++)
        {
            if (managed.Value[i] is not StructPropertyData entry) throw new InvalidDataException("Unsupported native managed-item structure.");
            var actorRef = entry.Value.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "ItemActorClass");
            var actor = actorRef?.Value.IsImport() == true ? SwordCombatService.Package(ga, actorRef.Value) : "";
            if (!HeldItemService.ValidPackage(actor)) continue; // Native empty-hand slot requests have no actor.
            var slot = "Unknown slot";
            var request = entry.Value.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "SlotRequestData");
            if (request?.Value.IsExport() == true && request.Value.ToExport(ga) is NormalExport r)
            {
                var primary = r.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "PrimarySlotData");
                if (primary?.Value.IsExport() == true && primary.Value.ToExport(ga) is NormalExport location)
                    slot = location.Data.OfType<StructPropertyData>().Where(p => p.Name.ToString() == "SlotTag")
                        .SelectMany(p => p.Value.OfType<NamePropertyData>()).FirstOrDefault()?.Value.ToString() ?? slot;
            }
            var actorAsset = EquipmentAssetService.Read(extracted, actor, mappings);
            var fingerprint = Fingerprint(extracted, package, actor);
            var found = false;
            foreach (var export in actorAsset.Exports.OfType<NormalExport>())
            foreach (var property in export.Data.OfType<ObjectPropertyData>().Where(p => p.Name.ToString() is "StaticMesh" or "SkeletalMesh" or "SkinnedAsset"))
            {
                var reference = EquipmentAssetService.Reference(actorAsset, property);
                if (reference is not { } mesh || mesh.Class is not ("StaticMesh" or "SkeletalMesh")) continue;
                // UE skeletal components may declare both aliases to the same mesh. Surface one visual component.
                if (property.Name.ToString() == "SkinnedAsset" && export.Data.Any(p => p.Name.ToString() == "SkeletalMesh")) continue;
                found = true;
                var componentClass = export.GetExportClassType()?.ToString() ?? "";
                var editable = mesh.Class == "StaticMesh" && componentClass == "StaticMeshComponent";
                result.Add(new(new() { AbilityPackage = package, ManagedItemIndex = i, ActorPackage = actor,
                    ExportName = export.ObjectName.ToString(), MeshProperty = property.Name.ToString(), OriginalMeshPackage = mesh.Package,
                    AssetClass = mesh.Class, SourceFingerprint = fingerprint }, slot, timing, editable,
                    editable ? "Owned static component: model, materials and placement can be edited. Native timing, sockets and gameplay remain unchanged."
                    : "Skinned/specialized component: inspect only. Replacing its native rig needs a dedicated adapter.") {
                        Placement = ReadTransform(export),
                        NativeMaterials = export.Data.OfType<ArrayPropertyData>().Where(p => p.Name.ToString() == "OverrideMaterials")
                            .SelectMany(p => p.Value.Select((value, slotIndex) => (value, slotIndex)))
                            .Where(p => p.value is ObjectPropertyData material && material.Value.IsImport())
                            .Select(p => new NativeHeldMaterialOverride { Slot = p.slotIndex, Package = SwordCombatService.Package(actorAsset, ((ObjectPropertyData)p.value).Value) })
                            .Where(p => HeldItemService.ValidPackage(p.Package)).ToList()
                    });
            }
            if (!found) result.Add(new(new() { AbilityPackage = package, ManagedItemIndex = i, ActorPackage = actor,
                ExportName = "(inherited or dynamically spawned)", SourceFingerprint = fingerprint }, slot, timing, false,
                "No direct mesh binding was found. This actor may inherit or spawn its visuals; no guessed replacement is offered."));
        }
        return result;
    }

    private static string Fingerprint(string root, params string[] packages)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var package in packages)
        {
            var file = ExtractedPackagePathService.ResolvePackageUasset(root, package) ?? throw new FileNotFoundException(package);
            foreach (var ext in new[] { ".uasset", ".uexp" })
            {
                var path = Path.ChangeExtension(file, ext);
                if (File.Exists(path)) { using var stream = File.OpenRead(path); hash.AppendData(SHA256.HashData(stream)); }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static IReadOnlyList<string> Validate(IReadOnlyList<NativeHeldItemEdit> edits)
    {
        var errors = new List<string>();
        if (edits.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) errors.Add("A native held-item binding is edited twice.");
        foreach (var e in edits)
        {
            if (!new[] { e.AbilityPackage, e.ActorPackage, e.OriginalMeshPackage }.All(HeldItemService.ValidPackage) || e.ManagedItemIndex < 0 ||
                string.IsNullOrWhiteSpace(e.ExportName) || e.AssetClass != "StaticMesh" || e.MeshProperty != "StaticMesh" || e.SourceFingerprint.Length != 64)
                errors.Add("Invalid or unsupported native held-item binding.");
            if (e.ReplacementMeshPackage.Length > 0 && !HeldItemService.ValidPackage(e.ReplacementMeshPackage)) errors.Add("Choose a cooked mesh package.");
            if (e.Materials.Any(m => m.Slot < 0 || m.Slot > 63 || !HeldItemService.ValidPackage(m.Package)) || e.Materials.GroupBy(m => m.Slot).Any(g => g.Count() > 1)) errors.Add("Invalid or duplicate material slot override.");
            if (e.CustomModel is { } model) { try { WeaponModelService.Validate(model); } catch (Exception ex) { errors.Add(ex.Message); } }
            if (e.CustomModel is not null && e.Materials.Count > 0) errors.Add("Set custom model materials in its model editor; component overrides would replace them.");
            if (e.Transform is { } t && (new[] {t.X,t.Y,t.Z,t.Pitch,t.Yaw,t.Roll,t.ScaleX,t.ScaleY,t.ScaleZ}.Any(v => !float.IsFinite(v) || Math.Abs(v) > 10000) || t.ScaleX <= 0 || t.ScaleY <= 0 || t.ScaleZ <= 0)) errors.Add("Placement must be finite with positive scale.");
        }
        return errors;
    }

    internal static IReadOnlyList<string> RewriteSetPackages(AbilityLoadoutProfile profile, IEnumerable<string> sets, string extracted, string staged, string mod, Usmap mappings)
    {
        return sets.Select(package => {
            var file = ExtractedPackagePathService.ResolvePackageUasset(staged, package);
            var a = EquipmentAssetService.Read(file is not null && File.Exists(file) ? staged : extracted, package, mappings);
            return Grants(a).Any(p => profile.NativeHeldItems.Any(e => e.AbilityPackage.Equals(p, StringComparison.OrdinalIgnoreCase))) ? Set(mod, package) : package;
        }).ToList();
    }
    private static IEnumerable<string> Grants(UAsset a) => a.Exports.OfType<NormalExport>().SelectMany(e => e.Data)
        .OfType<ArrayPropertyData>().Where(p => p.Name.ToString() == "GrantedGameplayAbilities").SelectMany(p => p.Value.OfType<StructPropertyData>())
        .SelectMany(p => p.Value.OfType<ObjectPropertyData>().Where(p => p.Name.ToString() == "Ability")).Select(p => SwordCombatService.Package(a, p.Value));

    internal static void Generate(AbilityLoadoutProfile profile, string extracted, string staged, string mod, string dprd, Usmap mappings, IList<string> log)
    {
        if (profile.NativeHeldItems.Count == 0) return;
        var errors = Validate(profile.NativeHeldItems); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var mutation = new AbilityAssetMutationService(); var inspection = mutation.InspectDprdAbilitySets(dprd);
        if (!inspection.Success) throw new InvalidDataException(inspection.Error);
        var originalSets = inspection.AbilitySets.Select(s => s.PackagePath).ToList();
        using var c = new SwordCombatService.Context(extracted, staged, mod, mappings, Root(mod));
        var active = originalSets.SelectMany(p => Grants(c.Read(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in profile.NativeHeldItems.GroupBy(e => e.AbilityPackage, StringComparer.OrdinalIgnoreCase))
        {
            if (!active.Contains(group.Key)) throw new InvalidDataException("Edited native held-item controller is no longer active: " + group.Key + ". Restore its edit or select the original fighting style.");
            var bindings = InspectAbility(extracted, group.Key, mappings);
            foreach (var edit in group)
            {
                var found = bindings.SingleOrDefault(b => b.Identity.Key.Equals(edit.Key, StringComparison.OrdinalIgnoreCase));
                if (found is null || !found.Editable || found.Identity.ActorPackage != edit.ActorPackage || found.Identity.OriginalMeshPackage != edit.OriginalMeshPackage ||
                    found.Identity.SourceFingerprint != edit.SourceFingerprint)
                    throw new InvalidDataException("Native held-item donor changed; restore and reopen this item before rebuilding: " + edit.ExportName);
            }
            var gaPath = Ability(mod, group.Key); var ga = c.Clone(group.Key, gaPath);
            var managed = ga.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"))
                .Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ManagedItems");
            foreach (var item in group.GroupBy(e => e.ManagedItemIndex))
            {
                var actorPath = Actor(mod, group.Key, item.Key); var actor = c.Clone(item.First().ActorPackage, actorPath);
                foreach (var edit in item)
                {
                    var component = actor.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == edit.ExportName);
                    var property = component.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == edit.MeshProperty);
                    if (edit.Hide) property.Value = new FPackageIndex(0); // Keep controller/slot/attack logic; remove only this visual binding.
                    else
                    {
                        var mesh = edit.ReplacementMeshPackage.Length == 0 ? edit.OriginalMeshPackage : edit.ReplacementMeshPackage;
                        if (edit.CustomModel is { } model)
                        {
                            mesh = Root(mod) + "/SM_NativeHeld_" + Hash(edit.Key);
                            WeaponModelService.Bake(model, extracted, AppSettings.Current.EffectiveUsmapPath()!, staged, mesh);
                            SetMaterials(actor, component, model.Materials.Select((m, i) => new NativeHeldMaterialOverride { Slot = i, Package = m.MaterialPath }).ToList(), c, staged, replaceAll: true);
                        }
                        var meshAsset = c.Read(mesh);
                        if (!meshAsset.Exports.Any(e => e.GetExportClassType()?.ToString() == "StaticMesh")) throw new InvalidDataException("Native held-item replacement must be a StaticMesh.");
                        if (edit.Materials.Count > 0) {
                            var slots = meshAsset.Exports.OfType<NormalExport>().SelectMany(e => e.Data).OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "StaticMaterials");
                            if (slots is null || edit.Materials.Any(m => m.Slot >= slots.Value.Length)) throw new InvalidDataException("Material override targets a slot not present on the selected held-item mesh.");
                        }
                        CustomEquipmentService.ReplaceReference(actor, component, property, mesh, "StaticMesh");
                        if (edit.Materials.Count > 0) SetMaterials(actor, component, edit.Materials, c, staged);
                        if (edit.Transform is { } transform) SetTransform(actor, component, transform);
                    }
                }
                c.Write(actor, actorPath);
                var entry = (StructPropertyData)managed.Value[item.Key];
                var reference = SwordCombatService.Obj(ga, actorPath, UnrealPathUtil.AssetName(actorPath) + "_C", "/Script/Engine", "BlueprintGeneratedClass");
                entry.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "ItemActorClass").Value = reference;
                ga.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__")).CreateBeforeSerializationDependencies.Add(reference);
            }
            c.Write(ga, gaPath);
            log.Add($"Native held items: {UnrealPathUtil.AssetName(group.Key)}, {group.Count()} visual binding(s) customized; native draw/hide, hand slots and attacks retained.");
        }
        var expected = RewriteSetPackages(profile, originalSets, extracted, staged, mod, mappings);
        foreach (var (source, index) in originalSets.Select((s, i) => (s, i)).Where(p => p.s != expected[p.i]))
        {
            var set = c.Clone(source, expected[index]);
            foreach (var export in set.Exports.OfType<NormalExport>())
            foreach (var grant in export.Data.OfType<ArrayPropertyData>().Where(p => p.Name.ToString() == "GrantedGameplayAbilities").SelectMany(p => p.Value.OfType<StructPropertyData>()))
            foreach (var property in grant.Value.OfType<ObjectPropertyData>().Where(p => p.Name.ToString() == "Ability"))
            {
                var old = SwordCombatService.Package(set, property.Value); var target = RedirectAbility(profile, mod, old);
                if (old == target) continue;
                property.Value = SwordCombatService.Obj(set, target, UnrealPathUtil.AssetName(target) + "_C", "/Script/Engine", "BlueprintGeneratedClass");
                export.CreateBeforeSerializationDependencies.Add(property.Value);
            }
            c.Write(set, expected[index]);
        }
        var written = mutation.SetDprdAbilitySets(dprd, expected); if (!written.Success) throw new InvalidDataException(written.Error);
        Verify(profile, extracted, staged, mod, originalSets, mappings);
    }

    internal static void SetMaterials(UAsset a, NormalExport e, IReadOnlyList<NativeHeldMaterialOverride> edits, SwordCombatService.Context c, string staged, bool replaceAll = false)
    {
        var array = e.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
        if (array is null) { array = new(new FName(a, "OverrideMaterials")) { ArrayType = new FName(a, "ObjectProperty"), Value = [] }; e.Data.Add(array); }
        var values = replaceAll ? new List<PropertyData>() : array.Value.ToList();
        foreach (var edit in edits)
        {
            if (edit.Package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) {
                var file = ExtractedPackagePathService.ResolvePackageUasset(staged, edit.Package);
                if (file is null || !File.Exists(file))
                    new ToolMaterialLibraryService(AppSettings.Current.EffectiveProjectRoot()).CopyMaterialClosureToContentRoot(edit.Package, staged);
            }
            var material = c.Read(edit.Package); var kind = material.Exports.FirstOrDefault(x => x.GetExportClassType()?.ToString() is "Material" or "MaterialInstanceConstant")?.GetExportClassType()?.ToString();
            if (kind is null) throw new InvalidDataException("Held-item override is not a material: " + edit.Package);
            while (values.Count <= edit.Slot) values.Add(new ObjectPropertyData(new FName(a, values.Count.ToString())) { Value = new FPackageIndex(0) });
            var reference = SwordCombatService.Obj(a, edit.Package, UnrealPathUtil.AssetName(edit.Package), "/Script/Engine", kind);
            values[edit.Slot] = new ObjectPropertyData(new FName(a, edit.Slot.ToString())) { Value = reference }; e.CreateBeforeSerializationDependencies.Add(reference);
        }
        array.Value = values.ToArray();
    }
    private static void SetTransform(UAsset a, NormalExport e, NativeHeldTransform t)
    {
        e.Data.RemoveAll(p => p.Name.ToString() is "RelativeLocation" or "RelativeRotation" or "RelativeScale3D");
        StructPropertyData Struct(string name, string kind, PropertyData value) => new(new FName(a, name)) { StructType = new FName(a, kind), Value = [value] };
        e.Data.Add(Struct("RelativeLocation", "Vector", new VectorPropertyData(new FName(a, "RelativeLocation")) { Value = new FVector(t.X,t.Y,t.Z) }));
        e.Data.Add(Struct("RelativeRotation", "Rotator", new RotatorPropertyData(new FName(a, "RelativeRotation")) { Value = new FRotator(t.Pitch,t.Yaw,t.Roll) }));
        e.Data.Add(Struct("RelativeScale3D", "Vector", new VectorPropertyData(new FName(a, "RelativeScale3D")) { Value = new FVector(t.ScaleX,t.ScaleY,t.ScaleZ) }));
    }

    internal static void Verify(AbilityLoadoutProfile profile, string extracted, string staged, string mod, IReadOnlyList<string> originalSets, Usmap mappings,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        using var c = new SwordCombatService.Context(extracted, staged, mod, mappings, Root(mod));
        var expectedSets = RewriteSetPackages(profile, originalSets, extracted, staged, mod, mappings);
        for (var i = 0; i < originalSets.Count; i++)
        {
            if (originalSets[i] == expectedSets[i]) continue;
            var source = c.Read(originalSets[i]); var emitted = c.ReadStaged(expectedSets[i]);
            VerifyCloneLayout(source, emitted, originalSets[i], expectedSets[i]);
            if (!Grants(emitted).SequenceEqual(Grants(source).Select(p => RedirectAbility(profile, mod, p)), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Native held-item grant rewrite changed unrelated abilities or their order.");
            VerifyUnchangedData(source, emitted, _ => new HashSet<string>(), "Ability");
        }
        foreach (var group in profile.NativeHeldItems.GroupBy(e => e.AbilityPackage))
        {
            var source = c.Read(group.Key); var emitted = c.ReadStaged(Ability(mod, group.Key));
            VerifyCloneLayout(source, emitted, group.Key, Ability(mod, group.Key));
            VerifyUnchangedData(source, emitted, _ => new HashSet<string>(), "ItemActorClass");
            var before = source.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
            var after = emitted.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
            var a = before.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ManagedItems");
            var b = after.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "ManagedItems");
            if (a.Value.Length != b.Value.Length) throw new InvalidDataException("Native held-item slot count changed.");
            for (var i = 0; i < a.Value.Length; i++)
            {
                var original = ((StructPropertyData)a.Value[i]).Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "ItemActorClass");
                var actual = ((StructPropertyData)b.Value[i]).Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "ItemActorClass");
                var expected = group.Any(e => e.ManagedItemIndex == i) ? Actor(mod, group.Key, i) : original.Value.IsImport() ? SwordCombatService.Package(source, original.Value) : "";
                var actualActor = actual.Value.IsImport() ? SwordCombatService.Package(emitted, actual.Value) : "";
                if (actualActor != expected) throw new InvalidDataException("Native held-item actor assigned to the wrong slot.");
            }
            foreach (var item in group.GroupBy(e => e.ManagedItemIndex))
            {
                var actor = c.ReadStaged(Actor(mod, group.Key, item.Key));
                var nativeActor = c.Read(item.First().ActorPackage);
                VerifyUnchangedData(nativeActor, actor, export => {
                    var fields = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var edit in item.Where(e => e.ExportName == export)) {
                        fields.Add(edit.MeshProperty);
                        if (!edit.Hide && (edit.Materials.Count > 0 || edit.CustomModel is not null)) fields.Add("OverrideMaterials");
                        if (!edit.Hide && edit.Transform is not null) fields.UnionWith(["RelativeLocation", "RelativeRotation", "RelativeScale3D"]);
                    }
                    return fields;
                });
                foreach (var edit in item)
                {
                    var component = actor.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == edit.ExportName);
                    var property = component.Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == edit.MeshProperty);
                    var expected = edit.CustomModel is not null ? Root(mod) + "/SM_NativeHeld_" + Hash(edit.Key) : edit.ReplacementMeshPackage.Length > 0 ? edit.ReplacementMeshPackage : edit.OriginalMeshPackage;
                    expected = CharacterAssetReuseService.Resolve(expected, aliases);
                    if (edit.Hide ? property.Value.Index != 0 : SwordCombatService.Package(actor, property.Value) != expected) throw new InvalidDataException("Native held-item visual failed cooked roundtrip.");
                    if (!edit.Hide && edit.Transform is { } transform && ReadTransform(component) != transform) throw new InvalidDataException("Native held-item placement failed cooked roundtrip.");
                    if (!edit.Hide) {
                        var materials = edit.CustomModel is { } model ? model.Materials.Select((m,i) => new NativeHeldMaterialOverride {Slot=i,Package=m.MaterialPath}).ToList() : edit.Materials;
                        var array = component.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "OverrideMaterials");
                        foreach (var material in materials)
                            if (array?.Value.ElementAtOrDefault(material.Slot) is not ObjectPropertyData value || SwordCombatService.Package(actor,value.Value) != material.Package)
                                throw new InvalidDataException("Native held-item material failed cooked roundtrip.");
                    }
                }
            }
        }
    }

    private static void VerifyUnchangedData(UAsset source, UAsset emitted, Func<string, IReadOnlySet<string>> excluded, string? redirectedReference = null)
    {
        // Compare decoded properties, not just a successful parse. Controlled mesh/placement
        // fields and exact redirected references are excluded; tags, slots, attack settings,
        // grant levels/input tags, collision and unedited component properties must match.
        string Snapshot(UAsset asset) {
            var exports = System.Text.Json.Nodes.JsonNode.Parse(asset.SerializeJson(false))!["Exports"]!.AsArray();
            var result = new System.Text.Json.Nodes.JsonArray();
            foreach (var export in exports) {
                var data = export?["Data"]?.DeepClone();
                if (data is System.Text.Json.Nodes.JsonArray array) {
                    var name = export!["ObjectName"]?.GetValue<string>() ?? "";
                    var skip = excluded(name);
                    for (var i=array.Count-1;i>=0;i--) if (array[i]?["Name"] is { } field && skip.Contains(field.GetValue<string>())) array.RemoveAt(i);
                    Scrub(array);
                }
                result.Add(data);
            }
            return result.ToJsonString();
        }
        void Scrub(System.Text.Json.Nodes.JsonNode node) {
            if (node is System.Text.Json.Nodes.JsonObject obj) {
                if (redirectedReference is not null && obj["Name"]?.GetValue<string>() == redirectedReference) { obj["Value"] = 0; obj["IsZero"] = false; }
                else foreach (var child in obj.Select(p=>p.Value).Where(v=>v is not null).ToArray()) Scrub(child!);
            } else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var child in array.Where(v=>v is not null)) Scrub(child!);
        }
        if (Snapshot(source) != Snapshot(emitted)) throw new InvalidDataException("Native held-item cloning changed unrelated serialized properties.");
    }

    private static void VerifyCloneLayout(UAsset source, UAsset emitted, string from, string to)
    {
        var oldName = UnrealPathUtil.AssetName(from); var newName = UnrealPathUtil.AssetName(to);
        foreach (var export in source.Exports)
        {
            var name = export.ObjectName.ToString();
            if (name == oldName || name == oldName + "_C" || name == "Default__" + oldName + "_C")
                export.ObjectName = new FName(source, name.Replace(oldName, newName, StringComparison.Ordinal));
        }
        CustomEquipmentService.RequirePropertyLayoutRoundtrip(source, emitted, to);
    }
    private static NativeHeldTransform ReadTransform(NormalExport e)
    {
        var loc = e.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "RelativeLocation")?.Value.OfType<VectorPropertyData>().FirstOrDefault()?.Value;
        var rot = e.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "RelativeRotation")?.Value.OfType<RotatorPropertyData>().FirstOrDefault()?.Value;
        var scale = e.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "RelativeScale3D")?.Value.OfType<VectorPropertyData>().FirstOrDefault()?.Value;
        return new() { X=(float)(loc?.X??0), Y=(float)(loc?.Y??0), Z=(float)(loc?.Z??0), Pitch=(float)(rot?.Pitch??0), Yaw=(float)(rot?.Yaw??0), Roll=(float)(rot?.Roll??0),
            ScaleX=(float)(scale?.X??1), ScaleY=(float)(scale?.Y??1), ScaleZ=(float)(scale?.Z??1) };
    }
}
