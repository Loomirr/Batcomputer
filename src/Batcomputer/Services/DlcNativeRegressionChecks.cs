using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Opt-in checks against owned extracted assets. Writes only to a new scratch workspace.</summary>
internal static class DlcNativeRegressionChecks
{
    internal static int Run(string outputRoot, TextWriter output)
    {
        try
        {
            outputRoot = Path.GetFullPath(outputRoot);
            if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
                throw new InvalidDataException("Choose a new, unused verification folder.");
            var content = AppSettings.Current.EffectiveExtractedContentRoot();
            var mappings = AppSettings.Current.EffectiveUsmapPath();
            if (!Directory.Exists(content) || !File.Exists(mappings))
                throw new InvalidDataException("Configure the current extraction and mappings first.");
            Directory.CreateDirectory(outputRoot);
            UAsset Read(string path) => new(path, EngineVersion.VER_UE5_6, MappingsCache.Load(mappings!), CustomSerializationFlags.SkipPreloadDependencyLoading);
            string Source(string package) => ExtractedPackagePathService.ResolvePackageUasset(content, package)
                ?? throw new FileNotFoundException(package);
            TemplateRecord Template(string package) => new() { PackagePath = package, Stem = package.Split('/').Last(), Uasset = Source(package) };
            List<NativeSuitPartRecord> Parts(string path)
            {
                using var json = JsonDocument.Parse(Read(path).SerializeJson(false));
                return PartIndexService.ExtractParts(json.RootElement, path, content);
            }
            static void Require(bool passed, string message)
            {
                if (!passed) throw new InvalidDataException(message);
            }
            foreach (var family in new[] { "Joker", "HarleyQuinn", "Batman" })
            {
                var dcmd = family == "Batman" ? "/Game/Characters/Minifig/Batman/DA_DCMD_Batman_TheBatman2025_Playable" :
                    $"/Game/AdditionalContent/VillainMode/Characters/Playables/{family}/DA_DCMD_{family}_Default_Playable";
                var donor = NativeMetadataDonorService.TryRead(Template(dcmd), null, null, out var error)
                    ?? throw new InvalidDataException(error);
                var templates = new[] { Template(donor.PlayablePackagePath), Template(donor.CutscenePackagePath), Template(donor.DcmdPackagePath) };
                var sourceHashes = templates.SelectMany(t => new[] { t.Uasset, Path.ChangeExtension(t.Uasset, ".uexp") })
                    .Where(File.Exists).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
                var faces = templates.Take(2).Select(t => Parts(t.Uasset).Single(p => p.Slot == "Face")).ToArray();
                string MaterialIdentity(NativeSuitPartRecord part) => string.Join("|", part.Materials.Select(m => m.ObjectPath));
                string? replacement = null;
                // Joker and Harley do not necessarily share a face rig. Find another costume
                // with the exact same meshes, just as the Faces menu's compatibility check does.
                foreach (var candidate in GameDataService.Instance.Db.Families.Single(f => f.Name == family).Dcmds.Order())
                {
                    var alternate = NativeMetadataDonorService.TryRead(Template(candidate), null, null, out _);
                    if (alternate is null) continue;
                    var otherFaces = new[] { alternate.PlayablePackagePath, alternate.CutscenePackagePath }
                        .Select(p => Parts(Source(p)).SingleOrDefault(part => part.Slot == "Face")).ToArray();
                    if (!Enumerable.Range(0, 2).All(i => otherFaces[i] is { } other &&
                        faces[i].MeshObjectPath == other.MeshObjectPath && other.Materials.Count > 0) ||
                        !Enumerable.Range(0, 2).Any(i => MaterialIdentity(faces[i]) != MaterialIdentity(otherFaces[i]!))) continue;
                    for (var i = 0; i < faces.Length; i++) faces[i].Materials = otherFaces[i]!.Materials;
                    replacement = alternate.PawnTag;
                    break;
                }
                // The DLC defaults may use a unique face rig (e.g. Joker89). Restoring that
                // exact rig is still useful coverage; Batman additionally tests a different print.
                Require(family != "Batman" || replacement is not null, "No compatible alternate printed-face fixture found for Batman");
                replacement ??= donor.PawnTag;
                var target = $"/Game/Mods/DlcFaceCheck{family}/";
                var project = new NativeSuitProject { SlotId = "dlc_face_" + family, DisplayName = "Face check " + family,
                    PawnTag = $"Pawns.Playable.{family}.FaceCheck", PlayableTemplate = templates[0], CutsceneTemplate = templates[1], DcmdTemplate = templates[2],
                    TargetPackages = new() { Playable = target + "BP_FaceCheck_Playable", Cutscene = target + "BP_FaceCheck_Cutscene", Dcmd = target + "DA_DCMD_FaceCheck_Playable" } };
                var patch = new UAssetPatchService(outputRoot).CreateNameMapPatchedStage(project);
                Require(patch.Status == "created", JsonSerializer.Serialize(patch));
                string Staged(string root, string package) => Path.Combine(root, package[6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset";
                var packages = new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene };
                // Ignore the intended visual replacement fields; all native face animation,
                // construction-node and reflected-field identities must survive the roundtrip.
                string Structure(string path)
                {
                    var asset = Read(path);
                    var face = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "Face_GEN_VARIABLE");
                    face.Data.RemoveAll(p => p.Name.ToString() is "SkeletalMesh" or "SkinnedAsset" or "OverrideMaterials");
                    using var doc = JsonDocument.Parse(asset.SerializeJson(false));
                    return string.Join("\n", doc.RootElement.GetProperty("Exports").EnumerateArray()
                        .Where(e => e.GetProperty("ObjectName").GetString() is { } name &&
                            (name == "Face_GEN_VARIABLE" || name.StartsWith("SCS_Node") || name == "SimpleConstructionScript_0" || name.EndsWith("_C")))
                        .Select(e =>
                        {
                            // Import/name-map growth moves payload offsets without changing the object.
                            var node = System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText())!.AsObject();
                            node.Remove("SerialOffset");
                            if (node["ObjectName"]?.GetValue<string>() == "Face_GEN_VARIABLE")
                                node.Remove("CreateBeforeSerializationDependencies"); // follows the requested mesh/material imports
                            return node.ToJsonString();
                        }));
                }
                var before = packages.Select(p => Structure(Staged(patch.PatchedContentRoot, p))).ToArray();
                var remove = new ComponentRemoveService(outputRoot).RemoveFromContentRoot(patch.PatchedContentRoot, project.SlotId,
                    packages[0], packages[1], "Face");
                Require(remove.Files.Count == 2 && remove.Files.All(f => f.Success && f.RemovedNodeReferences == 0 && f.ClearedMeshReferences > 0),
                    "Face removal did not preserve the native construction nodes: " + JsonSerializer.Serialize(remove));
                // Reapply through the same service used by the part menu, twice to catch replay drift.
                for (var replay = 0; replay < 2; replay++)
                {
                    var graft = new PartGraftService(outputRoot, new NativeSuitPartIndex { Parts = faces.ToList() })
                        .CreateSelectedPartGraftedStage(project, faces[0], faces[1], "Face", "Face", faces[0].AttachSocket);
                    Require(graft.PackageResults.Count == 2 && graft.PackageResults.All(p => p.Success), JsonSerializer.Serialize(graft));
                    for (var i = 0; i < packages.Length; i++)
                    {
                        var path = Staged(graft.GraftedContentRoot, packages[i]);
                        var after = Structure(path);
                        if (after != before[i])
                        {
                            File.WriteAllText(Path.Combine(outputRoot, family + "-" + i + "-before.json"), "[" + before[i].Replace("}\n{", "},\n{") + "]");
                            File.WriteAllText(Path.Combine(outputRoot, family + "-" + i + "-after.json"), "[" + after.Replace("}\n{", "},\n{") + "]");
                        }
                        Require(after == before[i], family + " face/animation/SCS identity changed on replacement");
                        var restored = Parts(path).Single(p => p.Slot == "Face");
                        Require(restored.MeshObjectPath == faces[i].MeshObjectPath && restored.AnimClassObjectPath == faces[i].AnimClassObjectPath,
                            family + " face mesh or animation class did not restore");
                        Require(restored.Materials.Select(m => m.ObjectPath).SequenceEqual(faces[i].Materials.Select(m => m.ObjectPath)),
                            family + " replacement face material did not survive save/reload");
                    }
                }
                Require(sourceHashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "A source donor changed");
                output.WriteLine($"PASS {family}: remove/replace/replay with {replacement}'s printed face preserves native Face, animation, SCS and class fields in playable + cutscene; source files unchanged");
                if (family == "Batman")
                {
                    string NonVoiceExports(string path)
                    {
                        // Register the same native parent schema before both snapshots; otherwise
                        // an unchanged CDO is raw before the write and decoded after it.
                        NativeBlueprintSchemaService.EnsureParents(Read(path));
                        var asset = Read(path);
                        foreach (var export in asset.Exports.OfType<NormalExport>())
                            if (export.ObjectName.ToString() == "WubDialogueVoiceActor_GEN_VARIABLE")
                                export.Data.RemoveAll(p => p.Name.ToString() == "VoiceActorName");
                        var node = System.Text.Json.Nodes.JsonNode.Parse(asset.SerializeJson(false))!;
                        foreach (var export in node["Exports"]!.AsArray())
                        {
                            export!.AsObject().Remove("SerialOffset");
                            export.AsObject().Remove("SerialSize");
                        }
                        return node["Exports"]!.ToJsonString();
                    }
                    var paths = packages.Select(p => Staged(patch.PatchedContentRoot, p)).ToArray();
                    var original = paths.Select(NonVoiceExports).ToArray();
                    File.WriteAllText(Path.Combine(outputRoot, "voice-before.json"), original[0]);
                    project.PairedCapeAdapter = new() { GameplayDonorPackage = "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Default_Playable" };
                    NativeVoiceService.RestoreAdapterVoice(project, patch.PatchedContentRoot);
                    NativeVoiceService.RestoreAdapterVoice(project, patch.PatchedContentRoot);
                    Require(NativeVoiceService.ReadVoice(Read(paths[0])) == "Nightwing", "Batman scaffold retained the wrong dialogue voice");
                    File.WriteAllText(Path.Combine(outputRoot, "voice-after.json"), NonVoiceExports(paths[0]));
                    Require(paths.Select(NonVoiceExports).SequenceEqual(original), "Voice repair changed unrelated Blueprint exports");
                    Require(sourceHashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "Voice repair changed a native source");
                    output.WriteLine("PASS Nightwing dialogue on Batman scaffold: persisted, replay-idempotent, unrelated Blueprint exports and native sources unchanged");
                }
            }
            var hats = AttachmentCatalogService.HatParts().ToArray();
            Require(hats.Any(p => p.MeshPackagePath.Contains("TaliaAlGhul_BatSuit") && !string.IsNullOrEmpty(p.AnimClassPackagePath)), "Talia BatSuit helmet or animation missing");
            foreach (var hood in new[] { "HoodSmall", "HoodFringe", "HoodFurlined" })
                Require(hats.Any(p => p.MeshPackagePath.Contains(hood)), hood + " missing from the parts catalog");
            output.WriteLine("PASS Talia BatSuit helmet (with animation), HoodSmall, HoodFringe and HoodFurlined are selectable catalog entries");
            output.WriteLine("Native DLC checks: PASS. No game installation or saves changed. Cutscene motion still needs in-game validation.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("Native DLC checks: FAIL: " + ex); return 1; }
    }
}
