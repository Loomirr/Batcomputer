namespace Batcomputer;

internal static class CharacterAssetReuseRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var aliases = new Dictionary<string, string> { ["/Game/Mods/ExampleSuit/Meshes/SM_Claws"] = "/Game/Mods/ExampleHero/Meshes/SM_Claws" };
        string Redirect(string text) => CharacterAssetReuseService.Redirect(text, aliases);
        var declared = new NativeSuitProject { TargetPackages = new() { Playable = "/Game/Mods/ExampleHero/Characters/BP_Playable" },
            CustomStaticMeshes = [new() { MeshPackagePath = "/Game/Mods/ExampleGeometry/Meshes/SM_Claws" }] };
        declared.ReleaseAssetAliases = aliases;
        yield return (!System.Text.Json.JsonSerializer.Serialize(declared).Contains("ReleaseAssetAliases", StringComparison.Ordinal) &&
            CharacterAssetReuseService.Resolve("/Game/Mods/ExampleSuit/Meshes/SM_Claws", aliases) == "/Game/Mods/ExampleHero/Meshes/SM_Claws",
            "shared-asset validation context resolves proven aliases without adding build state to saved authoring recipes");
        yield return (CharacterAssetReuseService.DeclaredRoots(declared).SequenceEqual(new[] { "/Game/Mods/ExampleGeometry", "/Game/Mods/ExampleHero" }),
            "shared-asset discovery includes explicitly declared geometry roots as well as the character root");
        yield return (Redirect("/Game/Mods/ExampleSuit/Meshes/SM_Claws.SM_Claws") == "/Game/Mods/ExampleHero/Meshes/SM_Claws.SM_Claws" &&
            Redirect("StaticMesh'/Game/Mods/ExampleSuit/Meshes/SM_Claws.SM_Claws'") == "StaticMesh'/Game/Mods/ExampleHero/Meshes/SM_Claws.SM_Claws'" &&
            Redirect("/Game/Mods/ExampleSuit/Meshes/SM_ClawsExtra") == "/Game/Mods/ExampleSuit/Meshes/SM_ClawsExtra" &&
            Redirect("Status.ExampleSuit.Claws") == "Status.ExampleSuit.Claws",
            "shared-asset redirects change exact package/object references, never similar names or owner-local gameplay tags");
        var folder = Directory.CreateTempSubdirectory("BatcomputerReuseChecks-").FullName;
        try
        {
            var files = new[] { "Child", "Parent", "NormalizedChild", "NormalizedParent" }.Select(n => Path.Combine(folder, n + ".uasset")).ToArray();
            foreach (var file in files) { File.WriteAllBytes(file, [1,2,3]); File.WriteAllBytes(Path.ChangeExtension(file,".uexp"), [4,5,6]); }
            bool Same() => CharacterAssetReuseService.EquivalentFamily(files[0], files[1], files[2], files[3]);
            bool matched = Same();
            File.WriteAllBytes(Path.ChangeExtension(files[0], ".ubulk"), [7]); bool missing = !Same();
            File.WriteAllBytes(Path.ChangeExtension(files[1], ".ubulk"), [8]); bool bulkChanged = !Same();
            File.WriteAllBytes(Path.ChangeExtension(files[1], ".ubulk"), [7]); bool bulkSame = Same();
            File.WriteAllBytes(Path.ChangeExtension(files[2], ".uexp"), [4,5,9]); bool payloadChanged = !Same();
            yield return (matched && missing && bulkChanged && bulkSame && payloadChanged,
                "asset sharing requires identical normalized headers, export data and every bulk sidecar; changed or missing payloads stay separate");
        }
        finally { if (FileSystemPathUtil.IsWithinDirectory(folder, Path.GetTempPath())) Directory.Delete(folder, true); }
    }
}
