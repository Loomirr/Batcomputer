using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Selects a real, cooked Cape + Torso Blueprint scaffold whose existing visual fields can host
/// the selected base's Head/Face recipes without adding or changing reflected component fields.
/// It also creates the exact declarative overlay that is replayed whenever the suit is rebuilt.
/// </summary>
internal static class PairedCapeVisualOverlayService
{
    internal const int OverlaySchemaVersion = 1;
    private static readonly string[] OverlaySlots = ["Face", "Head"];

    internal sealed record Selection(
        string ShellPlayablePackage,
        string ShellCutscenePackage,
        PairedCapeVisualOverlayProfile? Overlay,
        string Detail);

    internal sealed record IdentityMaterials(
        string PlayableBody,
        string CutsceneBody,
        string PlayableFace,
        string CutsceneFace);

    internal static bool TrySelect(
        NativeSuitProject project,
        NativeSuitPartIndex? partIndex,
        string preferredShellPlayable,
        string preferredShellCutscene,
        SavedPartGraft cosmeticCape,
        SavedPartGraft glideCape,
        out Selection selection,
        out string detail)
    {
        selection = null!;
        detail = "";

        // Only the explicit non-serialized release-regression fixture may omit a visual overlay.
        // A real saved project with missing identities must fail closed instead of inheriting the
        // Batman scaffold merely because an older JSON record is incomplete.
        if (project.AllowSyntheticPairedCapeVisualOverlayFixture)
        {
            selection = new Selection(
                UnrealPathUtil.NormalizePackagePath(preferredShellPlayable),
                UnrealPathUtil.NormalizePackagePath(preferredShellCutscene),
                null,
                "No concrete visual base was supplied; retained the exact selected Cape + Torso shell.");
            return true;
        }
        if (!HasConcreteVisualBase(project))
        {
            detail =
                "The saved project does not identify both its visual base and gameplay donor. Re-select the base before applying the paired-cape adapter.";
            return false;
        }

        partIndex ??= TryLoadActivePartIndex();
        if (partIndex?.Parts is not { Count: > 0 })
        {
            detail = "The active native part index is required to select a component-compatible Cape + Torso scaffold. Refresh the part index and re-apply the cape pair.";
            return false;
        }

        if (!TryResolveVisualPair(project, partIndex, out var visualPlayable, out var visualCutscene, out detail))
        {
            return false;
        }

        var overlayGrafts = new List<SavedPartGraft>();
        foreach (var slot in OverlaySlots)
        {
            var playableParts = ExactParts(partIndex, visualPlayable, "playable", slot);
            var cutsceneParts = ExactParts(partIndex, visualCutscene, "cutscene", slot);
            if (playableParts.Count != 1 || cutsceneParts.Count != 1)
            {
                detail =
                    $"The selected visual base does not expose one unambiguous {slot} recipe for both playable and cutscene " +
                    $"(found {playableParts.Count} playable and {cutsceneParts.Count} cutscene). The adapter will not substitute a Batman field or synthesize a new component.";
                return false;
            }

            var playablePart = playableParts[0];
            var cutscenePart = cutsceneParts[0];
            if (!HasExistingFieldRecipe(playablePart) || !HasExistingFieldRecipe(cutscenePart))
            {
                detail = $"The selected visual base's {slot} recipe is incomplete. Refresh the part index and re-apply the cape pair.";
                return false;
            }

            overlayGrafts.Add(new SavedPartGraft
            {
                Slot = slot,
                Label = $"paired-cape visual base {slot}",
                IsGlider = false,
                InstanceId = "paired-cape-overlay-" + slot.ToLowerInvariant(),
                OccupancyGroup = "paired-cape.visual." + slot.ToLowerInvariant(),
                PreferDonorComponentShell = false,
                Playable = ToSavedDonor(playablePart, "playable"),
                Cutscene = ToSavedDonor(cutscenePart, "cutscene"),
            });
        }

        var candidates = OrderedCompatibleScaffolds(
                partIndex,
                overlayGrafts,
                cosmeticCape,
                glideCape,
                preferredShellPlayable,
                preferredShellCutscene)
            .ToList();
        if (candidates.Count == 0)
        {
            detail =
                "No authored Cape + Torso Blueprint pair has existing Head/Face fields compatible with the selected visual base. " +
                "Batcomputer refused to append or change a cooked component field because that layout can crash while loading.";
            return false;
        }

        var chosen = candidates[0];
        if (!TryExtractIdentityMaterials(
                visualPlayable,
                visualCutscene,
                overlayGrafts,
                out var playableBody,
                out var cutsceneBody,
                out var playableFace,
                out var cutsceneFace,
                out detail))
        {
            return false;
        }

        var overlay = new PairedCapeVisualOverlayProfile
        {
            SchemaVersion = OverlaySchemaVersion,
            VisualPlayableSourcePackage = visualPlayable,
            VisualCutsceneSourcePackage = visualCutscene,
            ComponentGrafts = overlayGrafts,
            PlayableBodyMaterialPackage = playableBody,
            CutsceneBodyMaterialPackage = cutsceneBody,
            PlayableFaceMaterialPackage = playableFace,
            CutsceneFaceMaterialPackage = cutsceneFace,
        };
        var scaffoldChanged = !SamePackage(chosen.PlayablePackage, preferredShellPlayable) ||
                              !SamePackage(chosen.CutscenePackage, preferredShellCutscene);
        detail = scaffoldChanged
            ? $"Selected compatible authored scaffold {UnrealPathUtil.AssetName(chosen.PlayablePackage)} and certified the visual base's existing Head/Face fields plus role-specific body/face materials."
            : $"The selected Cape + Torso donor is already component-compatible; certified its Head/Face visual-base overlay and role-specific materials.";
        selection = new Selection(chosen.PlayablePackage, chosen.CutscenePackage, overlay, detail);
        return true;
    }

    internal static bool ValidateDeclaration(
        NativeSuitProject project,
        NativeSuitPartIndex? partIndex,
        PairedCapeAdapterProfile adapter,
        out string detail,
        IdentityMaterials? knownSourceMaterials = null)
    {
        detail = "";
        if (project.AllowSyntheticPairedCapeVisualOverlayFixture)
        {
            detail = "Synthetic adapter fixture has no visual-base overlay.";
            return adapter.VisualOverlay is null;
        }
        if (!HasConcreteVisualBase(project))
        {
            detail =
                "The paired-cape certificate cannot be validated because the saved visual-base or gameplay-donor package identity is missing. Re-select the base.";
            return false;
        }

        var overlay = adapter.VisualOverlay;
        if (overlay is null || overlay.SchemaVersion != OverlaySchemaVersion)
        {
            detail = "The paired-cape visual-base overlay is missing or uses a retired schema. Rebuild the adapter.";
            return false;
        }
        partIndex ??= TryLoadActivePartIndex();
        if (partIndex?.Parts is null ||
            !TryResolveVisualPair(project, partIndex, out var visualPlayable, out var visualCutscene, out detail))
        {
            detail = string.IsNullOrWhiteSpace(detail)
                ? "The paired-cape visual-base overlay could not be checked against the active part index."
                : detail;
            return false;
        }
        if (!SamePackage(overlay.VisualPlayableSourcePackage, visualPlayable) ||
            !SamePackage(overlay.VisualCutsceneSourcePackage, visualCutscene))
        {
            detail = "The selected visual base changed after the paired-cape overlay was certified.";
            return false;
        }
        var overlayGrafts = overlay.ComponentGrafts ?? [];
        if (overlayGrafts.Count != OverlaySlots.Length ||
            overlayGrafts.Any(graft =>
                graft is null ||
                !OverlaySlots.Contains(graft.Slot ?? "", StringComparer.OrdinalIgnoreCase) ||
                graft.IsGlider ||
                graft.PreferDonorComponentShell) ||
            overlayGrafts.Select(graft => graft.Slot ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() != OverlaySlots.Length)
        {
            detail = "The paired-cape overlay must contain exactly the existing Head and Face fields, with no synthetic component-shell request.";
            return false;
        }

        foreach (var graft in overlayGrafts)
        {
            if (!SavedDonorMatchesLive(partIndex, graft.Playable, visualPlayable, "playable", graft.Slot) ||
                !SavedDonorMatchesLive(partIndex, graft.Cutscene, visualCutscene, "cutscene", graft.Slot))
            {
                detail = $"The paired-cape overlay's saved {graft.Slot} recipe no longer matches the active visual source and part index.";
                return false;
            }
        }

        var partGrafts = project.PartGrafts ?? [];
        var cosmeticCape = partGrafts.FirstOrDefault(graft =>
            string.Equals(graft?.InstanceId, adapter.CosmeticCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase));
        var glideCape = partGrafts.FirstOrDefault(graft =>
            string.Equals(graft?.InstanceId, adapter.GlideCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase));
        if (cosmeticCape is null || glideCape is null)
        {
            detail = "The paired-cape overlay could not resolve its bound Cape + Torso grafts.";
            return false;
        }
        var compatible = FindCompatibleScaffolds(partIndex, overlayGrafts, cosmeticCape, glideCape).Any(candidate =>
            SamePackage(candidate.PlayablePackage, adapter.AuthoredShellPlayablePackage) &&
            SamePackage(candidate.CutscenePackage, adapter.AuthoredShellCutscenePackage));
        if (!compatible)
        {
            detail = "The certified authored scaffold no longer has component classes compatible with the visual base's existing Head/Face fields.";
            return false;
        }

        if (!ValidGamePackage(overlay.PlayableBodyMaterialPackage) ||
            !ValidGamePackage(overlay.CutsceneBodyMaterialPackage) ||
            !ValidGamePackage(overlay.PlayableFaceMaterialPackage) ||
            !ValidGamePackage(overlay.CutsceneFaceMaterialPackage))
        {
            detail = "The paired-cape visual overlay is missing a role-specific CharacterMesh0 or Face material.";
            return false;
        }

        if (knownSourceMaterials is null)
        {
            if (!TryExtractIdentityMaterials(
                    visualPlayable,
                    visualCutscene,
                    overlayGrafts,
                    out var playableBody,
                    out var cutsceneBody,
                    out var playableFace,
                    out var cutsceneFace,
                    out detail))
            {
                return false;
            }
            knownSourceMaterials = new IdentityMaterials(
                playableBody,
                cutsceneBody,
                playableFace,
                cutsceneFace);
        }
        if (!SamePackage(overlay.PlayableBodyMaterialPackage, knownSourceMaterials.PlayableBody) ||
            !SamePackage(overlay.CutsceneBodyMaterialPackage, knownSourceMaterials.CutsceneBody) ||
            !SamePackage(overlay.PlayableFaceMaterialPackage, knownSourceMaterials.PlayableFace) ||
            !SamePackage(overlay.CutsceneFaceMaterialPackage, knownSourceMaterials.CutsceneFace))
        {
            detail =
                "The paired-cape identity-material certificate no longer matches the selected visual base's exact playable/cutscene CharacterMesh0 and Face assignments. Re-apply the cape pair.";
            return false;
        }

        detail = "The authored scaffold, visual Head/Face recipes, and role-specific identity materials match the adapter certificate.";
        return true;
    }

    internal static NativeSuitPartRecord? ResolveExactLivePart(
        NativeSuitPartIndex? partIndex,
        SavedPartGraftDonor? donor)
    {
        if (partIndex is null || donor is null ||
            string.IsNullOrWhiteSpace(donor.SourcePackagePath) ||
            string.IsNullOrWhiteSpace(donor.MeshObjectPath) ||
            string.IsNullOrWhiteSpace(donor.Context))
        {
            return null;
        }
        var matches = (partIndex.Parts ?? []).Where(part =>
                part is not null &&
                SamePackage(part.SourcePackagePath, donor.SourcePackagePath) &&
                string.Equals(part.MeshObjectPath, donor.MeshObjectPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(part.Context, donor.Context, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(part.Slot, donor.Slot, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? PartRecipeService.Clone(matches[0]) : null;
    }

    private sealed record Scaffold(string PlayablePackage, string CutscenePackage);

    private static IEnumerable<Scaffold> OrderedCompatibleScaffolds(
        NativeSuitPartIndex index,
        IReadOnlyList<SavedPartGraft> overlayGrafts,
        SavedPartGraft cosmeticCape,
        SavedPartGraft glideCape,
        string preferredShellPlayable,
        string preferredShellCutscene) =>
        FindCompatibleScaffolds(index, overlayGrafts, cosmeticCape, glideCape)
            .OrderBy(candidate =>
                SamePackage(candidate.PlayablePackage, preferredShellPlayable) &&
                SamePackage(candidate.CutscenePackage, preferredShellCutscene) ? 0 : 1)
            .ThenBy(candidate =>
                candidate.PlayablePackage.Contains("/Characters/Minifig/Batman/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => candidate.PlayablePackage, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Exercises the production compatibility/ordering algorithm with an in-memory part index.
    /// It deliberately stops before reading extracted UAssets for material certification.
    /// </summary>
    internal static bool TrySelectCompatibleScaffoldForTest(
        NativeSuitPartIndex index,
        IReadOnlyList<SavedPartGraft> overlayGrafts,
        SavedPartGraft cosmeticCape,
        SavedPartGraft glideCape,
        string preferredShellPlayable,
        string preferredShellCutscene,
        out string playablePackage,
        out string cutscenePackage)
    {
        var selected = OrderedCompatibleScaffolds(
                index,
                overlayGrafts,
                cosmeticCape,
                glideCape,
                preferredShellPlayable,
                preferredShellCutscene)
            .FirstOrDefault();
        playablePackage = selected?.PlayablePackage ?? "";
        cutscenePackage = selected?.CutscenePackage ?? "";
        return selected is not null;
    }

    private static IEnumerable<Scaffold> FindCompatibleScaffolds(
        NativeSuitPartIndex index,
        IReadOnlyList<SavedPartGraft> overlayGrafts,
        SavedPartGraft cosmeticCape,
        SavedPartGraft glideCape)
    {
        var packages = (index.Parts ?? [])
            .Where(part => part is not null)
            .GroupBy(part => UnrealPathUtil.NormalizePackagePath(part.SourcePackagePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var playablePackages = packages.Where(pair => pair.Value.Any(part =>
                string.Equals(part.Context, "playable", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var (playablePackage, playableParts) in playablePackages)
        {
            var pairKey = DonorPairKey(playablePackage);
            var cutsceneMatches = packages.Where(pair =>
                    !SamePackage(pair.Key, playablePackage) &&
                    DonorPairKey(pair.Key).Equals(pairKey, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value.Any(part => string.Equals(part.Context, "cutscene", StringComparison.OrdinalIgnoreCase)))
                .Take(2)
                .ToList();
            if (cutsceneMatches.Count != 1)
            {
                continue;
            }
            var cutscenePackage = cutsceneMatches[0].Key;
            var cutsceneParts = cutsceneMatches[0].Value;
            if (!HasAuthoredCapeTorsoPair(playableParts) || !HasAuthoredCapeTorsoPair(cutsceneParts))
            {
                continue;
            }

            var scaffoldPlayableCape = UniqueSlot(playableParts, "Cape", "playable");
            var scaffoldCutsceneCape = UniqueSlot(cutsceneParts, "Cape", "cutscene");
            var scaffoldPlayableTorso = UniqueSlot(playableParts, "Torso", "playable");
            var scaffoldCutsceneTorso = UniqueSlot(cutsceneParts, "Torso", "cutscene");
            if (scaffoldPlayableCape is null || scaffoldCutsceneCape is null ||
                scaffoldPlayableTorso is null || scaffoldCutsceneTorso is null ||
                !SameComponentClass(scaffoldPlayableCape.ComponentClass, cosmeticCape.Playable?.TemplateComponentClass) ||
                !SameComponentClass(scaffoldCutsceneCape.ComponentClass, cosmeticCape.Cutscene?.TemplateComponentClass) ||
                !SameComponentClass(scaffoldPlayableTorso.ComponentClass, glideCape.Playable?.TemplateComponentClass) ||
                !SameComponentClass(scaffoldCutsceneTorso.ComponentClass, glideCape.Cutscene?.TemplateComponentClass))
            {
                continue;
            }

            var compatible = true;
            foreach (var overlay in overlayGrafts)
            {
                var desiredPlayableClass = overlay.Playable?.TemplateComponentClass ?? "";
                var desiredCutsceneClass = overlay.Cutscene?.TemplateComponentClass ?? "";
                var scaffoldPlayable = UniqueSlot(playableParts, overlay.Slot, "playable");
                var scaffoldCutscene = UniqueSlot(cutsceneParts, overlay.Slot, "cutscene");
                if (scaffoldPlayable is null || scaffoldCutscene is null ||
                    !SameComponentClass(scaffoldPlayable.ComponentClass, desiredPlayableClass) ||
                    !SameComponentClass(scaffoldCutscene.ComponentClass, desiredCutsceneClass))
                {
                    compatible = false;
                    break;
                }
            }
            if (compatible)
            {
                yield return new Scaffold(playablePackage, cutscenePackage);
            }
        }
    }

    private static bool HasAuthoredCapeTorsoPair(IReadOnlyList<NativeSuitPartRecord> parts)
    {
        var cape = UniqueSlot(parts, "Cape", parts.FirstOrDefault()?.Context ?? "");
        var torso = UniqueSlot(parts, "Torso", parts.FirstOrDefault()?.Context ?? "");
        return cape is not null && torso is not null &&
               string.Equals(cape.MeshKind, "SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
               (cape.ComponentTags ?? []).Any(tag => string.Equals(tag, "Cape", StringComparison.OrdinalIgnoreCase) ||
                                                     string.Equals(tag, "TtCharacterAsset.Cape", StringComparison.OrdinalIgnoreCase)) &&
               (cape.ComponentTags ?? []).All(tag => !string.Equals(tag, "Glider", StringComparison.OrdinalIgnoreCase)) &&
               GliderService.PairedCapeDriverForPart(torso) == PairedCapeVisibilityDriver.PairedCapable;
    }

    private static NativeSuitPartRecord? UniqueSlot(
        IReadOnlyList<NativeSuitPartRecord> parts,
        string slot,
        string context)
    {
        var matches = parts.Where(part =>
                part is not null &&
                string.Equals(part.Slot, slot, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(context) || string.Equals(part.Context, context, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool TryResolveVisualPair(
        NativeSuitProject project,
        NativeSuitPartIndex index,
        out string playablePackage,
        out string cutscenePackage,
        out string detail)
    {
        playablePackage = "";
        cutscenePackage = "";
        var visualSeed = UnrealPathUtil.NormalizePackagePath(
            project.BaseProfile?.VisualBasePackage ??
            project.VisualCutsceneSourceTemplate?.PackagePath ??
            project.VisualSourceTemplate?.PackagePath ?? "");
        if (string.IsNullOrWhiteSpace(visualSeed))
        {
            detail = "The selected visual-base package is missing from the saved project.";
            return false;
        }
        var pairKey = DonorPairKey(visualSeed);
        var packages = (index.Parts ?? [])
            .Where(part => part is not null)
            .Where(part => DonorPairKey(part.SourcePackagePath).Equals(pairKey, StringComparison.OrdinalIgnoreCase))
            .GroupBy(part => UnrealPathUtil.NormalizePackagePath(part.SourcePackagePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Package = group.Key,
                Contexts = group.Select(part => part.Context).ToHashSet(StringComparer.OrdinalIgnoreCase),
            })
            .ToList();
        var playable = packages.Where(candidate => candidate.Contexts.Contains("playable")).Take(2).ToList();
        var cutscene = packages.Where(candidate => candidate.Contexts.Contains("cutscene")).Take(2).ToList();
        if (playable.Count != 1 || cutscene.Count != 1)
        {
            detail =
                $"The selected visual base '{visualSeed}' could not resolve one exact playable/cutscene recipe pair " +
                $"(found {playable.Count} playable and {cutscene.Count} cutscene packages).";
            return false;
        }
        playablePackage = playable[0].Package;
        cutscenePackage = cutscene[0].Package;
        detail = "The selected visual-base playable/cutscene pair is exact.";
        return true;
    }

    private static bool TryExtractIdentityMaterials(
        string visualPlayable,
        string visualCutscene,
        IReadOnlyList<SavedPartGraft> overlayGrafts,
        out string playableBody,
        out string cutsceneBody,
        out string playableFace,
        out string cutsceneFace,
        out string detail)
    {
        playableBody = cutsceneBody = playableFace = cutsceneFace = "";
        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var mappings = TryLoadMappings();
        var playablePath = PackageToUasset(extractedRoot, visualPlayable);
        var cutscenePath = PackageToUasset(extractedRoot, visualCutscene);
        var playableFolder = CharacterFolder(visualPlayable);
        var cutsceneFolder = CharacterFolder(visualCutscene);
        (playableBody, playableFace) = AnimArchetypeGraftService.ExtractCharacterMaterials(
            playablePath,
            playableFolder,
            mappings);
        (cutsceneBody, cutsceneFace) = AnimArchetypeGraftService.ExtractCharacterMaterials(
            cutscenePath,
            cutsceneFolder,
            mappings);

        // Prefer the properties actually assigned on the Blueprint components. Import scanning is
        // only a fallback for assets whose material comes from an inherited/default mesh slot.
        playableBody = MaterialReplaceService.TryReadComponentMaterialPackage(
                           playablePath,
                           "CharacterMesh0",
                           0,
                           mappings) ?? playableBody;
        cutsceneBody = MaterialReplaceService.TryReadComponentMaterialPackage(
                           cutscenePath,
                           "CharacterMesh0",
                           0,
                           mappings) ?? cutsceneBody;
        playableFace = MaterialReplaceService.TryReadComponentMaterialPackage(
                           playablePath,
                           "Face",
                           0,
                           mappings) ?? playableFace;
        cutsceneFace = MaterialReplaceService.TryReadComponentMaterialPackage(
                           cutscenePath,
                           "Face",
                           0,
                           mappings) ?? cutsceneFace;

        var faceGraft = overlayGrafts.First(graft => string.Equals(graft.Slot, "Face", StringComparison.OrdinalIgnoreCase));
        playableFace = FirstMaterialOr(faceGraft.Playable, playableFace);
        cutsceneFace = FirstMaterialOr(faceGraft.Cutscene, cutsceneFace);
        if (!ValidGamePackage(playableBody) || !ValidGamePackage(cutsceneBody) ||
            !ValidGamePackage(playableFace) || !ValidGamePackage(cutsceneFace))
        {
            detail =
                "Batcomputer could not certify role-specific CharacterMesh0 and Face materials for the selected visual base. " +
                "Refresh the extracted character assets and part index before re-applying the cape pair.";
            return false;
        }
        detail = "The selected visual base's role-specific CharacterMesh0 and Face materials were certified.";
        return true;
    }

    private static string FirstMaterialOr(SavedPartGraftDonor? donor, string fallback) =>
        donor?.Materials?.Select(material => UnrealPathUtil.NormalizePackagePath(material.PackagePath))
            .FirstOrDefault(ValidGamePackage) ?? fallback;

    private static SavedPartGraftDonor ToSavedDonor(NativeSuitPartRecord part, string context) => new()
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
        Materials = (part.Materials ?? []).Select(material => new NativeSuitObjectRef
        {
            ObjectName = material.ObjectName,
            PackagePath = material.PackagePath,
            ObjectPath = material.ObjectPath,
            ClassName = material.ClassName,
        }).ToList(),
        ComponentTags = (part.ComponentTags ?? []).ToList(),
    };

    private static List<NativeSuitPartRecord> ExactParts(
        NativeSuitPartIndex index,
        string package,
        string context,
        string slot) =>
        (index.Parts ?? []).Where(part =>
                part is not null &&
                SamePackage(part.SourcePackagePath, package) &&
                string.Equals(part.Context, context, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(part.Slot, slot, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

    private static bool SavedDonorMatchesLive(
        NativeSuitPartIndex index,
        SavedPartGraftDonor? donor,
        string package,
        string context,
        string slot)
    {
        var live = ResolveExactLivePart(index, donor);
        return live is not null &&
               SamePackage(donor!.SourcePackagePath, package) &&
               string.Equals(donor.Context, context, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(donor.Slot, slot, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(live.MeshKind, donor.MeshKind, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(live.MeshObjectPath, donor.MeshObjectPath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(live.AnimClassObjectName, donor.AnimClassObjectName, StringComparison.OrdinalIgnoreCase) &&
               SameOptionalPackage(live.AnimClassPackagePath, donor.AnimClassPackagePath) &&
               string.Equals(live.AnimClassObjectPath, donor.AnimClassObjectPath, StringComparison.OrdinalIgnoreCase) &&
               SameComponentClass(live.ComponentClass, donor.TemplateComponentClass) &&
               SamePackage(live.TemplatePackagePath, donor.TemplatePackagePath) &&
               string.Equals(live.TemplateSlot, donor.TemplateSlot, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(live.ParentComponentOrVariableName, donor.ParentComponentOrVariableName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(live.AttachSocket, donor.AttachSocket, StringComparison.OrdinalIgnoreCase) &&
               SameMaterials(live.Materials, donor.Materials) &&
               SameTags(live.ComponentTags, donor.ComponentTags);
    }

    private static bool SameMaterials(
        IReadOnlyList<NativeSuitObjectRef>? left,
        IReadOnlyList<NativeSuitObjectRef>? right)
    {
        left ??= [];
        right ??= [];
        return left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First?.ObjectName, pair.Second?.ObjectName, StringComparison.OrdinalIgnoreCase) &&
            SameOptionalPackage(pair.First?.PackagePath, pair.Second?.PackagePath) &&
            string.Equals(pair.First?.ObjectPath, pair.Second?.ObjectPath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameTags(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        var leftSet = (left ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightSet = (right ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftSet.SetEquals(rightSet);
    }

    private static bool SameOptionalPackage(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right) || SamePackage(left, right);

    private static bool HasExistingFieldRecipe(NativeSuitPartRecord part) =>
        !string.IsNullOrWhiteSpace(part.SourcePackagePath) &&
        !string.IsNullOrWhiteSpace(part.MeshObjectPath) &&
        !string.IsNullOrWhiteSpace(part.ComponentClass) &&
        !string.IsNullOrWhiteSpace(part.TemplateComponentClass) &&
        !string.IsNullOrWhiteSpace(part.TemplateSlot) &&
        string.Equals(part.TemplateSlot, part.Slot, StringComparison.OrdinalIgnoreCase) &&
        SamePackage(part.TemplatePackagePath, part.SourcePackagePath);

    private static bool SameComponentClass(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static NativeSuitPartIndex? TryLoadActivePartIndex()
    {
        try
        {
            var projectRoot = AppSettings.Current.ProjectRoot;
            return string.IsNullOrWhiteSpace(projectRoot)
                ? null
                : new PartIndexService(projectRoot).LoadPartIndex();
        }
        catch
        {
            return null;
        }
    }

    private static Usmap? TryLoadMappings()
    {
        try
        {
            var path = AppSettings.Current.EffectiveUsmapPath();
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? MappingsCache.Load(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasConcreteVisualBase(NativeSuitProject project)
    {
        var visual = UnrealPathUtil.NormalizePackagePath(
            project.BaseProfile?.VisualBasePackage ??
            project.VisualCutsceneSourceTemplate?.PackagePath ??
            project.VisualSourceTemplate?.PackagePath ?? "");
        var gameplay = UnrealPathUtil.NormalizePackagePath(
            project.BaseProfile?.GameplayDonorPackage ?? project.PlayableTemplate?.PackagePath ?? "");
        return visual.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) &&
               gameplay.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase);
    }

    private static string DonorPairKey(string? packagePath)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath ?? "");
        foreach (var suffix in new[] { "_Playable", "_Cutscene" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized[..^suffix.Length];
            }
        }
        return normalized;
    }

    private static string CharacterFolder(string packagePath)
    {
        var segments = UnrealPathUtil.NormalizePackagePath(packagePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? segments[^2] : "";
    }

    private static string PackageToUasset(string contentRoot, string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        return package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(contentRoot, package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset"
            : "";
    }

    private static bool SamePackage(string? left, string? right) =>
        UnrealPathUtil.NormalizePackagePath(left ?? "").Equals(
            UnrealPathUtil.NormalizePackagePath(right ?? ""),
            StringComparison.OrdinalIgnoreCase);

    private static bool ValidGamePackage(string? package) =>
        UnrealPathUtil.NormalizePackagePath(package ?? "").StartsWith("/Game/", StringComparison.OrdinalIgnoreCase);
}
