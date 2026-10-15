using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Batcomputer;

internal static class SuitPreparationCacheRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        yield return (!new AppSettings().EnableSuitPreparationCache &&
            !JsonSerializer.Deserialize<AppSettings>("{}")!.EnableSuitPreparationCache,
            "preparation cache is opt-in for new settings and older settings without a cache preference");
        yield return (JsonSerializer.Deserialize<AppSettings>("{\"EnableSuitPreparationCache\":true}")!.EnableSuitPreparationCache,
            "an explicitly enabled preparation cache remains enabled when settings are loaded");
        var root = Directory.CreateTempSubdirectory("BatcomputerPreparationChecks-").FullName;
        var previous = AppSettings.Current;
        try
        {
            var stage = Path.Combine(root, "Stage");
            var content = Path.Combine(stage, "LEGOBatmanLotDK", "Content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "test.uasset"), "asset");
            File.WriteAllText(Path.Combine(content, "test.uexp"), "payload");
            var project = new NativeSuitProject { SlotId = "cache-test", DisplayName = "Cache test" };
            var native = Path.Combine(root, "Native", "Content"); var exports = Path.Combine(root, "Exports"); var paks = Path.Combine(root, "Paks");
            foreach (var dir in new[] { native, exports, paks }) Directory.CreateDirectory(dir);
            var mapping = Path.Combine(root, "test.usmap"); File.WriteAllText(mapping, "map1");
            var source = Path.Combine(root, "source.png"); File.WriteAllText(source, "one"); project.CoverImagePath = source;
            AppSettings.Current = new AppSettings { ProjectRoot = root, ExtractedContentRoot = native, ExportContentRoot = exports, GamePaksRoot = paks, UsmapPath = mapping };
            var key = SuitPreparationCache.Fingerprint(project, root, content);
            var copy = Path.Combine(root, "Copy"); Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(content)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            yield return (key == SuitPreparationCache.Fingerprint(project, root, copy), "copied preparation paths do not cause false cache misses");
            var stamp = File.GetLastWriteTimeUtc(source); File.WriteAllText(source, "two"); File.SetLastWriteTimeUtc(source, stamp);
            yield return (key != SuitPreparationCache.Fingerprint(project, root, content), "source-byte edits invalidate reuse even with unchanged size and timestamp");
            File.WriteAllText(source, "one"); File.SetLastWriteTimeUtc(source, stamp);
            project.DisplayName = "Changed";
            yield return (key != SuitPreparationCache.Fingerprint(project, root, content), "recipe edits invalidate preparation reuse");
            project.DisplayName = "Cache test"; File.WriteAllText(mapping, "map2");
            yield return (key != SuitPreparationCache.Fingerprint(project, root, content), "mapping changes invalidate preparation reuse");
            File.WriteAllText(mapping, "map1"); File.WriteAllText(Path.Combine(paks, "game.utoc"), "changed game");
            yield return (key != SuitPreparationCache.Fingerprint(project, root, content), "native container changes invalidate preparation reuse");
            var nativeKey = SuitPreparationCache.Fingerprint(project, root, content);
            Directory.CreateDirectory(Path.Combine(paks, "~mods")); File.WriteAllText(Path.Combine(paks, "~mods", "own.pak"), "installed mod");
            yield return (nativeKey == SuitPreparationCache.Fingerprint(project, root, content), "installing an authored mod does not invalidate native-game cache identity");
            yield return (SuitPreparationCache.Store(root, "one", project, stage, null, 16), "small preparation snapshots fit the configured storage budget");
            var media = Path.Combine(content, "test.uexp"); var report = Path.Combine(stage, "voice.json"); File.WriteAllText(report, "{}");
            var voice = new CharacterVoiceBuildService.Result("test", [], [media, media.Replace('\\', '/')], report);
            yield return (SuitPreparationCache.Store(root, "one", project, stage, voice, 16),
                "duplicate voice file paths with mixed Windows separators are cached once");
            yield return (SuitPreparationCache.Restore(root, project.SlotId, "changed", Path.Combine(root, "miss")) is null,
                "changed preparation keys never reuse old output");
            var hit = SuitPreparationCache.Restore(root, project.SlotId, "one", Path.Combine(root, "hit"));
            yield return (hit is not null && File.ReadAllText(Path.Combine(hit.Root, "LEGOBatmanLotDK", "Content", "test.uexp")) == "payload",
                "cache restoration verifies and preserves prepared bytes");
            SuitPreparationCache.Store(root, "two", project, stage, null, 16);
            var cache = SuitPreparationCache.Root(root);
            yield return (Directory.GetFiles(cache, "*.zip").Length == 1, "only the latest preparation per suit is retained");
            using (var zip = ZipFile.Open(Directory.GetFiles(cache, "*.zip").Single(), ZipArchiveMode.Update))
            {
                zip.GetEntry("LEGOBatmanLotDK/Content/test.uexp")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("LEGOBatmanLotDK/Content/test.uexp").Open()); writer.Write("corrupt");
            }
            yield return (Reject(() => SuitPreparationCache.Restore(root, project.SlotId, "two", Path.Combine(root, "corrupt"))),
                "corrupt cache payloads are rejected instead of packaged");
            SuitPreparationCache.Store(root, "two", project, stage, null, 16);
            using (var zip = ZipFile.Open(Directory.GetFiles(cache, "*.zip").Single(), ZipArchiveMode.Update))
            {
                zip.GetEntry("receipt.json")!.Delete();
                zip.GetEntry("LEGOBatmanLotDK/Content/test.uexp")!.Delete();
                using (var escape = new StreamWriter(zip.CreateEntry("../escape").Open())) escape.Write("bad");
                using var writer = new StreamWriter(zip.CreateEntry("receipt.json").Open());
                writer.Write(JsonSerializer.Serialize(new SuitPreparationCache.Receipt("two", project, null,
                    new() { ["../escape"] = "bad", ["LEGOBatmanLotDK/Content/test.uasset"] = "bad" })));
            }
            yield return (Reject(() => SuitPreparationCache.Restore(root, project.SlotId, "two", Path.Combine(root, "escape-test"))) && !File.Exists(Path.Combine(root, "escape")),
                "cache manifests cannot extract outside the disposable restore directory");
            File.WriteAllText(Path.Combine(cache, "unrelated.txt"), "keep");
            SuitPreparationCache.Clear(root);
            yield return (!Directory.GetFiles(cache, "*.zip").Any() && File.Exists(Path.Combine(cache, "unrelated.txt")),
                "clearing the build cache preserves unrelated files and authoring data");
            var random = new byte[9 * 1024 * 1024]; RandomNumberGenerator.Fill(random);
            File.WriteAllBytes(Path.Combine(content, "large.bin"), random);
            SuitPreparationCache.Store(root, "one", project, stage, null, 16);
            project.SlotId = "second"; SuitPreparationCache.Store(root, "one", project, stage, null, 16);
            yield return (Directory.GetFiles(cache, "*.zip").Length == 1 && Directory.GetFiles(cache, "*.zip").Sum(f => new FileInfo(f).Length) <= 16L * 1024 * 1024,
                "least-recently-used preparation snapshots are evicted under the storage cap");
            File.WriteAllBytes(Path.Combine(content, "extra.bin"), random);
            project.SlotId = "oversized";
            yield return (!SuitPreparationCache.Store(root, "one", project, stage, null, 16) && Directory.GetFiles(cache, "*.zip").Length == 1,
                "oversized preparations are skipped without purging other usable snapshots");
        }
        finally
        {
            AppSettings.Current = previous;
            // This is the exact private directory returned by CreateTempSubdirectory.
            Directory.Delete(root, recursive: true);
        }
    }
    private static bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
}
