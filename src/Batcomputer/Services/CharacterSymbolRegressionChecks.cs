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
        var native = Png(512,256);
        results.Add((CharacterSymbolService.Validate(native,CharacterSymbolService.NativeProfile).Length>0 && CharacterSymbolService.Validate(png,CharacterSymbolService.NativeProfile).Length>0,
            "native character emblem accepts wide and square transparent artwork without stretching"));
        using (var stream=new MemoryStream(Convert.FromBase64String(native)))
        using (var image=Image.FromStream(stream))
        {
            var field=CharacterSymbolSdfService.Generate(image);
            results.Add((field.Length==8192 && field[32*128+64]>128 && field[0]<128, "native emblem produces positive-inside 128x64 single-channel distance field"));
            results.Add((field[0]==0 && field.Min()==0 && field.Max()==255,
                "native emblem saturates distant background/interior to native G8 endpoints, not 9/244"));
            using var preview=CharacterSymbolSdfService.Preview(image,.3f);
            var pixels=Enumerable.Range(0,64).SelectMany(y=>Enumerable.Range(0,128).Select(x=>preview.GetPixel(x,y))).ToArray();
            results.Add((preview.GetPixel(0,0).A==0 && pixels.Any(p=>p.A>0 && p.R==0 && p.G==0 && p.B==0) &&
                !pixels.Any(p=>p.A>0 && p.R!=p.G), "native emblem preview uses a black outline and clear distant background"));
        }
        bool RejectNative(string value) { try { CharacterSymbolService.Validate(value,CharacterSymbolService.NativeProfile); return false; } catch { return true; } }
        results.Add((RejectNative(Png(256,256,full:true)) && RejectNative(Png(256,128,empty:true)), "native emblem rejects opaque backgrounds and empty silhouettes"));
        results.Add((CharacterSymbolService.Validate(png).Length > 0, "character symbol accepts a transparent square silhouette"));
        results.Add((Reject(Png(128, 64)) && Reject(Png(32, 32)), "character symbol rejects stretched and undersized sources"));
        results.Add((Reject(Png(64, 64, full: true)) && Reject(Png(64, 64, empty: true)), "character symbol rejects opaque backgrounds and empty silhouettes"));
        results.Add((Reject("not-base64") && Reject(new string('A', 6 * 1024 * 1024)), "character symbol rejects corrupt and oversized embedded sources"));
        var definition = CustomCharacterProjectService.CreateRecipe(null, "Moon Knight", "MoonKnight", "MoonKnight");
        definition.CustomCharacter!.SymbolPngBase64 = png;
        definition.CustomCharacter.SymbolCookProfile=CharacterSymbolService.NativeProfile;
        definition.CustomCharacter.SymbolBorderThickness=.2f;
        var roundtrip = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(definition))!;
        results.Add((roundtrip.CustomCharacter!.SymbolPngBase64 == png, "character symbol source survives recipe serialization without external file dependencies"));
        results.Add((roundtrip.CustomCharacter.SymbolCookProfile==CharacterSymbolService.NativeProfile && roundtrip.CustomCharacter.SymbolBorderThickness==.2f, "emblem cook profile and outline survive recipe serialization"));
        var child = CustomCharacterProjectService.CreateRecipe(definition, "Unmasked", "MoonKnight", "Unmasked", definition.SlotId);
        results.Add((string.IsNullOrEmpty(child.CustomCharacter!.SymbolPngBase64), "child suit inherits its owner's symbol instead of copying an independently editable PNG"));
        results.Add((CharacterSymbolService.MaterialPackage("ModA", "MoonKnight") != CharacterSymbolService.MaterialPackage("ModB", "MoonKnight"), "character symbol packages are scoped to their mod and character"));
        return results;
    }
}
