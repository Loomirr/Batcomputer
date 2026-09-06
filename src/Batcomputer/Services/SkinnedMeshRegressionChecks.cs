using System.Text;
using System.Text.Json;

namespace Batcomputer;

internal static class SkinnedMeshRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool pass, string name) => results.Add((pass, name));
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var donorAsset = new UAssetAPI.UAsset { Imports = [], Exports = [] }; donorAsset.ClearNameIndexList();
        var targetAsset = new UAssetAPI.UAsset { Imports = [], Exports = [] }; targetAsset.ClearNameIndexList();
        var owner = SwordCombatService.Obj(donorAsset, "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter", "BP_CutsceneMinifigCharacter_C", "/Script/Engine", "BlueprintGeneratedClass");
        var template = donorAsset.AddImport(new UAssetAPI.Import("/Script/TtLocationAttachmentManager", "TtLocationAttachmentManagerComponent", owner, "TtLocationAttachmentManager_GEN_VARIABLE", false, donorAsset));
        var rebased = AttachmentClearanceService.RebaseImport(targetAsset, donorAsset, template);
        Check(rebased.ToImport(targetAsset).OuterIndex.ToImport(targetAsset).ObjectName.ToString() == "BP_CutsceneMinifigCharacter_C" &&
            rebased.ToImport(targetAsset).OuterIndex.ToImport(targetAsset).OuterIndex.ToImport(targetAsset).ObjectName.ToString() == "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter",
            "cutscene attachment templates retain the full package/class/component import chain");
        var first = SwordCombatService.Obj(targetAsset, "/Game/Mods/Fixture/MI_Slot_0", "MI_Slot_0", "/Script/Engine", "MaterialInstanceConstant");
        var second = SwordCombatService.Obj(targetAsset, "/Game/Mods/Fixture/MI_Slot_1", "MI_Slot_1", "/Script/Engine", "MaterialInstanceConstant");
        // Explicitly model the shared numeric FNames found in UE's cooked material imports.
        targetAsset.AddNameReference(new UAssetAPI.UnrealTypes.FString("MI_Slot"));
        first.ToImport(targetAsset).ObjectName = new UAssetAPI.UnrealTypes.FName(targetAsset, "MI_Slot", 1);
        second.ToImport(targetAsset).ObjectName = new UAssetAPI.UnrealTypes.FName(targetAsset, "MI_Slot", 2);
        SkinnedMeshStageService.RedirectImports(targetAsset, new Dictionary<string,string> {
            ["MI_Slot_0"] = "MI_Body", ["MI_Slot_1"] = "MI_Trim",
            ["/Game/Mods/Fixture/MI_Slot_0"] = "/Game/Materials/MI_Body", ["/Game/Mods/Fixture/MI_Slot_1"] = "/Game/Materials/MI_Trim" });
        Check(first.ToImport(targetAsset).ObjectName.ToString() == "MI_Body" && second.ToImport(targetAsset).ObjectName.ToString() == "MI_Trim" &&
            first.ToImport(targetAsset).OuterIndex.ToImport(targetAsset).ObjectName.ToString() == "/Game/Materials/MI_Body",
            "skeletal placeholder materials redirect complete numbered FNames without conflating slots");
        foreach (var (name, header) in new[] { ("DinnerLocationAttachmentManagerComponent", (ushort)0x0303), ("TtLocationAttachmentManagerComponent", (ushort)0x0301) })
        {
            var manager = new UAssetAPI.ExportTypes.RawExport { Asset = targetAsset,
                ClassIndex = SwordCombatService.Obj(targetAsset, "/Script/TtLocationAttachmentManager", name, "/Script/CoreUObject", "Class") };
            manager.Data = AttachmentClearanceService.Encode(targetAsset, [new("Neck", [0,0,0,1,3,0,0,1,1,1], 1)], manager);
            Check(BitConverter.ToUInt16(manager.Data) == header && AttachmentClearanceService.Read(targetAsset, manager).Single().X == 3,
                name + " uses its own unversioned offset property index");
        }
        Check(Math.Abs(AttachmentClearanceService.Clearance(new() { Target = "Hip" }) - 4.3f) < .0001 &&
            AttachmentClearanceService.Clearance(new() { Target = "Shoulder" }) == 3 &&
            AttachmentClearanceService.Clearance(new() { Target = "Head" }) == 0,
            "attachment clearance uses native belt/shoulder defaults, not head offsets");
        Check(AttachmentClearanceService.Clearance(new() { Target = "Hip", BodyClearance = 0 }) == 0 &&
            Reject(() => AttachmentClearanceService.Clearance(new() { BodyClearance = float.NaN })) &&
            Reject(() => AttachmentClearanceService.Clearance(new() { BodyClearance = -1 })),
            "attachment clearance can be disabled and rejects invalid values");
        var recipe = new SkinnedMeshImport { DonorMeshPackage = "/Game/Characters/SK_Donor", SkeletonPackage = "/Game/Characters/SKEL_Donor",
            MeshPackage = "/Game/Mods/Fixture/Skinned/SK_Custom", Materials = [new() { Slot = 0, SourceMaterialName = "Body", MaterialPath = "/Game/Materials/MI_Body" }] };
        var clone = recipe.Clone(); clone.Materials[0].MaterialPath = "/Game/Materials/MI_Changed"; clone.HiddenComponents.Add("Head");
        Check(recipe.Materials[0].MaterialPath == "/Game/Materials/MI_Body" && recipe.HiddenComponents.Count == 0,
            "skinned authoring sessions deep-clone materials and hidden components");
        clone.HiddenComponents.Add("CharacterMesh0");
        Check(Reject(() => SkinnedMeshStageService.ValidateRecipe(clone)), "skinned replacements cannot hide the body or themselves");
        clone = recipe.Clone(); clone.Materials[0].Slot = 1;
        Check(Reject(() => SkinnedMeshStageService.ValidateRecipe(clone)), "skinned material slots must be contiguous from zero");
        clone = recipe.Clone(); clone.MeshPackage = "/Game/Characters/SK_Original";
        Check(Reject(() => SkinnedMeshStageService.ValidateRecipe(clone)), "skinned outputs cannot overwrite native game meshes");
        var project = new NativeSuitProject { SkinnedMeshes = [recipe] };
        Check(MainForm.CountCustomStaticMeshMaterialReferences(project, "/Game/Materials/MI_Body") == 1 &&
            MainForm.ReplaceCustomStaticMeshMaterialReferences(project, "/Game/Materials/MI_Body", "/Game/Materials/MI_New") == 1 &&
            recipe.Materials[0].MaterialPath == "/Game/Materials/MI_New", "material reference counting/renaming includes skinned recipes");

        var root = Path.Combine(Path.GetTempPath(), "Batcomputer-skinned-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(Reject(() => SkinnedMeshCookService.SafePath(root, "../escape.fbx")) &&
                Reject(() => SkinnedMeshCookService.SafePath(root, Path.GetFullPath(Path.Combine(root, "source.fbx")))),
                "skinned cache/source paths cannot escape their project");
            var source = Path.Combine(root, "fixture.fbx");
            foreach (var version in new[] { 7400, 7500 })
            {
                File.WriteAllBytes(source, Fbx(version, [1d, 1d, 1d], 1));
                var audit = SkinnedFbxValidator.Inspect(source);
                Check(audit.Vertices == 3 && audit.Bones == 1, $"binary FBX {version} preflight reads a weighted mesh without mutating it");
            }
            foreach (var (name, weights, count) in new[] {
                ("unweighted", new[]{1d, 1d, 0d}, 1), ("unnormalized", new[]{1d, .5d, 1d}, 1),
                ("non-finite", new[]{1d, double.NaN, 1d}, 1), ("excess influences", new[]{1d/9, 1d/9, 1d/9}, 9) })
            {
                File.WriteAllBytes(source, Fbx(7400, weights, count));
                var hash = SkinnedMeshCookService.Hash(source);
                Check(Reject(() => SkinnedFbxValidator.Inspect(source)) && hash == SkinnedMeshCookService.Hash(source),
                    $"FBX preflight rejects {name} weights without automatic repair");
            }
            recipe.CacheRelativePath = "cache"; recipe.SourceRelativePath = "fixture.fbx";
            recipe.SourceSha256 = SkinnedMeshCookService.Hash(source);
            var cache = Path.Combine(root, "cache"); Directory.CreateDirectory(cache);
            var files = new Dictionary<string, string>();
            foreach (var file in new[] { "mesh.uasset", "mesh.uexp" })
            { File.WriteAllBytes(Path.Combine(cache, file), [1, 2, 3]); files[file] = SkinnedMeshCookService.Hash(Path.Combine(cache, file)); }
            var manifest = new SkinnedMeshCookService.CookManifest(recipe.SourceSha256, recipe.DonorMeshPackage, recipe.SkeletonPackage,
                recipe.MeshPackage, "/Game/Mods/Fixture/Skinned/SK_Custom_Skeleton", ["Body"], files);
            File.WriteAllText(Path.Combine(cache, "validated.json"), JsonSerializer.Serialize(manifest));
            Check(SkinnedMeshStageService.ReadManifest(root, recipe).Files.Count == 2, "skinned validated cache survives a saved recipe roundtrip");
            File.WriteAllBytes(Path.Combine(cache, "mesh.uexp"), [4]);
            Check(Reject(() => SkinnedMeshStageService.ReadManifest(root, recipe)), "changed skeletal render buffers block packaging until reimport");
        }
        finally { Directory.Delete(root, recursive: true); }
        return results;
    }

    private sealed record Node(string Name, object[] Values, params Node[] Children);
    private static byte[] Fbx(int version, double[] weights, int influences)
    {
        var objects = new List<Node> { new("Geometry", [1L, "Mesh", "Mesh"], new Node("Vertices", [new[]{0d,0,0,1,0,0,0,1,0}])), new("Deformer", [2L, "Skin", "Skin"]) };
        var edges = new List<Node> { new("C", ["OO", 2L, 1L]) };
        for (int i = 0; i < influences; i++)
        {
            long bone = 10 + i, cluster = 100 + i;
            objects.Add(new("Model", [bone, "Bone" + i, "LimbNode"]));
            objects.Add(new("Deformer", [cluster, "Cluster", "Cluster"], new("Indexes", [new[]{0,1,2}]), new("Weights", [weights])));
            edges.Add(new("C", ["OO", bone, cluster])); edges.Add(new("C", ["OO", cluster, 2L]));
        }
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0")); writer.Write(version);
        Write(new("Objects", [], objects.ToArray())); Write(new("Connections", [], edges.ToArray()));
        writer.Write(new byte[version >= 7500 ? 25 : 13]); return stream.ToArray();
        void Size(long value) { if (version >= 7500) writer.Write((ulong)value); else writer.Write((uint)value); }
        void Write(Node node)
        {
            long start = stream.Position; Size(0); Size(node.Values.Length); Size(0);
            var name = Encoding.UTF8.GetBytes(node.Name); writer.Write((byte)name.Length); writer.Write(name);
            long properties = stream.Position;
            foreach (var value in node.Values)
                switch (value)
                {
                    case long l: writer.Write((byte)'L'); writer.Write(l); break;
                    case string s: var bytes = Encoding.UTF8.GetBytes(s); writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes); break;
                    case double[] d: writer.Write((byte)'d'); writer.Write(d.Length); writer.Write(0); writer.Write(d.Length * 8); foreach (var n in d) writer.Write(n); break;
                    case int[] a: writer.Write((byte)'i'); writer.Write(a.Length); writer.Write(0); writer.Write(a.Length * 4); foreach (var n in a) writer.Write(n); break;
                }
            long length = stream.Position - properties;
            foreach (var child in node.Children) Write(child);
            writer.Write(new byte[version >= 7500 ? 25 : 13]); long end = stream.Position;
            stream.Position = start; Size(end); Size(node.Values.Length); Size(length); stream.Position = end;
        }
    }
}
