using System.Text.Json;

namespace Batcomputer;

public sealed partial class MainForm
{
    private TextureImportQueueRunner? _textureImports;
    private bool _closingTextureQueue;

    private string TextureImportOwnerKey(NativeSuitProject project, string root) =>
        Path.GetFullPath(root) + "|" + project.SlotId + "|" + project.TargetPackages.Playable;

    private IEnumerable<string> ReservedTextureNames(NativeSuitProject project, string root) =>
        project.GeneratedTextures.Select(t => t.DisplayName).Concat((_textureImports?.Jobs ?? [])
            .Where(j => j.OwnerKey.Equals(TextureImportOwnerKey(project, root), StringComparison.OrdinalIgnoreCase))
            .Select(j => j.Name));

    private TextureImportQueueRunner TextureImports()
    {
        if (_textureImports is not null) return _textureImports;
        var queue = _textureImports = new TextureImportQueueRunner();
        queue.Changed += () =>
        {
            if (!IsDisposed && !Disposing) _yourCharacter.ShowTextureQueue(queue.Jobs, queue.IsRunning, queue.IsPaused);
        };
        _yourCharacter.TextureQueueToggleRequested += (_, _) =>
        {
            if (queue.IsRunning) queue.Pause(); else queue.Resume();
        };
        _yourCharacter.TextureQueueRetryRequested += (_, _) => queue.RetryFailed();
        _yourCharacter.TextureQueueClearRequested += (_, _) => queue.ClearFinished();
        FormClosing += async (_, e) =>
        {
            if (!queue.IsRunning) return;
            e.Cancel = true;
            if (_closingTextureQueue) return;
            _closingTextureQueue = true;
            queue.Pause();
            await queue.Completion;
            if (!IsDisposed) Close();
        };
        FormClosed += (_, _) => queue.Dispose();
        return queue;
    }

    private void EnqueueTextureImports(NativeSuitProject project, string root, string modFolder, IEnumerable<TextureImportJob> jobs)
    {
        var reserved = ReservedTextureNames(project, root).Select(MakeSafeTextureToken).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = jobs.ToArray();
        foreach (var job in added)
        {
            if (string.IsNullOrWhiteSpace(job.Name) || !reserved.Add(MakeSafeTextureToken(job.Name)))
                throw new InvalidOperationException($"A texture named '{job.Name}' already exists or is already queued. Choose another name.");
            job.OwnerKey = TextureImportOwnerKey(project, root);
            job.OwnerLabel = project.DisplayName;
        }
        var selection = _loadedProjectSelectionGeneration;
        TextureImports().Enqueue(added, (job, progress, token) => ImportQueuedTextureAsync(project, root, modFolder, selection, job, progress, token));
        AppendLog($"Added {added.Length} texture(s) to the queue for {project.DisplayName}. You can keep adding textures while imports run.");
    }

    private void ImportSingleTexture(NativeSuitProject project, string projectRoot, string modFolder, string source)
    {
        var reserved = ReservedTextureNames(project, projectRoot).Select(MakeSafeTextureToken).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = MakeSafeTextureToken(Path.GetFileNameWithoutExtension(source));
        var suggested = stem;
        for (var suffix = 2; reserved.Contains(suggested); suffix++) suggested = $"{stem}_{suffix}";
        var settings = PromptForTextureImportSettings(suggested, projectRoot);
        if (settings is null || string.IsNullOrWhiteSpace(settings.Value.Name)) return;
        var (name,kind,preset) = settings.Value;
        if (!ConfirmTextureProfileSafety(preset,"import this texture")) return;
        var job = new TextureImportJob
        {
            SourcePath=source, Name=name, Kind=kind,
            Profile=new(preset.Id,preset.Label,preset.Detail,preset.Safety==TextureProfileSafety.Verified,preset.ValidationNote)
        };
        EnqueueTextureImports(project, projectRoot, modFolder, [job]);
    }

    private async Task<string> ImportQueuedTextureAsync(NativeSuitProject project, string projectRoot, string modFolder, int selectionGeneration,
        TextureImportJob job, IProgress<TextureImportProgress> progress, CancellationToken cancellation)
    {
        void CheckProject()
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(project, _currentProject) || selectionGeneration != _loadedProjectSelectionGeneration ||
                !projectRoot.Equals(_projectRootText.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !modFolder.Equals(_modFolderText.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !job.OwnerKey.Equals(TextureImportOwnerKey(project, projectRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The active suit, workspace or output identity changed. Return to the intended suit and submit this texture again; no texture was attached to another suit.");
        }
        CheckProject();
        var preset = AvailableTextureCookPresets(projectRoot, job.Kind).FirstOrDefault(p => p.Id == job.Profile?.Id)
            ?? throw new InvalidOperationException("The selected cook profile is no longer available. Refresh game assets and retry.");
        if (project.GeneratedTextures.Any(t => t.DisplayName.Equals(job.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A texture named '{job.Name}' already exists. Choose another name.");
        var index = NextTextureSlotIndex(project);
        var package = TexturePackagePathFromUserName(preset.TemplateJson, job.Name, index, modFolder, project.SlotId, job.Kind);
        if (project.GeneratedTextures.Any(t => t.PackagePath.Equals(package,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This texture package already exists. Choose another name.");
        // A failed/cancelled attempt cannot overwrite an earlier recoverable source or cook.
        var output = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "TextureImports",
            MakeSafePackageBaseName(string.IsNullOrWhiteSpace(project.SlotId) ? "unsaved_suit" : project.SlotId),
            $"{MakeSafeTextureToken(job.Name)}_{index:00000}_{Guid.NewGuid():N}");
        AppendLog($"Texture queue: {Path.GetFileName(job.SourcePath)} → {job.Name} · {job.Kind} · {preset.Label}");
        List<GeneratedTextureEntry> entries;
        if (job.Kind == EquipmentUiTextureCatalog.ImportKind)
        {
            var packages = project.GeneratedTextures.Select(t => t.PackagePath).ToArray();
            var names = project.GeneratedTextures.Select(t => t.DisplayName).ToArray();
            progress.Report(new(5,"Cooking equipment artwork and distance field"));
            entries = await Task.Run(() => EquipmentIconPairCookService.Cook(projectRoot, job.SourcePath, package,
                job.Name, output, packages, names, progress, cancellation));
        }
        else
        {
            progress.Report(new(1,"Saving source PNG"));
            var source = CopyTextureSourceIntoOutput(job.SourcePath, output);
            var result = await Task.Run(() => new TextureCookService(projectRoot).Cook(new()
            {
                SourceImagePath=source, TemplateJsonPath=preset.TemplateJson,
                OutputContentRoot=Path.Combine(output,"Cooked","LEGOBatmanLotDK","Content"), OutputPackagePath=package,
                NearestNeighborMips=UseNearestNeighborMipsForTextureKind(job.Kind,preset.Id),
                BleedTransparentRgb=IsUiTextureKind(job.Kind), WriteInlineMips=true, Bc7InputLayout="rgba",
                Bc7Quality=IsNativeUimdIconCookProfile(preset.Id) ? "best" : "balanced",
                Progress=progress, CancellationToken=cancellation
            }));
            foreach (var line in result.Log) AppendLog("  texture cook: " + line);
            foreach (var line in result.Warnings) AppendLog("  texture cook warning: " + line);
            if (!result.Status.Equals("created",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(result.Error?.Split('\n').FirstOrDefault() ?? result.Status);
            entries = [BuildTextureEntryFromSummary(output,source,preset.TemplateJson,DefaultTextureSourceRawRoot(projectRoot),
                package,MakeSafePackageBaseName($"Texture_{MakeSafeTextureToken(job.Name)}_{index:00000}_P"),job.Name,job.Kind,preset)];
        }
        CheckProject();
        progress.Report(new(96,"Saving texture recipes to this suit"));
        EquipmentIconPairCookService.CheckCollisions(entries.Select(e=>e.PackagePath), entries.Select(e=>e.DisplayName),
            project.GeneratedTextures.Select(e=>e.PackagePath),project.GeneratedTextures.Select(e=>e.DisplayName));
        var previousTextures = JsonSerializer.Serialize(project.GeneratedTextures);
        var previousIcons = (project.IconSuit,project.IconMenu,project.IconLeft,project.IconRight);
        project.GeneratedTextures.AddRange(entries);
        try
        {
            if (!EquipmentUiTextureCatalog.IsEquipmentIcon(job.Kind)) AutoAssignGeneratedUiIconSlots(project);
            (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(project);
        }
        catch
        {
            project.GeneratedTextures = JsonSerializer.Deserialize<List<GeneratedTextureEntry>>(previousTextures)!;
            (project.IconSuit,project.IconMenu,project.IconLeft,project.IconRight) = previousIcons;
            throw;
        }
        foreach (var entry in entries)
        {
            RecordChange("Textures",entry.DisplayName,entry.PackagePath,status:"staged");
            AppendLog("Texture queue imported: " + entry.PackagePath);
        }
        // Refresh only after saving succeeded; batch imports never overwrite the clipboard.
        RefreshToyboxTiles();
        return string.Join(Environment.NewLine,entries.Select(e=>e.PackagePath));
    }
}
