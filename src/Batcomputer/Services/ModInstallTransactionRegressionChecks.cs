namespace Batcomputer;

internal static class ModInstallTransactionRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var root = Path.Combine(Path.GetTempPath(), "Batcomputer-mod-install-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "Game");
        Directory.CreateDirectory(game);
        try
        {
            var service = new ModInstallTransactionService();
            string[] names = ["Content/Paks/Test.pak", "Content/Paks/Test.ucas", "Content/Paks/Test.utoc",
                "Config/Tags/Test.ini", "Mods/Test/mod.json", "Registry/Test.uplugin", "Registry/AssetRegistry.bin", "Registry/Config/CharacterSelectSystem.ini"];
            var changes = names.Select((name, i) =>
            {
                var source = Path.Combine(root, "source" + i); File.WriteAllText(source, "new " + i);
                return new ModInstallTransactionService.Change(source, Path.Combine(game, name));
            }).ToList();
            var obsolete = Path.Combine(game, "Registry/Config/Tags/Old.ini");
            changes.Add(new(null, obsolete));
            void Seed(bool empty)
            {
                foreach (var change in changes)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Destination)!);
                    if (empty) File.Delete(change.Destination);
                    else File.WriteAllText(change.Destination, "old " + change.Destination);
                }
            }
            bool Restored(bool empty) => changes.All(c => empty ? !File.Exists(c.Destination)
                : File.ReadAllText(c.Destination) == "old " + c.Destination);
            foreach (bool empty in new[] { false, true })
            {
                for (int boundary = 0; boundary <= changes.Count; boundary++)
                {
                    Seed(empty);
                    var result = service.InstallForTest(game, changes, index =>
                    {
                        if (index == boundary) throw new IOException("Injected commit failure");
                    });
                    var completed = boundary == changes.Count;
                    results.Add((result.Success == completed && result.DestinationConsistent && (completed
                        ? changes.All(c => c.Source is null ? !File.Exists(c.Destination) : File.ReadAllText(c.Destination) == File.ReadAllText(c.Source))
                        : Restored(empty)), $"mod install {(empty ? "first install" : "replacement")} boundary {boundary}: trio and all loose files remain consistent"));
                }
            }
            Seed(false);
            using (var held = new FileStream(changes[1].Destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var locked = service.Install(game, changes);
                results.Add((!locked.Success && locked.DestinationConsistent && Restored(false), "locked UCAS restores the prior PAK and every mod metadata file"));
            }
            using (var held = new FileStream(changes[6].Destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var locked = service.Install(game, changes);
                results.Add((!locked.Success && locked.DestinationConsistent && Restored(false), "locked AssetRegistry restores the already replaced trio, tags and mod.json"));
            }
            var missing = changes.Select((c,i) => i == 7 ? c with { Source = Path.Combine(root, "missing.ini") } : c).ToArray();
            var incomplete = service.Install(game, missing);
            results.Add((!incomplete.Success && Restored(false), "missing mandatory plugin metadata rejects the whole install before writes"));
            var duplicate = service.Install(game, changes.Concat([changes[0]]).ToArray());
            results.Add((!duplicate.Success && Restored(false), "duplicate mod install destinations are rejected before writes"));
            var outside = Path.Combine(root, "outside.txt"); File.WriteAllText(outside, "untouched");
            var escaped = service.Install(game, [new(changes[0].Source, outside)]);
            results.Add((!escaped.Success && File.ReadAllText(outside) == "untouched", "mod install cannot write outside the selected game root"));
            results.Add((!Directory.GetFiles(game, "*", SearchOption.AllDirectories).Any(p => p.EndsWith(".backup") || p.EndsWith(".installing") || p.EndsWith(".restoring")), "successful rollback removes temporary install files"));
        }
        finally { Directory.Delete(root, recursive: true); }
        return results;
    }
}
