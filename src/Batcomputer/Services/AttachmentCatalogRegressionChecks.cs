namespace Batcomputer;

internal static class AttachmentCatalogRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        const string root = "/Game/Characters/Attachments/";
        static GameDataAsset Asset(string name, string kind = "StaticMesh") =>
            new() { Path = root + name, Class = kind };
        static NativeSuitPartRecord Usage(string name, string donor, string context, string slot,
            string kind = "StaticMesh", bool inferred = false) => new()
            {
                MeshPackagePath = root + name, MeshObjectName = name.Split('/').Last(),
                MeshObjectPath = root + name + "." + name.Split('/').Last(),
                MeshKind = kind, ComponentClass = kind == "StaticMesh" ? "StaticMeshComponent" : "SkeletalMeshComponentBudgeted",
                SourcePackagePath = donor, Context = context, Slot = slot,
                IsKnownVisualSlot = true, ParentComponentOrVariableName = "CharacterMesh0",
                IsSynthesized = inferred
            };
        var assets = new[]
        {
            Asset("HAIR/SM_HAIR_Static"), Asset("HAIR/SK_HAIR_Skeletal", "SkeletalMesh"),
            Asset("HAIR/SM_HAIR_Chest"), Asset("Torso/SK_TorsoPiece", "SkeletalMesh"),
            Asset("Hip/SM_HipPiece"), Asset("Torso/SK_TorsoA_Satchel", "SkeletalMesh"),
            Asset("Cape/SK_Cape", "SkeletalMesh"), Asset("Glider/SK_Glider", "SkeletalMesh"),
            Asset("Misc/LArmPatch/LArm_Patch"), Asset("HAT/SM_HAT_Inferred")
        };
        var usages = new[]
        {
            Usage("HAIR/SM_HAIR_Static", "CivilianPlayable", "playable", "Head"),
            Usage("HAIR/SK_HAIR_Skeletal", "CatwomanPlayable", "playable", "Hair", "SkeletalMesh"),
            Usage("HAIR/SM_HAIR_Chest", "ChestHairPlayable", "playable", "Torso2"),
            Usage("HAIR/SM_HAIR_Chest", "ChestHairCutscene", "cutscene", "Torso2"),
            Usage("HAIR/SM_HAIR_Chest", "ChestHairWrongSlot", "cutscene", "Head"),
            Usage("Torso/SK_TorsoPiece", "TorsoDonor", "playable", "Torso2", "SkeletalMesh"),
            Usage("Hip/SM_HipPiece", "HipDonor", "playable", "Hip"),
            Usage("Torso/SK_TorsoA_Satchel", "Arkham", "playable", "Costume", "SkeletalMesh"),
            Usage("Torso/SK_TorsoA_Satchel", "TwoFace", "playable", "Cape", "SkeletalMesh"),
            Usage("Cape/SK_Cape", "CapeDonor", "playable", "Cape", "SkeletalMesh"),
            Usage("Glider/SK_Glider", "GliderDonor", "playable", "Cape", "SkeletalMesh")
        };
        var entries = AttachmentAssetCatalogService.Build(assets, usages,
            [Usage("HAT/SM_HAT_Inferred", "", "playable", "Head", inferred: true)]);
        var byName = entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        Check(entries.Count == assets.Length &&
              byName["SM_HAIR_Chest"].NativeRecipes.Count == 3 &&
              byName["SM_HAIR_Chest"].NativeRecipes.Select(p => p.SourcePackagePath)
                  .SequenceEqual(["ChestHairCutscene", "ChestHairWrongSlot", "ChestHairPlayable"]) &&
              byName["LArm_Patch"].Status == "preview only",
            "attachment catalog groups by mesh, retains all donor usages, and includes non-prefixed preview-only meshes");
        Check(byName["SM_HAIR_Chest"].AttachmentPoints.SequenceEqual(["Head", "Torso2"]) &&
              byName["LArm_Patch"].AttachmentPoints.SequenceEqual(["Unverified"]) &&
              AttachmentAssetCatalogService.UsesFaceWorkflow(root + "FaceTex/SK_FaceTex") &&
              AttachmentAssetCatalogService.UsesFaceWorkflow(root + "LEGOface/SK_LEGOface") &&
              !AttachmentAssetCatalogService.UsesFaceWorkflow(root + "Misc/LArmPatch/LArm_Patch"),
            "attachment-point filter uses observed slots and leaves unknown mounts unverified; face meshes stay in Faces");
        var bpOnlyPart = new NativeSuitPartRecord
        {
            MeshPackagePath = "/Game/Characters/Bosses/MrFreeze/SM_MrFreezeMechHockeyStick",
            MeshObjectName = "SM_MrFreezeMechHockeyStick",
            MeshKind = "StaticMesh", ComponentClass = "StaticMeshComponent",
            Slot = "StaticMesh", SourcePackagePath = "/Game/Characters/Bosses/MrFreeze/BP_MrFreeze",
            Context = "playable", IsKnownVisualSlot = false
        };
        var secondBpUsage = PartRecipeService.Clone(bpOnlyPart);
        secondBpUsage.Slot = "Torso2";
        var bpCatalog = AttachmentAssetCatalogService.Build(assets,
            usages.Append(bpOnlyPart).Append(secondBpUsage), []);
        Check(bpCatalog.Count == assets.Length + 1 &&
              bpCatalog.Single(e => e.Name == "SM_MrFreezeMechHockeyStick") is { NativeRecipes.Count: 2 } bpEntry &&
              bpEntry.AttachmentPoints.SequenceEqual(["StaticMesh", "Torso2"]) &&
              AttachmentAssetCatalogService.IsCatalogPartUsage(bpOnlyPart) &&
              !AttachmentAssetCatalogService.IsCatalogPartUsage(new NativeSuitPartRecord
              {
                  MeshPackagePath = "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig",
                  MeshObjectName = "SK_LEGOfig_Minifig", Slot = "Mesh (CharacterMesh0)"
              }),
            "catalog includes character Blueprint meshes outside the attachment library without treating root bodies as graftable parts");
        var rootBody = new NativeSuitPartRecord
        {
            MeshPackagePath = "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig",
            MeshObjectName = "SK_LEGOfig_Minifig", MeshKind = "SkeletalMesh",
            ComponentClass = "SkeletalMeshComponentBudgeted", Slot = "Mesh (CharacterMesh0)",
            SourcePackagePath = "BatmanPlayable", Context = "playable", IsKnownVisualSlot = true
        };
        var face = Usage("FaceTex/SK_FaceTex", "BatmanCutscene", "cutscene", "FaceTex", "SkeletalMesh");
        var fullCatalog = AttachmentAssetCatalogService.Build(assets,
            usages.Append(bpOnlyPart).Append(secondBpUsage).Append(rootBody).Append(face), []);
        var browse = AttachmentAssetCatalogService.BrowseItems(fullCatalog);
        Check(browse.Count == usages.Length + 4 + 2 &&
              browse.Count(item => item.Asset.Name == "SM_HAIR_Chest") == 3 &&
              browse.Count(item => item.Asset.Name == "SM_MrFreezeMechHockeyStick") == 2 &&
              browse.Single(item => ReferenceEquals(item.Recipe, secondBpUsage)).AttachmentPoints.SequenceEqual(["Torso2"]) &&
              browse.Single(item => ReferenceEquals(item.Recipe, rootBody)) is { } rootItem &&
              !AttachmentAssetCatalogService.CanOneClickGraft(rootItem.Asset, rootBody) &&
              browse.Single(item => ReferenceEquals(item.Recipe, face)) is { } faceItem &&
              !AttachmentAssetCatalogService.CanOneClickGraft(faceItem.Asset, face),
            "All parts exposes ordinary Blueprint usages separately, while face and root usages remain inspect-only");
        var sharedFace = Enumerable.Range(0, AttachmentAssetCatalogService.FrequentUsageThreshold + 1)
            .Select(i =>
            {
                var usage = PartRecipeService.Clone(face);
                usage.SourcePackagePath = $"FaceDonor{i}";
                usage.Slot = i == 0 ? "Face" : "FaceTex";
                return usage;
            }).ToArray();
        var sharedCatalog = AttachmentAssetCatalogService.Build(assets, sharedFace, []);
        var sharedTiles = AttachmentAssetCatalogService.BrowseItems(sharedCatalog)
            .Where(item => item.Asset.Name == "SK_FaceTex").ToArray();
        Check(sharedTiles.Length == 2 &&
              sharedTiles.Sum(item => item.Recipes.Count) == sharedFace.Length &&
              sharedTiles.Single(item => item.AttachmentPoints.SequenceEqual(["FaceTex"]))
                  .GroupedRecipes?.Count == AttachmentAssetCatalogService.FrequentUsageThreshold &&
              sharedTiles.Single(item => item.AttachmentPoints.SequenceEqual(["Face"]))
                  .Recipe is not null,
            "frequently reused face meshes collapse by attachment point without losing or merging donor recipes");
        Check(byName["SM_HAT_Inferred"].Status == "inferred" &&
              !AttachmentAssetCatalogService.CanOneClickGraft(byName["SM_HAT_Inferred"],
                  byName["SM_HAT_Inferred"].InferredRecipes[0]) &&
              AttachmentAssetCatalogService.CanOneClickGraft(byName["SM_HAIR_Static"],
                  byName["SM_HAIR_Static"].NativeRecipes[0]) &&
              AttachmentAssetCatalogService.CanOneClickGraft(byName["SK_HAIR_Skeletal"],
                  byName["SK_HAIR_Skeletal"].NativeRecipes[0]),
            "inferred hair and hats are inspect-only while explicit static and skeletal recipes remain selectable");
        var satchel = byName["SK_TorsoA_Satchel"];
        Check(AttachmentAssetCatalogService.UniqueCompatibleCounterpart(byName["SM_HAIR_Chest"],
                  byName["SM_HAIR_Chest"].NativeRecipes.Single(p => p.Context == "playable"))?.SourcePackagePath == "ChestHairCutscene" &&
              AttachmentAssetCatalogService.UniqueCompatibleCounterpart(byName["SM_HAIR_Static"],
                  byName["SM_HAIR_Static"].NativeRecipes[0]) is not null &&
              AttachmentAssetCatalogService.UniqueCompatibleCounterpart(byName["SK_HAIR_Skeletal"],
                  byName["SK_HAIR_Skeletal"].NativeRecipes[0]) is null &&
              AttachmentAssetCatalogService.UniqueCompatibleCounterpart(satchel,
                  satchel.NativeRecipes.Single(p => p.Slot == "Costume")) is null,
            "counterpart matching does not turn chest hair into head hair or fall back for skeletal pieces");
        Check(satchel.NativeRecipes.Count == 2 &&
              !AttachmentAssetCatalogService.CanOneClickGraft(satchel, satchel.NativeRecipes.Single(p => p.Slot == "Cape")) &&
              !AttachmentAssetCatalogService.CanOneClickGraft(byName["SK_Cape"], byName["SK_Cape"].NativeRecipes[0]) &&
              !AttachmentAssetCatalogService.CanOneClickGraft(byName["SK_Glider"], byName["SK_Glider"].NativeRecipes[0]),
            "misleading Satchel, cape, and glider usages stay inspectable without bypassing dedicated graft flows");

        void Check(bool ok, string label)
        {
            output.WriteLine((ok ? "PASS" : "FAIL") + "  " + label);
            if (!ok) failures.Add(label);
        }
    }
}
