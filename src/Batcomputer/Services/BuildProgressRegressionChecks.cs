using System.Diagnostics;

namespace Batcomputer;

/// <summary>Hidden UI fixtures only; no packaging, installations or clipboard access.</summary>
internal static class BuildProgressRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var thread = new Thread(() =>
        {
            var previous = Control.CheckForIllegalCrossThreadCalls;
            Control.CheckForIllegalCrossThreadCalls = true;
            try
            {
                using var owner = new Form();
                using var progress = new ProgressDialog(owner, "Building mod", 0, detailed: true, showWindow: false);
                results.Add((!owner.Enabled && !progress.Visible, "build progress owns input without opening a window in regression checks"));
                Task.Run(() =>
                {
                    progress.SetStep("Building release");
                    progress.SetContext("Example · Character/suit 2 of 3: Second suit");
                    progress.Report("Preparing voices…");
                    for (var i = 0; i < 30; i++) progress.AppendActivity("activity " + i);
                }).GetAwaiter().GetResult();
                results.Add((!progress.IsHandleCreated, "early build callbacks do not create worker-owned window handles"));
                _ = progress.Handle;
                var log = Descendants(progress).OfType<DiagnosticsControl>().Single();
                _ = log.Handle;
                _ = log.Controls[0].Handle;
                PumpUntil(() => log.LogText.Contains("activity 29") && Find(progress, "BuildDetail").Text.Contains("voices"));
                var lines = log.LogText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                results.Add((lines.Length == 30 && lines.Select((line, i) => line.EndsWith("activity " + i)).All(ok => ok),
                    "build activity preserves worker message ordering"));
                results.Add((Find(progress, "BuildPhase").Text == "Building release" && Find(progress, "BuildContext").Text.Contains("2 of 3"),
                    "worker phase, content counts and current task update on the UI thread"));
                results.Add((Descendants(progress).OfType<ThemedProgressBar>().Single().Indeterminate && Find(progress, "BuildElapsed").Text.StartsWith("Elapsed "),
                    "unknown build duration shows elapsed time and indeterminate activity, not a guessed percentage"));
                progress.Report("Packing release…");
                results.Add((Find(progress, "BuildDetail").Text == "Packing release…", "synchronous build stages repaint their task immediately"));
                log.MaximumCharacters = 256;
                Task.Run(() => { for (var i = 0; i < 100; i++) progress.AppendActivity("bounded activity " + i); }).GetAwaiter().GetResult();
                PumpUntil(() => log.LogText.Contains("bounded activity 99"));
                results.Add((log.LogText.Length <= 256 && !log.LogText.Contains("bounded activity 0"), "build activity retains a bounded recent tail"));
                progress.Close();
                results.Add((!progress.IsDisposed && !owner.Enabled, "closing the progress UI cannot orphan an active build"));
                progress.Dispose();
                Task.Run(() => { progress.Report("late task"); progress.AppendActivity("late output"); }).GetAwaiter().GetResult();
                Application.DoEvents();
                results.Add((owner.Enabled, "completion/disposal restores owner input and ignores late worker output"));
                owner.Enabled = false;
                using (var compact = new ProgressDialog(owner, "Compact operation", 5, false, false))
                {
                    compact.Advance(3, "Third item");
                    results.Add((Descendants(compact).OfType<ThemedProgressBar>().Single().Value == 3 && !Descendants(compact).OfType<DiagnosticsControl>().Any(),
                        "compact determinate progress retains its existing behavior"));
                }
                results.Add((!owner.Enabled, "nested progress does not enable an owner that was already disabled"));
            }
            catch (Exception ex) { results.Add((false, "build progress regression: " + ex)); }
            finally { Control.CheckForIllegalCrossThreadCalls = previous; }
        }) { IsBackground = true, Name = "Build progress regression UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) return [(false, "build progress worker callbacks must not deadlock")];
        return results;
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static Control Find(Control root, string name) => Descendants(root).Single(c => c.Name == name);

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Build progress callback did not arrive.");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}
