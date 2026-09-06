using System.Diagnostics;

namespace Batcomputer;

/// <summary>Hidden WinForms controls only: no game, visible windows, clipboard, or desktop automation.</summary>
internal static class DiagnosticsRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var uiThread = new Thread(() =>
        {
            var previousCrossThreadCheck = Control.CheckForIllegalCrossThreadCalls;
            Control.CheckForIllegalCrossThreadCalls = true;
            try
            {
                using var log = new RecreatedDiagnostics();
                // A worker must not create a handle while InvokeRequired would still be false.
                Task.Run(() => log.AppendLog("early worker message")).GetAwaiter().GetResult();
                bool noWorkerHandle = !log.IsHandleCreated && !log.Controls["_logText"]!.IsHandleCreated;
                _ = log.Handle;
                PumpUntil(() => log.LogText.Contains("early worker message"));
                results.Add((noWorkerHandle, "worker diagnostics wait for UI handle creation without creating worker-owned controls"));

                log.ClearLog();
                var workerThreadId = 0;
                var operation = MainForm.RunFileLockRetryPolicyAsync(() =>
                {
                    workerThreadId = Environment.CurrentManagedThreadId;
                    log.AppendLog("Skinned mesh staged\r\n\r\nNative attachment clearance: Spine_01 >= 4.3");
                    return 42;
                }, "apply skinned meshes and attachment body clearance", log.AppendLog);
                PumpUntil(() => operation.IsCompleted);
                var value = operation.GetAwaiter().GetResult();
                PumpUntil(() => log.LogText.Contains("Native attachment clearance"));
                var lines = log.LogText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                results.Add((value == 42 && workerThreadId != Environment.CurrentManagedThreadId && lines.Length == 2 &&
                    lines[0].StartsWith('[') && lines[0].EndsWith("Skinned mesh staged") &&
                    lines[1].EndsWith("Native attachment clearance: Spine_01 >= 4.3"),
                    "attachment/skinned callbacks through the actual file-retry worker reach the UI without cross-thread exceptions"));

                log.ClearLog();
                Task.Run(() => { for (int i = 0; i < 20; i++) log.AppendLog("ordered " + i); }).GetAwaiter().GetResult();
                PumpUntil(() => log.LogText.Contains("ordered 19"));
                lines = log.LogText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                results.Add((lines.Length == 20 && lines.Select((line, i) => line.EndsWith("ordered " + i)).All(ok => ok),
                    "queued worker log bursts preserve every line and its order"));

                log.ClearLog();
                Task.Run(() => log.AppendLog("discarded by clear")).GetAwaiter().GetResult();
                log.ClearLog(); Application.DoEvents();
                results.Add((log.LogText.Length == 0, "Clear log also clears queued messages instead of resurrecting them later"));

                Task.Run(() => log.AppendLog("retained across handle recreation")).GetAwaiter().GetResult();
                log.Recreate();
                PumpUntil(() => log.LogText.Contains("retained across handle recreation"));
                results.Add((true, "pending diagnostics survive UI handle recreation"));

                Task.Run(() => log.AppendLog("pending at shutdown")).GetAwaiter().GetResult();
                log.Dispose();
                Task.Run(() => log.AppendLog("late worker callback")).GetAwaiter().GetResult();
                Application.DoEvents();
                results.Add((true, "queued and late worker diagnostics are harmless when the log control is disposed"));
            }
            catch (Exception ex) { results.Add((false, "diagnostics thread regression: " + ex)); }
            finally { Control.CheckForIllegalCrossThreadCalls = previousCrossThreadCheck; }
        }) { IsBackground = true, Name = "Diagnostics regression UI" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        if (!uiThread.Join(TimeSpan.FromSeconds(15)))
            return [(false, "diagnostics callbacks must not deadlock their UI thread")];
        return results;
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var watch = Stopwatch.StartNew();
        while (!completed())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Diagnostics callback did not reach the UI.");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private sealed class RecreatedDiagnostics : DiagnosticsControl
    {
        internal void Recreate() => RecreateHandle();
    }
}
