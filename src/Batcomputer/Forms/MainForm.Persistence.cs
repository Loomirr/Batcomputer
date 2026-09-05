namespace Batcomputer;

/// <summary>Coordinates editor project saves across async UI operations.</summary>
public sealed partial class MainForm
{
    private sealed record CurrentProjectEditContext(
        NativeSuitProject Project,
        SuitProjectService Service,
        int SelectionGeneration,
        long OperationGeneration,
        string SlotId,
        string ProjectPath,
        SuitProjectService.ProjectSaveGenerationSnapshot StartingSaveGeneration);

    private long _currentProjectEditOperationGeneration;

    private sealed record CurrentProjectSaveCapture(
        CurrentProjectEditContext Context,
        SuitProjectService.ProjectSaveSnapshot Snapshot,
        string Operation);

    private sealed class ProjectSaveRollbackOwnership
    {
        public SuitProjectService.ProjectSaveSnapshot? LatestSave { get; set; }
        public bool SaveWasCommitted { get; set; }
    }

    /// <summary>
    /// Signals that an immutable save was intentionally not committed because a newer editor
    /// operation or a different suit/workspace now owns the UI. Callers use this to restore any
    /// transaction-owned files without rolling the visible editor back to a stale project.
    /// </summary>
    private sealed class CurrentProjectSaveSupersededException(string message)
        : OperationCanceledException(message);

    private static bool ContainsCurrentProjectSaveSuperseded(Exception exception)
    {
        if (exception is CurrentProjectSaveSupersededException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(ContainsCurrentProjectSaveSuperseded);
        }

        return exception.InnerException is not null &&
               ContainsCurrentProjectSaveSuperseded(exception.InnerException);
    }

    private CurrentProjectEditContext CaptureCurrentProjectEditContext(
        NativeSuitProject project,
        string? projectRootOverride = null)
    {
        var projectRoot = string.IsNullOrWhiteSpace(projectRootOverride)
            ? _projectRootText.Text.Trim()
            : projectRootOverride.Trim();
        var service = ResolveProjectServiceForRoot(_projectService, projectRoot);
        _projectService = service;
        var operationGeneration = Interlocked.Increment(ref _currentProjectEditOperationGeneration);
        var startingSaveGeneration = service.CaptureProjectSaveGeneration(project.SlotId);
        return new CurrentProjectEditContext(
            project,
            service,
            _loadedProjectSelectionGeneration,
            operationGeneration,
            project.SlotId,
            service.ProjectPathForSlot(project.SlotId),
            startingSaveGeneration);
    }

    private CurrentProjectSaveCapture CaptureCurrentProjectSave(
        NativeSuitProject project,
        string operation,
        string? projectRootOverride = null) =>
        CaptureCurrentProjectSave(
            CaptureCurrentProjectEditContext(project, projectRootOverride),
            operation);

    private CurrentProjectSaveCapture CaptureCurrentProjectSave(
        CurrentProjectEditContext context,
        string operation)
    {
        // Reject stale UI work before CaptureProjectSave publishes a new path generation. Without
        // this synchronous guard, an older operation that resumes after a newer edit could announce
        // a higher sequence and supersede the newer edit even though its own commit is rejected.
        if (!CurrentProjectEditContextMatches(context))
        {
            throw new CurrentProjectSaveSupersededException(
                $"Could not {operation} because another suit or workspace operation superseded it.");
        }

        var captureResult = context.Service.CaptureProjectSaveIfCurrent(
            context.Project,
            context.StartingSaveGeneration,
            () => CurrentProjectEditContextMatches(context));
        if (captureResult.Snapshot is null)
        {
            throw new CurrentProjectSaveSupersededException(
                captureResult.RejectedByContext
                    ? $"Could not {operation} because another suit or workspace operation superseded it."
                    : $"Could not {operation} because a newer project save superseded it before capture.");
        }

        return new CurrentProjectSaveCapture(context, captureResult.Snapshot, operation);
    }

    private bool CurrentProjectEditContextMatches(CurrentProjectEditContext context) =>
        context.OperationGeneration == Volatile.Read(ref _currentProjectEditOperationGeneration) &&
        ProjectSaveContextMatches(
            context.Project,
            _currentProject,
            context.SelectionGeneration,
            _loadedProjectSelectionGeneration,
            context.Service,
            _projectService,
            context.SlotId,
            context.ProjectPath);

    private sealed class RebuildGateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private static async Task<IDisposable> EnterRebuildTransactionAsync()
    {
        await RebuildGate.WaitAsync();
        return new RebuildGateLease(RebuildGate);
    }

    private bool CurrentProjectSaveContextMatches(CurrentProjectSaveCapture capture) =>
        CurrentProjectEditContextMatches(capture.Context) &&
        Path.GetFullPath(capture.Context.ProjectPath).Equals(
            Path.GetFullPath(capture.Snapshot.Path),
            StringComparison.OrdinalIgnoreCase);

    private async Task<SuitProjectService.ProjectSaveCommitResult> CommitCurrentProjectSaveCaptureAsync(
        CurrentProjectSaveCapture capture)
    {
        var result = await RunWithFileLockRetryAsync(
            () => capture.Context.Service.CommitProjectSave(
                capture.Snapshot,
                () => CurrentProjectSaveContextMatches(capture)),
            capture.Operation);
        if (result.RejectedByContext)
        {
            AppendLog($"  {capture.Operation} stopped because another suit or workspace was selected while it was waiting.");
        }
        else if (result.Superseded)
        {
            AppendLog($"  {capture.Operation} stopped because a newer save already owns this suit project.");
        }
        return result;
    }

    private static void RequireCurrentProjectSaveCommitted(
        SuitProjectService.ProjectSaveCommitResult result,
        string operation)
    {
        if (result.Written)
        {
            return;
        }

        throw new CurrentProjectSaveSupersededException(
            result.RejectedByContext
                ? $"Could not {operation} because another suit or workspace was selected."
                : $"Could not {operation} because a newer save superseded this operation.");
    }

    private async Task<SuitProjectService.ProjectSaveCommitResult> SaveCurrentProjectSnapshotAsync(
        NativeSuitProject project,
        string operation,
        string? projectRootOverride = null)
    {
        // Serialization happens here, on the UI thread, before Task.Run or a retry can yield.
        var capture = CaptureCurrentProjectSave(project, operation, projectRootOverride);
        return await CommitCurrentProjectSaveCaptureAsync(capture);
    }

    private static SuitProjectService ResolveProjectServiceForRoot(
        SuitProjectService? current,
        string projectRoot)
    {
        if (current is not null && PathsEqual(current.ProjectRoot, projectRoot))
        {
            return current;
        }
        return new SuitProjectService(projectRoot);
    }

    private static bool ProjectSaveContextMatches(
        NativeSuitProject expectedProject,
        NativeSuitProject? currentProject,
        int expectedSelectionGeneration,
        int currentSelectionGeneration,
        SuitProjectService expectedService,
        SuitProjectService? currentService,
        string expectedSlotId,
        string expectedPath)
    {
        if (!ReferenceEquals(expectedProject, currentProject) ||
            expectedSelectionGeneration != currentSelectionGeneration ||
            !ReferenceEquals(expectedService, currentService) ||
            !string.Equals(expectedProject.SlotId, expectedSlotId, StringComparison.Ordinal))
        {
            return false;
        }

        return Path.GetFullPath(expectedService.ProjectPathForSlot(expectedProject.SlotId))
            .Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static SuitProjectService ResolveProjectServiceForRootForTest(
        SuitProjectService? current,
        string projectRoot) => ResolveProjectServiceForRoot(current, projectRoot);

    internal static bool ProjectSaveContextMatchesForTest(
        NativeSuitProject expectedProject,
        NativeSuitProject? currentProject,
        int expectedSelectionGeneration,
        int currentSelectionGeneration,
        SuitProjectService expectedService,
        SuitProjectService? currentService,
        string expectedSlotId,
        string expectedPath) => ProjectSaveContextMatches(
            expectedProject,
            currentProject,
            expectedSelectionGeneration,
            currentSelectionGeneration,
            expectedService,
            currentService,
            expectedSlotId,
            expectedPath);
}
