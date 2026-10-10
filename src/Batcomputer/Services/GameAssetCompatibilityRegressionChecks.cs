namespace Batcomputer;

internal static class GameAssetCompatibilityRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        results.Add((new[] { GameAssetRefreshService.AllCharacterFilters, GameAssetRefreshService.DeveloperResearchFilters }
            .All(filters => GameAssetRefreshService.FiltersCoverPackage(filters, "Content/" + CharacterSymbolService.MaterialDonor[6..])),
            "normal and developer extraction include the character emblem material, not only its texture"));
        results.Add((new[] { GameAssetRefreshService.BatmanFilters, GameAssetRefreshService.AllCharacterFilters, GameAssetRefreshService.DeveloperResearchFilters }
            .All(filters => new[] { "Sprint_Left", "Sprint_Right" }.All(side =>
                GameAssetRefreshService.FiltersCoverPackage(filters, "Content/Wub/AudioEvents/Play_FFB_FS_" + side))),
            "character extraction retains native footstep events referenced by imported animation notifies"));
        var root = Path.Combine(Path.GetTempPath(), "Batcomputer-game-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new GameAssetCompatibilityService.Source("100", "containers-a");
            var map = Path.Combine(root, "Dinner-5.6.1-100+++Dinner+mainline-test.usmap");
            File.WriteAllText(map, "fixture mappings");
            ModReleaseValidationService.Result Check(GameAssetCompatibilityService.Source? current, string mapping)
            {
                var result = new ModReleaseValidationService.Result();
                GameAssetCompatibilityService.Validate(current, root, mapping, result);
                return result;
            }
            var legacy = Check(source, map);
            results.Add((legacy.Passed && legacy.WarningCount == 1, "untracked legacy extraction is explicitly unverified, not silently marked current"));
            var wrongMap = Check(source, Path.Combine(root, "Dinner-5.6.1-99+++Dinner+mainline-test.usmap"));
            results.Add((!wrongMap.Passed && wrongMap.Findings.Any(f => f.Message.Contains("build 99")), "known stale mappings block packaging with actionable build details"));
            GameAssetCompatibilityService.Record(root, source, map);
            results.Add((Check(source, map).Passed && Check(source, map).WarningCount == 0, "matching game, extraction and mappings pass source validation"));
            results.Add((!Check(source with { GameBuild = "101" }, map).Passed, "game build changes invalidate an old extraction"));
            results.Add((!Check(source with { ContainerFingerprint = "containers-b" }, map).Passed, "container or DLC changes invalidate extraction even at the same executable build"));
            var renamed = Path.Combine(root, "Dinner.usmap");
            File.Copy(map, renamed);
            results.Add((Check(source, renamed).Passed, "identical mappings can be relocated or renamed without invalidation"));
            File.WriteAllText(renamed, "different mapping bytes");
            results.Add((!Check(source, renamed).Passed, "generic mapping filenames cannot bypass receipt content validation"));
            var offline = Check(null, map);
            results.Add((offline.Passed && offline.WarningCount == 1, "offline game detection is reported as unverified"));
            File.WriteAllText(Path.Combine(root, GameAssetCompatibilityService.ReceiptName), "{");
            results.Add((!Check(source, map).Passed, "damaged source receipts fail closed with a build finding"));
        }
        finally { Directory.Delete(root, true); }
        return results;
    }
}
