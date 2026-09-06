using System.Reflection;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Native bone clearance, not a mesh translation. Replayed once on a clean suit stage.</summary>
internal static class AttachmentClearanceService
{
    private const string BeltDonor = "/Game/Characters/Minifig/Batman/BP_Batman_LBM_Cutscene";
    internal static string? BoneForSlot(string slot) => slot.ToLowerInvariant() switch
    { "hip" or "offset_hip" => "Spine_01", "shoulder" or "offset_neck" => "Neck", _ => null };
    internal static float DefaultForSlot(string slot) => BoneForSlot(slot) switch { "Spine_01" => 4.3f, "Neck" => 3f, _ => 0 };
    internal static float Clearance(CustomStaticMeshImport mesh)
    {
        float value = mesh.BodyClearance ?? DefaultForSlot(mesh.Target);
        if (!float.IsFinite(value) || value is < 0 or > 30) throw new InvalidDataException("Body clearance must be between 0 and 30 game units.");
        return value;
    }

    internal static void Apply(string content, NativeSuitProject project, Action<string> log)
    {
        var desired = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var mesh in project.CustomStaticMeshes)
            if (BoneForSlot(mesh.Target) is { } bone && Clearance(mesh) > 0 && !Removed(project, CustomStaticMeshImportService.ComponentNameFor(mesh)))
                desired[bone] = Math.Max(desired.GetValueOrDefault(bone), Clearance(mesh));
        var mappings = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        // Native grafts carry the selected donor's clearance for the relevant attachment region.
        foreach (var graft in project.PartGrafts.Where(g => !Removed(project, string.IsNullOrEmpty(g.ResolvedComponent) ? g.Slot : g.ResolvedComponent)))
        {
            var donor = graft.Playable ?? graft.Cutscene;
            if (donor is null) continue;
            var bone = BoneForSlot(graft.Slot) ?? (graft.Slot is "Torso" or "Cape" or "Collar" ? "Neck" : null);
            if (bone is null) continue;
            var package = string.IsNullOrWhiteSpace(donor.TemplatePackagePath) ? donor.SourcePackagePath : donor.TemplatePackagePath;
            var asset = EquipmentAssetService.Read(extracted, package, mappings);
            var manager = FindManager(asset);
            if (manager is null) continue;
            foreach (var entry in Read(asset, manager).Where(e => e.Bone == bone))
                desired[bone] = Math.Max(desired.GetValueOrDefault(bone), entry.X);
        }
        if (desired.Count == 0) return;
        // Both files are prepared and verified before either original is replaced.
        var prepared = new List<(string File, byte[] Uasset, byte[] Uexp)>();
        foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene })
        {
            var asset = EquipmentAssetService.Read(content, package, mappings);
            NativeBodyProfileService.EnsureMinimalSchema(asset, "BP_CutsceneMinifigCharacter_C", "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
            var manager = FindManager(asset) ?? AddCutsceneOverride(asset, EquipmentAssetService.Read(extracted, BeltDonor, mappings));
            var entries = Read(asset, manager);
            foreach (var (bone, value) in desired)
            {
                var existing = entries.FirstOrDefault(e => e.Bone == bone);
                if (existing is not null)
                {
                    // A belt and a collar do not repeatedly stack their shared clearance on every rebuild.
                    existing.X = Math.Max(existing.X, value);
                }
                else entries.Add(new Entry(bone, [0, 0, 0, 1, value, 0, 0, 1, 1, 1], 1));
            }
            var raw = new RawExport(); CopyExportMetadata(manager, raw); raw.Asset = asset; raw.Extras = []; raw.Data = Encode(asset, entries, raw);
            var boneStruct = SwordCombatService.Obj(asset, "/Script/TtLocationAttachmentManager", "BoneOffsetData", "/Script/CoreUObject", "Object");
            if (!raw.CreateBeforeSerializationDependencies.Contains(boneStruct)) raw.CreateBeforeSerializationDependencies.Add(boneStruct);
            asset.Exports[asset.Exports.IndexOf(manager)] = raw;
            var file = SkinnedMeshCookService.SafePath(content, package[6..] + ".uasset");
            var scratch = Path.Combine(Path.GetTempPath(), "Batcomputer-offset-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
            var written = Path.Combine(scratch, "mesh.uasset"); asset.Write(written);
            try
            {
                var read = new UAsset(written, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
                var actual = Read(read, FindManager(read) ?? throw new InvalidDataException("Offset manager disappeared."));
                if (desired.Any(pair => !actual.Any(e => e.Bone == pair.Key && e.X >= pair.Value - .0001))) throw new InvalidDataException("Body clearance did not survive serialization.");
                prepared.Add((file, File.ReadAllBytes(written), File.ReadAllBytes(Path.ChangeExtension(written, ".uexp"))));
            }
            finally
            {
                foreach (var name in new[] { "mesh.uasset", "mesh.uexp" }) { var f = Path.Combine(scratch, name); if (File.Exists(f)) File.Delete(f); }
                Directory.Delete(scratch);
            }
        }
        foreach (var file in prepared) { File.WriteAllBytes(file.File, file.Uasset); File.WriteAllBytes(Path.ChangeExtension(file.File, ".uexp"), file.Uexp); }
        log("Native attachment clearance: " + string.Join(", ", desired.Select(p => $"{p.Key} ≥ {p.Value:0.###}")) + " (playable + cutscene).");
    }
    private static bool Removed(NativeSuitProject project, string component) => project.Requirements.Any(r => r.Kind == "remove-component" && r.TargetComponent.Split(':')[0].Equals(component, StringComparison.OrdinalIgnoreCase));
    private static Export? FindManager(UAsset asset) => asset.Exports.FirstOrDefault(e => e.GetExportClassType()?.ToString() is "DinnerLocationAttachmentManagerComponent" or "TtLocationAttachmentManagerComponent");
    internal sealed record Entry(string Bone, double[] Transform, byte Priority)
    { internal double X { get => Transform[4]; set => Transform[4] = value; } }

    // UAssetAPI cannot currently decode these InstancedStructs. This narrow codec recognizes the
    // actual native BoneOffsetData schema and rejects any unknown layout instead of dropping data.
    internal static List<Entry> Read(UAsset asset, Export manager)
    {
        if (manager is NormalExport normal && normal.Data.Count == 0) return [];
        if (manager is not RawExport raw) throw new InvalidDataException("This attachment manager has unsupported additional properties; clearance was not changed.");
        using var reader = new BinaryReader(new MemoryStream(raw.Data));
        if (reader.ReadUInt16() != OffsetHeader(manager)) throw new InvalidDataException("Unrecognized attachment-manager layout; no offsets were changed.");
        int count = reader.ReadInt32(); if (count < 0 || count > 64) throw new InvalidDataException("Invalid attachment offset count.");
        var entries = new List<Entry>();
        for (int i = 0; i < count; i++)
        {
            var reference = FPackageIndex.FromRawIndex(reader.ReadInt32());
            if (!reference.IsImport() || reference.ToImport(asset).ObjectName.ToString() != "BoneOffsetData" || reader.ReadInt32() != 93 || reader.ReadUInt16() != 0x0700)
                throw new InvalidDataException("Unsupported native offset type. It was preserved; this build stopped.");
            int name = reader.ReadInt32(), number = reader.ReadInt32();
            if (number != 0 || name < 0 || name >= asset.GetNameMapIndexList().Count || reader.ReadUInt16() != 0x0700) throw new InvalidDataException("Invalid bone-offset record.");
            var values = Enumerable.Range(0, 10).Select(_ => reader.ReadDouble()).ToArray();
            if (values.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Non-finite native bone offset.");
            entries.Add(new Entry(asset.GetNameReference(name).ToString(), values, reader.ReadByte()));
        }
        if (reader.BaseStream.Length - reader.BaseStream.Position != 8 || reader.ReadInt64() != 0) throw new InvalidDataException($"Unexpected attachment-manager trailing data ({reader.BaseStream.Length - reader.BaseStream.Position} bytes remain).");
        return entries;
    }
    // Unversioned property indices include the derived class's own properties. The playable
    // Dinner manager adds two fields before its base's PermanentOffsetData; the cinematic
    // Tt manager does not. Reusing the playable header makes cutscenes read ComponentTags.
    internal static ushort OffsetHeader(Export manager) => manager.GetExportClassType()?.ToString() switch
    {
        "DinnerLocationAttachmentManagerComponent" => 0x0303,
        "TtLocationAttachmentManagerComponent" => 0x0301,
        _ => throw new InvalidDataException("Unsupported attachment-manager class.")
    };
    internal static byte[] Encode(UAsset asset, List<Entry> entries, Export manager)
    {
        var reference = SwordCombatService.Obj(asset, "/Script/TtLocationAttachmentManager", "BoneOffsetData", "/Script/CoreUObject", "Object");
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(OffsetHeader(manager)); writer.Write(entries.Count);
        foreach (var e in entries)
        {
            writer.Write(reference.Index); writer.Write(93); writer.Write((ushort)0x0700);
            writer.Write(asset.AddNameReference(new FString(e.Bone))); writer.Write(0); writer.Write((ushort)0x0700);
            foreach (double v in e.Transform) writer.Write(v); writer.Write(e.Priority);
        }
        writer.Write(0L); return bytes.ToArray();
    }
    private static void CopyExportMetadata(Export source, Export target)
    {
        foreach (var field in typeof(Export).GetFields(BindingFlags.Public | BindingFlags.Instance)) field.SetValue(target, field.GetValue(source));
        foreach (var property in typeof(Export).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0))
            property.SetValue(target, property.GetValue(source));
    }
    private static Export AddCutsceneOverride(UAsset target, UAsset donor)
    {
        var handler = target.Exports.OfType<NormalExport>().SingleOrDefault(e => e.GetExportClassType()?.ToString() == "InheritableComponentHandler")
            ?? throw new InvalidDataException("This character has no inherited cutscene component handler. Its attachment clearance needs a compatible native recipe.");
        if (!EquipmentAssetService.Properties(handler.Data).Any(pair => pair.Property is ObjectPropertyData obj &&
                obj.Name.ToString() == "OwnerClass" && EquipmentAssetService.Reference(target, obj)?.Package == "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter"))
            throw new InvalidDataException("This character does not use the verified minifigure cutscene master. Its attachment clearance needs a separate native recipe.");
        var source = FindManager(donor) ?? throw new InvalidDataException("Native belt offset donor is missing.");
        var clone = new RawExport(); CopyExportMetadata(source, clone); clone.Asset = target;
        clone.ObjectName = new FName(target, source.ObjectName.ToString());
        clone.OuterIndex = handler.OuterIndex; clone.ClassIndex = RebaseImport(target, donor, source.ClassIndex);
        clone.TemplateIndex = RebaseImport(target, donor, source.TemplateIndex);
        clone.SuperIndex = FPackageIndex.FromRawIndex(0); clone.Extras = []; clone.Data = Encode(target, [], clone);
        clone.SerializationBeforeSerializationDependencies = [handler.OuterIndex]; clone.CreateBeforeSerializationDependencies = [];
        clone.SerializationBeforeCreateDependencies = [clone.TemplateIndex, clone.ClassIndex]; clone.CreateBeforeCreateDependencies = [handler.OuterIndex];
        target.Exports.Add(clone); var reference = FPackageIndex.FromExport(target.Exports.Count - 1);
        var donorHandler = donor.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "InheritableComponentHandler");
        var record = donorHandler.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Records").Value.OfType<StructPropertyData>()
            .Single(s => s.Value.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "ComponentTemplate" && p.Value.IsExport() && p.Value.ToExport(donor) == source));
        var copied = (StructPropertyData)PartGraftService.DeepClonePropertiesRebased([record], target).Single();
        foreach (var (path, property) in EquipmentAssetService.Properties(copied.Value))
            if (property is ObjectPropertyData obj)
                obj.Value = path == "ComponentTemplate" ? reference : RebaseImport(target, donor, obj.Value);
        var records = handler.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Records");
        records.Value = [.. records.Value, copied]; handler.CreateBeforeSerializationDependencies.Add(reference);
        return clone;
    }

    // A template import can be Package -> GeneratedClass -> Component. Flattening its immediate
    // outer into a package creates an unresolved import in IoStore, despite a successful UAsset read.
    internal static FPackageIndex RebaseImport(UAsset target, UAsset donor, FPackageIndex index, int depth = 0)
    {
        if (index.IsNull()) return FPackageIndex.FromRawIndex(0);
        if (!index.IsImport() || depth > 64) throw new InvalidDataException("Unsupported offset-template import chain.");
        var source = index.ToImport(donor);
        var outer = RebaseImport(target, donor, source.OuterIndex, depth + 1);
        var existing = target.Imports.FindIndex(i => i.OuterIndex.Index == outer.Index &&
            i.ObjectName.ToString() == source.ObjectName.ToString() && i.ClassName.ToString() == source.ClassName.ToString() && i.ClassPackage.ToString() == source.ClassPackage.ToString());
        return existing >= 0 ? FPackageIndex.FromImport(existing) : target.AddImport(new Import(
            source.ClassPackage.ToString(), source.ClassName.ToString(), outer, source.ObjectName.ToString(), false, target));
    }
}
