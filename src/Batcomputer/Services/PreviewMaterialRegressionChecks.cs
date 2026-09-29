using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;

namespace Batcomputer;

internal static class PreviewMaterialRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var mounts = PreviewMountPathService.GameFeatureMounts([
            "LEGOBatmanLotDK/Plugins/GameFeatures/DLC_ArkhamPack/Content/Characters/BP_Catwoman.uasset",
            "legobatmanlotdk/plugins/gamefeatures/dlc_arkhampack/content/Textures/T_Body.uasset",
            "LEGOBatmanLotDK/Plugins/GameFeatures/DLC_BeyondPack/Content/Characters/BP_Batman.uasset",
            "LEGOBatmanLotDK/Content/Characters/BP_Base.uasset",
            "LEGOBatmanLotDK/Plugins/GameFeatures/Invalid/Nested/Content/Mesh.uasset",
            "OtherGame/Plugins/GameFeatures/DLC_Other/Content/Mesh.uasset",
            "LEGOBatmanLotDK/Plugins/GameFeatures/../Content/Mesh.uasset",
            "LEGOBatmanLotDK/Plugins/GameFeatures/Game/Content/Mesh.uasset",
        ]);
        results.Add((mounts.Count == 2 && mounts["DLC_ArkhamPack"] == "LEGOBatmanLotDK/Plugins/GameFeatures/DLC_ArkhamPack",
            "preview plugin mounts preserve physical file identities and deduplicate case-insensitively"));
        results.Add((mounts.ContainsKey("dlc_beyondpack") && !mounts.ContainsKey("Game") && !mounts.ContainsKey(".."),
            "preview mount discovery ignores unrelated, nested, unsafe and reserved paths"));
        CheckLooseContentMounts(results);
        var gameplay = new GeneratedMaterialEntry { PackagePath="/Game/Mods/Fixture/MI_Test_Base_Body_EoM", TemplateOutputRole="gameplay", TemplateGroupId="pair" };
        var cutscene = new GeneratedMaterialEntry { PackagePath="/Game/Mods/Fixture/MI_Test_Base_Body_CUT", TemplateOutputRole="cutscene", TemplateGroupId="pair" };
        var playTile = MaterialTilePresentationService.Describe(gameplay.PackagePath, gameplay, false);
        var cutTile = MaterialTilePresentationService.Describe(cutscene.PackagePath, cutscene, false);
        results.Add((playTile.Subtitle == "Gameplay · your MI" && cutTile.Subtitle == "Cutscene · your MI",
            "paired materials have distinct short role labels even when long tile titles are clipped"));
        results.Add((playTile.ToolTip.Contains(gameplay.PackagePath) && cutTile.ToolTip.Contains(cutscene.PackagePath) &&
            gameplay.PackagePath != cutscene.PackagePath && playTile.ToolTip.Contains("paired recipe"),
            "paired-material presentation retains both exact package identities and explains the pair"));
        results.Add((MaterialTilePresentationService.Describe(cutscene.PackagePath, null, false).Subtitle.StartsWith("Cutscene") &&
            MaterialTilePresentationService.Describe(gameplay.PackagePath, null, false, true).Subtitle == "Gameplay · shared MI",
            "legacy suffixes and shared-library materials retain visible gameplay/cutscene roles"));
        results.Add((MaterialTilePresentationService.Describe("/Game/Mods/Fixture/MI_Custom", null, false).Subtitle == "your MI · drag to apply" &&
            MaterialTilePresentationService.Describe("/Game/Mods/Fixture/MI_Face", null, true).Subtitle.Contains("apply to Face"),
            "ordinary materials and face-only tile instructions remain unchanged"));
        return results;
    }

    private static void CheckLooseContentMounts(List<(bool Passed, string Description)> results)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "BatcomputerPreviewMountChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var versions = new VersionContainer(EGame.GAME_UE5_6);
            const string stem = "Mods/Fixture/MI_Face";
            var firstRoot = new DirectoryInfo(Path.Combine(fixture, "Library", "Content"));
            var secondRoot = new DirectoryInfo(Path.Combine(fixture, "OtherLibrary", "Content"));
            Dictionary<string, GameFile> Sources(DirectoryInfo root, byte marker)
            {
                var files = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
                {
                    var path = Path.Combine(root.FullName, stem.Replace('/', Path.DirectorySeparatorChar) + extension);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, [marker, (byte)extension.Length]);
                    var file = new OsGameFile(root, new FileInfo(path), "", versions);
                    files.Add(file.Path, file);
                }
                return files;
            }

            var firstSources = Sources(firstRoot, 11);
            var first = PreviewMountPathService.LooseContentFiles(firstRoot, firstSources, versions);
            var second = PreviewMountPathService.LooseContentFiles(secondRoot, Sources(secondRoot, 22), versions);
            var mountedStem = PreviewMountPathService.GameContentFilePrefix + stem;
            results.Add((first.Count == 3 && first.All(p => p.Key == p.Value.Path) &&
                first.ContainsKey(mountedStem + ".uasset") && first.ContainsKey(mountedStem + ".uexp") &&
                first.ContainsKey(mountedStem + ".ubulk") && !first.ContainsKey(stem + ".uexp"),
                "loose preview assets and split .uexp/.ubulk payloads carry the same canonical key and file path"));
            results.Add((firstSources.All(p => p.Key == p.Value.Path && p.Value.Path.StartsWith("Mods/")) &&
                first.All(p => p.Value is OsGameFile os && os.ActualFile.FullName ==
                    ((OsGameFile)firstSources[p.Key[PreviewMountPathService.GameContentFilePrefix.Length..]]).ActualFile.FullName &&
                    p.Value.Read()[0] == 11),
                "loose remounts retain read-only physical sources without changing the original GameFile identities"));

            var empty = Path.Combine(fixture, "EmptyProvider");
            Directory.CreateDirectory(empty);
            using var provider = new DefaultFileProvider(empty, SearchOption.TopDirectoryOnly, versions, StringComparer.OrdinalIgnoreCase);
            provider.Initialize();
            provider.Files.AddFiles(first, long.MaxValue);
            provider.Files.AddFiles(second, long.MaxValue - 1);
            results.Add((provider.Files[mountedStem + ".UASSET"].Read()[0] == 11 &&
                provider.Files[Path.ChangeExtension(provider.Files[mountedStem + ".uasset"].Path, ".uexp")].Read()[0] == 11 &&
                provider.Files[Path.ChangeExtension(provider.Files[mountedStem + ".uasset"].Path, ".ubulk")].Read()[0] == 11,
                "first loose-root priority applies equally to case-insensitive asset, export and bulk lookups"));
            using var reverse = new DefaultFileProvider(empty, SearchOption.TopDirectoryOnly, versions, StringComparer.OrdinalIgnoreCase);
            reverse.Initialize();
            reverse.Files.AddFiles(second, long.MaxValue - 1);
            reverse.Files.AddFiles(first, long.MaxValue);
            results.Add((reverse.Files[mountedStem + ".uasset"].Read()[0] == 11 &&
                reverse.Files[mountedStem + ".uexp"].Read()[0] == 11 && reverse.Files[mountedStem + ".ubulk"].Read()[0] == 11,
                "split loose overlay precedence is independent of dictionary insertion order"));

            var canonicalRoot = new DirectoryInfo(Path.Combine(fixture, "Native", "LEGOBatmanLotDK", "Content"));
            var canonicalSources = Sources(canonicalRoot, 33).Values.OfType<OsGameFile>()
                .Select(file => (GameFile)new OsGameFile(canonicalRoot.Parent!.Parent!, file.ActualFile, "", versions))
                .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            canonicalSources.Add("Engine/Content/Fixture.uasset", firstSources[stem + ".uasset"]);
            var canonical = PreviewMountPathService.LooseContentFiles(canonicalRoot, canonicalSources, versions);
            results.Add((canonical.Count == 3 && canonical.All(p => ReferenceEquals(p.Value, canonicalSources[p.Key])) &&
                canonical.All(p => p.Key == p.Value.Path) && !canonical.ContainsKey("Engine/Content/Fixture.uasset"),
                "canonical native extracts keep original file identities and do not acquire duplicate prefixes or unrelated files"));
            var incomplete = PreviewMountPathService.LooseContentFiles(firstRoot,
                firstSources.Where(p => p.Key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)), versions);
            results.Add((incomplete.Count == 1 && !incomplete.ContainsKey(Path.ChangeExtension(incomplete.Values.Single().Path, ".uexp")),
                "a missing split companion is not synthesized or masked by an unsafe raw-path alias"));
        }
        catch (Exception ex)
        {
            results.Add((false, "loose preview mounting checks: " + ex.Message));
        }
        finally
        {
            // This exact GUID fixture is owned by this check; never clean an extraction/library root.
            Directory.Delete(fixture, recursive: true);
        }
    }
}
