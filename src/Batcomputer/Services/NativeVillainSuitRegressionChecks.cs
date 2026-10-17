using System.Security.Cryptography;
using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Opt-in, disposable native-asset matrix. Never writes to the extraction or installation.</summary>
internal static class NativeVillainSuitRegressionChecks
{
    internal static void Run(string workspace, TextWriter output)
    {
        Directory.CreateDirectory(workspace);
        var content = AppSettings.Current.EffectiveExtractedContentRoot();
        var maps = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        var mutation = new AbilityAssetMutationService();
        var hashes = new Dictionary<string, string>();
        string Source(string package) => ExtractedPackagePathService.ResolvePackageUasset(content, package)
            ?? throw new InvalidDataException("Missing native matrix source: " + package);
        void Protect(string package)
        {
            foreach (var file in new[] { Source(package), Path.ChangeExtension(Source(package), ".uexp") })
                if (File.Exists(file)) hashes.TryAdd(file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }
        static void Require(bool condition, string detail) { if (!condition) throw new InvalidDataException(detail); }
        var count = 0;
        foreach (var family in new[] { "Joker", "HarleyQuinn" })
        foreach (var path in Directory.GetFiles(Path.GetDirectoryName(Source(NativeVillainTraversalService.VillainRoot +
                     family + "/BP_" + family + "_Default_Playable"))!, "BP_*_Playable.uasset").Order())
        {
            var package = ExtractedPackagePathService.PackagePathFromFile(content, path)!;
            var donor = AnimArchetypeGraftService.DetectDonor(path, content, maps)!;
            Require(donor.Family == family && !string.IsNullOrWhiteSpace(donor.AbilitySetPackage), "Animation variant changed gameplay identity: " + package);
            foreach (var source in new[] { package, donor.ArchetypePackage, donor.MasCharPackage, donor.LasCharPackage, donor.DprdPackage }) Protect(source);
            // Deliberately begin with the donor family: repeated substring reparenting
            // previously expanded these class names into missing generated parents.
            var mod = family + "Variant" + count++;
            var metadataPackage = package[..package.LastIndexOf('/')] + "/DA_DCMD_" + Path.GetFileNameWithoutExtension(path)[3..];
            // Harley's unsuffixed base Blueprint is an inherited wrapper, not a
            // roster costume. Its roster metadata is the native Default entry.
            if (Path.GetFileNameWithoutExtension(path) == "BP_HarleyQuinn_Playable")
                metadataPackage = NativeVillainTraversalService.VillainRoot + "HarleyQuinn/DA_DCMD_HarleyQuinn_Default_Playable";
            Protect(metadataPackage);
            var p = new NativeSuitProject { SlotId = mod, DisplayName = mod, PawnTag = "Pawns.Playable." + family + "." + mod,
                PlayableTemplate = new() { PackagePath = package, Stem = Path.GetFileNameWithoutExtension(path), Uasset = path },
                DcmdTemplate = new() { PackagePath = metadataPackage, Stem = UnrealPathUtil.AssetName(metadataPackage), Uasset = Source(metadataPackage) },
                TargetPackages = new() { Playable = $"/Game/Mods/{mod}/Characters/BP_{mod}_Playable", Dcmd = $"/Game/Mods/{mod}/Characters/DA_DCMD_{mod}_Playable" } };
            Require(!p.UseCustomArchetype && AnimArchetypeGraftService.RequiresCustomArchetype(p), "Unedited ordinary native villain skipped its concrete behavior bridge");
            var patch = new UAssetPatchService(workspace).CreateNameMapPatchedStage(p);
            Require(patch.Status == "created", "Could not stage " + package);
            var result = new AnimArchetypeGraftService().ApplyToPackagedRoot(p, patch.PatchedContentRoot);
            Require(result.Status == "ok", result.Error ?? string.Join("; ", result.Log));
            var staged = patch.PatchedContentRoot;
            string FileFor(string pkg) => ExtractedPackagePathService.ResolvePackageUasset(staged, pkg)!;
            var child = AnimArchetypeGraftService.ReadBehaviorDefaults(FileFor(p.TargetPackages.Playable), maps);
            var parentPackage = UAssetPatchService.CustomArchetypePackage(p)!;
            var parent = AnimArchetypeGraftService.ReadBehaviorDefaults(FileFor(parentPackage), maps);
            string Effective(string key) => child.GetValueOrDefault(key) ?? parent.GetValueOrDefault(key) ?? "";
            Require(Effective("AnimSet") == donor.MasCharPackage && Effective("LayerSet") == donor.LasCharPackage &&
                    Effective("AudioTagData") == donor.AudioTagDataPackage, "Ordinary suit lost its costume animation/audio defaults: " + package);
            Require(NativeVoiceService.ReadVoice(new UAsset(FileFor(p.TargetPackages.Playable), EngineVersion.VER_UE5_6, maps)) ==
                    NativeVoiceService.ReadVoice(new UAsset(path, EngineVersion.VER_UE5_6, maps)), "Ordinary suit lost native dialogue voice");
            var dprd = Effective("DinnerPawnRuntimeData");
            Require(dprd == donor.DprdPackage, "Ordinary suit replaced its native upgrade-gated loadout");
            var original = mutation.InspectDprdAbilitySets(Source(donor.DprdPackage)).AbilitySets.Select(s => s.PackagePath).ToArray();
            var actual = mutation.InspectDprdAbilitySets(Source(dprd)).AbilitySets.Select(s => s.PackagePath).ToArray();
            Require(actual.SequenceEqual(original) && !File.Exists(Path.Combine(staged, "Mods", mod, "Characters", $"DA_DPRD_{mod}.uasset")),
                "Native ability sets changed or unconditional traversal was packaged: " + package);
            Require(mutation.InspectDprdEquipment(Source(donor.DprdPackage)).Equipment.Select(e => e.PackagePath)
                    .SequenceEqual(mutation.InspectDprdEquipment(Source(dprd)).Equipment.Select(e => e.PackagePath)), "Native equipment changed");
            string[] MetadataDependencies(string file) => CharacterRegistryBundleService.CreateCharacterRow(metadataPackage,
                    new UAsset(file, EngineVersion.VER_UE5_6, maps)).EffectiveBundles
                .Where(b => b.Name == RegistryPluginService.MetadataBundle).SelectMany(b => b.Assets).ToArray();
            Require(MetadataDependencies(Source(metadataPackage)).SequenceEqual(MetadataDependencies(FileFor(p.TargetPackages.Dcmd))),
                "Native equipment/upgrade metadata changed: " + package);
            Require(!new StageValidationService(staged, AppSettings.Current.EffectiveUsmapPath(), workspace).Validate(p)
                .Any(f => f.Severity == "ERROR"), "Ordinary variant failed final validation: " + package);

            var graphLog = new AnimArchetypeGraftService.Result();
            foreach (var (source, kind, certified) in new[] {
                (donor.MasCharPackage, "MAS", "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Batman"),
                (donor.LasCharPackage, "LAS", "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Batman") })
            {
                var target = $"/Game/Mods/{mod}/Characters/{kind}_Cape_{mod}";
                AnimArchetypeGraftService.CloneDonorAsset(content, source, UnrealPathUtil.AssetName(source), staged,
                    target, UnrealPathUtil.AssetName(target), maps, graphLog);
                var prefix = kind == "MAS" ? "MAS_Glide_" : "LAS_Traversal_";
                var preferred = prefix + family;
                var route = PairedCapeAnimationGraphService.NativeRoute(source, prefix, preferred, kind == "LAS");
                foreach (var owner in route.Owners) Protect(owner);
                if (route.Category.Length > 0) Protect(route.Category);
                PairedCapeAnimationGraphService.Apply(source, target, staged, certified, prefix, preferred, kind == "LAS", maps, graphLog);
                PairedCapeAnimationGraphService.Verify(source, target, staged, certified, prefix, preferred, kind == "LAS");
            }
            output.WriteLine("PASS ordinary + cape animation graph: " + Path.GetFileNameWithoutExtension(path) + "; native abilities/upgrades/equipment/voice retained");

            if (Path.GetFileNameWithoutExtension(path) == $"BP_{family}_Default_Playable")
            {
                p.AbilityLoadout = new() { DonorDprdPackage = donor.DprdPackage,
                    DonorAbilitySetFingerprint = AbilityLoadoutService.Fingerprint(original), DonorAbilitySetPackages = original.ToList(),
                    AbilitySets = original.Select((s, i) => new AbilitySetSelection { PackagePath = s, Order = i }).ToList() };
                p.AbilityLoadout.AbilitySets.Add(new() { PackagePath = NativeVillainTraversalService.GrapplingSet, Enabled = false, Order = 100 });
                var own = p.AbilityLoadout.AbilitySets.Single(s => s.PackagePath == donor.AbilitySetPackage);
                var removed = mutation.InspectAbilitySet(Source(own.PackagePath)).GameplayAbilities.First().PackagePath;
                own.RemovedGameplayAbilities.Add(removed);
                result = new AnimArchetypeGraftService().ApplyToPackagedRoot(p, staged);
                Require(result.Status == "ok", result.Error ?? "Customized native loadout failed");
                var sets = mutation.InspectDprdAbilitySets(FileFor($"/Game/Mods/{mod}/Characters/DA_DPRD_{mod}")).AbilitySets.Select(s => s.PackagePath).ToArray();
                Require(!sets.Contains(NativeVillainTraversalService.GrapplingSet) && !sets.Contains(GliderService.GlidingAbilitySetPackage), "Unrequested traversal grants were packaged");
                Require(!sets.SelectMany(s => mutation.InspectAbilitySet(s.StartsWith("/Game/Mods/") ? FileFor(s) : Source(s)).GameplayAbilities)
                    .Any(a => a.PackagePath == removed), "Explicit native ability removal was restored");
                Require(!new StageValidationService(staged, AppSettings.Current.EffectiveUsmapPath(), workspace).Validate(p).Any(f => f.Severity == "ERROR"), "Customized variant failed final validation");
                output.WriteLine("PASS " + family + ": saved native ability removal and disabled grapple remain authoritative on rebuild");
            }
        }
        Require(hashes.All(h => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(h.Key))) == h.Value), "A native matrix source was modified");
        output.WriteLine($"PASS {count} ordinary variants + {count} paired-cape animation graphs; {hashes.Count} native source files unchanged");
    }
}
