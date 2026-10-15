namespace Batcomputer;

internal static class BuildSharedAssetsRegressionChecks
{
    internal static void VerifyCooked(string projectRoot, string modPath)
    {
        var service = new ModProjectService(projectRoot);
        var mod = service.LoadMod(modPath) ?? throw new InvalidDataException("Mod not found.");
        var suits = mod.Suits.Where(e => e.Enabled).Select(e => new SuitProjectService(projectRoot)
            .LoadProject(service.ResolveSuitProjectPath(e)) ?? throw new InvalidDataException("Suit not found.")).ToArray();
        var settings = AppSettings.Current;
        var extracted = settings.EffectiveExtractedContentRoot();
        var maps = settings.EffectiveUsmapPath()!;
        var folder = Path.Combine(Path.GetTempPath(), "Batcomputer-shared-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recipes = suits.SelectMany(BuildSharedAssets.Models).ToList();
            var realCount = recipes.Count;
            // Exercise multiple slots, shared material imports, different materials with equal
            // leaf names, and reversed bindings, in addition to the user's actual recipes.
            foreach (var sharedMaterial in new[] { false, true })
            {
                var fixture = new WeaponModelRecipe { ObjText = "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nusemtl body\nf 1 2 3\nusemtl trim\nf 1 3 4\n",
                    Materials = [new() { Slot = 0, SourceMaterialName = "body", StableSlotName = "body", MaterialPath = "/Game/Mods/FixtureA/MI_Body" },
                        new() { Slot = 1, SourceMaterialName = "trim", StableSlotName = "trim", MaterialPath = sharedMaterial ? "/Game/Mods/FixtureA/MI_Body" : "/Game/Mods/FixtureB/MI_Body" }] };
                recipes.Add(fixture);
                var variant = fixture.Clone();
                foreach (var slot in variant.Materials) slot.MaterialPath = slot.MaterialPath.Replace("Fixture", "Variant", StringComparison.Ordinal);
                recipes.Add(variant);
            }
            using var session = new BuildSharedAssets(extracted, maps, Console.WriteLine);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            session.PrepareModels(recipes);
            var sharedTime = clock.Elapsed;
            var unsharedTime = TimeSpan.Zero;
            var index = 0;
            foreach (var recipe in recipes)
            {
                var package = "/Game/Mods/SharingCheck/SM_" + index++;
                var baseline = Path.Combine(folder, "baseline");
                var shared = Path.Combine(folder, "shared");
                clock.Restart();
                WeaponModelService.BakeUnshared(recipe, extracted, maps, baseline, package);
                unsharedTime += clock.Elapsed;
                clock.Restart();
                WeaponModelService.Bake(recipe, extracted, maps, shared, package);
                sharedTime += clock.Elapsed;
                foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
                {
                    var relative = package[6..].Replace('/', Path.DirectorySeparatorChar) + ext;
                    var a = Path.Combine(baseline, relative); var b = Path.Combine(shared, relative);
                    if (File.Exists(a) != File.Exists(b) || (File.Exists(a) && !File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b))))
                        throw new InvalidDataException("Shared and independent cooked outputs differ: " + relative);
                }
            }
            Console.WriteLine($"Shared cook byte-equivalence: PASS ({realCount} actual mod mesh recipes, {index - realCount} multi-slot fixtures).");
            Console.WriteLine($"Mesh-only timing: independent={unsharedTime.TotalMilliseconds:F0}ms shared-including-preparation={sharedTime.TotalMilliseconds:F0}ms. Full mod timing must be measured separately.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        string[] mediaPaths = [
            "LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Media/Hello.wem",
            "LEGOBatmanLotDK/Plugins/Wub_Loc_enUS/Content/Wub/Platforms/Windows/Media/Hello.wem",
            "LEGOBatmanLotDK/Plugins/Wub_Loc_frFR/Content/Wub/Platforms/Windows/Media/Hello.wem",
            "LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Media/NotHello.wem",
            "LEGOBatmanLotDK/Content/Textures/Hello.uasset"
        ];
        var mediaIndex = VoiceMediaIndex.Create(mediaPaths, 1024 * 1024)!;
        foreach (var name in new[] { "Hello", "HELLO", "NotHello", "Missing", "Media/Hello" })
            yield return (mediaIndex.Find(name).Order().SequenceEqual(mediaPaths.Where(p => p.EndsWith('/' + name + ".wem", StringComparison.OrdinalIgnoreCase)).Order()),
                "indexed native voice lookup preserves exact suffix matching: " + name);
        var privateA = "BCV_" + new string('a', 64);
        var privateB = "BCV_" + new string('b', 64);
        yield return (CharacterVoiceBuildService.LocalizedMediaMounts(mediaPaths, "Hello", privateA).SequenceEqual(
            CharacterVoiceBuildService.LocalizedMediaMounts(mediaIndex.Find("Hello"), "Hello", privateA)) &&
            CharacterVoiceBuildService.LocalizedMediaMounts(mediaIndex.Find("Hello"), "Hello", privateB).All(p => p.EndsWith(privateB + ".wem")),
            "indexed native voice routes retain every language and each suit's private recording name");
        var editedPaths = mediaIndex.Find("Hello"); editedPaths[0] = "edited";
        yield return (mediaIndex.Find("Hello")[0] == mediaPaths[0] && VoiceMediaIndex.Create(mediaPaths, 1) is null,
            "voice media index returns independent arrays and respects the build-local memory allowance");
        yield return (!new AppSettings().EnableSuitPreparationCache, "persistent preparation cache remains off by default");
        using (var scope = new BuildSharedAssets("test", "test", _ => { }))
        {
            var scans = 0;
            IEnumerable<string> MediaInventory() { scans++; foreach (var path in mediaPaths) yield return path; }
            var indexedA = scope.VoiceMediaFiles("Hello", MediaInventory());
            var indexedB = scope.VoiceMediaFiles("NotHello", MediaInventory());
            yield return (scans == 1 && indexedA.Length == 3 && indexedB.Length == 1,
                "build-local voice media inventory is enumerated once across different suit lookups");
            yield return (scope.VoiceExtractionDirectory("/Game/A") != scope.VoiceExtractionDirectory("/Game/B") &&
                FileSystemPathUtil.IsWithinDirectory(scope.VoiceExtractionDirectory("/Game/A"), Path.GetTempPath()),
                "parallel voice extraction uses isolated temporary roots outside long suit preparation paths");
            var decodes = 0;
            Newtonsoft.Json.Linq.JArray Decode()
            {
                decodes++;
                return Newtonsoft.Json.Linq.JArray.Parse("[{\"nested\":{\"speaker\":\"native\",\"media\":\"original\"}}]");
            }
            var export = scope.VoiceExports("/Game/Voice/Event", Decode);
            export[0]["nested"]!["speaker"] = "suit one";
            var second = scope.VoiceExports("/Game/Voice/Event", Decode);
            second[0]["nested"]!["media"] = "suit two";
            var third = scope.VoiceExports("/Game/Voice/Event", Decode);
            yield return (decodes == 1 && third[0]["nested"]!["speaker"]!.ToString() == "native" &&
                third[0]["nested"]!["media"]!.ToString() == "original", "shared voice exports deep-clone nested speaker/media data for every suit");
            scope.VoiceExports("/Game/Mods/Private/Event", Decode);
            scope.VoiceExports("/Game/Mods/Private/Event", Decode);
            yield return (decodes == 3, "private voice exports bypass native memoization");
            scans = 0;
            CharacterVoiceCatalogService.Snapshot Inspect()
            {
                scans++;
                return new("/Game/Characters/Donor", "Voice", [new("line", "category", "event", "sequence", "Voice", "media", true)], []);
            }
            var first = scope.VoiceCatalog("/Game/Characters/Donor", Inspect);
            first.Lines[0] = first.Lines[0] with { Media = "modified by caller" };
            var next = scope.VoiceCatalog("/Game/Characters/Donor", Inspect);
            yield return (scans == 1 && next.Lines[0].Media == "media", "build-local voice discovery reuses immutable donor facts without sharing caller-mutable arrays");
            scope.VoiceCatalog("/Game/Mods/Local/Donor", Inspect);
            scope.VoiceCatalog("/Game/Mods/Local/Donor", Inspect);
            yield return (scans == 3, "private suit voice donors are never memoized as shipped sources");
            var incomplete = 0;
            CharacterVoiceCatalogService.Snapshot Warn() { incomplete++; return new("Donor", "", [], ["incomplete"]); }
            scope.VoiceCatalog("/Game/Characters/Incomplete", Warn);
            scope.VoiceCatalog("/Game/Characters/Incomplete", Warn);
            yield return (incomplete == 2, "incomplete native voice discovery is not reused");
        }
        var recipe = new WeaponModelRecipe
        {
            ObjText = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n",
            Materials = [new() { Slot = 0, SourceMaterialName = "body", StableSlotName = "body", MaterialPath = "/Game/Materials/MI_A" }]
        };
        var key = BuildSharedAssets.Key(recipe);
        yield return (key == BuildSharedAssets.Key(recipe.Clone()), "identical custom mesh recipes share work");
        foreach (var (name, edit) in new (string, Action<WeaponModelRecipe>)[] {
            ("geometry", r => r.ObjText += "v 0 0 1\n"),
            ("scale", r => r.Scale = .8f), ("position", r => r.X = 1),
            ("rotation", r => r.Yaw = 90),
            ("slot identity", r => r.Materials[0].StableSlotName = "other") })
        {
            var changed = recipe.Clone(); edit(changed);
            yield return (key != BuildSharedAssets.Key(changed), "shared mesh plan preserves unique " + name);
        }
        var materialOnly = recipe.Clone(); materialOnly.Materials[0].MaterialPath = "/Game/Mods/SuitB/MI_A";
        yield return (key == BuildSharedAssets.Key(materialOnly), "shared geometry allows separate per-suit material bindings");
        var twoSlots = recipe.Clone();
        twoSlots.Materials.Add(new() { Slot = 1, SourceMaterialName = "trim", StableSlotName = "trim", MaterialPath = recipe.Materials[0].MaterialPath });
        var separateBindings = twoSlots.Clone(); separateBindings.Materials[1].MaterialPath = "/Game/Materials/MI_B";
        yield return (BuildSharedAssets.Key(twoSlots) != BuildSharedAssets.Key(separateBindings),
            "shared geometry preserves repeated versus independent material import bindings");
        var before = BuildSharedAssets.Current;
        using (var outer = new BuildSharedAssets("test", "test", _ => { }))
        {
            using (var inner = new BuildSharedAssets("test", "test", _ => { }))
                if (!ReferenceEquals(BuildSharedAssets.Current, inner)) throw new InvalidOperationException("Nested build scope missing.");
            yield return (ReferenceEquals(BuildSharedAssets.Current, outer), "nested shared-build scope restores its parent");
        }
        yield return (ReferenceEquals(BuildSharedAssets.Current, before), "shared work cannot leak between builds");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "Batcomputer-native-sharing-test-" + Guid.NewGuid().ToString("N"));
        string? sharedPath = null;
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            var source = Path.Combine(fixtureRoot, "ABP_Test.uasset");
            File.WriteAllBytes(source, [1, 2, 3]);
            File.WriteAllBytes(Path.ChangeExtension(source, ".uexp"), [4, 5]);
            using (var byteScope = new BuildSharedAssets(fixtureRoot, "test", _ => { }))
            {
                var first = byteScope.TryReadNativeBytes(source, out var firstBytes, out var firstSplit);
                var second = byteScope.TryReadNativeBytes(source, out var secondBytes, out var secondSplit);
                yield return (first && second && firstSplit && secondSplit && ReferenceEquals(firstBytes, secondBytes) &&
                    firstBytes.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }),
                    "all-class native input reuse preserves complete header/export bytes without a disk cache");
                yield return (!byteScope.TryReadNativeBytes(Path.Combine(Path.GetTempPath(), "mutable-suit-stage", "Example.uasset"), out _, out _),
                    "class-independent input pool rejects mutable files outside the extracted source tree");
                File.WriteAllBytes(source, [1, 2, 3, 4]);
                var rejected = false;
                try { byteScope.TryReadNativeBytes(source, out _, out _); }
                catch (IOException) { rejected = true; }
                yield return (rejected, "native source changes during a build cannot silently reuse stale bytes");
            }
            File.WriteAllBytes(source, [1, 2, 3]);
            var calls = 0;
            string? Extract() { calls++; return source; }
            using (var scope = new BuildSharedAssets("test", "test", _ => { }))
            {
                sharedPath = scope.NativeAnimationParent("/Game/Characters/ABP_Test", Extract);
                File.WriteAllBytes(source, [9]); // Consumers must not depend on another suit's scratch files.
                var again = scope.NativeAnimationParent("/Game/Characters/ABP_Test", Extract);
                yield return (calls == 1 && again == sharedPath && File.ReadAllBytes(again!).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                    File.ReadAllBytes(Path.ChangeExtension(again!, ".uexp")).SequenceEqual(new byte[] { 4, 5 }),
                    "native animation extraction is shared once as a separate immutable package snapshot");
                scope.NativeAnimationParent("/Game/Mods/SuitA/ABP_Test", Extract);
                scope.NativeAnimationParent("/Game/Mods/SuitA/ABP_Test", Extract);
                yield return (calls == 3, "suit-local animation sources never enter the shared native pool");
                var absentCalls = 0;
                scope.NativeAnimationParent("/Game/Characters/ABP_Absent", () => { absentCalls++; return null; });
                scope.NativeAnimationParent("/Game/Characters/ABP_Absent", () => { absentCalls++; return null; });
                yield return (absentCalls == 1, "missing shipped animation parents are probed once per build");
            }
            yield return (!File.Exists(sharedPath), "shared native package files are removed after the build scope ends");
        }
        finally { if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true); }
    }
}
