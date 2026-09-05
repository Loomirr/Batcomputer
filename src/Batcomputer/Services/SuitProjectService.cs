using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Batcomputer;

public sealed class SuitProjectService
{
    public sealed record ProjectSaveSnapshot(
        string Path,
        string SlotId,
        string Json,
        long Sequence);

    public sealed record ProjectSaveCommitResult(
        string Path,
        bool Written,
        bool Superseded,
        bool RejectedByContext);

    public sealed record ProjectSaveGenerationSnapshot(
        string Path,
        string SlotId,
        long LastCommittedSequence,
        long LastCapturedSequence);

    public sealed record ProjectSaveCaptureResult(
        ProjectSaveSnapshot? Snapshot,
        bool Superseded,
        bool RejectedByContext);

    public sealed record ProjectFileRollbackSnapshot(
        string Path,
        string SlotId,
        bool Existed,
        string? Contents,
        string Fingerprint,
        long LastCommittedSequence,
        long LastCapturedSequence);

    public sealed record ProjectFileRestoreResult(
        string Path,
        bool Restored,
        bool Superseded,
        bool RejectedByContext,
        long CapturedOwnershipSequence = 0,
        long CommittedOwnershipSequence = 0,
        string RestoredFingerprint = "");

    private sealed class ProjectSaveState
    {
        public object Gate { get; } = new();
        public long LastCommittedSequence { get; set; }
        public long LastCapturedSequence { get; set; }
    }

    private static readonly ConcurrentDictionary<string, ProjectSaveState> ProjectSaveStates =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _nextProjectSaveSequence;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public sealed record ProjectSummary(
        string SlotId,
        string DisplayName,
        string Path,
        DateTime Modified,
        string CoverImagePath,
        string TargetPlayablePath);

    /// <summary>Lists saved suit projects (newest first).</summary>
    public IReadOnlyList<ProjectSummary> ListProjects()
    {
        return ListProjectFiles()
            .OrderByDescending(project => project.Modified)
            .GroupBy(project => string.IsNullOrWhiteSpace(project.TargetPlayablePath)
                    ? "slot:" + project.SlotId
                    : "target:" + project.TargetPlayablePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// Lists every readable project file without collapsing stale aliases. The normal
    /// project picker uses <see cref="ListProjects"/>; deletion uses this list so an
    /// older file cannot reappear after its newer replacement is removed.
    /// </summary>
    public IReadOnlyList<ProjectSummary> ListProjectFiles()
    {
        var results = new List<ProjectSummary>();
        if (!Directory.Exists(GuiOutputRoot))
        {
            return results;
        }

        foreach (var path in Directory.EnumerateFiles(GuiOutputRoot, "*.native-suit-project.json"))
        {
            var slot = System.IO.Path.GetFileName(path).Replace(".native-suit-project.json", "");
            var display = slot;
            var cover = "";
            var targetPlayable = "";
            try
            {
                var project = JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(path), JsonOptions);
                if (project is not null && string.IsNullOrWhiteSpace(project.DisplayName))
                {
                    continue;
                }
                if (project is not null)
                {
                    display = project.DisplayName;
                    slot = project.SlotId;
                    cover = project.CoverImagePath ?? "";
                    targetPlayable = UnrealPathUtil.NormalizePackagePath(project.TargetPackages?.Playable);
                }
            }
            catch
            {
                // Keep a corrupt file visible by filename so it can still be removed from Home.
            }
            results.Add(new ProjectSummary(slot, display, path, File.GetLastWriteTime(path), cover, targetPlayable));
        }

        return results;
    }

    /// <summary>Finds every saved alias that generates the same playable package.</summary>
    public IReadOnlyList<ProjectSummary> FindProjectAliases(NativeSuitProject project)
    {
        var target = UnrealPathUtil.NormalizePackagePath(project.TargetPackages?.Playable);
        if (string.IsNullOrWhiteSpace(target))
        {
            return Array.Empty<ProjectSummary>();
        }

        return ListProjectFiles()
            .Where(summary => target.Equals(summary.TargetPlayablePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public NativeSuitProject? LoadProject(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var project = JsonSerializer.Deserialize<NativeSuitProject>(File.ReadAllText(path), JsonOptions);
        if (project is not null)
        {
            project.ProgressTag = NativeMetadataDonorService.CanonicalProgressTag(project.ProgressTag);
            HeldItemService.Migrate(project.AbilityLoadout);
            // Early body/visual-base builds could mistake gameplay SCS nodes for cosmetic parts.
            // Repair only declarations carrying the tool's exact legacy auto-hide provenance;
            // explicit hand-authored removals remain visible for validation to reject.
            GameplayShellComponentPolicy.RemoveLegacyAutomaticRemovals(project);

            // Absolute extract paths are local cache details, not part of a suit's identity. When
            // an old dump has been replaced, keep the same /Game packages and migrate only their
            // disk locations in memory. Loading is deliberately read-only: an asynchronously loaded
            // stale object must never overwrite a newer editor save. The next explicit project save
            // persists the repaired local paths together with the user's current recipe.
            RefreshSavedTemplateSources(project, AppSettings.Current.EffectiveExtractedContentRoot());
        }
        return project;
    }

    public string ProjectRoot { get; }
    public string GuiOutputRoot => Path.Combine(AppSettings.GeneratedRootFor(ProjectRoot), "NativeSuitGuiProjects");

    public SuitProjectService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public string SaveProject(NativeSuitProject project)
    {
        var snapshot = CaptureProjectSave(project);
        var result = CommitProjectSave(snapshot);
        if (!result.Written)
        {
            throw new InvalidOperationException(
                "The suit project was not saved because a newer captured save already owns this path.");
        }
        return result.Path;
    }

    /// <summary>
    /// Captures an immutable project document on the calling thread. Async callers must do this
    /// before yielding so a later UI edit or suit selection cannot change what their queued save
    /// eventually serializes.
    /// </summary>
    public ProjectSaveSnapshot CaptureProjectSave(NativeSuitProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.ProgressTag = NativeMetadataDonorService.CanonicalProgressTag(project.ProgressTag);
        HeldItemService.Migrate(project.AbilityLoadout);
        // Persist the load-time migration even when a caller constructed or retained an older
        // project object without loading it through LoadProject. These exact auto-hide rules were
        // emitted by Batcomputer itself; explicit/manual protected removals remain for validation
        // to reject instead of being erased silently.
        GameplayShellComponentPolicy.RemoveLegacyAutomaticRemovals(project);
        Directory.CreateDirectory(GuiOutputRoot);
        var path = Path.GetFullPath(ProjectPathForSlot(project.SlotId));
        var json = JsonSerializer.Serialize(project, JsonOptions);
        var sequence = Interlocked.Increment(ref _nextProjectSaveSequence);
        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            // Sequence allocation and state publication are deliberately separate. Math.Max keeps
            // a temporarily-paused older capture from replacing the newest announced intent.
            state.LastCapturedSequence = Math.Max(state.LastCapturedSequence, sequence);
        }
        return new ProjectSaveSnapshot(path, project.SlotId, json, sequence);
    }

    public ProjectSaveGenerationSnapshot CaptureProjectSaveGeneration(string slotId)
    {
        var path = Path.GetFullPath(ProjectPathForSlot(slotId));
        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            return new ProjectSaveGenerationSnapshot(
                path,
                slotId,
                state.LastCommittedSequence,
                state.LastCapturedSequence);
        }
    }

    /// <summary>
    /// Captures and publishes an immutable save only while the project path still has the same save
    /// generations observed when the editor operation began. This closes the gap between an older
    /// UI continuation checking its selection and announcing a sequence over a newer direct save.
    /// </summary>
    public ProjectSaveCaptureResult CaptureProjectSaveIfCurrent(
        NativeSuitProject project,
        ProjectSaveGenerationSnapshot expectedGeneration,
        Func<bool>? contextIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(expectedGeneration);
        GameplayShellComponentPolicy.RemoveLegacyAutomaticRemovals(project);
        Directory.CreateDirectory(GuiOutputRoot);
        var path = Path.GetFullPath(ProjectPathForSlot(project.SlotId));
        if (!path.Equals(Path.GetFullPath(expectedGeneration.Path), StringComparison.OrdinalIgnoreCase) ||
            !project.SlotId.Equals(expectedGeneration.SlotId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refused to capture a suit project through a different workspace or slot path.");
        }

        // Freeze the document before it can be handed to background I/O. Publication remains under
        // the per-path gate below, after both the UI context and starting generations are checked.
        var json = JsonSerializer.Serialize(project, JsonOptions);
        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            if (contextIsCurrent is not null && !contextIsCurrent())
            {
                return new ProjectSaveCaptureResult(null, Superseded: false, RejectedByContext: true);
            }
            if (state.LastCapturedSequence != expectedGeneration.LastCapturedSequence ||
                state.LastCommittedSequence != expectedGeneration.LastCommittedSequence)
            {
                return new ProjectSaveCaptureResult(null, Superseded: true, RejectedByContext: false);
            }

            var sequence = Interlocked.Increment(ref _nextProjectSaveSequence);
            state.LastCapturedSequence = sequence;
            return new ProjectSaveCaptureResult(
                new ProjectSaveSnapshot(path, project.SlotId, json, sequence),
                Superseded: false,
                RejectedByContext: false);
        }
    }

    /// <summary>
    /// Materializes the immutable document captured for a save. Derived artifacts such as patch
    /// plans must use this copy rather than retaining the live editor object across an await.
    /// </summary>
    public NativeSuitProject MaterializeProjectSaveSnapshot(ProjectSaveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = Path.GetFullPath(snapshot.Path);
        var expectedPath = Path.GetFullPath(ProjectPathForSlot(snapshot.SlotId));
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refused to materialize a captured suit project through a different workspace or slot path.");
        }

        return JsonSerializer.Deserialize<NativeSuitProject>(snapshot.Json, JsonOptions)
               ?? throw new InvalidOperationException("The captured suit project document was empty.");
    }

    /// <summary>
    /// Commits a previously captured document under a process-wide per-path gate. A delayed older
    /// snapshot cannot replace a newer snapshot that has already committed. The optional context
    /// predicate lets the editor reject a save after the user selected another suit or workspace.
    /// </summary>
    public ProjectSaveCommitResult CommitProjectSave(
        ProjectSaveSnapshot snapshot,
        Func<bool>? contextIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = Path.GetFullPath(snapshot.Path);
        var expectedPath = Path.GetFullPath(ProjectPathForSlot(snapshot.SlotId));
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refused to commit a captured suit project through a different workspace or slot path.");
        }
        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            if (contextIsCurrent is not null && !contextIsCurrent())
            {
                return new ProjectSaveCommitResult(path, Written: false, Superseded: false, RejectedByContext: true);
            }

            if (snapshot.Sequence < state.LastCapturedSequence ||
                snapshot.Sequence <= state.LastCommittedSequence)
            {
                return new ProjectSaveCommitResult(path, Written: false, Superseded: true, RejectedByContext: false);
            }

            AtomicFileUtil.WriteAllText(path, snapshot.Json);
            state.LastCommittedSequence = snapshot.Sequence;
            return new ProjectSaveCommitResult(path, Written: true, Superseded: false, RejectedByContext: false);
        }
    }

    /// <summary>
    /// Captures the project file and its coordinated save generations. Stage transactions use this
    /// instead of copying raw JSON so rollback can prove that no newer editor save owns the path.
    /// </summary>
    public ProjectFileRollbackSnapshot CaptureProjectFileRollback(string slotId)
    {
        var path = Path.GetFullPath(ProjectPathForSlot(slotId));
        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            var existed = File.Exists(path);
            var contents = existed ? File.ReadAllText(path) : null;
            return new ProjectFileRollbackSnapshot(
                path,
                slotId,
                existed,
                contents,
                ProjectFileFingerprint(existed, contents),
                state.LastCommittedSequence,
                state.LastCapturedSequence);
        }
    }

    /// <summary>
    /// Restores a stage transaction's original project file only while that transaction still owns
    /// both the latest captured intent and the last committed document. A newer pending capture is
    /// enough to reject rollback, preventing an older failure from erasing a save that is waiting on
    /// a file lock. The current-file fingerprint also detects writes made outside this service.
    /// </summary>
    public ProjectFileRestoreResult TryRestoreProjectFile(
        ProjectFileRollbackSnapshot rollback,
        ProjectSaveSnapshot? latestOwnedSave = null,
        bool ownedSaveWasCommitted = false,
        Func<bool>? contextIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        if (ownedSaveWasCommitted && latestOwnedSave is null)
        {
            throw new ArgumentException(
                "A committed rollback owner must include its captured project save.",
                nameof(latestOwnedSave));
        }

        var path = Path.GetFullPath(rollback.Path);
        var expectedPath = Path.GetFullPath(ProjectPathForSlot(rollback.SlotId));
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) ||
            (latestOwnedSave is not null &&
             (!Path.GetFullPath(latestOwnedSave.Path).Equals(path, StringComparison.OrdinalIgnoreCase) ||
              !latestOwnedSave.SlotId.Equals(rollback.SlotId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                "Refused to restore a captured suit project through a different workspace or slot path.");
        }

        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            if (contextIsCurrent is not null && !contextIsCurrent())
            {
                return new ProjectFileRestoreResult(path, Restored: false, Superseded: false, RejectedByContext: true);
            }

            var expectedCapturedSequence = latestOwnedSave?.Sequence ?? rollback.LastCapturedSequence;
            var expectedCommittedSequence = ownedSaveWasCommitted
                ? latestOwnedSave!.Sequence
                : rollback.LastCommittedSequence;
            var currentExists = File.Exists(path);
            var currentContents = currentExists ? File.ReadAllText(path) : null;
            var expectedFingerprint = ownedSaveWasCommitted
                ? ProjectFileFingerprint(existed: true, latestOwnedSave!.Json)
                : rollback.Fingerprint;
            if (state.LastCapturedSequence != expectedCapturedSequence ||
                state.LastCommittedSequence != expectedCommittedSequence ||
                !ProjectFileFingerprint(currentExists, currentContents)
                    .Equals(expectedFingerprint, StringComparison.Ordinal))
            {
                return new ProjectFileRestoreResult(path, Restored: false, Superseded: true, RejectedByContext: false);
            }

            if (latestOwnedSave is null)
            {
                // The transaction never announced or committed a project edit. The fingerprint
                // above proves the original document is already present, so leave generations
                // untouched. This lets nested stage-only snapshots compose with their outer
                // transaction instead of manufacturing a conflicting project save.
                return new ProjectFileRestoreResult(
                    path,
                    Restored: true,
                    Superseded: false,
                    RejectedByContext: false,
                    state.LastCapturedSequence,
                    state.LastCommittedSequence,
                    rollback.Fingerprint);
            }

            if (rollback.Existed)
            {
                AtomicFileUtil.WriteAllText(path, rollback.Contents ?? string.Empty);
            }
            else
            {
                File.Delete(path);
            }

            // A successful rollback is itself a new authoritative generation. Advancing both
            // counters prevents any already-queued pre-rollback capture from committing later.
            var rollbackSequence = Interlocked.Increment(ref _nextProjectSaveSequence);
            state.LastCapturedSequence = rollbackSequence;
            state.LastCommittedSequence = rollbackSequence;
            return new ProjectFileRestoreResult(
                path,
                Restored: true,
                Superseded: false,
                RejectedByContext: false,
                rollbackSequence,
                rollbackSequence,
                rollback.Fingerprint);
        }
    }

    /// <summary>
    /// Confirms that a successful rollback still owns the project path. Call this immediately before
    /// re-certifying a restored stage; a manual save made during directory restoration invalidates
    /// the certification even though it correctly remains the authoritative project document.
    /// </summary>
    public bool ProjectFileRestoreStillCurrent(ProjectFileRestoreResult restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        if (!restore.Restored)
        {
            return false;
        }

        var path = Path.GetFullPath(restore.Path);
        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            var exists = File.Exists(path);
            var contents = exists ? File.ReadAllText(path) : null;
            return state.LastCapturedSequence == restore.CapturedOwnershipSequence &&
                   state.LastCommittedSequence == restore.CommittedOwnershipSequence &&
                   ProjectFileFingerprint(exists, contents)
                       .Equals(restore.RestoredFingerprint, StringComparison.Ordinal);
        }
    }

    public bool RunIfProjectFileRestoreStillCurrent(
        ProjectFileRestoreResult restore,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(restore);
        if (!restore.Restored)
        {
            return false;
        }

        var path = Path.GetFullPath(restore.Path);
        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            var exists = File.Exists(path);
            var contents = exists ? File.ReadAllText(path) : null;
            if (state.LastCapturedSequence != restore.CapturedOwnershipSequence ||
                state.LastCommittedSequence != restore.CommittedOwnershipSequence ||
                !ProjectFileFingerprint(exists, contents)
                    .Equals(restore.RestoredFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            // Keep stage certification and project-save ownership indivisible. A save captured or
            // committed after this callback starts waits until the restored marker is in place.
            action();
            return true;
        }
    }

    /// <summary>
    /// Runs stage certification only while the exact immutable save that produced the stage is
    /// still the newest captured and committed document for this project path. Holding the same
    /// per-path gate across the callback prevents another in-process save from landing between the
    /// ownership check and removal of the fail-closed stage marker.
    /// </summary>
    public bool RunIfProjectSaveStillCurrent(
        ProjectSaveSnapshot snapshot,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(action);
        var path = Path.GetFullPath(snapshot.Path);
        var expectedPath = Path.GetFullPath(ProjectPathForSlot(snapshot.SlotId));
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refused to certify a generated stage through a different workspace or slot path.");
        }

        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            var exists = File.Exists(path);
            var contents = exists ? File.ReadAllText(path) : null;
            if (state.LastCapturedSequence != snapshot.Sequence ||
                state.LastCommittedSequence != snapshot.Sequence ||
                !ProjectFileFingerprint(exists, contents)
                    .Equals(ProjectFileFingerprint(existed: true, snapshot.Json), StringComparison.Ordinal))
            {
                return false;
            }

            action();
            return true;
        }
    }

    /// <summary>
    /// Runs stage certification only while an unchanged on-disk project snapshot still owns the
    /// path. Loaded-project replay uses this read-only guard so it cannot certify an older loaded
    /// object after another save has changed (or merely announced a change to) the same project.
    /// </summary>
    public bool RunIfProjectFileSnapshotStillCurrent(
        ProjectFileRollbackSnapshot snapshot,
        Action action,
        Func<bool>? contextIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(action);
        var path = Path.GetFullPath(snapshot.Path);
        var expectedPath = Path.GetFullPath(ProjectPathForSlot(snapshot.SlotId));
        if (!path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refused to certify a generated stage through a different workspace or slot path.");
        }

        var state = ProjectSaveStates.GetOrAdd(path, static _ => new ProjectSaveState());
        lock (state.Gate)
        {
            if (contextIsCurrent is not null && !contextIsCurrent())
            {
                return false;
            }

            var exists = File.Exists(path);
            var contents = exists ? File.ReadAllText(path) : null;
            if (state.LastCapturedSequence != snapshot.LastCapturedSequence ||
                state.LastCommittedSequence != snapshot.LastCommittedSequence ||
                !ProjectFileFingerprint(exists, contents)
                    .Equals(snapshot.Fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            action();
            return true;
        }
    }

    private static string ProjectFileFingerprint(bool existed, string? contents)
    {
        if (!existed)
        {
            return "missing";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(contents ?? string.Empty));
        return "sha256:" + Convert.ToHexString(bytes);
    }

    public string ProjectPathForSlot(string slotId) =>
        Path.Combine(GuiOutputRoot, $"{MakeSafeFileName(slotId)}.native-suit-project.json");

    public void DeleteSavedProjectFile(string projectPath)
    {
        var root = Path.GetFullPath(GuiOutputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(projectPath);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".native-suit-project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refused to delete a project outside the tool's saved-project folder.");
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    public string SavePatchPlan(NativeSuitPatchPlan plan)
    {
        Directory.CreateDirectory(GuiOutputRoot);
        var safeSlot = MakeSafeFileName(plan.Project.SlotId);
        var path = Path.Combine(GuiOutputRoot, $"{safeSlot}.patch-plan.json");
        AtomicFileUtil.WriteAllText(path, JsonSerializer.Serialize(plan, JsonOptions));
        return path;
    }

    public string ProjectOutputDirectory(NativeSuitProject project) =>
        Path.Combine(GuiOutputRoot, MakeSafeFileName(project.SlotId));

    /// <summary>
    /// Deletes only the saved project JSON and its project-owned generated
    /// directory. Imported source files outside the project directory are left
    /// alone because they may be shared by another suit.
    /// </summary>
    public void DeleteProjectFromTool(string projectPath, NativeSuitProject project)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!FileSystemPathUtil.IsWithinDirectory(fullProjectPath, GuiOutputRoot) ||
            !fullProjectPath.EndsWith(".native-suit-project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refused to delete a project outside the tool's saved-project folder.");
        }

        if (File.Exists(fullProjectPath))
        {
            File.Delete(fullProjectPath);
        }

        var projectDir = Path.GetFullPath(ProjectOutputDirectory(project));
        if (FileSystemPathUtil.IsWithinDirectory(projectDir, GuiOutputRoot) && Directory.Exists(projectDir))
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    public string CreateUnpatchedStage(NativeSuitProject project)
    {
        var stageRoot = Path.Combine(ProjectOutputDirectory(project), "UnpatchedStage", "LEGOBatmanLotDK", "Content");
        CopyPackagePair(EffectiveCharacterTemplate(project, playable: true), project.TargetPackages.Playable, stageRoot);
        CopyPackagePair(EffectiveCharacterTemplate(project, playable: false), project.TargetPackages.Cutscene, stageRoot);
        CopyPackagePair(project.DcmdTemplate, project.TargetPackages.Dcmd, stageRoot);

        // Stage the donor archetype for the name-map clone pass.
        var customArchetypePkg = UAssetPatchService.CustomArchetypePackage(project);
        if (customArchetypePkg is not null)
        {
            CopyArchetypeDonor(
                UAssetPatchService.StageArchetypeDonorPackage(project),
                customArchetypePkg,
                stageRoot);
        }

        return stageRoot;
    }

    private static TemplateRecord? EffectiveCharacterTemplate(NativeSuitProject project, bool playable)
    {
        var fallback = playable ? project.PlayableTemplate : project.CutsceneTemplate;
        if (!GliderService.TryGetAuthoredPairedCapeShell(
                project,
                out var shellPlayable,
                out var shellCutscene,
                out var shellDetail))
        {
            if (project.PairedCapeAdapter is not null)
            {
                throw new InvalidOperationException(
                    "The declared paired-cape adapter could not resolve its certified authored scaffold. " +
                    "Batcomputer refused to stage the glide-only base as a fallback because its cooked component layout cannot safely host the Cape + Torso pair. " +
                    shellDetail);
            }
            return fallback;
        }

        var package = playable ? shellPlayable : shellCutscene;
        if (UnrealPathUtil.NormalizePackagePath(fallback?.PackagePath ?? "").Equals(
                package,
                StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var sourceBase = ExtractedPackagePathService.ResolvePackageBase(extractedRoot, package)
            ?? throw new InvalidOperationException(
                $"Authored paired-cape shell is not available in the active game or installed DLC extract: '{package}'.");
        var uasset = sourceBase + ".uasset";
        if (!File.Exists(uasset))
        {
            throw new FileNotFoundException(
                $"The authored paired-cape { (playable ? "playable" : "cutscene") } shell is not present in the active extract.",
                uasset);
        }

        var uexp = sourceBase + ".uexp";
        var ubulk = sourceBase + ".ubulk";
        return new TemplateRecord
        {
            PackagePath = package,
            ContentRelative = ExtractedPackagePathService.ContentRelativeFromFile(extractedRoot, uasset) ?? "",
            Stem = UnrealPathUtil.AssetName(package),
            Character = fallback?.Character ?? "",
            Role = playable ? "playable" : "cutscene",
            Uasset = uasset,
            Uexp = File.Exists(uexp) ? uexp : null,
            Ubulk = File.Exists(ubulk) ? ubulk : null,
            UassetLength = new FileInfo(uasset).Length,
            UexpLength = File.Exists(uexp) ? new FileInfo(uexp).Length : 0,
            HasSplitPair = File.Exists(uexp),
            HasPair = File.Exists(uexp),
        };
    }

    private static void CopyArchetypeDonor(
        string sourcePackagePath,
        string targetPackagePath,
        string stageContentRoot)
    {
        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var donorBase = ExtractedPackagePathService.ResolvePackageBase(extractedRoot, sourcePackagePath);
        if (string.IsNullOrWhiteSpace(donorBase))
        {
            return;
        }
        if (!File.Exists(donorBase + ".uasset"))
        {
            return; // donor not extracted — request will fail gracefully downstream
        }

        var targetRel = GamePackageRelativePath(targetPackagePath);
        if (targetRel is null)
        {
            return;
        }

        var targetBase = Path.Combine(stageContentRoot, targetRel);
        Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
        File.Copy(donorBase + ".uasset", targetBase + ".uasset", overwrite: true);
        if (File.Exists(donorBase + ".uexp"))
        {
            File.Copy(donorBase + ".uexp", targetBase + ".uexp", overwrite: true);
        }
        if (File.Exists(donorBase + ".ubulk"))
        {
            File.Copy(donorBase + ".ubulk", targetBase + ".ubulk", overwrite: true);
        }
    }

    private static void CopyPackagePair(TemplateRecord? record, string targetPackagePath, string stageContentRoot)
    {
        if (record is null)
        {
            return;
        }

        targetPackagePath = UnrealPathUtil.NormalizePackagePath(targetPackagePath);
        var targetRel = GamePackageRelativePath(targetPackagePath);
        if (targetRel is null)
        {
            throw new InvalidOperationException(
                $"Target package path for {record.Role} must start with /Game/. Current value: '{targetPackagePath}'.");
        }

        if (!TryRefreshTemplateSource(
                record,
                AppSettings.Current.EffectiveExtractedContentRoot(),
                out _))
        {
            var role = string.IsNullOrWhiteSpace(record.Role) ? "base" : record.Role;
            var package = UnrealPathUtil.NormalizePackagePath(record.PackagePath);
            var identity = string.IsNullOrWhiteSpace(package)
                ? (string.IsNullOrWhiteSpace(record.ContentRelative) ? record.Stem : record.ContentRelative)
                : package;
            throw new FileNotFoundException(
                $"The saved {role} package '{identity}' is not present in the active extracted Content folder. " +
                "Refresh character assets, then open Base and re-select this suit's visual base and gameplay donor. " +
                "The saved project has not been replaced.");
        }

        var targetBase = Path.Combine(stageContentRoot, targetRel);
        Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
        File.Copy(record.Uasset, targetBase + ".uasset", overwrite: true);
        if (!string.IsNullOrWhiteSpace(record.Uexp) && File.Exists(record.Uexp))
        {
            File.Copy(record.Uexp, targetBase + ".uexp", overwrite: true);
        }
        if (!string.IsNullOrWhiteSpace(record.Ubulk) && File.Exists(record.Ubulk))
        {
            File.Copy(record.Ubulk, targetBase + ".ubulk", overwrite: true);
        }
    }

    /// <summary>
    /// Moves saved template records from a retired extract folder to the currently configured
    /// Content root. The Unreal package path remains authoritative, so this never guesses a
    /// different character merely because its old absolute path disappeared.
    /// </summary>
    private static bool RefreshSavedTemplateSources(NativeSuitProject project, string activeContentRoot)
    {
        var changed = false;
        foreach (var record in new[]
                 {
                     project.PlayableTemplate,
                     project.CutsceneTemplate,
                     project.DcmdTemplate,
                     project.VisualSourceTemplate,
                     project.VisualCutsceneSourceTemplate,
                     project.StaticMeshComponentShapeTemplate,
                 })
        {
            if (record is not null && TryRefreshTemplateSource(record, activeContentRoot, out var recordChanged))
            {
                changed |= recordChanged;
            }
        }
        return changed;
    }

    internal static bool RefreshTemplateSourceForTest(TemplateRecord record, string activeContentRoot) =>
        TryRefreshTemplateSource(record, activeContentRoot, out _);

    private static bool TryRefreshTemplateSource(
        TemplateRecord record,
        string activeContentRoot,
        out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(activeContentRoot) || !Directory.Exists(activeContentRoot))
        {
            return !string.IsNullOrWhiteSpace(record.Uasset) && File.Exists(record.Uasset);
        }

        var contentRoot = Path.GetFullPath(activeContentRoot);
        var savedPackage = UnrealPathUtil.NormalizePackagePath(record.PackagePath);
        var sourceBase = ExtractedPackagePathService.ResolvePackageBase(contentRoot, savedPackage);
        if (string.IsNullOrWhiteSpace(sourceBase))
        {
            // ContentRelative predates mount-aware records. It is safe only when the record has no
            // package identity (legacy) or explicitly belongs to /Game. Falling back by this
            // mountless relative path for a missing Game Feature could silently bind the suit to a
            // different base-game asset with the same Characters/... path.
            if (!string.IsNullOrWhiteSpace(savedPackage) &&
                !savedPackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var relative = NormalizeContentRelative(record.ContentRelative) ??
                           ContentRelativeFromSavedPath(record.Uasset);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return false;
            }

            sourceBase = Path.GetFullPath(Path.Combine(contentRoot, relative));
            if (!FileSystemPathUtil.IsWithinDirectory(sourceBase, contentRoot))
            {
                return false;
            }
        }

        var uasset = sourceBase + ".uasset";
        if (!File.Exists(uasset))
        {
            return false;
        }

        var uexp = sourceBase + ".uexp";
        var ubulk = sourceBase + ".ubulk";
        var normalizedRelative = ExtractedPackagePathService.ContentRelativeFromFile(contentRoot, uasset) ?? "";
        var package = ExtractedPackagePathService.PackagePathFromFile(contentRoot, uasset);
        if (string.IsNullOrWhiteSpace(package))
        {
            return false;
        }
        var resolvedUexp = File.Exists(uexp) ? uexp : null;
        var resolvedUbulk = File.Exists(ubulk) ? ubulk : null;

        changed = !string.Equals(record.Uasset, uasset, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.Uexp, resolvedUexp, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.Ubulk, resolvedUbulk, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.PackagePath, package, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(record.ContentRelative, normalizedRelative, StringComparison.OrdinalIgnoreCase);

        record.Uasset = uasset;
        record.Uexp = resolvedUexp;
        record.Ubulk = resolvedUbulk;
        record.PackagePath = package;
        record.ContentRelative = normalizedRelative;
        record.Stem = Path.GetFileName(sourceBase);
        record.UassetLength = new FileInfo(uasset).Length;
        record.UexpLength = resolvedUexp is null ? 0 : new FileInfo(resolvedUexp).Length;
        record.HasSplitPair = resolvedUexp is not null;
        record.HasPair = resolvedUexp is not null;
        return true;
    }

    private static string? NormalizeContentRelative(string? contentRelative)
    {
        if (string.IsNullOrWhiteSpace(contentRelative))
        {
            return null;
        }

        var relative = contentRelative.Trim().Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative["Content/".Length..];
        }
        if (relative.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[..^".uasset".Length];
        }
        return string.IsNullOrWhiteSpace(relative)
            ? null
            : relative.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? ContentRelativeFromSavedPath(string? savedUasset)
    {
        if (string.IsNullOrWhiteSpace(savedUasset))
        {
            return null;
        }

        var normalized = savedUasset.Replace('\\', '/');
        var markerIndex = normalized.LastIndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }
        return NormalizeContentRelative(normalized[(markerIndex + "/Content/".Length)..]);
    }

    private static string? GamePackageRelativePath(string? packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (string.IsNullOrWhiteSpace(normalized) ||
            !normalized.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
