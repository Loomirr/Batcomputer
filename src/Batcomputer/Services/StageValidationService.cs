using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using PropertyData = UAssetAPI.PropertyTypes.Objects.PropertyData;

namespace Batcomputer;

/// <summary>
/// Structural preflight for a staged suit - reads the cooked playable/cutscene (+ the
/// generated anim sets) and surfaces the failure classes that actually crashed the game
/// or silently broke features this project hit:
///   * an asset that fails to re-parse (would crash the cooked loader)         [ERROR]
///   * component class vs mesh-kind mismatch (conversion corruption)           [ERROR]
///   * donor-shell static hair/hat (the hover-crash signature)                 [WARN]
///   * glider configured but its glide anim isn't injected (invisible wingsuit)[WARN]
/// Findings feed the existing package preflight; ERRORs block the package before retoc.
/// </summary>
public sealed class StageValidationService
{
    public sealed record Finding(string Severity, string Message); // "ERROR" | "WARN"

    private readonly string _contentRoot;
    private readonly string _projectRoot;
    private readonly Usmap? _mappings;

    public StageValidationService(string contentRoot, string? mappingsPath, string? projectRoot = null)
    {
        _contentRoot = contentRoot;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? AppSettings.Current.EffectiveProjectRoot()
            : projectRoot;
        _mappings = string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath)
            ? null
            : MappingsCache.Load(mappingsPath);
    }

    public List<Finding> Validate(NativeSuitProject project)
    {
        var findings = new List<Finding>();
        var characterAssets = new Dictionary<string, UAsset>(StringComparer.OrdinalIgnoreCase);

        CheckCustomStaticMeshDeclarations(project, findings);

        foreach (var (role, pkg) in new[]
        {
            ("playable", project.TargetPackages.Playable),
            ("cutscene", project.TargetPackages.Cutscene)
        })
        {
            if (string.IsNullOrWhiteSpace(pkg))
            {
                continue;
            }
            var asset = TryLoad(pkg, role, findings);
            if (asset is null)
            {
                continue;
            }
            characterAssets[role] = asset;
            CheckMeshKindMismatch(asset, role, findings);
            CheckDonorShellStaticHair(asset, role, findings);
            CheckCustomStaticMeshComponentIntegrity(asset, role, project, findings);
        }

        CheckGameplayShellIntegrity(project, characterAssets, findings);
        CheckNativeBodyProfile(project, characterAssets, findings);
        CheckPawnTag(project, findings);
        CheckGliderAnimInjection(project, findings);
        CheckAbilityDependencyDeclarations(project, findings);
        CheckRequiredAbilitySets(project, findings);
        CheckEquipmentDependencies(project, findings);
        CheckGliderDependencies(project, characterAssets, findings);
        return findings;
    }

    /// <summary>
    /// Certifies the non-visual Blueprint nodes and parent archetype that the selected gameplay
    /// donor contributes. A native body profile is only a CharacterMesh0 geometry choice; it must
    /// never remove dialogue/character-presentation nodes or replace the donor's DPRD/MAS/LAS
    /// inheritance chain.
    /// </summary>
    private void CheckGameplayShellIntegrity(
        NativeSuitProject project,
        IReadOnlyDictionary<string, UAsset> characterAssets,
        List<Finding> findings)
    {
        foreach (var requirement in project.Requirements.Where(requirement =>
                     requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) &&
                     GameplayShellComponentPolicy.IsRequired(requirement.TargetComponent)))
        {
            findings.Add(new("ERROR",
                $"Saved component removal '{requirement.TargetComponent}' targets required gameplay infrastructure. " +
                "Remove that rule and rebuild; dialogue and in-level character/suit presentation depend on this node."));
        }

        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        foreach (var (role, stagedAsset) in characterAssets)
        {
            var playable = role.Equals("playable", StringComparison.OrdinalIgnoreCase);
            var template = playable ? project.PlayableTemplate : project.CutsceneTemplate;
            string sourcePackage;
            try
            {
                sourcePackage = UAssetPatchService.EffectiveCharacterSourcePackage(project, playable);
            }
            catch
            {
                sourcePackage = UnrealPathUtil.NormalizePackagePath(template?.PackagePath);
            }

            var sourceUasset = !string.IsNullOrWhiteSpace(template?.Uasset) && File.Exists(template.Uasset)
                ? template.Uasset
                : ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, sourcePackage) ?? "";
            if (string.IsNullOrWhiteSpace(sourceUasset) || !File.Exists(sourceUasset))
            {
                continue;
            }

            try
            {
                var sourceAsset = new UAsset(
                    sourceUasset,
                    EngineVersion.VER_UE5_6,
                    _mappings,
                    CustomSerializationFlags.SkipPreloadDependencyLoading);
                var missing = MissingRequiredGameplayShellComponentsForTest(
                    LiveScsComponentNames(sourceAsset),
                    LiveScsComponentNames(stagedAsset));
                if (missing.Count > 0)
                {
                    findings.Add(new("ERROR",
                        $"{role}: required gameplay-shell component(s) from the selected donor are inactive: " +
                        string.Join(", ", missing) +
                        ". Rebuild from the selected gameplay donor; body/visual cleanup may remove appearance nodes only."));
                }
            }
            catch (Exception ex)
            {
                findings.Add(new("WARN",
                    $"{role}: could not compare required gameplay-shell nodes with '{sourcePackage}': {ex.Message}"));
            }
        }

        // With no generated archetype, the playable must still inherit the exact donor archetype.
        // That parent owns the DPRD plus MAS/LAS composition, including native stealth/focus and
        // menu behavior. A body mesh choice never has authority to change it.
        if (project.BodyProfile is null ||
            AnimArchetypeGraftService.RequiresCustomArchetype(project) ||
            !characterAssets.TryGetValue("playable", out var playableAsset))
        {
            return;
        }

        var donor = AnimArchetypeGraftService.DetectDonorForProject(project, _contentRoot, _mappings);
        if (donor is { Valid: true } &&
            !IsGeneratedClassParentedToPackage(
                playableAsset,
                project.TargetPackages.Playable,
                donor.ArchetypePackage))
        {
            findings.Add(new("ERROR",
                "playable: selecting a native body changed or lost the gameplay donor archetype. " +
                $"The suit must remain parented to '{donor.ArchetypePackage}' so its DPRD, abilities, " +
                "stealth/focus behavior, animation sets, and in-level suit flow stay intact."));
        }
    }

    internal static IReadOnlyList<string> MissingRequiredGameplayShellComponentsForTest(
        IEnumerable<string> requiredSourceComponents,
        IEnumerable<string> stagedComponents)
    {
        var required = requiredSourceComponents
            .Select(GameplayShellComponentPolicy.ComponentName)
            .Where(GameplayShellComponentPolicy.IsRequired)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var actual = stagedComponents
            .Select(GameplayShellComponentPolicy.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required
            .Where(component => !actual.Contains(component))
            .OrderBy(component => component, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CheckNativeBodyProfile(
        NativeSuitProject project,
        IReadOnlyDictionary<string, UAsset> characterAssets,
        List<Finding> findings)
    {
        var profile = project.BodyProfile;
        if (profile is null)
        {
            return;
        }

        var canonical = NativeBodyProfileService.Find(profile.Id) ??
                        NativeBodyProfileService.MatchMesh(profile.MeshPackagePath);
        if (canonical is null)
        {
            findings.Add(new("ERROR",
                $"The saved native body profile '{profile.Id}' is not in this Batcomputer body catalog. Re-select it and rebuild the suit."));
            return;
        }

        if (!profile.HeadPolicy.Equals(NativeBodyProfileService.IntegratedHeadPolicy, StringComparison.OrdinalIgnoreCase) &&
            !profile.HeadPolicy.Equals(NativeBodyProfileService.IntentionallyAbsentHeadPolicy, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new("ERROR",
                $"Native body profile '{profile.DisplayName}' has an unknown head policy '{profile.HeadPolicy}'. Re-select it before packaging."));
        }

        foreach (var role in new[] { "playable", "cutscene" })
        {
            if (!characterAssets.TryGetValue(role, out var asset))
            {
                continue;
            }
            var actual = UnrealPathUtil.NormalizePackagePath(
                NativeBodyProfileService.TryReadBodyMeshPackage(asset));
            if (!actual.Equals(canonical.MeshPackagePath, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new("ERROR",
                    $"{role}: CharacterMesh0 uses '{actual}' but the suit declares native body '{canonical.MeshPackagePath}'. Rebuild the suit so the body profile is applied to both roles."));
            }
        }

        if (canonical.MissingRegions.Count > 0)
        {
            findings.Add(new("WARN",
                $"Native body '{canonical.DisplayName}' intentionally leaves {string.Join(", ", canonical.MissingRegions).ToLowerInvariant()} missing. Check equipment, attachments, damage/death, LODs, and cutscenes in-game."));
        }
        foreach (var warning in canonical.Warnings)
        {
            findings.Add(new("WARN", $"Native body '{canonical.DisplayName}': {warning}"));
        }
    }

    /// <summary>
    /// The donor tag every generated suit used to share. A suit that still ships with it collides
    /// with the native TheBatman2025 button and with every other suit that fell back to it.
    /// </summary>
    private const string LegacyDonorPawnTag = "Pawns.Playable.Batman.TheBatman2025";

    /// <summary>
    /// Every native suit owns a globally-unique pawn tag. Sharing one is what makes custom-to-custom
    /// switching do nothing (the game sees no identity change, so it never rebuilds the pawn), makes
    /// the menu highlight several buttons at once, and bleeds one suit's icon onto another's button.
    /// PawnTagConfigService already refuses to write a mod bundle without it; this catches the
    /// single-suit package, which used to fall back to the donor tag silently.
    /// </summary>
    private static void CheckPawnTag(NativeSuitProject project, List<Finding> findings)
    {
        var tag = (project.PawnTag ?? "").Trim();

        if (string.IsNullOrWhiteSpace(tag))
        {
            findings.Add(new("ERROR",
                "This suit has no PawnTag. Every native suit needs its own globally-unique tag - " +
                "set one in Base → Native identity. Without it the suit falls back to the shared " +
                "donor tag and will not switch cleanly from another custom suit."));
            return;
        }

        if (tag.Equals(LegacyDonorPawnTag, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new("ERROR",
                $"PawnTag is the shared donor tag ({LegacyDonorPawnTag}). It belongs to the game's own " +
                "TheBatman2025 character - a suit using it collides with that button and with any other " +
                "suit on the same tag. Pick a unique tag in Base → Native identity."));
            return;
        }

        if (!tag.StartsWith("Pawns.Playable.", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new("WARN",
                $"PawnTag '{tag}' is outside the Pawns.Playable.* namespace the game registers playable " +
                "characters under. It will build, but the character may not resolve at runtime."));
        }
    }

    private UAsset? TryLoad(string pkg, string role, List<Finding> findings)
    {
        string uasset;
        try
        {
            uasset = PackagePathToBasePath(pkg) + ".uasset";
        }
        catch (Exception ex)
        {
            findings.Add(new("WARN", $"{role}: could not resolve staged path for {pkg}: {ex.Message}"));
            return null;
        }

        if (!File.Exists(uasset))
        {
            findings.Add(new("ERROR", $"{role}: staged asset is missing ({uasset}). Re-pick base character to rebuild the stage."));
            return null;
        }

        try
        {
            // A staged asset that won't re-parse would crash the cooked loader on spawn.
            return new UAsset(uasset, EngineVersion.VER_UE5_6, _mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR", $"{role}: staged asset failed to parse — it would crash the game on load. {ex.Message}"));
            return null;
        }
    }

    /// <summary>A mesh component whose class disagrees with its mesh property (StaticMeshComponent
    /// carrying a SkeletalMesh, or vice versa) is the class-conversion corruption that crashes
    /// the cooked loader.</summary>
    private static void CheckMeshKindMismatch(UAsset asset, string role, List<Finding> findings)
    {
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            var cls = export.GetExportClassType().Value?.ToString() ?? "";
            var isStatic = cls.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase);
            var isSkeletal = cls.Contains("Skeletal", StringComparison.OrdinalIgnoreCase) ||
                             cls.Contains("SkinnedMesh", StringComparison.OrdinalIgnoreCase);
            if (!isStatic && !isSkeletal)
            {
                continue;
            }
            var hasStaticMesh = export.Data.Any(p => p.Name.ToString().Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
            var hasSkeletalMesh = export.Data.Any(p =>
                p.Name.ToString().Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ||
                p.Name.ToString().Equals("SkinnedAsset", StringComparison.OrdinalIgnoreCase));

            if (isStatic && hasSkeletalMesh)
            {
                findings.Add(new("ERROR",
                    $"{role}: component '{export.ObjectName}' is a StaticMeshComponent but carries a SkeletalMesh property — a class/property mismatch that crashes the cooked loader (banned component conversion). Re-pick base and use a matching-kind part."));
            }
            if (isSkeletal && hasStaticMesh)
            {
                findings.Add(new("ERROR",
                    $"{role}: component '{export.ObjectName}' is a skeletal component but carries a StaticMesh property — a class/property mismatch (crash risk)."));
            }
        }
    }

    /// <summary>Static hair/hat on a StaticMeshComponent is the donor-shell graft signature that
    /// crashed the game on menu hover. The graft path is disabled, so this only catches a stale
    /// poisoned stage - WARN (a base's native static head, e.g. ThomasWayne, is legitimate).</summary>
    private static void CheckDonorShellStaticHair(UAsset asset, string role, List<Finding> findings)
    {
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            var cls = export.GetExportClassType().Value?.ToString() ?? "";
            if (!cls.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var meshProp = export.Data.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name.ToString().Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
            if (meshProp is null || meshProp.Value.IsNull() || !meshProp.Value.IsImport())
            {
                continue;
            }
            var meshName = meshProp.Value.ToImport(asset).ObjectName.ToString();
            if (meshName.StartsWith("SM_HAIR_", StringComparison.OrdinalIgnoreCase) ||
                meshName.StartsWith("SM_HAT_", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new("WARN",
                    $"{role}: static component '{export.ObjectName}' holds '{meshName}'. Verify that it came from a native static-component recipe before in-game testing. (Ignore if it's the base character's own native static head.)"));
            }
        }
    }

    /// <summary>If a cross-type glider is configured (GliderAnimLas/Mas set), the generated
    /// LAS_Char/MAS_Char must actually reference those sets - otherwise the wingsuit renders
    /// invisible (wrong body glide pose). Catches a silent injection failure.</summary>
    private void CheckGliderAnimInjection(NativeSuitProject project, List<Finding> findings)
    {
        var mod = ExtractMod(project.TargetPackages.Playable);
        if (string.IsNullOrWhiteSpace(mod))
        {
            return;
        }

        var contract = new AnimArchetypeGraftService().BaseCapeGlideContract(project);
        var certifiedPairedCape = GliderService.IsDeclaredPairedCapeAdapterValid(
            project,
            contract,
            requireResolvedComponents: true,
            out _);
        CheckAnimSetReferenced(
            project.GliderAnimLas,
            $"/Game/Mods/{mod}/Characters/LAS_Char_{mod}",
            "LAS_Char",
            certifiedPairedCape,
            findings);
        CheckAnimSetReferenced(
            project.GliderAnimMas,
            $"/Game/Mods/{mod}/Characters/MAS_Char_{mod}",
            "MAS_Char",
            certifiedPairedCape,
            findings);
    }

    private void CheckAnimSetReferenced(
        string? animSetPkg,
        string charSetPkg,
        string label,
        bool failClosed,
        List<Finding> findings)
    {
        if (string.IsNullOrWhiteSpace(animSetPkg))
        {
            return;
        }
        var setPackage = UnrealPathUtil.NormalizePackagePath(animSetPkg);
        var setName = AssetName(setPackage);
        string uasset;
        try
        {
            uasset = PackagePathToBasePath(charSetPkg) + ".uasset";
        }
        catch
        {
            return;
        }
        if (!File.Exists(uasset))
        {
            findings.Add(new(failClosed ? "ERROR" : "WARN",
                $"glider glide-animation '{setName}' is configured but {label} was not generated ({uasset}) — the body cannot use the selected visual's glide pose. Ensure the custom archetype is enabled and re-package."));
            return;
        }
        try
        {
            var asset = new UAsset(uasset, EngineVersion.VER_UE5_6, _mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            var referenced = ParentSetPackages(asset).Contains(
                setPackage,
                StringComparer.OrdinalIgnoreCase);
            if (!referenced)
            {
                findings.Add(new(failClosed ? "ERROR" : "WARN",
                    $"glider glide-animation '{setPackage}' is configured but that exact package is not active in {label}'s ParentSetsArray — the body will use the wrong glide pose. Re-apply the glider preset and re-package."));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new(failClosed ? "ERROR" : "WARN",
                $"could not verify glider anim injection in {label}: {ex.Message}"));
        }
    }

    private void CheckRequiredAbilitySets(NativeSuitProject project, List<Finding> findings)
    {
        if (project.EquipmentSlots.Count == 0 &&
            !project.PartGrafts.Any(graft => graft.IsGlider) &&
            !AbilityLoadoutService.HasCustomizations(project))
        {
            return;
        }

        var gameData = GameDataService.Instance;
        var donorFamily = project.BaseProfile?.GameplayFamily;
        if (string.IsNullOrWhiteSpace(donorFamily))
        {
            donorFamily = gameData.FamilyForBasePath(project.PlayableTemplate?.PackagePath ?? "")?.Name;
        }

        // Equipment-owned AbilitySets live on the ED's AbilitySetsToGrant and must not also be
        // appended to DPRD. Only character-side style/glider sets belong in this certificate.
        var requiredSets = AbilityDependencyService.Build(
                project,
                donorFamily,
                gameData.Db.Equipment)
            .RequiredAbilitySets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var baseContract = new AnimArchetypeGraftService().BaseCapeGlideContract(project);
        var preservesNativeGliding = GliderService.IsDeclaredPairedCapeAdapterValid(
            project,
            baseContract,
            requireResolvedComponents: true,
            out _);
        if (project.PartGrafts.Any(graft => graft.IsGlider) &&
            !preservesNativeGliding &&
            !requiredSets.Contains(GliderService.GlidingAbilitySetPackage, StringComparer.OrdinalIgnoreCase))
        {
            requiredSets.Add(GliderService.GlidingAbilitySetPackage);
        }
        if (requiredSets.Count == 0 && project.EquipmentSlots.Count == 0 &&
            !AbilityLoadoutService.HasCustomizations(project))
        {
            return;
        }

        var mod = ExtractMod(project.TargetPackages.Playable);
        if (string.IsNullOrWhiteSpace(mod))
        {
            findings.Add(new("ERROR",
                "Equipment or glider dependencies need a generated mod DPRD, but the playable package has no /Game/Mods/<mod>/ path."));
            return;
        }
        var package = $"/Game/Mods/{mod}/Characters/DA_DPRD_{mod}";
        var uasset = PackagePathToBasePath(package) + ".uasset";
        if (!File.Exists(uasset))
        {
            findings.Add(new("ERROR",
                "Equipment or glider dependencies need a generated DPRD, but none was produced. " +
                "Re-apply the gadget or glider, then rebuild."));
            return;
        }

        try
        {
            var donor = AnimArchetypeGraftService.DetectDonorForProject(
                project,
                _contentRoot,
                _mappings);
            if (donor is null || !donor.Valid || string.IsNullOrWhiteSpace(donor.DprdPackage))
            {
                findings.Add(new("ERROR",
                    "The generated DPRD dependency certificate could not resolve the exact gameplay donor."));
                return;
            }
            var plan = AbilityDependencyService.Build(
                project,
                donorFamily,
                gameData.Db.Equipment);
            if (project.EquipmentSlots.Count > 0)
            {
                var mutation = new AbilityAssetMutationService();
                var donorDprd = ExtractedPackagePathService.ResolvePackageUasset(
                    AppSettings.Current.EffectiveExtractedContentRoot(),
                    donor.DprdPackage) ?? "";
                var donorEquipment = mutation.InspectDprdEquipment(donorDprd);
                var stagedEquipment = mutation.InspectDprdEquipment(uasset);
                if (!donorEquipment.Success || !stagedEquipment.Success)
                {
                    findings.Add(new("ERROR",
                        donorEquipment.Error ?? stagedEquipment.Error ??
                        "The exact donor/staged DPRD Equipment arrays could not be inspected."));
                }
                else
                {
                    var expectedEquipment = donorEquipment.Equipment.OrderBy(entry => entry.Index)
                        .Select(entry => entry.IsNull ? "" : UnrealPathUtil.NormalizePackagePath(entry.PackagePath))
                        .ToList();
                    var exact = true;
                    foreach (var change in project.EquipmentSlots)
                    {
                        var equipment = gameData.FindEquipment(change.Gadget);
                        if (equipment is null || string.IsNullOrWhiteSpace(equipment.EdPackage) ||
                            change.Slot < 0 || change.Slot >= expectedEquipment.Count)
                        {
                            exact = false;
                            break;
                        }
                        expectedEquipment[change.Slot] = UnrealPathUtil.NormalizePackagePath(equipment.EdPackage);
                    }
                    var actualEquipment = stagedEquipment.Equipment.OrderBy(entry => entry.Index)
                        .Select(entry => entry.IsNull ? "" : UnrealPathUtil.NormalizePackagePath(entry.PackagePath))
                        .ToList();
                    if (!exact || !actualEquipment.SequenceEqual(
                            expectedEquipment,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        findings.Add(new("ERROR",
                            "The staged DPRD Equipment array does not match the exact donor runtime slots plus saved replacements."));
                    }
                }
            }
            var mas = PackagePathToBasePath($"/Game/Mods/{mod}/Characters/MAS_Char_{mod}") + ".uasset";
            var las = PackagePathToBasePath($"/Game/Mods/{mod}/Characters/LAS_Char_{mod}") + ".uasset";
            if (!AnimArchetypeGraftService.VerifyStagedDependencyCertificate(
                    project,
                    donor,
                    AppSettings.Current.EffectiveExtractedContentRoot(),
                    _contentRoot,
                    uasset,
                    mod,
                    requiredSets,
                    plan.GameplayAbilitiesToBridge,
                    plan.RequiredGameplayEffects,
                    plan,
                    File.Exists(mas) ? mas : "",
                    File.Exists(las) ? las : "",
                    out var detail))
            {
                findings.Add(new("ERROR",
                    "Generated ability/equipment state is not the exact resolved loadout: " + detail));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR",
                $"Could not verify equipment or glider dependencies in the generated DPRD: {ex.Message}"));
        }
    }

    private static void CheckEquipmentDependencies(NativeSuitProject project, List<Finding> findings)
    {
        if (project.EquipmentSlots.Count == 0)
        {
            return;
        }

        var donorFamily = project.BaseProfile?.GameplayFamily;
        if (string.IsNullOrWhiteSpace(donorFamily))
        {
            donorFamily = GameDataService.Instance
                .FamilyForBasePath(project.PlayableTemplate?.PackagePath ?? "")?
                .Name;
        }
        foreach (var change in project.EquipmentSlots)
        {
            var equipment = GameDataService.Instance.FindEquipment(change.Gadget);
            var resolutionError = EquipmentDependencyService.SavedChangeResolutionError(change, equipment);
            if (resolutionError is not null)
            {
                findings.Add(new("ERROR",
                    resolutionError +
                    " Refresh the game-data catalog or choose a resolvable gadget before packaging."));
                continue;
            }

            // SavedChangeResolutionError established that this catalog record has its required ETA.
            var resolvedEquipment = equipment!;
            var profile = EquipmentDependencyService.Analyze(resolvedEquipment, donorFamily);
            if (profile.Support == EquipmentSupportKind.Controller)
            {
                var actors = profile.RuntimeActors.Count == 0
                    ? ""
                    : " Runtime actors: " + string.Join(", ", profile.RuntimeActors) + ".";
                if (!string.IsNullOrWhiteSpace(profile.RequiredGameplayFamily) &&
                    !string.Equals(profile.RequiredGameplayFamily, donorFamily, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new("ERROR",
                        $"Equipment '{resolvedEquipment.Name}' only works from a {profile.RequiredGameplayFamily} gameplay base. " +
                        $"The current donor is {donorFamily ?? "not set"}; its remote pawn will not operate in-game. " +
                        $"Choose a {profile.RequiredGameplayFamily} playable when selecting the visual base, then add this gadget again.{actors}"));
                }
                else
                {
                    findings.Add(new("WARN",
                        $"Equipment '{resolvedEquipment.Name}' is a controller setup. Its staged ED retains AbilitySetsToGrant without duplicating them in DPRD, but controller spawn and recall behavior still needs an in-game check.{actors}"));
                }
            }
            else if (profile.Support is EquipmentSupportKind.Experimental or EquipmentSupportKind.FamilyOnly)
            {
                findings.Add(new("WARN",
                    $"Equipment '{resolvedEquipment.Name}' is {profile.SupportLabel.ToLowerInvariant()}: {profile.Summary}"));
            }
        }
    }

    private static void CheckAbilityDependencyDeclarations(
        NativeSuitProject project,
        ICollection<Finding> findings)
    {
        if (!AbilityLoadoutService.HasCustomizations(project) && project.EquipmentSlots.Count == 0)
        {
            return;
        }
        var donorFamily = project.BaseProfile?.GameplayFamily;
        if (string.IsNullOrWhiteSpace(donorFamily))
        {
            donorFamily = GameDataService.Instance
                .FamilyForBasePath(project.PlayableTemplate?.PackagePath ?? "")?
                .Name;
        }
        var plan = AbilityDependencyService.Build(
            project,
            donorFamily,
            GameDataService.Instance.Db.Equipment);
        foreach (var issue in plan.Issues)
        {
            findings.Add(new Finding(
                issue.Severity == AbilityDependencySeverity.Error ? "ERROR" : "WARN",
                issue.Message));
        }
    }

    /// <summary>
    /// Proves that every saved custom-mesh declaration has one complete cooked mesh package.
    /// Blueprint checks below then bind that package to both generated character roles. Keeping
    /// the declaration as the source of truth prevents an orphaned or stale CustomMesh_* export
    /// from being mistaken for a successfully rebuilt attachment.
    /// </summary>
    private void CheckCustomStaticMeshDeclarations(
        NativeSuitProject project,
        List<Finding> findings)
    {
        var declarations = (project.CustomStaticMeshes ?? []).Where(custom => custom is not null).ToList();
        if (declarations.Count == 0)
        {
            return;
        }

        foreach (var duplicate in DuplicateCustomStaticMeshDeclarationKeys(project, declarations))
        {
            findings.Add(new("ERROR",
                $"Custom static mesh declarations reuse {duplicate}. Every imported OBJ needs a unique saved ID, component, and mesh package. Remove the duplicate and rebuild the custom meshes."));
        }

        foreach (var custom in declarations)
        {
            var display = CustomStaticMeshLabel(custom);
            CheckProjectOwnedCustomStaticMeshSource(project, custom, display, findings);
            if (string.IsNullOrWhiteSpace(NormalizeCustomStaticMeshId(custom.Id)))
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has no valid saved ID. Remove it and import the OBJ again before packaging."));
            }

            // Validate the saved slot table before looking at generated assets. The writer and
            // component overrides both rely on a dense 0..N-1 mapping; accepting duplicate or
            // sparse slot IDs here can make a valid-looking package bind the wrong material at
            // runtime. Projects saved before multi-material OBJ support synthesize slot zero from
            // MaterialPath and remain fully supported.
            var materialSlots = CheckCustomStaticMeshMaterialDeclarations(
                project,
                custom,
                display,
                findings);

            string meshPackage;
            try
            {
                meshPackage = DeclaredCustomStaticMeshPackage(project, custom);
            }
            catch (Exception ex)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has an invalid mesh package declaration: {ex.Message}"));
                continue;
            }

            if (!ValidExpectedPackage(meshPackage))
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has invalid mesh package '{meshPackage}'. Rebuild it from Parts before packaging."));
                continue;
            }

            var missingMeshFiles = MissingNonEmptyPackageFiles(
                _contentRoot,
                meshPackage,
                ".uasset",
                ".uexp",
                ".ubulk");
            if (missingMeshFiles.Count > 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' is missing its complete generated package ({string.Join(", ", missingMeshFiles)}): {meshPackage}. Rebuild the OBJ before packaging."));
            }
            else
            {
                CheckGeneratedCustomStaticMeshPackage(
                    meshPackage,
                    display,
                    materialSlots,
                    findings);
            }
        }
    }

    /// <summary>
    /// Validates the declarative material table and every saved inspector override for one custom
    /// component. Keeping this check ahead of package inspection catches malformed JSON without
    /// relying on whatever subset the writer happened to accept.
    /// </summary>
    private IReadOnlyList<CustomStaticMeshMaterialSlot> CheckCustomStaticMeshMaterialDeclarations(
        NativeSuitProject project,
        CustomStaticMeshImport custom,
        string display,
        List<Finding> findings)
    {
        var hasExplicitSlots = custom.MaterialSlots is { Count: > 0 };
        var slots = EffectiveCustomStaticMeshMaterialSlots(custom);

        if (hasExplicitSlots)
        {
            var rawSlots = custom.MaterialSlots ?? [];
            var nullSlotCount = rawSlots.Count(slot => slot is null);
            if (nullSlotCount > 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has {nullSlotCount} empty material-slot declaration(s). Re-import the OBJ before packaging."));
            }

            var duplicateSlots = slots
                .GroupBy(slot => slot.Slot)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(slot => slot)
                .ToList();
            if (duplicateSlots.Count > 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' reuses material slot(s) {string.Join(", ", duplicateSlots)}. Re-import the OBJ so every section has one unique slot."));
            }

            var orderedSlotIds = slots.Select(slot => slot.Slot).OrderBy(slot => slot).ToList();
            var expectedSlotIds = Enumerable.Range(0, slots.Count).ToList();
            if (!orderedSlotIds.SequenceEqual(expectedSlotIds))
            {
                var actual = orderedSlotIds.Count == 0 ? "<none>" : string.Join(", ", orderedSlotIds);
                var expected = expectedSlotIds.Count == 0 ? "<none>" : string.Join(", ", expectedSlotIds);
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has non-contiguous material slots [{actual}]; expected [{expected}]. Re-import the OBJ before packaging."));
            }

            var missingNames = slots
                .Where(slot => string.IsNullOrWhiteSpace(slot.StableSlotName))
                .Select(slot => slot.Slot)
                .OrderBy(slot => slot)
                .ToList();
            if (missingNames.Count > 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has no stable material-slot name for slot(s) {string.Join(", ", missingNames)}. Re-import the OBJ before packaging."));
            }

            var duplicateNames = slots
                .Where(slot => !string.IsNullOrWhiteSpace(slot.StableSlotName))
                .GroupBy(slot => slot.StableSlotName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (duplicateNames.Count > 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' reuses stable material-slot name(s) {string.Join(", ", duplicateNames.Select(name => $"'{name}'"))}. Re-import the OBJ before packaging."));
            }

            var sourceNameIssues = CustomStaticMeshSourceMaterialNameIssues(slots);
            if (sourceNameIssues.MissingSlots.Count > 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has no source OBJ material name for slot(s) {string.Join(", ", sourceNameIssues.MissingSlots)}. Re-import the OBJ before packaging."));
            }
            if (sourceNameIssues.DuplicateNames.Count > 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' reuses source OBJ material name(s) {string.Join(", ", sourceNameIssues.DuplicateNames.Select(name => $"'{name}'"))}. Re-import the OBJ before packaging."));
            }
        }

        if (slots.Count == 0)
        {
            findings.Add(new("ERROR",
                $"Custom static mesh '{display}' declares no material slots. Re-import the OBJ before packaging."));
        }

        foreach (var slot in slots)
        {
            var declaredMaterial = EffectiveCustomStaticMeshDeclaredMaterial(custom, slot);
            CheckCustomStaticMeshMaterialFiles(
                declaredMaterial,
                $"{display} — slot {slot.Slot} ({CustomStaticMeshSlotLabel(slot)})",
                findings);
        }

        var componentName = NormalizeCustomStaticMeshComponent(
            CustomStaticMeshImportService.ComponentNameFor(custom));
        var validSlotIds = slots.Select(slot => slot.Slot).ToHashSet();
        foreach (var assignment in (project.MaterialAssignments ?? [])
                     .Where(assignment => assignment is not null &&
                         CustomStaticMeshMaterialAssignmentMatches(assignment.Component, componentName)))
        {
            if (!validSlotIds.Contains(assignment.Slot))
            {
                var validRange = slots.Count == 0
                    ? "no valid slots are declared"
                    : $"valid slots are 0 through {slots.Count - 1}";
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has a saved material assignment for out-of-range slot {assignment.Slot}; {validRange}. Remove that assignment or re-import the OBJ before packaging."));
                continue;
            }

            var assignmentPackage = UnrealPathUtil.NormalizePackagePath(assignment.MiPackagePath);
            CheckCustomStaticMeshMaterialFiles(
                assignmentPackage,
                $"{display} — slot {assignment.Slot} {assignment.Context} override",
                findings);
        }

        return slots;
    }

    private static (List<int> MissingSlots, List<string> DuplicateNames)
        CustomStaticMeshSourceMaterialNameIssues(
            IReadOnlyList<CustomStaticMeshMaterialSlot> slots)
    {
        var missing = slots
            .Where(slot => string.IsNullOrWhiteSpace(slot.SourceMaterialName))
            .Select(slot => slot.Slot)
            .OrderBy(slot => slot)
            .ToList();
        var duplicates = slots
            .Where(slot => !string.IsNullOrWhiteSpace(slot.SourceMaterialName))
            .GroupBy(slot => slot.SourceMaterialName.Trim(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        return (missing, duplicates);
    }

    private static IReadOnlyList<CustomStaticMeshMaterialSlot> EffectiveCustomStaticMeshMaterialSlots(
        CustomStaticMeshImport custom)
    {
        if (custom.MaterialSlots is { Count: > 0 })
        {
            // Do not repair, deduplicate, or renumber declarations during validation. Returning
            // their raw numeric identities is what lets the checks above report corrupt JSON.
            return custom.MaterialSlots
                .Where(slot => slot is not null)
                .OrderBy(slot => slot.Slot)
                .ToList();
        }

        return
        [
            new CustomStaticMeshMaterialSlot
            {
                Slot = 0,
                SourceMaterialName = "Default",
                StableSlotName = "",
                MaterialPath = custom.MaterialPath ?? "",
            },
        ];
    }

    private static string EffectiveCustomStaticMeshDeclaredMaterial(
        CustomStaticMeshImport custom,
        CustomStaticMeshMaterialSlot slot)
    {
        var path = slot.MaterialPath;
        if (string.IsNullOrWhiteSpace(path) && slot.Slot == 0)
        {
            path = custom.MaterialPath;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            path = CustomStaticMeshImportService.DefaultMaterialPackagePath;
        }
        return UnrealPathUtil.NormalizePackagePath(path);
    }

    private static string CustomStaticMeshSlotLabel(CustomStaticMeshMaterialSlot slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.SourceMaterialName))
        {
            return slot.SourceMaterialName.Trim();
        }
        if (!string.IsNullOrWhiteSpace(slot.StableSlotName))
        {
            return slot.StableSlotName.Trim();
        }
        return "unnamed";
    }

    private static bool CustomStaticMeshMaterialAssignmentMatches(
        string? assignmentComponent,
        string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName))
        {
            return false;
        }
        return NormalizeCustomStaticMeshComponent(assignmentComponent)
            .Equals(componentName, StringComparison.OrdinalIgnoreCase);
    }

    private void CheckProjectOwnedCustomStaticMeshSource(
        NativeSuitProject project,
        CustomStaticMeshImport custom,
        string display,
        List<Finding> findings)
    {
        if (string.IsNullOrWhiteSpace(custom.SourceObjRelativePath))
        {
            findings.Add(new("ERROR",
                $"Custom static mesh '{display}' has no project-owned OBJ source. Import the OBJ again before packaging so later rebuilds remain possible."));
            return;
        }

        try
        {
            if (!TryResolveProjectOwnedCustomMeshSource(
                    _projectRoot,
                    project,
                    custom.SourceObjRelativePath,
                    out var sourcePath))
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' has an OBJ path outside its suit project. Import the OBJ again so the source is portable and safe to rebuild."));
                return;
            }
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length <= 0)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' is missing its project-owned OBJ source '{custom.SourceObjRelativePath}'. Restore or re-import that OBJ before packaging."));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR",
                $"Custom static mesh '{display}' has an invalid project-owned OBJ path: {ex.Message}"));
        }
    }

    private static bool TryResolveProjectOwnedCustomMeshSource(
        string projectRoot,
        NativeSuitProject project,
        string relativePath,
        out string sourcePath)
    {
        var projectDirectory = Path.GetFullPath(
            new SuitProjectService(projectRoot).ProjectOutputDirectory(project));
        sourcePath = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
        return FileSystemPathUtil.IsWithinDirectory(sourcePath, projectDirectory);
    }

    private void CheckGeneratedCustomStaticMeshPackage(
        string meshPackage,
        string display,
        IReadOnlyList<CustomStaticMeshMaterialSlot> expectedMaterialSlots,
        List<Finding> findings)
    {
        var uasset = PackagePathToBasePath(meshPackage) + ".uasset";
        try
        {
            var generated = new UAsset(
                uasset,
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var expectedName = UnrealPathUtil.AssetName(meshPackage);
            var expectedExport = generated.Exports.OfType<NormalExport>().FirstOrDefault(export =>
                export.ObjectName.ToString().Equals(expectedName, StringComparison.OrdinalIgnoreCase));
            var exportClass = expectedExport?.GetExportClassType().Value?.ToString() ?? "";
            if (expectedExport is null ||
                !exportClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase) ||
                exportClass.Contains("Component", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' package '{meshPackage}' does not contain the expected StaticMesh export '{expectedName}'. Rebuild the OBJ before packaging."));
                return;
            }

            try
            {
                StaticMeshObjProbeService.ValidateStaticMaterialSlots(
                    expectedExport,
                    expectedMaterialSlots);
            }
            catch (Exception ex)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' package '{meshPackage}' has unsafe StaticMaterials metadata: {ex.Message} Rebuild the OBJ before packaging."));
            }

            try
            {
                StaticMeshObjProbeService.ValidateCookedMaterialSections(
                    expectedExport,
                    expectedMaterialSlots);
            }
            catch (Exception ex)
            {
                findings.Add(new("ERROR",
                    $"Custom static mesh '{display}' package '{meshPackage}' has an unsafe cooked LOD0 section table: {ex.Message} Rebuild the OBJ before packaging."));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR",
                $"Custom static mesh '{display}' package '{meshPackage}' failed to reopen and is unsafe to package: {ex.Message}"));
        }
    }

    private void CheckCustomStaticMeshMaterialFiles(
        string materialPackage,
        string display,
        List<Finding> findings)
    {
        if (!ValidExpectedPackage(materialPackage))
        {
            findings.Add(new("ERROR",
                $"Custom static mesh '{display}' has invalid material package '{materialPackage}'. Choose a /Game material and rebuild it."));
            return;
        }
        if (!materialPackage.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var missingMaterialFiles = MissingNonEmptyPackageFiles(
            _contentRoot,
            materialPackage,
            ".uasset",
            ".uexp");
        if (missingMaterialFiles.Count > 0)
        {
            findings.Add(new("ERROR",
                $"Custom static mesh '{display}' points at generated material '{materialPackage}', but its complete cooked pair is missing ({string.Join(", ", missingMaterialFiles)}). Recreate or reassign the material before packaging."));
        }
    }

    /// <summary>
    /// CustomMesh_* components are authored by Batcomputer through a cross-package static-shell
    /// clone. Older stages could retain donor FName indexes in nested BodyInstance data and in the
    /// cutscene SCS parent-owner field. Those indexes resolve to arbitrary names in the target
    /// package and can crash as soon as the suit preview constructs the component. Only inspect
    /// live SCS fields so a deliberately removed custom mesh's orphan exports do not block builds.
    /// </summary>
    private static void CheckCustomStaticMeshComponentIntegrity(
        UAsset asset,
        string role,
        NativeSuitProject project,
        List<Finding> findings)
    {
        var declarations = (project.CustomStaticMeshes ?? []).Where(custom => custom is not null).ToList();
        var liveComponents = LiveScsComponentNames(asset);
        var declaredComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var custom in declarations)
        {
            var display = CustomStaticMeshLabel(custom);
            var componentName = NormalizeCustomStaticMeshComponent(
                CustomStaticMeshImportService.ComponentNameFor(custom));
            if (string.IsNullOrWhiteSpace(componentName))
            {
                findings.Add(new("ERROR",
                    $"{role}: custom static mesh '{display}' has no valid resolved component. Remove it and import the OBJ again."));
                continue;
            }
            declaredComponents.Add(componentName);

            void Error(string message) => findings.Add(new(
                "ERROR",
                $"{role}: custom static component '{componentName}' {message} " +
                "Rebuild the custom mesh from Parts before packaging."));

            if (!liveComponents.Contains(componentName))
            {
                Error("is declared by the suit but is not live in the SimpleConstructionScript.");
                continue;
            }

            var matchingLiveNodes = LiveScsNodeExportIndices(asset)
                .Select(index => asset.Exports[index - 1])
                .OfType<NormalExport>()
                .Where(export =>
                    export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase) &&
                    export.Data.OfType<NamePropertyData>().Any(property =>
                        property.Name.ToString().Equals("InternalVariableName", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ToString().Equals(componentName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var node = matchingLiveNodes.Count == 1 ? matchingLiveNodes[0] : null;
            var templateIndex = node is null
                ? 0
                : FindObjectProperty(node.Data, "ComponentTemplate")?.Value.Index ?? 0;
            var templateExportName = templateIndex > 0 && templateIndex <= asset.Exports.Count
                ? asset.Exports[templateIndex - 1].ObjectName.ToString()
                : "";
            if (node is null ||
                !CustomStaticComponentTemplateBindingMatches(
                    matchingLiveNodes.Count,
                    templateIndex,
                    asset.Exports.Count,
                    templateExportName,
                    componentName) ||
                asset.Exports[templateIndex - 1] is not NormalExport component)
            {
                Error(matchingLiveNodes.Count switch
                {
                    0 => "is marked live but has no matching live SCS node.",
                    > 1 => "has more than one matching live SCS node.",
                    _ => "has a missing, invalid, or misdirected SCS ComponentTemplate binding.",
                });
                continue;
            }

            var hasSyntheticClassField = asset.Exports.OfType<ClassExport>()
                .SelectMany(classExport => classExport.LoadedProperties)
                .Any(property => property.Name.ToString().Equals(
                    componentName,
                    StringComparison.OrdinalIgnoreCase));
            if (hasSyntheticClassField)
            {
                Error("also appears as a synthetic reflected Blueprint class field. " +
                      "That changes the schema of an opaque cooked class-default object and can crash during startup preload.");
            }

            var componentClass = component.GetExportClassType().Value?.ToString() ?? "";
            if (!componentClass.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase))
            {
                Error($"uses component class '{componentClass}' instead of StaticMeshComponent.");
                continue;
            }

            if (!PartGraftService.TryValidateCanonicalStaticShellBodyInstance(component, out var bodyError))
            {
                Error("has malformed or foreign BodyInstance names: " + bodyError);
            }

            string meshPackage;
            try
            {
                meshPackage = DeclaredCustomStaticMeshPackage(project, custom);
            }
            catch (Exception ex)
            {
                Error("has an invalid saved mesh package: " + ex.Message);
                continue;
            }
            var expectedMeshObject = UnrealPathUtil.ObjectPath(meshPackage);
            var actualMesh = FindObjectProperty(component.Data, "StaticMesh");
            if (actualMesh is null || actualMesh.Value.IsNull() ||
                !ObjectIdentityMatches(asset, actualMesh.Value, expectedMeshObject))
            {
                Error($"does not reference its declared mesh '{expectedMeshObject}' " +
                      $"(found '{ObjectName(asset, actualMesh?.Value ?? FPackageIndex.FromRawIndex(0))}').");
            }

            var expectedMaterialSlots = EffectiveCustomStaticMeshMaterialSlots(custom);
            var materials = MaterialObjectProperties(component);
            if (materials.Count != expectedMaterialSlots.Count)
            {
                Error($"has {materials.Count} OverrideMaterials entry/entries, but its declaration requires exactly {expectedMaterialSlots.Count}.");
            }

            foreach (var materialSlot in expectedMaterialSlots.OrderBy(slot => slot.Slot))
            {
                var fallbackMaterial = EffectiveCustomStaticMeshDeclaredMaterial(custom, materialSlot);
                var expectedMaterial = UnrealPathUtil.NormalizePackagePath(
                    FinalMaterialPackage(
                        project,
                        role,
                        componentName,
                        materialSlot.Slot,
                        fallbackMaterial));
                if (!ValidExpectedPackage(expectedMaterial))
                {
                    Error($"has invalid effective material '{expectedMaterial}' for slot {materialSlot.Slot} ({CustomStaticMeshSlotLabel(materialSlot)}).");
                    continue;
                }

                var material = materialSlot.Slot >= 0 && materialSlot.Slot < materials.Count
                    ? materials[materialSlot.Slot]
                    : null;
                if (material is null || material.Value.IsNull() ||
                    !ObjectIdentityMatches(
                        asset,
                        material.Value,
                        UnrealPathUtil.ObjectPath(expectedMaterial)))
                {
                    var actualMaterial = material is null || material.Value.IsNull()
                        ? "<missing>"
                        : ObjectPackagePath(asset, material.Value);
                    Error($"uses slot {materialSlot.Slot} ({CustomStaticMeshSlotLabel(materialSlot)}) material '{actualMaterial}' instead of the declared effective material '{expectedMaterial}'.");
                }
            }

            var expectedSocket = CustomStaticMeshImportService.ResolveAttachmentSlot(
                custom.Target,
                custom.AttachSocket).AttachSocket;
            var attachProperty = node.Data.OfType<NamePropertyData>().FirstOrDefault(property =>
                property.Name.ToString().Equals("AttachToName", StringComparison.OrdinalIgnoreCase));
            var actualSocket = attachProperty?.Value.ToString() ?? "";
            // A root attachment can legitimately omit AttachToName entirely. Any authored
            // non-root socket, or a present-but-different value, must still match exactly.
            if ((attachProperty is not null ||
                 !expectedSocket.Equals("Root", StringComparison.OrdinalIgnoreCase)) &&
                !actualSocket.Equals(expectedSocket, StringComparison.OrdinalIgnoreCase))
            {
                Error($"attaches to '{actualSocket}' instead of declared socket '{expectedSocket}'.");
            }

            if (role.Equals("cutscene", StringComparison.OrdinalIgnoreCase) &&
                !PartGraftService.TryValidateCutsceneParentOwner(node, out var ownerError))
            {
                Error("has an invalid SCS ParentComponentOwnerClassName: " + ownerError);
            }
        }

        foreach (var staleComponent in liveComponents.Where(component =>
                     component.StartsWith("CustomMesh_", StringComparison.OrdinalIgnoreCase) &&
                     !declaredComponents.Contains(NormalizeCustomStaticMeshComponent(component))))
        {
            findings.Add(new("ERROR",
                $"{role}: live custom static component '{staleComponent}' has no saved CustomStaticMeshes declaration. Rebuild the suit from its declarative Parts list before packaging."));
        }
    }

    private static bool CustomStaticComponentTemplateBindingMatches(
        int matchingLiveNodeCount,
        int componentTemplateIndex,
        int exportCount,
        string? componentTemplateExportName,
        string expectedComponentName) =>
        matchingLiveNodeCount == 1 &&
        componentTemplateIndex > 0 &&
        componentTemplateIndex <= exportCount &&
        NormalizeCustomStaticMeshComponent(componentTemplateExportName)
            .Equals(
                NormalizeCustomStaticMeshComponent(expectedComponentName),
                StringComparison.OrdinalIgnoreCase);

    private static string CustomStaticMeshLabel(CustomStaticMeshImport custom) =>
        string.IsNullOrWhiteSpace(custom.DisplayName)
            ? (string.IsNullOrWhiteSpace(custom.Id) ? "unnamed import" : custom.Id.Trim())
            : custom.DisplayName.Trim();

    private static string DeclaredCustomStaticMeshPackage(
        NativeSuitProject project,
        CustomStaticMeshImport custom) =>
        UnrealPathUtil.NormalizePackagePath(
            CustomStaticMeshImportService.MeshPackagePathFor(project, custom));

    private static IReadOnlyList<string> DuplicateCustomStaticMeshDeclarationKeys(
        NativeSuitProject project,
        IEnumerable<CustomStaticMeshImport> declarations)
    {
        var identities = declarations.Select(custom => new
        {
            Id = NormalizeCustomStaticMeshId(custom.Id),
            Component = NormalizeCustomStaticMeshComponent(
                CustomStaticMeshImportService.ComponentNameFor(custom)),
            MeshPackage = TryDeclaredCustomStaticMeshPackage(project, custom),
        }).ToList();
        var duplicates = new List<string>();

        AddDuplicateKeys(identities.Select(identity => identity.Id), "saved ID", duplicates);
        AddDuplicateKeys(identities.Select(identity => identity.Component), "resolved component", duplicates);
        AddDuplicateKeys(identities.Select(identity => identity.MeshPackage), "mesh package", duplicates);
        return duplicates;
    }

    private static void AddDuplicateKeys(
        IEnumerable<string> values,
        string label,
        List<string> duplicates)
    {
        duplicates.AddRange(values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{label} '{group.Key}'"));
    }

    private static string TryDeclaredCustomStaticMeshPackage(
        NativeSuitProject project,
        CustomStaticMeshImport custom)
    {
        try
        {
            return DeclaredCustomStaticMeshPackage(project, custom);
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeCustomStaticMeshId(string? id)
    {
        var token = new string((id ?? "").Where(char.IsLetterOrDigit).ToArray());
        return token.Length > 24 ? token[..24] : token;
    }

    private static string NormalizeCustomStaticMeshComponent(string? component)
    {
        var normalized = (component ?? "").Trim();
        var colon = normalized.IndexOf(':');
        if (colon >= 0)
        {
            normalized = normalized[..colon].Trim();
        }
        const string generatedSuffix = "_GEN_VARIABLE";
        if (normalized.EndsWith(generatedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^generatedSuffix.Length];
        }
        return normalized;
    }

    private static IReadOnlyList<string> MissingNonEmptyPackageFiles(
        string contentRoot,
        string packagePath,
        params string[] extensions)
    {
        string packageBase;
        try
        {
            packageBase = PackagePathToBasePath(contentRoot, packagePath);
        }
        catch
        {
            return extensions.ToList();
        }

        return extensions
            .Where(extension =>
            {
                var path = packageBase + extension;
                return !File.Exists(path) || new FileInfo(path).Length <= 0;
            })
            .ToList();
    }

    internal static bool CustomStaticMeshDeclarationIdentityRegressionPasses()
    {
        var project = new NativeSuitProject
        {
            TargetPackages = new TargetPackages
            {
                Playable = "/Game/Mods/IdentityFixture/Characters/BP_Fixture_Playable",
            },
        };
        var distinct = new[]
        {
            new CustomStaticMeshImport
            {
                Id = "cowl-one",
                ResolvedComponent = "CustomMesh_CowlOne",
                MeshPackagePath = "/Game/Mods/IdentityFixture/Meshes/SM_Custom_CowlOne",
            },
            new CustomStaticMeshImport
            {
                Id = "belt-two",
                ResolvedComponent = "CustomMesh_BeltTwo",
                MeshPackagePath = "/Game/Mods/IdentityFixture/Meshes/SM_Custom_BeltTwo",
            },
        };
        var duplicate = new[]
        {
            distinct[0],
            new CustomStaticMeshImport
            {
                Id = "COWL ONE",
                ResolvedComponent = "custommesh_cowlone_GEN_VARIABLE",
                MeshPackagePath = "/game/mods/IdentityFixture/Meshes/SM_Custom_CowlOne",
            },
        };
        return DuplicateCustomStaticMeshDeclarationKeys(project, distinct).Count == 0 &&
               DuplicateCustomStaticMeshDeclarationKeys(project, duplicate).Count == 3;
    }

    internal static bool CustomStaticMeshSourceMaterialNameRegressionPasses()
    {
        var valid = CustomStaticMeshSourceMaterialNameIssues(
        [
            new CustomStaticMeshMaterialSlot { Slot = 0, SourceMaterialName = "Black Plastic" },
            new CustomStaticMeshMaterialSlot { Slot = 1, SourceMaterialName = "Metal" },
        ]);
        var missing = CustomStaticMeshSourceMaterialNameIssues(
        [
            new CustomStaticMeshMaterialSlot { Slot = 0, SourceMaterialName = " " },
            new CustomStaticMeshMaterialSlot { Slot = 1, SourceMaterialName = "Metal" },
        ]);
        var duplicate = CustomStaticMeshSourceMaterialNameIssues(
        [
            new CustomStaticMeshMaterialSlot { Slot = 0, SourceMaterialName = "Metal" },
            new CustomStaticMeshMaterialSlot { Slot = 1, SourceMaterialName = "Metal" },
        ]);
        return valid.MissingSlots.Count == 0 && valid.DuplicateNames.Count == 0 &&
               missing.MissingSlots.SequenceEqual([0]) && missing.DuplicateNames.Count == 0 &&
               duplicate.MissingSlots.Count == 0 && duplicate.DuplicateNames.SequenceEqual(["Metal"]);
    }

    internal static bool RequiredPackageFilesAreNonEmptyForTest(
        string contentRoot,
        string packagePath,
        params string[] extensions) =>
        MissingNonEmptyPackageFiles(contentRoot, packagePath, extensions).Count == 0;

    internal static bool ProjectOwnedCustomMeshSourcePathIsSafeForTest(
        string projectRoot,
        NativeSuitProject project,
        string relativePath) =>
        TryResolveProjectOwnedCustomMeshSource(
            projectRoot,
            project,
            relativePath,
            out _);

    internal static string ResolveValidationProjectRoot(
        string projectJsonPath,
        string? explicitProjectRoot,
        string fallbackProjectRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitProjectRoot))
        {
            return Path.GetFullPath(explicitProjectRoot);
        }

        try
        {
            var current = new DirectoryInfo(Path.GetDirectoryName(
                Path.GetFullPath(projectJsonPath))!);
            while (current is not null)
            {
                if (current.Name.Equals("NativeSuitGuiProjects", StringComparison.OrdinalIgnoreCase) &&
                    current.Parent is { } generated &&
                    (generated.Name.Equals(AppSettings.GeneratedFolderName, StringComparison.OrdinalIgnoreCase) ||
                     generated.Name.Equals("_generated", StringComparison.OrdinalIgnoreCase)) &&
                    generated.Parent is { } workspace)
                {
                    return workspace.FullName;
                }
                current = current.Parent;
            }
        }
        catch
        {
            // The caller's configured workspace remains a deterministic fallback for malformed
            // or noncanonical archived project paths.
        }

        return Path.GetFullPath(fallbackProjectRoot);
    }

    internal static bool ValidationProjectRootResolutionRegressionPasses()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "Batcomputer-validation-root-fixture");
        var canonicalWorkspace = Path.Combine(fixtureRoot, "CanonicalWorkspace");
        var projectJson = Path.Combine(
            canonicalWorkspace,
            "_generated",
            "NativeSuitGuiProjects",
            "fixture.native-suit-project.json");
        var explicitWorkspace = Path.Combine(fixtureRoot, "ExplicitWorkspace");
        var fallbackWorkspace = Path.Combine(fixtureRoot, "FallbackWorkspace");
        var noncanonicalProject = Path.Combine(fixtureRoot, "Archive", "fixture.json");
        return ResolveValidationProjectRoot(projectJson, null, fallbackWorkspace)
                   .Equals(Path.GetFullPath(canonicalWorkspace), StringComparison.OrdinalIgnoreCase) &&
               ResolveValidationProjectRoot(projectJson, explicitWorkspace, fallbackWorkspace)
                   .Equals(Path.GetFullPath(explicitWorkspace), StringComparison.OrdinalIgnoreCase) &&
               ResolveValidationProjectRoot(noncanonicalProject, null, fallbackWorkspace)
                   .Equals(Path.GetFullPath(fallbackWorkspace), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CustomStaticComponentTemplateBindingRegressionPasses() =>
        CustomStaticComponentTemplateBindingMatches(
            1,
            60,
            61,
            "CustomMesh_Cowl_GEN_VARIABLE",
            "CustomMesh_Cowl") &&
        !CustomStaticComponentTemplateBindingMatches(
            1,
            0,
            61,
            "",
            "CustomMesh_Cowl") &&
        !CustomStaticComponentTemplateBindingMatches(
            1,
            62,
            61,
            "CustomMesh_Cowl_GEN_VARIABLE",
            "CustomMesh_Cowl") &&
        !CustomStaticComponentTemplateBindingMatches(
            1,
            60,
            61,
            "CustomMesh_Other_GEN_VARIABLE",
            "CustomMesh_Cowl") &&
        !CustomStaticComponentTemplateBindingMatches(
            2,
            60,
            61,
            "CustomMesh_Cowl_GEN_VARIABLE",
            "CustomMesh_Cowl");

    internal static string EffectiveCustomStaticMeshMaterialForTest(
        NativeSuitProject project,
        string role,
        string component,
        string fallback) =>
        FinalMaterialPackage(project, role, component, 0, fallback);

    internal static bool RejectsMalformedCustomStaticMetadataForTest()
    {
        var malformedComponent = new NormalExport { Data = [] };
        var malformedNode = new NormalExport { Data = [] };

        return !PartGraftService.TryValidateCanonicalStaticShellBodyInstance(malformedComponent, out _) &&
               !PartGraftService.TryValidateCutsceneParentOwner(malformedNode, out _);
    }

    /// <summary>
    /// Glider safety is independent of the equipment list. Keeping these checks outside
    /// <see cref="CheckEquipmentDependencies"/> ensures a suit with no gadgets cannot
    /// bypass the package-blocking cape/glider compatibility rules.
    /// </summary>
    private void CheckGliderDependencies(
        NativeSuitProject project,
        IReadOnlyDictionary<string, UAsset> characterAssets,
        List<Finding> findings)
    {
        if (project.PartGrafts.Any(graft => graft.IsGlider) &&
            (!string.IsNullOrWhiteSpace(project.GliderAnimLas) || !string.IsNullOrWhiteSpace(project.GliderAnimMas)) &&
            !project.UseCustomArchetype)
        {
            findings.Add(new("ERROR",
                "This glider needs a donor glide pose, but the custom archetype is off so its animation sets cannot be injected. Re-apply the glider preset."));
        }

        var capeGlideContract = new AnimArchetypeGraftService().BaseCapeGlideContract(project);
        CheckCapeGliderContract(project, capeGlideContract, characterAssets, findings);

        if (GliderService.IsDeclaredPairedCapeAdapterValid(
                project,
                capeGlideContract,
                requireResolvedComponents: true,
                out _))
        {
            CheckPairedCapeAdapterShell(project, characterAssets, findings);
        }
    }

    /// <summary>
    /// The current adapter deliberately starts from a Blueprint that already owns exact
    /// Cape + Torso fields. Verify that the final stage kept that cooked schema and that its
    /// mod-local archetype kept the authored shell's binary layout while only swapping the
    /// gameplay behavior references. These checks guard the malformed synthetic-field crash
    /// that the first Nightwing acceptance package exposed.
    /// </summary>
    private void CheckPairedCapeAdapterShell(
        NativeSuitProject project,
        IReadOnlyDictionary<string, UAsset> characterAssets,
        List<Finding> findings)
    {
        var adapter = project.PairedCapeAdapter!;
        if (!adapter.ResolvedCosmeticComponent.Equals("Cape", StringComparison.OrdinalIgnoreCase) ||
            !adapter.ResolvedGliderComponent.Equals("Torso", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new("ERROR",
                "The paired-cape adapter did not resolve to the authored Cape + Torso topology. " +
                $"It resolved '{adapter.ResolvedCosmeticComponent}' + '{adapter.ResolvedGliderComponent}' instead."));
        }

        var customArchetype = UAssetPatchService.CustomArchetypePackage(project);
        if (string.IsNullOrWhiteSpace(customArchetype))
        {
            findings.Add(new("ERROR",
                "The paired-cape adapter has no mod-local authored-shell archetype. Re-apply the adapter."));
            return;
        }

        if (!GliderService.TryGetAuthoredPairedCapeShell(
                project,
                out var shellPlayable,
                out var shellCutscene,
                out var shellDetail))
        {
            findings.Add(new("ERROR",
                "The paired-cape authored shell could not be resolved during final validation: " + shellDetail));
            return;
        }

        UAsset sourcePlayable;
        UAsset sourceCutscene;
        try
        {
            sourcePlayable = new UAsset(
                PackagePathToBasePath(
                    AppSettings.Current.EffectiveExtractedContentRoot(),
                    shellPlayable) + ".uasset",
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            sourceCutscene = new UAsset(
                PackagePathToBasePath(
                    AppSettings.Current.EffectiveExtractedContentRoot(),
                    shellCutscene) + ".uasset",
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR",
                "The authored paired-cape playable/cutscene shell could not be read: " + ex.Message));
            return;
        }

        foreach (var (role, asset) in characterAssets)
        {
            var hasSyntheticCape = asset.Exports.Any(export =>
                    export.ObjectName.ToString().Equals("Cape_2_GEN_VARIABLE", StringComparison.OrdinalIgnoreCase)) ||
                asset.Exports.OfType<ClassExport>().Any(classExport =>
                    classExport.LoadedProperties.Any(property =>
                        property.Name.ToString().Equals("Cape_2", StringComparison.OrdinalIgnoreCase)));
            if (hasSyntheticCape)
            {
                findings.Add(new("ERROR",
                    $"{role}: the paired-cape adapter contains a synthetic Cape_2 field. " +
                    "Only the authored Cape + Torso fields are safe for this adapter."));
            }

            var targetPackage = role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                ? project.TargetPackages.Playable
                : project.TargetPackages.Cutscene;
            var hasSafeParent = role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                ? IsGeneratedClassParentedToPackage(asset, targetPackage, customArchetype)
                : HasSameGeneratedClassParent(
                    asset,
                    targetPackage,
                    sourceCutscene,
                    shellCutscene);
            if (!hasSafeParent)
            {
                findings.Add(new("ERROR",
                    role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                        ? $"{role}: the authored paired-cape shell is not parented to its final mod-local archetype '{customArchetype}'."
                        : $"{role}: the authored paired-cape shell no longer preserves the source cutscene superclass."));
            }

            // A component-template export can survive after its SCS node is removed from
            // RootNodes/AllNodes. Looking up that orphaned export made earlier builds appear
            // valid even though Unreal would never construct the cape shell's complete graph.
            // The adapter is certified against an authored Blueprint, so every one of that
            // Blueprint's live construction nodes must remain live. User additions may coexist.
            var sourceShell = role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                ? sourcePlayable
                : sourceCutscene;
            var requiredLiveComponents = LiveScsComponentNames(sourceShell);
            var actualLiveComponents = LiveScsComponentNames(asset);
            var missingLiveComponents = requiredLiveComponents
                .Where(component => !actualLiveComponents.Contains(component))
                .OrderBy(component => component, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missingLiveComponents.Count > 0)
            {
                findings.Add(new("ERROR",
                    $"{role}: the paired-cape authored shell has inactive construction node(s): " +
                    string.Join(", ", missingLiveComponents) + ". Rebuild the suit so the authored cape shell can be restored."));
            }
        }

        string sourceArchetype;
        try
        {
            sourceArchetype = UAssetPatchService.StageArchetypeDonorPackage(project);
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR",
                "The paired-cape authored shell's parent archetype could not be detected: " + ex.Message));
            return;
        }

        var generatedBase = PackagePathToBasePath(customArchetype);
        var sourceBase = PackagePathToBasePath(
            AppSettings.Current.EffectiveExtractedContentRoot(),
            sourceArchetype);
        var generatedUasset = generatedBase + ".uasset";
        var sourceUasset = sourceBase + ".uasset";
        if (!File.Exists(generatedUasset) || !File.Exists(sourceUasset))
        {
            findings.Add(new("ERROR",
                "The paired-cape archetype layout could not be compared because its generated or authored source package is missing."));
            return;
        }

        try
        {
            var generated = new UAsset(
                generatedUasset,
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var source = new UAsset(
                sourceUasset,
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);

            if (generated.Exports.Count != source.Exports.Count)
            {
                findings.Add(new("ERROR",
                    "The paired-cape mod-local archetype changed the authored shell's export layout " +
                    $"({source.Exports.Count} source exports, {generated.Exports.Count} generated exports)."));
            }

            var sourceFields = ClassFieldSignature(source);
            var generatedFields = ClassFieldSignature(generated);
            if (!sourceFields.SequenceEqual(generatedFields, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new("ERROR",
                    "The paired-cape mod-local archetype changed the authored shell's reflected class-field schema."));
            }

            var sourceUexp = sourceBase + ".uexp";
            var generatedUexp = generatedBase + ".uexp";
            if (!File.Exists(sourceUexp) || !File.Exists(generatedUexp) ||
                !File.ReadAllBytes(sourceUexp).SequenceEqual(File.ReadAllBytes(generatedUexp)))
            {
                findings.Add(new("ERROR",
                    "The paired-cape mod-local archetype changed the authored shell's cooked export payload. " +
                    "Only name-map behavior references may differ."));
            }

            var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var shellDonor = AnimArchetypeGraftService.DetectDonor(
                PackagePathToBasePath(extractedRoot, shellPlayable) + ".uasset",
                extractedRoot,
                _mappings);
            var gameplayDonor = AnimArchetypeGraftService.DetectDonorForProject(
                project,
                _contentRoot,
                _mappings);
            if (shellDonor is null || !shellDonor.Valid || gameplayDonor is null || !gameplayDonor.Valid)
            {
                findings.Add(new("ERROR",
                    "The paired-cape authored-shell/gameplay behavior bridge could not resolve both archetype donors."));
                return;
            }

            var mod = ExtractMod(project.TargetPackages.Playable);
            if (string.IsNullOrWhiteSpace(mod))
            {
                findings.Add(new("ERROR",
                    "The paired-cape animation bridge could not resolve its mod-local MAS/LAS package names."));
                return;
            }
            var generatedMas = $"/Game/Mods/{mod}/Characters/MAS_Char_{mod}";
            var generatedLas = $"/Game/Mods/{mod}/Characters/LAS_Char_{mod}";

            CheckBehaviorBridgeReference(
                generated,
                "character montage set",
                shellDonor.MasCharPackage,
                generatedMas,
                findings);
            CheckBehaviorBridgeReference(
                generated,
                "character layer set",
                shellDonor.LasCharPackage,
                generatedLas,
                findings);
            CheckPairedCapeAnimationBridge(
                project,
                gameplayDonor,
                generatedMas,
                generatedLas,
                findings);
            var expectedDprd = ExpectedPairedCapeDprdPackageForTest(
                project,
                mod,
                gameplayDonor.DprdPackage,
                gameplayDonor.Family);
            if (AnimArchetypeGraftService.RequiresGeneratedDprd(project, gameplayDonor.Family) &&
                !File.Exists(PackagePathToBasePath(expectedDprd) + ".uasset"))
            {
                findings.Add(new("ERROR",
                    $"The paired-cape suit declares equipment but its generated pawn runtime data '{expectedDprd}' is missing."));
            }
            CheckBehaviorBridgeReference(
                generated,
                "pawn runtime data",
                shellDonor.DprdPackage,
                expectedDprd,
                findings,
                gameplayDonor.DprdPackage);
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR",
                "The paired-cape authored-shell schema verification failed: " + ex.Message));
        }
    }

    private void CheckPairedCapeAnimationBridge(
        NativeSuitProject project,
        DonorInfo gameplayDonor,
        string generatedMasPackage,
        string generatedLasPackage,
        List<Finding> findings)
    {
        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        try
        {
            var sourceMas = new UAsset(
                PackagePathToBasePath(extractedRoot, gameplayDonor.MasCharPackage) + ".uasset",
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var sourceLas = new UAsset(
                PackagePathToBasePath(extractedRoot, gameplayDonor.LasCharPackage) + ".uasset",
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var generatedMas = new UAsset(
                PackagePathToBasePath(generatedMasPackage) + ".uasset",
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var generatedLas = new UAsset(
                PackagePathToBasePath(generatedLasPackage) + ".uasset",
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);

            CheckPairedCapeAnimationParents(
                "MAS_Char",
                ParentSetPackages(sourceMas),
                ParentSetPackages(generatedMas),
                project.PairedCapeAdapter!.GlideAnimMasPackage,
                "MAS_Glide_",
                findings);
            CheckPairedCapeAnimationParents(
                "LAS_Char",
                ParentSetPackages(sourceLas),
                ParentSetPackages(generatedLas),
                project.PairedCapeAdapter.GlideAnimLasPackage,
                "LAS_Traversal_",
                findings);
        }
        catch (Exception ex)
        {
            findings.Add(new("ERROR",
                "The paired-cape animation bridge could not compare the gameplay donor and generated MAS/LAS composites: " +
                ex.Message));
        }
    }

    private static void CheckPairedCapeAnimationParents(
        string label,
        IReadOnlyList<string> gameplayParents,
        IReadOnlyList<string> generatedParents,
        string certifiedPackage,
        string categoryPrefix,
        List<Finding> findings)
    {
        if (!PairedCapeAnimationParentsAreSafeForTest(
                gameplayParents,
                generatedParents,
                certifiedPackage,
                categoryPrefix,
                out var detail))
        {
            findings.Add(new("ERROR", $"The paired-cape {label} bridge is incomplete: {detail}"));
        }
    }

    internal static bool PairedCapeAnimationParentsAreSafeForTest(
        IReadOnlyList<string> gameplayParents,
        IReadOnlyList<string> generatedParents,
        string certifiedPackage,
        string categoryPrefix,
        out string detail)
    {
        static string Package(string value) => UnrealPathUtil.NormalizePackagePath(value?.Trim() ?? "");
        static string Stem(string value) => UnrealPathUtil.AssetName(value);
        static bool IsExactContentPackage(string value) =>
            ExtractedPackagePathService.IsContentPackagePath(value);

        var certified = Package(certifiedPackage);
        var certifiedStem = Stem(certified);
        if (!IsExactContentPackage(certified) ||
            !certifiedStem.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"the certified set '{certifiedPackage}' is missing or is not a {categoryPrefix} block";
            return false;
        }

        // Preserve entry cardinality. An empty value means a non-import, null, or an object import
        // whose outer chain did not resolve to an exact content Package; silently filtering it would
        // let malformed ParentSetsArray entries bypass the fail-closed certificate check.
        var source = gameplayParents.Select(Package).ToList();
        var generated = generatedParents.Select(Package).ToList();
        if (source.Any(parent => !IsExactContentPackage(parent)) ||
            generated.Any(parent => !IsExactContentPackage(parent)))
        {
            detail = "one or more ParentSetsArray entries could not be resolved to an exact game or installed DLC package";
            return false;
        }
        var sourceCategoryParents = source
            .Where(parent => Stem(parent).StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sourceCategoryParents.Count != 1)
        {
            detail =
                $"the gameplay donor exposes {sourceCategoryParents.Count} native {categoryPrefix} categories; exactly one is required for a safe replacement";
            return false;
        }

        var missingGameplayParents = source
            .Where(parent => !Stem(parent).StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(parent => !generated.Contains(parent, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingGameplayParents.Count > 0)
        {
            detail =
                "the generated composite dropped gameplay-donor parent(s): " +
                string.Join(", ", missingGameplayParents);
            return false;
        }

        var certifiedIndexes = generated
            .Select((parent, index) => (parent, index))
            .Where(item => item.parent.Equals(certified, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToList();
        if (certifiedIndexes.Count != 1)
        {
            detail = certifiedIndexes.Count == 0
                ? $"the certified block '{certified}' is absent from ParentSetsArray"
                : $"the certified block '{certified}' appears {certifiedIndexes.Count} times";
            return false;
        }

        var generatedCategoryParents = generated
            .Where(parent => Stem(parent).StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (generatedCategoryParents.Count != 1 ||
            !generatedCategoryParents[0].Equals(certified, StringComparison.OrdinalIgnoreCase))
        {
            detail =
                $"the generated composite retains competing {categoryPrefix} controller(s): " +
                string.Join(", ", generatedCategoryParents);
            return false;
        }

        var retainedNativeCategories = sourceCategoryParents
            .Where(parent => !parent.Equals(certified, StringComparison.OrdinalIgnoreCase))
            .Where(parent => generated.Contains(parent, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (retainedNativeCategories.Count > 0)
        {
            detail =
                "the generated composite retained the gameplay donor's competing native category: " +
                string.Join(", ", retainedNativeCategories);
            return false;
        }

        detail = "all non-glide gameplay parents remain and the native glide category is replaced by the one certified cape block";
        return true;
    }

    /// <summary>
    /// Resolves each ParentSetsArray object import to its outer package rather than trusting the
    /// leaf object name. Different packages can legally contain the same MAS/LAS stem; schema-3
    /// certificates bind the exact authored package and must reject such aliases.
    /// </summary>
    private static List<string> ParentSetPackages(UAsset asset)
    {
        var parentSets = asset.Exports.OfType<NormalExport>()
            .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
            .FirstOrDefault(property =>
                property.Name.ToString().Equals("ParentSetsArray", StringComparison.OrdinalIgnoreCase));
        if (parentSets is null)
        {
            return new List<string>();
        }
        return parentSets.Value
            .Select(item => item is ObjectPropertyData objectProperty &&
                            !objectProperty.Value.IsNull() &&
                            objectProperty.Value.IsImport()
                ? ImportOuterPackage(asset, objectProperty.Value)
                : "")
            .ToList();
    }

    private static string ImportOuterPackage(UAsset asset, FPackageIndex objectIndex)
    {
        if (!objectIndex.IsImport())
        {
            return "";
        }

        var import = objectIndex.ToImport(asset);
        var outer = import.OuterIndex;
        var remaining = asset.Imports.Count + 1;
        while (outer.IsImport() && remaining-- > 0)
        {
            var candidate = outer.ToImport(asset);
            var candidateName = UnrealPathUtil.NormalizePackagePath(candidate.ObjectName.ToString());
            if (candidate.ClassName.ToString().Equals("Package", StringComparison.OrdinalIgnoreCase) &&
                ExtractedPackagePathService.IsContentPackagePath(candidateName))
            {
                return candidateName;
            }
            outer = candidate.OuterIndex;
        }
        return "";
    }

    private static bool IsGeneratedClassParentedToPackage(
        UAsset asset,
        string generatedPackage,
        string parentPackage)
    {
        var generatedClassName = UnrealPathUtil.AssetName(generatedPackage) + "_C";
        var parentClassName = UnrealPathUtil.AssetName(parentPackage) + "_C";
        var generatedClass = asset.Exports.FirstOrDefault(export =>
            export.ObjectName.ToString().Equals(generatedClassName, StringComparison.OrdinalIgnoreCase));
        if (generatedClass is null || !generatedClass.SuperIndex.IsImport())
        {
            return false;
        }

        var parentClass = generatedClass.SuperIndex.ToImport(asset);
        if (!parentClass.ObjectName.ToString().Equals(parentClassName, StringComparison.OrdinalIgnoreCase) ||
            !parentClass.OuterIndex.IsImport())
        {
            return false;
        }

        return parentClass.OuterIndex.ToImport(asset).ObjectName.ToString().Equals(
            UnrealPathUtil.NormalizePackagePath(parentPackage),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameGeneratedClassParent(
        UAsset generated,
        string generatedPackage,
        UAsset source,
        string sourcePackage)
    {
        return TryGetGeneratedClassParent(
                   generated,
                   generatedPackage,
                   out var generatedParentClass,
                   out var generatedParentPackage) &&
               TryGetGeneratedClassParent(
                   source,
                   sourcePackage,
                   out var sourceParentClass,
                   out var sourceParentPackage) &&
               generatedParentClass.Equals(sourceParentClass, StringComparison.OrdinalIgnoreCase) &&
               generatedParentPackage.Equals(sourceParentPackage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetGeneratedClassParent(
        UAsset asset,
        string generatedPackage,
        out string parentClass,
        out string parentPackage)
    {
        parentClass = "";
        parentPackage = "";
        var generatedClassName = UnrealPathUtil.AssetName(generatedPackage) + "_C";
        var generatedClass = asset.Exports.FirstOrDefault(export =>
            export.ObjectName.ToString().Equals(generatedClassName, StringComparison.OrdinalIgnoreCase));
        if (generatedClass is null || !generatedClass.SuperIndex.IsImport())
        {
            return false;
        }

        var parent = generatedClass.SuperIndex.ToImport(asset);
        if (!parent.OuterIndex.IsImport())
        {
            return false;
        }

        parentClass = parent.ObjectName.ToString();
        parentPackage = parent.OuterIndex.ToImport(asset).ObjectName.ToString();
        return !string.IsNullOrWhiteSpace(parentClass) && !string.IsNullOrWhiteSpace(parentPackage);
    }

    private static List<string> ClassFieldSignature(UAsset asset) =>
        asset.Exports.OfType<ClassExport>()
            .SelectMany(classExport => classExport.LoadedProperties)
            .Select(property => property.Name + ":" + property.GetType().Name)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void CheckBehaviorBridgeReference(
        UAsset generatedArchetype,
        string label,
        string authoredPackage,
        string gameplayPackage,
        List<Finding> findings,
        string? additionallyForbiddenPackage = null)
    {
        authoredPackage = UnrealPathUtil.NormalizePackagePath(authoredPackage);
        gameplayPackage = UnrealPathUtil.NormalizePackagePath(gameplayPackage);
        if (string.IsNullOrWhiteSpace(authoredPackage) || string.IsNullOrWhiteSpace(gameplayPackage))
        {
            findings.Add(new("ERROR",
                $"The paired-cape behavior bridge has no resolvable {label} source or target."));
            return;
        }

        var references = generatedArchetype.Imports
            .Select((import, index) => (
                PackagePath: ImportOuterPackage(
                    generatedArchetype,
                    FPackageIndex.FromImport(index)),
                ObjectName: import.ObjectName.ToString()))
            .Where(reference => ExtractedPackagePathService.IsContentPackagePath(reference.PackagePath))
            .ToList();
        static bool HasExact(
            IEnumerable<(string PackagePath, string ObjectName)> values,
            string package) =>
            values.Any(reference =>
                reference.PackagePath.Equals(package, StringComparison.OrdinalIgnoreCase) &&
                reference.ObjectName.Equals(
                    UnrealPathUtil.AssetName(package),
                    StringComparison.OrdinalIgnoreCase));
        if (!HasExact(references, gameplayPackage))
        {
            findings.Add(new("ERROR",
                $"The paired-cape mod-local archetype does not reference the exact expected {label} package '{gameplayPackage}'."));
        }
        if (!authoredPackage.Equals(gameplayPackage, StringComparison.OrdinalIgnoreCase) &&
            HasExact(references, authoredPackage))
        {
            findings.Add(new("ERROR",
                $"The paired-cape mod-local archetype still references the authored shell's {label} '{authoredPackage}'."));
        }
        var additionallyForbidden = UnrealPathUtil.NormalizePackagePath(additionallyForbiddenPackage);
        if (!string.IsNullOrWhiteSpace(additionallyForbidden) &&
            !additionallyForbidden.Equals(gameplayPackage, StringComparison.OrdinalIgnoreCase) &&
            !additionallyForbidden.Equals(authoredPackage, StringComparison.OrdinalIgnoreCase) &&
            HasExact(references, additionallyForbidden))
        {
            findings.Add(new("ERROR",
                $"The paired-cape mod-local archetype still references superseded {label} '{additionallyForbidden}'."));
        }
    }

    internal static bool BehaviorBridgeReferencesAreSafeForTest(
        IEnumerable<(string PackagePath, string ObjectName)> importedReferences,
        string authoredPackage,
        string expectedPackage,
        string? additionallyForbiddenPackage = null)
    {
        var authored = UnrealPathUtil.NormalizePackagePath(authoredPackage);
        var expected = UnrealPathUtil.NormalizePackagePath(expectedPackage);
        var additionallyForbidden = UnrealPathUtil.NormalizePackagePath(additionallyForbiddenPackage);
        var references = importedReferences
            .Select(reference => (
                PackagePath: UnrealPathUtil.NormalizePackagePath(reference.PackagePath),
                ObjectName: reference.ObjectName?.Trim() ?? ""))
            .ToList();
        bool HasExact(string package) => references.Any(reference =>
            reference.PackagePath.Equals(package, StringComparison.OrdinalIgnoreCase) &&
            reference.ObjectName.Equals(
                UnrealPathUtil.AssetName(package),
                StringComparison.OrdinalIgnoreCase));
        return ExtractedPackagePathService.IsContentPackagePath(expected) &&
               HasExact(expected) &&
               (authored.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                !HasExact(authored)) &&
               (string.IsNullOrWhiteSpace(additionallyForbidden) ||
                additionallyForbidden.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                additionallyForbidden.Equals(authored, StringComparison.OrdinalIgnoreCase) ||
                !HasExact(additionallyForbidden));
    }

    internal static string ExpectedPairedCapeDprdPackageForTest(
        NativeSuitProject project,
        string mod,
        string gameplayDprdPackage,
        string? donorFamily) =>
        AnimArchetypeGraftService.RequiresGeneratedDprd(project, donorFamily)
            ? $"/Game/Mods/{UnrealPathUtil.SanitizeIdentifier(mod)}/Characters/DA_DPRD_{UnrealPathUtil.SanitizeIdentifier(mod)}"
            : UnrealPathUtil.NormalizePackagePath(gameplayDprdPackage);

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        return ExtractedPackagePathService.ResolvePackageBase(contentRoot, packagePath)
               ?? throw new InvalidOperationException(
                   $"The source package is not available in the active game or installed DLC extract: {packagePath}");
    }

    private static void CheckCapeGliderContract(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus capeGlideContract,
        IReadOnlyDictionary<string, UAsset> characterAssets,
        List<Finding> findings)
    {
        if (GliderService.HasAdditiveCapeAndGliderCombination(project, capeGlideContract))
        {
            findings.Add(new("ERROR",
                "This suit combines a custom static mesh attached to Cape with a glide visual. " +
                "Custom static meshes are additive components and are not controlled by the playable base's native cape/glider visibility wiring, " +
                "so the custom cape would remain visible while gliding. Remove the custom Cape attachment or the glider before packaging."));
        }
        else if (GliderService.HasCapeAndGliderCombination(project, capeGlideContract))
        {
            var replacementDriver = GliderService.ProjectHasReplacementGlider(project)
                ? GliderService.ProjectReplacementGliderDriver(project)
                : PairedCapeVisibilityDriver.PairedCapable;
            if (replacementDriver == PairedCapeVisibilityDriver.GlideOnly)
            {
                findings.Add(new("ERROR",
                    "This suit combines a regular Cape with a replacement glider whose animation blueprint is glide-only. " +
                    "It animates the glide visual but does not hide a separate regular Cape, so both would appear while gliding. " +
                    "Use an ABP_Cape_Glide visual (including Batgirl Party), or explicitly remove the regular Cape before packaging."));
            }
            else if (replacementDriver == PairedCapeVisibilityDriver.Unknown)
            {
                findings.Add(new("ERROR",
                    "This suit combines a regular Cape with a replacement glider whose paired-cape visibility driver could not be verified. " +
                    "Packaging is blocked conservatively to prevent a double-cape build. Re-apply a current indexed glider preset, " +
                    "use an ABP_Cape_Glide visual, or explicitly remove the regular Cape."));
            }
            else if (capeGlideContract == AnimArchetypeGraftService.CapeGlideContractStatus.Unknown)
            {
                findings.Add(new("ERROR",
                    "This suit combines a regular Cape with a glide visual, but Batcomputer could not inspect the playable base's cape visibility contract. " +
                    "Packaging is blocked until the character assets are refreshed and the base is verified."));
            }
            else if (capeGlideContract != AnimArchetypeGraftService.CapeGlideContractStatus.Paired)
            {
                var adapterDetail = "";
                if (capeGlideContract == AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly &&
                    GliderService.IsDeclaredPairedCapeAdapterValid(
                        project,
                        capeGlideContract,
                        requireResolvedComponents: true,
                        out adapterDetail))
                {
                    CheckPairedCapeAdapterAssets(project, characterAssets, findings);
                }
                else
                {
                    var reason = capeGlideContract == AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly &&
                                 project.PairedCapeAdapter is not null
                        ? " Adapter certificate rejected: " + adapterDetail
                        : "";
                    findings.Add(new("ERROR",
                        "This suit combines a regular cape with a glide visual, but its playable base does not natively own separate cosmetic-cape and glider components. " +
                        "That synthetic component layout is not runtime-proven and may crash or leave the regular cape visible while gliding. " +
                        "Use the dynamic adapter with a complete matching native cape pair, or choose a playable donor with the native two-cape visibility setup." +
                        reason));
                }
            }
        }
        else if (project.PairedCapeAdapter is not null)
        {
            findings.Add(new("ERROR",
                "A paired-cape adapter certificate remains on this suit, but its bound regular-cape/glide-cape pair is no longer active. " +
                "Re-apply both parts or clear the stale adapter before packaging."));
        }
    }

    private static void CheckPairedCapeAdapterAssets(
        NativeSuitProject project,
        IReadOnlyDictionary<string, UAsset> characterAssets,
        List<Finding> findings)
    {
        var adapter = project.PairedCapeAdapter;
        if (adapter is null)
        {
            findings.Add(new("ERROR", "The paired-cape adapter declaration disappeared before its cooked assets could be validated."));
            return;
        }
        var nativeGliderComponent = new AnimArchetypeGraftService()
            .BaseGlideVisualComponent(project);
        if (string.IsNullOrWhiteSpace(nativeGliderComponent) ||
            !string.Equals(nativeGliderComponent,
                adapter.NativeGliderComponent,
                StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new("ERROR",
                "The paired-cape adapter no longer targets the gameplay donor's original " +
                $"Glider-tagged component (expected '{nativeGliderComponent ?? "missing"}', " +
                $"certificate names '{adapter.NativeGliderComponent}')."));
            return;
        }

        var partGrafts = project.PartGrafts ?? [];
        var cosmetic = partGrafts.FirstOrDefault(graft => string.Equals(
            graft?.InstanceId,
            adapter.CosmeticCapeGraftInstanceId,
            StringComparison.OrdinalIgnoreCase));
        var glider = partGrafts.FirstOrDefault(graft => string.Equals(
            graft?.InstanceId,
            adapter.GlideCapeGraftInstanceId,
            StringComparison.OrdinalIgnoreCase));
        if (cosmetic?.Playable is null || cosmetic.Cutscene is null ||
            glider?.Playable is null || glider.Cutscene is null)
        {
            findings.Add(new("ERROR",
                "The paired-cape adapter could not resolve one complete playable/cutscene Cape + Torso graft pair while validating the cooked assets."));
            return;
        }

        foreach (var role in new[] { "playable", "cutscene" })
        {
            if (!characterAssets.TryGetValue(role, out var asset))
            {
                // TryLoad already emitted the missing/unreadable package error.
                continue;
            }

            try
            {
                var cosmeticDonor = role == "playable" ? cosmetic.Playable : cosmetic.Cutscene;
                var gliderDonor = role == "playable" ? glider.Playable : glider.Cutscene;
                CheckAdapterComponent(
                    asset,
                    project,
                    role,
                    adapter.ResolvedCosmeticComponent ?? "",
                    cosmeticDonor,
                    isGlider: false,
                    findings);
                CheckAdapterComponent(
                    asset,
                    project,
                    role,
                    adapter.ResolvedGliderComponent ?? "",
                    gliderDonor,
                    isGlider: true,
                    findings);
                CheckPairedCapeVisualOverlayAssets(project, asset, role, findings);
            }
            catch (Exception ex)
            {
                // A malformed legacy declaration must block the package. The packaging caller has
                // historically downgraded unexpected validation exceptions, so contain the fault
                // here and turn it into an explicit release-blocking finding.
                findings.Add(new("ERROR",
                    $"{role}: paired-cape cooked-asset validation could not complete safely ({ex.Message}). Rebuild the adapter and retry."));
            }
        }
    }

    private static void CheckPairedCapeVisualOverlayAssets(
        NativeSuitProject project,
        UAsset asset,
        string role,
        List<Finding> findings)
    {
        var overlay = project.PairedCapeAdapter?.VisualOverlay;
        if (overlay is null)
        {
            // The declaration validator already rejects this for every real saved project. The
            // only valid null case is the explicit in-memory release-regression fixture.
            return;
        }

        var overlayGrafts = overlay.ComponentGrafts ?? [];
        foreach (var slot in new[] { "Head", "Face" })
        {
            if (ProjectExplicitlyRemovesComponentSafe(project, slot))
            {
                CheckPairedCapeHiddenVisual(asset, role, slot, findings);
                continue;
            }
            if (HasLaterUserPartOverride(project, slot))
            {
                // Automatic overlay replay precedes user grafts/removals. The user's later
                // declaration is authoritative, so requiring Nightwing's original field here
                // would incorrectly block legitimate customization.
                continue;
            }

            var graft = overlayGrafts.FirstOrDefault(candidate =>
                string.Equals(candidate?.Slot, slot, StringComparison.OrdinalIgnoreCase));
            var donor = role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                ? graft?.Playable
                : graft?.Cutscene;
            if (donor is null)
            {
                findings.Add(new("ERROR",
                    $"{role}: paired-cape visual overlay is missing its certified {slot} donor recipe."));
                continue;
            }

            CheckVisualOverlayComponent(asset, project, role, slot, donor, findings);
        }

        if (!ProjectExplicitlyRemovesComponentSafe(project, "CharacterMesh0") &&
            !ProjectExplicitlyRemovesComponentSafe(project, "Mesh") &&
            !HasLaterUserPartOverride(project, "CharacterMesh0"))
        {
            var expectedBody = role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                ? overlay.PlayableBodyMaterialPackage
                : overlay.CutsceneBodyMaterialPackage;
            expectedBody = FinalMaterialPackage(project, role, "CharacterMesh0", 0, expectedBody);
            CheckComponentMaterialSlot(asset, role, "CharacterMesh0", 0, expectedBody, "visual-base body", findings);
        }
    }

    private static void CheckPairedCapeHiddenVisual(
        UAsset asset,
        string role,
        string componentName,
        List<Finding> findings)
    {
        var component = FindActiveComponentExport(asset, componentName);
        if (component is null)
        {
            findings.Add(new("ERROR",
                $"{role}: paired-cape visual '{componentName}' was removed by unlinking its authored construction node. " +
                "Rebuild the suit so Batcomputer can keep the safe shell and hide only its mesh."));
            return;
        }

        var meshReferences = component.Data
            .OfType<ObjectPropertyData>()
            .Where(property => ComponentRemoveService.IsVisualMeshProperty(property.Name.ToString()))
            .ToList();
        if (meshReferences.Count == 0 || meshReferences.Any(property => !property.Value.IsNull()))
        {
            findings.Add(new("ERROR",
                $"{role}: paired-cape visual '{componentName}' is declared hidden, but its authored component still has a mesh. " +
                "Rebuild the suit before packaging."));
        }
    }

    private static void CheckVisualOverlayComponent(
        UAsset asset,
        NativeSuitProject project,
        string role,
        string componentName,
        SavedPartGraftDonor donor,
        List<Finding> findings)
    {
        void Error(string message) => findings.Add(new("ERROR",
            $"{role}: paired-cape visual overlay component '{componentName}' {message}"));

        var component = FindActiveComponentExport(asset, componentName);
        if (component is null)
        {
            Error("is missing its existing component template export.");
            return;
        }

        var componentClass = component.GetExportClassType().Value?.ToString() ?? "";
        if (!string.Equals(componentClass, donor.TemplateComponentClass, StringComparison.OrdinalIgnoreCase))
        {
            Error($"uses class '{componentClass}' instead of certified visual-base class '{donor.TemplateComponentClass}'.");
        }

        var staticMesh = string.Equals(donor.MeshKind, "StaticMesh", StringComparison.OrdinalIgnoreCase);
        var mesh = staticMesh
            ? FindObjectProperty(component.Data, "StaticMesh")
            : FindObjectProperty(component.Data, "SkeletalMesh") ?? FindObjectProperty(component.Data, "SkinnedAsset");
        if (mesh is null || !ObjectIdentityMatches(asset, mesh.Value, donor.MeshObjectPath))
        {
            var kind = staticMesh ? "static" : "skeletal";
            Error($"does not reference the certified {kind} mesh '{donor.MeshObjectPath}'.");
        }

        var anim = FindObjectProperty(component.Data, "AnimClass");
        var expectedAnimPath = !string.IsNullOrWhiteSpace(donor.AnimClassObjectPath)
            ? donor.AnimClassObjectPath
            : !string.IsNullOrWhiteSpace(donor.AnimClassPackagePath) &&
              !string.IsNullOrWhiteSpace(donor.AnimClassObjectName)
                ? donor.AnimClassPackagePath + "." + donor.AnimClassObjectName
                : "";
        if (string.IsNullOrWhiteSpace(expectedAnimPath))
        {
            if (anim is not null && !anim.Value.IsNull())
            {
                Error($"retained AnimClass '{ObjectName(asset, anim.Value)}' although the visual-base donor has none.");
            }
        }
        else if (anim is null || !ObjectIdentityMatches(asset, anim.Value, expectedAnimPath))
        {
            Error($"does not reference the certified AnimClass '{expectedAnimPath}'.");
        }

        CheckExistingVisualFieldIntegrity(asset, componentName, component, donor, Error);

        var actualTags = component.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => string.Equals(
                property.Name.ToString(),
                "ComponentTags",
                StringComparison.OrdinalIgnoreCase))?.Value?
            .OfType<NamePropertyData>()
            .Select(value => value.Value.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList() ?? [];
        var expectedTags = (donor.ComponentTags ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (actualTags.Count != expectedTags.Count ||
            !actualTags.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(expectedTags))
        {
            Error("does not retain the visual-base donor's exact component-tag set.");
        }

        var actualMaterials = MaterialObjectProperties(component);
        var expectedMaterials = (donor.Materials ?? [])
            .Select(material => material?.PackagePath ?? "")
            .ToList();
        for (var slot = 0; slot < expectedMaterials.Count; slot++)
        {
            expectedMaterials[slot] = FinalMaterialPackage(
                project,
                role,
                componentName,
                slot,
                componentName.Equals("Face", StringComparison.OrdinalIgnoreCase) && slot == 0
                    ? role.Equals("playable", StringComparison.OrdinalIgnoreCase)
                        ? project.PairedCapeAdapter?.VisualOverlay?.PlayableFaceMaterialPackage ?? expectedMaterials[slot]
                        : project.PairedCapeAdapter?.VisualOverlay?.CutsceneFaceMaterialPackage ?? expectedMaterials[slot]
                    : expectedMaterials[slot]);
        }
        if (actualMaterials.Count != expectedMaterials.Count ||
            expectedMaterials.Where((expected, slot) =>
                    !ValidExpectedPackage(expected) ||
                    slot >= actualMaterials.Count ||
                    !ObjectPackageMatches(asset, actualMaterials[slot].Value, expected))
                .Any())
        {
            Error("does not retain the certified ordered material set after later user material overrides.");
        }
    }

    private static void CheckComponentMaterialSlot(
        UAsset asset,
        string role,
        string componentName,
        int slot,
        string expectedPackage,
        string label,
        List<Finding> findings)
    {
        var component = FindActiveComponentExport(asset, componentName);
        var materials = component is null ? [] : MaterialObjectProperties(component);
        if (component is null || slot < 0 || slot >= materials.Count ||
            !ValidExpectedPackage(expectedPackage) ||
            !ObjectPackageMatches(asset, materials[slot].Value, expectedPackage))
        {
            findings.Add(new("ERROR",
                $"{role}: paired-cape {label} material slot {slot} does not reference '{expectedPackage}'."));
        }
    }

    private static void CheckExistingVisualFieldIntegrity(
        UAsset asset,
        string componentName,
        NormalExport component,
        SavedPartGraftDonor donor,
        Action<string> error)
    {
        var node = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
            export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase) &&
            export.Data.OfType<NamePropertyData>().Any(property =>
                string.Equals(property.Name.ToString(), "InternalVariableName", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(property.Value.ToString(), componentName, StringComparison.OrdinalIgnoreCase)));
        if (node is null)
        {
            error("is not connected to the live SimpleConstructionScript node for that existing field.");
            return;
        }

        var componentIndex = asset.Exports.IndexOf(component) + 1;
        var nodeIndex = asset.Exports.IndexOf(node) + 1;
        var nodeTemplate = FindObjectProperty(node.Data, "ComponentTemplate");
        var scs = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
            string.Equals(export.ObjectName.ToString(), "SimpleConstructionScript_0", StringComparison.OrdinalIgnoreCase));
        if (componentIndex <= 0 || nodeTemplate?.Value.Index != componentIndex ||
            !node.CreateBeforeSerializationDependencies.Any(index => index.Index == componentIndex) ||
            scs is null ||
            !scs.CreateBeforeSerializationDependencies.Any(index => index.Index == nodeIndex))
        {
            error("has an incomplete live SCS node-to-template dependency chain.");
        }

        var classExport = asset.Exports.OfType<ClassExport>().FirstOrDefault();
        var classProperty = classExport?.LoadedProperties.OfType<FObjectProperty>().FirstOrDefault(property =>
            string.Equals(property.Name.ToString(), componentName, StringComparison.OrdinalIgnoreCase));
        if (classProperty is null || classProperty.PropertyClass.Index != component.ClassIndex.Index)
        {
            error("is missing its correctly typed existing Blueprint class field.");
        }
        if (!component.SerializationBeforeCreateDependencies.Any(index => index.Index == component.ClassIndex.Index) ||
            (!component.TemplateIndex.IsNull() &&
             !component.SerializationBeforeCreateDependencies.Any(index => index.Index == component.TemplateIndex.Index)))
        {
            error("is missing its component-class/archetype preload dependencies.");
        }

        var attachSocket = node.Data.OfType<NamePropertyData>().FirstOrDefault(property =>
            string.Equals(property.Name.ToString(), "AttachToName", StringComparison.OrdinalIgnoreCase))?.Value.ToString() ?? "";
        if (!string.Equals(attachSocket, donor.AttachSocket, StringComparison.OrdinalIgnoreCase))
        {
            error($"attaches to '{attachSocket}' instead of visual-base socket '{donor.AttachSocket}'.");
        }
        var parent = node.Data.OfType<NamePropertyData>().FirstOrDefault(property =>
            string.Equals(property.Name.ToString(), "ParentComponentOrVariableName", StringComparison.OrdinalIgnoreCase))?.Value.ToString() ?? "";
        if (!string.Equals(parent, donor.ParentComponentOrVariableName, StringComparison.OrdinalIgnoreCase))
        {
            error($"uses parent '{parent}' instead of visual-base parent '{donor.ParentComponentOrVariableName}'.");
        }
    }

    private static bool HasLaterUserPartOverride(NativeSuitProject project, string componentName)
    {
        var adapter = project.PairedCapeAdapter;
        var grafts = project.PartGrafts ?? [];
        return grafts.Any(graft =>
        {
            if (graft is null ||
                string.Equals(graft.InstanceId, adapter?.CosmeticCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(graft.InstanceId, adapter?.GlideCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var slot = graft.Slot ?? "";
            var resolved = graft.ResolvedComponent ?? "";
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                // ResolvedComponent records the field this graft actually changed. A Head_2 hair
                // or hat coexists with Head and must not suppress validation of the base Head.
                return componentName.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase)
                    ? resolved.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) ||
                      resolved.Equals("Mesh", StringComparison.OrdinalIgnoreCase)
                    : resolved.Equals(componentName, StringComparison.OrdinalIgnoreCase);
            }
            if (componentName.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase))
            {
                return slot.Equals("Body", StringComparison.OrdinalIgnoreCase) ||
                       slot.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase);
            }
            return slot.Equals(componentName, StringComparison.OrdinalIgnoreCase);
        });
    }

    internal static bool HasLaterUserPartOverrideForTest(NativeSuitProject project, string componentName) =>
        HasLaterUserPartOverride(project, componentName);

    private static bool ProjectExplicitlyRemovesComponentSafe(NativeSuitProject project, string componentName) =>
        (project.Requirements ?? []).Any(requirement =>
        {
            if (requirement is null ||
                !string.Equals(requirement.Kind, "remove-component", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var target = requirement.TargetComponent?.Trim() ?? "";
            var colon = target.LastIndexOf(':');
            if (colon > 0)
            {
                target = target[..colon];
            }
            return target.Equals(componentName, StringComparison.OrdinalIgnoreCase) ||
                   (componentName.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) &&
                    target.Equals("Mesh", StringComparison.OrdinalIgnoreCase));
        });

    private static string FinalMaterialPackage(
        NativeSuitProject project,
        string role,
        string componentName,
        int slot,
        string fallback)
    {
        var assignment = (project.MaterialAssignments ?? []).LastOrDefault(candidate =>
            candidate is not null &&
            candidate.Slot == slot &&
            MaterialComponentMatches(candidate.Component, componentName) &&
            (string.Equals(candidate.Context, "both", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(candidate.Context, role, StringComparison.OrdinalIgnoreCase)));
        return assignment?.MiPackagePath ?? fallback ?? "";
    }

    private static bool MaterialComponentMatches(string? left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return right.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left, "Mesh", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ObjectPropertyData> MaterialObjectProperties(NormalExport component) =>
        component.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => string.Equals(
                property.Name.ToString(),
                "OverrideMaterials",
                StringComparison.OrdinalIgnoreCase))?.Value?
            .OfType<ObjectPropertyData>()
            .ToList() ?? [];

    /// <summary>
    /// Returns only SCS fields that are referenced by a live construction array. Cooked removal
    /// intentionally leaves the SCS node and component-template exports behind, so export lookup
    /// alone cannot distinguish a constructed field from an orphan.
    /// </summary>
    internal static HashSet<string> LiveScsComponentNames(UAsset asset)
    {
        return LiveScsNodeExportIndices(asset)
            .Select(index => asset.Exports[index - 1])
            .OfType<NormalExport>()
            .Where(export => export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase))
            .Select(export => export.Data.OfType<NamePropertyData>().FirstOrDefault(property =>
                property.Name.ToString().Equals("InternalVariableName", StringComparison.OrdinalIgnoreCase))?.Value.ToString() ?? "")
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<int> LiveScsNodeExportIndices(UAsset asset) =>
        asset.Exports
            .OfType<NormalExport>()
            .SelectMany(export => export.Data.OfType<ArrayPropertyData>())
            .Where(property =>
                property.Name.ToString().Equals("RootNodes", StringComparison.OrdinalIgnoreCase) ||
                property.Name.ToString().Equals("AllNodes", StringComparison.OrdinalIgnoreCase) ||
                property.Name.ToString().Equals("ChildNodes", StringComparison.OrdinalIgnoreCase))
            .SelectMany(property => property.Value ?? Array.Empty<PropertyData>())
            .OfType<ObjectPropertyData>()
            .Select(property => property.Value.Index)
            .Where(index => index > 0 && index <= asset.Exports.Count)
            .ToHashSet();

    internal static bool AuthoredShellLiveComponentsRemainForTest(
        IEnumerable<string> required,
        IEnumerable<string> actual) =>
        required.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .IsSubsetOf(actual.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static NormalExport? FindActiveComponentExport(UAsset asset, string componentName)
    {
        foreach (var candidate in ComponentAliases(componentName))
        {
            var node = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
                export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase) &&
                export.Data.OfType<NamePropertyData>().Any(property =>
                    string.Equals(property.Name.ToString(), "InternalVariableName", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(property.Value.ToString(), candidate, StringComparison.OrdinalIgnoreCase)));
            var template = node is null ? null : FindObjectProperty(node.Data, "ComponentTemplate");
            if (template?.Value.IsExport() == true &&
                template.Value.Index > 0 && template.Value.Index <= asset.Exports.Count &&
                asset.Exports[template.Value.Index - 1] is NormalExport active)
            {
                return active;
            }

            var exact = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
                string.Equals(export.ObjectName.ToString(), candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(export.ObjectName.ToString(), candidate + "_GEN_VARIABLE", StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }
        return null;
    }

    private static IEnumerable<string> ComponentAliases(string componentName)
    {
        yield return componentName;
        if (componentName.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Mesh (CharacterMesh0)";
            yield return "Mesh";
        }
    }

    private static bool ObjectIdentityMatches(UAsset asset, FPackageIndex index, string expectedObjectPath)
    {
        return ObjectIdentityMatchesForTest(
            ObjectName(asset, index),
            ObjectPackagePath(asset, index),
            expectedObjectPath);
    }

    internal static bool ObjectIdentityMatchesForTest(
        string? actualObject,
        string? actualPackage,
        string? expectedObjectPath) =>
        string.Equals(actualObject, ObjectLeaf(expectedObjectPath ?? ""), StringComparison.OrdinalIgnoreCase) &&
        ValidExpectedPackage(actualPackage) &&
        UnrealPathUtil.NormalizePackagePath(actualPackage ?? "").Equals(
            ExpectedPackagePath(expectedObjectPath ?? ""),
            StringComparison.OrdinalIgnoreCase);

    private static bool ObjectPackageMatches(UAsset asset, FPackageIndex index, string expectedPackage)
    {
        var expected = UnrealPathUtil.NormalizePackagePath(expectedPackage ?? "");
        var actual = ObjectPackagePath(asset, index);
        return ValidExpectedPackage(expected) &&
               ValidExpectedPackage(actual) &&
               UnrealPathUtil.NormalizePackagePath(actual).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ObjectPackagePath(UAsset asset, FPackageIndex index)
    {
        if (index.IsNull() || !index.IsImport())
        {
            return "";
        }
        var importIndex = -index.Index - 1;
        if (importIndex < 0 || importIndex >= asset.Imports.Count)
        {
            return "";
        }
        var current = asset.Imports[importIndex];
        for (var depth = 0; depth <= asset.Imports.Count; depth++)
        {
            var name = current.ObjectName.ToString();
            if (ExtractedPackagePathService.IsContentPackagePath(name))
            {
                return UnrealPathUtil.NormalizePackagePath(name);
            }
            if (!current.OuterIndex.IsImport())
            {
                return "";
            }
            importIndex = -current.OuterIndex.Index - 1;
            if (importIndex < 0 || importIndex >= asset.Imports.Count)
            {
                return "";
            }
            current = asset.Imports[importIndex];
        }
        return "";
    }

    private static string ExpectedPackagePath(string objectPath)
    {
        var value = objectPath?.Trim() ?? "";
        var dot = value.LastIndexOf('.');
        return UnrealPathUtil.NormalizePackagePath(dot > 0 ? value[..dot] : value);
    }

    private static bool ValidExpectedPackage(string? package) =>
        ExtractedPackagePathService.IsContentPackagePath(package);

    private static void CheckAdapterComponent(
        UAsset asset,
        NativeSuitProject project,
        string role,
        string componentName,
        SavedPartGraftDonor donor,
        bool isGlider,
        List<Finding> findings)
    {
        void Error(string message) => findings.Add(new("ERROR",
            $"{role}: paired-cape adapter component '{componentName}' {message}"));

        var component = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
            export.ObjectName.ToString().Equals(
                componentName + "_GEN_VARIABLE",
                StringComparison.OrdinalIgnoreCase));
        if (component is null)
        {
            Error("is missing its component template export.");
            return;
        }

        var componentClass = component.GetExportClassType().Value?.ToString() ?? "";
        if (!componentClass.Equals(donor.TemplateComponentClass, StringComparison.OrdinalIgnoreCase))
        {
            Error($"uses class '{componentClass}' instead of donor class '{donor.TemplateComponentClass}'.");
        }

        var mesh = FindObjectProperty(component.Data, "SkeletalMesh") ??
                   FindObjectProperty(component.Data, "SkinnedAsset");
        if (mesh is null || !ObjectIdentityMatches(asset, mesh.Value, donor.MeshObjectPath))
        {
            Error($"does not reference expected skeletal mesh '{donor.MeshObjectPath}'.");
        }

        var anim = FindObjectProperty(component.Data, "AnimClass");
        var actualAnim = anim is null ? "" : ObjectName(asset, anim.Value);
        if (!isGlider && !string.IsNullOrWhiteSpace(actualAnim))
        {
            Error($"retained AnimClass '{actualAnim}' even though the cosmetic cape donor has none.");
        }
        var expectedAnimPath = !string.IsNullOrWhiteSpace(donor.AnimClassObjectPath)
            ? donor.AnimClassObjectPath
            : !string.IsNullOrWhiteSpace(donor.AnimClassPackagePath) &&
              !string.IsNullOrWhiteSpace(donor.AnimClassObjectName)
                ? donor.AnimClassPackagePath + "." + donor.AnimClassObjectName
                : "";
        if (isGlider &&
            (anim is null || !ObjectIdentityMatches(asset, anim.Value, expectedAnimPath)))
        {
            Error($"uses AnimClass '{actualAnim}' instead of exact paired driver '{expectedAnimPath}'.");
        }

        var tags = component.Data.OfType<ArrayPropertyData>()
            .FirstOrDefault(property => property.Name.ToString().Equals(
                "ComponentTags",
                StringComparison.OrdinalIgnoreCase))?.Value?
            .OfType<NamePropertyData>()
            .Select(value => value.Value.ToString())
            .ToList() ?? [];
        if (isGlider)
        {
            if (!tags.Contains("Glider", StringComparer.OrdinalIgnoreCase))
            {
                Error("is missing the native Glider tag.");
            }
        }
        else
        {
            if (!tags.Any(tag =>
                    tag.Equals("Cape", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("TtCharacterAsset.Cape", StringComparison.OrdinalIgnoreCase)) ||
                tags.Any(tag => tag.Equals("Glider", StringComparison.OrdinalIgnoreCase)))
            {
                Error("does not have a cosmetic Cape-only tag set.");
            }
        }

        var actualMaterials = MaterialObjectProperties(component);
        var expectedMaterials = donor.Materials ?? [];
        if (actualMaterials.Count != expectedMaterials.Count ||
            expectedMaterials.Where((expected, slot) =>
                    !AdapterMaterialMatches(
                        asset,
                        project,
                        role,
                        componentName,
                        slot,
                        expected,
                        slot < actualMaterials.Count ? actualMaterials[slot] : null))
                .Any())
        {
            Error("does not retain the donor's complete ordered material set after later user material overrides.");
        }

        var node = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
            export.ObjectName.ToString().StartsWith("SCS_Node", StringComparison.OrdinalIgnoreCase) &&
            export.Data.OfType<NamePropertyData>().Any(property =>
                property.Name.ToString().Equals("InternalVariableName", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ToString().Equals(componentName, StringComparison.OrdinalIgnoreCase)));
        if (node is null)
        {
            Error("is missing its SCS node.");
            return;
        }

        var componentIndex = asset.Exports.IndexOf(component) + 1;
        var nodeIndex = asset.Exports.IndexOf(node) + 1;
        var nodeTemplate = FindObjectProperty(node.Data, "ComponentTemplate");
        var scs = asset.Exports.OfType<NormalExport>().FirstOrDefault(export =>
            export.ObjectName.ToString().Equals("SimpleConstructionScript_0", StringComparison.OrdinalIgnoreCase));
        if (nodeTemplate is null || nodeTemplate.Value.Index != componentIndex ||
            !node.CreateBeforeSerializationDependencies.Any(index => index.Index == componentIndex) ||
            scs is null ||
            !scs.CreateBeforeSerializationDependencies.Any(index => index.Index == nodeIndex))
        {
            Error("has an incomplete SimpleConstructionScript dependency chain.");
        }

        var classExport = asset.Exports.OfType<ClassExport>().FirstOrDefault();
        var classProperty = classExport?.LoadedProperties.OfType<FObjectProperty>().FirstOrDefault(property =>
            property.Name.ToString().Equals(componentName, StringComparison.OrdinalIgnoreCase));
        if (classProperty is null || classProperty.PropertyClass.Index != component.ClassIndex.Index)
        {
            Error("is missing its correctly typed reflected Blueprint class field.");
        }

        if (!component.SerializationBeforeCreateDependencies.Any(index =>
                index.Index == component.ClassIndex.Index) ||
            (!component.TemplateIndex.IsNull() &&
             !component.SerializationBeforeCreateDependencies.Any(index =>
                 index.Index == component.TemplateIndex.Index)))
        {
            Error("is missing component-class/archetype preload dependencies.");
        }

        var attachSocket = node.Data.OfType<NamePropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("AttachToName", StringComparison.OrdinalIgnoreCase))?.Value.ToString() ?? "";
        if (!attachSocket.Equals(donor.AttachSocket, StringComparison.OrdinalIgnoreCase))
        {
            Error($"attaches to '{attachSocket}' instead of donor socket '{donor.AttachSocket}'.");
        }
        var parent = node.Data.OfType<NamePropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals("ParentComponentOrVariableName", StringComparison.OrdinalIgnoreCase))?.Value.ToString() ?? "";
        if (!parent.Equals(donor.ParentComponentOrVariableName, StringComparison.OrdinalIgnoreCase))
        {
            Error($"uses parent '{parent}' instead of donor parent '{donor.ParentComponentOrVariableName}'.");
        }
    }

    private static bool AdapterMaterialMatches(
        UAsset asset,
        NativeSuitProject project,
        string role,
        string componentName,
        int slot,
        NativeSuitObjectRef? donorMaterial,
        ObjectPropertyData? actualMaterial)
    {
        if (donorMaterial is null || actualMaterial is null)
        {
            return false;
        }

        var donorPackage = UnrealPathUtil.NormalizePackagePath(
            !string.IsNullOrWhiteSpace(donorMaterial.PackagePath)
                ? donorMaterial.PackagePath
                : donorMaterial.ObjectPath);
        var finalPackage = UnrealPathUtil.NormalizePackagePath(FinalMaterialPackage(
            project,
            role,
            componentName,
            slot,
            donorPackage));
        if (!ValidExpectedPackage(finalPackage))
        {
            return false;
        }

        // Untouched adapter materials still receive the strict donor object + package check.
        // A saved material assignment declares only a package path, so an explicit override is
        // validated against that final package while every structural adapter invariant remains
        // exact (mesh, AnimClass, tags, material count, ordering, and SCS wiring).
        if (finalPackage.Equals(donorPackage, StringComparison.OrdinalIgnoreCase))
        {
            var donorObjectPath = !string.IsNullOrWhiteSpace(donorMaterial.ObjectPath)
                ? donorMaterial.ObjectPath
                : donorMaterial.PackagePath + "." + donorMaterial.ObjectName;
            return ObjectIdentityMatches(asset, actualMaterial.Value, donorObjectPath);
        }

        return ObjectPackageMatches(asset, actualMaterial.Value, finalPackage);
    }

    internal static string FinalMaterialPackageForTest(
        NativeSuitProject project,
        string role,
        string componentName,
        int slot,
        string fallback) =>
        FinalMaterialPackage(project, role, componentName, slot, fallback);

    private static ObjectPropertyData? FindObjectProperty(IEnumerable<PropertyData> properties, string name) =>
        properties.OfType<ObjectPropertyData>().FirstOrDefault(property =>
            property.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string ObjectName(UAsset asset, FPackageIndex index)
    {
        if (index.IsNull())
        {
            return "";
        }
        if (index.IsImport() && -index.Index <= asset.Imports.Count)
        {
            return index.ToImport(asset).ObjectName.ToString();
        }
        if (index.IsExport() && index.Index <= asset.Exports.Count)
        {
            return index.ToExport(asset).ObjectName.ToString();
        }
        return "";
    }

    private static string ObjectLeaf(string objectPath)
    {
        var value = objectPath?.Trim() ?? "";
        var dot = value.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < value.Length)
        {
            return value[(dot + 1)..];
        }
        var slash = value.LastIndexOf('/');
        return slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : value;
    }

    internal static bool BlocksSyntheticCapePairOnGlideOnlyBaseForTest(NativeSuitProject project)
    {
        var findings = new List<Finding>();
        CheckCapeGliderContract(
            project,
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly,
            new Dictionary<string, UAsset>(StringComparer.OrdinalIgnoreCase),
            findings);
        return findings.Any(finding =>
            finding.Severity == "ERROR" &&
            finding.Message.Contains("not runtime-proven", StringComparison.Ordinal));
    }

    private string PackagePathToBasePath(string packagePath)
    {
        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only /Game package paths are supported. Got: {packagePath}");
        }
        return Path.Combine(_contentRoot, packagePath["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ExtractMod(string playablePackagePath)
    {
        // /Game/Mods/<mod>/Characters/BP_...  →  <mod>
        var norm = UnrealPathUtil.NormalizePackagePath(playablePackagePath);
        const string marker = "/Mods/";
        var i = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return "";
        }
        var after = norm[(i + marker.Length)..];
        var slash = after.IndexOf('/');
        return slash > 0 ? after[..slash] : after;
    }

    private static string AssetName(string packagePath)
    {
        var norm = packagePath.Contains('.') ? packagePath[..packagePath.IndexOf('.')] : packagePath;
        var slash = norm.LastIndexOf('/');
        return slash >= 0 ? norm[(slash + 1)..] : norm;
    }
}
