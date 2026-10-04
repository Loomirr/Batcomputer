using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json.Linq;

namespace Batcomputer;

/// <summary>Only exposes real scalar controls from the donor's compiled material chain.</summary>
internal static class MaterialSurfaceControlService
{
    internal sealed record Control(string Name, float Value, bool UvChannel);
    internal static bool IsUvChannel(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return (key.Contains("uv") || key.Contains("texcoord")) &&
            (key.Contains("channel") || key.Contains("index") || key is "uvset" or "texcoord");
    }
    internal static bool IsSurface(string name) => IsUvChannel(name) ||
        new[] { "fuzz", "fluff", "hair card", "hair fuzz", "micro detail", "micro noise", "noise scale", "noise strength", "tiling", "normal intensity" }
            .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    internal static bool IsFuzzStrength(string name) =>
        name.Equals("Fuzz Strength", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Fluffball Strength", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Hair Card Intensity", StringComparison.OrdinalIgnoreCase);
    internal static IReadOnlyList<Control> Read(MaterialGenService.MaterialTemplateInfo template)
    {
        var package = UnrealPathUtil.NormalizePackagePath(template.ParentMaterialPath);
        if (string.IsNullOrWhiteSpace(package)) return [];
        using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(),
            AppSettings.Current.EffectiveUsmapPath()!, package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)
                ? [AppSettings.Current.EffectiveExportContentRoot()] : null);
        var chain = new List<UMaterialInterface>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var material = provider.LoadPackageObject<UMaterialInterface>(package); material is not null && chain.Count < 32;
             material = material.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load() as UMaterialInterface)
        {
            if (!seen.Add(material.GetPathName())) throw new InvalidDataException("Cyclic material parent chain.");
            chain.Add(material);
        }
        if (chain.LastOrDefault() is not UMaterial master || master.CachedExpressionData is null) return [];
        var controls = ReadGlobalDefaults(JObject.FromObject(master.CachedExpressionData));
        foreach (var material in chain.AsEnumerable().Reverse())
        foreach (var entry in material.GetOrDefault<FStructFallback[]>("ScalarParameterValues") ?? [])
        {
            var info = entry.GetOrDefault<FStructFallback>("ParameterInfo");
            if (info is null || !IsGlobal(JObject.FromObject(info))) continue;
            var name = info.GetOrDefault<FName>("Name").Text;
            var value = entry.GetOrDefault<float>("ParameterValue");
            if (controls.ContainsKey(name) && float.IsFinite(value)) controls[name] = value;
        }
        foreach (var scalar in template.ScalarParams.Where(p => IsSurface(p.Name) && float.IsFinite(p.Value)))
            if (controls.ContainsKey(scalar.Name)) controls[scalar.Name] = scalar.Value;
        return controls.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new Control(p.Key, p.Value, IsUvChannel(p.Key))).ToArray();
    }

    private static bool IsGlobal(JObject info) => info.Value<int?>("Index") == -1 &&
        info.Value<string>("Association") is "GlobalParameter" or "EMaterialParameterAssociation::GlobalParameter";

    internal static Dictionary<string, float> ReadGlobalDefaults(JObject cache)
    {
        var controls = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        // Cached expression entry 0 is the scalar table. Its indexed values belong to these
        // exact parameter infos, not same-named inputs on another material layer.
        if (cache["RuntimeEntries"]?["ParameterInfoSet"] is not JArray infos || cache["ScalarValues"] is not JArray values ||
            infos.Count != values.Count) return controls;
        for (var i = 0; i < infos.Count; i++)
        {
            if (infos[i] is not JObject info || !IsGlobal(info) || info.Value<string>("Name") is not { } name || !IsSurface(name)) continue;
            var value = values[i].Value<float>();
            if (float.IsFinite(value)) controls[name] = value;
        }
        return controls;
    }
}
