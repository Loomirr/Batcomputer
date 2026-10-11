using System.IO.Compression;

namespace Batcomputer;

internal static class ReleaseOutputRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var root = Path.Combine(Path.GetTempPath(), "release-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.bin");
            var payload = new byte[400_000];
            new Random(37).NextBytes(payload);
            File.WriteAllBytes(source, payload);
            var zipPath = Path.Combine(root, "release.zip");
            var records = new List<ReleaseArchiveProgress>();
            ReleaseArchiveService.CreateAsync(zipPath, [(source, "Game/Content/example.bin"), (source, "Game/Config/example.bin")],
                new InlineProgress(p => records.Add(p))).GetAwaiter().GetResult();
            using (var zip = ZipFile.OpenRead(zipPath))
            using (var stream = zip.GetEntry("Game/Content/example.bin")!.Open())
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                results.Add((zip.Entries.Count == 2 && copy.ToArray().SequenceEqual(payload), "release ZIP preserves file contents and install-relative paths"));
            }
            results.Add((records.First().FileIndex == 1 && records.Any(p => p.FileIndex == 2 && p.Stage == "Compressing") &&
                records.Last() is { Stage: "Archive ready", Bytes: 800_000, TotalBytes: 800_000 } &&
                records.Select(p => p.Bytes).SequenceEqual(records.Select(p => p.Bytes).Order()),
                "archive progress reports ordered files, actual bytes and final completion"));
            var previous = File.ReadAllBytes(zipPath);
            try
            {
                ReleaseArchiveService.CreateAsync(zipPath, [(source, "Game/new.bin")],
                    new InlineProgress(p => { if (p.Stage == "Finalizing ZIP") throw new IOException("Simulated interrupted archive"); })).GetAwaiter().GetResult();
                results.Add((false, "archive failure must propagate"));
            }
            catch (IOException)
            {
                results.Add((File.ReadAllBytes(zipPath).SequenceEqual(previous) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
                    "failed archive preserves the previous ZIP and removes only its own temporary output"));
            }
            try { ReleaseArchiveService.CreateAsync(zipPath, [(Path.Combine(root, "missing"), "Game/missing")]).GetAwaiter().GetResult(); }
            catch (FileNotFoundException) { }
            results.Add((File.ReadAllBytes(zipPath).SequenceEqual(previous), "missing archive input cannot overwrite a good release"));

            var thread = new Thread(() =>
            {
                try
                {
                    var longPath = Path.Combine(root, string.Concat(Enumerable.Repeat("long-project-name-", 25)), "release.zip");
                    var model = new Dialog.Model { ExpandPaths = true, Title = "Mod built and installed", PrimaryText = "Done",
                        Fields = [("Archive", longPath), ("Existing archive", zipPath), ("Folder", root), ("Example only", "...\\Steam\\steamapps\\common")] };
                    using var form = Dialog.CreateForm(null, model);
                    _ = form.Handle;
                    var boxes = All(form).OfType<TextBox>().ToArray();
                    results.Add((boxes.Length == 4 && boxes[0].Text == longPath && boxes.All(b => b.ReadOnly && b.Multiline && b.WordWrap),
                        "release receipts retain selectable full paths rather than truncated labels"));
                    results.Add((All(form).OfType<Button>().Count(b => b.Text == "Copy path") == 4 &&
                        All(form).OfType<Button>().Count(b => b.Text == "Open folder" && b.Enabled) == 2,
                        "release receipts expose copy actions and only enable opening existing locations"));
                    results.Add((ReleasePathsForm.FolderFor(zipPath) == root && ReleasePathsForm.FolderFor("https://example.com") is null &&
                        ReleasePathsForm.FolderFor("relative/path") is null, "open-folder resolves files to their containing directory and rejects non-filesystem targets"));
                    using var compact = Dialog.CreateForm(null, new Dialog.Model { ExpandPaths = true, Title = "Mod release archive created", Subtitle = "Example character pack",
                        Message = "Install Loomirr's LOTDK UE4SS first, then extract this mod into your Steam common folder. Its folders are already arranged for installation.",
                        CalloutTitle = "Ready to share", CalloutDetail = "10.8 MB · 9 mod files packaged. Loomirr's LOTDK UE4SS supplies the shared registry core.",
                        Fields = [("Archive", Path.Combine(root, "Project collection", "NativeSuitModBuilds", "Example-release.zip")), ("Extract into (this PC)", root)] });
                    _ = compact.Handle;
                    foreach (var child in All(compact)) _ = child.Handle;
                    compact.PerformLayout();
                    var scroller = All(compact).OfType<Panel>().Single(p => p.AutoScroll);
                    results.Add((scroller.Controls[0].Height <= scroller.ClientSize.Height,
                        "default ZIP receipt fits both path cards without unnecessary scrolling"));
                }
                catch (Exception ex) { results.Add((false, "release receipt UI: " + ex)); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10))) results.Add((false, "release receipt UI timed out"));
        }
        catch (Exception ex) { results.Add((false, "release output checks: " + ex)); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
        return results;
    }

    private static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(All(c)));
    private sealed class InlineProgress(Action<ReleaseArchiveProgress> callback) : IProgress<ReleaseArchiveProgress>
    {
        public void Report(ReleaseArchiveProgress value) => callback(value);
    }
}
