using System.Drawing.Imaging;
using System.Text.Json;

namespace Batcomputer;

internal static class CharacterSymbolRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        string Png(int width, int height, bool full = false, bool empty = false)
        {
            using var image = new Bitmap(width, height);
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.Transparent);
                if (!empty) g.FillEllipse(Brushes.White, full ? -width : width / 4, full ? -height : height / 4, full ? width * 3 : width / 2, full ? height * 3 : height / 2);
            }
            using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png); return Convert.ToBase64String(stream.ToArray());
        }
        bool Reject(string source) { try { CharacterSymbolService.Validate(source); return false; } catch { return true; } }
        var png = Png(256, 256);
        results.Add((CharacterSymbolService.Validate(png).Length > 0, "character symbol accepts a transparent square silhouette"));
        results.Add((Reject(Png(128, 64)) && Reject(Png(32, 32)), "character symbol rejects stretched and undersized sources"));
        results.Add((Reject(Png(64, 64, full: true)) && Reject(Png(64, 64, empty: true)), "character symbol rejects opaque backgrounds and empty silhouettes"));
        results.Add((Reject("not-base64") && Reject(new string('A', 6 * 1024 * 1024)), "character symbol rejects corrupt and oversized embedded sources"));
        var definition = CustomCharacterProjectService.CreateRecipe(null, "Moon Knight", "MoonKnight", "MoonKnight");
        definition.CustomCharacter!.SymbolPngBase64 = png;
        var roundtrip = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(definition))!;
        results.Add((roundtrip.CustomCharacter!.SymbolPngBase64 == png, "character symbol source survives recipe serialization without external file dependencies"));
        var child = CustomCharacterProjectService.CreateRecipe(definition, "Unmasked", "MoonKnight", "Unmasked", definition.SlotId);
        results.Add((string.IsNullOrEmpty(child.CustomCharacter!.SymbolPngBase64), "child suit inherits its owner's symbol instead of copying an independently editable PNG"));
        results.Add((CharacterSymbolService.MaterialPackage("ModA", "MoonKnight") != CharacterSymbolService.MaterialPackage("ModB", "MoonKnight"), "character symbol packages are scoped to their mod and character"));
        return results;
    }
}
