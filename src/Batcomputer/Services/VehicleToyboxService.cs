using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class VehicleToyboxService
{
    internal sealed record Choice(string Path, string Name, string Group);
    private static bool MeshPath(string path) => ExtractedPackagePathService.IsContentPackagePath(path) && !path.Contains("/Mods/", StringComparison.OrdinalIgnoreCase)
        && path.Split('/').Skip(1).All(UnrealPathUtil.IsValidIdentifier)
        && UnrealPathUtil.AssetName(path).StartsWith("SM_", StringComparison.OrdinalIgnoreCase)
        && (path.Contains("/Models/Vehicles/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Lighting/Lego_Lights/", StringComparison.OrdinalIgnoreCase));
    internal static bool Allowed(string path) => MeshPath(path) && !Regex.IsMatch(UnrealPathUtil.AssetName(path), "(^|_)(COL|COLLISION|UCX|UBX|USP|UCP)(_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool Removed(VehicleProject p, VehicleToyboxPart part) => part.Added && p.DisabledParts.Contains(part.Component, StringComparer.Ordinal);

    // Keep removals in the editable recipe for undo/restore, but do not ship their
    // component templates, SCS nodes, offsets or material dependencies.
    internal static VehicleProject BuildRecipe(VehicleProject project)
    {
        var copy = project.Clone();
        var removed = copy.ToyboxParts.Where(t => Removed(copy, t)).Select(t => t.Component).ToHashSet(StringComparer.Ordinal);
        copy.ToyboxParts.RemoveAll(t => removed.Contains(t.Component));
        copy.Transforms.RemoveAll(t => removed.Contains(t.Component));
        copy.MaterialOverrides.RemoveAll(t => removed.Contains(t.Component));
        copy.DisabledParts.RemoveAll(removed.Contains);
        return copy;
    }
    internal static IReadOnlyList<Choice> Catalog(DefaultFileProvider provider) => provider.Files.Keys.Select(VehicleMaterialCatalogService.PackageFromMountedFile)
        .Where(Allowed).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
        .Select(p => new Choice(p, UnrealPathUtil.AssetName(p), p.Contains("/Lighting/", StringComparison.OrdinalIgnoreCase) ? "Light pieces" : "Vehicle pieces")).ToArray();
    internal static void ValidateShape(VehicleProject p)
    {
        if (p.ToyboxParts is null || p.ToyboxParts.Count > 128 || p.ToyboxParts.Any(t => t is null || !MeshPath(t.MeshPackage) || string.IsNullOrWhiteSpace(t.Component) ||
            t.Added && !Regex.IsMatch(t.Component, "^BC_Part_[a-f0-9]{12}_GEN_VARIABLE$")) || p.ToyboxParts.Select(t => t.Component).Distinct(StringComparer.Ordinal).Count() != p.ToyboxParts.Count)
            throw new InvalidDataException("Invalid vehicle toybox parts (maximum 128). Choose meshes from the installed vehicle library.");
        if (p.ToyboxParts.Any(t => p.LightSurfaces.Any(s => VehicleLightSurfaceService.Roles.Any(r => r.Id == s.Role && (r.Component == t.Component || r.Glow == t.Component)))))
            throw new InvalidDataException("Remove the custom light-surface assignment before replacing that lamp's mesh.");
    }
    internal static void ValidateAssets(VehicleProject p, HashSet<string> nativeParts)
    {
        ValidateShape(p);
        if (p.ToyboxParts.All(t => Removed(p, t))) return;
        using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
        foreach (var part in p.ToyboxParts)
        {
            if (Removed(p, part)) continue;
            if (!Allowed(part.MeshPackage)) throw new InvalidDataException("Internal collision meshes cannot be used as decorative vehicle parts. Remove or replace this toybox part: " + part.Component);
            if (!part.Added && !nativeParts.Contains(part.Component)) throw new InvalidDataException("Choose a native decorative part to replace: " + part.Component);
            var mesh = provider.LoadPackageObject<UStaticMesh>(part.MeshPackage);
            var count = ModelPreviewService.MeshSlotMaterials(mesh).Count;
            if (p.MaterialOverrides.Any(m => m.Component == part.Component && m.Slot >= count)) throw new InvalidDataException("The replacement part has fewer material slots. Choose its materials again.");
        }
    }
    internal static void Apply(UAsset asset, VehicleProject p)
    {
        foreach (var part in p.ToyboxParts)
        {
            if (Removed(p, part)) continue;
            if (part.Added) AddComponent(asset, part, p);
            var component = asset.Exports.OfType<NormalExport>().SingleOrDefault(e => e.ObjectName.ToString() == part.Component);
            // Display actors do not always carry every gameplay-only lamp endpoint.
            if (component is null) continue;
            VehicleAssetService.Require(component.GetExportClassType()?.ToString() == "StaticMeshComponent", "Toybox target is not a decorative mesh.");
            var mesh = SwordCombatService.Obj(asset, part.MeshPackage, UnrealPathUtil.AssetName(part.MeshPackage), "/Script/Engine", "StaticMesh");
            component.Data.RemoveAll(d => d.Name.ToString() is "StaticMesh" or "OverrideMaterials");
            component.Data.Add(new ObjectPropertyData(new FName(asset, "StaticMesh")) { Value = mesh });
            if (!component.CreateBeforeSerializationDependencies.Contains(mesh)) component.CreateBeforeSerializationDependencies.Add(mesh);
        }
    }
    internal static void Verify(UAsset asset, VehicleProject p)
    {
        foreach (var part in p.ToyboxParts)
        {
            var component = asset.Exports.OfType<NormalExport>().SingleOrDefault(e => e.ObjectName.ToString() == part.Component);
            if (Removed(p, part))
            {
                VehicleAssetService.Require(component is null && !asset.Exports.Any(e => e.ObjectName.ToString() == "BC_Node_" + part.Component), "Removed toybox part was still exported.");
                continue;
            }
            if (component is null && !part.Added) continue;
            VehicleAssetService.Require(component is not null, "Added vehicle part was lost.");
            var mesh = component!.Data.OfType<ObjectPropertyData>().Single(d => d.Name.ToString() == "StaticMesh");
            VehicleAssetService.Require(p.DisabledParts.Contains(part.Component) ? mesh.Value.IsNull() : EquipmentAssetService.Reference(asset, mesh)?.Package == part.MeshPackage, "Toybox mesh did not survive staging.");
            if (!part.Added) continue;
            VehicleAssetService.Require(component.Extras.AsSpan().SequenceEqual(EmptyComponentTail), "Added static-mesh component is missing its native serialization tail.");
            var index = asset.Exports.IndexOf(component) + 1;
            VehicleAssetService.Require(asset.Exports.OfType<NormalExport>().Any(n => n.GetExportClassType()?.ToString() == "SCS_Node" && n.Data.OfType<ObjectPropertyData>().Any(d => d.Name.ToString() == "ComponentTemplate" && d.Value.Index == index) && n.CreateBeforeSerializationDependencies.Any(d => d.Index == index)), "Toybox construction node is incomplete.");
        }
    }
    // The native empty component payload contains no package references or baked
    // lighting. Property-only exports are invalid: Unreal reads past their end.
    private static readonly byte[] EmptyComponentTail = Convert.FromHexString("00000000000000000100000000000000");
    internal static byte[] ComponentTail(UAsset asset, VehicleProject project)
    {
        static NormalExport? Sample(UAsset a) => a.Exports.OfType<NormalExport>().FirstOrDefault(e =>
            e.GetExportClassType()?.ToString() == "StaticMeshComponent" && e.Extras.AsSpan().SequenceEqual(EmptyComponentTail));
        var sample = Sample(asset);
        var donor = VehicleDonorService.Get(project);
        foreach (var package in new[] { donor.Blueprint, donor.Menu, donor.Plinth })
        {
            if (sample is not null) break;
            sample = Sample(VehicleAssetService.Read(AppSettings.Current.EffectiveExtractedContentRoot(), package));
        }
        VehicleAssetService.Require(sample is not null, "No verified native static-mesh component serialization template is available for this driving base.");
        return sample!.Extras.ToArray();
    }
    private static void AddComponent(UAsset a, VehicleToyboxPart part, VehicleProject project)
    {
        VehicleAssetService.Require(!a.Exports.Any(e => e.ObjectName.ToString() == part.Component), "Toybox component already exists.");
        var cls = a.Exports.OfType<ClassExport>().Single();
        var scs = a.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "SimpleConstructionScript");
        var sample = a.Exports.OfType<NormalExport>().First(e => e.GetExportClassType()?.ToString() == "SCS_Node");
        FPackageIndex Index(Export e) => FPackageIndex.FromExport(a.Exports.IndexOf(e));
        FName Name(string s) => new(a, s);
        var componentClass = SwordCombatService.Obj(a, "/Script/Engine", "StaticMeshComponent", "/Script/CoreUObject", "Class");
        var componentTemplate = SwordCombatService.Obj(a, "/Script/Engine", "Default__StaticMeshComponent", "/Script/Engine", "StaticMeshComponent");
        var component = new NormalExport { Asset = a, ObjectName = Name(part.Component), ClassIndex = componentClass, TemplateIndex = componentTemplate, OuterIndex = Index(cls),
            ObjectFlags = EObjectFlags.RF_Public | EObjectFlags.RF_Transactional | EObjectFlags.RF_ArchetypeObject | EObjectFlags.RF_InheritableComponentTemplate, Data = [], Extras = ComponentTail(a, project), CreateBeforeSerializationDependencies = [], SerializationBeforeSerializationDependencies = [Index(cls)], SerializationBeforeCreateDependencies = [componentClass, componentTemplate], CreateBeforeCreateDependencies = [Index(cls)] };
        // Decorative only: new components must not introduce extra collision or overlap events.
        component.Data.Add(new BoolPropertyData(Name("bGenerateOverlapEvents")) { Value = false });
        component.Data.Add(new StructPropertyData(Name("BodyInstance")) { StructType = Name("BodyInstance"), Value = [new NamePropertyData(Name("CollisionProfileName")) { Value = Name("NoCollision") }] });
        a.Exports.Add(component); a.DependsMap.Add([]);
        var bodyName = VehicleCustomizationService.BodyComponent(a);
        var body = a.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == bodyName);
        var localParent = a.Exports.OfType<NormalExport>().FirstOrDefault(e => e.GetExportClassType()?.ToString() == "SCS_Node" && e.Data.OfType<ObjectPropertyData>().Any(d => d.Name.ToString() == "ComponentTemplate" && d.Value.Index == Index(body).Index));
        var node = new NormalExport { Asset = a, ObjectName = Name("BC_Node_" + part.Component), ClassIndex = sample.ClassIndex, TemplateIndex = sample.TemplateIndex, OuterIndex = Index(scs), ObjectFlags = sample.ObjectFlags,
            Data = [new ObjectPropertyData(Name("ComponentClass")) { Value = componentClass }, new ObjectPropertyData(Name("ComponentTemplate")) { Value = Index(component) },
                new NamePropertyData(Name("AttachToName")) { Value = Name("Body") }, new NamePropertyData(Name("InternalVariableName")) { Value = Name(part.Component.Replace("_GEN_VARIABLE", "")) },
                new StructPropertyData(Name("VariableGuid")) { StructType = Name("Guid"), Value = [new GuidPropertyData(Name("VariableGuid")) { Value = Guid.NewGuid() }] }],
            Extras = [], CreateBeforeSerializationDependencies = [Index(component)], SerializationBeforeSerializationDependencies = [], SerializationBeforeCreateDependencies = [sample.ClassIndex, sample.TemplateIndex], CreateBeforeCreateDependencies = [Index(scs)] };
        if (localParent is null)
        {
            node.Data.Add(new NamePropertyData(Name("ParentComponentOrVariableName")) { Value = Name(bodyName) });
            node.Data.Add(new BoolPropertyData(Name("bIsParentComponentNative")) { Value = true });
        }
        a.Exports.Add(node); a.DependsMap.Add([]);
        void Link(NormalExport parent, string field)
        {
            var list = parent.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == field);
            if (list is null) { list = new(Name(field)) { ArrayType = Name("ObjectProperty"), Value = [] }; parent.Data.Add(list); }
            list.Value = [.. list.Value, new ObjectPropertyData(Name(list.Value.Length.ToString())) { Value = Index(node) }]; list.IsZero = false;
            parent.CreateBeforeSerializationDependencies.Add(Index(node));
        }
        Link(scs, "AllNodes"); Link(localParent ?? scs, localParent is null ? "RootNodes" : "ChildNodes");
        foreach (var generation in a.Generations) { generation.ExportCount = a.Exports.Count; generation.NameCount = a.GetNameMapIndexList().Count; }
    }
}
