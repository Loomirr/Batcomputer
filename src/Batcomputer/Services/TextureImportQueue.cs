namespace Batcomputer;

internal sealed record TextureImportProfile(string Id, string Label, string Detail, bool Verified, string Note)
{
    public override string ToString() => $"{Label} · {Detail}{(Verified ? "" : " · Experimental")}";
}

internal enum TextureImportState { Queued, Importing, Complete, Failed, Cancelled }

internal sealed class TextureImportJob
{
    public required string SourcePath { get; init; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public TextureImportProfile? Profile { get; set; }
    public TextureImportState State { get; set; }
    public string Detail { get; set; } = "Ready to import";
    public int Percent { get; set; }
    public string OutputPath { get; set; } = "";
    public string OwnerKey { get; set; } = "";
    public string OwnerLabel { get; set; } = "";
}

/// <summary>Main-window owned FIFO. New single/batch jobs can join the same serial worker.</summary>
internal sealed class TextureImportQueueRunner : IDisposable
{
    private sealed record Work(TextureImportJob Job,
        Func<TextureImportJob, IProgress<TextureImportProgress>, CancellationToken, Task<string>> Import);
    private readonly List<Work> _work = [];
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    internal event Action? Changed;
    internal bool IsRunning { get; private set; }
    internal bool IsPaused { get; private set; }
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal IReadOnlyList<TextureImportJob> Jobs => _work.Select(w => w.Job).ToArray();

    internal void Enqueue(IEnumerable<TextureImportJob> jobs,
        Func<TextureImportJob, IProgress<TextureImportProgress>, CancellationToken, Task<string>> import)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var added = jobs.ToArray();
        if (added.Any(j => j.State != TextureImportState.Queued || _work.Any(w => ReferenceEquals(w.Job, j))) ||
            added.Distinct().Count() != added.Length)
            throw new InvalidOperationException("Only new texture jobs can be added to the queue.");
        _work.AddRange(added.Select(j => new Work(j, import)));
        Changed?.Invoke();
        Start();
    }

    internal void Pause()
    {
        IsPaused = true;
        _cancellation?.Cancel();
        Changed?.Invoke();
    }

    internal void Resume()
    {
        if (_disposed || IsRunning) return;
        foreach (var job in Jobs.Where(j => j.State == TextureImportState.Cancelled)) Reset(job);
        IsPaused = false;
        Changed?.Invoke();
        Start();
    }

    internal void RetryFailed()
    {
        if (_disposed) return;
        foreach (var job in Jobs.Where(j => j.State is TextureImportState.Failed or TextureImportState.Cancelled)) Reset(job);
        IsPaused = false;
        Changed?.Invoke();
        Start();
    }

    internal void ClearFinished()
    {
        _work.RemoveAll(w => w.Job.State is TextureImportState.Complete or TextureImportState.Failed or TextureImportState.Cancelled);
        Changed?.Invoke();
    }

    private static void Reset(TextureImportJob job)
    {
        job.State = TextureImportState.Queued;
        job.Percent = 0;
        job.Detail = "Ready to retry";
        job.OutputPath = "";
    }

    private void Start()
    {
        if (_disposed || IsRunning || IsPaused || !_work.Any(w => w.Job.State == TextureImportState.Queued)) return;
        IsRunning = true;
        Completion = RunAsync();
    }

    private async Task RunAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            while (!_disposed && !IsPaused && !cancellation.IsCancellationRequested &&
                   _work.FirstOrDefault(w => w.Job.State == TextureImportState.Queued) is { } work)
                await TextureImportQueue.RunAsync([work.Job], work.Import, _ =>
                {
                    if (!_disposed) Changed?.Invoke();
                }, cancellation.Token);
        }
        finally
        {
            _cancellation = null;
            IsRunning = false;
            if (!_disposed) Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cancellation?.Cancel();
        Changed = null;
    }
}

public sealed record TextureImportProgress(int Percent, string Detail);

/// <summary>Serial processing bounds encoder memory and isolates failures to one file.</summary>
internal static class TextureImportQueue
{
    internal static async Task RunAsync(IReadOnlyList<TextureImportJob> jobs,
        Func<TextureImportJob, IProgress<TextureImportProgress>, CancellationToken, Task<string>> import,
        Action<TextureImportJob> changed, CancellationToken cancellation)
    {
        foreach (var job in jobs.Where(j => j.State == TextureImportState.Queued))
        {
            if (cancellation.IsCancellationRequested) break;
            job.State = TextureImportState.Importing;
            job.Detail = "Preparing texture";
            job.Percent = 0;
            changed(job);
            // Bound to the UI context; late notifications cannot overwrite terminal states.
            var progress = new Progress<TextureImportProgress>(p =>
            {
                if (job.State != TextureImportState.Importing) return;
                job.Percent = Math.Clamp(p.Percent, 0, 99);
                job.Detail = p.Detail;
                changed(job);
            });
            try
            {
                job.OutputPath = await import(job, progress, cancellation);
                job.State = TextureImportState.Complete;
                job.Percent = 100;
                job.Detail = "Imported and saved";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                job.State = TextureImportState.Cancelled;
                job.Detail = "Cancelled · retry when ready";
            }
            catch (Exception ex)
            {
                job.State = TextureImportState.Failed;
                job.Detail = ex.Message;
            }
            changed(job);
        }
    }
}
