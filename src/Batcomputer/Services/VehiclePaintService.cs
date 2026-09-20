using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class VehiclePaintService
{
    internal const string MaterialRoot = "/Game/Global/Materials/LEGO_Material_Library/Project/LEGO_Models/Material_Instances/";
    internal const string Palette = "/Game/Global/Materials/LEGO_Material_Library/Core/Data/LEGO_Colours/T_LEGO_Solid";
    internal static string Material(string finish) => finish switch {
        "Solid" => MaterialRoot + "Mi_LEGO_MD_Solid_RedBrick",
        "Metallic" => MaterialRoot + "Mi_LEGO_MD_Metallic_DynamicTPage_LCS",
        "Transparent" => MaterialRoot + "Mi_LEGO_MD_Transp_DynamicTPage_LCS",
        _ => throw new InvalidDataException("Unknown LEGO paint finish: " + finish)
    };
    internal static bool ValidFinish(string finish) => finish is "Flat" or "Solid" or "Metallic" or "Transparent";
    internal static IEnumerable<string> ExtractionFilters => new[] { Palette, Material("Solid"), Material("Metallic"), Material("Transparent") }.Select(p => "Content/" + p[6..]);
    internal static string DefaultSlotMaterial(string name, string fallback) => name.ToUpperInvariant() switch {
        "LEGO_SOLID" => Material("Solid"), "LEGO_METALLIC" => Material("Metallic"), "LEGO_TRANSPARENT" => Material("Transparent"), _ => fallback
    };
    internal static string ImportSlotMaterial(string name, string? previous, bool vehicle)
    {
        var placeholder = CustomStaticMeshImportService.DefaultMaterialPackagePath;
        if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, placeholder, StringComparison.OrdinalIgnoreCase)) return previous;
        return vehicle ? DefaultSlotMaterial(name, placeholder) : previous ?? placeholder;
    }

    internal static void Stage(string native, VehicleProject vehicle, VehiclePaletteColor color, Action<UAsset,string> save)
        => Create(native, color, VehicleProjectService.ContentRoot(vehicle) + "/Materials/MI_Color_" + color.Slot, save);

    internal static void Create(string native, VehiclePaletteColor color, string materialPackage, Action<UAsset,string> save)
    {
        if (!ValidFinish(color.Finish) || new[] { color.R, color.G, color.B }.Any(v => !float.IsFinite(v) || v < 0 || v > 1))
            throw new InvalidDataException("Invalid vehicle paint color or finish.");
        if (color.Finish == "Flat")
        {
            var flat = VehicleAssetService.Read(native, VehicleAssetService.PaletteTemplate);
            var values = flat.Exports.OfType<NormalExport>().SelectMany(e => EquipmentAssetService.Properties(e.Data))
                .Where(p => p.Path.Contains("VectorParameterValues", StringComparison.Ordinal)).Select(p => p.Property).OfType<LinearColorPropertyData>().ToArray();
            VehicleAssetService.Require(values.Length == 1, "The simple vehicle color template changed.");
            values[0].Value = new FLinearColor(color.R, color.G, color.B, 1);
            CustomEquipmentService.Rename(flat, new Dictionary<string, string> { [VehicleAssetService.PaletteTemplate] = materialPackage });
            save(flat, materialPackage); return;
        }
        var paletteFile = ExtractedPackagePathService.ResolvePackageUasset(native, Palette);
        if (paletteFile is null || !File.Exists(Path.ChangeExtension(paletteFile, ".uexp")))
            throw new InvalidDataException("Native LEGO paint needs its palette texture. Run Full refresh, then rebuild.");
        // This small, linear half-float swatch has one mip and no bulk sidecar.
        // Do not patch an unfamiliar texture layout after a game update.
        if (SkinnedMeshCookService.Hash(paletteFile) != "8A8ED04CF1EB1730114F29AC231B9D7DDBB261AF8BCB934A98492C3C2663E673" ||
            SkinnedMeshCookService.Hash(Path.ChangeExtension(paletteFile, ".uexp")) != "6C6CFEA89E4D5EEEA65EC30046A90B65AF95C0A9BC9252E9D92A13B1D864ABBB")
            throw new InvalidDataException("The native LEGO paint swatch layout changed. Use a chosen material or simple color until this game version is supported.");
        var texture = EquipmentAssetService.ReadRaw(native, Palette, VehicleAssetService.Maps,
            CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
        var raw = texture.Exports.OfType<RawExport>().Single();
        VehicleAssetService.Require(raw.Data.Length == 2198, "Unexpected native LEGO palette export size.");
        WriteUniformPalette(raw.Data, color);
        var texturePackage = materialPackage[..materialPackage.LastIndexOf('/')] + "/T_" + UnrealPathUtil.AssetName(materialPackage) + "_Swatch";
        CustomEquipmentService.Rename(texture, new Dictionary<string,string> { [Palette] = texturePackage }); save(texture, texturePackage);
        var source = Material(color.Finish); var material = VehicleAssetService.Read(native, source);
        SetSwatch(material, texturePackage);
        CustomEquipmentService.Rename(material, new Dictionary<string,string> { [source] = materialPackage }); save(material, materialPackage);
    }
    internal static void WriteUniformPalette(byte[] data, VehiclePaletteColor color)
    {
        if (data.Length < 0x7e + 256 * 8 || new[]{color.R,color.G,color.B}.Any(v=>!float.IsFinite(v)||v<0||v>1))
            throw new InvalidDataException("Invalid LEGO paint swatch.");
        // Uniform swatches keep ordinary Blender vertex colors from selecting an
        // unrelated LEGO hue. Native shader lighting and runtime tint inputs remain.
        for (int i=0;i<256;i++)
        {
            var offset=0x7e+i*8;
            foreach (var (channel,value) in new[]{(0,color.R),(1,color.G),(2,color.B),(3,1f)})
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset+channel*2,2),BitConverter.HalfToUInt16Bits((Half)value));
        }
    }
    private static void SetSwatch(UAsset asset, string package)
    {
        var export=asset.Exports.OfType<NormalExport>().Single();
        var prototype=export.Data.OfType<ArrayPropertyData>().Single(p=>p.Name.ToString()=="ScalarParameterValues").Value.OfType<StructPropertyData>().First();
        var entry=(StructPropertyData)PartGraftService.DeepClonePropertiesRebased([prototype],asset).Single();
        entry.Name=new FName(asset,"0"); entry.StructType=new FName(asset,"TextureParameterValue");
        var info=entry.Value.OfType<StructPropertyData>().Single(p=>p.Name.ToString()=="ParameterInfo");
        info.Value.OfType<NamePropertyData>().Single(p=>p.Name.ToString()=="Name").Value=new FName(asset,"LEGO Swatch Colours");
        entry.Value.RemoveAll(p=>p.Name.ToString()=="ExpressionGUID");
        entry.Value.Add(new StructPropertyData(new FName(asset,"ExpressionGUID")) { StructType=new FName(asset,"Guid"), Value=[new GuidPropertyData(new FName(asset,"ExpressionGUID")){Value=Guid.Empty}] });
        var texture=SwordCombatService.Obj(asset,package,UnrealPathUtil.AssetName(package),"/Script/Engine","Texture2D");
        var index=entry.Value.FindIndex(p=>p.Name.ToString()=="ParameterValue");
        entry.Value[index]=new ObjectPropertyData(new FName(asset,"ParameterValue")){Value=texture};
        VehicleAssetService.Require(!export.Data.Any(p=>p.Name.ToString()=="TextureParameterValues"), "The native LEGO material now has texture overrides. Review its template before changing the swatch.");
        export.Data.Add(new ArrayPropertyData(new FName(asset,"TextureParameterValues")){ArrayType=new FName(asset,"StructProperty"),Value=[entry]});
        export.CreateBeforeSerializationDependencies.Add(texture);
    }
}
