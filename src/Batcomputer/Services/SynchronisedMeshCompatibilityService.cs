using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Retains the selected rig's paired-animation contract on imported bodies.</summary>
internal static class SynchronisedMeshCompatibilityService
{
    internal const string UserDataClass = "TtSynchronisedAnimSkeletalMeshUserData";

    internal static void Restore(string output, string nativeDonor, Usmap maps)
    {
        var donor = new UAsset(nativeDonor, EngineVersion.VER_UE5_6, maps);
        if (!donor.Exports.OfType<NormalExport>().Any(e => e.GetExportClassType()?.ToString() == UserDataClass)) return;
        var target = new UAsset(output, EngineVersion.VER_UE5_6, maps);
        Copy(target, donor);
        target.Write(output);
        var written = new UAsset(output, EngineVersion.VER_UE5_6, maps);
        if (!Tags(written).SequenceEqual(Tags(donor)))
            throw new InvalidDataException("The imported body's paired-animation rig metadata did not survive cooking.");
    }

    internal static string[] Tags(UAsset asset) => asset.Exports.OfType<NormalExport>()
        .Where(e => e.GetExportClassType()?.ToString() == UserDataClass)
        .SelectMany(e => EquipmentAssetService.Properties(e.Data).Select(p => p.Property))
        .OfType<GameplayTagContainerPropertyData>().SelectMany(p => p.Value.Select(n => n.ToString())).Order().ToArray();

    internal static void Copy(UAsset target, UAsset donor)
    {
        var source = donor.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == UserDataClass);
        var body = target.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "SkeletalMesh");
        var existing = target.Exports.OfType<NormalExport>().SingleOrDefault(e => e.GetExportClassType()?.ToString() == UserDataClass);
        var copy = existing ?? new NormalExport { Asset = target, ObjectName = Name(target, "TtSynchronisedAnimSkeletalMeshUserData_Batcomputer"),
            ClassIndex = Import(target, donor, source.ClassIndex), TemplateIndex = Import(target, donor, source.TemplateIndex),
            OuterIndex = FPackageIndex.FromExport(target.Exports.IndexOf(body)), ObjectFlags = source.ObjectFlags, Extras = [] };
        copy.Data = PartGraftService.DeepClonePropertiesRebased(source.Data, target);
        // Native packages use unversioned fields, whereas editor-imported meshes
        // use UE5 complete property type names. Supply headers when crossing formats.
        foreach (var property in copy.Data.OfType<StructPropertyData>())
            property.PropertyTypeName = new FPropertyTypeName([new() { Name = Name(target, "StructProperty"), InnerCount = 1 },
                new() { Name = Name(target, property.StructType.ToString()), InnerCount = 1 }, new() { Name = Name(target, "/Script/GameplayTags"), InnerCount = 0 }], true);
        copy.SerializationBeforeCreateDependencies = new[] { copy.ClassIndex, copy.TemplateIndex }.Where(p => !p.IsNull()).ToList();
        copy.CreateBeforeCreateDependencies = [copy.OuterIndex];
        if (existing is null) target.Exports.Add(copy);
        var reference = FPackageIndex.FromExport(target.Exports.IndexOf(copy));
        var list = body.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "AssetUserData");
        if (list is null) { list = new ArrayPropertyData(Name(target, "AssetUserData")) { ArrayType = Name(target, "ObjectProperty"), Value = [] }; body.Data.Add(list); }
        list.PropertyTypeName = new FPropertyTypeName([new() { Name = Name(target, "ArrayProperty"), InnerCount = 1 }, new() { Name = Name(target, "ObjectProperty"), InnerCount = 0 }], true);
        if (!list.Value.OfType<ObjectPropertyData>().Any(p => p.Value.Index == reference.Index))
            list.Value = [.. list.Value, new ObjectPropertyData(Name(target, list.Value.Length.ToString())) { Value = reference }];
        if (!body.CreateBeforeSerializationDependencies.Any(p => p.Index == reference.Index)) body.CreateBeforeSerializationDependencies.Add(reference);
    }

    private static FName Name(UAsset a, string n) { a.AddNameReference(new FString(n)); return new FName(a, n); }
    private static FPackageIndex Import(UAsset target, UAsset source, FPackageIndex index)
    {
        if (index is null || index.IsNull()) return FPackageIndex.FromRawIndex(0);
        if (!index.IsImport()) throw new InvalidDataException("Unsupported rig metadata template dependency.");
        var input = index.ToImport(source); var outer = Import(target, source, input.OuterIndex);
        var found = target.Imports.FindIndex(i => i.ObjectName.ToString() == input.ObjectName.ToString() && i.OuterIndex.Index == outer.Index && i.ClassName.ToString() == input.ClassName.ToString() && i.ClassPackage.ToString() == input.ClassPackage.ToString());
        if (found >= 0) return FPackageIndex.FromImport(found);
        target.Imports.Add(new Import { ObjectName = Name(target, input.ObjectName.ToString()), ClassName = Name(target, input.ClassName.ToString()), ClassPackage = Name(target, input.ClassPackage.ToString()), OuterIndex = outer });
        return FPackageIndex.FromImport(target.Imports.Count - 1);
    }
}
