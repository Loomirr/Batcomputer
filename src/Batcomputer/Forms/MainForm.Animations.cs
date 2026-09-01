using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Animation overrides, swaps, and the anim-set browser.
/// </summary>
public sealed partial class MainForm
{
    private bool _animationImportInProgress;

    /// <summary>
    /// Browses animation building blocks and shows the current suit's composition.
    /// Animations are compositional: a suit's MAS_Char/LAS_Char pull in categorized
    /// blocks (Equipment/Traversal/Interaction/…) via ParentSetsArray. Applying a
    /// custom composition requires the suit to own its anim sets (custom archetype),
    /// which is a separate generation step - this tab is the browse/plan surface.
    /// </summary>
    private void RefreshAnimationTiles(string? type)
    {
        var gd = GameDataService.Instance;

        // The project-owned animation library is useful even before a suit has a base or the
        // extracted game-data catalogue is ready. Keep it independent from native set browsing.
        if (type is "Imported animation library" or "Imported animations")
        {
            RefreshImportedAnimationTiles();
            return;
        }

        var family = gd.FamilyForBasePath(_basePlayableText.Text.Trim());
        if (type is "Character animations" or "Overview & setup" || string.IsNullOrEmpty(type))
        {
            RefreshAnimationOverview(family);
            return;
        }

        if (!gd.HasAnimSets)
        {
            _toyboxTileFlow.Controls.Add(FullWidthNote(
                "Animation data not loaded. Rebuild gamedata (--build-gamedata) after dumping Content/Animation."));
            return;
        }

        var search = CurrentToyboxSearch();

        if (type is "Advanced: whole-set swaps" or "Animation families" or "Swap category by family")
        {
            RefreshAnimSwapTiles();
            return;
        }

        if (type is "Idle, walk & run" or "Replace idle/walk/run")
        {
            RefreshLocomotionTiles();
            return;
        }

        // Building-block browser (reference only).
        IEnumerable<GameDataAnimSet> sets;
        string header;
        if (type is "Reference: montage sets" or "Browse: Montage composites")
        {
            sets = gd.AnimSets("Montage").Where(a => a.IsCharacterComposite);
            header = "Per-family montage composites (MAS_Char_*). Each is a family's full montage set. Reference only.";
        }
        else if (type is "Reference: layer sets" or "Browse: Layer blocks")
        {
            sets = gd.AnimSets("Layer");
            header = "All layer anim sets (LAS). Equipment/Traversal/Interaction blocks + per-family composites. Reference only.";
        }
        else
        {
            const string referencePrefix = "Reference: layer · ";
            const string legacyPrefix = "Browse: Layer · ";
            var cat = type!.StartsWith(referencePrefix, StringComparison.Ordinal)
                ? type[referencePrefix.Length..]
                : type.StartsWith(legacyPrefix, StringComparison.Ordinal)
                    ? type[legacyPrefix.Length..]
                    : type;
            sets = gd.AnimSets("Layer", cat);
            header = $"Layer anim blocks · {cat}. Reference only.";
        }

        var categoryFilter = FilterVal(0);
        _toyboxTileFlow.Controls.Add(FullWidthNote(header));
        foreach (var set in sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (categoryFilter is not null && !set.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!MatchesToyboxSearch(search, set.Name, set.Category)) continue;
            var tile = MakeTile(set.Name.Replace("LAS_", "").Replace("MAS_", ""), set.Category,
                () => ShowAnimSetDetail(set.Name), Theme.Animations);
            _toyboxTileFlow.Controls.Add(tile);
        }
    }

    /// <summary>
    /// Per-category "borrow another family's animations" tiles. Whole-set swap:
    /// picks e.g. Locomotion → Catwoman, replacing LAS_Default_Batman with
    /// LAS_Default_Catwoman in the suit's composition (requires custom archetype).
    /// </summary>
    private void RefreshAnimSwapTiles()
    {
        var gd = GameDataService.Instance;
        EnsureProject();
        var archetypeOn = _currentProject?.UseCustomArchetype == true;
        _toyboxTileFlow.Controls.Add(FullWidthNote(
            archetypeOn
                ? "Advanced: replace a whole animation category at once. Prefer Edit character animations when you only want to change one action. Montage categories (Movement/Glide/LedgeGrab) are the safer choice. Cross-family Layer categories (Locomotion/Traversal) swap a compiled AnimBlueprint and can crash."
                : "Advanced: whole-set swaps need suit-owned animation assets. Turn on the advanced custom-archetype option on the Character animations page first."));

        foreach (var (category, kind, _, _) in GameDataService.AnimCategoryMap)
        {
            var current = _currentProject?.AnimationOverrides.FirstOrDefault(o => o.Category == category);
            var risky = kind.Equals("Layer", StringComparison.OrdinalIgnoreCase);
            var label = (risky ? "⚠ " : "") + category.Split(' ')[0];
            var sub = current is null ? $"{kind} · donor default{(risky ? " (ABP — crash-prone)" : "")}" : $"→ {current.ReplacementSet.Split('_').Last()}";
            var accent = current is not null ? Theme.Animations : risky ? Color.FromArgb(220, 120, 60) : Theme.OnDarkMuted;
            var cat = category;
            var tile = MakeTile(label, sub, () => PickAnimSwapFamily(cat), accent);
            tile.Height = 88;
            _toyboxTileFlow.Controls.Add(tile);
        }
    }

    /// <summary>
    /// Animations landing page: leads with the unified per-character editor and keeps
    /// imports, whole-set swaps, and manual custom-archetype control clearly separated.
    /// </summary>
    private void RefreshAnimationOverview(GameDataFamily? family)
    {
        EnsureProject();
        var on = _currentProject?.UseCustomArchetype == true;

        var intro = FullWidthNote(
            "Start with Edit character animations. It shows every action, montage, animation-blueprint layer, and locomotion slot inherited from this suit's gameplay donor, then lets you choose a compatible base-game or imported replacement.\n" +
            "Imported packs only add choices to the workspace library. Whole-set family swaps and the custom-archetype switch remain available below as advanced tools.");
        var introTextHeight = TextRenderer.MeasureText(
            intro.Text,
            intro.Font,
            new Size(Math.Max(1, intro.ClientSize.Width - intro.Padding.Horizontal), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
        intro.Height = Math.Max(132, introTextHeight + intro.Padding.Vertical + 4);
        _toyboxTileFlow.Controls.Add(intro);

        var explorerTile = MakeTile(
            "Edit character animations",
            "all character slots · base-game + imported replacements",
            () => OpenAnimationExplorer(),
            Theme.Animations);
        explorerTile.Width = 420;
        explorerTile.Height = 104;
        _toyboxTileFlow.Controls.Add(explorerTile);
        _toyboxTileFlow.SetFlowBreak(explorerTile, true);

        // Library actions are workspace-level, not suit-level. They stay available without a
        // selected base and without enabling a custom archetype.
        var importedCount = 0;
        try
        {
            var lib = new AnimLibraryService(_projectRootText.Text.Trim(), AppSettings.Current.EffectiveUsmapPath()).Load();
            importedCount = lib.Entries.Count(IsManagedAnimationEntry);
        }
        catch { /* library optional */ }

        var importTile = MakeTile(
            "+ Import animation pack",
            "choose any .utoc, .ucas, or .pak file",
            () => _ = ImportCustomAnimationsFromPakAsync(),
            Theme.Animations,
            dashed: true);
        importTile.Width = 210;
        importTile.Height = 104;
        _toyboxTileFlow.Controls.Add(importTile);

        var libraryTile = MakeTile(
            "Imported animation library",
            importedCount > 0 ? $"{importedCount} ready · browse library" : "library is empty",
            () => SelectComboValue(_toyboxTypeCombo, "Imported animation library"),
            Theme.Animations);
        libraryTile.Width = 210;
        libraryTile.Height = 104;
        _toyboxTileFlow.Controls.Add(libraryTile);

        _toyboxTileFlow.SetFlowBreak(libraryTile, true);

        var advancedSwapTile = MakeTile(
            "Advanced: whole-set swaps",
            "replace a complete family category",
            () => SelectComboValue(_toyboxTypeCombo, "Advanced: whole-set swaps"),
            Color.FromArgb(184, 126, 232));
        advancedSwapTile.Width = 280;
        advancedSwapTile.Height = 104;
        _toyboxTileFlow.Controls.Add(advancedSwapTile);

        // The custom-archetype switch remains available for troubleshooting and whole-set swaps.
        var toggle = MakeTile(
            on ? "Advanced assets: ON" : "Advanced assets: OFF",
            on ? "suit-owned animation sets · click to disable" : "custom archetype · normally enabled when needed",
            () =>
            {
                EnsureProject();
                if (_currentProject is null) { AppendLog("Set a base suit first."); return; }
                _currentProject.UseCustomArchetype = !_currentProject.UseCustomArchetype;
                _currentProject.GliderAutoEnabledCustomArchetype = false;
                RecordChange("Animations", "archetype", _currentProject.UseCustomArchetype ? "custom archetype enabled" : "custom archetype disabled", status: "staged");
                AppendLog($"Custom archetype {(_currentProject.UseCustomArchetype ? "ENABLED" : "disabled")}. Clones this suit's family archetype + anim sets and reparents the playable/cutscene on next package.");
                RefreshToyboxTiles();
            },
            on ? Theme.Animations : Color.FromArgb(150, 156, 166));
        toggle.Height = 104;
        toggle.Width = 280;
        _toyboxTileFlow.Controls.Add(toggle);
        _toyboxTileFlow.SetFlowBreak(toggle, true);

        // Current staged animation changes for this suit.
        var loco = _currentProject?.LocomotionOverrides.Count ?? 0;
        var slots = _currentProject?.AnimationSlotOverrides?.Count ?? 0;
        var swaps = _currentProject?.AnimationOverrides.Count ?? 0;
        _toyboxTileFlow.Controls.Add(FullWidthNote(
            $"Staged for this suit: {loco + slots} individual slot override(s), {swaps} advanced whole-set swap(s). Package the suit to apply them."));
    }

    private static bool IsManagedAnimationEntry(AnimLibraryEntry entry) =>
        entry.IsAvailable &&
        entry.CachedFiles.Count > 0 &&
        !entry.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase) &&
        !entry.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase);

    private void RefreshImportedAnimationTiles()
    {
        var importTile = MakeTile(
            "+ Import animation pack",
            "pick any file from the cooked trio",
            () => _ = ImportCustomAnimationsFromPakAsync(),
            Theme.Animations,
            dashed: true);
        importTile.Width = 210;
        importTile.Height = 104;
        _toyboxTileFlow.Controls.Add(importTile);

        var explorerTile = MakeTile(
            "Edit character animations",
            "choose a character slot, then replace it",
            () => OpenAnimationExplorer(),
            Theme.Animations);
        explorerTile.Width = 210;
        explorerTile.Height = 104;
        _toyboxTileFlow.Controls.Add(explorerTile);
        _toyboxTileFlow.SetFlowBreak(explorerTile, true);

        AnimLibrary library;
        try
        {
            library = new AnimLibraryService(
                _projectRootText.Text.Trim(),
                AppSettings.Current.EffectiveUsmapPath()).Load();
        }
        catch (Exception ex)
        {
            _toyboxTileFlow.Controls.Add(FullWidthNote("The animation library could not be opened: " + ex.Message));
            return;
        }

        var search = CurrentToyboxSearch();
        var entries = library.Entries
            .Where(entry => MatchesToyboxSearch(
                search,
                entry.Name,
                entry.PackagePath,
                entry.Skeleton,
                entry.HealthStatus))
            .OrderByDescending(entry => entry.IsAvailable)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ready = entries.Count(IsManagedAnimationEntry);
        var unavailable = entries.Count(entry => !entry.IsAvailable);
        _toyboxTileFlow.Controls.Add(FullWidthNote(
            $"Animation library · {ready} ready" +
            (unavailable > 0 ? $" · {unavailable} kept out for safety" : "") +
            ". Click an animation to inspect its authored rig and connected packages. " +
            "Apply compatible choices from Edit character animations."));

        foreach (var entry in entries)
        {
            var healthy = IsManagedAnimationEntry(entry);
            var rig = string.IsNullOrWhiteSpace(entry.Skeleton)
                ? "rig not identified"
                : entry.Skeleton[(entry.Skeleton.LastIndexOf('/') + 1)..];
            var status = healthy
                ? $"ready · {rig} · {entry.SupportPackages.Count} support"
                : $"{(string.IsNullOrWhiteSpace(entry.HealthStatus) ? "unavailable" : entry.HealthStatus)} · {rig}";
            var tile = MakeTile(
                healthy ? entry.Name : "⚠ " + entry.Name,
                status,
                () => OpenAnimationExplorer(entry.PackagePath),
                healthy ? Theme.Animations : Color.FromArgb(220, 120, 60));
            tile.Width = 184;
            tile.Height = 98;
            _toyboxTileFlow.Controls.Add(tile);
        }

        if (entries.Count == 0)
        {
            _toyboxTileFlow.Controls.Add(MakeNoteTile(
                string.IsNullOrWhiteSpace(search)
                    ? "No custom animations imported yet. Choose any file from a cooked animation package to begin."
                    : "No imported animations matched this search."));
        }
    }

    private void OpenAnimationExplorer(string? initialPackagePath = null)
    {
        AnimLibrary library;
        try
        {
            library = new AnimLibraryService(
                _projectRootText.Text.Trim(),
                AppSettings.Current.EffectiveUsmapPath()).Load();
        }
        catch (Exception ex)
        {
            Dialog.Error(
                this,
                "Animation library could not be opened",
                ex.Message,
                windowTitle: "Animation Explorer");
            return;
        }

        using var explorer = new AnimationExplorerForm(_currentProject, library, initialPackagePath);
        explorer.ReplaceRequested += (_, request) =>
        {
            var projectBeforeSave = _currentProject;
            if (ReplaceAnimationFromExplorer(request.Target, request.Slot, library))
            {
                explorer.RefreshFromProject();
            }
            else if (!ReferenceEquals(projectBeforeSave, _currentProject))
            {
                // A failed project-file save restores a rollback object. Close this view because it
                // intentionally holds the pre-save reference and must never continue editing it.
                explorer.Close();
            }
        };
        explorer.ResetRequested += (_, request) =>
        {
            var projectBeforeSave = _currentProject;
            if (ResetAnimationFromExplorer(request.Target, request.Slot))
            {
                explorer.RefreshFromProject();
            }
            else if (!ReferenceEquals(projectBeforeSave, _currentProject))
            {
                explorer.Close();
            }
        };
        explorer.ShowDialog(this);
    }

    private bool ReplaceAnimationFromExplorer(
        CharacterAnimationTargetSnapshot target,
        CharacterAnimationSlotSnapshot? slot,
        AnimLibrary library)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Applying the imported animation"))
        {
            return false;
        }

        EnsureProject();
        if (_currentProject is null)
        {
            Dialog.Warn(this, "Open a suit first", "Open or create a suit before assigning an animation.");
            return false;
        }
        if (target.ReferenceKind != CharacterAnimationReferenceKind.LocomotionSequence && slot is null)
        {
            Dialog.Warn(this, "Animation target was not found",
                "Reopen Animation Explorer and select the individual animation beneath its action/context row.");
            return false;
        }

        using var picker = new AnimationReplacementPickerForm(target, library);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedCandidate is not { } candidate)
        {
            return false;
        }
        if (candidate.PackagePath.Equals(target.EffectivePackage, StringComparison.OrdinalIgnoreCase))
        {
            Dialog.Info(this, "Animation is already selected",
                "That exact animation is already active on this target.");
            return false;
        }
        if (candidate.LibraryEntry is { } imported && !ConfirmExperimentalAnimationRig(imported))
        {
            return false;
        }
        if (target.ReferenceKind == CharacterAnimationReferenceKind.LayerAnimation &&
            !candidate.PackagePath.Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase) &&
            !Dialog.Confirm(
                this,
                "Experimental animation layer",
                "This replaces a compiled animation-blueprint layer. The asset class matches, but another character's graph may expect different state, curves, or linked layers and can still crash when that action starts.\n\nUse this only when the source character is compatible or you are ready to test it in-game.",
                confirmText: "Use layer anyway",
                severity: Dialog.Level.Crit))
        {
            return false;
        }

        NativeSuitProject rollbackProject;
        try
        {
            rollbackProject = JsonSerializer.Deserialize<NativeSuitProject>(
                                  JsonSerializer.Serialize(_currentProject))
                              ?? throw new InvalidOperationException("The saved suit snapshot was empty.");
        }
        catch (Exception ex)
        {
            Dialog.Error(
                this,
                "Animation could not be staged safely",
                "Batcomputer could not make a rollback snapshot of the current suit, so it left the suit unchanged.\n\n" + ex.Message,
                windowTitle: "Animation Explorer");
            return false;
        }

        if (!_currentProject.UseCustomArchetype)
        {
            var enable = Dialog.Confirm(
                this,
                "Enable animation editing?",
                "This suit needs its own animation composition before an individual animation can be assigned. Batcomputer can enable it now without changing the suit's gameplay donor.",
                confirmText: "Enable and apply");
            if (!enable)
            {
                return false;
            }
            _currentProject.UseCustomArchetype = true;
            _currentProject.GliderAutoEnabledCustomArchetype = false;
            AddAnimationProjectChange(_currentProject, "archetype", "custom archetype enabled");
        }

        var packagePath = UnrealPathUtil.NormalizePackagePath(candidate.PackagePath);
        var replacementName = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        _currentProject.LocomotionOverrides ??= [];
        _currentProject.AnimationSlotOverrides ??= [];
        if (target.ReferenceKind == CharacterAnimationReferenceKind.LocomotionSequence)
        {
            _currentProject.LocomotionOverrides.RemoveAll(item =>
                UnrealPathUtil.NormalizePackagePath(item.DonorSequencePackage)
                    .Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase) ||
                item.DonorSequence.Equals(target.OriginalObjectName, StringComparison.OrdinalIgnoreCase));
            if (!packagePath.Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase))
            {
                _currentProject.LocomotionOverrides.Add(new AnimSequenceOverride
                {
                    DonorSequence = target.OriginalObjectName,
                    DonorSequencePackage = target.OriginalPackage,
                    ReplacementSequence = replacementName,
                    ReplacementPackage = packagePath,
                });
            }
        }
        else
        {
            var removed = RemovePersistedAnimationSlotOverride(
                _currentProject.AnimationSlotOverrides,
                target,
                slot!,
                out var ambiguousSavedOverride);
            if (ambiguousSavedOverride || (target.IsOverridden && removed == 0))
            {
                _currentProject = rollbackProject;
                ApplyProjectToFields(_currentProject);
                UpdateSelectedLabels();
                Dialog.Warn(
                    this,
                    ambiguousSavedOverride ? "Saved animation target is ambiguous" : "Saved animation target was not found",
                    ambiguousSavedOverride
                        ? "More than one saved override matches this action/context after the animation data refresh. Batcomputer left the suit unchanged instead of replacing the wrong one. Reset the conflicting saved entries or rebase the suit, then reopen Animation Explorer."
                        : "The saved override no longer matches this action/context. Batcomputer left the suit unchanged. Reopen Animation Explorer after refreshing or rebasing the suit.",
                    windowTitle: "Animation Explorer");
                return false;
            }
            if (!packagePath.Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase))
            {
                _currentProject.AnimationSlotOverrides.Add(new AnimationSlotOverride
                {
                    Kind = slot!.SetKind == CharacterAnimationSetKind.Montage ? "Montage" : "Layer",
                    OwnerSetPackage = target.OwnerPackage,
                    ActionTag = slot.ActionTag,
                    ContextTags = slot.ContextTags.ToList(),
                    EntryIndex = target.EntryIndex,
                    VariantIndex = target.WeightIndex,
                    ReferenceKind = target.ReferenceKind == CharacterAnimationReferenceKind.AnimFile
                        ? "AnimFile"
                        : "LayerAnim",
                    ReferenceIndex = target.ReferenceKind == CharacterAnimationReferenceKind.AnimFile
                        ? 0
                        : Math.Max(0, target.LayerIndex),
                    DonorPackage = target.OriginalPackage,
                    DonorClass = target.AssetClass,
                    ReplacementPackage = packagePath,
                    ReplacementClass = candidate.AssetClass,
                });
            }
        }
        _currentProject.GliderAutoEnabledCustomArchetype = false;
        AddAnimationProjectChange(
            _currentProject,
            AnimationTargetLabel(target, slot),
            packagePath.Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase)
                ? "restored gameplay donor"
                : $"→ {replacementName}");
        try
        {
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        }
        catch (Exception ex)
        {
            _currentProject = rollbackProject;
            ApplyProjectToFields(_currentProject);
            UpdateSelectedLabels();
            AppendLog("Animation override was not applied because the suit project could not be saved: " + ex.Message);
            Dialog.Error(
                this,
                "Animation was not applied",
                "Batcomputer restored the prior suit because its project file could not be saved. Close anything holding the project file, then retry.\n\n" + ex.Message,
                windowTitle: "Animation Explorer");
            RefreshToyboxTiles();
            PopulateToyboxSlots();
            RefreshInspector();
            return false;
        }

        _session.RaiseChanged();
        AppendLog($"{AnimationTargetLabel(target, slot)} → {replacementName}. Referenced imported support packages will ship with the suit automatically.");
        RefreshToyboxTiles();
        PopulateToyboxSlots();
        RefreshInspector();
        return true;
    }

    private bool ResetAnimationFromExplorer(
        CharacterAnimationTargetSnapshot target,
        CharacterAnimationSlotSnapshot? slot)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Resetting the animation"))
        {
            return false;
        }
        EnsureProject();
        if (_currentProject is null || !target.IsOverridden)
        {
            return false;
        }

        NativeSuitProject rollbackProject;
        try
        {
            rollbackProject = JsonSerializer.Deserialize<NativeSuitProject>(
                                  JsonSerializer.Serialize(_currentProject))
                              ?? throw new InvalidOperationException("The saved suit snapshot was empty.");
        }
        catch (Exception ex)
        {
            Dialog.Error(this, "Animation could not be reset safely",
                "Batcomputer could not make a rollback snapshot, so it left the suit unchanged.\n\n" + ex.Message,
                windowTitle: "Animation Explorer");
            return false;
        }

        var removed = target.ReferenceKind == CharacterAnimationReferenceKind.LocomotionSequence
            ? (_currentProject.LocomotionOverrides ??= []).RemoveAll(item =>
                UnrealPathUtil.NormalizePackagePath(item.DonorSequencePackage)
                    .Equals(target.OriginalPackage, StringComparison.OrdinalIgnoreCase) ||
                item.DonorSequence.Equals(target.OriginalObjectName, StringComparison.OrdinalIgnoreCase))
            : slot is null
                ? 0
                : RemovePersistedAnimationSlotOverride(
                    _currentProject.AnimationSlotOverrides ??= [],
                    target,
                    slot,
                    out _);
        if (removed == 0)
        {
            Dialog.Warn(this, "Saved override was not found",
                "The target changed after the Explorer was opened. Reopen it and try again.");
            return false;
        }

        AddAnimationProjectChange(_currentProject, AnimationTargetLabel(target, slot), "restored gameplay donor");
        try
        {
            (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject);
        }
        catch (Exception ex)
        {
            _currentProject = rollbackProject;
            ApplyProjectToFields(_currentProject);
            UpdateSelectedLabels();
            Dialog.Error(this, "Animation was not reset",
                "Batcomputer restored the prior suit because its project file could not be saved. Close anything holding the project file, then retry.\n\n" + ex.Message,
                windowTitle: "Animation Explorer");
            RefreshToyboxTiles();
            PopulateToyboxSlots();
            RefreshInspector();
            return false;
        }

        _session.RaiseChanged();
        AppendLog($"{AnimationTargetLabel(target, slot)} restored to {target.OriginalObjectName}.");
        RefreshToyboxTiles();
        PopulateToyboxSlots();
        RefreshInspector();
        return true;
    }

    private static int RemovePersistedAnimationSlotOverride(
        List<AnimationSlotOverride> changes,
        CharacterAnimationTargetSnapshot target,
        CharacterAnimationSlotSnapshot slot,
        out bool ambiguous)
    {
        var index = CharacterAnimationGraphService.SelectPersistedSlotOverrideIndex(
            changes,
            slot,
            target,
            out ambiguous);
        if (index < 0)
        {
            return 0;
        }
        changes.RemoveAt(index);
        return 1;
    }

    private static string AnimationTargetLabel(
        CharacterAnimationTargetSnapshot target,
        CharacterAnimationSlotSnapshot? slot)
    {
        if (target.ReferenceKind == CharacterAnimationReferenceKind.LocomotionSequence)
        {
            return target.OriginalObjectName;
        }
        var action = string.IsNullOrWhiteSpace(slot?.ActionTag) ? "animation slot" : slot.ActionTag;
        return $"{action} · {target.OriginalObjectName}";
    }

    private bool ConfirmExperimentalAnimationRig(AnimLibraryEntry entry)
    {
        if (entry.Skeleton.Equals(NativeBodyProfileService.SharedSkeleton, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rig = string.IsNullOrWhiteSpace(entry.Skeleton) ? "an unidentified skeleton" : entry.Skeleton;
        return Dialog.Confirm(
            this,
            "Unverified animation rig",
            $"This animation was cooked for {rig}, not the game's shared LEGOfig skeleton. Its package is complete, but Batcomputer cannot prove that its bone layout matches this suit's AnimBlueprint. An incompatible rig can crash when the pose starts.\n\nOnly continue if this exact animation rig has been authored or tested for the current character.",
            confirmText: "Apply experimental",
            severity: Dialog.Level.Crit);
    }

    private static void AddAnimationProjectChange(
        NativeSuitProject project,
        string target,
        string detail)
    {
        project.Changes.RemoveAll(change =>
            change.Category.Equals("Animations", StringComparison.Ordinal) &&
            change.Target.Equals(target, StringComparison.Ordinal) &&
            change.Detail.Equals(detail, StringComparison.Ordinal));
        project.Changes.Add(new SavedChange
        {
            When = DateTime.Now.ToString("o"),
            Category = "Animations",
            Target = target,
            Detail = detail,
            Status = "staged",
        });
    }

    private (string Name, string Package) ChooseLocomotionTarget(
        string role,
        IReadOnlyList<(string Name, string Package)> candidates)
    {
        using var dialog = new AdaptiveDialogForm
        {
            Text = $"Choose the {role.ToLowerInvariant()} slot",
            Width = 540,
            Height = 430,
            MinimumSize = new Size(440, 340),
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };
        dialog.Shown += (_, _) => Theme.UseDarkTitleBar(dialog);

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(10, 8, 10, 6),
            Text = $"This gameplay family has more than one {role.ToLowerInvariant()} sequence. Choose the exact AnimBlueprint slot to replace.",
            Font = Theme.Caption,
            ForeColor = Theme.OnDarkMuted,
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            ForeColor = Theme.OnDark,
            BorderStyle = BorderStyle.None,
        };
        Theme.StyleListBox(list);
        foreach (var candidate in candidates)
        {
            list.Items.Add(candidate.Name);
        }
        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        var use = new Button { Text = "Use this slot", Dock = DockStyle.Bottom, Height = 36 };
        Theme.StyleGoldButton(use);
        use.DialogResult = DialogResult.OK;
        list.DoubleClick += (_, _) => use.PerformClick();
        dialog.Controls.Add(list);
        dialog.Controls.Add(note);
        dialog.Controls.Add(use);
        dialog.AcceptButton = use;
        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0)
        {
            return default;
        }
        return candidates[list.SelectedIndex];
    }

    /// <summary>
    /// Picker for a locomotion replacement: imported custom animations (library) first, then the
    /// game AnimSequence catalog, plus a free-text custom /Game path. Returns an object path
    /// (<c>/Game/…/A_Foo.A_Foo</c>) or null. Imported anims ship in the suit's pak automatically
    /// when referenced; a raw custom path only resolves if that anim is mounted (import it, or ship
    /// it in its own pak).
    /// </summary>
    private string? PickAnimReplacement(string title)
    {
        var svc = new AnimLibraryService(_projectRootText.Text.Trim(), AppSettings.Current.EffectiveUsmapPath());
        var libEntries = svc.Load().Entries
            .Where(e => e.IsAvailable
                        && e.CachedFiles.Count > 0
                        && !e.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase)
                        && !e.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gd = GameDataService.Instance;
        var gameAnims = gd.HasCatalog
            ? gd.AssetsOfClass("AnimSequence").Select(a => a.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        var rows = new List<(string Display, string Pkg, AnimLibraryEntry? Entry)>();
        foreach (var e in libEntries)
        {
            rows.Add(($"★ custom · {e.Name}    {e.PackagePath}", UnrealPathUtil.NormalizePackagePath(e.PackagePath), e));
        }
        foreach (var p in gameAnims)
        {
            rows.Add((p, p, null));
        }

        using var dlg = new AdaptiveDialogForm
        {
            Text = title,
            Width = 800,
            Height = 580,
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(620, 440),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);
        var search = new TextBox { Dock = DockStyle.Top, Height = 30, PlaceholderText = "Filter, or paste a custom /Game/… path" };
        Theme.StyleDarkInput(search);
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.None };
        Theme.StyleListBox(list);
        var ok = new Button { Text = "Use selected", Dock = DockStyle.Bottom, Height = 34 };
        Theme.StyleGoldButton(ok);
        ok.DialogResult = DialogResult.OK;

        var view = new List<(string Display, string Pkg, AnimLibraryEntry? Entry)>();
        void Fill(string term)
        {
            list.BeginUpdate();
            list.Items.Clear();
            view.Clear();
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(term) || r.Display.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    view.Add(r);
                    list.Items.Add(r.Display);
                }
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        list.DoubleClick += (_, _) => { if (list.SelectedItem is not null) ok.PerformClick(); };
        Fill("");

        dlg.Controls.Add(list);
        dlg.Controls.Add(search);
        dlg.Controls.Add(ok);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        // A typed game or installed Game Feature path wins; otherwise use the selected row.
        var typed = search.Text.Trim();
        string? pkg = null;
        AnimLibraryEntry? selectedLibraryEntry = null;
        if (ExtractedPackagePathService.IsContentPackagePath(typed))
        {
            pkg = UnrealPathUtil.NormalizePackagePath(typed);
        }
        else if (list.SelectedIndex >= 0 && list.SelectedIndex < view.Count)
        {
            pkg = view[list.SelectedIndex].Pkg;
            selectedLibraryEntry = view[list.SelectedIndex].Entry;
        }
        if (string.IsNullOrWhiteSpace(pkg))
        {
            return null;
        }
        if (selectedLibraryEntry is not null && !ConfirmExperimentalAnimationRig(selectedLibraryEntry))
        {
            return null;
        }
        var leaf = pkg[(pkg.LastIndexOf('/') + 1)..];
        return $"{pkg}.{leaf}";
    }

    /// <summary>
    /// "Import custom animations": verifies a modder-cooked pak trio, converts only that source
    /// container while resolving imports against the full base-game/DLC package store, then
    /// registers each AnimSequence and its connected rig support. Managed packages ship inside a
    /// suit only when referenced; base-game animations are never duplicated.
    /// </summary>
    private async Task ImportCustomAnimationsFromPakAsync()
    {
        if (_animationImportInProgress)
        {
            Dialog.Info(
                this,
                "Animation import is already running",
                "Let the current package finish validating and importing before starting another one.",
                windowTitle: "Animations");
            return;
        }

        var projectRoot = _projectRootText.Text.Trim();
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            AppendLog("Set a project root first.");
            return;
        }

        using var ofd = new OpenFileDialog
        {
            Title = "Choose any file from the cooked animation package",
            Filter = "Cooked animation package (*.utoc;*.ucas;*.pak)|*.utoc;*.ucas;*.pak|All supported files|*.utoc;*.ucas;*.pak",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (ofd.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        if (!AnimationContainerSelectionService.TryResolve(ofd.FileName, out var selectedContainer, out var selectionError) ||
            selectedContainer is null)
        {
            AppendLog("Import failed: " + selectionError.Replace('\n', ' '));
            Dialog.Error(this, "Animation package could not be opened", selectionError, windowTitle: "Animations");
            return;
        }
        var utoc = selectedContainer.UtocPath;
        var trioBase = selectedContainer.BasePath;

        var paksRoot = AppSettings.Current.EffectiveGamePaksRoot();
        var globalUtoc = string.IsNullOrWhiteSpace(paksRoot) ? "" : Path.Combine(paksRoot, "global.utoc");
        if (string.IsNullOrWhiteSpace(paksRoot) || !File.Exists(globalUtoc))
        {
            var detail = "Batcomputer needs the game's Paks folder to resolve the animation's engine and rig dependencies. " +
                         "Set it in Settings, then import again.\n\nLooked for:\n" + globalUtoc;
            AppendLog(detail.Replace('\n', ' '));
            Dialog.Error(this, "Game Paks folder is required", detail, windowTitle: "Animations");
            return;
        }

        AppendLog($"Importing custom animations from {Path.GetFileName(utoc)}…");
        var work = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "AnimImport", Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(work, "out");
        var manifestDir = Path.Combine(work, "manifest");
        string? containerMount = null;
        Directory.CreateDirectory(work);
        using var progress = new AnimationImportProgressForm(selectedContainer.DisplayName);
        _animationImportInProgress = true;
        progress.Show(this);
        try
        {
            progress.SetPhase("Checking the package", "Verifying the cooked container before anything is added to your library…");
            var verify = await RunRetocVerifyAsync(utoc);
            if (verify.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"retoc could not verify the selected animation container (exit {verify.ExitCode}).\n{verify.Detail}");
            }

            Directory.CreateDirectory(manifestDir);
            progress.SetPhase("Reading animation assets", "Finding the authored animations, skeletons, meshes, and physics support…");
            var manifest = await ReadAnimationPakManifestAsync(utoc, manifestDir);
            if (manifest.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"retoc could not read the animation container manifest (exit {manifest.ExitCode}).\n{manifest.Detail}");
            }
            if (manifest.Entries.Count == 0)
            {
                throw new InvalidDataException("The selected container manifest does not contain any cooked /Game packages.");
            }
            if (manifest.Entries.Any(entry => entry.PackageId == 0))
            {
                throw new InvalidDataException(
                    "The selected container did not expose a complete package identity table. Batcomputer stopped before importing uncertain assets.");
            }

            // retoc needs the complete base package store while converting a user Zen container.
            // global.utoc resolves /Script types, but Engine animation settings and ACL assets live
            // in pakchunk0; omitting them turns valid imports into UnknownPackage/UnknownExport.
            var dlcRoot = GameAssetRefreshService.DlcRootForPaksRoot(paksRoot);
            AppendLog("  checking source package identities against the installed game and DLC…");
            progress.SetPhase("Checking package ownership", "Making sure the animation pack does not overwrite a base-game or DLC package…");
            var collisions = await FindInstalledAnimationPackageCollisionsAsync(
                manifest.Entries,
                paksRoot,
                dlcRoot);
            if (collisions.Count > 0)
            {
                var detail = string.Join(
                    "\n",
                    collisions.Take(8).Select(collision =>
                        $"{collision.PackagePath}  ({Path.GetFileName(collision.ContainerPath)})"));
                if (collisions.Count > 8)
                {
                    detail += $"\n…and {collisions.Count - 8} more.";
                }
                throw new InvalidDataException(
                    "The custom animation container reuses package identities already owned by the installed game or DLC. " +
                    "Shipping those assets would globally overwrite game content. Re-cook them under unique package paths.\n\n" + detail);
            }

            containerMount = GameAssetRefreshService.CreateCombinedContainerMount(paksRoot, dlcRoot, work);

            var importedContainerBase = Path.Combine(
                containerMount,
                "BatcomputerAnimationImport_" + Guid.NewGuid().ToString("N"));
            foreach (var ext in new[] { ".utoc", ".ucas", ".pak" })
            {
                var f = trioBase + ext;
                if (File.Exists(f)) File.Copy(f, importedContainerBase + ext, overwrite: true);
            }

            var filters = BuildAnimationManifestFilters(manifest.Entries, paksRoot);
            if (filters.Count == 0)
            {
                throw new InvalidDataException(
                    "Batcomputer could not derive a safe source-only extraction filter from this animation container.");
            }
            AppendLog(
                $"  verified container: {manifest.Entries.Count} source package(s); resolving against the full base game" +
                (Directory.Exists(dlcRoot) ? " + installed DLC" : "") + ".");
            progress.SetPhase("Preparing animations", "Resolving engine, ACL, and authored rig dependencies against the installed game…");
            foreach (var filter in filters)
            {
                var exit = await RunRetocToLegacyAsync(containerMount, outDir, filter);
                if (exit != 0)
                {
                    throw new InvalidDataException(
                        $"retoc could not convert the animation source package filter '{filter}' (exit {exit}).");
                }
            }

            var svc = new AnimLibraryService(projectRoot, AppSettings.Current.EffectiveUsmapPath());
            var lib = svc.Load();
            var allowedPackages = manifest.Entries
                .Select(entry => entry.PackagePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            progress.SetPhase("Adding to your library", "Inspecting each sequence or montage and keeping its complete authored support tree together…");
            var report = await Task.Run(() => svc.ImportAnimationPakFolder(lib, outDir, allowedPackages));
            progress.Close();

            foreach (var e in report.Imported)
            {
                AppendLog(
                    $"  ✓ imported '{e.Name}' → {e.PackagePath} " +
                    $"(+ {e.SupportPackages.Count} support package(s))");
            }
            foreach (var e in report.Quarantined)
            {
                AppendLog($"  ✗ quarantined '{e.Name}': {string.Join("; ", e.HealthIssues)}");
            }
            foreach (var r in report.RejectedNonAnim) AppendLog($"  ✗ skipped (not an AnimSequence or AnimMontage): {r}");
            foreach (var w in report.Warnings) AppendLog($"  ⚠ {w}");
            AppendLog(
                $"Import complete: {report.Imported.Count} animation(s) ready, " +
                $"{report.Quarantined.Count} quarantined, {report.RejectedNonAnim.Count} unrelated asset(s) skipped.");
            if (report.Imported.Count > 0)
            {
                AppendLog("They now appear in the Imported animation library and as compatible choices in Edit character animations. Their required source packages ship with the suit automatically.");
                var rigCount = report.Imported
                    .Select(entry => entry.Skeleton)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                Dialog.Info(
                    this,
                    "Animation import complete",
                    $"{report.Imported.Count} animation(s) are ready in your library.\n\n" +
                    $"Batcomputer preserved {rigCount} detected rig(s) and the connected support packages. " +
                    "Open Edit character animations, choose the exact character slot, then select a compatible imported animation.",
                    windowTitle: "Animations");
            }
            if (report.Quarantined.Count > 0)
            {
                Dialog.Warn(
                    this,
                    "Some animations were kept out",
                    $"{report.Quarantined.Count} animation(s) contained unresolved or incomplete cooked references, so Batcomputer did not make them selectable. " +
                    "Check Diagnostics for the exact package and re-cook/re-import it before building a suit.",
                    windowTitle: "Animations");
            }
            SelectComboValue(_toyboxTypeCombo, "Imported animation library");
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            if (!progress.IsDisposed)
            {
                progress.Close();
            }
            AppendLog($"Import error: {ex.Message}");
            Dialog.Error(
                this,
                "Animation import failed safely",
                "Nothing crash-prone was added to the animation picker.\n\n" + ex.Message,
                windowTitle: "Animations");
        }
        finally
        {
            _animationImportInProgress = false;
            if (!progress.IsDisposed)
            {
                progress.Close();
            }
            GameAssetRefreshService.TryDeleteCombinedContainerMount(containerMount);
            try { Directory.Delete(work, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    private sealed record AnimationManifestEntry(string PackagePath, string Filename, ulong PackageId);

    private sealed record InstalledAnimationPackageCollision(
        string PackagePath,
        ulong PackageId,
        string ContainerPath);

    private sealed record AnimationManifestResult(
        int ExitCode,
        IReadOnlyList<AnimationManifestEntry> Entries,
        string Detail);

    private sealed record RetocCommandResult(int ExitCode, string Detail);

    private async Task<RetocCommandResult> RunRetocVerifyAsync(string utoc)
    {
        var retoc = AppSettings.Current.EffectiveRetocExePath();
        if (!File.Exists(retoc))
        {
            return new RetocCommandResult(-1, $"retoc.exe was not found: {retoc}");
        }

        var startInfo = NewRetocStartInfo(retoc, Path.GetDirectoryName(retoc) ?? AppSettings.ToolRoot);
        startInfo.ArgumentList.Add("verify");
        startInfo.ArgumentList.Add(utoc);
        var command = await RunRetocCommandAsync(startInfo);
        return new RetocCommandResult(command.ExitCode, command.Detail);
    }

    private async Task<AnimationManifestResult> ReadAnimationPakManifestAsync(string stagedUtoc, string manifestDir)
    {
        var retoc = AppSettings.Current.EffectiveRetocExePath();
        if (!File.Exists(retoc))
        {
            return new AnimationManifestResult(-1, [], $"retoc.exe was not found: {retoc}");
        }

        var startInfo = NewRetocStartInfo(retoc, manifestDir);
        startInfo.ArgumentList.Add("manifest");
        startInfo.ArgumentList.Add(stagedUtoc);
        var command = await RunRetocCommandAsync(startInfo);
        if (command.ExitCode != 0)
        {
            return new AnimationManifestResult(command.ExitCode, [], command.Detail);
        }

        var manifestPath = Path.Combine(manifestDir, "pakstore.json");
        if (!File.Exists(manifestPath))
        {
            return new AnimationManifestResult(-1, [], "retoc did not produce pakstore.json.");
        }

        var entries = new List<AnimationManifestEntry>();
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (document.RootElement.TryGetProperty("oplog", out var oplog) &&
            oplog.TryGetProperty("entries", out var rawEntries) &&
            rawEntries.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawEntry in rawEntries.EnumerateArray())
            {
                if (!rawEntry.TryGetProperty("packagestoreentry", out var packageStore) ||
                    !packageStore.TryGetProperty("packagename", out var packageNameValue) ||
                    !rawEntry.TryGetProperty("packagedata", out var packageData) ||
                    packageData.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var packagePath = packageNameValue.GetString() ?? "";
                var filename = "";
                ulong packageId = 0;
                foreach (var item in packageData.EnumerateArray())
                {
                    var candidateFilename = item.TryGetProperty("filename", out var filenameValue)
                        ? filenameValue.GetString() ?? ""
                        : "";
                    if (string.IsNullOrWhiteSpace(candidateFilename) ||
                        !candidateFilename.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    filename = candidateFilename;
                    if (item.TryGetProperty("id", out var idValue))
                    {
                        TryPackageIdFromIoChunkId(idValue.GetString(), out packageId);
                    }
                    break;
                }
                if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(filename))
                {
                    continue;
                }
                entries.Add(new AnimationManifestEntry(
                    UnrealPathUtil.NormalizePackagePath(packagePath),
                    filename.Replace('\\', '/'),
                    packageId));
            }
        }

        entries = entries
            .GroupBy(entry => entry.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(entry => entry.Filename, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(entry => entry.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AnimationManifestResult(command.ExitCode, entries, command.Detail);
    }

    internal static bool TryPackageIdFromIoChunkId(string? chunkId, out ulong packageId)
    {
        packageId = 0;
        if (string.IsNullOrWhiteSpace(chunkId) || chunkId.Length < 16)
        {
            return false;
        }

        try
        {
            // The first eight chunk-ID bytes are the package ID in little-endian order.
            for (var index = 0; index < 8; index++)
            {
                var value = Convert.ToByte(chunkId.Substring(index * 2, 2), 16);
                packageId |= (ulong)value << (index * 8);
            }
            return packageId != 0;
        }
        catch (FormatException)
        {
            packageId = 0;
            return false;
        }
        catch (OverflowException)
        {
            packageId = 0;
            return false;
        }
    }

    private async Task<IReadOnlyList<InstalledAnimationPackageCollision>> FindInstalledAnimationPackageCollisionsAsync(
        IReadOnlyList<AnimationManifestEntry> sourceEntries,
        string paksRoot,
        string dlcRoot)
    {
        var sourceById = sourceEntries
            .Where(entry => entry.PackageId != 0)
            .GroupBy(entry => entry.PackageId)
            .ToDictionary(group => group.Key, group => group.First());
        var collisions = new List<InstalledAnimationPackageCollision>();
        var retoc = AppSettings.Current.EffectiveRetocExePath();
        var containers = new[] { paksRoot, dlcRoot }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.utoc", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var container in containers)
        {
            var startInfo = NewRetocStartInfo(retoc, Path.GetDirectoryName(retoc) ?? AppSettings.ToolRoot);
            startInfo.ArgumentList.Add("list");
            startInfo.ArgumentList.Add("--package");
            startInfo.ArgumentList.Add(container);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start retoc.exe while checking installed package identities.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 3 || !ulong.TryParse(columns[2], out var packageId) ||
                    !sourceById.TryGetValue(packageId, out var source))
                {
                    continue;
                }

                collisions.Add(new InstalledAnimationPackageCollision(
                    source.PackagePath,
                    packageId,
                    container));
            }

            await process.WaitForExitAsync();
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"retoc could not inspect installed container '{Path.GetFileName(container)}' while checking package ownership " +
                    $"(exit {process.ExitCode}).\n{stderr.Trim()}");
            }
        }

        return collisions
            .GroupBy(collision => (collision.PackageId, collision.ContainerPath))
            .Select(group => group.First())
            .OrderBy(collision => collision.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(collision => collision.ContainerPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildAnimationManifestFilters(
        IReadOnlyList<AnimationManifestEntry> entries,
        string paksRoot)
    {
        static string NormalizeFilename(string filename)
        {
            var value = filename.Replace('\\', '/');
            while (value.StartsWith("../", StringComparison.Ordinal))
            {
                value = value[3..];
            }
            return value.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                ? value[..^".uasset".Length]
                : value;
        }

        var exact = entries
            .Select(entry => NormalizeFilename(entry.Filename))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exact.Count <= 1)
        {
            return exact;
        }

        var split = exact.Select(value => value.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var commonCount = 0;
        while (commonCount < split.Min(parts => parts.Length) &&
               split.All(parts => parts[commonCount].Equals(split[0][commonCount], StringComparison.OrdinalIgnoreCase)))
        {
            commonCount++;
        }

        // Do not include the first package's filename as a directory component.
        commonCount = Math.Min(commonCount, split[0].Length - 1);
        if (commonCount <= 0)
        {
            return exact;
        }

        var common = string.Join('/', split[0].Take(commonCount)) + "/";
        var contentIndex = common.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIndex <= 0)
        {
            return exact;
        }

        var sourceProject = common[..contentIndex].Split('/').LastOrDefault() ?? "";
        var gameProject = Directory.GetParent(Directory.GetParent(Path.GetFullPath(paksRoot))?.FullName ?? "")?.Name ?? "";
        return sourceProject.Equals(gameProject, StringComparison.OrdinalIgnoreCase)
            ? exact // avoid accidentally extracting a broad section of the 39 GB base container
            : [common];
    }

    private static ProcessStartInfo NewRetocStartInfo(string retoc, string workingDirectory) => new()
    {
        FileName = retoc,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    private static async Task<RetocCommandResult> RunRetocCommandAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start retoc.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var detail = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new RetocCommandResult(process.ExitCode, detail);
    }

    private void PickAnimSwapFamily(string category)
    {
        if (BlockSynchronousEditWhileLoadedProjectRestores("Changing the animation family"))
        {
            return;
        }

        EnsureProject();
        if (_currentProject is null) { AppendLog("Set a base suit first."); return; }

        var gd = GameDataService.Instance;
        var currentFamily = TargetFamilyNameForProject(_currentProject);
        var families = gd.FamiliesWithAnimCategory(category)
            .Where(f => !f.Equals(currentFamily, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (families.Count == 0) { AppendLog($"No other families have a '{category}' set."); return; }

        using var dlg = new AdaptiveDialogForm { Text = $"{category} — source family", Width = 360, Height = 420, AutoScaleMode = AutoScaleMode.Dpi, MinimumSize = new Size(320, 340), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.WindowBg, ForeColor = Theme.OnDark };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.CardBg, ForeColor = Theme.OnDark, BorderStyle = BorderStyle.None };
        Theme.StyleListBox(list);
        list.Items.Add("(donor default — remove override)");
        foreach (var f in families) list.Items.Add(f);
        var ok = new Button { Text = "Use", Dock = DockStyle.Bottom, Height = 32 };
        Theme.StyleGoldButton(ok);
        ok.DialogResult = DialogResult.OK;
        list.DoubleClick += (_, _) => ok.PerformClick();
        dlg.Controls.Add(list);
        dlg.Controls.Add(ok);
        if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedItem is null) return;

        _currentProject.AnimationOverrides.RemoveAll(o => o.Category == category);
        _currentProject.GliderAutoEnabledCustomArchetype = false;
        var choice = list.SelectedItem.ToString()!;
        if (choice.StartsWith("("))
        {
            RecordChange("Animations", category, "reverted to donor default", status: "staged");
            AppendLog($"{category}: reverted to donor default.");
        }
        else
        {
            var ov = gd.BuildAnimOverride(category, choice);
            if (ov is null) { AppendLog($"{choice} has no {category} set."); return; }
            if (ov.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase))
            {
                var proceed = Dialog.Confirm(this,
                    "Crash-prone animation swap",
                    $"'{category}' swaps a compiled AnimBlueprint (ABP_Core_{choice}). Driving another family's animgraph on this pawn is KNOWN to crash — confirmed with Catwoman locomotion on Thomas.",
                    confirmText: "Apply anyway", severity: Dialog.Level.Crit);
                if (!proceed) { RefreshToyboxTiles(); return; }
            }
            _currentProject.AnimationOverrides.Add(ov);
            RecordChange("Animations", category, $"borrow {choice} ({ov.ReplacementSet})", status: "staged");
            AppendLog($"{category} → {choice} ({ov.ReplacementSet}). Regenerate to apply.");
        }
        RefreshToyboxTiles();
        PopulateToyboxSlots();
    }

    private void ShowAnimSetDetail(string name)
    {
        var set = GameDataService.Instance.FindAnimSet(name);
        if (set is null)
        {
            Dialog.Info(null, "Animation set", $"{name}\n\n(not in catalog — it may be a building block whose folder wasn't dumped)");
            return;
        }
        var lines = new List<string>
        {
            $"Name: {set.Name}",
            $"Kind: {set.Kind} anim set   Category: {set.Category}",
            $"Package: {set.Package}",
            set.IsCharacterComposite ? $"Composite of {set.ParentSets.Count} block(s):" : "(building block)",
        };
        if (set.ParentSets.Count > 0)
        {
            lines.AddRange(set.ParentSets.Select(p => "  • " + p));
        }
        Dialog.Info(null, $"Animation · {set.Name}", string.Join(Environment.NewLine, lines));
    }

    /// <summary>Stages library-owned animation assets referenced by this suit.</summary>
    private void StageLibraryAnimsIntoContentRoot(NativeSuitProject project, string contentRootToPackage)
    {
        try
        {
            var svc = new AnimLibraryService(_projectRootText.Text.Trim(), AppSettings.Current.EffectiveUsmapPath());
            var lib = svc.Load();
            if (lib.Entries.Count == 0)
            {
                return;
            }

            var referenced = project.AnimationOverrides.Select(o => o.ReplacementPackage)
                .Concat(project.LocomotionOverrides.Select(o => o.ReplacementPackage))
                .Concat((project.AnimationSlotOverrides ?? []).Select(o => o.ReplacementPackage))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(UnrealPathUtil.NormalizePackagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A quarantined managed record must block the build. Treating it as "nothing to ship"
            // leaves the grafted AnimBlueprint pointing at a package that is absent from the pak.
            foreach (var packagePath in referenced)
            {
                var entry = lib.Entries.FirstOrDefault(candidate =>
                    UnrealPathUtil.NormalizePackagePath(candidate.PackagePath)
                        .Equals(packagePath, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    continue; // a typed external/base-game path may intentionally live elsewhere
                }

                var managed = entry.CachedFiles.Count > 0 ||
                              (!entry.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase) &&
                               !entry.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase));
                if (managed && !entry.IsAvailable)
                {
                    var reason = entry.HealthIssues.Count > 0
                        ? string.Join("; ", entry.HealthIssues)
                        : "the managed cache is incomplete";
                    throw new InvalidOperationException(
                        $"Referenced animation '{entry.Name}' is quarantined and cannot ship: {reason}. " +
                        "Re-import its original cooked .utoc/.ucas container, then select it again.");
                }
            }

            var shippable = svc.ReferencedShippable(lib, referenced);
            if (shippable.Count == 0)
            {
                return;
            }

            var conflicts = svc.ValidateStagingSet(shippable);
            if (conflicts.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, conflicts));
            }

            var total = 0;
            foreach (var entry in shippable)
            {
                var staged = svc.StageInto(entry, contentRootToPackage);
                if (staged <= 0)
                {
                    throw new InvalidOperationException(
                        $"Referenced library animation '{entry.Name}' has no cached cooked files to stage for {entry.PackagePath}.");
                }
                total += staged;
                AppendLog($"  library anim '{entry.Name}' ({entry.SourceMode}): staged {staged} file(s) → {entry.PackagePath}");
            }
            AppendLog($"Library animations staged into pak: {shippable.Count} entr(y/ies), {total} file(s).");
        }
        catch (Exception ex)
        {
            AppendLog($"  could not stage required library animations: {ex.Message}");
            throw new InvalidOperationException(
                "Required library animation staging failed; release preparation was stopped.",
                ex);
        }
    }
}
