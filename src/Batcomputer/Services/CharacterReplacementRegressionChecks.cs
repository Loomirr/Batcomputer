namespace Batcomputer;

internal static class CharacterReplacementRegressionChecks
{
    internal static void CreateGameplayFixture(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("Choose a new fixture directory.");
        var content = AppSettings.Current.EffectiveExtractedContentRoot();
        var maps = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        var targets = new[] {
            "Minifig/Alfred/BP_Alfred_Default_Playable", "Minifig/Alfred/BP_Alfred_Default_Quest",
            "Minifig/Alfred/BP_Alfred_Default_Cutscene", "Minifig/Kiteman/BP_Kiteman_Quest",
            "Minifig/Kiteman/BP_Kiteman_Default_Cutscene", "Smallfig/Batmite/BP_Batmite_Quest" };
        var service = new SuitProjectService(root);
        var mod = new NativeSuitModProject { ModId = "ReplacerGameplayTest", DisplayName = "Replacer gameplay test" };
        foreach (var relative in targets)
        {
            var target = "/Game/Characters/" + relative;
            var id = "test_" + UnrealPathUtil.AssetName(target);
            var project = CharacterReplacerProjectService.Create(target, "TEST " + UnrealPathUtil.AssetName(target), content, id);
            var asset = new UAssetAPI.UAsset(Path.Combine(content, target[6..] + ".uasset"), UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_6, maps,
                UAssetAPI.CustomSerializationFlags.SkipPreloadDependencyLoading);
            var characterFolder = target[..target.LastIndexOf('/')];
            var material = relative.Contains("Batmite")
                ? "/Game/Characters/SmallFig/BatMite/Materials/MI_BatMiteVariant2_EoM"
                : "/Game/Characters/Minifig/Gordon/Materials/MI_JimGordon_Skeleton" + (relative.EndsWith("Cutscene") ? "_CUT" : "");
            if (!File.Exists(Path.Combine(content, material[6..] + ".uasset"))) throw new FileNotFoundException(material);
            foreach (var export in asset.Exports.OfType<UAssetAPI.ExportTypes.NormalExport>())
            {
                var materials = export.Data.OfType<UAssetAPI.PropertyTypes.Objects.ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString() == "OverrideMaterials");
                if (materials is null) continue;
                for (var slot = 0; slot < materials.Value.Length; slot++)
                {
                    if (materials.Value[slot] is not UAssetAPI.PropertyTypes.Objects.ObjectPropertyData value || !value.Value.IsImport()) continue;
                    var original = SwordCombatService.Package(asset, value.Value);
                    if (!original.StartsWith(characterFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    project.MaterialAssignments.Add(new() { Component = export.ObjectName.ToString().Replace("_GEN_VARIABLE", ""),
                        Slot = slot, Context = "both", MiPackagePath = material });
                }
            }
            if (project.MaterialAssignments.Count == 0) throw new InvalidDataException("No direct outfit slots found: " + target);
            var path = service.SaveProject(project);
            mod.Suits.Add(new() { SuitId = id, SuitProjectPath = Path.GetRelativePath(root, path) });
            Console.WriteLine(path + " | " + project.MaterialAssignments.Count + " outfit slots -> " + material);
        }
        Console.WriteLine(new ModProjectService(root).SaveMod(mod));
    }

    internal static void CreateFlowFixture(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("Choose a new fixture directory.");
        Directory.CreateDirectory(root);
        var p = CharacterReplacerProjectService.Create("/Game/Characters/Smallfig/Batmite/BP_Batmite_Quest", "Replacer flow acceptance",
            AppSettings.Current.EffectiveExtractedContentRoot(), "replacer_flow_acceptance");
        p.MaterialAssignments.Add(new() { Component = "CharacterMesh0", Slot = 0, MiPackagePath = "/Game/Characters/SmallFig/BatMite/Materials/MI_BatMiteVariant2_EoM" });
        var path = new SuitProjectService(root).SaveProject(p);
        var mod = new NativeSuitModProject { ModId = "ReplacerFlowAcceptance", DisplayName = "Replacer flow acceptance",
            Suits = [new() { SuitId = p.SlotId, SuitProjectPath = Path.GetRelativePath(root, path) }] };
        Console.WriteLine(path); Console.WriteLine(new ModProjectService(root).SaveMod(mod));
    }

    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        const string replacementTarget = "/Game/Characters/Test/BP_Test_Quest";
        bool SameIdentity(string? a, string? b)
        {
            try { CharacterReplacerProjectService.VerifyPackageIdentity(a, b); return true; }
            catch (InvalidDataException) { return false; }
        }
        yield return (SameIdentity(replacementTarget, replacementTarget), "replacement preserves its native IoStore package identity");
        yield return (!SameIdentity(replacementTarget, "/Game/Mods/Test/BP_Replacement_Playable"), "replacement rejects a mod-local package identity even at a native output path");
        yield return (!SameIdentity(null, null) && !SameIdentity(replacementTarget, null), "replacement rejects missing package identities");
        NativeSuitProject Replacement() => new() { ReplacementTarget = replacementTarget,
            PlayableTemplate = new() { PackagePath = replacementTarget }, CutsceneTemplate = new() { PackagePath = replacementTarget },
            TargetPackages = new() { Playable = "/Game/Mods/Test/BP_Work_Playable", Cutscene = "/Game/Mods/Test/BP_Work_Cutscene" } };
        bool Valid(NativeSuitProject project) { try { CharacterReplacerProjectService.Validate(project); return true; } catch (InvalidDataException) { return false; } }
        yield return (Valid(Replacement()) && BaseEligibilityService.Evaluate(Replacement()).IsReady, "replacement uses its original quest base without a playable machinery donor");
        var gameplay = Replacement(); gameplay.UseCustomArchetype = true;
        yield return (!Valid(gameplay), "replacement build rejects custom gameplay archetypes");
        var voice = Replacement(); voice.VoiceProfileId = "fixture";
        yield return (!Valid(voice), "replacement build rejects voice edits");
        var wrongBase = Replacement(); wrongBase.PlayableTemplate!.PackagePath = "/Game/Characters/Batman/BP_Batman_Playable";
        yield return (!Valid(wrongBase), "replacement cannot substitute another character's gameplay shell");
        var visual = Replacement(); visual.MaterialAssignments.Add(new() { Component = "Face", MiPackagePath = "/Game/MI_Test" });
        yield return (Valid(visual), "replacement retains the existing suit material assignment model");
        yield return (CharacterReplacerProjectService.VisualCategory("Parts") && !CharacterReplacerProjectService.VisualCategory("Abilities") &&
            !CharacterReplacerProjectService.VisualCategory("Animations"), "replacer reuses visual suit categories without gameplay editors");
        bool Accepted(NpcAppearanceTestService.Recipe recipe)
        {
            try { NpcAppearanceTestService.Validate(recipe); return true; }
            catch (InvalidDataException) { return false; }
        }
        foreach (var role in new[] { "Playable", "Quest", "Cutscene", "Civilian", "Boss", "Goon" })
            yield return (Accepted(new($"/Game/Characters/Test/BP_Test_{role}", "/Game/MI_A", "/Game/MI_B")), "replacer accepts concrete " + role + " appearance target");
        foreach (var target in new[] { "/Game/Characters/BP_Master/BP_Master_Playable", "/Game/Props/BP_Chair", "/DLC_Test/Characters/BP_Test_Quest", "/Game/../Characters/BP_Test_Quest" })
            yield return (!Accepted(new(target, "/Game/MI_A", "/Game/MI_B")), "replacer rejects unsafe or unsupported target " + target);
        const string pawn = "/Game/Characters/BP_Test_Quest";
        yield return (!Accepted(new(pawn, "/Game/MI_A", "/Game/MI_A")), "replacer rejects unchanged references");
        yield return (!Accepted(new(pawn, "/Game/MI_A", "/Game/Mods/Private/MI_B")), "replacer rejects unbundled custom dependency");
        yield return (Accepted(new(pawn, "/Game/MI_A", "/Game/Mods/Test/MI_B", VisualContentRoots: ["C:/Fixture/Content"])), "replacer accepts explicitly sourced custom visuals for dependency validation");
        yield return (!Accepted(new(pawn, Changes: [new("/Game/MI_A", "/Game/MI_B"), new("/Game/MI_A", "/Game/MI_C")])), "replacer rejects duplicate original assignments");
        yield return (Accepted(new(pawn, Changes: [new("/Game/MI_A", "/Game/MI_B"), new("/Game/SK_A", "/Game/SK_B")])), "replacer preserves multiple independent appearance changes");
        yield return (NpcAppearanceTestService.ChangesFor(new(pawn, "/Game/MI_A", "/Game/MI_B")).Single() == new NpcAppearanceTestService.Change("/Game/MI_A", "/Game/MI_B"), "legacy NPC recipes remain readable");
        yield return (CharacterReplacementService.Package("LEGOBatmanLotDK/Content/Characters/BP_Test_Quest") == pawn &&
            CharacterReplacementService.Package("Game/Characters/BP_Test_Quest") == pawn, "replacer canonicalizes catalogue paths");
        yield return (!CharacterReplacementService.Supported("BlueprintGeneratedClass") && !CharacterReplacementService.Supported("AnimBlueprintGeneratedClass"), "replacer never treats behavior classes as appearance assets");
    }

    internal static string VerifyCustomAssets(string content, string mappings, string unrealPak, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || FileSystemPathUtil.IsWithinDirectory(output, content)) throw new IOException("Choose a new output outside source content.");
        var root = Path.Combine(output, "CustomContent");
        var maps = new UAssetAPI.Unversioned.Usmap(mappings);
        UAssetAPI.UAsset Read(string package) => new(Path.Combine(content, package[6..] + ".uasset"), UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_6,
            maps, UAssetAPI.CustomSerializationFlags.SkipParsingExports | UAssetAPI.CustomSerializationFlags.SkipPreloadDependencyLoading);
        const string material = "/Game/Characters/SmallFig/BatMite/Materials/MI_BatMiteVariant2_EoM";
        const string customMaterial = "/Game/Mods/ReplacementAcceptance/MI_Test";
        const string customTexture = "/Game/Mods/ReplacementAcceptance/T_Test";
        var source = Read(material);
        var texture = source.Imports.Where(i => i.ClassName.ToString() == "Texture2D" && i.OuterIndex.IsImport())
            .Select(i => i.OuterIndex.ToImport(source).ObjectName.ToString())
            .First(p => p.StartsWith("/Game/") && File.Exists(Path.Combine(content, p[6..] + ".uasset")));
        void Clone(string originalPackage, string destinationPackage, Dictionary<string, string>? extra = null)
        {
            var asset = Read(originalPackage);
            var replacements = extra ?? new();
            replacements[originalPackage] = destinationPackage;
            replacements[Path.GetFileName(originalPackage)] = Path.GetFileName(destinationPackage);
            var names = asset.GetNameMapIndexList().Select(n => n.ToString()).ToArray();
            for (var i = 0; i < names.Length; i++)
                if (replacements.TryGetValue(names[i], out var replacement)) asset.SetNameReference(i, new UAssetAPI.UnrealTypes.FString(replacement));
            var destination = Path.Combine(root, destinationPackage[6..] + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var bulk = Path.Combine(content, originalPackage[6..] + ".ubulk");
            if (File.Exists(bulk)) File.Copy(bulk, Path.ChangeExtension(destination, ".ubulk"));
            asset.Write(destination);
        }
        Clone(texture, customTexture);
        Clone(material, customMaterial, new() { [texture] = customTexture, [Path.GetFileName(texture)] = Path.GetFileName(customTexture) });
        var recipe = new NpcAppearanceTestService.Recipe("/Game/Characters/Smallfig/Batmite/BP_Batmite_Quest",
            "/Game/Characters/SmallFig/BatMite/Materials/MI_BatMite_EoM", customMaterial, VisualContentRoots: [root]);
        var recipeFile = Path.Combine(output, "recipe.json");
        File.WriteAllText(recipeFile, System.Text.Json.JsonSerializer.Serialize(recipe));
        var pak = NpcAppearanceTestService.Build(recipeFile, content, mappings, unrealPak, Path.Combine(output, "Build"));
        var stage = Path.Combine(output, "Build", "Stage", "LEGOBatmanLotDK", "Content");
        foreach (var package in new[] { recipe.Target, customMaterial, customTexture })
            if (!File.Exists(Path.Combine(stage, package[6..] + ".uasset"))) throw new InvalidDataException("Missing packaged visual closure: " + package);
        var preview = ModelPreviewService.BuildPreviewCharacter(AppSettings.Current.EffectiveGamePaksRoot(), mappings, recipe.Target,
            previewOptions: new ModelPreviewService.CharacterPreviewOptions
            {
                OutputDirectory = Path.Combine(output, "Preview"), AllowPartMover = false, IgnoreSavedLayout = true,
            }, looseContentRoots: [stage]);
        if (!Directory.EnumerateFiles(preview, "*.glb", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("The custom appearance did not produce preview meshes.");
        return pak;
    }
}
