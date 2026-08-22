using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
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
    private readonly Usmap? _mappings;

    public StageValidationService(string contentRoot, string? mappingsPath)
    {
        _contentRoot = contentRoot;
        _mappings = string.IsNullOrWhiteSpace(mappingsPath) || !File.Exists(mappingsPath)
            ? null
            : MappingsCache.Load(mappingsPath);
    }

    public List<Finding> Validate(NativeSuitProject project)
    {
        var findings = new List<Finding>();

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
            CheckMeshKindMismatch(asset, role, findings);
            CheckDonorShellStaticHair(asset, role, findings);
        }

        CheckPawnTag(project, findings);
        CheckGliderAnimInjection(project, findings);
        CheckRequiredAbilitySets(project, findings);
        CheckEquipmentDependencies(project, findings);
        CheckGliderDependencies(project, findings);
        return findings;
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

        CheckAnimSetReferenced(project.GliderAnimLas, $"/Game/Mods/{mod}/Characters/LAS_Char_{mod}", "LAS_Char", findings);
        CheckAnimSetReferenced(project.GliderAnimMas, $"/Game/Mods/{mod}/Characters/MAS_Char_{mod}", "MAS_Char", findings);
    }

    private void CheckAnimSetReferenced(string? animSetPkg, string charSetPkg, string label, List<Finding> findings)
    {
        if (string.IsNullOrWhiteSpace(animSetPkg))
        {
            return;
        }
        var setName = AssetName(animSetPkg);
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
            findings.Add(new("WARN",
                $"glider glide-animation '{setName}' is configured but {label} was not generated ({uasset}) — the glider may render invisible. Ensure the custom archetype is enabled and re-package."));
            return;
        }
        try
        {
            var asset = new UAsset(uasset, EngineVersion.VER_UE5_6, _mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            var referenced = asset.Imports.Any(imp => imp.ObjectName.ToString().Equals(setName, StringComparison.OrdinalIgnoreCase));
            if (!referenced)
            {
                findings.Add(new("WARN",
                    $"glider glide-animation '{setName}' is configured but NOT injected into {label} — the wingsuit will render invisible (wrong body pose). Re-apply the glider preset and re-package."));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new("WARN", $"could not verify glider anim injection in {label}: {ex.Message}"));
        }
    }

    private void CheckRequiredAbilitySets(NativeSuitProject project, List<Finding> findings)
    {
        if (project.EquipmentSlots.Count == 0 &&
            !project.PartGrafts.Any(graft => graft.IsGlider))
        {
            return;
        }

        var gameData = GameDataService.Instance;
        var donorFamily = project.BaseProfile?.GameplayFamily;
        if (string.IsNullOrWhiteSpace(donorFamily))
        {
            donorFamily = gameData.FamilyForBasePath(project.PlayableTemplate?.PackagePath ?? "")?.Name;
        }

        var requiredSets = project.EquipmentSlots
            .Select(change => gameData.FindEquipment(change.Gadget))
            .Where(equipment => equipment is not null)
            .SelectMany(equipment => EquipmentDependencyService.RequiredAbilitySets(equipment!, donorFamily))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (project.PartGrafts.Any(graft => graft.IsGlider) &&
            !requiredSets.Contains(GliderService.GlidingAbilitySetPackage, StringComparer.OrdinalIgnoreCase))
        {
            requiredSets.Add(GliderService.GlidingAbilitySetPackage);
        }
        if (requiredSets.Count == 0)
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
            var asset = new UAsset(
                uasset,
                EngineVersion.VER_UE5_6,
                _mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            foreach (var abilitySet in requiredSets)
            {
                var name = AssetName(abilitySet);
                if (!asset.Imports.Any(import =>
                        import.ObjectName.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    findings.Add(new("ERROR",
                        $"Required ability set '{name}' is missing from the generated DPRD. " +
                        "The related equipment or glider would be incomplete in-game."));
                }
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
                        $"Equipment '{resolvedEquipment.Name}' is a controller setup. The tool stages its ability set, but controller spawn and recall behavior still needs an in-game check.{actors}"));
                }
            }
            else if (profile.Support is EquipmentSupportKind.Experimental or EquipmentSupportKind.FamilyOnly)
            {
                findings.Add(new("WARN",
                    $"Equipment '{resolvedEquipment.Name}' is {profile.SupportLabel.ToLowerInvariant()}: {profile.Summary}"));
            }
        }
    }

    /// <summary>
    /// Glider safety is independent of the equipment list. Keeping these checks outside
    /// <see cref="CheckEquipmentDependencies"/> ensures a suit with no gadgets cannot
    /// bypass the package-blocking cape/glider compatibility rules.
    /// </summary>
    private static void CheckGliderDependencies(NativeSuitProject project, List<Finding> findings)
    {
        if (project.PartGrafts.Any(graft => graft.IsGlider) &&
            (!string.IsNullOrWhiteSpace(project.GliderAnimLas) || !string.IsNullOrWhiteSpace(project.GliderAnimMas)) &&
            !project.UseCustomArchetype)
        {
            findings.Add(new("ERROR",
                "This glider needs a donor glide pose, but the custom archetype is off so its animation sets cannot be injected. Re-apply the glider preset."));
        }

        var capeGlideContract = new AnimArchetypeGraftService().BaseCapeGlideContract(project);
        if (GliderService.HasAdditiveCapeAndGliderCombination(project, capeGlideContract))
        {
            findings.Add(new("ERROR",
                "This suit combines a custom static mesh attached to Cape with a glide visual. " +
                "Custom static meshes are additive components and are not controlled by the playable base's native cape/glider visibility wiring, " +
                "so the custom cape would remain visible while gliding. Remove the custom Cape attachment or the glider before packaging."));
        }
        else if (GliderService.HasCapeAndGliderCombination(project, capeGlideContract))
        {
            if (capeGlideContract == AnimArchetypeGraftService.CapeGlideContractStatus.Unknown)
            {
                findings.Add(new("WARN",
                    "This suit combines a regular cape with a glide visual, but Batcomputer could not inspect the playable base's cape visibility contract. " +
                    "Refresh the character assets and run the build check again."));
            }
            else if (capeGlideContract != AnimArchetypeGraftService.CapeGlideContractStatus.Paired)
            {
                findings.Add(new("ERROR",
                    "This suit combines a regular cape with a glide visual, but its playable base does not natively own separate cosmetic-cape and glider components. " +
                    "The regular cape would remain visible while gliding. Re-select the visual base and choose a playable donor with the native two-cape visibility setup, then re-apply both parts."));
            }
        }
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
