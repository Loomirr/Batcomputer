using System.Collections.Concurrent;
using System.Drawing;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

internal static class VehicleLightService
{
    private static readonly object MapLock = new();
    private static string _mapKey = "";
    private static Usmap? _maps;
    internal static bool IsSource(string id) => id is "H_Light_01_Light_GEN_VARIABLE" or "H_Light_02_Light_GEN_VARIABLE" or "R_Light_01_Light_GEN_VARIABLE" or "R_Light_02_Light_GEN_VARIABLE";
    internal static bool IsClass(string? name) => name is "BP_VehicleLight_Player_HeadLight_C" or "BP_VehicleLight_Player_Rearlight_C" or "BP_VehicleLight_Player_RearLight_C";
    internal static Usmap Maps
    {
        get
        {
            var file = AppSettings.Current.EffectiveUsmapPath() ?? throw new InvalidDataException("Set the .usmap mappings path in Settings.");
            var key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks;
            lock (MapLock)
            {
                if (_maps is not null && key == _mapKey) return _maps;
                // These verified Blueprint subclasses add no serialized fields. Use a private
                // schema table: character/material readers must not inherit vehicle aliases.
                var maps = new Usmap(file);
                foreach (var name in new[] { "BP_VehicleLight_Player_C", "BP_VehicleLight_Player_HeadLight_C", "BP_VehicleLight_Player_Rearlight_C", "BP_VehicleLight_Player_RearLight_C" })
                    maps.Schemas[name] = new UsmapSchema(name, name == "BP_VehicleLight_Player_C" ? "TtSpotLightComponent" : "BP_VehicleLight_Player_C", 0, new ConcurrentDictionary<int, UsmapProperty>(), maps.AreFNamesCaseInsensitive, "", true);
                _maps = maps; _mapKey = key; return maps;
            }
        }
    }
    internal static VehicleLightSettings Defaults(string component) => new()
    {
        Component = component, R = component.StartsWith("H_") ? 56 : 255,
        G = component.StartsWith("H_") ? 252 : 0, B = component.StartsWith("H_") ? 246 : 0,
        Intensity = 128, Radius = component.StartsWith("H_") ? 2400 : 500, OuterCone = component.StartsWith("H_") ? 60 : 40
    };
    internal static void Validate(IEnumerable<VehicleLightSettings>? values)
    {
        if (values is null) throw new InvalidDataException("Missing vehicle light settings.");
        var lights = values.ToArray();
        if (lights.Any(l => l is null || !IsSource(l.Component) || l.R is < 0 or > 255 || l.G is < 0 or > 255 || l.B is < 0 or > 255 || !float.IsFinite(l.Intensity) || l.Intensity is < 0 or > 10000 || !float.IsFinite(l.Radius) || l.Radius is < 1 or > 10000 || !float.IsFinite(l.OuterCone) || l.OuterCone is < 1 or > 89) || lights.Select(l => l.Component).Distinct().Count() != lights.Length)
            throw new InvalidDataException("Invalid or duplicate vehicle light settings.");
    }
    internal static void ValidateAccent(VehicleRgbColor? color)
    {
        if (color is not null && (color.R is < 0 or > 255 || color.G is < 0 or > 255 || color.B is < 0 or > 255)) throw new InvalidDataException("Invalid vehicle accent color.");
    }
    private static float[] Linear(VehicleRgbColor color) => new[] { color.R, color.G, color.B }.Select(v => v <= 10 ? v / 255f / 12.92f : MathF.Pow((v / 255f + .055f) / 1.055f, 2.4f)).ToArray();
    private static bool IsAccent(NormalExport e) => e.GetExportClassType()?.ToString() == "StaticMeshComponent" && (e.ObjectName.ToString().StartsWith("C_Glow_") || e.ObjectName.ToString().StartsWith("C_LED_"));
    internal static void ApplyAccent(UAsset asset, VehicleRgbColor? color, bool gameplay)
    {
        if (color is null) return;
        ValidateAccent(color); var rgb = Linear(color); var edited = 0;
        foreach (var component in asset.Exports.OfType<NormalExport>().Where(IsAccent))
        {
            var data = component.Data.OfType<StructPropertyData>().SingleOrDefault(p => p.Name.ToString() == "CustomPrimitiveData");
            if (data is null) { data = new(new FName(asset, "CustomPrimitiveData")) { StructType = new FName(asset, "CustomPrimitiveData"), Value = [] }; component.Data.Add(data); }
            var array = data.Value.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "Data");
            if (array is null) { array = new(new FName(asset, "Data")) { ArrayType = new FName(asset, "FloatProperty"), Value = [] }; data.Value.Add(array); }
            var entries = array.Value.ToList(); while (entries.Count < 17) entries.Add(new FloatPropertyData(new FName(asset, entries.Count.ToString())) { Value = 0 });
            for (int i = 0; i < 3; i++) entries[14 + i] = new FloatPropertyData(new FName(asset, (14 + i).ToString())) { Value = rgb[i] };
            array.Value = entries.ToArray(); array.IsZero = false; data.IsZero = false; edited++;
        }
        VehicleAssetService.Require(edited > 0, "The native accent-light components changed.");
        if (!gameplay) return;
        var constants = AccentConstants(asset).ToArray();
        VehicleAssetService.Require(constants.Length == 1, "The native accent-color initialization graph changed.");
        for (int i = 0; i < 3; i++) ((EX_FloatConst)constants[0].Value[i]).Value = rgb[i];
    }
    private static IEnumerable<EX_StructConst> AccentConstants(UAsset asset)
    {
        var found = new List<EX_StructConst>();
        foreach (var function in asset.Exports.OfType<FunctionExport>())
        {
            uint offset = 0;
            foreach (var expression in function.ScriptBytecode ?? []) expression.Visit(asset, ref offset, (e, _) =>
            {
                if (e is EX_CallMath call && call.StackNode.IsImport() && call.StackNode.ToImport(asset).ObjectName.ToString() == "Conv_LinearColorToVector" && call.Parameters is [EX_StructConst value] && value.Value.Length == 4 && value.Value.All(v => v is EX_FloatConst)) found.Add(value);
            });
        }
        return found;
    }
    internal static void VerifyAccent(UAsset asset, VehicleRgbColor? color, bool gameplay)
    {
        if (color is null) return; var rgb = Linear(color);
        foreach (var component in asset.Exports.OfType<NormalExport>().Where(IsAccent))
        {
            var values = component.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "CustomPrimitiveData").Value.OfType<ArrayPropertyData>().Single().Value;
            VehicleAssetService.Require(Enumerable.Range(0, 3).All(i => Math.Abs(((FloatPropertyData)values[14 + i]).Value - rgb[i]) < .000001f), "Accent mesh color did not survive staging.");
        }
        if (gameplay) VehicleAssetService.Require(AccentConstants(asset).Count() == 1 && Enumerable.Range(0, 3).All(i => Math.Abs(((EX_FloatConst)AccentConstants(asset).Single().Value[i]).Value - rgb[i]) < .000001f), "Accent initialization color did not survive staging.");
    }
    internal static void Apply(UAsset asset, IEnumerable<VehicleLightSettings> values)
    {
        Validate(values);
        foreach (var light in values)
        {
            var component = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == light.Component && IsClass(e.GetExportClassType()?.ToString()));
            void Float(string name, float value) { component.Data.RemoveAll(p => p.Name.ToString() == name); component.Data.Add(new FloatPropertyData(new FName(asset, name)) { Value = value }); }
            Float("Intensity", light.Intensity); Float("AttenuationRadius", light.Radius); Float("OuterConeAngle", light.OuterCone);
            // Keep the inner cone within the outer cone even if an inherited template changes.
            Float("InnerConeAngle", 0);
            component.Data.RemoveAll(p => p.Name.ToString() == "LightColor");
            component.Data.Add(new StructPropertyData(new FName(asset, "LightColor")) { StructType = new FName(asset, "Color"), Value = [new ColorPropertyData(new FName(asset, "LightColor")) { Value = Color.FromArgb(255, light.R, light.G, light.B) }] });
            // Rear lights switch between 128 and 250 on the donor's native brake graph. Scale
            // both existing constants; changing their byte length or bypassing the graph would
            // break jumps or remove the brake response. No calls are inserted or removed.
            var calls = IntensityCalls(asset, light.Component).ToArray();
            if (light.Component.StartsWith("R_")) VehicleAssetService.Require(calls.Length == 2 && calls.Select(v => v.Value).Order().SequenceEqual(new[] { 128f, 250f }), "Native rear-light brake graph changed. Light edits cannot be staged safely.");
            foreach (var value in calls) value.Value = value.Value > 128 ? light.Intensity * (250f / 128f) : light.Intensity;
        }
    }
    private static IEnumerable<EX_FloatConst> IntensityCalls(UAsset asset, string component)
    {
        var result = new List<EX_FloatConst>();
        foreach (var function in asset.Exports.OfType<FunctionExport>())
        {
            uint offset = 0;
            foreach (var expression in function.ScriptBytecode ?? []) expression.Visit(asset, ref offset, (e, _) =>
            {
                if (e is EX_Context { ObjectExpression: EX_InstanceVariable target, ContextExpression: EX_FinalFunction call } &&
                    target.Variable.New?.Path.LastOrDefault()?.ToString() == component.Replace("_GEN_VARIABLE", "") &&
                    call.StackNode.IsImport() && call.StackNode.ToImport(asset).ObjectName.ToString() == "SetIntensity" && call.Parameters is [EX_FloatConst value]) result.Add(value);
            });
        }
        return result;
    }
    internal static void Verify(UAsset asset, IEnumerable<VehicleLightSettings> values)
    {
        foreach (var light in values)
        {
            var component = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == light.Component);
            float Float(string name) => component.Data.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == name).Value;
            var color = component.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "LightColor").Value.OfType<ColorPropertyData>().Single().Value;
            VehicleAssetService.Require(color.R == light.R && color.G == light.G && color.B == light.B && Float("Intensity") == light.Intensity && Float("AttenuationRadius") == light.Radius && Float("OuterConeAngle") == light.OuterCone, "Vehicle light settings did not survive staging.");
            if (light.Component.StartsWith("R_"))
            {
                var calls = IntensityCalls(asset, light.Component).Select(v => v.Value).Order().ToArray();
                VehicleAssetService.Require(calls.Length == 2 && Math.Abs(calls[0] - light.Intensity) < .001 && Math.Abs(calls[1] - light.Intensity * (250f / 128f)) < .001, "Rear-light brake response did not survive staging.");
            }
        }
    }
}
