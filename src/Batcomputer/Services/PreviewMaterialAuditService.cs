using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Materials;

namespace Batcomputer;

/// <summary>Read-only material provenance and resolved native parameters for viewer diagnosis.</summary>
internal static class PreviewMaterialAuditService
{
    public static void Run(string paks, string mappings, string output, string looseContent, IEnumerable<string> paths)
    {
        Directory.CreateDirectory(output);
        using var provider = ModelPreviewService.MakeProvider(paks, mappings, [looseContent]);
        foreach (var path in paths)
        {
            var material = provider.LoadPackageObject(path) as UMaterialInterface
                ?? throw new InvalidDataException($"Not a material: {path}");
            var parameters = new CMaterialParams2();
            material.GetParams(parameters, EMaterialFormat.AllLayers);
            var chain = new List<string>();
            for (UMaterialInterface? node = material; node is not null && chain.Count < 32;)
            {
                var name = node.GetPathName();
                if (chain.Contains(name)) break;
                chain.Add(name);
                node = node.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.Load() as UMaterialInterface;
            }
            var textures = parameters.Textures.ToDictionary(pair => pair.Key, pair => pair.Value is UTexture2D texture
                ? new { Path = texture.GetPathName(), Format = texture.Format.ToString(), SRGB = texture.GetOrDefault("SRGB", true) }
                : null);
            var report = new { Material = path, Chain = chain, Textures = textures, Scalars = parameters.Scalars, Switches = parameters.Switches };
            File.WriteAllText(Path.Combine(output, material.Name + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var entry in parameters.Textures.Where(pair => pair.Key is "MMR" or "MMR_Pristine" or "RAO"))
                if (entry.Value is UTexture2D texture)
                    TextureDecodeService.TryExportPng(texture, Path.Combine(output, texture.Name + "_raw.png"), keepAlpha: true);
            Console.WriteLine($"{material.Name}: {chain.Count} levels, {textures.Count} textures, {parameters.Scalars.Count} scalars, {parameters.Switches.Count} switches");
        }
    }
}
