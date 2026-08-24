using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// The part browser, part grafting, and everything the character figure drives.
/// </summary>
public sealed partial class MainForm
{
    private const string CompletedGraftStageMarkerName = ".batcomputer-stage-complete";
    private const string IncompleteDeclarativeStageMarkerName = ".batcomputer-declarative-stage-incomplete";

    /// <summary>Right-click menu for a part tile (drag-only; menu exposes select/apply).</summary>
    private ContextMenuStrip BuildPartTileMenu(NativeSuitPartRecord part)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Inspect part in 3D…", null, (_, _) => ShowPartInspector(part));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Apply to character", null, async (_, _) => await ApplyToyboxPartDropToCharacterAsync(part));
        menu.Items.Add("Select for advanced graft", null, (_, _) => SelectToyboxPart(part));
        menu.Items.Add("Copy source path", null, (_, _) => { try { Clipboard.SetText(part.SourcePackagePath); } catch { /* clipboard busy */ } });
        return menu;
    }

    private void FocusInspectorOnSlot(string component, int slot)
    {
        _pendingInspectorComponentFocus = component;
        _pendingInspectorSlotFocus = slot;

        if (_isRefreshingInspector)
        {
            return;
        }

        SelectInspectorNodeForSlot(component, slot);
    }

    /// <summary>Figure → inspector sync: expands and scrolls to the component's card.</summary>
    private void SelectInspectorNodeForSlot(string component, int slot)
    {
        _inspector.FocusComponent(component, slot);
    }

    private void RecordSlotDetail(string component, int slot, string mesh, string material, bool isDefault)
    {
        _slotDetails[$"{component}:{slot}"] = (mesh ?? "", material ?? "", isDefault);
    }

    /// <summary>
    /// Readout for a minifig region: what part it is, and which mesh/material is on it. Regions that
    /// cover several slots (torso = body + cape LODs + gliding) summarise the count.
    /// </summary>
    private MinifigDiagram.RegionInfo DescribeRegion(string region)
    {
        if (region.Equals("Glider", StringComparison.OrdinalIgnoreCase))
        {
            var type = string.IsNullOrWhiteSpace(_currentProject?.GliderType) ? "base" : _currentProject!.GliderType;
            var mat = _currentProject?.GliderMaterial;
            return new MinifigDiagram.RegionInfo
            {
                Title = "Glider",
                Detail = string.IsNullOrWhiteSpace(mat) ? $"{type} · drop a material" : $"{type} · {ShortMaterialName(mat!)}"
            };
        }

        if (region.Equals("Equipment", StringComparison.OrdinalIgnoreCase))
        {
            return new MinifigDiagram.RegionInfo
            {
                Title = "Equipment",
                Detail = _currentProject?.EquipmentSlots is { Count: > 0 } es
                    ? string.Join(", ", es.OrderBy(s => s.Slot).Select(s => s.Gadget))
                    : "none · click to add"
            };
        }

        // Every component this region covers (e.g. Head + Head_2, or the cape's LODs).
        var components = RegionComponents(region);
        if (components.Count == 0)
        {
            return new MinifigDiagram.RegionInfo { Title = region, Detail = "not on this base" };
        }

        // Prefer the component holding the current selection, else the first.
        var pick = components.FirstOrDefault(gp =>
                       gp.Key.Equals(_toyboxComponent, StringComparison.OrdinalIgnoreCase))
                   ?? components[0];

        var mesh = pick
            .Select(t => _slotDetails.TryGetValue($"{t.Component}:{t.Slot}", out var d) ? d.Mesh : "")
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

        var slots = pick.OrderBy(t => t.Slot).Select(t =>
        {
            var has = _slotDetails.TryGetValue($"{t.Component}:{t.Slot}", out var d);
            return new MinifigDiagram.SlotEntry
            {
                Component = t.Component,
                Slot = t.Slot,
                Material = has && !string.IsNullOrWhiteSpace(d.Material) ? ShortMaterialName(d.Material) : "",
                Overridden = has && !d.IsDefault,
            };
        }).ToList();

        return new MinifigDiagram.RegionInfo
        {
            Title = components.Count > 1
                ? $"{pick.First().Label}  ({pick.Key} +{components.Count - 1} more)"
                : $"{pick.First().Label}  ({pick.Key})",
            Mesh = string.IsNullOrWhiteSpace(mesh) ? "mesh: —" : $"mesh: {UnrealPathUtil.AssetName(mesh)}",
            Slots = slots,
        };
    }

    /// <summary>The components a region covers, grouped by component name.</summary>
    private List<IGrouping<string, (string Label, string Component, int Slot)>> RegionComponents(string region) =>
        _characterSlots
            .Where(t => ClassifyRegion(t.Component).Equals(region, StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.Component, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void PopulateToyboxSlots()
    {
        _characterSlots.Clear();
        _characterSlots.AddRange(DiscoverToyboxSlots()
            .Where(slot => IsToyboxVisualComponent(slot.Component)));

        // The figure needs its part art. A build made without Assets/ has none, so fall back to
        // the slot list rather than showing an empty panel.
        var useMinifig = AppSettings.Current.UseMinifigCharacterPanel && MinifigDiagram.HasArt;
        _yourCharacter.SetMinifigMode(useMinifig);

        ClearAndDisposeControls(_yourCharacter.SlotFlow);
        if (!useMinifig)
        {
            // Classic list mode: a row per mesh slot, plus the "MORE" non-mesh aspects.
            foreach (var (label, component, slot) in _characterSlots)
            {
                _yourCharacter.SlotFlow.Controls.Add(BuildSlotRow(label, component, slot));
            }

            _yourCharacter.SlotFlow.Controls.Add(BuildSectionDivider("MORE"));

            var gliderType = string.IsNullOrWhiteSpace(_currentProject?.GliderType) ? "base" : _currentProject!.GliderType;
            var gliderMat = _currentProject?.GliderMaterial;
            var gliderSub = string.IsNullOrWhiteSpace(gliderMat)
                ? $"{gliderType} · drag a material here"
                : $"{gliderType} · {ShortMaterialName(gliderMat!)}";
            _yourCharacter.SlotFlow.Controls.Add(BuildActionRow(
                "Glider", gliderSub, Theme.Gliders,
                onClick: () => SelectComboValue(_toyboxCategoryCombo, "Materials"),
                onMaterialDrop: materialPath => _ = ApplyGliderMaterialAsync(materialPath)));

            var gadgets = _currentProject?.EquipmentSlots is { Count: > 0 } es
                ? string.Join(", ", es.OrderBy(s => s.Slot).Select(s => s.Gadget))
                : "none · click to add";
            _yourCharacter.SlotFlow.Controls.Add(BuildActionRow(
                "Equipment", gadgets, Theme.Equipment,
                onClick: () => SelectComboValue(_toyboxCategoryCombo, "Equipment"),
                onMaterialDrop: null));

            var animCount = (_currentProject?.AnimationOverrides.Count ?? 0) + (_currentProject?.LocomotionOverrides.Count ?? 0);
            _yourCharacter.SlotFlow.Controls.Add(BuildActionRow(
                "Animations", animCount > 0 ? $"{animCount} override(s) · click to edit" : "none · click to edit", Theme.Animations,
                onClick: () => SelectComboValue(_toyboxCategoryCombo, "Animations"),
                onMaterialDrop: null));

            // Straight from the suit you are building into the 3D viewer, so you can check the
            // look without hunting for the character in the list.
            _yourCharacter.SlotFlow.Controls.Add(BuildActionRow(
                "View in 3D", "see this suit rendered", Theme.Gold,
                onClick: ViewCurrentSuitIn3D,
                onMaterialDrop: null));
        }

        UpdateSlotDots();
    }

    private async Task ApplyGliderMaterialAsync(string materialPath)
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        if (GliderService.WingsuitCharFromMaterial(materialPath) is not null)
        {
            if (_partIndex is null)
            {
                LoadPartIndexAndRefreshGrid(logIfMissing: false);
            }

            var wingsuit = GliderService.FindWingsuitPartForMaterial(
                _partIndex,
                materialPath,
                "playable");
            if (wingsuit is null)
            {
                AppendLog("Glider: the matching native wingsuit was not found. Rebuild the part index and try again.");
                return;
            }

            await ApplyNativeGliderPresetAsync(wingsuit, materialPath);
            return;
        }

        var glideComponent = new AnimArchetypeGraftService().BaseGlideVisualComponent(_currentProject);
        if (string.IsNullOrWhiteSpace(glideComponent))
        {
            AppendLog("Glider material: this base has no native glide-visual component to recolor.");
            return;
        }

        var compatibility = GliderService.CheckMaterialCompatibility(
            ActiveGliderVisualPart(_currentProject),
            materialPath);
        AppendLog($"Glider material check: {compatibility.Title}. {compatibility.Detail}");
        if (compatibility.NeedsConfirmation && !ConfirmGliderMaterialOverride(compatibility, materialPath))
        {
            return;
        }

        _currentProject.GliderMaterial = materialPath;
        _currentProject.GliderType = "";
        _currentProject.GliderGrafted = false;
        RecordChange("Materials", "Glider material", UnrealPathUtil.AssetName(materialPath), status: "staged");
        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(_currentProject); } catch { /* best effort */ }
        AppendLog($"Set glider material → {UnrealPathUtil.AssetName(materialPath)}.");
        _matAssignComponentText.Text = glideComponent;
        _matAssignSlotText.Text = "0";
        _matAssignMiText.Text = materialPath;
        SelectComboValue(_matAssignContextCombo, "both");
        await ApplyMaterialAssignmentAsync();

        _session.RaiseChanged();
    }

    private NativeSuitPartRecord? ActiveGliderVisualPart(NativeSuitProject project)
    {
        var graft = project.PartGrafts.LastOrDefault(part => part.IsGlider);
        return ResolveLivePart(graft?.Playable) ?? ResolveLivePart(graft?.Cutscene);
    }

    private bool ConfirmGliderMaterialOverride(GliderMaterialCompatibilityResult compatibility, string materialPath)
    {
        var model = new Dialog.Model
        {
            WindowTitle = "Glide material check",
            Title = compatibility.Title,
            Subtitle = UnrealPathUtil.AssetName(materialPath),
            Message = compatibility.Detail,
            Severity = Dialog.Level.Warn,
            PrimaryText = "Use anyway",
            SecondaryText = "Cancel",
            CalloutTitle = "Preview before release",
            CalloutDetail = "A glider mesh can use a different UV layout from a regular cape or body. This warning does not block deliberate material experiments."
        };
        model.Fields.Add(("Selected material", materialPath));
        return Dialog.Show(this, model);
    }

    private string? ActiveGliderVisualComponent(NativeSuitProject project)
    {
        var graft = project.PartGrafts.LastOrDefault(part => part.IsGlider);
        if (graft is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(graft.ResolvedComponent))
        {
            return graft.ResolvedComponent;
        }

        var nativeComponent = new AnimArchetypeGraftService().BaseGlideVisualComponent(project);
        return !string.IsNullOrWhiteSpace(nativeComponent)
            ? nativeComponent
            : graft.Slot;
    }

    private List<VirtualTilePanel.Tile> NativeBodyProfileTiles()
    {
        var search = CurrentToyboxSearch();
        var currentId = _currentProject?.BodyProfile?.Id ?? "";
        return NativeBodyProfileService.Catalog()
            .Where(profile => MatchesToyboxSearch(
                search,
                profile.DisplayName,
                profile.GeometryFamily,
                profile.MeshPackagePath,
                string.Join(" ", profile.MissingRegions)))
            .Select(profile => new VirtualTilePanel.Tile
            {
                Section = profile.MissingRegions.Count == 0 ? "STANDARD BODIES" : "REDUCED NATIVE BODIES",
                Title = (profile.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase) ? "● " : "") + profile.DisplayName,
                Subtitle = profile.MissingRegions.Count == 0
                    ? $"{profile.GeometryFamily} · complete body"
                    : $"missing {string.Join(", ", profile.MissingRegions).ToLowerInvariant()}",
                Accent = profile.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase) ? Theme.Gold : Theme.Parts,
                OnClick = () => _ = ApplyNativeBodyProfileAsync(profile, confirm: true),
                ToolTip =
                    $"Native mesh: {profile.MeshPackagePath}\n" +
                    $"Skeleton: {profile.SkeletonPackagePath}\n" +
                    $"Evidence: {profile.EvidenceTier}\n" +
                    (profile.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", profile.Warnings)),
            })
            .ToList();
    }

    private async Task<bool> ApplyNativeBodyProfileAsync(NativeBodyProfile profile, bool confirm)
    {
        EnsureProject();
        var project = _currentProject;
        if (project?.PlayableTemplate is null || project.CutsceneTemplate is null)
        {
            AppendLog("Choose a playable + cutscene base before selecting a native body profile.");
            SelectComboValue(_toyboxCategoryCombo, "Base");
            return false;
        }

        var warning = profile.MissingRegions.Count == 0
            ? "This keeps the gameplay donor's animation class and runtime behavior, and replaces only CharacterMesh0's native mesh."
            :
                $"This body intentionally has no {string.Join(", ", profile.MissingRegions).ToLowerInvariant()}.\n\n" +
                string.Join("\n", profile.Warnings) +
                "\n\nYou can add a compatible native replacement part separately.";
        if (confirm && !Dialog.Confirm(
                this,
                "Use native body profile",
                $"Use '{profile.DisplayName}' for both playable and cutscene?\n\n{warning}",
                confirmText: "Use body"))
        {
            return false;
        }

        NativeBodyProfile? previous;
        try
        {
            previous = project.BodyProfile is null
                ? null
                : JsonSerializer.Deserialize<NativeBodyProfile>(JsonSerializer.Serialize(project.BodyProfile));
        }
        catch
        {
            previous = project.BodyProfile;
        }

        project.BodyProfile = NativeBodyProfileService.Find(profile.Id) ?? profile;
        project.BodyProfile.SourceVisualPackage = profile.SourceVisualPackage;
        try
        {
            await RebuildGraftStageFromDeclarativeAsync(project, _projectRootText.Text.Trim());
            RecordChange(project, "Parts", "Native body", profile.DisplayName, status: "staged");
            AppendLog($"Native body profile = {profile.DisplayName}. Gameplay donor behavior and animation class were preserved.");
            RefreshInspector();
            RefreshToyboxTiles();
            return true;
        }
        catch (Exception ex)
        {
            project.BodyProfile = previous;
            AppendLog("Native body profile was not changed: " + ex.Message);
            Dialog.Error(
                this,
                "Body profile could not be applied",
                "The previous project and generated stage were kept.\n\n" + ex.Message);
            RefreshToyboxTiles();
            return false;
        }
    }

    private static bool GraftTargetsComponent(SavedPartGraft graft, string component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(graft.ResolvedComponent) &&
                string.Equals(graft.ResolvedComponent, component, StringComparison.OrdinalIgnoreCase)) ||
               (string.IsNullOrWhiteSpace(graft.ResolvedComponent) &&
                string.Equals(graft.Slot, component, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PairedCapeAdapterTargetsComponent(NativeSuitProject project, string component)
    {
        var adapter = project.PairedCapeAdapter;
        if (adapter is null || string.IsNullOrWhiteSpace(component))
        {
            return false;
        }

        if (string.Equals(adapter.ResolvedCosmeticComponent, component, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(adapter.ResolvedGliderComponent, component, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return project.PartGrafts.Any(graft =>
            (string.Equals(graft.InstanceId, adapter.CosmeticCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(graft.InstanceId, adapter.GlideCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase)) &&
            GraftTargetsComponent(graft, component));
    }

    /// <summary>
    /// The authored paired-cape shell is one atomic runtime layout. Once either member is removed,
    /// retaining the other member would replay a shell-only Cape/Torso slot onto the glide-only
    /// gameplay Blueprint and could synthesize a reflected field into its opaque cooked CDO.
    /// Remove both declarations together and only disable the custom archetype when the glider
    /// itself automatically enabled it; explicit animation/equipment ownership remains intact.
    /// </summary>
    private static List<string> RemovePairedCapeAdapterAtomically(NativeSuitProject project)
    {
        var adapter = project.PairedCapeAdapter;
        if (adapter is null)
        {
            return [];
        }

        var grafts = project.PartGrafts ??
            throw new InvalidOperationException(
                "The paired-cape adapter cannot be removed safely because its declarative graft list is missing.");
        static bool SamePackage(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            UnrealPathUtil.NormalizePackagePath(left).Equals(
                UnrealPathUtil.NormalizePackagePath(right),
                StringComparison.OrdinalIgnoreCase);
        static bool MatchesRole(
            SavedPartGraft graft,
            bool isGlider,
            string? playableSource,
            string? cutsceneSource,
            string expectedSlot)
        {
            if (graft.IsGlider != isGlider)
            {
                return false;
            }
            // An instance ID is never sufficient by itself: malformed JSON could point the
            // cosmetic ID at an unrelated non-glider Head. Resolve both current and stale IDs by
            // the complete authored field + playable/cutscene source recipe instead.
            return string.Equals(graft.Slot, expectedSlot, StringComparison.OrdinalIgnoreCase) &&
                   graft.Playable is not null && graft.Cutscene is not null &&
                   SamePackage(graft.Playable.SourcePackagePath, playableSource) &&
                   SamePackage(graft.Cutscene.SourcePackagePath, cutsceneSource);
        }

        var cosmeticMatches = grafts.Where(graft => graft is not null && MatchesRole(
                graft,
                isGlider: false,
                adapter.CosmeticPlayableSourcePackage,
                adapter.CosmeticCutsceneSourcePackage,
                expectedSlot: "Cape"))
            .Take(2)
            .ToList();
        var gliderMatches = grafts.Where(graft => graft is not null && MatchesRole(
                graft,
                isGlider: true,
                adapter.GliderPlayableSourcePackage,
                adapter.GliderCutsceneSourcePackage,
                expectedSlot: "Torso"))
            .Take(2)
            .ToList();
        if (cosmeticMatches.Count != 1 || gliderMatches.Count != 1 ||
            ReferenceEquals(cosmeticMatches[0], gliderMatches[0]))
        {
            throw new InvalidOperationException(
                "The paired-cape adapter could not resolve exactly one bound cosmetic Cape and one distinct bound Torso glider. " +
                "No adapter state was changed; refresh the part index and rebuild or re-apply the pair before changing the base/glider.");
        }
        var boundGrafts = new[] { cosmeticMatches[0], gliderMatches[0] };
        var removedComponents = boundGrafts
            .Select(graft => !string.IsNullOrWhiteSpace(graft.ResolvedComponent)
                ? graft.ResolvedComponent
                : graft.Slot)
            .Concat(new[]
            {
                adapter.ResolvedCosmeticComponent,
                adapter.ResolvedGliderComponent,
            })
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        project.PartGrafts.RemoveAll(graft => boundGrafts.Any(bound => ReferenceEquals(bound, graft)));
        project.PairedCapeAdapter = null;
        project.GliderType = "";
        project.GliderMaterial = "";
        project.GliderGrafted = false;
        project.GliderAnimLas = "";
        project.GliderAnimMas = "";
        if (project.GliderAutoEnabledCustomArchetype)
        {
            project.UseCustomArchetype = false;
        }
        project.GliderAutoEnabledCustomArchetype = false;

        return removedComponents;
    }

    internal static IReadOnlyList<string> RemovePairedCapeAdapterAtomicallyForTest(NativeSuitProject project) =>
        RemovePairedCapeAdapterAtomically(project);

    private static bool RequirementTargetsComponent(string targetComponent, string component)
    {
        if (string.IsNullOrWhiteSpace(targetComponent) || string.IsNullOrWhiteSpace(component))
        {
            return false;
        }

        var target = targetComponent.Trim();
        var colon = target.LastIndexOf(':');
        if (colon > 0)
        {
            target = target[..colon];
        }

        return target.Equals(component.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool RemoveSavedRemovalForComponent(NativeSuitProject project, string component)
    {
        var before = project.Requirements.Count;
        project.Requirements.RemoveAll(requirement =>
            requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
            RequirementTargetsComponent(requirement.TargetComponent, component));
        return project.Requirements.Count != before;
    }

    /// <summary>
    /// A static hair/helmet cloned into a Batman-style base cannot reuse the occupied
    /// <c>Head</c> SCS slot, so it is added as (for example) <c>Head_2</c>. In that
    /// case the original Head is the donor cowl, not the shared minifig head, and must
    /// be removed for the selected character's own head visual to be authoritative.
    /// </summary>
    private static bool HasCrossKindHeadGraft(NativeSuitProject project) =>
        project.PartGrafts.Any(graft =>
            graft.Slot.Equals("Head", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(graft.ResolvedComponent) &&
            !RequirementTargetsComponent(graft.ResolvedComponent, graft.Slot));

    internal static bool CrossKindHeadGraftNeedsCowlRemovalForTest(NativeSuitProject project) =>
        HasCrossKindHeadGraft(project);

    private bool EnsureCrossKindHeadGraftHidesBaseHead(NativeSuitProject project)
    {
        if (!HasCrossKindHeadGraft(project) || project.Requirements.Any(requirement =>
                requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
                RequirementTargetsComponent(requirement.TargetComponent, "Head")))
        {
            return false;
        }

        project.Requirements.Add(new NativeSuitRequirement
        {
            Id = "remove-head-0",
            Kind = "remove-component",
            SourcePackage = project.TargetPackages.Playable,
            TargetComponent = ToyboxSlotKey("Head", 0),
            Notes = "Hidden because a cross-kind head graft replaces the donor cowl."
        });
        return true;
    }

    private bool EnsureVisualHeadAttachmentHidesDonorHead(NativeSuitProject project)
    {
        if (project.Requirements.Any(requirement =>
                requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
                RequirementTargetsComponent(requirement.TargetComponent, "Head")))
        {
            return false;
        }

        project.Requirements.Add(new NativeSuitRequirement
        {
            Id = "remove-head-0",
            Kind = "remove-component",
            SourcePackage = project.TargetPackages.Playable,
            TargetComponent = ToyboxSlotKey("Head", 0),
            Notes = "Hidden because the visual base supplies its own head attachment."
        });
        return true;
    }

    /// <summary>
    /// Drops any saved material assignments targeting <paramref name="component"/>. Used when
    /// a glider preset is applied to the base's glide-visual component: the glider brings its
    /// OWN materials (mesh decal + solid), and re-applying a stale override for that component
    /// (e.g. an old cape material) would paint over the glider. Recolor via the glider decal
    /// override instead, not a component material assignment.
    /// </summary>
    private bool ClearMaterialAssignmentsForComponent(NativeSuitProject project, string component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return false;
        }
        var before = project.MaterialAssignments.Count;
        project.MaterialAssignments.RemoveAll(a =>
            a.Component.Equals(component, StringComparison.OrdinalIgnoreCase));
        return project.MaterialAssignments.Count != before;
    }

    private DeclarativeReplayOutcome RestoreProtectedGliderComponent(
        NativeSuitProject project,
        string glideComponent,
        string? projectRootOverride = null,
        string? stageContentRootOverride = null)
    {
        var outcome = new DeclarativeReplayOutcome();
        var projectRoot = string.IsNullOrWhiteSpace(projectRootOverride)
            ? _projectRootText.Text.Trim()
            : projectRootOverride;
        var service = new ComponentRemoveService(projectRoot);
        var result = RunWithStructuredFileLockRetry(
            () => string.IsNullOrWhiteSpace(stageContentRootOverride)
                ? service.RestoreScsReferences(
                    project.SlotId,
                    project.TargetPackages.Playable,
                    project.TargetPackages.Cutscene,
                    glideComponent)
                : service.RestoreScsReferencesInContentRoot(
                    stageContentRootOverride,
                    project.SlotId,
                    project.TargetPackages.Playable,
                    project.TargetPackages.Cutscene,
                    glideComponent),
            restoreResult => restoreResult.TransientFileLock ||
                             restoreResult.Files.Any(file => file.TransientFileLock),
            $"restore protected glider component '{glideComponent}'");

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppendLog($"Glider: restore check for '{glideComponent}' reported {result.Status}: {result.Error}");
        }

        foreach (var file in result.Files)
        {
            if (file.RestoredNodeReferences > 0)
            {
                AppendLog($"Glider: restored {file.RestoredNodeReferences} SCS reference(s) for '{glideComponent}' in {file.Role}.");
            }
            else if (!file.Success && !string.IsNullOrWhiteSpace(file.Error))
            {
                AppendLog($"Glider: could not restore '{glideComponent}' in {file.Role}: {file.Error}");
            }
        }

        foreach (var required in new[]
                 {
                     (Role: "playable", Package: project.TargetPackages.Playable),
                     (Role: "cutscene", Package: project.TargetPackages.Cutscene),
                 })
        {
            if (string.IsNullOrWhiteSpace(required.Package))
            {
                outcome.Failures.Add($"{glideComponent}/{required.Role}: target package path is empty");
                continue;
            }

            var file = result.Files.FirstOrDefault(candidate =>
                candidate.Role.Equals(required.Role, StringComparison.OrdinalIgnoreCase));
            if (file is not null && file.Success && file.ComponentFound)
            {
                continue;
            }

            outcome.Failures.Add(
                $"{glideComponent}/{required.Role}: {file?.Error ?? result.Error ?? "no restore result was returned"}");
            outcome.TransientFileLock |= file?.TransientFileLock == true || result.TransientFileLock;
        }

        return outcome;
    }

    private async Task ApplyNativeGliderPresetAsync(NativeSuitPartRecord part, string? materialOverride = null)
    {
        EnsureProject();
        var project = _currentProject;
        if (project is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_slotIdText.Text.Trim()))
        {
            AppendLog("Glider: set a base character first (Base -> Pick base character), then set the glider.");
            return;
        }

        ReadFieldsIntoProject(project);

        var capeGlideContract = new AnimArchetypeGraftService().BaseCapeGlideContract(project);
        if (GliderService.HasAdditiveCapeAndGliderCombination(
                project,
                capeGlideContract,
                addingGlider: true))
        {
            Dialog.Error(this,
                "Custom Cape and glider are not compatible",
                "This suit has a custom static mesh attached to Cape. Custom meshes are additive components and are not driven by the playable base's native cape/glider visibility wiring, even on a native two-cape base.\n\n" +
                "Remove the custom Cape attachment before applying a glider preset.",
                windowTitle: "Gliders");
            return;
        }

        var glideComponent = new AnimArchetypeGraftService().BaseGlideVisualComponent(project);
        if (string.IsNullOrWhiteSpace(glideComponent))
        {
            AppendLog("Glider: this base has no native glide visual. A dedicated Glider component and the native gliding ability set will be added.");
        }

        if (_partIndex is null)
        {
            LoadPartIndexAndRefreshGrid(logIfMissing: false);
        }

        var playable = part.Context.Equals("playable", StringComparison.OrdinalIgnoreCase)
            ? part
            : FindCounterpartPart(part, "playable");
        var cutscene = part.Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase)
            ? part
            : FindCounterpartPart(part, "cutscene");

        if (playable is null && cutscene is null)
        {
            AppendLog("Glider: no playable or cutscene donor could be matched for that native glider preset.");
            return;
        }

        if (BlockUnsupportedCapeGliderPairing(
                project,
                capeGlideContract,
                incomingGraft: ProspectivePartGraft(
                    isGlider: true,
                    playable,
                    cutscene),
                windowTitle: "Gliders"))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(materialOverride))
        {
            if (playable is not null)
            {
                playable = GliderService.WithWingsuitDecalOverride(playable, materialOverride);
            }
            if (cutscene is not null)
            {
                cutscene = GliderService.WithWingsuitDecalOverride(cutscene, materialOverride);
            }
        }
        var selectedGliderMaterial = !string.IsNullOrWhiteSpace(materialOverride)
            ? materialOverride
            : (playable ?? cutscene)?.Materials.FirstOrDefault()?.PackagePath ?? "";

        var presetName = GliderService.GliderPresetLabel(playable ?? cutscene!);
        _selectedPlayablePart = playable;
        _selectedCutscenePart = cutscene;
        UpdateSelectedPartLabels();

        AppendLog($"Glider: applying native preset '{presetName}' to base glide component '{glideComponent}' ({GliderService.GliderPresetSubtitle(playable ?? cutscene!)})…");
        if (!await GraftSelectedPartsAsync())
        {
            AppendLog("Glider: the preset was not marked active because its declarative rebuild did not complete.");
            return;
        }
        project.GliderType = $"native:{presetName}";
        project.GliderMaterial = selectedGliderMaterial;
        project.GliderGrafted = true;
        RecordChange("Gliders", "Native glider preset", presetName, status: "staged");
        try { (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim())).SaveProject(project); } catch { /* keep the staged result usable */ }
        _session.RaiseChanged();
    }

    private List<(string Label, string Component, int Slot)> DiscoverToyboxSlots()
    {
        _slotDetails.Clear();
        var slotId = _slotIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(slotId))
        {
            return ToyboxSlots
                .Where(slot => !IsToyboxSlotRemoved(slot.Component, slot.Slot))
                .ToList();
        }

        try
        {
            var service = new MaterialReplaceService(_projectRootText.Text.Trim());
            var discovered = new Dictionary<string, (string Label, string Component, int Slot)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (role, packagePath) in new[]
            {
                ("playable", _targetPlayableText.Text.Trim()),
                ("cutscene", _targetCutsceneText.Text.Trim())
            })
            {
                var report = service.DescribeStageComponents(slotId, role, packagePath);
                if (!report.Found)
                {
                    continue;
                }

                foreach (var component in report.Components)
                {
                    if (!IsToyboxVisualComponent(component.Name))
                    {
                        continue;
                    }
                    if (component.Slots.Count == 0)
                    {
                        AddDiscoveredSlot(discovered, component.Name, 0);
                        RecordSlotDetail(component.Name, 0, component.Mesh, "", true);
                        continue;
                    }

                    foreach (var slot in component.Slots)
                    {
                        AddDiscoveredSlot(discovered, component.Name, slot.Slot);
                        RecordSlotDetail(component.Name, slot.Slot, component.Mesh, slot.Material, slot.IsDefault);
                    }
                }
            }

            // The material inspector sees mesh exports and override-material slots,
            // but newly grafted parts can be missed if they have no override slots
            // yet or if a staged package is in an unusual SCS shape. Merge in the
            // live SCS InternalVariableName list so "Your Character" reflects the
            // constructed components, not only the components with material data.
            var removeService = new ComponentRemoveService(_projectRootText.Text.Trim());
            foreach (var packagePath in new[]
            {
                _targetPlayableText.Text.Trim(),
                _targetCutsceneText.Text.Trim()
            })
            {
                foreach (var component in removeService.ListScsComponentNames(slotId, packagePath, ""))
                {
                    if (ShouldShowToyboxScsComponent(component))
                    {
                        AddDiscoveredSlot(discovered, component, 0);
                    }
                }
            }

            if (discovered.Count > 0)
            {
                return discovered.Values
                    .Where(entry => IsToyboxVisualComponent(entry.Component))
                    .Where(entry => !IsToyboxSlotRemoved(entry.Component, entry.Slot))
                    .OrderBy(entry => SlotSortKey(entry.Component, entry.Slot))
                    .ThenBy(entry => entry.Component, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Slot)
                    .ToList();
            }
        }
        catch
        {
            // Fall through to starter slots. Inspector/log calls surface detailed
            // errors; the left rail should stay usable even if one asset is odd.
        }

        return ToyboxSlots
            .Where(slot => !IsToyboxSlotRemoved(slot.Component, slot.Slot))
            .ToList();
    }

    private static void AddDiscoveredSlot(
        Dictionary<string, (string Label, string Component, int Slot)> discovered,
        string component,
        int slot)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return;
        }

        var key = $"{component}:{slot}";
        if (discovered.ContainsKey(key))
        {
            return;
        }

        discovered[key] = (FriendlySlotLabel(component, slot), component, slot);
    }

    private static bool ShouldShowToyboxScsComponent(string component) =>
        IsToyboxVisualComponent(component);

    private static bool IsToyboxVisualComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return false;
        }

        var name = component.Trim();
        if (name.Equals("DefaultSceneRoot", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Root", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var (baseName, _) = SplitGeneratedDuplicateComponent(name);
        return baseName.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Mesh", StringComparison.OrdinalIgnoreCase) ||
               baseName.StartsWith("Face", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Head", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Hair", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Hat", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Hip", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Torso", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Torso1", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("Torso2", StringComparison.OrdinalIgnoreCase) ||
               baseName.StartsWith("Cape", StringComparison.OrdinalIgnoreCase) ||
               new[]
               {
                   "body", "cowl", "hair", "hat", "helmet", "cape", "cloak", "collar",
                   "spine", "shoulder", "pauldron", "belt", "hip", "pelvis", "wing",
                   "backpack", "batpack", "tail", "horn", "costume", "arm", "hand", "leg", "foot"
               }.Any(token => baseName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string FriendlySlotLabel(string component, int slot)
    {
        var (baseComponent, duplicateNumber) = SplitGeneratedDuplicateComponent(component);

        if (baseComponent.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase))
        {
            return slot == 0 ? AppendDuplicateLabel("Body", duplicateNumber) : $"Body material {slot}";
        }
        if (baseComponent.Equals("Head", StringComparison.OrdinalIgnoreCase))
        {
            return slot == 0 ? AppendDuplicateLabel("Head / cowl", duplicateNumber) : $"Head material {slot}";
        }
        if (baseComponent.Equals("Cape", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDuplicateLabel($"Cape LOD{slot}", duplicateNumber);
        }
        if (baseComponent.Equals("Hip", StringComparison.OrdinalIgnoreCase))
        {
            return slot == 0 ? AppendDuplicateLabel("Hip / belt", duplicateNumber) : $"Hip material {slot}";
        }
        if (baseComponent.Equals("Torso2", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDuplicateLabel("Torso2 / chest add-on", duplicateNumber);
        }
        if (baseComponent.Equals("Hair", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDuplicateLabel("Hair", duplicateNumber);
        }
        if (baseComponent.StartsWith("Face", StringComparison.OrdinalIgnoreCase))
        {
            return AppendDuplicateLabel("Face", duplicateNumber);
        }

        return slot == 0 ? AppendDuplicateLabel(component, duplicateNumber) : $"{component} material {slot}";
    }

    private static int SlotSortKey(string component, int slot)
    {
        var (baseComponent, duplicateNumber) = SplitGeneratedDuplicateComponent(component);
        var rank = baseComponent.ToLowerInvariant() switch
        {
            "charactermesh0" => 0,
            "head" => 10,
            "face" => 20,
            "hair" => 30,
            "cape" => 40,
            "torso" => 50,
            "torso1" => 51,
            "torso2" => 52,
            "hip" => 60,
            _ => 100
        };
        return rank * 10000 + duplicateNumber * 100 + slot;
    }

    private Control BuildSlotRow(string label, string component, int slot)
    {
        var row = new RoundedPanel { Width = 206, Height = 42, Margin = new Padding(2, 2, 2, 2), BackColor = Theme.CardBg, CornerRadius = Theme.RadiusSm, Cursor = Cursors.Hand, Tag = (label, component, slot), AllowDrop = true };
        var dot = new StatusDot { Name = "dot", Width = 10, Height = 10, Left = 8, Top = 16, DotColor = Theme.DefaultDot };
        var name = new Label { Text = label, Left = 24, Top = 4, Width = 150, Height = 16, AutoSize = false, BackColor = Color.Transparent, ForeColor = Theme.OnDark, Font = AppFonts.Condensed(9f, FontStyle.Bold) };
        var sub = new Label { Text = $"{component} · slot {slot}", Left = 24, Top = 21, Width = 155, Height = 14, AutoSize = false, BackColor = Color.Transparent, ForeColor = Theme.OnDarkMuted, Font = AppFonts.Condensed(7.5f, FontStyle.Bold) };
        var menu = new Button { Text = "⋯", Left = 176, Top = 8, Width = 26, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = Theme.OnDarkMuted };
        menu.FlatAppearance.BorderSize = 0;
        var ctx = BuildSlotContextMenu(label, component, slot);
        row.ContextMenuStrip = ctx;
        menu.Click += (_, _) => { SelectToyboxSlot(label, component, slot); ctx.Show(menu, new Point(0, menu.Height)); };

        void Select(object? s, EventArgs e) => SelectToyboxSlot(label, component, slot);
        row.Click += Select; name.Click += Select; sub.Click += Select; dot.Click += Select;
        WireToyboxSlotDropTarget(row, row, label, component, slot);
        WireToyboxSlotDropTarget(name, row, label, component, slot);
        WireToyboxSlotDropTarget(sub, row, label, component, slot);
        WireToyboxSlotDropTarget(dot, row, label, component, slot);
        row.Controls.Add(dot);
        row.Controls.Add(name);
        row.Controls.Add(sub);
        row.Controls.Add(menu);
        return row;
    }

    private ContextMenuStrip BuildSlotContextMenu(string label, string component, int slot)
    {
        var ctx = new ContextMenuStrip();
        ctx.Items.AddRange(BuildSlotMenuItems(label, component, slot));
        return ctx;
    }

    /// <summary>The per-slot actions, as fresh items (so they can go in a menu OR a submenu).</summary>
    private ToolStripItem[] BuildSlotMenuItems(string label, string component, int slot)
    {
        return new ToolStripItem[]
        {
            new ToolStripMenuItem("Change material here", null, (_, _) => { SelectToyboxSlot(label, component, slot); SelectComboValue(_toyboxCategoryCombo, "Materials"); }),
            new ToolStripMenuItem("Create new material…", null, (_, _) => { SelectToyboxSlot(label, component, slot); OpenMaterialWizard(); }),
            new ToolStripMenuItem("Browse game material…", null, (_, _) => { SelectToyboxSlot(label, component, slot); BrowseAndApplyGameMaterial(); }),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Remove / hide part", null, async (_, _) =>
            {
                SelectToyboxSlot(label, component, slot);
                await RemoveToyboxPartAsync(label, component, slot);
            }),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Copy component name", null, (_, _) => { try { Clipboard.SetText(component); AppendLog($"Copied: {component}"); } catch { } }),
            new ToolStripMenuItem("Refresh inspector", null, (_, _) => RefreshInspector()),
        };
    }

    /// <summary>
    /// Right-click on a minifig region: the slot actions that used to live on each slot row's
    /// context menu. Several slots in one region get a submenu each.
    /// </summary>
    private void ShowRegionContextMenu(string region)
    {
        // Grouped by component for the same reason as the left-click chooser.
        var components = RegionComponents(region);
        if (components.Count == 0)
        {
            return;
        }

        var menu = new ContextMenuStrip();
        if (components.Count == 1)
        {
            var only = components[0].OrderBy(t => t.Slot).First();
            SelectToyboxSlot(only.Label, only.Component, only.Slot);
            menu.Items.AddRange(BuildSlotMenuItems(only.Label, only.Component, only.Slot));
        }
        else
        {
            foreach (var group in components)
            {
                var first = group.OrderBy(t => t.Slot).First();
                var marked = group.Any(t => _customSlotKeys.Contains($"{t.Component}:{t.Slot}")) ? "● " : "";
                var item = new ToolStripMenuItem($"{marked}{first.Label}  ({group.Key})");
                item.DropDownItems.AddRange(BuildSlotMenuItems(first.Label, first.Component, first.Slot));
                menu.Items.Add(item);
            }
        }
        menu.Show(_yourCharacter.Diagram, _yourCharacter.Diagram.PointToClient(Cursor.Position));
    }

    private void SelectToyboxSlot(string label, string component, int slot)
    {
        _selection.Select(label, component, slot);
        foreach (Control c in _yourCharacter.SlotFlow.Controls)
        {
            if (c.Tag is ValueTuple<string, string, int> t)
            {
                RestoreToyboxSlotRowBackColor(c, t.Item2, t.Item3);
            }
        }
        _toyboxSelectionLabel.Text = $"Selected: {label}  ({component} · slot {slot}) — applies to playable + cutscene";
        _yourCharacter.Diagram.SetSelected(ClassifyRegion(component));
        FocusInspectorOnSlot(component, slot);
    }

    private void RestoreToyboxSlotRowBackColor(Control row, string component, int slot)
    {
        var selected = component.Equals(_toyboxComponent, StringComparison.OrdinalIgnoreCase) && slot == _toyboxSlot;
        row.BackColor = selected ? Theme.CardHi : Theme.CardBg;
        if (row is RoundedPanel rp)
        {
            rp.BorderColor = selected ? Theme.Gold : (Color?)null; // clean gold outline instead of the olive tint
        }
    }

    private void WireToyboxSlotDropTarget(Control control, Control row, string label, string component, int slot)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
        {
            var payload = TryGetToyboxDragPayload(e.Data);
            if (payload is null)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            e.Effect = DragDropEffects.Copy;
            row.BackColor = Theme.Tint(payload.Kind.Equals("part", StringComparison.OrdinalIgnoreCase) ? Theme.Parts : Theme.Materials);
        };
        control.DragOver += (_, e) =>
        {
            e.Effect = TryGetToyboxDragPayload(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        };
        control.DragLeave += (_, _) => RestoreToyboxSlotRowBackColor(row, component, slot);
        control.DragDrop += async (_, e) =>
        {
            RestoreToyboxSlotRowBackColor(row, component, slot);
            var payload = TryGetToyboxDragPayload(e.Data);
            if (payload is null)
            {
                return;
            }

            await ApplyToyboxDropAsync(payload, label, component, slot);
        };
    }

    /// <summary>
    /// Drop routing for the minifig figure. Parts apply to their natural slot (as before); materials
    /// apply to whatever body region they were dropped on, and the Glider tray slot takes a glider
    /// material - replacing the per-row drop targets the slot list used to provide.
    /// </summary>
    private void WireMinifigDropTarget(MinifigDiagram diagram)
    {
        diagram.AllowDrop = true;

        static bool IsPart(ToyboxDragPayload p) =>
            p.Kind.Equals("part", StringComparison.OrdinalIgnoreCase) && p.Part is not null;
        static bool IsMaterial(ToyboxDragPayload p) =>
            p.Kind.Equals("material", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.MaterialPath);

        void Update(DragEventArgs e)
        {
            var payload = TryGetToyboxDragPayload(e.Data);
            e.Effect = payload is not null && (IsPart(payload) || IsMaterial(payload))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        diagram.DragEnter += (_, e) => Update(e);
        diagram.DragOver += (_, e) =>
        {
            Update(e);
            var dragged = TryGetToyboxDragPayload(e.Data);
            diagram.SetPartDropHint(dragged is not null && IsPart(dragged)
                ? CleanPartMeshDisplayName(dragged.Part!)
                : null);
            // Light up whatever is under the cursor: a material slot row wins over the region.
            var p = diagram.PointToClient(new Point(e.X, e.Y));
            var slot = diagram.SlotAtPoint(p);
            diagram.SetHoverSlot(slot);
            diagram.SetHoverRegion(slot is null ? diagram.RegionAtPoint(p) : null);
        };
        diagram.DragLeave += (_, _) => { diagram.SetHoverRegion(null); diagram.SetHoverSlot(null); diagram.SetPartDropHint(null); };
        diagram.DragDrop += async (_, e) =>
        {
            diagram.SetHoverRegion(null);
            diagram.SetHoverSlot(null);
            diagram.SetPartDropHint(null);
            var payload = TryGetToyboxDragPayload(e.Data);
            if (payload is null) return;

            var p = diagram.PointToClient(new Point(e.X, e.Y));

            if (IsMaterial(payload))
            {
                // 1. Dropped on a specific material slot row → that slot only.
                if (diagram.SlotAtPoint(p) is { } target)
                {
                    var label = _characterSlots.FirstOrDefault(t =>
                        t.Component.Equals(target.Component, StringComparison.OrdinalIgnoreCase) && t.Slot == target.Slot).Label
                        ?? target.Component;
                    await ApplyToyboxDropAsync(payload, label, target.Component, target.Slot);
                    return;
                }

                var region = diagram.RegionAtPoint(p);
                if (string.Equals(region, "Glider", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyGliderMaterialAsync(payload.MaterialPath!);
                    return;
                }

                // 2. Dropped on a figure part → EVERY material slot on that part.
                var slots = region is null
                    ? new List<(string Label, string Component, int Slot)>()
                    : _characterSlots
                        .Where(t => ClassifyRegion(t.Component).Equals(region, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                if (slots.Count == 0)
                {
                    AppendLog("Drop a material onto a body part, a material slot, or the Glider slot.");
                    return;
                }

                // Apply to the whole component the region resolves to (all of its slots).
                var primary = slots[0].Component;
                var group = slots.Where(t => t.Component.Equals(primary, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var t in group)
                {
                    await ApplyToyboxDropAsync(payload, t.Label, t.Component, t.Slot);
                }
                AppendLog($"Applied to all {group.Count} material slot(s) on {primary}.");
                return;
            }

            if (IsPart(payload))
            {
                await ApplyToyboxPartDropToCharacterAsync(payload.Part!);
            }
        };
    }

    private async Task ApplyToyboxPartDropToCharacterAsync(NativeSuitPartRecord part)
    {
        var component = string.IsNullOrWhiteSpace(part.Slot) ? "Part" : part.Slot;
        var label = FriendlySlotLabel(component, 0);
        AppendLog($"Dropped part {CleanPartMeshDisplayName(part)} into Your Character. Native slot={component}.");
        await ApplyToyboxDropAsync(
            new ToyboxDragPayload { Kind = "part", Part = part },
            label,
            component,
            0);
    }

    private static string ToyboxSlotKey(string component, int slot)
    {
        return $"{component.Trim()}:{slot}";
    }

    private HashSet<string> RemovedToyboxSlotKeys()
    {
        if (_currentProject?.Requirements is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return _currentProject.Requirements
            .Where(requirement => requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase))
            .Select(requirement => requirement.TargetComponent)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool IsToyboxSlotRemoved(string component, int slot)
    {
        return RemovedToyboxSlotKeys().Contains(ToyboxSlotKey(component, slot));
    }

    private async Task RemoveToyboxPartAsync(string label, string component, int slot)
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);

        var removingPairedCapeMember = PairedCapeAdapterTargetsComponent(_currentProject, component);
        var protectedGliderComponent = ActiveGliderVisualComponent(_currentProject);
        if (!string.IsNullOrWhiteSpace(protectedGliderComponent) &&
            protectedGliderComponent.Equals(component, StringComparison.OrdinalIgnoreCase) &&
            !removingPairedCapeMember)
        {
            Dialog.Warn(null, "Glider component is protected", $"'{component}' is this base suit's native glide-visual component. The wingsuit system needs that component to stay constructed.\n\nChange the glider type back to base/none before removing it.");
            AppendLog($"Remove blocked: '{component}' is the active native glider component for this suit.");
            return;
        }

        AppendLog($"Removing {label} ({component} slot {slot}) from staged playable/cutscene assets…");

        NativeSuitProject previousProjectSnapshot;
        try
        {
            previousProjectSnapshot = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(_currentProject))
                ?? throw new InvalidOperationException("Could not snapshot the suit before removing the part.");
        }
        catch (Exception ex)
        {
            AppendLog("Remove/hide failed before staging: " + ex.Message);
            return;
        }

        var key = ToyboxSlotKey(component, slot);
        _currentProject.Requirements.RemoveAll(requirement =>
            requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
            requirement.TargetComponent.Equals(key, StringComparison.OrdinalIgnoreCase));

        _currentProject.Requirements.Add(new NativeSuitRequirement
        {
            Id = $"remove-{component}-{slot}".Replace(' ', '-').ToLowerInvariant(),
            Kind = "remove-component",
            SourcePackage = _targetPlayableText.Text.Trim(),
            TargetComponent = key,
            Notes = $"Removed declaratively from both staged SCS construction arrays for {label} ({component} slot {slot})."
        });

        // If the removed component was a DECLARATIVE part graft, drop its entry so a rebuild
        // (load / re-base / next drop) won't re-add it. Match on the resolved component name
        // recorded at graft time (e.g. "Head_2"); fall back to the requested slot for legacy suits.
        List<string> atomicallyRemovedComponents = [];
        var removedGrafts = 0;
        if (removingPairedCapeMember)
        {
            var before = _currentProject.PartGrafts.Count;
            try
            {
                atomicallyRemovedComponents = RemovePairedCapeAdapterAtomically(_currentProject);
            }
            catch (InvalidOperationException ex)
            {
                _currentProject = previousProjectSnapshot;
                ApplyProjectToFields(_currentProject);
                UpdateSelectedLabels();
                AppendLog("Remove/hide stopped before staging: " + ex.Message);
                Dialog.Error(
                    this,
                    "Paired cape could not be removed safely",
                    ex.Message,
                    windowTitle: "Parts");
                _session.RaiseChanged();
                RefreshInspector();
                RefreshToyboxTiles();
                return;
            }
            removedGrafts = before - _currentProject.PartGrafts.Count;
            AppendLog("  removed the authored paired-cape adapter atomically; its cosmetic Cape and Torso glider cannot be replayed independently on a glide-only base.");
        }
        else
        {
            removedGrafts = _currentProject.PartGrafts.RemoveAll(pg =>
                GraftTargetsComponent(pg, component));
        }
        if (removedGrafts > 0 || removingPairedCapeMember)
        {
            if (removingPairedCapeMember)
            {
                // The inspector just created a remove-component rule for the clicked graft. Neither
                // adapter member remains after the atomic removal, so no shell-only removal may be
                // carried onto the restored gameplay Blueprint.
                foreach (var removedComponent in atomicallyRemovedComponents)
                {
                    RemoveSavedRemovalForComponent(_currentProject, removedComponent);
                }
            }
            // The part no longer exists declaratively, so the remove-component rule we just added
            // is redundant (nothing will graft that component to remove) - drop it too.
            _currentProject.Requirements.RemoveAll(r =>
                r.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
                r.TargetComponent.Equals(key, StringComparison.OrdinalIgnoreCase));
            AppendLog($"  cleared the declarative part graft for '{component}' — it won't be re-added on rebuild.");
        }

        try
        {
            // Rebuild from the clean patched base instead of editing the current stage in place.
            // The completion marker is written only after BOTH playable and cutscene replays pass,
            // so a locked role can never leave a packageable half-removal behind.
            await RebuildGraftStageFromDeclarativeAsync(persistProject: false);
        }
        catch (Exception ex)
        {
            _currentProject = previousProjectSnapshot;
            ApplyProjectToFields(_currentProject);
            UpdateSelectedLabels();
            AppendLog("Remove/hide failed; the saved project was left unchanged:");
            AppendLog(ex.ToString());
            _session.RaiseChanged();
            RefreshInspector();
            RefreshToyboxTiles();
            return;
        }

        var projectSaved = false;
        try
        {
            await RunWithFileLockRetryAsync(
                () => (_projectService ??= new SuitProjectService(_projectRootText.Text.Trim()))
                    .SaveProject(_currentProject),
                "save the completed part removal");
            projectSaved = true;
            await FinalizeDeclarativeGraftStageAsync(_currentProject, _projectRootText.Text.Trim());
        }
        catch (Exception ex)
        {
            if (!projectSaved)
            {
                _currentProject = previousProjectSnapshot;
                ApplyProjectToFields(_currentProject);
                UpdateSelectedLabels();
            }
            AppendLog(projectSaved
                ? "The part removal was saved, but its completed stage could not be certified:"
                : "The part was removed from both temporary staged roles, but the suit project could not be saved:");
            AppendLog(ex.ToString());
            Dialog.Error(
                this,
                projectSaved ? "Part saved; stage incomplete" : "Part removed but not saved",
                (projectSaved
                    ? "The project was saved, but Batcomputer could not certify its generated stage. Packaging remains blocked until the declarative stage rebuild succeeds."
                    : "The playable and cutscene temporary stages were updated, but the suit project file could not be saved. The prior saved project remains active and packaging stays blocked until the edit is rebuilt.") +
                "\n\n" + ex.Message);
            _session.RaiseChanged();
            RefreshInspector();
            RefreshToyboxTiles();
            return;
        }

        RecordChange("Parts", $"{component} slot {slot}", $"removed SCS part {label}");
        AppendLog($"Removed {label} from both staged character roles. Package the current stage to test it in-game.");
        _session.RaiseChanged();
        RefreshToyboxTiles();
    }

    private DeclarativeReplayOutcome ApplySavedComponentRemovals(
        NativeSuitProject project,
        bool logNoRemovals,
        string? stageContentRootOverride = null)
    {
        var outcome = new DeclarativeReplayOutcome();
        var removals = project.Requirements
            .Where(requirement => requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase))
            .Select(requirement => requirement.TargetComponent)
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (removals.Count == 0)
        {
            if (logNoRemovals)
            {
                AppendLog("No saved component removals to apply.");
            }
            return outcome;
        }

        var protectedGliderComponent = ActiveGliderVisualComponent(project);
        foreach (var removal in removals)
        {
            var component = removal;
            var colon = removal.LastIndexOf(':');
            if (colon > 0)
            {
                component = removal[..colon];
            }

            if (!string.IsNullOrWhiteSpace(protectedGliderComponent) &&
                protectedGliderComponent.Equals(component, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Skipped saved remove-component {removal}: '{component}' is the active native glider component.");
                continue;
            }

            var service = new ComponentRemoveService(_projectRootText.Text.Trim());
            var result = RunWithStructuredFileLockRetry(
                () => string.IsNullOrWhiteSpace(stageContentRootOverride)
                    ? service.Remove(
                        project.SlotId,
                        project.TargetPackages.Playable,
                        project.TargetPackages.Cutscene,
                        component)
                    : service.RemoveFromContentRoot(
                        stageContentRootOverride,
                        project.SlotId,
                        project.TargetPackages.Playable,
                        project.TargetPackages.Cutscene,
                        component),
                removalResult => removalResult.TransientFileLock ||
                                 removalResult.Files.Any(file => file.TransientFileLock),
                $"re-apply saved removal '{removal}'");

            var requiredRoles = new[]
            {
                (Role: "playable", Package: project.TargetPackages.Playable),
                (Role: "cutscene", Package: project.TargetPackages.Cutscene),
            };
            var satisfiedRoles = 0;
            foreach (var required in requiredRoles)
            {
                if (string.IsNullOrWhiteSpace(required.Package))
                {
                    outcome.Failures.Add($"{removal}/{required.Role}: target package path is empty");
                    continue;
                }

                var file = result.Files.FirstOrDefault(candidate =>
                    candidate.Role.Equals(required.Role, StringComparison.OrdinalIgnoreCase));
                if (file is not null && (file.Success || file.AlreadyRemoved))
                {
                    satisfiedRoles++;
                    continue;
                }

                var error = file?.Error ?? result.Error ?? "no result was returned";
                outcome.Failures.Add($"{removal}/{required.Role}: {error}");
                if (file?.TransientFileLock == true || result.TransientFileLock)
                {
                    outcome.TransientFileLock = true;
                }
            }

            var successes = result.Files.Count(file => file.Success);
            var alreadyRemoved = result.Files.Count(file => file.AlreadyRemoved);
            var changedRefs = result.Files.Sum(file => file.RemovedNodeReferences);
            if (satisfiedRoles == requiredRoles.Length)
            {
                AppendLog($"Re-applied saved remove-component {removal}: files={successes} alreadyRemoved={alreadyRemoved} removedRefs={changedRefs} status={result.Status}");
            }
            else
            {
                AppendLog($"  ERROR saved remove-component {removal} was incomplete ({satisfiedRoles}/{requiredRoles.Length} roles satisfied).");
            }
        }
        return outcome;
    }

    private void UpdateSlotDots()
    {
        // List mode still has per-row dots; figure mode carries the state on the minifig itself.
        foreach (Control c in _yourCharacter.SlotFlow.Controls)
        {
            if (c.Tag is ValueTuple<string, string, int> t && c.Controls["dot"] is StatusDot sd)
            {
                sd.DotColor = _customSlotKeys.Contains($"{t.Item2}:{t.Item3}") ? Theme.CustomDot : Theme.DefaultDot;
            }
        }
        UpdateMinifigDiagram();
    }

    /// <summary>
    /// Maps a component name onto a <see cref="MinifigDiagram"/> region (best-effort keyword match).
    /// Mirrors how the game splits a character: CharacterMesh0 is ONE mesh covering torso, arms,
    /// hands, legs and feet, so all of those classify as "Body".
    /// </summary>
    private static string ClassifyRegion(string component)
    {
        var c = component.ToLowerInvariant();
        if (c.Contains("face")) return "Face";
        if (c.Contains("head") || c.Contains("cowl") || c.Contains("hair") || c.Contains("hat") || c.Contains("helmet")) return "Head";
        if (c.Contains("cape") || c.Contains("cloak")) return "Cape";
        // Torso2 is the game's shoulders/pauldrons add-on slot (checked before the generic torso).
        if (c.Contains("torso2") || c.Contains("torso_2") || c.Contains("shoulder") || c.Contains("pauldron")) return "Shoulders";
        if (c.Contains("hip") || c.Contains("belt") || c.Contains("pelvis") || c.Contains("waist")) return "Belt";
        // Body = CharacterMesh0 and every limb/torso mesh, plus anything unclassified.
        return "Body";
    }

    /// <summary>Recomputes per-region present/customized state from the current slot rows and repaints the diagram.</summary>
    private void UpdateMinifigDiagram()
    {
        var states = new Dictionary<string, MinifigDiagram.RegionState>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _characterSlots)
        {
            var region = ClassifyRegion(t.Item2);
            var customized = _customSlotKeys.Contains($"{t.Item2}:{t.Item3}");
            var next = customized ? MinifigDiagram.RegionState.Customized : MinifigDiagram.RegionState.Present;
            // Customized wins over a plain "present" already recorded for the same region.
            if (!states.TryGetValue(region, out var cur) || next == MinifigDiagram.RegionState.Customized || cur == MinifigDiagram.RegionState.Absent)
            {
                states[region] = next == MinifigDiagram.RegionState.Customized ? next
                    : cur == MinifigDiagram.RegionState.Customized ? cur : MinifigDiagram.RegionState.Present;
            }
        }

        // Accessory slots aren't mesh components - their state comes from the project.
        var gliderSet = !string.IsNullOrWhiteSpace(_currentProject?.GliderMaterial)
                        || !string.IsNullOrWhiteSpace(_currentProject?.GliderType)
                           && !string.Equals(_currentProject!.GliderType, "base", StringComparison.OrdinalIgnoreCase);
        states["Glider"] = gliderSet ? MinifigDiagram.RegionState.Customized : MinifigDiagram.RegionState.Present;
        states["Equipment"] = _currentProject?.EquipmentSlots is { Count: > 0 }
            ? MinifigDiagram.RegionState.Customized
            : MinifigDiagram.RegionState.Present;

        // Badge counts: how many material slots each region carries.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _characterSlots)
        {
            var region = ClassifyRegion(t.Component);
            counts[region] = counts.TryGetValue(region, out var n) ? n + 1 : 1;
        }

        var selected = string.IsNullOrWhiteSpace(_toyboxComponent) ? null : ClassifyRegion(_toyboxComponent);
        _yourCharacter.Diagram.SetStates(states, counts, selected);
    }

    /// <summary>
    /// Handles a click on a minifig region. One matching slot selects it directly; several (e.g. the
    /// torso covers Body + Cape LODs + gliding meshes) pop a chooser so every slot stays reachable
    /// now that the per-slot rows are gone.
    /// </summary>
    private void SelectFirstSlotInRegion(string region)
    {
        // Accessory slots jump to their own category instead of selecting a mesh slot.
        if (region.Equals("Glider", StringComparison.OrdinalIgnoreCase))
        {
            SelectComboValue(_toyboxCategoryCombo, "Gliders");
            return;
        }
        if (region.Equals("Equipment", StringComparison.OrdinalIgnoreCase))
        {
            SelectComboValue(_toyboxCategoryCombo, "Equipment");
            return;
        }

        // Group by COMPONENT, not slot: the materials panel already lists every slot on the chosen
        // component, so a per-slot chooser was pointless (picking Cape LOD0 vs LOD1 showed the same
        // panel). Only a region spanning genuinely different components needs a chooser.
        var components = RegionComponents(region);
        if (components.Count == 0)
        {
            AppendLog($"No character slot maps to '{region}' on this base.");
            return;
        }

        if (components.Count == 1)
        {
            var only = components[0].OrderBy(t => t.Slot).First();
            SelectToyboxSlot(only.Label, only.Component, only.Slot);
            return;
        }

        var menu = new ContextMenuStrip();
        foreach (var group in components)
        {
            var first = group.OrderBy(t => t.Slot).First();
            var count = group.Count();
            var marked = group.Any(t => _customSlotKeys.Contains($"{t.Component}:{t.Slot}")) ? "● " : "";
            menu.Items.Add($"{marked}{first.Label}  ({group.Key} · {count} slot{(count == 1 ? "" : "s")})", null,
                (_, _) => SelectToyboxSlot(first.Label, first.Component, first.Slot));
        }
        menu.Show(_yourCharacter.Diagram, _yourCharacter.Diagram.PointToClient(Cursor.Position));
    }

    /// <summary>Builds a virtualized tile for a part - same title/subtitle/drag/menu/tooltip as the
    /// old Button tile (MakePartTile), just as data instead of a control.</summary>
    private VirtualTilePanel.Tile PartTile(NativeSuitPartRecord part)
    {
        // Recipe confidence is shown up-front: a bad recipe otherwise only surfaces as an in-game
        // crash after a package. Glyph + accent + tooltip reason (never colour alone).
        var (level, reason) = PartRecipeService.Confidence(part);
        // Native (the common case) shows no glyph - it was just noise on nearly every tile. The
        // inferred/risky glyphs stay: they flag parts that can crash in-game, so colour alone won't do.
        var (glyph, accent) = level switch
        {
            PartRecipeService.RecipeConfidence.Native => ("", Theme.Parts),
            PartRecipeService.RecipeConfidence.Inferred => ("~", Color.FromArgb(220, 160, 40)),
            _ => ("⚠", Color.FromArgb(232, 96, 96)),
        };

        return new VirtualTilePanel.Tile
        {
            Title = string.IsNullOrEmpty(glyph)
                ? TrimMiddle(CleanPartMeshDisplayName(part), 28)
                : $"{glyph} {TrimMiddle(CleanPartMeshDisplayName(part), 28)}",
            Subtitle = $"{part.Slot} • {part.Context}\nfrom {TrimMiddle(PartSourceDisplayName(part), 24)}",
            Accent = accent,
            DragPayload = new ToyboxDragPayload { Kind = "part", Part = part },
            ToolTip =
                $"Recipe: {level} — {reason}\n\n" +
                $"{part.Slot} from {part.SourcePackagePath}\nMesh: {part.MeshObjectPath} ({part.MeshKind})\n" +
                $"Component: {part.ComponentClass}\nAttach: {(string.IsNullOrWhiteSpace(part.AttachSocket) ? "(none)" : part.AttachSocket)}" +
                $"{(string.IsNullOrWhiteSpace(part.ParentComponentOrVariableName) ? "" : $" on {part.ParentComponentOrVariableName}")}\n" +
                $"Display: {CleanPartMeshDisplayName(part)}\nMaterials: {string.Join(", ", part.Materials.Select(m => m.ObjectPath).Take(4))}",
            MenuFactory = () => BuildPartTileMenu(part),
        };
    }

    private List<VirtualTilePanel.Tile> CustomStaticMeshTiles(string search)
    {
        var tiles = new List<VirtualTilePanel.Tile>
        {
            new()
            {
                Section = "",
                Title = "+ Import custom mesh",
                Subtitle = "OBJ static attachment",
                Accent = Theme.Parts,
                Dashed = true,
                OnClick = () => _ = OpenCustomStaticMeshDialogAsync(null),
                ToolTip = "Imports a project-owned OBJ as a static attachment. Pick a real game socket, then set scale and local XYZ offset."
            }
        };

        if (_currentProject?.CustomStaticMeshes is not { Count: > 0 })
        {
            var legacy = _currentProject is null
                ? null
                : new CustomStaticMeshImportService().FindLegacyObjProof(_currentProject, _projectRootText.Text.Trim());
            if (legacy is not null && MatchesToyboxSearch(search, legacy.DisplayName, legacy.SourceObjPath, "legacy OBJ import"))
            {
                tiles.Add(new VirtualTilePanel.Tile
                {
                    Section = "LEGACY MESHES",
                    Title = $"Adopt {TrimMiddle(legacy.DisplayName, 20)}",
                    Subtitle = "existing OBJ import\nmake it editable",
                    Accent = Theme.Parts,
                    Dashed = true,
                    OnClick = () => _ = AdoptLegacyStaticMeshAsync(legacy),
                    ToolTip = "Copies this older OBJ import into the suit. After adoption, click its tile or use the 3D viewer to save its real scale and local XYZ placement."
                });
            }
            return tiles;
        }

        foreach (var mesh in _currentProject.CustomStaticMeshes)
        {
            var current = mesh;
            var attachment = CustomStaticMeshImportService.ResolveAttachmentSlot(current.Target, current.AttachSocket);
            if (!MatchesToyboxSearch(search, current.DisplayName, current.SourceObjRelativePath, attachment.Label, attachment.AttachSocket))
            {
                continue;
            }
            tiles.Add(new VirtualTilePanel.Tile
            {
                Section = "CUSTOM MESHES",
                Title = TrimMiddle(current.DisplayName, 28),
                Subtitle = $"{attachment.Label} · scale {current.Scale:0.###}\noffset {current.OffsetX:0.##}, {current.OffsetY:0.##}, {current.OffsetZ:0.##}",
                Accent = Theme.Parts,
                OnClick = () => _ = OpenCustomStaticMeshDialogAsync(current),
                ToolTip = $"Project OBJ: {current.SourceObjRelativePath}\nSocket: {attachment.AttachSocket}\n\nClick to edit. Right-click to edit or remove this mesh from the suit.",
                MenuFactory = () => BuildCustomStaticMeshTileMenu(current),
            });
        }
        return tiles;
    }

    private ContextMenuStrip BuildCustomStaticMeshTileMenu(CustomStaticMeshImport mesh)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit custom mesh", null, (_, _) => _ = OpenCustomStaticMeshDialogAsync(mesh));
        menu.Items.Add("Remove from suit", null, (_, _) => _ = RemoveCustomStaticMeshAsync(mesh));
        return menu;
    }

    private void ShowPartInspector(NativeSuitPartRecord part)
    {
        var inspector = new PartInspectorForm(part);
        inspector.ApplyRequested += async (_, _) => await ApplyToyboxPartDropToCharacterAsync(part);
        inspector.Show(this);
    }

    private async Task RemoveCustomStaticMeshAsync(CustomStaticMeshImport mesh)
    {
        var project = _currentProject;
        if (project is null || !project.CustomStaticMeshes.Contains(mesh))
        {
            return;
        }
        if (!Dialog.Confirm(
                this,
                "Remove custom mesh",
                $"Remove '{mesh.DisplayName}' from this suit?\n\nIts project-owned OBJ copy will also be deleted."))
        {
            return;
        }

        var projectRoot = _projectRootText.Text.Trim();
        var outputDirectory = new SuitProjectService(projectRoot).ProjectOutputDirectory(project);
        if (!string.IsNullOrWhiteSpace(mesh.SourceObjRelativePath))
        {
            try
            {
                var outputFull = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var sourceFull = Path.GetFullPath(Path.Combine(outputDirectory, mesh.SourceObjRelativePath));
                if (sourceFull.StartsWith(outputFull, StringComparison.OrdinalIgnoreCase) && File.Exists(sourceFull))
                {
                    File.Delete(sourceFull);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Custom mesh: could not delete the project OBJ copy for '{mesh.DisplayName}': {ex.Message}");
            }
        }

        project.CustomStaticMeshes.Remove(mesh);
        var componentName = CustomStaticMeshImportService.ComponentNameFor(mesh);
        if (!string.IsNullOrWhiteSpace(componentName))
        {
            project.MaterialAssignments.RemoveAll(assignment =>
                assignment.Component.Equals(componentName, StringComparison.OrdinalIgnoreCase));
            project.PreviewPartPlacements.RemoveAll(placement =>
                placement.Component.Equals(componentName, StringComparison.OrdinalIgnoreCase));
        }
        SyncCustomStaticMeshHeadRemoval(project);
        (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(project);
        await RebuildGraftStageFromDeclarativeAsync();
        RecordChange("Parts", mesh.DisplayName, "removed custom mesh", status: "removed");
        AppendLog($"Custom mesh: removed '{mesh.DisplayName}' from the suit.");
        _session.RaiseChanged();
        RefreshToyboxTiles();
        RefreshInspector();
    }

    private async Task AdoptLegacyStaticMeshAsync(CustomStaticMeshImportService.LegacyObjProof legacy)
    {
        EnsureProject();
        var project = _currentProject;
        if (project?.PlayableTemplate is null || project.CutsceneTemplate is null)
        {
            Dialog.Warn(this, "Custom mesh", "Set a visual base first, then adopt the old OBJ import.");
            return;
        }

        var attachment = CustomStaticMeshImportService.ResolveAttachmentSlot("Head");
        var mesh = new CustomStaticMeshImport
        {
            Id = "imported" + Guid.NewGuid().ToString("N")[..12],
            DisplayName = legacy.DisplayName,
            Target = attachment.Id,
            AttachSocket = attachment.AttachSocket,
            Scale = legacy.Scale,
            OffsetX = legacy.OffsetX,
            OffsetY = legacy.OffsetY,
            OffsetZ = legacy.OffsetZ,
            HideBaseHead = true,
        };
        var projectRoot = _projectRootText.Text.Trim();
        var service = new CustomStaticMeshImportService();
        try
        {
            service.CopySourceIntoProject(projectRoot, project, mesh, legacy.SourceObjPath);
            project.CustomStaticMeshes.Add(mesh);
            SyncCustomStaticMeshHeadRemoval(project);
            (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(project);
            await RebuildGraftStageFromDeclarativeAsync();
        }
        catch (Exception ex)
        {
            project.CustomStaticMeshes.Remove(mesh);
            Dialog.Error(this, "Custom mesh", "Could not adopt the old OBJ import. " + ex.Message);
            return;
        }

        RecordChange("Parts", mesh.DisplayName, "adopted legacy OBJ import", status: "staged");
        AppendLog($"Custom mesh: adopted legacy OBJ import '{mesh.DisplayName}'. It can now be edited in Parts or the 3D viewer.");
        _session.RaiseChanged();
        RefreshToyboxTiles();
    }

    private async Task OpenCustomStaticMeshDialogAsync(CustomStaticMeshImport? existing)
    {
        EnsureProject();
        var project = _currentProject;
        if (project?.PlayableTemplate is null || project.CutsceneTemplate is null)
        {
            Dialog.Warn(this, "Custom mesh", "Set a visual base first. The OBJ needs the suit's playable and cutscene Blueprints before it can be attached.");
            return;
        }

        var projectRoot = _projectRootText.Text.Trim();
        var service = new CustomStaticMeshImportService();
        var sourcePath = existing is null || string.IsNullOrWhiteSpace(existing.SourceObjRelativePath)
            ? ""
            : Path.Combine(new SuitProjectService(projectRoot).ProjectOutputDirectory(project), existing.SourceObjRelativePath);
        using var dialog = new CustomStaticMeshImportDialog(existing, sourcePath);
        var dialogResult = dialog.ShowDialog(this);
        if (dialog.DeleteRequested && existing is not null)
        {
            await RemoveCustomStaticMeshAsync(existing);
            return;
        }
        if (dialogResult != DialogResult.OK)
        {
            return;
        }

        if (dialog.AttachmentSlot.Id.Equals("Cape", StringComparison.OrdinalIgnoreCase))
        {
            var capeGlideContract = new AnimArchetypeGraftService().BaseCapeGlideContract(project);
            if (GliderService.HasAdditiveCapeAndGliderCombination(
                    project,
                    capeGlideContract,
                    addingCustomCape: true))
            {
                Dialog.Error(this,
                    "Custom Cape and glider are not compatible",
                    "A custom static mesh attached to Cape is an additive component and is not driven by the playable base's native cape/glider visibility wiring, even on a native two-cape base.\n\n" +
                    "Remove the glider before attaching this mesh to Cape.",
                    windowTitle: "Parts");
                return;
            }
        }

        var mesh = existing ?? new CustomStaticMeshImport();
        mesh.DisplayName = dialog.DisplayName;
        mesh.Scale = dialog.ImportScale;
        mesh.OffsetX = dialog.OffsetX;
        mesh.OffsetY = dialog.OffsetY;
        mesh.OffsetZ = dialog.OffsetZ;
        mesh.RotationPitch = dialog.RotationPitch;
        mesh.RotationYaw = dialog.RotationYaw;
        mesh.RotationRoll = dialog.RotationRoll;
        mesh.HideBaseHead = dialog.HideBaseHead;
        mesh.Target = dialog.AttachmentSlot.Id;
        mesh.AttachSocket = dialog.AttachmentSlot.AttachSocket;
        try
        {
            service.CopySourceIntoProject(projectRoot, project, mesh, dialog.SourceObjPath);
        }
        catch (Exception ex)
        {
            Dialog.Error(this, "Custom mesh", ex.Message);
            return;
        }

        if (existing is null)
        {
            project.CustomStaticMeshes.Add(mesh);
        }
        SyncCustomStaticMeshHeadRemoval(project);
        try
        {
            (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(project);
        }
        catch (Exception ex)
        {
            Dialog.Error(this, "Custom mesh", "The mesh source was copied, but the suit project could not be saved. " + ex.Message);
            return;
        }

        RecordChange("Parts", mesh.DisplayName, $"custom OBJ {dialog.AttachmentSlot.Label} · scale {mesh.Scale:0.###} · offset {mesh.OffsetX:0.##}, {mesh.OffsetY:0.##}, {mesh.OffsetZ:0.##} · rotation {mesh.RotationPitch:0.#}, {mesh.RotationYaw:0.#}, {mesh.RotationRoll:0.#}", status: "staged");
        AppendLog($"Custom mesh: saved {Path.GetFileName(mesh.SourceObjRelativePath)} for {dialog.AttachmentSlot.Label} ({dialog.AttachmentSlot.AttachSocket}) with scale {mesh.Scale:0.###}, offset ({mesh.OffsetX:0.###}, {mesh.OffsetY:0.###}, {mesh.OffsetZ:0.###}), rotation ({mesh.RotationPitch:0.##}, {mesh.RotationYaw:0.##}, {mesh.RotationRoll:0.##}).");
        try
        {
            await RebuildGraftStageFromDeclarativeAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Custom mesh: staging failed for '{mesh.DisplayName}': {ex.Message}");
            Dialog.Error(this, "Custom mesh", "The OBJ was saved with this suit, but could not be staged for preview or build.\n\n" + ex.Message);
            return;
        }
        _session.RaiseChanged();
        RefreshToyboxTiles();
    }

    private async Task StageCustomStaticMeshesAsync(NativeSuitProject project)
    {
        if (project.CustomStaticMeshes is not { Count: > 0 })
        {
            return;
        }

        var projectRoot = _projectRootText.Text.Trim();
        var service = new CustomStaticMeshImportService();
        var errors = new List<string>();
        foreach (var mesh in project.CustomStaticMeshes)
        {
            var result = await RunWithFileLockRetryAsync(
                () =>
                {
                    var staged = service.Stage(project, projectRoot, mesh);
                    if (staged.TransientFileLock)
                    {
                        throw new TransientFileLockException(
                            staged.Error ?? "A generated custom-mesh file is temporarily locked.");
                    }
                    return staged;
                },
                $"stage custom mesh '{mesh.DisplayName}'");
            if (!result.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"  custom mesh '{mesh.DisplayName}' was not staged: {result.Error}");
                errors.Add($"{mesh.DisplayName}: {result.Error}");
                continue;
            }
            foreach (var line in result.Log)
            {
                AppendLog($"  custom mesh: {line}");
            }
        }
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Custom mesh staging failed. " + string.Join(" | ", errors));
        }
    }

    private static void SyncCustomStaticMeshHeadRemoval(NativeSuitProject project)
    {
        const string requirementId = "custom-static-mesh-hide-head";
        project.Requirements.RemoveAll(requirement => requirement.Id.Equals(requirementId, StringComparison.OrdinalIgnoreCase));
        if (project.CustomStaticMeshes.Any(mesh =>
                mesh.HideBaseHead &&
                CustomStaticMeshImportService.ResolveAttachmentSlot(mesh.Target, mesh.AttachSocket).CanHideBaseHead))
        {
            project.Requirements.Add(new NativeSuitRequirement
            {
                Id = requirementId,
                Kind = "remove-component",
                SourcePackage = project.TargetPackages.Playable,
                TargetComponent = ToyboxSlotKey("Head", 0),
                Notes = "Hidden because a custom static head attachment is active."
            });
        }
    }

    private enum FaceMaterialCompatibility
    {
        Compatible,
        Unknown,
        Incompatible,
    }

    private async Task<T> RunWithFileLockRetryAsync<T>(Func<T> action, string operation)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Task.Run(action);
            }
            catch (Exception ex) when (attempt < attempts && FileLockUtil.IsTransient(ex))
            {
                AppendLog($"  {operation}: a generated file is temporarily locked; retrying ({attempt}/{attempts - 1})…");
                await Task.Delay(180 * attempt);
            }
        }
    }

    private T RunWithStructuredFileLockRetry<T>(
        Func<T> action,
        Func<T, bool> hasTransientFileLock,
        string operation)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            T result;
            try
            {
                result = action();
            }
            catch (Exception ex) when (attempt < attempts && FileLockUtil.IsTransient(ex))
            {
                AppendLog($"  {operation}: a generated file is temporarily locked; retrying ({attempt}/{attempts - 1})…");
                Thread.Sleep(180 * attempt);
                continue;
            }

            if (!hasTransientFileLock(result) || attempt >= attempts)
            {
                return result;
            }

            AppendLog($"  {operation}: a generated file is temporarily locked; retrying ({attempt}/{attempts - 1})…");
            Thread.Sleep(180 * attempt);
        }
    }

    private sealed class DeclarativeReplayOutcome
    {
        public List<string> Failures { get; } = [];
        public bool TransientFileLock { get; set; }
        public bool Success => Failures.Count == 0;

        public string Summary => Failures.Count == 0
            ? "complete"
            : string.Join(" | ", Failures);
    }

    private static void RequireCompleteDeclarativeReplay(
        DeclarativeReplayOutcome outcome,
        string operation)
    {
        if (outcome.Success)
        {
            return;
        }

        var message = $"{operation} did not succeed for every required character package. {outcome.Summary}";
        if (outcome.TransientFileLock)
        {
            throw new TransientFileLockException(message);
        }

        throw new InvalidOperationException(message);
    }

    private sealed record FaceTarget(string Label, string Component, int Slot, string MeshPackagePath);

    /// <summary>
    /// Face materials are applied to the existing Face skeletal component. Most minifigs use
    /// SK_LEGOface, but the game also ships Superhero, Joker89, FaceTex and a few one-off meshes.
    /// The material browser therefore compares the donor material's observed mesh family with the
    /// current Face component before enabling a one-click/drag application.
    /// </summary>
    private void RefreshFaceTiles(string? type)
    {
        var gd = GameDataService.Instance;
        if (!gd.HasCatalog)
        {
            ShowVirtualTiles(Array.Empty<VirtualTilePanel.Tile>(), emptyMessage: "Asset catalog not loaded (ship gamedata/*.json).");
            return;
        }

        var folder = (type is null || type == "<all faces>") ? null : type;
        var faceSource = FilterVal(0);
        var search = CurrentToyboxSearch();
        var gameFaces = AttachmentCatalogService.FaceMaterials(folder)
            .Where(a => faceSource is null || a.Path.Contains($"/{faceSource}/", StringComparison.OrdinalIgnoreCase))
            .Where(a => MatchesToyboxSearch(search, a.Path, AttachmentCatalogService.AssetName(a.Path)))
            .ToList();

        var mod = ExtractModFolder(_targetPlayableText.Text.Trim());
        var userFaces = DiscoverUserMaterialPaths(mod)
            .Where(IsFaceMaterialPackage)
            .Where(path => MatchesToyboxSearch(search, path, UnrealPathUtil.AssetName(path)))
            .ToList();

        var tiles = new List<VirtualTilePanel.Tile>();
        tiles.AddRange(userFaces.Select(path => BuildFaceMaterialTile(path, isUserMade: true, "Your face materials")));
        tiles.AddRange(gameFaces.Select(asset => BuildFaceMaterialTile(asset.Path, isUserMade: false, "Base-game faces")));

        ShowVirtualTiles(
            tiles,
            header: $"Faces{(folder is null ? "" : $" · {folder}")} — swaps the printed-expression material on the existing Face component. Matching mesh families apply directly; special face rigs are blocked. Click or drag a compatible face onto the Face row, or right-click to create a custom variant.",
            emptyMessage: "No face materials matched. Try <all faces> or clear the search.");
    }

    private VirtualTilePanel.Tile BuildFaceMaterialTile(
        string materialPath,
        bool isUserMade,
        string section,
        bool allowDelete = true,
        IReadOnlyList<string>? compatibleFaceMeshes = null)
    {
        var compatibility = FaceCompatibilityFor(
            materialPath,
            CurrentFaceTarget(),
            out var targetMesh,
            out var sourceMeshes,
            compatibleFaceMeshes);
        var sourceLabel = sourceMeshes.Count == 0
            ? "unrecorded face family"
            : string.Join(", ", sourceMeshes.Select(UnrealPathUtil.AssetName));
        var subtitle = compatibility switch
        {
            FaceMaterialCompatibility.Compatible => "compatible · click or drag to Face",
            FaceMaterialCompatibility.Incompatible => $"blocked · requires {sourceLabel}",
            _ => "face material · compatibility unrecorded",
        };
        var tooltip = $"{materialPath}\nTarget face mesh: {(string.IsNullOrWhiteSpace(targetMesh) ? "unknown" : targetMesh)}\nObserved donor mesh: {sourceLabel}";

        return new VirtualTilePanel.Tile
        {
            Title = UnrealPathUtil.AssetName(materialPath).Replace("MI_FACE_", "", StringComparison.OrdinalIgnoreCase),
            Subtitle = subtitle,
            Accent = compatibility == FaceMaterialCompatibility.Incompatible ? Theme.Warn : Theme.Faces,
            Section = section,
            ToolTip = tooltip,
            DragPayload = compatibility == FaceMaterialCompatibility.Incompatible
                ? null
                : new ToyboxDragPayload { Kind = "material", MaterialPath = materialPath, FaceOnly = true },
            OnClick = () => ApplyFaceMaterial(materialPath),
            MenuFactory = () => BuildFaceMaterialTileMenu(materialPath, isUserMade, allowDelete),
        };
    }

    private ContextMenuStrip BuildFaceMaterialTileMenu(
        string materialPath,
        bool isUserMade,
        bool allowDelete = true)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Apply to Face", null, (_, _) => ApplyFaceMaterial(materialPath));
        menu.Items.Add(new ToolStripSeparator());
        if (isUserMade)
        {
            menu.Items.Add("Edit this face material…", null, (_, _) => OpenMaterialFromBase(materialPath, editInPlace: true));
            if (allowDelete)
            {
                menu.Items.Add("Delete this face material…", null, async (_, _) => await DeleteGeneratedMaterialAsync(materialPath));
            }
        }
        else
        {
            menu.Items.Add("Use as base for a custom face…", null, (_, _) => OpenMaterialFromBase(materialPath, editInPlace: false));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy material path", null, (_, _) =>
        {
            try { Clipboard.SetText(materialPath); }
            catch { /* clipboard may be busy */ }
        });
        return menu;
    }

    private void ApplyFaceMaterial(string materialPath)
    {
        var target = CurrentFaceTarget();
        if (target is null)
        {
            AppendLog($"Face material not applied: this character has no editable Face component ({materialPath}).");
            Dialog.Warn(this, "No Face component", "This character base does not expose an editable Face component. A printed face material cannot be applied safely.");
            return;
        }

        if (!CanApplyFaceMaterial(materialPath, target.Component, target.Slot, confirmUnknown: true))
        {
            return;
        }

        SelectToyboxSlot(target.Label, target.Component, target.Slot);
        ApplyToyboxMaterial(materialPath);
    }

    private bool CanApplyFaceMaterial(string materialPath, string component, int slot, bool confirmUnknown)
    {
        if (!component.Contains("face", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var target = FaceTargetFor(component, slot);
        if (target is null)
        {
            AppendLog($"Face material blocked: {component} slot {slot} is not the character's editable skeletal face overlay.");
            Dialog.Warn(this, "Not a face overlay",
                "That component is named like a face part, but it is not the character's editable LEGO face overlay. Batcomputer will not replace a bandana or other Face1 accessory with a printed-face material.");
            return false;
        }

        var compatibility = FaceCompatibilityFor(materialPath, target, out var targetMesh, out var sourceMeshes);
        if (compatibility == FaceMaterialCompatibility.Incompatible)
        {
            var required = string.Join(", ", sourceMeshes.Select(UnrealPathUtil.AssetName));
            var current = string.IsNullOrWhiteSpace(targetMesh) ? "an unknown face mesh" : UnrealPathUtil.AssetName(targetMesh);
            AppendLog($"Face material blocked: {materialPath} was observed on [{required}], current Face uses {current}.");
            Dialog.Warn(this, "Different face rig",
                $"This material was observed on {required}, but the current character uses {current}. Batcomputer will not force a material across different face mesh families because UVs and expression layers may not line up.");
            return false;
        }

        if (compatibility == FaceMaterialCompatibility.Unknown && confirmUnknown)
        {
            return Dialog.Confirm(this, "Unverified face material",
                "Batcomputer recognizes this as a face material, but an older project did not record which face mesh family it came from. Apply it to the current Face component anyway? Verify it in the 3D preview and in-game.",
                confirmText: "Apply face", severity: Dialog.Level.Warn);
        }

        return true;
    }

    private FaceMaterialCompatibility FaceCompatibilityFor(
        string materialPath,
        FaceTarget? target,
        out string targetMesh,
        out IReadOnlyList<string> sourceMeshes,
        IReadOnlyList<string>? knownSourceMeshes = null)
    {
        targetMesh = UnrealPathUtil.NormalizePackagePath(target?.MeshPackagePath);
        sourceMeshes = knownSourceMeshes is null
            ? CompatibleFaceMeshesForMaterial(materialPath)
            : knownSourceMeshes
                .Select(UnrealPathUtil.NormalizePackagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (string.IsNullOrWhiteSpace(targetMesh) || sourceMeshes.Count == 0)
        {
            return FaceMaterialCompatibility.Unknown;
        }

        var normalizedTargetMesh = targetMesh;
        return sourceMeshes.Any(source => UnrealPathUtil.NormalizePackagePath(source)
                .Equals(normalizedTargetMesh, StringComparison.OrdinalIgnoreCase))
            ? FaceMaterialCompatibility.Compatible
            : FaceMaterialCompatibility.Incompatible;
    }

    private FaceTarget? CurrentFaceTarget()
    {
        return _characterSlots
            .Select(slot => FaceTargetFor(slot.Component, slot.Slot, slot.Label))
            .Where(target => target is not null)
            .OrderByDescending(target => IsKnownFaceOverlayMesh(target!.MeshPackagePath))
            .ThenByDescending(target => SplitGeneratedDuplicateComponent(target!.Component).BaseComponent
                .Equals("Face", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private FaceTarget? FaceTargetFor(string component, int slot, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(component) ||
            !component.Contains("face", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mesh = _slotDetails.TryGetValue($"{component}:{slot}", out var detail)
            ? UnrealPathUtil.NormalizePackagePath(detail.Mesh)
            : "";
        var baseComponent = SplitGeneratedDuplicateComponent(component).BaseComponent;
        if (!IsKnownFaceOverlayMesh(mesh) &&
            !baseComponent.Equals("Face", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new FaceTarget(
            string.IsNullOrWhiteSpace(label) ? FriendlySlotLabel(component, slot) : label,
            component,
            slot,
            mesh);
    }

    private static bool IsKnownFaceOverlayMesh(string? meshPackagePath)
    {
        var mesh = UnrealPathUtil.NormalizePackagePath(meshPackagePath);
        return mesh.Contains("/Attachments/LEGOface/", StringComparison.OrdinalIgnoreCase) ||
               mesh.Contains("/Attachments/FaceTex/", StringComparison.OrdinalIgnoreCase) ||
               UnrealPathUtil.AssetName(mesh).StartsWith("SK_LEGOface", StringComparison.OrdinalIgnoreCase) ||
               UnrealPathUtil.AssetName(mesh).StartsWith("SK_FaceTex", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> CompatibleFaceMeshesForMaterial(string materialPath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(materialPath);
        var authored = _currentProject?.GeneratedMaterials?.FirstOrDefault(material =>
            UnrealPathUtil.NormalizePackagePath(material.PackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase));
        if (authored?.CompatibleFaceMeshPackagePaths is { Count: > 0 })
        {
            return authored.CompatibleFaceMeshPackagePaths
                .Select(UnrealPathUtil.NormalizePackagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var shared = new ToolMaterialLibraryService(_projectRootText.Text.Trim())
            .LoadAvailable()
            .FirstOrDefault(material => UnrealPathUtil.NormalizePackagePath(material.PackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase));
        if (shared?.CompatibleFaceMeshPackagePaths is { Count: > 0 })
        {
            return shared.CompatibleFaceMeshPackagePaths
                .Select(UnrealPathUtil.NormalizePackagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return FaceMeshesForMaterial(package);
    }

    private IReadOnlyList<string> FaceMeshesForMaterial(string materialPath)
    {
        if (_partIndex is null)
        {
            _partIndex = new PartIndexService(_projectRootText.Text.Trim()).LoadPartIndex();
        }

        var package = UnrealPathUtil.NormalizePackagePath(materialPath);
        if (_partIndex is null || string.IsNullOrWhiteSpace(package))
        {
            return Array.Empty<string>();
        }

        return _partIndex.Parts
            .Where(part => part.Slot.Contains("face", StringComparison.OrdinalIgnoreCase) ||
                           part.SemanticKind.Equals("Face", StringComparison.OrdinalIgnoreCase))
            .Where(part => part.Materials.Any(material =>
                UnrealPathUtil.NormalizePackagePath(material.PackagePath).Equals(package, StringComparison.OrdinalIgnoreCase) ||
                UnrealPathUtil.NormalizePackagePath(material.ObjectPath).Equals(package, StringComparison.OrdinalIgnoreCase)))
            .Select(part => UnrealPathUtil.NormalizePackagePath(part.MeshPackagePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsFaceMaterialPackage(string materialPath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(materialPath);
        var authored = _currentProject?.GeneratedMaterials?.FirstOrDefault(material =>
            UnrealPathUtil.NormalizePackagePath(material.PackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase));
        if (authored?.Kind.Equals("Face", StringComparison.OrdinalIgnoreCase) == true ||
            package.Contains("/Attachments/Face/", StringComparison.OrdinalIgnoreCase) ||
            UnrealPathUtil.AssetName(package).StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase) ||
            FaceMeshesForMaterial(package).Count > 0)
        {
            return true;
        }

        var diskPath = ResolveMaterialDiskPath(package, preferExport: true);
        if (diskPath is null)
        {
            return false;
        }

        try
        {
            var info = new MaterialGenService(_projectRootText.Text.Trim()).ReadTemplate(diskPath);
            return info.Status.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
                   (info.ParentMaterialPath.Contains("LEGOface", StringComparison.OrdinalIgnoreCase) ||
                    info.TextureParams.Any(parameter => IsFaceParameterName(parameter.Name)) ||
                    info.ScalarParams.Any(parameter => IsFaceParameterName(parameter.Name)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFaceParameterName(string? parameter)
    {
        var compact = new string((parameter ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return compact.Contains("brow", StringComparison.Ordinal) ||
               compact.Contains("eyelid", StringComparison.Ordinal) ||
               compact.Contains("lash", StringComparison.Ordinal) ||
               compact.Contains("mouth", StringComparison.Ordinal) ||
               compact.Contains("teeth", StringComparison.Ordinal) ||
               compact.Contains("tongue", StringComparison.Ordinal);
    }

    /// <summary>Shows glider visuals and saves the selected package-time graft.</summary>
    private void RefreshGliderTiles(string? type)
    {
        if (!string.Equals(type, "Wingsuit decals", StringComparison.OrdinalIgnoreCase))
        {
            if (_partIndex is null)
            {
                LoadPartIndexAndRefreshGrid(logIfMissing: false);
            }

            if (_partIndex is null || _partIndex.Parts.Count == 0)
            {
                _toyboxTileFlow.Controls.Add(MakeTile("Build part index", "scan extracted BPs", () => { _ = BuildPartIndexAsync(); }, Theme.Gliders, dashed: true));
                _toyboxTileFlow.Controls.Add(MakeNoteTile("Build the native part index first. Gliders need real donor component records so the tool can preserve mesh, anim BP, all materials, attach socket, and component tags."));
                return;
            }

            var gliderSource = FilterVal(0);
            var gliderKind = FilterVal(1);
            var parts = GliderService.NativeGliderParts(_partIndex, CurrentToyboxSearch())
                .Where(part => gliderSource is null || part.CharacterFolder.Equals(gliderSource, StringComparison.OrdinalIgnoreCase))
                .Where(part => gliderKind is null || GliderService.KindLabel(part).Equals(gliderKind, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var tiles = parts.Select(part => new VirtualTilePanel.Tile
            {
                Title = TrimMiddle(GliderService.GliderPresetLabel(part), 30),
                Subtitle = GliderService.GliderPresetSubtitle(part),
                Accent = Theme.Gliders,
                OnClick = () => ShowGliderPresetDetail(part),
                DragPayload = new ToyboxDragPayload { Kind = "part", Part = part },
                MenuFactory = () => BuildPartTileMenu(part),
                ToolTip = $"{GliderService.RoleLabel(part)} from {part.SourcePackagePath}\nMesh: {part.MeshObjectPath}\nAnim: {part.AnimClassObjectPath}\nNative materials: {string.Join(", ", part.Materials.Select(m => m.ObjectPath).Take(6))}",
            }).ToList();
            if (_currentProject?.PartGrafts.Any(graft => graft.IsGlider) == true)
            {
                tiles.Insert(0, new VirtualTilePanel.Tile
                {
                    Title = "Use base glider",
                    Subtitle = "remove the custom glide visual and pose",
                    Accent = Theme.Gold,
                    Dashed = true,
                    OnClick = () => { _ = ClearCustomGliderAsync(); },
                    ToolTip = "Rebuilds the suit without its custom glider and restores the gameplay donor's original glide visual."
                });
            }
            ShowVirtualTiles(
                tiles,
                header: "Native glide visuals only: glide capes, wingsuits, and character gliders. Batman glide capes are selectable here under Filter → Source → Batman; cosmetic back capes stay in Parts. Click for mount, animation, materials, and pose details.",
                emptyMessage: "No native glide visuals matched the current search. Rebuild the part index if you recently extracted more characters.");
            return;
        }

        var gd = GameDataService.Instance;
        if (!gd.HasCatalog)
        {
            ShowVirtualTiles(Array.Empty<VirtualTilePanel.Tile>(), emptyMessage: "Asset catalog not loaded (ship gamedata/*.json).");
            return;
        }

        if (type == "Wingsuit decals")
        {
            var search = CurrentToyboxSearch();
            var decals = gd.AssetsOfClass("MaterialInstanceConstant")
                .Where(a => AttachmentCatalogService.AssetName(a.Path).StartsWith("MI_DECAL_Wingsuit_", StringComparison.OrdinalIgnoreCase))
                .Where(a => MatchesToyboxSearch(search, a.Path, AttachmentCatalogService.AssetName(a.Path)))
                .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ShowVirtualTiles(
                decals.Select(a => new VirtualTilePanel.Tile
                {
                    Title = AttachmentCatalogService.AssetName(a.Path).Replace("MI_DECAL_Wingsuit_", ""),
                    Subtitle = "wingsuit decal",
                    Accent = Theme.Gliders,
                    DragPayload = new ToyboxDragPayload { Kind = "material", MaterialPath = a.Path },
                    MenuFactory = () => BuildMaterialTileMenu(a.Path, isUserMade: false),
                }).ToList(),
                header: "Per-character wingsuit decal materials (MI_DECAL_Wingsuit_*). Dropping one on the Glider row uses the matching native wingsuit component, then overrides only the decal material slot.",
                emptyMessage: "No wingsuit decals in the catalog.");
            return;
        }
    }

    private async Task ClearCustomGliderAsync()
    {
        EnsureProject();
        var project = _currentProject;
        if (project is null)
        {
            return;
        }

        var activeComponent = ActiveGliderVisualComponent(project);
        NativeSuitProject previousProjectSnapshot;
        try
        {
            previousProjectSnapshot = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(project))
                ?? throw new InvalidOperationException("Could not snapshot the suit before clearing its glider.");
        }
        catch (Exception ex)
        {
            AppendLog("Clear glider stopped before staging: " + ex.Message);
            return;
        }

        var projectRoot = _projectRootText.Text.Trim();
        BaseStageFilesystemSnapshot? stageFilesystemSnapshot = null;
        var saveAttempted = false;
        await RebuildGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_currentProject, project))
            {
                AppendLog("Clear glider stopped because the active suit changed while it was waiting to stage.");
                return;
            }
            stageFilesystemSnapshot = await CaptureBaseStageFilesystemAsync(
                projectRoot,
                project.SlotId,
                new[] { "UnpatchedStage", "PatchedNameMapStage", "GraftedPartStage" });

            var adapterComponents = project.PairedCapeAdapter is null
                ? []
                : RemovePairedCapeAdapterAtomically(project);
            if (adapterComponents.Count == 0)
            {
                project.PartGrafts.RemoveAll(graft => graft.IsGlider);
                project.GliderType = "";
                project.GliderMaterial = "";
                project.GliderGrafted = false;
                project.GliderAnimLas = "";
                project.GliderAnimMas = "";
                if (project.GliderAutoEnabledCustomArchetype)
                {
                    project.UseCustomArchetype = false;
                }
                project.GliderAutoEnabledCustomArchetype = false;
            }
            if (!string.IsNullOrWhiteSpace(activeComponent))
            {
                RemoveSavedRemovalForComponent(project, activeComponent);
            }
            foreach (var adapterComponent in adapterComponents)
            {
                RemoveSavedRemovalForComponent(project, adapterComponent);
            }

            RecordChange("Gliders", "Glide visual", "restored gameplay donor default", status: "staged");
            await RebuildGraftStageCoreAsync(project, projectRoot, persistProject: false);
            saveAttempted = true;
            await RunWithFileLockRetryAsync(
                () => (_projectService ??= new SuitProjectService(projectRoot)).SaveProject(project),
                "save the cleared glider");
            await FinalizeDeclarativeGraftStageAsync(project, projectRoot);
            await DiscardBaseStageFilesystemBackupAsync(stageFilesystemSnapshot, logFailure: true);
            stageFilesystemSnapshot = null;

            AppendLog(adapterComponents.Count > 0
                ? "Glider: removed the authored cosmetic Cape + Torso glide pair atomically and restored the gameplay donor's default glide visual."
                : "Glider: removed the custom preset and restored the gameplay donor's default glide visual.");
            _session.RaiseChanged();
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            Exception reportedFailure = ex;
            try
            {
                if (saveAttempted)
                {
                    await RunWithFileLockRetryAsync(
                        () => (_projectService ??= new SuitProjectService(projectRoot))
                            .SaveProject(previousProjectSnapshot),
                        "restore the prior project after a failed glider clear");
                }
                _currentProject = previousProjectSnapshot;
                ApplyProjectToFields(_currentProject);
                UpdateSelectedLabels();
                if (stageFilesystemSnapshot is not null)
                {
                    await RestoreBaseStageFilesystemAsync(
                        stageFilesystemSnapshot,
                        discardBackup: false);
                    await DiscardBaseStageFilesystemBackupAsync(stageFilesystemSnapshot, logFailure: true);
                    stageFilesystemSnapshot = null;
                }
            }
            catch (Exception restoreFailure)
            {
                reportedFailure = new AggregateException(
                    "Clearing the glider failed and the previous project/stage could not be fully restored.",
                    ex,
                    restoreFailure);
            }

            AppendLog("Clear glider failed; the prior project and generated stages were restored: " + reportedFailure.Message);
            Dialog.Error(
                this,
                "Glider was not cleared",
                "Batcomputer could not rebuild and save the complete glider removal. The previous project and generated stage remain active.\n\n" +
                reportedFailure.Message,
                windowTitle: "Gliders");
            _session.RaiseChanged();
            RefreshInspector();
            RefreshToyboxTiles();
        }
        finally
        {
            RebuildGate.Release();
        }
    }

    private void ShowGliderPresetDetail(NativeSuitPartRecord part)
    {
        var (las, mas) = GliderService.GliderAnimSetsForPart(part);
        var currentComponent = _currentProject is null
            ? ""
            : new AnimArchetypeGraftService().BaseGlideVisualComponent(_currentProject);
        var model = new Dialog.Model
        {
            WindowTitle = "Glider preset",
            Title = GliderService.GliderPresetLabel(part),
            Subtitle = $"{GliderService.KindLabel(part)} from {part.CharacterFolder}",
            Message =
                "This uses the donor's complete glide-only visual component: mesh, animation blueprint, " +
                "material slots, attach socket, and visibility tags.",
            Severity = Dialog.Level.Info,
            PrimaryText = "Use preset",
            SecondaryText = "Cancel"
        };
        model.Chips.Add((GliderService.KindLabel(part), Theme.Gliders));
        model.Chips.Add((GliderService.MountLabel(part), Theme.Info));
        model.Chips.Add((string.IsNullOrWhiteSpace(las) ? "native glide pose" : "matching pose graft", Theme.Good));
        model.Fields.Add(("Mesh", part.MeshObjectPath));
        model.Fields.Add(("Animation", string.IsNullOrWhiteSpace(part.AnimClassObjectPath) ? "(none)" : part.AnimClassObjectPath));
        model.Fields.Add(("Parent", string.IsNullOrWhiteSpace(part.ParentComponentOrVariableName) ? "CharacterMesh0" : part.ParentComponentOrVariableName));
        model.Fields.Add(("Socket", string.IsNullOrWhiteSpace(part.AttachSocket) ? "(root)" : part.AttachSocket));
        model.Fields.Add(("Gameplay", GliderService.GlidingAbilitySetPackage));
        model.Fields.Add(("Materials", $"{part.Materials.Count} donor slot(s)"));
        if (!string.IsNullOrWhiteSpace(las))
        {
            model.Fields.Add(("Body LAS", las));
        }
        if (!string.IsNullOrWhiteSpace(mas))
        {
            model.Fields.Add(("Glide MAS", mas));
        }

        if (_currentProject is null)
        {
            model.CalloutTitle = "Set a suit base first";
            model.CalloutDetail = "The preset can be inspected now, but it needs an active suit before it can be applied.";
        }
        else if (string.IsNullOrWhiteSpace(currentComponent))
        {
            model.CalloutTitle = "Adds a new glide visual";
            model.CalloutDetail =
                "This base has no existing glide component. Batcomputer will add the donor component " +
                "under its own Glider slot, add AS_Gliding, and graft the matching body pose.";
        }
        else
        {
            model.CalloutTitle = $"Replaces {currentComponent}";
            model.CalloutDetail =
                "The existing glide visual stays wired to the game's Visible.Glider state while its " +
                "mesh, animation blueprint, materials, and mount are replaced by this preset.";
        }

        if (Dialog.Show(this, model))
        {
            _ = ApplyNativeGliderPresetAsync(part);
        }
    }

    /// <summary>
    /// Asks which 0-based equipment slot to replace, showing the current occupant
    /// of each slot (read from the base donor DCMD, or the standard Batarang/BatClaw
    /// loadout as a fallback). Returns -1 if cancelled.
    /// </summary>
    private int AskEquipmentSlot(GameDataEquipment eq)
    {
        var current = CurrentEquipmentSlotNames();

        using var dlg = new AdaptiveDialogForm
        {
            Text = $"Place {eq.Name} in which slot?",
            Width = 420,
            Height = 210,
            AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.WindowBg,
            ForeColor = Theme.OnDark,
        };
        dlg.Shown += (_, _) => Theme.UseDarkTitleBar(dlg);
        var info = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 12, 12, 0), Text = $"Characters carry two gadgets. Which slot should '{eq.Name}' replace?" };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 8, 12, 12), WrapContents = false, AutoScroll = true };

        int chosen = -1;
        for (var i = 0; i < Math.Max(2, current.Count); i++)
        {
            var slotIndex = i;
            var occupant = i < current.Count ? current[i] : "(empty)";
            var staged = _currentProject?.EquipmentSlots.FirstOrDefault(s => s.Slot == slotIndex);
            var label = staged is not null ? $"{occupant} → {staged.Gadget} (staged)" : occupant;
            var btn = new Button { Text = $"Slot {slotIndex + 1}:  {label}", Width = 370, Height = 40, Margin = new Padding(0, 0, 0, 6) };
            Theme.StyleSmallDarkButton(btn);
            btn.Click += (_, _) => { chosen = slotIndex; dlg.DialogResult = DialogResult.OK; };
            flow.Controls.Add(btn);
        }
        flow.ClientSizeChanged += (_, _) =>
        {
            var width = Math.Max(120,
                flow.ClientSize.Width - flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
            foreach (var button in flow.Controls.OfType<Button>())
            {
                button.Width = width;
            }
        };

        dlg.Controls.Add(flow);
        dlg.Controls.Add(info);
        return dlg.ShowDialog(this) == DialogResult.OK ? chosen : -1;
    }

    /// <summary>Current gadget names per slot, from the base donor DCMD if readable.</summary>
    private List<string> CurrentEquipmentSlotNames()
    {
        try
        {
            var donorDcmd = _currentProject?.DcmdTemplate?.Uasset;
            if (string.IsNullOrWhiteSpace(donorDcmd) || !File.Exists(donorDcmd))
            {
                donorDcmd = PackageToExtractedUasset(
                    _currentProject?.DcmdTemplate?.PackagePath ?? "",
                    AppSettings.Current.EffectiveExtractedContentRoot());
            }
            if (string.IsNullOrWhiteSpace(donorDcmd) || !File.Exists(donorDcmd))
            {
                var playable = _currentProject?.PlayableTemplate?.Uasset;
                if (string.IsNullOrWhiteSpace(playable) || !File.Exists(playable))
                {
                    playable = PackageToExtractedUasset(
                        _currentProject?.PlayableTemplate?.PackagePath ?? "",
                        AppSettings.Current.EffectiveExtractedContentRoot());
                }
                donorDcmd = string.IsNullOrWhiteSpace(playable) ? null : FindDcmdSiblingForPlayable(playable);
            }

            donorDcmd ??= DcmdGenService.ResolveBaseDcmdPath();
            var names = new DcmdGenService(_projectRootText.Text.Trim()).ReadEquipmentSlots(donorDcmd);
            if (names.Count > 0)
            {
                // Strip DA_ETA_ prefix for readability.
                return names.Select(n => n.StartsWith("DA_ETA_", StringComparison.OrdinalIgnoreCase) ? n["DA_ETA_".Length..] : n).ToList();
            }
        }
        catch { /* fall through to default loadout */ }

        return new List<string> { "Batarang", "Batclaw" };
    }

    private static bool MatchesPartSearch(NativeSuitPartRecord part, string search)
    {
        var materialNames = string.Join(" ", part.Materials.Select(material => $"{material.ObjectName} {material.ObjectPath}"));
        return MatchesToyboxSearch(
            search,
            CleanPartMeshDisplayName(part),
            part.MeshObjectName,
            part.MeshObjectPath,
            part.MeshPackagePath,
            part.MeshKind,
            part.Slot,
            part.Context,
            part.CharacterFolder,
            part.Stem,
            part.SourcePackagePath,
            part.ComponentTemplateExport,
            part.AttachSocket,
            materialNames);
    }

    private IEnumerable<NativeSuitPartRecord> ToyboxPartCandidates(string selectedSlot)
    {
        // Catalog-driven attachment libraries need no extracted part index.
        if (selectedSlot.Equals("Attachment: Hair", StringComparison.OrdinalIgnoreCase) ||
            selectedSlot.Equals("Attachment: Hat", StringComparison.OrdinalIgnoreCase))
        {
            var isHair = selectedSlot.EndsWith("Hair", StringComparison.OrdinalIgnoreCase);
            var parts = isHair ? AttachmentCatalogService.HairParts() : AttachmentCatalogService.HatParts();
            var attachSearch = CurrentToyboxSearch();
            if (!string.IsNullOrWhiteSpace(attachSearch))
            {
                parts = parts.Where(p => MatchesPartSearch(p, attachSearch));
            }
            return parts;
        }

        if (_partIndex is null)
        {
            return Enumerable.Empty<NativeSuitPartRecord>();
        }

        IEnumerable<NativeSuitPartRecord> query = _partIndex.Parts
            .Where(part => part.HasMesh)
            .Where(part => !IsGliderVisualPart(part))
            // Faces have their own dedicated category - keep them out of the Parts browser.
            .Where(part => !part.Slot.Equals("Face", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(selectedSlot) &&
            !selectedSlot.Equals("<all parts>", StringComparison.OrdinalIgnoreCase) &&
            !selectedSlot.Equals("Build part index first", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(part => part.Slot.Equals(selectedSlot, StringComparison.OrdinalIgnoreCase));
        }

        // Context filter (A), mesh-kind filter (B), source-character filter (C).
        var contextFilter = FilterVal(0);
        if (contextFilter is not null)
        {
            query = query.Where(part => part.Context.Equals(contextFilter, StringComparison.OrdinalIgnoreCase));
        }

        var meshFilter = FilterVal(1);
        if (meshFilter == "Skeletal")
        {
            query = query.Where(part => part.MeshKind.Contains("Skel", StringComparison.OrdinalIgnoreCase));
        }
        else if (meshFilter == "Static")
        {
            query = query.Where(part => part.MeshKind.Contains("Static", StringComparison.OrdinalIgnoreCase));
        }

        var sourceFilter = FilterVal(2);
        if (sourceFilter is not null && !sourceFilter.Equals("Your meshes", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(part => part.CharacterFolder.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase));
        }

        var search = CurrentToyboxSearch();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(part => MatchesPartSearch(part, search));
        }

        return query
            .OrderBy(part => part.IsLikelyGraftCandidate ? 0 : 1)
            .ThenBy(part => part.Context.Equals("playable", StringComparison.OrdinalIgnoreCase) ? 0 :
                part.Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(part => part.CharacterFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.Stem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.MeshObjectName, StringComparer.OrdinalIgnoreCase);
    }

    private Button MakePartTile(NativeSuitPartRecord part)
    {
        var title = TrimMiddle(CleanPartMeshDisplayName(part), 30);
        var subtitle = $"{part.Slot} • {part.Context}\nfrom {TrimMiddle(PartSourceDisplayName(part), 24)}";

        var tile = MakeDragTile(
            title,
            subtitle,
            Theme.Parts,
            new ToyboxDragPayload { Kind = "part", Part = part },
            BuildPartTileMenu(part));
        tile.Width = 154;
        tile.Height = 96;
        _toyboxToolTip.SetToolTip(
            tile,
            $"{part.Slot} from {part.SourcePackagePath}\nMesh: {part.MeshObjectPath}\nDisplay: {CleanPartMeshDisplayName(part)}\nMaterials: {string.Join(", ", part.Materials.Select(m => m.ObjectPath).Take(4))}");
        return tile;
    }

    private void SelectToyboxPart(NativeSuitPartRecord part)
    {
        _partSlotCombo.Text = part.Slot;

        var playable = part.Context.Equals("playable", StringComparison.OrdinalIgnoreCase)
            ? part
            : FindCounterpartPart(part, "playable");
        var cutscene = part.Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase)
            ? part
            : FindCounterpartPart(part, "cutscene");

        // Do not blindly graft a playable-only donor into cutscene (or vice
        // versa). If a counterpart is missing, the selected role is still useful
        // and Advanced remains available for explicit per-role picks.
        _selectedPlayablePart = playable ??
            (part.Context.Equals("playable", StringComparison.OrdinalIgnoreCase) ? part : null);
        _selectedCutscenePart = cutscene ??
            (part.Context.Equals("cutscene", StringComparison.OrdinalIgnoreCase) ? part : null);

        UpdateSelectedPartLabels();
        AppendLog($"Toybox selected part: {DescribePart(part)}");
        AppendLog($"  playable donor: {(_selectedPlayablePart is null ? "<none>" : DescribePart(_selectedPlayablePart))}");
        AppendLog($"  cutscene donor: {(_selectedCutscenePart is null ? "<none>" : DescribePart(_selectedCutscenePart))}");
    }

    private NativeSuitPartRecord? FindCounterpartPart(NativeSuitPartRecord part, string desiredContext)
    {
        if (_partIndex is null)
        {
            return null;
        }

        var candidates = _partIndex.Parts
            .Where(candidate =>
                candidate.HasMesh &&
                candidate.Context.Equals(desiredContext, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sameSlot = candidates
            .Where(candidate => candidate.Slot.Equals(part.Slot, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameSlot.Count > 0)
        {
            candidates = sameSlot;
        }

        if (!string.IsNullOrWhiteSpace(part.MeshObjectName))
        {
            var exactMesh = candidates
                .Where(candidate => candidate.MeshObjectName.Equals(part.MeshObjectName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exactMesh.Count > 0)
            {
                candidates = exactMesh;
            }
            else
            {
                var exactMeshAnySlot = _partIndex.Parts
                    .Where(candidate =>
                        candidate.HasMesh &&
                        candidate.Context.Equals(desiredContext, StringComparison.OrdinalIgnoreCase) &&
                        candidate.MeshObjectName.Equals(part.MeshObjectName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (exactMeshAnySlot.Count > 0)
                {
                    candidates = exactMeshAnySlot;
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.CharacterFolder.Equals(part.CharacterFolder, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => candidate.MeshObjectName.Equals(part.MeshObjectName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => candidate.AnimClassObjectName.Equals(part.AnimClassObjectName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private NativeSuitPartRecord? FindExactMeshCounterpartPart(NativeSuitPartRecord part, string desiredContext)
    {
        if (_partIndex is null ||
            string.IsNullOrWhiteSpace(part.MeshObjectName) ||
            string.IsNullOrWhiteSpace(part.CharacterFolder) ||
            string.IsNullOrWhiteSpace(part.SourcePackagePath))
        {
            return null;
        }

        return _partIndex.Parts
            .Where(candidate =>
                candidate.HasMesh &&
                candidate.Context.Equals(desiredContext, StringComparison.OrdinalIgnoreCase) &&
                candidate.CharacterFolder.Equals(part.CharacterFolder, StringComparison.OrdinalIgnoreCase) &&
                BaseEligibilityService.IsSameCharacterVariant(
                    part.SourcePackagePath,
                    candidate.SourcePackagePath) &&
                (candidate.MeshObjectName.Equals(part.MeshObjectName, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(part.MeshObjectPath) &&
                  candidate.MeshObjectPath.Equals(part.MeshObjectPath, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(candidate => candidate.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string CleanPartMeshDisplayName(NativeSuitPartRecord part)
    {
        var raw = !string.IsNullOrWhiteSpace(part.MeshObjectName)
            ? part.MeshObjectName
            : (!string.IsNullOrWhiteSpace(part.MeshObjectPath) ? part.MeshObjectPath : part.MeshPackagePath);
        return CleanAssetDisplayName(raw);
    }

    private static string PartSourceDisplayName(NativeSuitPartRecord part)
    {
        if (!string.IsNullOrWhiteSpace(part.CharacterFolder))
        {
            return part.CharacterFolder;
        }

        if (!string.IsNullOrWhiteSpace(part.Stem))
        {
            return part.Stem;
        }

        return "(unknown source)";
    }

    private Control CreatePartsStepPanel()
    {
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _buildPartIndexButton.Text = "Build part index";
        _buildPartIndexButton.Dock = DockStyle.Fill;
        toolbar.Controls.Add(_buildPartIndexButton, 0, 0);
        _loadIndexButton.Text = "Load index";
        _loadIndexButton.Dock = DockStyle.Fill;
        toolbar.Controls.Add(_loadIndexButton, 1, 0);
        toolbar.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.OnDarkMuted, Text = "Build the part index once, then pick a part and graft it into a slot." }, 2, 0);
        container.Controls.Add(toolbar, 0, 0);

        container.Controls.Add(CreatePartPickerPanel(), 0, 1);
        return container;
    }

    private Control CreatePartPickerPanel()
    {
        var box = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Native part picker"
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        box.Controls.Add(layout);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 9,
            RowCount = 1
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        layout.Controls.Add(filters, 0, 0);

        filters.Controls.Add(new Label { Text = "Context", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _partContextCombo.Dock = DockStyle.Fill;
        _partContextCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _partContextCombo.Items.AddRange(new object[] { "playable", "cutscene", "batcave", "<all>" });
        _partContextCombo.SelectedIndex = 0;
        filters.Controls.Add(_partContextCombo, 1, 0);

        filters.Controls.Add(new Label { Text = "Slot", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        _partSlotCombo.Dock = DockStyle.Fill;
        _partSlotCombo.DropDownStyle = ComboBoxStyle.DropDown;
        _partSlotCombo.Text = "Torso2";
        filters.Controls.Add(_partSlotCombo, 3, 0);

        filters.Controls.Add(new Label { Text = "Search", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 4, 0);
        _partSearchText.Dock = DockStyle.Fill;
        filters.Controls.Add(_partSearchText, 5, 0);

        _refreshPartGridButton.Text = "Refresh";
        _refreshPartGridButton.Dock = DockStyle.Fill;
        filters.Controls.Add(_refreshPartGridButton, 6, 0);

        _usePartAsPlayableButton.Text = "Use for playable";
        _usePartAsPlayableButton.Dock = DockStyle.Fill;
        filters.Controls.Add(_usePartAsPlayableButton, 7, 0);

        _usePartAsCutsceneButton.Text = "Use for cutscene";
        _usePartAsCutsceneButton.Dock = DockStyle.Fill;
        filters.Controls.Add(_usePartAsCutsceneButton, 8, 0);

        ConfigurePartGrid(_partGrid);
        layout.Controls.Add(_partGrid, 0, 1);

        var selectedLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        selectedLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        selectedLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        selectedLayout.Controls.Add(_selectedPlayablePartLabel, 0, 0);
        selectedLayout.Controls.Add(_selectedCutscenePartLabel, 0, 1);
        layout.Controls.Add(selectedLayout, 0, 2);

        return box;
    }

    private static void ConfigurePartGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoGenerateColumns = false;
        grid.RowHeadersVisible = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Context", DataPropertyName = nameof(PartRow.Context), Width = 75 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", DataPropertyName = nameof(PartRow.Slot), Width = 85 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Character", DataPropertyName = nameof(PartRow.CharacterFolder), Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mesh kind", DataPropertyName = nameof(PartRow.MeshKind), Width = 85 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mesh", DataPropertyName = nameof(PartRow.MeshObjectName), Width = 210 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Materials", DataPropertyName = nameof(PartRow.MaterialsSummary), Width = 260 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source package", DataPropertyName = nameof(PartRow.SourcePackagePath), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
    }

    private async Task<bool> EnsurePartIndexAsync(string? projectRootOverride = null)
    {
        var projectRoot = string.IsNullOrWhiteSpace(projectRootOverride)
            ? _projectRootText.Text.Trim()
            : projectRootOverride.Trim();
        await PartIndexGate.WaitAsync();
        try
        {
            var service = new PartIndexService(projectRoot);
            try
            {
                // Always reload the cache for this project root. Viewer/acceptance builds can use a
                // disposable root while the main window still has another workspace index in memory.
                _partIndex = service.LoadPartIndex();
            }
            catch (Exception ex)
            {
                _partIndex = null;
                AppendLog($"Part index cache could not be read and will be rebuilt: {ex.Message}");
            }

            if (_partIndex is { Parts.Count: > 0 })
            {
                return true;
            }

            AppendLog("Part index is missing, stale, empty, or unreadable; rebuilding it from the extracted character assets…");
            try
            {
                _partIndex = await Task.Run(() => service.BuildPartIndex());
                AppendLog($"Part index status: {_partIndex.Status}; parts indexed: {_partIndex.Parts.Count}");
                return _partIndex.Parts.Count > 0;
            }
            catch (Exception ex)
            {
                _partIndex = null;
                AppendLog($"Part index build failed: {ex.Message}");
                return false;
            }
        }
        finally
        {
            PartIndexGate.Release();
        }
    }

    private async Task BuildPartIndexAsync()
    {
        var projectRoot = _projectRootText.Text.Trim();
        var service = new PartIndexService(projectRoot);

        AppendLog("Building native suit part index from extracted cooked character BPs…");
        _buildPartIndexButton.Enabled = false;
        await PartIndexGate.WaitAsync();
        try
        {
            var index = await Task.Run(() => service.BuildPartIndex());
            _partIndex = index;
            AppendLog($"Part index status: {index.Status}");
            AppendLog($"Part index path: {service.PartIndexPath}");
            AppendLog($"Assets found={index.AssetsFound} parsed={index.AssetsParsed} withParts={index.AssetsWithParts} errors={index.Errors.Count}");
            AppendLog($"Parts indexed: {index.Parts.Count}");

            var slotSummary = index.Parts
                .GroupBy(part => part.Slot, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(group => $"{group.Key}={group.Count()}");
            AppendLog("Top slots: " + string.Join(", ", slotSummary));

            var contextSummary = index.Parts
                .GroupBy(part => part.Context, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}={group.Count()}");
            AppendLog("Contexts: " + string.Join(", ", contextSummary));

            if (index.Errors.Count > 0)
            {
                AppendLog("First part-index scan errors:");
                foreach (var error in index.Errors.Take(8))
                {
                    AppendLog($"{error.Uasset}: {error.Error}");
                }
            }

            UpdatePartSlotChoices();
            RefreshPartGrid();
            PopulateToyboxTypes();
            // The source list is built from the index, so it has to be rebuilt now the index exists.
            ConfigureToyboxFilters();
            RefreshToyboxTiles();
        }
        catch (Exception ex)
        {
            AppendLog("Part index build failed:");
            AppendLog(ex.ToString());
        }
        finally
        {
            PartIndexGate.Release();
            _buildPartIndexButton.Enabled = true;
        }
    }

    private void LoadPartIndexAndRefreshGrid(bool logIfMissing = true)
    {
        var projectRoot = _projectRootText.Text.Trim();
        var service = new PartIndexService(projectRoot);
        _partIndex = service.LoadPartIndex();
        if (_partIndex is null)
        {
            _partCandidates = new List<NativeSuitPartRecord>();
            _partGrid.DataSource = new List<PartRow>();
            if (logIfMissing)
            {
                AppendLog($"Part index not found yet: {service.PartIndexPath}");
                AppendLog("Run Build part index first.");
            }
            return;
        }

        AppendLog($"Loaded part index: {_partIndex.Parts.Count} parts.");
        UpdatePartSlotChoices();
        RefreshPartGrid();
        PopulateToyboxTypes();
        ConfigureToyboxFilters();
        RefreshToyboxTiles();
    }

    private void UpdatePartSlotChoices()
    {
        if (_partIndex is null)
        {
            return;
        }

        var current = _partSlotCombo.Text;
        var slots = _partIndex.Parts
            .Select(part => part.Slot)
            .Where(slot => !string.IsNullOrWhiteSpace(slot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _partSlotCombo.BeginUpdate();
        try
        {
            _partSlotCombo.Items.Clear();
            _partSlotCombo.Items.Add("<all>");
            foreach (var slot in slots)
            {
                _partSlotCombo.Items.Add(slot);
            }
        }
        finally
        {
            _partSlotCombo.EndUpdate();
        }

        _partSlotCombo.Text = string.IsNullOrWhiteSpace(current) ? "Torso2" : current;
    }

    private void RefreshPartGrid()
    {
        if (_partIndex is null)
        {
            return;
        }

        var context = _partContextCombo.Text.Trim();
        var slot = _partSlotCombo.Text.Trim();
        var search = _partSearchText.Text.Trim();

        IEnumerable<NativeSuitPartRecord> query = _partIndex.Parts
            .Where(part => part.HasMesh)
            .Where(part => !IsGliderVisualPart(part));

        if (!string.IsNullOrWhiteSpace(context) && !context.Equals("<all>", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(part => part.Context.Equals(context, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(slot) && !slot.Equals("<all>", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(part => part.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(part =>
                part.SourcePackagePath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                part.CharacterFolder.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                part.Stem.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                part.MeshObjectName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                part.MeshObjectPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                part.Materials.Any(material => material.ObjectPath.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        _partCandidates = query
            .OrderBy(part => part.Context, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.Slot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.CharacterFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
            .Take(800)
            .ToList();

        _partGrid.DataSource = _partCandidates.Select(PartRow.FromRecord).ToList();
    }

    private NativeSuitPartRecord? GetCurrentPartGridSelection()
    {
        if (_partGrid.CurrentRow?.DataBoundItem is not PartRow row)
        {
            return null;
        }

        return _partCandidates.FirstOrDefault(part =>
            part.Context.Equals(row.Context, StringComparison.OrdinalIgnoreCase) &&
            part.Slot.Equals(row.Slot, StringComparison.OrdinalIgnoreCase) &&
            part.SourcePackagePath.Equals(row.SourcePackagePath, StringComparison.OrdinalIgnoreCase) &&
            part.ComponentTemplateExport.Equals(row.ComponentTemplateExport, StringComparison.OrdinalIgnoreCase));
    }

    private void UseSelectedPartForPlayable()
    {
        var part = GetCurrentPartGridSelection();
        if (part is null)
        {
            AppendLog("No part selected.");
            return;
        }

        _selectedPlayablePart = part;
        _partSlotCombo.Text = part.Slot;
        UpdateSelectedPartLabels();
        AppendLog($"Selected playable donor part: {DescribePart(part)}");
    }

    private void UseSelectedPartForCutscene()
    {
        var part = GetCurrentPartGridSelection();
        if (part is null)
        {
            AppendLog("No part selected.");
            return;
        }

        _selectedCutscenePart = part;
        _partSlotCombo.Text = part.Slot;
        UpdateSelectedPartLabels();
        AppendLog($"Selected cutscene donor part: {DescribePart(part)}");
    }

    private async Task GraftTorso2Async()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return;
        }

        ReadFieldsIntoProject(_currentProject);
        AppendLog("Creating experimental Thomas + Absolute Torso2 graft stage…");
        _graftTorso2Button.Enabled = false;
        try
        {
            var projectRoot = _projectRootText.Text.Trim();
            var result = await Task.Run(() => new PartGraftService(projectRoot).CreateTorso2GraftedStage(_currentProject));
            AppendLog($"Torso2 graft status: {result.Status}");
            AppendLog($"Grafted content root: {result.GraftedContentRoot}");
            AppendLog($"Graft report: {result.ReportPath}");
            foreach (var package in result.PackageResults)
            {
                AppendLog($"{package.Role}: success={package.Success} alreadyHadTorso2={package.AlreadyHadTorso2} addedImports={package.AddedImports} addedExports={package.AddedExports} componentExport={package.NewComponentExportIndex} scsNodeExport={package.NewScsNodeExportIndex}");
                if (!package.Success && !string.IsNullOrWhiteSpace(package.Error))
                {
                    AppendLog(package.Error);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog("Torso2 graft failed:");
            AppendLog(ex.ToString());
        }
        finally
        {
            _graftTorso2Button.Enabled = true;
        }
    }

    private static bool IsGliderVisualPart(NativeSuitPartRecord part)
    {
        return GliderService.IsNativeGliderPart(part);
    }

    /// <summary>
    /// A base having separate Cape + Glider components is necessary but not sufficient: the
    /// replacement glider's AnimBlueprint must also emit the paired-cape visibility signal.
    /// Wingsuit/Talia/Gordon drivers animate themselves but leave the regular Cape visible.
    /// Unknown drivers are rejected until their runtime contract is proven.
    /// </summary>
    private bool BlockUnsupportedCapeGliderPairing(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus baseContract,
        SavedPartGraft? incomingGraft,
        string windowTitle)
    {
        // Projects saved before complete donor recipes were persisted can be repaired from the
        // current exact-source part index before adapter preflight. This is metadata migration only;
        // fail closed rather than borrowing a same-mesh or same-package donor recipe.
        foreach (var savedGraft in project.PartGrafts)
        {
            BackfillSavedDonorRecipe(savedGraft.Playable, ResolveExactLivePart(savedGraft.Playable));
            BackfillSavedDonorRecipe(savedGraft.Cutscene, ResolveExactLivePart(savedGraft.Cutscene));
        }

        var incomingGlider = incomingGraft?.IsGlider == true;
        var addingCosmeticCape = incomingGraft is not null &&
                                 !incomingGraft.IsGlider &&
                                 (GliderService.IsCosmeticCapeAttachment(incomingGraft.Playable) ||
                                  GliderService.IsCosmeticCapeAttachment(incomingGraft.Cutscene));
        if (!GliderService.HasCapeAndGliderCombination(
                project,
                baseContract,
                addingCosmeticCape: addingCosmeticCape,
                addingGlider: incomingGlider))
        {
            return false;
        }

        var hasReplacementGlider = incomingGlider ||
                                   GliderService.ProjectHasReplacementGlider(project);
        var driver = incomingGlider
            ? GliderService.PairedCapeDriverForDonor(
                incomingGraft!.Playable ?? incomingGraft.Cutscene)
            : hasReplacementGlider
                ? GliderService.ProjectReplacementGliderDriver(project)
                : baseContract == AnimArchetypeGraftService.CapeGlideContractStatus.Paired
                    ? PairedCapeVisibilityDriver.PairedCapable
                    : baseContract == AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly
                        ? PairedCapeVisibilityDriver.GlideOnly
                        : PairedCapeVisibilityDriver.Unknown;
        if (baseContract != AnimArchetypeGraftService.CapeGlideContractStatus.Paired)
        {
            if (incomingGraft is not null &&
                driver == PairedCapeVisibilityDriver.PairedCapable &&
                GliderService.CanConfigurePairedCapeAdapterWithIncoming(
                    project,
                    baseContract,
                    incomingGraft,
                    out var adapterDetail))
            {
                AppendLog("Cape adapter preflight: " + adapterDetail);
                return false;
            }

            var detail = baseContract == AnimArchetypeGraftService.CapeGlideContractStatus.Unknown
                ? "Batcomputer could not verify that this playable base owns the native two-component cape visibility setup. Refresh the character assets and run the build check before pairing a regular cape with a glider."
                : baseContract == AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly
                    ? "This gameplay donor owns only a native glide visual. To preserve its play style, first apply a proven ABP_Cape_Glide preset, then add the matching native regular cape from that same playable/cutscene donor pair. Batcomputer will construct and verify a dynamic paired-cape adapter."
                    : "This playable base does not natively own separate regular-cape and glide-visual components. Adding both is not a proven runtime layout and may crash or leave the regular cape visible during gliding.";
            Dialog.Error(this,
                "Cape and glider are not compatible with this base",
                detail + "\n\nIf the adapter requirements cannot be met, choose a verified two-cape playable donor or remove the regular Cape.",
                windowTitle: windowTitle);
            return true;
        }

        if (!hasReplacementGlider)
        {
            // The untouched glider on a base classified Paired already owns the proven driver.
            return false;
        }

        if (driver == PairedCapeVisibilityDriver.PairedCapable)
        {
            return false;
        }

        var driverDetail = driver == PairedCapeVisibilityDriver.GlideOnly
            ? "This glider uses a glide-only animation blueprint. It animates the glide visual, but it does not send the hide/show signal for a separate regular Cape, so both would appear while gliding."
            : "Batcomputer could not prove that this glider's animation blueprint drives a separate regular Cape. Unknown visibility drivers are blocked to prevent a double-cape build.";
        Dialog.Error(this,
            "Glider cannot hide the regular Cape",
            driverDetail + "\n\nUse a native glide cape driven by ABP_Cape_Glide (including Batgirl Party), or remove the regular Cape before using this glider.",
            windowTitle: windowTitle);
        return true;
    }

    private bool _graftSelectedPartInProgress;

    private async Task<bool> GraftSelectedPartsAsync()
    {
        if (_graftSelectedPartInProgress)
        {
            AppendLog("A selected-part graft is already running; the second request was ignored.");
            return false;
        }

        _graftSelectedPartInProgress = true;
        var workspaceWasEnabled = _mainWorkspaceHost.Enabled;
        _mainWorkspaceHost.Enabled = false;
        try
        {
            return await GraftSelectedPartsCoreAsync();
        }
        finally
        {
            _mainWorkspaceHost.Enabled = workspaceWasEnabled;
            _graftSelectedPartInProgress = false;
        }
    }

    private async Task<bool> GraftSelectedPartsCoreAsync()
    {
        EnsureProject();
        if (_currentProject is null)
        {
            return false;
        }
        var transactionProject = _currentProject;
        var selectedPlayablePart = _selectedPlayablePart;
        var selectedCutscenePart = _selectedCutscenePart;

        if (selectedPlayablePart is null && selectedCutscenePart is null)
        {
            AppendLog("No selected part donors. Pick a row in the Part picker tab, then click Use for playable and/or Use for cutscene.");
            return false;
        }

        var samplePart = selectedPlayablePart ?? selectedCutscenePart!;
        var targetSlot = samplePart.Slot;

        // Glider/cape parts (GA_Glider_*, GA_Wingsuit_*, SK_CAPE_Glide - they come in
        // on a "Cape" slot or carry the "Glider" tag) must land on the base's ACTUAL
        // glide-visual component so the game's glide-visibility wiring drives them.
        // That component is named "Cape" on some characters but "Torso" on Batman -
        // retarget to it so we replace the real glide visual instead of adding a stray.
        var isGliderPart = IsGliderVisualPart(samplePart);
        var isCosmeticCape = GliderService.IsCosmeticCapeAttachment(samplePart);
        var contract = isGliderPart || isCosmeticCape
            ? new AnimArchetypeGraftService().BaseCapeGlideContract(transactionProject)
            : AnimArchetypeGraftService.CapeGlideContractStatus.Unknown;
        var additiveCapeConflict =
            (isGliderPart || isCosmeticCape) &&
            GliderService.HasAdditiveCapeAndGliderCombination(
                transactionProject,
                contract,
                addingGlider: isGliderPart);
        if (additiveCapeConflict)
        {
            Dialog.Error(this,
                "Custom Cape and glider are not compatible",
                "This suit has a custom static mesh attached to Cape. Custom meshes are additive components and are not driven by the playable base's native cape/glider visibility wiring, even on a native two-cape base.\n\n" +
                "Remove the custom Cape attachment or the glider before adding this part.",
                windowTitle: "Parts");
            return false;
        }
        if ((isGliderPart || isCosmeticCape) &&
            BlockUnsupportedCapeGliderPairing(
                transactionProject,
                contract,
                incomingGraft: ProspectivePartGraft(
                    isGliderPart,
                    selectedPlayablePart,
                    selectedCutscenePart),
                windowTitle: "Parts"))
        {
            return false;
        }

        // Part/glider selection mutates several declarative fields before staging. Snapshot the
        // fully synchronized project so a failed donor-shell clone or role replay cannot leave a
        // certified-looking adapter in memory (or be saved later by an unrelated UI action).
        ReadFieldsIntoProject(transactionProject);
        NativeSuitProject previousProjectSnapshot;
        try
        {
            previousProjectSnapshot = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(transactionProject))
                ?? throw new InvalidOperationException("Could not snapshot the suit before grafting the selected part.");
        }
        catch (Exception ex)
        {
            AppendLog("Selected-part graft stopped before staging: " + ex.Message);
            return false;
        }
        _graftSelectedPartButton.Enabled = false;
        var projectRoot = _projectRootText.Text.Trim();
        var projectSaved = false;
        var gateHeld = false;
        BaseStageFilesystemSnapshot? stageFilesystemSnapshot = null;
        try
        {
            // Own the rebuild gate for the complete project/stage transaction. The snapshot is
            // captured before RestoreProtectedGliderComponent or any declarative field mutation.
            await RebuildGate.WaitAsync();
            gateHeld = true;
            if (!ReferenceEquals(_currentProject, transactionProject))
            {
                AppendLog("Selected-part graft stopped because the active suit changed while it was waiting to stage.");
                return false;
            }
            stageFilesystemSnapshot = await CaptureBaseStageFilesystemAsync(
                projectRoot,
                transactionProject.SlotId,
                // RebuildGraftStageCoreAsync can refresh the clean authored base before it
                // recreates the graft stage. Snapshot every stage it can mutate so a later
                // part/material failure cannot leave a tentative PatchedNameMapStage behind.
                new[] { "UnpatchedStage", "PatchedNameMapStage", "GraftedPartStage" });

            if (isGliderPart)
            {
                if (!transactionProject.UseCustomArchetype)
                {
                    transactionProject.UseCustomArchetype = true;
                    transactionProject.GliderAutoEnabledCustomArchetype = true;
                    RecordChange("Animations", "archetype", "enabled for glider dependencies", status: "staged");
                    AppendLog("Glider: enabled the custom archetype for its gameplay and pose dependencies.");
                }

                var glideComp = new AnimArchetypeGraftService().BaseGlideVisualComponent(transactionProject);
                if (!string.IsNullOrWhiteSpace(glideComp))
                {
                    // Base HAS a native glide visual (Batman Torso, Catwoman/Nightwing/Gordon
                    // Cape) → repoint it.
                    if (RemoveSavedRemovalForComponent(transactionProject, glideComp))
                    {
                        AppendLog($"Glider part: removed stale remove-component rule for native glide component '{glideComp}'.");
                    }

                    RestoreProtectedGliderComponent(transactionProject, glideComp);

                    // The glider owns the glide component's materials (its own decal + solid).
                    // Drop any saved material override so replay cannot paint over the glider.
                    if (ClearMaterialAssignmentsForComponent(transactionProject, glideComp))
                    {
                        AppendLog($"Glider part: cleared saved material override on glide component '{glideComp}' (the glider provides its own material; recolor via the glider decal).");
                    }

                    if (!glideComp.Equals(targetSlot, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog($"Glider part → retargeting to the base's glide-visual component '{glideComp}' so it replaces the real glide (not a stray Cape).");
                        targetSlot = glideComp!;
                    }
                }
                else
                {
                    targetSlot = "Glider";
                    AppendLog("Glider: adding a dedicated 'Glider' component so no existing torso or cape component is replaced.");
                    ClearMaterialAssignmentsForComponent(transactionProject, targetSlot);
                }

                // Cross-type glide: record the donor character's glide anim sets so the package
                // step injects them into the suit's LAS_Char/MAS_Char. The paired-cape adapter
                // re-derives and certifies these paths from its exact Cape + Torso donor.
                var (gliderLas, gliderMas) = GliderService.GliderAnimSetsForPart(samplePart);
                transactionProject.GliderAnimLas = gliderLas;
                transactionProject.GliderAnimMas = gliderMas;
                if (!string.IsNullOrWhiteSpace(gliderLas))
                {
                    AppendLog($"Glider: glide animation will switch to the '{samplePart.CharacterFolder}' style ({gliderLas[(gliderLas.LastIndexOf('/') + 1)..]} + {gliderMas[(gliderMas.LastIndexOf('/') + 1)..]}) so the wingsuit poses correctly.");
                }
                else
                {
                    AppendLog("Glider: the donor character did not expose a resolvable glide-animation set.");
                }
            }

            // Static head/hair/hat attachments are native Head-slot parts attached to
            // HeadStud_Attach_Socket. Do not retarget them to the non-native NeckPeg slot.
            if (selectedPlayablePart is not null &&
                selectedCutscenePart is not null &&
                !selectedPlayablePart.Slot.Equals(selectedCutscenePart.Slot, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Warning: selected playable slot '{selectedPlayablePart.Slot}' and cutscene slot '{selectedCutscenePart.Slot}' differ. Target slot will use '{targetSlot}'.");
            }

            var cloneSlot = GuessCloneSlot(samplePart);
            var attachSocket = GuessAttachSocket(samplePart);
            AppendLog($"Drop: recording part in slot '{targetSlot}' (clone {cloneSlot}, attach {attachSocket}); rebuilding graft stage from clean base…");

            // Rebuild the clean stage from the saved graft list.
            UpsertPartGraft(transactionProject, targetSlot, isGliderPart, selectedPlayablePart, selectedCutscenePart);
            if (isGliderPart || isCosmeticCape)
            {
                var nowHasPair = GliderService.HasCapeAndGliderCombination(
                    transactionProject,
                    contract);
                if (contract == AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly && nowHasPair)
                {
                    var nativeGliderComponent = new AnimArchetypeGraftService()
                        .BaseGlideVisualComponent(transactionProject);
                     if (!GliderService.TryConfigurePairedCapeAdapter(
                             transactionProject,
                             contract,
                             nativeGliderComponent,
                             out var adapterDetail,
                             _partIndex))
                    {
                        throw new InvalidOperationException(
                            "The dynamic paired-cape adapter could not be certified after recording both parts. " +
                            adapterDetail);
                    }

                    AppendLog("Cape adapter: " + adapterDetail);
                    RecordChange(
                        "Gliders",
                        "dynamic paired-cape adapter",
                        $"preserve {transactionProject.BaseProfile?.GameplayFamily ?? "gameplay donor"} gameplay + use {UnrealPathUtil.AssetName(transactionProject.GliderAnimMas)} glide",
                        status: "staged");
                }
                else if (transactionProject.PairedCapeAdapter is not null)
                {
                    if (contract == AnimArchetypeGraftService.CapeGlideContractStatus.Unknown)
                    {
                        throw new InvalidOperationException(
                            "The active base's cape/glider contract could not be verified, so the existing paired-cape adapter was kept unchanged. " +
                            "Refresh the character assets and part index, then retry this edit.");
                    }
                    var removedAdapterComponents = RemovePairedCapeAdapterAtomically(transactionProject);
                    AppendLog(
                        "Cape adapter: removed its certified Cape + Torso pair atomically because the known base/part combination no longer satisfies the adapter" +
                        (removedAdapterComponents.Count > 0
                            ? $" ({string.Join(", ", removedAdapterComponents)})"
                            : "") + ".");
                }
            }
            var donor = System.IO.Path.GetFileNameWithoutExtension(samplePart.SourceUasset);
            RecordChange("Parts", $"{targetSlot} @ {attachSocket}", $"graft {donor} (clone {cloneSlot})");
            await RebuildGraftStageCoreAsync(
                transactionProject,
                projectRoot,
                persistProject: false);
            await RunWithFileLockRetryAsync(
                () => (_projectService ??= new SuitProjectService(projectRoot))
                    .SaveProject(transactionProject),
                "save the completed selected-part graft");
            projectSaved = true;
            await FinalizeDeclarativeGraftStageAsync(transactionProject, projectRoot);
            await DiscardBaseStageFilesystemBackupAsync(stageFilesystemSnapshot, logFailure: true);
            stageFilesystemSnapshot = null;
            RefreshToyboxTiles();
            return true;
        }
        catch (Exception ex)
        {
            Exception reportedFailure = ex;
            if (!projectSaved && stageFilesystemSnapshot is not null)
            {
                var recoveryBackupRoot = stageFilesystemSnapshot.BackupRoot;
                try
                {
                    await RestoreBaseStageFilesystemAsync(stageFilesystemSnapshot);
                    AppendLog("  restored the previous generated part stage after the failed graft.");
                    stageFilesystemSnapshot = null;
                }
                catch (Exception restoreFailure)
                {
                    reportedFailure = new AggregateException(
                        "The selected-part graft failed and its previous generated stage could not be fully restored. " +
                        $"Recovery backup: {recoveryBackupRoot}",
                        ex,
                        restoreFailure);
                }
            }
            if (!projectSaved)
            {
                if (ReferenceEquals(_currentProject, transactionProject))
                {
                    _currentProject = previousProjectSnapshot;
                    ApplyProjectToFields(_currentProject);
                    UpdateSelectedLabels();
                }
                else
                {
                    AppendLog("  the active suit changed, so its UI state was not overwritten during rollback.");
                }
            }
            AppendLog(projectSaved
                ? "Selected-part graft was saved, but its completed stage could not be certified:"
                : stageFilesystemSnapshot is null
                    ? "Selected-part graft failed; the prior saved project and generated stage were restored:"
                    : "Selected-part graft failed; packaging remains blocked because stage rollback was incomplete:");
            if (projectSaved && stageFilesystemSnapshot is not null)
            {
                AppendLog($"  recovery backup retained at {stageFilesystemSnapshot.BackupRoot}");
            }
            AppendLog(reportedFailure.ToString());
            _session.RaiseChanged();
            RefreshInspector();
            RefreshToyboxTiles();
            return false;
        }
        finally
        {
            if (gateHeld)
            {
                RebuildGate.Release();
            }
            _graftSelectedPartButton.Enabled = true;
        }
    }

    /// <summary>
    /// Records a dropped part in <c>project.PartGrafts</c> as a component INSTANCE. "Smart default"
    ///: the new part REPLACES only the existing part in the SAME occupancy group (e.g. a
    /// new hair replaces the old hair) and COEXISTS with parts in other groups (hair + cowl + cape
    /// all stay). Each instance gets a stable <c>InstanceId</c> for per-instance right-click removal.
    /// Gliders keep the legacy slot-keyed replace (a suit has one glide visual).
    /// </summary>
    private static SavedPartGraft ProspectivePartGraft(
        bool isGlider,
        NativeSuitPartRecord? playable,
        NativeSuitPartRecord? cutscene)
    {
        var sample = playable ?? cutscene;
        return new SavedPartGraft
        {
            Slot = sample?.Slot ?? (isGlider ? "Glider" : "Cape"),
            IsGlider = isGlider,
            InstanceId = "prospective-" + Guid.NewGuid().ToString("N"),
            OccupancyGroup = isGlider ? "glider.primary" : OccupancyGroupOf(sample),
            Playable = PartToDonor(playable, "playable"),
            Cutscene = PartToDonor(cutscene, "cutscene"),
        };
    }

    private static void UpsertPartGraft(
        NativeSuitProject project,
        string slot,
        bool isGlider,
        NativeSuitPartRecord? playable,
        NativeSuitPartRecord? cutscene)
    {
        if (string.IsNullOrWhiteSpace(slot))
        {
            return;
        }
        var sample = playable ?? cutscene;
        var group = isGlider
            ? "glider.primary"
            : OccupancyGroupOf(sample);

        // Replace within the same occupancy group only (glider replaces by its own group too);
        // parts in other groups are left untouched, so "add hair" no longer deletes the cowl.
        project.PartGrafts.RemoveAll(pg =>
            (string.IsNullOrWhiteSpace(pg.OccupancyGroup)
                ? OccupancyGroupOf(pg.Playable ?? pg.Cutscene)
                : pg.OccupancyGroup)
            .Equals(group, StringComparison.OrdinalIgnoreCase));

        project.PartGrafts.Add(new SavedPartGraft
        {
            Slot = slot,
            IsGlider = isGlider,
            InstanceId = Guid.NewGuid().ToString("N"),
            OccupancyGroup = group,
            Label = sample is null ? slot : CleanPartMeshDisplayName(sample),
            Playable = PartToDonor(playable, "playable"),
            Cutscene = PartToDonor(cutscene, "cutscene"),
        });
    }

    /// <summary>Backfills OccupancyGroup + InstanceId for legacy part grafts saved before the
    /// component-instance model, so old suits load with sensible replace-within-group / coexist
    /// behavior and per-instance removal. Idempotent (only fills empties).</summary>
    private static void MigratePartGraftInstances(NativeSuitProject? project)
    {
        if (project?.PartGrafts is null)
        {
            return;
        }
        foreach (var pg in project.PartGrafts)
        {
            if (string.IsNullOrWhiteSpace(pg.OccupancyGroup))
            {
                pg.OccupancyGroup = pg.IsGlider
                    ? "glider.primary"
                    : OccupancyGroupOf(pg.Playable ?? pg.Cutscene);
            }
            if (string.IsNullOrWhiteSpace(pg.InstanceId))
            {
                pg.InstanceId = Guid.NewGuid().ToString("N");
            }
        }
    }

    private static SavedPartGraftDonor? PartToDonor(NativeSuitPartRecord? part, string context)
    {
        if (part is null)
        {
            return null;
        }
        return new SavedPartGraftDonor
        {
            SourcePackagePath = part.SourcePackagePath,
            Slot = part.Slot,
            Context = string.IsNullOrWhiteSpace(part.Context) ? context : part.Context,
            MeshObjectPath = part.MeshObjectPath,
            AnimClassObjectName = part.AnimClassObjectName,
            AnimClassPackagePath = part.AnimClassPackagePath,
            AnimClassObjectPath = part.AnimClassObjectPath,
            Stem = part.Stem,
            MeshKind = part.MeshKind,
            SemanticKind = part.SemanticKind,
            TemplatePackagePath = part.TemplatePackagePath,
            TemplateUasset = part.TemplateUasset,
            TemplateSlot = part.TemplateSlot,
            TemplateComponentClass = part.TemplateComponentClass,
            ParentComponentOrVariableName = part.ParentComponentOrVariableName,
            AttachSocket = part.AttachSocket,
            Materials = part.Materials.Select(material => new NativeSuitObjectRef
            {
                ObjectName = material.ObjectName,
                PackagePath = material.PackagePath,
                ObjectPath = material.ObjectPath,
                ClassName = material.ClassName,
            }).ToList(),
            ComponentTags = part.ComponentTags.ToList(),
        };
    }

    internal static SavedPartGraftDonor? PartToDonorForTest(
        NativeSuitPartRecord? part,
        string context) => PartToDonor(part, context);

    /// <summary>
    /// Re-resolves a saved donor back to a live <see cref="NativeSuitPartRecord"/> from the loaded
    /// part index, matching on source package + mesh (the fields that uniquely identify a part on
    /// a slot). Returns null if the index can't supply it (e.g. the part index hasn't been built).
    /// </summary>
    private NativeSuitPartRecord? ResolveLivePart(SavedPartGraftDonor? donor)
    {
        if (donor is null || _partIndex is null)
        {
            return null;
        }
        var match = _partIndex.Parts.FirstOrDefault(p =>
            p.SourcePackagePath.Equals(donor.SourcePackagePath, StringComparison.OrdinalIgnoreCase) &&
            p.MeshObjectPath.Equals(donor.MeshObjectPath, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(donor.Context) || p.Context.Equals(donor.Context, StringComparison.OrdinalIgnoreCase)));

        // Catalog parts are synthesized from mesh paths, so their SourcePackagePath is
        // the mesh package rather than a character BP. Re-resolve those by exact mesh
        // identity and recover the native component recipe from the extracted BP index.
        match ??= _partIndex.Parts.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(donor.MeshObjectPath) &&
            p.MeshObjectPath.Equals(donor.MeshObjectPath, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(donor.Context) || p.Context.Equals(donor.Context, StringComparison.OrdinalIgnoreCase)));
        match ??= _partIndex.Parts.FirstOrDefault(p =>
            p.SourcePackagePath.Equals(donor.SourcePackagePath, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(donor.Context) || p.Context.Equals(donor.Context, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            return null;
        }

        var resolved = PartRecipeService.Clone(match);
        if (!string.IsNullOrWhiteSpace(donor.SemanticKind)) resolved.SemanticKind = donor.SemanticKind;
        if (!string.IsNullOrWhiteSpace(donor.MeshKind)) resolved.MeshKind = donor.MeshKind;
        if (!string.IsNullOrWhiteSpace(donor.TemplatePackagePath)) resolved.TemplatePackagePath = donor.TemplatePackagePath;
        if (!string.IsNullOrWhiteSpace(donor.TemplateUasset)) resolved.TemplateUasset = donor.TemplateUasset;
        if (!string.IsNullOrWhiteSpace(donor.TemplateSlot)) resolved.TemplateSlot = donor.TemplateSlot;
        if (!string.IsNullOrWhiteSpace(donor.TemplateComponentClass)) resolved.TemplateComponentClass = donor.TemplateComponentClass;
        if (!string.IsNullOrWhiteSpace(donor.ParentComponentOrVariableName)) resolved.ParentComponentOrVariableName = donor.ParentComponentOrVariableName;
        if (!string.IsNullOrWhiteSpace(donor.AttachSocket)) resolved.AttachSocket = donor.AttachSocket;
        if (!string.IsNullOrWhiteSpace(donor.AnimClassObjectName)) resolved.AnimClassObjectName = donor.AnimClassObjectName;
        if (!string.IsNullOrWhiteSpace(donor.AnimClassPackagePath)) resolved.AnimClassPackagePath = donor.AnimClassPackagePath;
        if (!string.IsNullOrWhiteSpace(donor.AnimClassObjectPath)) resolved.AnimClassObjectPath = donor.AnimClassObjectPath;
        if (donor.ComponentTags is { Count: > 0 }) resolved.ComponentTags = donor.ComponentTags.ToList();
        resolved.RecipeKey = PartRecipeService.BuildRecipeKey(resolved);
        return resolved;
    }

    /// <summary>
    /// Resolves only an unambiguous donor with the same saved source package, mesh, and context.
    /// Adapter certification uses this stricter path so migration can never borrow component-shell
    /// metadata from another character that happens to share a cape mesh.
    /// </summary>
    private NativeSuitPartRecord? ResolveExactLivePart(SavedPartGraftDonor? donor)
    {
        if (donor is null ||
            _partIndex is null ||
            string.IsNullOrWhiteSpace(donor.SourcePackagePath) ||
            string.IsNullOrWhiteSpace(donor.MeshObjectPath) ||
            string.IsNullOrWhiteSpace(donor.Context))
        {
            return null;
        }

        var matches = _partIndex.Parts
            .Where(part =>
                part.SourcePackagePath.Equals(donor.SourcePackagePath, StringComparison.OrdinalIgnoreCase) &&
                part.MeshObjectPath.Equals(donor.MeshObjectPath, StringComparison.OrdinalIgnoreCase) &&
                part.Context.Equals(donor.Context, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? PartRecipeService.Clone(matches[0]) : null;
    }

    private static bool IsAdapterBoundGraft(NativeSuitProject project, SavedPartGraft graft)
    {
        var adapter = project.PairedCapeAdapter;
        return adapter is not null &&
               (graft.InstanceId.Equals(
                    adapter.CosmeticCapeGraftInstanceId,
                    StringComparison.OrdinalIgnoreCase) ||
                graft.InstanceId.Equals(
                    adapter.GlideCapeGraftInstanceId,
                    StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Projects saved before complete native component recipes were persisted can recover missing
    /// metadata from the exact live-index donor. Existing donor identity is never replaced.
    /// </summary>
    private static void BackfillSavedDonorRecipe(
        SavedPartGraftDonor? donor,
        NativeSuitPartRecord? resolved)
    {
        if (donor is null || resolved is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(donor.Context)) donor.Context = resolved.Context;
        if (string.IsNullOrWhiteSpace(donor.Stem)) donor.Stem = resolved.Stem;
        if (string.IsNullOrWhiteSpace(donor.MeshKind)) donor.MeshKind = resolved.MeshKind;
        if (string.IsNullOrWhiteSpace(donor.SemanticKind)) donor.SemanticKind = resolved.SemanticKind;
        if (string.IsNullOrWhiteSpace(donor.TemplatePackagePath)) donor.TemplatePackagePath = resolved.TemplatePackagePath;
        if (string.IsNullOrWhiteSpace(donor.TemplateUasset)) donor.TemplateUasset = resolved.TemplateUasset;
        if (string.IsNullOrWhiteSpace(donor.TemplateSlot)) donor.TemplateSlot = resolved.TemplateSlot;
        if (string.IsNullOrWhiteSpace(donor.TemplateComponentClass)) donor.TemplateComponentClass = resolved.TemplateComponentClass;
        if (string.IsNullOrWhiteSpace(donor.ParentComponentOrVariableName)) donor.ParentComponentOrVariableName = resolved.ParentComponentOrVariableName;
        if (string.IsNullOrWhiteSpace(donor.AttachSocket)) donor.AttachSocket = resolved.AttachSocket;
        if (string.IsNullOrWhiteSpace(donor.AnimClassObjectName))
        {
            donor.AnimClassObjectName = resolved.AnimClassObjectName;
        }
        if (string.IsNullOrWhiteSpace(donor.AnimClassPackagePath))
        {
            donor.AnimClassPackagePath = resolved.AnimClassPackagePath;
        }
        if (string.IsNullOrWhiteSpace(donor.AnimClassObjectPath))
        {
            donor.AnimClassObjectPath = resolved.AnimClassObjectPath;
        }
        if ((donor.Materials is null || donor.Materials.Count == 0) && resolved.Materials.Count > 0)
        {
            donor.Materials = resolved.Materials.Select(material => new NativeSuitObjectRef
            {
                ObjectName = material.ObjectName,
                PackagePath = material.PackagePath,
                ObjectPath = material.ObjectPath,
                ClassName = material.ClassName,
            }).ToList();
        }
        if ((donor.ComponentTags is null || donor.ComponentTags.Count == 0) && resolved.ComponentTags.Count > 0)
        {
            donor.ComponentTags = resolved.ComponentTags.ToList();
        }
    }

    // Public entry: acquire the gate, then run the rebuild. Callers that already hold the gate
    // (UseAsBase) must call RebuildGraftStageCoreAsync directly to avoid deadlocking on this.
    private async Task RebuildGraftStageFromDeclarativeAsync(
        NativeSuitProject? projectOverride = null,
        string? projectRootOverride = null,
        bool persistProject = true)
    {
        var project = projectOverride ?? _currentProject;
        if (project is null)
        {
            return;
        }
        var projectRoot = string.IsNullOrWhiteSpace(projectRootOverride)
            ? _projectRootText.Text.Trim()
            : projectRootOverride.Trim();
        await RebuildGate.WaitAsync();
        BaseStageFilesystemSnapshot? stageFilesystemSnapshot = null;
        try
        {
            // Saved-project restore, material replay, removals, and custom meshes all enter through
            // this wrapper. Their replay can fail after the clean/grafted stages were already
            // replaced, so keep a complete payload snapshot until the new stage is certified.
            stageFilesystemSnapshot = await CaptureBaseStageFilesystemAsync(
                projectRoot,
                project.SlotId,
                new[] { "UnpatchedStage", "PatchedNameMapStage", "GraftedPartStage" });
            await RebuildGraftStageCoreAsync(project, projectRoot, persistProject);
            await DiscardBaseStageFilesystemBackupAsync(stageFilesystemSnapshot, logFailure: true);
            stageFilesystemSnapshot = null;
        }
        catch (Exception rebuildFailure)
        {
            if (stageFilesystemSnapshot is not null)
            {
                var recoveryBackupRoot = stageFilesystemSnapshot.BackupRoot;
                try
                {
                    await RestoreBaseStageFilesystemAsync(
                        stageFilesystemSnapshot,
                        discardBackup: false);

                    if (persistProject)
                    {
                        // Most callers persist declarative intent before asking for a replay. The
                        // restored payload may therefore represent the prior intent, so retain the
                        // fail-closed sentinel until a complete retry certifies the saved project.
                        await MarkDeclarativeStageIncompleteAsync(project, projectRoot);
                        AppendLog("  restored the previous generated payload after the failed replay; packaging remains blocked until the saved edits replay successfully.");
                    }
                    else
                    {
                        // Transactional callers using persistProject:false keep the prior project
                        // JSON too, so the snapshot's original marker state is authoritative.
                        AppendLog("  restored the previous generated payload and project after the failed replay.");
                    }
                    await DiscardBaseStageFilesystemBackupAsync(stageFilesystemSnapshot, logFailure: true);
                    stageFilesystemSnapshot = null;
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException(
                        "The declarative replay failed and its previous generated payload could not be fully restored. " +
                        $"Recovery backup: {recoveryBackupRoot}",
                        rebuildFailure,
                        restoreFailure);
                }
            }
            throw;
        }
        finally
        {
            RebuildGate.Release();
        }
    }

    // The actual rebuild work. MUST be called while holding RebuildGate (via the public wrapper
    // above, or by UseAsBase which holds it across its whole staging pass).
    private async Task RebuildGraftStageCoreAsync(
        NativeSuitProject? projectOverride = null,
        string? projectRootOverride = null,
        bool persistProject = true)
    {
        var project = projectOverride ?? _currentProject;
        if (project is null)
        {
            return;
        }
        var projectRoot = string.IsNullOrWhiteSpace(projectRootOverride)
            ? _projectRootText.Text.Trim()
            : projectRootOverride;

        // Delete the grafted stage so the graft service re-copies it fresh from the clean
        // PatchedNameMapStage (its first graft call copies patched→grafted only when absent).
        var graftStage = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "NativeSuitGuiProjects",
            project.SlotId, "GraftedPartStage");
        var projectStageRoot = Directory.GetParent(graftStage)?.FullName
            ?? throw new InvalidOperationException("Could not resolve the generated suit stage root.");
        var incompleteMarker = Path.Combine(projectStageRoot, IncompleteDeclarativeStageMarkerName);

        // Mark the transaction incomplete before any awaited index work. Some callers have already
        // saved new declarative intent, so an older completed stage must never remain packageable
        // against newer JSON if refreshing the cache fails.
        try
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    Directory.CreateDirectory(projectStageRoot);
                    File.WriteAllText(
                        incompleteMarker,
                        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                },
                "mark the declarative stage rebuild incomplete");
        }
        catch (Exception ex)
        {
            if (FileLockUtil.IsTransient(ex))
            {
                throw new TransientFileLockException(
                    "Could not mark the declarative stage rebuild as incomplete because its transaction file stayed locked. " +
                    "Close any viewer opened on this suit and retry.",
                    ex);
            }
            throw new InvalidOperationException(
                "Could not establish the declarative-stage transaction guard before rebuilding it.", ex);
        }

        // Adapter migration and visual-overlay replay both require exact live recipes. Load the
        // index before migrating an older certificate so a saved schema-2 project cannot silently
        // fall back to its Batman scaffold when its Nightwing overlay is unavailable.
        if ((project.PairedCapeAdapter is not null ||
             project.PartGrafts.Any(graft => graft.Playable is not null || graft.Cutscene is not null)) &&
            !await EnsurePartIndexAsync(projectRoot))
        {
            throw new InvalidOperationException(
                "The native part index could not be loaded or rebuilt from the extracted character assets. " +
                "Refresh game assets, then rebuild the part index and retry. The previous generated payload was left in place, " +
                "but packaging remains blocked until the saved edits can be replayed.");
        }

        if (project.PairedCapeAdapter is not null &&
            project.PairedCapeAdapter.SchemaVersion != GliderService.PairedCapeAdapterSchemaVersion)
        {
            // Migrate a disposable model copy. TryConfigure intentionally clears a rejected
            // certificate, and donor hydration mutates saved records; doing either on the live
            // project before all checks pass could leave an orphan Cape/Torso pair after failure.
            var migrationProject = JsonSerializer.Deserialize<NativeSuitProject>(
                JsonSerializer.Serialize(project))
                ?? throw new InvalidOperationException(
                    "This suit's retired paired-cape adapter could not be copied for an atomic migration.");
            migrationProject.AllowSyntheticPairedCapeVisualOverlayFixture =
                project.AllowSyntheticPairedCapeVisualOverlayFixture;
            var retiredAdapter = migrationProject.PairedCapeAdapter
                ?? throw new InvalidOperationException("The retired paired-cape adapter disappeared from its migration snapshot.");
            var boundGrafts = migrationProject.PartGrafts.Where(graft =>
                    graft.InstanceId.Equals(retiredAdapter.CosmeticCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase) ||
                    graft.InstanceId.Equals(retiredAdapter.GlideCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var exactHydration = boundGrafts.Select(graft => new
            {
                Graft = graft,
                Playable = ResolveExactLivePart(graft.Playable),
                Cutscene = ResolveExactLivePart(graft.Cutscene),
            }).ToList();
            if (boundGrafts.Count != 2 || exactHydration.Any(item =>
                    item.Graft.Playable is null || item.Graft.Cutscene is null ||
                    item.Playable is null || item.Cutscene is null))
            {
                throw new InvalidOperationException(
                    "This suit's retired paired-cape adapter could not resolve both exact playable/cutscene donor recipes. " +
                    "Refresh the part index, or remove and re-apply its regular cape and glide cape before packaging.");
            }

            // Do not partially mutate a legacy donor record: resolve all four exact recipes first,
            // then hydrate missing schema-2 metadata (notably Materials) as one in-memory unit.
            foreach (var item in exactHydration)
            {
                BackfillSavedDonorRecipe(item.Graft.Playable, item.Playable);
                BackfillSavedDonorRecipe(item.Graft.Cutscene, item.Cutscene);
            }

            var contract = new AnimArchetypeGraftService().BaseCapeGlideContract(migrationProject);
            var nativeGlider = new AnimArchetypeGraftService().BaseGlideVisualComponent(migrationProject);
            if (!GliderService.TryConfigurePairedCapeAdapter(
                     migrationProject,
                     contract,
                     nativeGlider,
                     out var migrationDetail,
                     _partIndex))
            {
                throw new InvalidOperationException(
                    "This suit contains a retired paired-cape adapter certificate that could not be migrated to the " +
                    "component-compatible visual overlay. Remove and re-apply its regular cape and glide cape before packaging. " +
                     migrationDetail);
            }
            project.PartGrafts = migrationProject.PartGrafts;
            project.PairedCapeAdapter = migrationProject.PairedCapeAdapter;
            project.GliderAnimLas = migrationProject.GliderAnimLas;
            project.GliderAnimMas = migrationProject.GliderAnimMas;
            project.UseCustomArchetype = migrationProject.UseCustomArchetype;
            project.GliderAutoEnabledCustomArchetype = migrationProject.GliderAutoEnabledCustomArchetype;
            AppendLog("  migrated retired paired-cape layout: " + migrationDetail);
        }

        // Resolve every donor before deleting the last generated payload. This catches a genuinely
        // missing legacy donor while the old files are still available for recovery; the root-level
        // incomplete marker above keeps those files safely non-packageable.
        var replayParts = new List<(
            SavedPartGraft Graft,
            bool AdapterBound,
            bool AutomaticVisualOverlay,
            NativeSuitPartRecord? Playable,
            NativeSuitPartRecord? Cutscene)>();
        var visualOverlayGrafts = project.PairedCapeAdapter?.VisualOverlay?.ComponentGrafts ?? [];
        var allReplayGrafts = visualOverlayGrafts.Concat(project.PartGrafts).ToList();
        foreach (var graft in allReplayGrafts)
        {
            var automaticVisualOverlay = visualOverlayGrafts.Contains(graft);
            var adapterBound = automaticVisualOverlay || IsAdapterBoundGraft(project, graft);
            var playable = adapterBound
                ? ResolveExactLivePart(graft.Playable)
                : ResolveLivePart(graft.Playable);
            var cutscene = adapterBound
                ? ResolveExactLivePart(graft.Cutscene)
                : ResolveLivePart(graft.Cutscene);
            BackfillSavedDonorRecipe(graft.Playable, playable);
            BackfillSavedDonorRecipe(graft.Cutscene, cutscene);
            var missingPlayable = graft.Playable is not null && playable is null;
            var missingCutscene = graft.Cutscene is not null && cutscene is null;
            if ((graft.Playable is null && graft.Cutscene is null) || missingPlayable || missingCutscene)
            {
                var missing = string.Join(" and ", new[]
                {
                    missingPlayable ? "playable donor" : "",
                    missingCutscene ? "cutscene donor" : "",
                    graft.Playable is null && graft.Cutscene is null ? "saved donor record" : "",
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                throw new InvalidOperationException(
                    $"Part '{graft.Label}' (slot {graft.Slot}) could not resolve its {missing} from the part index. " +
                    (adapterBound
                        ? "Its paired-cape adapter requires the exact saved source, mesh, and role; refresh the part index or re-apply the cape pair. "
                        : "Rebuild the part index and retry; ") +
                    "the previous generated payload was retained and packaging is blocked until the declarative stage is complete.");
            }

            replayParts.Add((graft, adapterBound, automaticVisualOverlay, playable, cutscene));
        }

        await EnsurePatchedBaseStageCurrentAsync(project, projectRoot);

        try
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    // A rebuild is packageable only after every declarative operation succeeds.
                    // Removing the stage marker before the tree prevents an older good stage from
                    // masking a newer partial replay.
                    DeleteCompletedGraftStageMarkerIfPresent(graftStage);
                    if (Directory.Exists(graftStage))
                    {
                        Directory.Delete(graftStage, recursive: true);
                    }
                    return true;
                },
                "clear generated graft stage");
        }
        catch (Exception ex)
        {
            if (FileLockUtil.IsTransient(ex))
            {
                throw new TransientFileLockException(
                    "Could not clear the previous generated stage because one of its files stayed locked. " +
                    "Close any FModel/UAsset viewer opened on this suit and retry.",
                    ex);
            }
            throw new InvalidOperationException(
                "Could not clear the previous generated stage before rebuilding it.", ex);
        }

        // Removals and material assignments are just as declarative as part/custom-mesh grafts.
        // Always replay them in their own generated stage, seeded from the clean name-map output,
        // so failures cannot mutate PatchedNameMapStage or silently fall back to a base-only build.
        if (ProjectRequiresCompletedGraftStage(project))
        {
            var patchedContentRoot = Path.Combine(
                AppSettings.GeneratedRootFor(projectRoot),
                "NativeSuitGuiProjects",
                project.SlotId,
                "PatchedNameMapStage",
                "LEGOBatmanLotDK",
                "Content");
            var graftedContentRoot = Path.Combine(graftStage, "LEGOBatmanLotDK", "Content");
            if (!Directory.Exists(patchedContentRoot))
            {
                throw new DirectoryNotFoundException(
                    "The clean PatchedNameMapStage is missing. Set the base again before replaying saved edits. " +
                    patchedContentRoot);
            }

            await RunWithFileLockRetryAsync(
                () =>
                {
                    Directory.CreateDirectory(graftedContentRoot);
                    CopyDirectoryContents(patchedContentRoot, graftedContentRoot, overwrite: true);
                    return true;
                },
                "seed the declarative stage from the clean patched base");

            if (project.BodyProfile is not null)
            {
                var bodyResult = new NativeBodyProfileService().ApplyToContentRoot(
                    graftedContentRoot,
                    project,
                    UiMappings());
                foreach (var file in bodyResult.Files)
                {
                    AppendLog($"  native body {file.Role}: {(file.Success ? "OK" : "FAILED")} — {file.Detail}");
                }
                if (!bodyResult.Success)
                {
                    throw new InvalidOperationException(
                        $"Native body profile '{project.BodyProfile.DisplayName}' could not be written to both required character packages. " +
                        string.Join(" | ", bodyResult.Files.Where(file => !file.Success).Select(file => $"{file.Role}: {file.Detail}")));
                }
            }
        }

        AppendLog($"  replaying {replayParts.Count} declared part(s) onto a clean base…");
        var pairedCapeVisualMaterialsApplied = false;
        foreach (var replay in replayParts)
        {
            if (!replay.AutomaticVisualOverlay && !pairedCapeVisualMaterialsApplied)
            {
                // The visual overlay is the authored-scaffold baseline, not a final user edit.
                // Apply its identity materials immediately after its automatic Head/Face grafts
                // and before every ordinary part graft. A later user Face/Body graft must keep its
                // own donor material until an explicit saved material assignment says otherwise.
                var adapterVisualMaterials = ApplyPairedCapeVisualOverlayMaterials(project, projectRoot);
                RequireCompleteDeclarativeReplay(adapterVisualMaterials, "Paired-cape visual-base material replay");
                pairedCapeVisualMaterialsApplied = true;
            }
            var pg = replay.Graft;
            var playable = replay.Playable;
            var cutscene = replay.Cutscene;
            var sample = playable ?? cutscene!;
            var cloneSlot = GuessCloneSlot(sample);
            var attachSocket = GuessAttachSocket(sample);
            try
            {
                var result = await RunWithFileLockRetryAsync(
                    () =>
                    {
                        var graft = new PartGraftService(projectRoot).CreateSelectedPartGraftedStage(
                            project,
                            playable,
                            cutscene,
                            pg.Slot,
                            cloneSlot,
                            attachSocket,
                            pg.PreferDonorComponentShell,
                            restoreExistingFieldRecipe: replay.AutomaticVisualOverlay);
                        var locked = graft.PackageResults.FirstOrDefault(package => package.TransientFileLock);
                        if (locked is not null)
                        {
                            throw new TransientFileLockException(
                                locked.Error ?? "A generated character package is temporarily locked.");
                        }
                        return graft;
                    },
                    $"replay part '{pg.Label}'");
                foreach (var package in result.PackageResults)
                {
                    AppendLog($"  {pg.Slot}/{package.Role}: slot={package.TargetSlot} success={package.Success} addedExports={package.AddedExports} componentExport={package.NewComponentExportIndex}");
                    if (!package.Success && !string.IsNullOrWhiteSpace(package.Error))
                    {
                        AppendLog($"    {package.Error}");
                    }
                }
                var failedPackages = result.PackageResults.Where(package => !package.Success).ToList();
                if (failedPackages.Count > 0)
                {
                    var failureSummary = string.Join(" | ", failedPackages.Select(package =>
                        $"{package.Role}: {(!string.IsNullOrWhiteSpace(package.Error) ? package.Error : "unknown graft error")}"));
                    throw new InvalidOperationException(
                        $"Part '{pg.Label}' could not be replayed for every required character package. " +
                        $"The rebuild stopped before producing a partial suit. {failureSummary}");
                }
                if (result.PackageResults.Any(p => p.Success))
                {
                    // Record the ACTUAL resolved component name (e.g. "Head_2") so the remove
                    // button can map a removed component precisely back to this graft entry.
                    var resolved = result.PackageResults
                        .Where(p => p.Success && !string.IsNullOrWhiteSpace(p.TargetSlot))
                        .Select(p => p.TargetSlot)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        pg.ResolvedComponent = resolved;
                    }
                    // Don't let a saved removal strip the component we just (re)grafted.
                    foreach (var graftedSlot in result.PackageResults
                                 .Where(p => !replay.AutomaticVisualOverlay &&
                                             p.Success &&
                                             !string.IsNullOrWhiteSpace(p.TargetSlot))
                                 .Select(p => p.TargetSlot)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        RemoveSavedRemovalForComponent(project, graftedSlot);
                    }
                    if (!replay.AutomaticVisualOverlay && EnsureCrossKindHeadGraftHidesBaseHead(project))
                    {
                        AppendLog("  cross-kind head graft replaces the donor cowl; queued Head:0 for removal.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  ERROR replay of part '{pg.Label}' (slot {pg.Slot}) failed: {ex.Message}");
                var detail = FileLockUtil.IsTransient(ex)
                    ? $"Part '{pg.Label}' could not be replayed because its generated asset stayed locked. " +
                      "Close FModel or any asset viewer using this suit, then retry."
                    : $"Part '{pg.Label}' could not be replayed for the playable and cutscene packages.";
                if (FileLockUtil.IsTransient(ex))
                {
                    throw new TransientFileLockException(
                        detail + " The rebuild stopped before producing a partial suit.", ex);
                }
                throw new InvalidOperationException(
                    detail + " The rebuild stopped before producing a partial suit.", ex);
            }
        }

        if (!pairedCapeVisualMaterialsApplied)
        {
            var adapterVisualMaterials = ApplyPairedCapeVisualOverlayMaterials(project, projectRoot);
            RequireCompleteDeclarativeReplay(adapterVisualMaterials, "Paired-cape visual-base material replay");
        }

        GliderService.RefreshPairedCapeAdapterResolvedComponents(project);

        SyncCustomStaticMeshHeadRemoval(project);
        await StageCustomStaticMeshesAsync(project);

        // Re-apply the rest of the suit's declarative edits onto the freshly grafted stage.
        var removalReplay = ApplySavedComponentRemovals(project, logNoRemovals: false);
        RequireCompleteDeclarativeReplay(removalReplay, "Saved component removal replay");
        var materialReplay = ApplySavedMaterials(project, logIfNone: false);
        RequireCompleteDeclarativeReplay(materialReplay, "Saved material replay");
        if (persistProject)
        {
            await RunWithFileLockRetryAsync(
                () => new SuitProjectService(projectRoot).SaveProject(project),
                "save the completed declarative stage");
            await FinalizeDeclarativeGraftStageAsync(project, projectRoot);
        }
        _session.RaiseChanged();
    }

    private async Task EnsurePatchedBaseStageCurrentAsync(
        NativeSuitProject project,
        string projectRoot)
    {
        var patchService = new UAssetPatchService(projectRoot);
        if (patchService.IsPatchedStageCurrent(project))
        {
            return;
        }

        var snapshot = await CaptureBaseStageFilesystemAsync(
            projectRoot,
            project.SlotId,
            new[] { "UnpatchedStage", "PatchedNameMapStage" });
        try
        {
            await ClearBaseStageFilesystemAsync(snapshot);
            var result = await RunWithFileLockRetryAsync(
                () => patchService.CreateNameMapPatchedStage(project),
                "rebuild the clean authored base stage");
            var failed = result.PackageResults.Where(package => !package.Success).ToList();
            if (result.PackageResults.Count == 0 || failed.Count > 0 || !patchService.IsPatchedStageCurrent(project))
            {
                var detail = failed.Count == 0
                    ? "no complete character-package patch was produced"
                    : string.Join(" | ", failed.Select(package =>
                        $"{package.Role}: {package.Error ?? "unknown patch failure"}"));
                throw new InvalidOperationException(
                    "The clean base stage could not be rebuilt for the current component shell: " + detail);
            }

            AppendLog("  refreshed the clean base stage for the current playable/cutscene component shell.");
            await DiscardBaseStageFilesystemBackupAsync(snapshot, logFailure: true);
        }
        catch (Exception stageFailure)
        {
            try
            {
                await RestoreBaseStageFilesystemAsync(snapshot);
            }
            catch (Exception restoreFailure)
            {
                throw new AggregateException(
                    $"The clean base-stage refresh failed and its previous stages could not be fully restored. Recovery backup: {snapshot.BackupRoot}",
                    stageFailure,
                    restoreFailure);
            }
            throw;
        }
    }

    /// <summary>
    /// Clears the certification marker only when its stage directory exists. A suit's first
    /// declarative rebuild has no GraftedPartStage yet; calling File.Delete beneath that missing
    /// parent throws DirectoryNotFoundException on Windows and incorrectly blocks the fresh stage.
    /// </summary>
    internal static bool DeleteCompletedGraftStageMarkerIfPresent(string graftStage)
    {
        if (!Directory.Exists(graftStage))
        {
            return false;
        }

        var marker = Path.Combine(graftStage, CompletedGraftStageMarkerName);
        if (!File.Exists(marker))
        {
            return false;
        }

        File.Delete(marker);
        return true;
    }

    internal static bool DeleteGeneratedStageDirectoryIfPresent(string stagePath)
    {
        if (!Directory.Exists(stagePath))
        {
            return false;
        }

        DeleteCompletedGraftStageMarkerIfPresent(stagePath);
        Directory.Delete(stagePath, recursive: true);
        return true;
    }

    private async Task FinalizeDeclarativeGraftStageAsync(
        NativeSuitProject project,
        string projectRoot)
    {
        var projectStageRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            project.SlotId);
        var graftStage = Path.Combine(projectStageRoot, "GraftedPartStage");
        var incompleteMarker = Path.Combine(projectStageRoot, IncompleteDeclarativeStageMarkerName);

        if (Directory.Exists(graftStage))
        {
            await RunWithFileLockRetryAsync(
                () =>
                {
                    File.WriteAllText(
                        Path.Combine(graftStage, CompletedGraftStageMarkerName),
                        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                },
                "mark generated graft stage complete");
        }

        // Delete the fail-closed sentinel last. If this step is interrupted, packaging remains
        // blocked even when a completion marker was already written.
        await RunWithFileLockRetryAsync(
            () =>
            {
                File.Delete(incompleteMarker);
                return true;
            },
            "certify the completed declarative stage");
    }

    private async Task MarkDeclarativeStageIncompleteAsync(
        NativeSuitProject project,
        string projectRoot)
    {
        var projectStageRoot = Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            "NativeSuitGuiProjects",
            project.SlotId);
        var incompleteMarker = Path.Combine(projectStageRoot, IncompleteDeclarativeStageMarkerName);
        await RunWithFileLockRetryAsync(
            () =>
            {
                Directory.CreateDirectory(projectStageRoot);
                File.WriteAllText(
                    incompleteMarker,
                    DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            },
            "mark the generated suit stages incomplete");
    }

    private void UpdateSelectedPartLabels()
    {
        _selectedPlayablePartLabel.Text = $"Playable part donor: {(_selectedPlayablePart is null ? "<none>" : DescribePart(_selectedPlayablePart))}";
        _selectedCutscenePartLabel.Text = $"Cutscene part donor: {(_selectedCutscenePart is null ? "<none>" : DescribePart(_selectedCutscenePart))}";
    }

    private static string DescribePart(NativeSuitPartRecord part)
    {
        return $"{part.Context}/{part.Slot} {part.CharacterFolder} :: {part.MeshObjectName} from {part.SourcePackagePath}";
    }

    /// <summary>True for the shared-minifig CORE components every character needs (body/head/face/
    /// limbs/root) - these must never be auto-hidden when reskinning to a base that "lacks" them,
    /// since the identity is applied via material, not a separate part. Cosmetic attachments (Cape,
    /// Hat, Wings, Backpack, …) are NOT core, so they can be hidden when the villain lacks them.</summary>
    private static bool IsCoreKeepComponent(string comp, string kind)
    {
        string[] coreKinds =
        {
            "CharacterMesh0", "Mesh", "Torso", "Head", "Face", "Legs", "Leg", "Arms", "Arm",
            "Hips", "Hip", "Hands", "Hand", "Body", "DefaultSceneRoot", "Root", "Scene", "LEGOface",
        };
        return coreKinds.Any(c =>
                   kind.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                   comp.Equals(c, StringComparison.OrdinalIgnoreCase))
               || comp.Contains("Minifig", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransplantableVillainPart(NativeSuitPartRecord p)
    {
        if (p is null || !p.HasMesh)
        {
            return false;
        }
        var slot = p.Slot ?? "";
        var mesh = p.MeshObjectName ?? "";
        if (slot.Equals("Face", StringComparison.OrdinalIgnoreCase) ||
            slot.Equals("Mesh", StringComparison.OrdinalIgnoreCase) ||
            slot.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // The shared minifig body/head/legs + LEGO face are identity via material, not a part.
        return !mesh.Contains("LEGOface", StringComparison.OrdinalIgnoreCase)
            && !mesh.Contains("Minifig_Body", StringComparison.OrdinalIgnoreCase)
            && !mesh.Contains("Minifig_Legs", StringComparison.OrdinalIgnoreCase)
            && !mesh.Contains("Minifig_Head", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessCloneSlot(NativeSuitPartRecord part)
    {
        var kind = string.IsNullOrWhiteSpace(part.SemanticKind)
            ? PartRecipeService.SemanticKind(part)
            : part.SemanticKind;
        return kind switch
        {
            "Hair" => part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ? "Head" : "Hair",
            "Hat" => part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ? "Head" : "Hat",
            "Head" => "Head",
            "Face" => "Face",
            "Cape" => "Cape",
            "Torso" => "Torso",
            "Torso2" => "Torso2",
            "Hip" => "Hip",
            "Collar" => "Collar",
            "Spine" => "Spine",
            "Costume" => "Costume",
            "CustomHead" => "CustomHead",
            _ => part.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ? "Head" : "Face"
        };
    }
}
