namespace Batcomputer;

internal static class TextureImportQueueRegressionChecks
{
    internal static int RunCooked(string workspace,string output)
    {
        try
        {
            output=Path.GetFullPath(output);
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new IOException("Choose an empty output directory for the texture batch test.");
            Directory.CreateDirectory(output);
            var templates=new[] { "TextureStandaloneTemplate_DroneControlBGRA8","TextureStandaloneTemplate_BatarangBC5" }
                .Select(f=>TextureCookTemplateService.TemplateJsonPath(workspace,f)).ToArray();
            if (templates.Any(t=>!TextureCookTemplateService.IsTemplateReady(t))) throw new IOException("Prepare the body and normal-map templates first.");
            var source=Path.Combine(output,"Example_BC.png");
            using (var bitmap=new Bitmap(32,32))
            {
                using var graphics=Graphics.FromImage(bitmap); graphics.Clear(Color.FromArgb(255,128,128,255));
                bitmap.Save(source,System.Drawing.Imaging.ImageFormat.Png);
            }
            var bad=Path.Combine(output,"Broken.png"); File.WriteAllBytes(bad,[1,2,3]);
            var jobs=new[]
            {
                new TextureImportJob { SourcePath=source,Name="Body",Kind="Character texture" },
                new TextureImportJob { SourcePath=bad,Name="Broken",Kind="Character texture" },
                new TextureImportJob { SourcePath=source,Name="Normal",Kind="Normal map" }
            };
            var results=new List<TextureCookService.Result>();
            TextureCookService.Request Request(TextureImportJob job,IProgress<TextureImportProgress>? progress,CancellationToken token) => new()
            {
                SourceImagePath=job.SourcePath, TemplateJsonPath=templates[job.Name=="Normal" ? 1 : 0],
                OutputPackagePath="/Game/Mods/TextureQueueTest/Textures/T_"+job.Name,
                OutputContentRoot=Path.Combine(output,"Cooked"), Progress=progress,CancellationToken=token
            };
            async Task<string> Import(TextureImportJob job,IProgress<TextureImportProgress> progress,CancellationToken token)
            {
                var result=await Task.Run(()=>new TextureCookService(workspace).Cook(Request(job,progress,token)));
                if (result.Status!="created") throw new InvalidDataException(result.Error);
                results.Add(result); return result.OutputPackagePath;
            }
            using var runner=new TextureImportQueueRunner();
            runner.Enqueue([jobs[0]],Import);
            runner.Enqueue(jobs.Skip(1),Import);
            runner.Completion.GetAwaiter().GetResult();
            if (jobs[0].State!=TextureImportState.Complete || jobs[1].State!=TextureImportState.Failed || jobs[2].State!=TextureImportState.Complete ||
                results.Count!=2 || results.Any(r=>!File.Exists(r.OutputUasset) || r.MipCount<2))
                throw new InvalidDataException("Real queue did not retain both valid texture cooks around a failed PNG.");
            Console.WriteLine("PASS: shared main-window queue cooked a single body plus appended normal/bad-PNG jobs with full mip chains and isolated failures");
            var previous=File.ReadAllBytes(results[0].OutputUasset);
            using var cancel=new CancellationTokenSource();
            var interrupted=false;
            try
            {
                new TextureCookService(workspace).Cook(Request(jobs[0],new InlineProgress(p=> { if(p.Percent>=88) cancel.Cancel(); }),cancel.Token));
            }
            catch (OperationCanceledException) { interrupted=true; }
            if (!interrupted || !previous.SequenceEqual(File.ReadAllBytes(results[0].OutputUasset)) ||
                Directory.EnumerateFiles(output,"*.texture-cook-attempt-*",SearchOption.AllDirectories).Any())
                throw new InvalidDataException("Cancellation did not preserve the completed cook or clean temporary attempts.");
            Console.WriteLine("PASS: cancellation before publication preserves the existing cooked asset and cleans partial attempts");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private sealed class InlineProgress(Action<TextureImportProgress> report) : IProgress<TextureImportProgress>
    { public void Report(TextureImportProgress value)=>report(value); }

    internal static IEnumerable<(bool Passed,string Description)> Run()
    {
        static TextureImportJob Job(string name) => new() { Name=name,SourcePath=name+".png",Kind="Character texture" };
        var jobs=new[] { Job("Body"), Job("Bad_source"), Job("Normal") };
        var active=0; var peak=0; var order=new List<string>();
        TextureImportQueue.RunAsync(jobs, async (job,progress,token) =>
        {
            active++; peak=Math.Max(peak,active); order.Add(job.Name);
            try { await Task.Delay(1,token); if (job.Name=="Bad_source") throw new IOException("Invalid PNG"); return "/Game/Mods/"+job.Name; }
            finally { active--; }
        }, _=>{}, CancellationToken.None).GetAwaiter().GetResult();
        yield return (peak==1 && order.SequenceEqual(new[]{"Body","Bad_source","Normal"}),"texture queue cooks sequentially to bound encoder memory");
        yield return (jobs[0].State==TextureImportState.Complete && jobs[1].State==TextureImportState.Failed && jobs[2].State==TextureImportState.Complete,
            "one failed texture does not prevent later imports");
        yield return (jobs[1].Detail=="Invalid PNG" && jobs[1].OutputPath=="" && jobs[0].Percent==100,
            "texture queue retains a useful failure without claiming an output was saved");
        var calls=0;
        TextureImportQueue.RunAsync(jobs, (_,_,_)=>{ calls++; return Task.FromResult("unexpected"); }, _=>{}, CancellationToken.None).GetAwaiter().GetResult();
        yield return (calls==0,"completed and failed textures are not silently recooked on resume");
        jobs[1].State=TextureImportState.Queued;
        TextureImportQueue.RunAsync(jobs, (_,_,_)=>{ calls++; return Task.FromResult("/Game/Mods/Repaired"); }, _=>{}, CancellationToken.None).GetAwaiter().GetResult();
        yield return (calls==1 && jobs.All(j=>j.State==TextureImportState.Complete),"retry cooks only the explicitly requeued failure");
        using var cancel=new CancellationTokenSource();
        var cancelJobs=new[]{Job("Active"),Job("Pending")};
        TextureImportQueue.RunAsync(cancelJobs, (_,_,token)=>
        {
            cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult("");
        }, _=>{},cancel.Token).GetAwaiter().GetResult();
        yield return (cancelJobs[0].State==TextureImportState.Cancelled && cancelJobs[1].State==TextureImportState.Queued,
            "cancelling leaves the active import retryable and remaining files queued");
        var cancelled=false;
        try { new TextureCookService("unused").Cook(new() { SourceImagePath="missing.png",CancellationToken=cancel.Token }); }
        catch (OperationCanceledException) { cancelled=true; }
        yield return (cancelled,"a cancelled texture cook stops before reading source or touching outputs");
        foreach (var result in RunSharedWorkerChecks()) yield return result;
        foreach (var result in RunSettingsChecks()) yield return result;
    }

    private static IReadOnlyList<(bool Passed, string Description)> RunSharedWorkerChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var results = new List<(bool, string)>();
            using var runner = new TextureImportQueueRunner();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TextureImportJob { Name = "Single", SourcePath = "Single.png", Kind = "Character texture",
                Profile = new("body", "Body", "2K", true, ""), OwnerLabel = "Suit A" };
            var normal = new TextureImportJob { Name = "Batch_Normal", SourcePath = "Normal.png", Kind = "Normal map",
                Profile = new("normal", "Normal", "BC5", true, ""), OwnerLabel = "Suit B" };
            var mask = new TextureImportJob { Name = "Batch_Mask", SourcePath = "Mask.png", Kind = "Roughness/spec mask",
                Profile = new("mask", "Mask", "DXT1", true, ""), OwnerLabel = "Suit B" };
            var order = new List<string>(); var active = 0; var peak = 0;
            async Task<string> Import(TextureImportJob job, string owner)
            {
                active++; peak = Math.Max(peak, active); order.Add(job.Name);
                try
                {
                    if (ReferenceEquals(job, first)) await gate.Task;
                    return $"/Game/Mods/{owner}/{job.Profile!.Id}/{job.Name}";
                }
                finally { active--; }
            }
            runner.Enqueue([first], (j, _, _) => Import(j, "A"));
            runner.Enqueue([normal, mask], (j, _, _) => Import(j, "B"));
            results.Add((runner.IsRunning && first.State == TextureImportState.Importing && normal.State == TextureImportState.Queued &&
                runner.Jobs.Count == 3 && order.SequenceEqual(["Single"]),
                "single and batch imports can join the main-window queue while the first texture is cooking"));
            gate.SetResult(); runner.Completion.GetAwaiter().GetResult();
            results.Add((peak == 1 && order.SequenceEqual(["Single", "Batch_Normal", "Batch_Mask"]) &&
                runner.Jobs.All(j => j.State == TextureImportState.Complete),
                "appending jobs retains FIFO order and never starts overlapping texture encoders"));
            results.Add((first.OutputPath == "/Game/Mods/A/body/Single" && normal.OutputPath == "/Game/Mods/B/normal/Batch_Normal" &&
                mask.OutputPath == "/Game/Mods/B/mask/Batch_Mask" && normal.Kind == "Normal map",
                "queued batches retain their individual profile, type and captured suit destination"));
            var duplicateRejected = false;
            try { runner.Enqueue([first], (_, _, _) => Task.FromResult("unexpected")); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            results.Add((duplicateRejected, "already submitted texture jobs cannot be queued twice"));
            runner.ClearFinished();
            results.Add((runner.Jobs.Count == 0 && first.OutputPath.Length > 0,
                "clearing the queue removes finished rows without changing imported outputs"));

            using var stoppable = new TextureImportQueueRunner();
            var interrupted = new TextureImportJob { Name = "Stop", SourcePath = "Stop.png", Kind = "Character texture" };
            var pending = new TextureImportJob { Name = "Pending", SourcePath = "Pending.png", Kind = "Character texture" };
            var attempts = 0;
            stoppable.Enqueue([interrupted, pending], async (j, _, token) =>
            {
                if (ReferenceEquals(j, interrupted) && attempts++ == 0) await Task.Delay(Timeout.Infinite, token);
                return "/Game/Mods/Queue/" + j.Name;
            });
            stoppable.Pause(); stoppable.Completion.GetAwaiter().GetResult();
            results.Add((stoppable.IsPaused && interrupted.State == TextureImportState.Cancelled && pending.State == TextureImportState.Queued,
                "stopping the main-window queue cancels the active cook and preserves waiting jobs"));
            stoppable.Resume(); stoppable.Completion.GetAwaiter().GetResult();
            results.Add((!stoppable.IsPaused && !stoppable.IsRunning && stoppable.Jobs.All(j => j.State == TextureImportState.Complete) && attempts == 2,
                "continuing restarts the interrupted texture and processes the remaining queue"));

            using var retry = new TextureImportQueueRunner();
            var failed = new TextureImportJob { Name = "Retry", SourcePath = "Retry.png", Kind = "Normal map" };
            var retries = 0;
            retry.Enqueue([failed], (_, _, _) => ++retries == 1 ? Task.FromException<string>(new IOException("Invalid source")) : Task.FromResult("repaired"));
            retry.Completion.GetAwaiter().GetResult(); retry.RetryFailed(); retry.Completion.GetAwaiter().GetResult();
            results.Add((retries == 2 && failed.State == TextureImportState.Complete && failed.OutputPath == "repaired",
                "failed main-window imports remain retryable without recreating the settings popup"));
            return results;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static IReadOnlyList<(bool Passed, string Description)> RunSettingsChecks()
    {
        var results = new List<(bool, string)>();
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "texture-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var paths = new[] { Path.Combine(root, "Body_BC.png"), Path.Combine(root, "Body_NRM.png") };
                foreach (var path in paths) File.WriteAllBytes(path, [1]);
                using (var image = new Bitmap(128, 64)) image.Save(paths[0], System.Drawing.Imaging.ImageFormat.Png);
                TextureImportProfile Profile(string kind, bool verified = true) => new(kind, kind, "Fixture profile", verified, "");
                using var form = new TextureImportQueueForm("Example", paths.Concat([paths[0]]), ["Character texture", "Normal map"],
                    ["Body_BC"], s => s, kind => [Profile(kind)]);
                static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(All(c)));
                using (var unlocked = File.Open(paths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    results.Add((All(form).OfType<PictureBox>().Any(p => p.Image is { Width: <= 64, Height: <= 64 }),
                        "batch previews retain only thumbnails and release source PNG handles before importing"));
                form.SetKindForAudit(0, "Normal map");
                results.Add((form.Jobs.Count == 2 && form.Jobs[0].Name == "Body_BC_2" &&
                    form.Jobs[0].Kind == "Normal map" && form.Jobs[0].Profile?.Id == "Normal map",
                    "batch setup reserves queued names, ignores repeated source selections and retains per-row profile choices"));
                form.SetKindForAudit(1, "Character texture");
                form.ApplySettingsForAudit(0, 1);
                results.Add((form.Jobs[1].Kind == "Normal map" && form.Jobs[1].Profile?.Id == "Normal map" &&
                    All(form).OfType<PictureBox>().Count() == 2 && !All(form).Any(c => c is DataGridView),
                    "modern batch preview cards support copying the active type and profile to selected files"));
                results.Add((!All(form).Any(c => c is ThemedProgressBar) &&
                    form.SubmitForAudit() && form.DialogResult == DialogResult.OK && form.Jobs.All(j => j.State == TextureImportState.Queued),
                    "Import closes the settings-only popup and returns queued jobs without running an encoder there"));
                using var missing = new TextureImportQueueForm("Example", paths.Take(1), ["Character texture"], [], s => s, _ => []);
                using var experimental = new TextureImportQueueForm("Example", paths.Take(1), ["Character texture"], [], s => s, kind => [Profile(kind, false)]);
                results.Add((!missing.SubmitForAudit() && !experimental.SubmitForAudit() &&
                    missing.DialogResult != DialogResult.OK && experimental.DialogResult != DialogResult.OK,
                    "batch setup refuses missing profiles and unapproved experimental cooks before closing"));
                using var names = new TextureImportQueueForm("Example", paths, ["Character texture"], ["Taken"], s => s.Replace(" ", "_"), kind => [Profile(kind)]);
                names.Jobs[0].Name = "Taken";
                var reserved = !names.SubmitForAudit();
                names.Jobs[0].Name = names.Jobs[1].Name;
                var duplicate = !names.SubmitForAudit();
                names.Jobs[0].Name = "Bad name";
                results.Add((reserved && duplicate && !names.SubmitForAudit(),
                    "batch setup rejects reserved, duplicate and unsafe texture names without overwriting existing assets"));
            }
            catch (Exception ex) { results.Add((false, "batch setup regression: " + ex.Message)); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) return [(false, "batch setup regression timed out")];
        return results;
    }
}
