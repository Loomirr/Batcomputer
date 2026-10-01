using System.Security.Cryptography;
using System.Text.Json;

namespace Batcomputer;

internal static class GameplayAnimationGraphRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var original = "/Game/Abilities/GA_Original";
        var replacement = "/Game/Mods/Example/Combat/GA_Custom";
        var selection = new GameplayAnimationGraphSelection { Id = new string('a',32), Name = "Example combat",
            OwnerDprdPackage = "/Game/Mods/Example/Characters/DA_DPRD_Example", Abilities = [new() { OriginalPackage = original, ReplacementPackage = replacement }] };
        GameplayAnimationGraphService.ValidateSelection(selection);
        var project = new NativeSuitProject { TargetPackages = new() { Playable = "/Game/Mods/Example/Characters/BP_Example" }, GameplayAnimationGraphs = [selection] };
        var restored = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project))!;
        yield return (restored.GameplayAnimationGraphs[0].Id == selection.Id && restored.GameplayAnimationGraphs[0].Abilities[0].ReplacementPackage == replacement &&
            JsonSerializer.Deserialize<NativeSuitProject>("{}")!.GameplayAnimationGraphs.Count == 0 && AnimArchetypeGraftService.RequiresCustomArchetype(project),
            "cooked combat/reaction graph bindings survive project saves and require suit-local archetype wiring; older recipes remain unchanged");
        yield return (GameplayAnimationGraphService.ValidPrivatePackage(replacement) && !GameplayAnimationGraphService.ValidPrivatePackage("/Game/Abilities/GA_Base") &&
            !GameplayAnimationGraphService.ValidPrivatePackage("/Game/Mods/Example/../Other/GA") &&
            Reject(() => GameplayAnimationGraphService.ValidateSelection(new() { Id = "../escape", OwnerDprdPackage = selection.OwnerDprdPackage, Abilities = selection.Abilities })) &&
            Reject(() => GameplayAnimationGraphService.ValidateSelection(new() { Id = selection.Id, OwnerDprdPackage = "/Game/Characters/DA_Base", Abilities = selection.Abilities })) &&
            Reject(() => GameplayAnimationGraphService.ValidateSelection(new() { Id = selection.Id, OwnerDprdPackage = selection.OwnerDprdPackage, Abilities = [selection.Abilities[0],selection.Abilities[0]] })),
            "animation graph caches and binding targets reject traversal, native output paths and duplicate grant replacements");
        yield return (Reject(() => GameplayAnimationGraphService.ValidateSelections(new() { GameplayAnimationGraphs = [selection, selection] })),
            "multiple saved animation graphs cannot compete for the same ability grant");
        var folder = Directory.CreateTempSubdirectory("BatcomputerGraphChecks-").FullName;
        var cache = Path.Combine(GameplayAnimationGraphService.CacheRoot(folder), selection.Id);
        var members = new List<GameplayAnimationGraphService.CachedFile>();
        foreach (var ext in new[] { ".uasset", ".uexp" })
        {
            var relative = replacement[6..] + ext;var path = Path.Combine(cache,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,[1,2,3]);
            members.Add(new(relative,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }
        var manifest = new GameplayAnimationGraphService.Manifest(1,selection.Id,"Example",[new(replacement,[],members.ToArray())]);
        var manifestPath = Path.Combine(cache,"manifest.json");
        File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest));
        var stage = Path.Combine(folder,"Stage");
        GameplayAnimationGraphService.Stage(project,folder,stage,_=>{});
        var target = Path.Combine(stage,replacement[6..]+".uasset");
        var before = File.ReadAllBytes(target);GameplayAnimationGraphService.Stage(project,folder,stage,_=>{});
        yield return (before.SequenceEqual(File.ReadAllBytes(target)) && before.SequenceEqual(new byte[]{1,2,3}),
            "saved gameplay animation graph staging preserves immutable cooked bytes and repeated staging is idempotent");
        File.WriteAllBytes(target,[9]);
        yield return (Reject(() => GameplayAnimationGraphService.Stage(project,folder,stage,_=>{})) && File.ReadAllBytes(target).SequenceEqual(new byte[]{9}),
            "gameplay animation graph staging stops on conflicting package bytes instead of choosing an overlay by order");
        File.WriteAllBytes(Path.Combine(cache,members[0].Path),[8]);
        yield return (Reject(() => GameplayAnimationGraphService.Load(folder,selection)),
            "missing or changed animation graph cache members stop rebuilding rather than silently reverting to native combat");
        File.WriteAllBytes(Path.Combine(cache,members[0].Path),[1,2,3]);
        File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest with { Packages = [new(replacement,["/Game/Mods/Example/Combat/Missing"],members.ToArray())] }));
        yield return (Reject(() => GameplayAnimationGraphService.Load(folder,selection)),
            "saved animation graph caches reject incomplete private dependency closures");
        File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest with { Packages = [new(replacement,[],[new("../escape.uasset",members[0].Sha256),members[1]])] }));
        yield return (Reject(() => GameplayAnimationGraphService.Load(folder,selection)),
            "animation graph cache members must match their declared package identities and cannot escape the cache root");
        GameplayAnimationGraphService.Apply(new(),"not-created",_=>{});
        var owner = CustomCharacterProjectService.CreateRecipe(null,"Example","Example","Example");
        owner.GameplayAnimationGraphs = [selection];
        owner.GameplayAnimationGraphs[0].OwnerDprdPackage = "/Game/Mods/CC_Example_Example/Characters/DA_DPRD_CC_Example_Example";
        var child = CustomCharacterProjectService.CreateRecipe(owner,"Alternate","Example","Alternate",owner.SlotId);
        yield return (child.GameplayAnimationGraphs[0].Id == selection.Id && child.GameplayAnimationGraphs[0].Abilities[0].ReplacementPackage == replacement &&
            child.GameplayAnimationGraphs[0].OwnerDprdPackage == "/Game/Mods/CC_Example_Alternate/Characters/DA_DPRD_CC_Example_Alternate" &&
            !ReferenceEquals(child.GameplayAnimationGraphs[0].Abilities,owner.GameplayAnimationGraphs[0].Abilities) &&
            CharacterRebaseService.Sections().Single(s=>s.Id=="animations").Fields.Contains("GameplayAnimationGraphs"),
            "new suits and animation rebases retain cooked graph cache identities while directing grant wiring to the receiving suit's own DPRD");
    }
    private static bool Reject(Action action) { try { action();return false; } catch(InvalidDataException) { return true; } }
}
