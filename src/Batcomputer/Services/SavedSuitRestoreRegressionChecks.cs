using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

internal static class SavedSuitRestoreRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        void Check(bool value, string label) => results.Add((value, "saved-suit restore: " + label));
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var root = Path.Combine(Path.GetTempPath(), "BatcomputerRestore-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = new NativeSuitProject { SlotId = "restore_fixture", UseCustomArchetype = true,
                PlayableTemplate = new() { PackagePath = "/Game/Characters/Minifig/Batman/BP_Batman_LBM_Playable", Role = "playable" },
                CutsceneTemplate = new() { PackagePath = "/Game/Characters/Minifig/Batman/BP_Batman_LBM_Cutscene", Role = "cutscene" },
                TargetPackages = new() { Playable = "/Game/Mods/RestoreFixture/Characters/BP_Restore_Playable",
                    Cutscene = "/Game/Mods/RestoreFixture/Characters/BP_Restore_Cutscene" },
                CustomStaticMeshes = [new() { Id = "clearance", Target = "Torso", BodyClearance = 1.4f }],
                MaterialAssignments = [new() { Component = "CharacterMesh0", Slot = 0, MiPackagePath = "/Game/Materials/MI_Saved" }],
                Requirements = [new() { Kind = "remove-component", TargetComponent = "Face" }] };
            var patcher = new UAssetPatchService(root);
            var content = Path.Combine(patcher.GuiOutputRoot, project.SlotId, "PatchedNameMapStage", "LEGOBatmanLotDK", "Content");
            string FileFor(string package, string ext = ".uasset") => Path.Combine(content, package[6..] + ext);
            var identity = Path.Combine(patcher.GuiOutputRoot, project.SlotId, "PatchedNameMapStage", ".batcomputer-source-identity");
            Directory.CreateDirectory(Path.GetDirectoryName(identity)!);
            File.WriteAllText(identity, UAssetPatchService.BuildStageIdentityForTest(project));
            Check(!patcher.IsPatchedStageCurrent(project), "a matching identity marker cannot certify missing package pairs");
            foreach (var package in UAssetPatchService.RequiredPatchedPackages(project))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileFor(package))!);
                File.WriteAllText(FileFor(package), "fixture"); File.WriteAllText(FileFor(package, ".uexp"), "fixture");
            }
            Check(patcher.IsPatchedStageCurrent(project), "all declared generated package pairs satisfy the identity guard");
            var parent = UAssetPatchService.CustomArchetypePackage(project)!;
            File.Delete(FileFor(parent, ".uexp"));
            Check(!patcher.IsPatchedStageCurrent(project), "a missing generated-parent sidecar invalidates the cached stage");

            var native = AppSettings.Current.EffectiveExtractedContentRoot();
            var mappings = AppSettings.Current.EffectiveUsmapPath();
            if (!File.Exists(mappings) || new[] { project.PlayableTemplate.PackagePath, project.CutsceneTemplate.PackagePath,
                    UAssetPatchService.DonorArchetypePackage }.Any(p => !VehicleDonorService.PackageComplete(native, p)))
            {
                Check(true, "native integration not run: matching game extraction/mappings unavailable");
                return results;
            }
            var batch = patcher.CreateNameMapPatchedStage(project);
            if (batch.PackageResults.Any(p => !p.Success)) throw new InvalidDataException("Fixture name-map stage failed: " + batch.Status);
            var projectService = new SuitProjectService(root); var projectFile = projectService.SaveProject(project);
            var beforeRecipe = JsonSerializer.Serialize(project);
            var savedJson = File.ReadAllText(projectFile);
            foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene })
            {
                var fresh = new Usmap(mappings!);
                var asset = EquipmentAssetService.Read(content, package, fresh);
                Check(asset.FilePath == Path.GetFullPath(FileFor(package)), "memory-loaded " + UnrealPathUtil.AssetName(package) + " retains its source path");
                NativeBlueprintSchemaService.EnsureParents(asset);
                asset.GetParentClass(out _, out var parentClass);
                Check(fresh.Schemas.ContainsKey(parentClass!.ToString()), "fresh mappings resolve the role's actual parent schema");
            }
            // Exercise a generated cutscene ancestor too. This is an isolated
            // schema fixture, not a new authoring recipe or a grafting fallback.
            const string nativeCutsceneParent = "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter";
            const string customCutsceneParent = "/Game/Mods/RestoreFixture/Characters/BP_CustomCutsceneParent";
            var cutsceneContent = Path.Combine(root, "CutsceneParentTest", "Content");
            void RenameRaw(string sourceContent, string package, string targetPackage, string destination, string from, string to)
            {
                var asset = EquipmentAssetService.ReadRaw(sourceContent, package, new Usmap(mappings!),
                    CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading);
                var names = asset.GetNameMapIndexList();
                for (var i = 0; i < names.Count; i++)
                {
                    var renamed = names[i].ToString().Replace(from, to, StringComparison.Ordinal)
                        .Replace(UnrealPathUtil.AssetName(from), UnrealPathUtil.AssetName(to), StringComparison.Ordinal);
                    if (renamed != names[i].ToString()) asset.SetNameReference(i, new FString(renamed));
                }
                asset.FolderName = new FString(targetPackage);
                var target = Path.Combine(destination, targetPackage[6..] + ".uasset");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); asset.Write(target);
            }
            RenameRaw(native, nativeCutsceneParent, customCutsceneParent, cutsceneContent, nativeCutsceneParent, customCutsceneParent);
            RenameRaw(content, project.TargetPackages.Cutscene, project.TargetPackages.Cutscene, cutsceneContent, nativeCutsceneParent, customCutsceneParent);
            var cutscene = EquipmentAssetService.Read(cutsceneContent, project.TargetPackages.Cutscene, new Usmap(mappings!));
            NativeBlueprintSchemaService.EnsureParents(cutscene);
            Check(cutscene.Mappings!.Schemas.ContainsKey("BP_CustomCutsceneParent_C"), "memory-loaded cutscene resolves its own generated parent with fresh mappings");
            File.Delete(Path.Combine(cutsceneContent, customCutsceneParent[6..] + ".uasset"));
            Check(Reject(() => NativeBlueprintSchemaService.EnsureParents(cutscene)), "even a warmed parent schema cannot hide a missing generated parent");
            AttachmentClearanceService.Apply(content, project, _ => { });
            foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene })
            {
                var asset = EquipmentAssetService.Read(content, package, new Usmap(mappings!));
                var manager = asset.Exports.First(e => e.GetExportClassType()?.ToString() is "DinnerLocationAttachmentManagerComponent" or "TtLocationAttachmentManagerComponent");
                Check(AttachmentClearanceService.Read(asset, manager).Any(e => e.Bone == "Neck" && e.X >= 1.3999),
                    UnrealPathUtil.AssetName(package) + " clearance survives cooked serialization");
            }
            var originalPackages = new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene }
                .SelectMany(p => new[] { FileFor(p), FileFor(p, ".uexp") }).ToDictionary(p => p, File.ReadAllBytes);
            var parentBytes = File.ReadAllBytes(FileFor(parent, ".uexp"));
            File.Delete(FileFor(parent, ".uexp"));
            foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene })
            {
                var asset = EquipmentAssetService.Read(content, package, new Usmap(mappings!));
                asset.GetParentClass(out var actualParent, out _);
                if (UnrealPathUtil.NormalizePackagePath(actualParent?.ToString() ?? "") == parent)
                    Check(Reject(() => NativeBlueprintSchemaService.EnsureParents(asset)), "missing parent sidecar fails closed with fresh mappings");
                Check(NativeBlueprintSchemaService.ResolveParentFile(Path.Combine(root, "Other", "Content", package[6..] + ".uasset"), parent, content) is null,
                    "a generated parent from another stage is never borrowed");
            }
            Check(Reject(() => AttachmentClearanceService.Apply(content, project, _ => { })) &&
                originalPackages.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) &&
                File.ReadAllText(projectFile) == savedJson && JsonSerializer.Serialize(project) == beforeRecipe,
                "failed clearance leaves both original packages, saved JSON, material assignments and removals untouched");
            File.WriteAllBytes(FileFor(parent, ".uexp"), parentBytes);
        }
        catch (Exception ex) { Check(false, ex.ToString()); }
        finally
        {
            if (Directory.Exists(root) && FileSystemPathUtil.IsWithinDirectory(root, Path.GetTempPath())) Directory.Delete(root, true);
        }
        return results;
    }
}
