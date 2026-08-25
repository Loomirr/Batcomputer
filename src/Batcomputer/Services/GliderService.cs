namespace Batcomputer;

public enum GliderVisualKind
{
    GlideCape,
    Wingsuit,
    CharacterGlider
}

/// <summary>
/// Whether a glide component's AnimBlueprint participates in the game's paired regular-cape
/// visibility contract. Glide-only drivers can animate their own mesh, but do not hide a separate
/// cosmetic Cape when gliding starts.
/// </summary>
public enum PairedCapeVisibilityDriver
{
    Unknown,
    PairedCapable,
    GlideOnly
}

public enum GliderMaterialCompatibility
{
    NativeMatch,
    CustomMaterial,
    DifferentNativeMaterial,
    Unknown
}

public sealed class GliderMaterialCompatibilityResult
{
    public GliderMaterialCompatibility Kind { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool NeedsConfirmation => Kind == GliderMaterialCompatibility.DifferentNativeMaterial;
}

/// <summary>
/// Helpers for selecting and applying native glide visuals. The important rule:
/// gliders should come from real indexed character components whenever possible
/// (mesh + anim BP + all material slots + component tags), not from a synthetic
/// one-material record.
/// </summary>
public static class GliderService
{
    public const int PairedCapeAdapterSchemaVersion = 3;

    public const string GlidingAbilitySetPackage =
        "/Game/Characters/Abilities/CoreAbilities/Gliding/AS_Gliding";

    public static bool IsNativeGliderPart(NativeSuitPartRecord part)
    {
        if (HasGlideTag(part))
        {
            return true;
        }

        var haystack = string.Join(" ", new[]
        {
            part.MeshObjectName,
            part.MeshPackagePath,
            part.MeshObjectPath,
            part.AnimClassObjectName,
            part.AnimClassPackagePath,
            part.AnimClassObjectPath,
            part.SourcePackagePath,
            part.Notes
        });

        return haystack.Contains("Wingsuit", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Cape_Glide", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("GA_Glider", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("GA_Wingsuit", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Glide", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Glider", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCosmeticCapeAttachment(NativeSuitPartRecord part)
    {
        if (IsNativeGliderPart(part))
        {
            return false;
        }

        return part.ComponentTags.Any(tag =>
                   tag.Equals("Cape", StringComparison.OrdinalIgnoreCase) ||
                   tag.Equals("TtCharacterAsset.Cape", StringComparison.OrdinalIgnoreCase)) ||
               part.MeshObjectName.Contains("Cape", StringComparison.OrdinalIgnoreCase) ||
               part.MeshPackagePath.Contains("/Cape/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCosmeticCapeAttachment(SavedPartGraftDonor? donor)
    {
        if (donor is null)
        {
            return false;
        }

        var componentTags = donor.ComponentTags ?? new List<string>();
        var hasGliderTag = componentTags.Any(tag =>
            string.Equals(tag, "Glider", StringComparison.OrdinalIgnoreCase));
        if (hasGliderTag)
        {
            return false;
        }

        return componentTags.Any(tag =>
                   string.Equals(tag, "Cape", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "TtCharacterAsset.Cape", StringComparison.OrdinalIgnoreCase)) ||
               (donor.MeshObjectPath?.Contains("Cape", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (donor.Stem?.Contains("Cape", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Classifies the runtime visibility driver from the indexed donor record. The two confirmed
    /// paired-capable AnimBlueprints are the shared ABP_Cape_Glide family (including the dedicated
    /// Batgirl Party variant). Wingsuits and the Talia/Gordon character gliders are glide-only.
    /// Anything else remains unknown so callers can fail conservatively.
    /// </summary>
    public static PairedCapeVisibilityDriver PairedCapeDriverForPart(NativeSuitPartRecord? part)
    {
        if (part is null)
        {
            return PairedCapeVisibilityDriver.Unknown;
        }

        var fromAnimClass = ClassifyPairedCapeDriver(
            part.AnimClassObjectName,
            part.AnimClassPackagePath,
            part.AnimClassObjectPath);
        if (fromAnimClass != PairedCapeVisibilityDriver.Unknown)
        {
            return fromAnimClass;
        }

        // Legacy/synthesized index rows can lack AnimClass metadata. Mesh and donor package
        // identity provide a conservative fallback for the five native glide families.
        return ClassifyLegacyPairedCapeDriver(
            part.MeshObjectName,
            part.MeshPackagePath,
            part.MeshObjectPath,
            part.CharacterFolder,
            part.SourcePackagePath,
            part.Stem);
    }

    /// <summary>Saved-project form of <see cref="PairedCapeDriverForPart"/>.</summary>
    public static PairedCapeVisibilityDriver PairedCapeDriverForDonor(SavedPartGraftDonor? donor)
    {
        if (donor is null)
        {
            return PairedCapeVisibilityDriver.Unknown;
        }

        var fromAnimClass = ClassifyPairedCapeDriver(
            donor.AnimClassObjectName,
            donor.AnimClassPackagePath,
            donor.AnimClassObjectPath);
        if (fromAnimClass != PairedCapeVisibilityDriver.Unknown)
        {
            return fromAnimClass;
        }

        return ClassifyLegacyPairedCapeDriver(
            donor.MeshObjectPath,
            donor.SourcePackagePath,
            donor.Stem,
            donor.TemplatePackagePath,
            donor.TemplateUasset);
    }

    /// <summary>
    /// True when the project replaces the gameplay donor's native glide component rather than
    /// simply retaining a proven native paired setup.
    /// </summary>
    public static bool ProjectHasReplacementGlider(NativeSuitProject project) =>
        project.PartGrafts.Any(graft => graft.IsGlider) ||
        project.GliderGrafted ||
        (!string.IsNullOrWhiteSpace(project.GliderType) &&
         !project.GliderType.Trim().Equals("base", StringComparison.OrdinalIgnoreCase)) ||
        !string.IsNullOrWhiteSpace(project.GliderAnimLas) ||
        !string.IsNullOrWhiteSpace(project.GliderAnimMas);

    /// <summary>
    /// Resolves the saved replacement glider's paired-cape driver. New projects use persisted
    /// AnimClass identity; old projects fall back to their donor mesh and glider recipe strings.
    /// </summary>
    public static PairedCapeVisibilityDriver ProjectReplacementGliderDriver(NativeSuitProject project)
    {
        // Declarative rebuild and the UI treat the last saved glider as active. Legacy projects
        // can contain duplicates, so validate the same record that will actually be replayed.
        var graft = project.PartGrafts.LastOrDefault(candidate => candidate.IsGlider);
        if (graft?.Playable is not null)
        {
            // Runtime traversal uses the playable BP. Do not let a cutscene donor make an unknown
            // playable driver look safe, and do not promote it from a display-name fallback.
            return PairedCapeDriverForDonor(graft.Playable);
        }
        if (graft?.Cutscene is not null)
        {
            return PairedCapeDriverForDonor(graft.Cutscene);
        }

        // Very old projects can express glider intent without a saved donor record.
        return ClassifyLegacyPairedCapeDriver(
            project.GliderType,
            project.GliderAnimLas,
            project.GliderAnimMas);
    }

    /// <summary>Whether a declarative remove-component rule targets a component, with or without
    /// the UI's material-slot suffix (for example Cape and Cape:0).</summary>
    public static bool ProjectExplicitlyRemovesComponent(NativeSuitProject project, string component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return false;
        }

        return project.Requirements.Any(requirement =>
        {
            if (!requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var target = requirement.TargetComponent?.Trim() ?? "";
            var colon = target.LastIndexOf(':');
            if (colon > 0)
            {
                target = target[..colon];
            }
            return target.Equals(component.Trim(), StringComparison.OrdinalIgnoreCase);
        });
    }

    public static bool ProjectHasNativeCosmeticCapeGraft(NativeSuitProject project) =>
        project.PartGrafts.Any(graft =>
        {
            if (graft.IsGlider ||
                (!IsCosmeticCapeAttachment(graft.Playable) && !IsCosmeticCapeAttachment(graft.Cutscene)))
            {
                return false;
            }

            var component = !string.IsNullOrWhiteSpace(graft.ResolvedComponent)
                ? graft.ResolvedComponent
                : !string.IsNullOrWhiteSpace(graft.Slot)
                    ? graft.Slot
                    : graft.Playable?.Slot ?? graft.Cutscene?.Slot ?? "";
            return string.IsNullOrWhiteSpace(component) ||
                   !ProjectExplicitlyRemovesComponent(project, component);
        });

    /// <summary>
    /// Custom static meshes are additive component shells. Unlike a native cape graft, they do not
    /// repoint the playable base's existing visibility-wired cosmetic-cape component.
    /// </summary>
    public static bool ProjectHasAdditiveCustomCape(NativeSuitProject project) =>
        project.CustomStaticMeshes.Any(mesh =>
            string.Equals(mesh.Target?.Trim(), "Cape", StringComparison.OrdinalIgnoreCase));

    public static bool ProjectHasCosmeticCape(NativeSuitProject project) =>
        ProjectHasNativeCosmeticCapeGraft(project) || ProjectHasAdditiveCustomCape(project);

    private static bool ProjectHasGlider(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus baseContract,
        bool addingGlider = false) =>
        baseContract is AnimArchetypeGraftService.CapeGlideContractStatus.Paired or
            AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly ||
        addingGlider ||
        project.GliderGrafted ||
        (!string.IsNullOrWhiteSpace(project.GliderType) &&
         !project.GliderType.Trim().Equals("base", StringComparison.OrdinalIgnoreCase)) ||
        project.PartGrafts.Any(graft => graft.IsGlider);

    internal static bool HasAdditiveCapeAndGliderCombination(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus baseContract,
        bool addingCustomCape = false,
        bool addingGlider = false) =>
        (addingCustomCape || ProjectHasAdditiveCustomCape(project)) &&
        ProjectHasGlider(project, baseContract, addingGlider);

    internal static bool HasCapeAndGliderCombination(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus baseContract,
        bool addingCosmeticCape = false,
        bool addingGlider = false)
    {
        var baseHasCosmeticCape = (baseContract is
            AnimArchetypeGraftService.CapeGlideContractStatus.Paired or
            AnimArchetypeGraftService.CapeGlideContractStatus.CapeOnly) &&
            !ProjectExplicitlyRemovesComponent(project, "Cape");
        var hasCosmeticCape = baseHasCosmeticCape || addingCosmeticCape || ProjectHasCosmeticCape(project);
        var hasGlider = ProjectHasGlider(project, baseContract, addingGlider);
        return hasCosmeticCape && hasGlider;
    }

    /// <summary>
    /// Creates or refreshes the explicit proof intent for a native glide-only gameplay donor.
    /// The adapter is deliberately limited to the one topology we can validate: a real native
    /// cosmetic cape plus a paired-capable glide cape, both supplied for playable and cutscene by
    /// the same native donor pair. The cooked assets are verified separately before packaging.
    /// </summary>
    public static bool TryConfigurePairedCapeAdapter(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus baseContract,
        string? nativeGliderComponent,
        out string detail,
        NativeSuitPartIndex? partIndex = null)
    {
        detail = "";
        if (baseContract != AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly)
        {
            detail = "The gameplay donor is not a verified native glide-only base.";
            project.PairedCapeAdapter = null;
            return false;
        }
        if (ProjectHasAdditiveCustomCape(project))
        {
            detail = "Custom static Cape attachments cannot participate in the paired-cape adapter.";
            project.PairedCapeAdapter = null;
            return false;
        }
        if (string.IsNullOrWhiteSpace(nativeGliderComponent))
        {
            detail = "The gameplay donor's native Glider-tagged component could not be resolved.";
            project.PairedCapeAdapter = null;
            return false;
        }

        var cosmeticCandidates = project.PartGrafts.Where(graft =>
            !graft.IsGlider &&
            (IsCosmeticCapeAttachment(graft.Playable) || IsCosmeticCapeAttachment(graft.Cutscene))).ToList();
        var gliderCandidates = project.PartGrafts.Where(graft => graft.IsGlider).ToList();
        if (cosmeticCandidates.Count != 1 || gliderCandidates.Count != 1)
        {
            detail = "The adapter requires exactly one native cosmetic cape and one replacement glide cape.";
            project.PairedCapeAdapter = null;
            return false;
        }

        var cosmetic = cosmeticCandidates[0];
        var glider = gliderCandidates[0];

        if (!ValidateAdapterGrafts(cosmetic, glider, out detail))
        {
            project.PairedCapeAdapter = null;
            return false;
        }

        if (!TryResolveAuthoredShellSlots(cosmetic, glider, out var cosmeticSlot, out var gliderSlot, out detail))
        {
            project.PairedCapeAdapter = null;
            return false;
        }
        if (!TryResolvePairedCapeGlideAnimSets(glider, out var glideAnimLas, out var glideAnimMas, out detail))
        {
            project.PairedCapeAdapter = null;
            return false;
        }
        if (!PairedCapeVisualOverlayService.TrySelect(
                project,
                partIndex,
                cosmetic.Playable!.SourcePackagePath,
                cosmetic.Cutscene!.SourcePackagePath,
                cosmetic,
                glider,
                out var visualSelection,
                out detail))
        {
            project.PairedCapeAdapter = null;
            return false;
        }

        // Never append a donor component/property to a cooked gameplay Blueprint. Its generated
        // class can be parsed and written while its CDO remains an opaque raw export; changing the
        // reflected field list in that state produced an immediate Bad export index crash. Instead
        // use the donor pair's already-authored Cape + Torso shell and repoint those existing fields.
        cosmetic.Slot = cosmeticSlot;
        glider.Slot = gliderSlot;
        cosmetic.PreferDonorComponentShell = false;
        glider.PreferDonorComponentShell = false;
        var existing = project.PairedCapeAdapter;
        project.PairedCapeAdapter = new PairedCapeAdapterProfile
        {
            SchemaVersion = PairedCapeAdapterSchemaVersion,
            AdapterId = existing is not null &&
                        !string.IsNullOrWhiteSpace(existing.AdapterId) &&
                        string.Equals(existing.CosmeticCapeGraftInstanceId, cosmetic.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.GlideCapeGraftInstanceId, glider.InstanceId, StringComparison.OrdinalIgnoreCase)
                ? existing.AdapterId
                : Guid.NewGuid().ToString("N"),
            GameplayDonorPackage = GameplayDonorPackage(project),
            NativeGliderComponent = nativeGliderComponent.Trim(),
            AuthoredShellPlayablePackage = visualSelection.ShellPlayablePackage,
            AuthoredShellCutscenePackage = visualSelection.ShellCutscenePackage,
            CosmeticCapeGraftInstanceId = cosmetic.InstanceId,
            GlideCapeGraftInstanceId = glider.InstanceId,
            CosmeticPlayableSourcePackage = cosmetic.Playable!.SourcePackagePath,
            CosmeticCutsceneSourcePackage = cosmetic.Cutscene!.SourcePackagePath,
            GliderPlayableSourcePackage = glider.Playable!.SourcePackagePath,
            GliderCutsceneSourcePackage = glider.Cutscene!.SourcePackagePath,
            PairedAnimClassObjectName = glider.Playable.AnimClassObjectName,
            GlideAnimLasPackage = glideAnimLas,
            GlideAnimMasPackage = glideAnimMas,
            VisualOverlay = visualSelection.Overlay,
            ResolvedCosmeticComponent = cosmetic.ResolvedComponent,
            ResolvedGliderComponent = glider.ResolvedComponent,
        };

        // Keep the gameplay donor's controller, DPRD, equipment, and general animation graph, but
        // replace its glide-only categories with the exact blocks belonging to the certified Cape + Torso donor. A cape-less
        // gameplay donor otherwise keeps its own glide blocks and can drive a Batman cape with the wrong body pose.
        project.GliderAnimLas = glideAnimLas;
        project.GliderAnimMas = glideAnimMas;
        if (!project.UseCustomArchetype)
        {
            project.UseCustomArchetype = true;
            project.GliderAutoEnabledCustomArchetype = true;
        }
        detail = $"Certified authored paired-cape shell configured ({cosmeticSlot} + {gliderSlot}); {visualSelection.Detail} Gameplay-donor behavior is preserved, and its glide-only categories are replaced by {UnrealPathUtil.AssetName(glideAnimLas)} + {UnrealPathUtil.AssetName(glideAnimMas)} for the cape glide pose.";
        return true;
    }

    /// <summary>Non-mutating UI preflight for the part that is about to be recorded.</summary>
    public static bool CanConfigurePairedCapeAdapterWithIncoming(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus baseContract,
        SavedPartGraft incoming,
        out string detail)
    {
        if (baseContract != AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly)
        {
            detail = "Only a verified native glide-only base can use the dynamic adapter.";
            return false;
        }
        if (ProjectHasAdditiveCustomCape(project))
        {
            detail = "A custom static Cape is additive and cannot join the adapter.";
            return false;
        }

        var counterpartCandidates = incoming.IsGlider
            ? project.PartGrafts.Where(graft =>
                    !graft.IsGlider &&
                    (IsCosmeticCapeAttachment(graft.Playable) ||
                     IsCosmeticCapeAttachment(graft.Cutscene)))
                .Take(2)
                .ToList()
            : project.PartGrafts.Where(graft => graft.IsGlider)
                .Take(2)
                .ToList();
        if (counterpartCandidates.Count != 1)
        {
            detail = counterpartCandidates.Count == 0
                ? incoming.IsGlider
                    ? "Apply the matching native regular cape before adding its ABP_Cape_Glide preset."
                    : "Apply a proven ABP_Cape_Glide preset before adding the regular cape."
                : "The adapter preflight requires one unambiguous existing cape counterpart.";
            return false;
        }
        var cosmetic = incoming.IsGlider ? counterpartCandidates[0] : incoming;
        var glider = incoming.IsGlider ? incoming : counterpartCandidates[0];
        if (!ValidateAdapterGrafts(cosmetic, glider, out detail))
        {
            return false;
        }

        // This check runs before the incoming graft is recorded, so it must prove the actual
        // donor recipes already expose the two existing fields that replay will repoint. Merely
        // having a cosmetic cape plus a paired-capable AnimClass is not enough: accepting a pair
        // whose glide recipe also targets Cape would make replay append/alias a reflected field
        // in the cooked gameplay Blueprint and can crash the game at load time.
        return TryResolveAuthoredShellSlots(cosmetic, glider, out _, out _, out detail);
    }

    /// <summary>Updates the post-rebuild component identities bound into the adapter profile.</summary>
    public static void RefreshPairedCapeAdapterResolvedComponents(NativeSuitProject project)
    {
        var adapter = project.PairedCapeAdapter;
        if (adapter is null)
        {
            return;
        }

        var cosmetic = project.PartGrafts.FirstOrDefault(graft =>
            string.Equals(graft.InstanceId, adapter.CosmeticCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase));
        var glider = project.PartGrafts.FirstOrDefault(graft =>
            string.Equals(graft.InstanceId, adapter.GlideCapeGraftInstanceId, StringComparison.OrdinalIgnoreCase));
        adapter.ResolvedCosmeticComponent = cosmetic?.ResolvedComponent ?? "";
        adapter.ResolvedGliderComponent = glider?.ResolvedComponent ?? "";
    }

    /// <summary>
    /// Validates that a persisted adapter still names the exact current base and graft pair. This
    /// intentionally does not inspect cooked exports; StageValidationService performs that final
    /// proof. Call with <paramref name="requireResolvedComponents"/> for package-time validation.
    /// </summary>
    public static bool IsDeclaredPairedCapeAdapterValid(
        NativeSuitProject project,
        AnimArchetypeGraftService.CapeGlideContractStatus baseContract,
        bool requireResolvedComponents,
        out string detail)
    {
        if (baseContract != AnimArchetypeGraftService.CapeGlideContractStatus.GlideOnly)
        {
            detail = "The adapter is only valid for a verified native glide-only gameplay donor.";
            return false;
        }
        if (!TryResolveCertifiedPairedCapePair(
                project,
                out var adapter,
                out var cosmetic,
                out var glider,
                out var cosmeticSlot,
                out var gliderSlot,
                out detail))
        {
            return false;
        }

        if (requireResolvedComponents)
        {
            if (string.IsNullOrWhiteSpace(cosmetic.ResolvedComponent) ||
                string.IsNullOrWhiteSpace(glider.ResolvedComponent) ||
                string.Equals(cosmetic.ResolvedComponent, glider.ResolvedComponent, StringComparison.OrdinalIgnoreCase))
            {
                detail = "The generated cosmetic and glide cape components are missing or share one identity.";
                return false;
            }
            if (!string.Equals(
                    adapter.ResolvedCosmeticComponent,
                    cosmetic.ResolvedComponent,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    adapter.ResolvedGliderComponent,
                    glider.ResolvedComponent,
                    StringComparison.OrdinalIgnoreCase))
            {
                detail = "The generated component identities do not match the adapter certificate.";
                return false;
            }
            if (!string.Equals(
                    cosmetic.ResolvedComponent,
                    cosmeticSlot,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    glider.ResolvedComponent,
                    gliderSlot,
                    StringComparison.OrdinalIgnoreCase))
            {
                detail = "The generated cape pair did not repoint the authored shell's existing component fields.";
                return false;
            }
        }

        detail = "The persisted paired-cape adapter matches the current base and graft pair.";
        return true;
    }

    /// <summary>
    /// Returns the native two-component Blueprint shell selected by a current adapter. Consumers
    /// use these packages only as the cooked component layout; the project's original playable
    /// template remains the gameplay/archetype donor.
    /// </summary>
    public static bool TryGetAuthoredPairedCapeShell(
        NativeSuitProject project,
        out string playablePackage,
        out string cutscenePackage,
        out string detail)
    {
        playablePackage = "";
        cutscenePackage = "";
        if (!TryResolveCertifiedPairedCapePair(
                project,
                out _,
                out _,
                out _,
                out _,
                out _,
                out detail))
        {
            return false;
        }

        var adapter = project.PairedCapeAdapter!;
        playablePackage = UnrealPathUtil.NormalizePackagePath(adapter.AuthoredShellPlayablePackage);
        cutscenePackage = UnrealPathUtil.NormalizePackagePath(adapter.AuthoredShellCutscenePackage);
        if (string.IsNullOrWhiteSpace(playablePackage) || string.IsNullOrWhiteSpace(cutscenePackage))
        {
            detail = "The certified authored scaffold packages are missing.";
            return false;
        }
        detail = "The authored paired-cape shell is ready.";
        return true;
    }

    /// <summary>
    /// Resolves a schema-2 certificate without calling either public adapter validator. Keeping
    /// this as the shared leaf routine prevents a validation cycle between shell selection and
    /// package-time certification while making both entry points enforce the same exact sources.
    /// </summary>
    private static bool TryResolveCertifiedPairedCapePair(
        NativeSuitProject project,
        out PairedCapeAdapterProfile adapter,
        out SavedPartGraft cosmetic,
        out SavedPartGraft glider,
        out string cosmeticSlot,
        out string gliderSlot,
        out string detail)
    {
        adapter = null!;
        cosmetic = null!;
        glider = null!;
        cosmeticSlot = "";
        gliderSlot = "";

        var declared = project.PairedCapeAdapter;
        if (declared is null)
        {
            detail = "No paired-cape adapter is declared.";
            return false;
        }
        if (declared.SchemaVersion != PairedCapeAdapterSchemaVersion)
        {
            detail = $"Paired-cape adapter schema {declared.SchemaVersion} must be rebuilt.";
            return false;
        }
        adapter = declared;

        if (string.IsNullOrWhiteSpace(adapter.AdapterId) ||
            string.IsNullOrWhiteSpace(adapter.NativeGliderComponent) ||
            string.IsNullOrWhiteSpace(adapter.CosmeticCapeGraftInstanceId) ||
            string.IsNullOrWhiteSpace(adapter.GlideCapeGraftInstanceId) ||
            string.Equals(
                adapter.CosmeticCapeGraftInstanceId,
                adapter.GlideCapeGraftInstanceId,
                StringComparison.OrdinalIgnoreCase))
        {
            detail = "The adapter certificate is missing a distinct adapter, graft, or native-glider identity.";
            return false;
        }
        if (!project.UseCustomArchetype)
        {
            detail = "The gameplay-donor archetype bridge required by the authored shell is disabled.";
            return false;
        }
        if (ProjectHasAdditiveCustomCape(project))
        {
            detail = "A custom static Cape cannot be controlled by the paired-cape adapter.";
            return false;
        }

        var gameplayDonor = GameplayDonorPackage(project);
        if (!SameRequiredPackage(adapter.GameplayDonorPackage, gameplayDonor))
        {
            detail = "The gameplay donor is missing or changed after the adapter was created.";
            return false;
        }
        var profileGameplayDonor = project.BaseProfile?.GameplayDonorPackage;
        var playableTemplatePackage = project.PlayableTemplate?.PackagePath;
        if (!string.IsNullOrWhiteSpace(profileGameplayDonor) &&
            !string.IsNullOrWhiteSpace(playableTemplatePackage) &&
            !SameRequiredPackage(profileGameplayDonor, playableTemplatePackage))
        {
            detail = "The saved base profile and playable template disagree about the gameplay donor.";
            return false;
        }

        var cosmeticCandidateCount = project.PartGrafts.Count(graft =>
            !graft.IsGlider &&
            (IsCosmeticCapeAttachment(graft.Playable) || IsCosmeticCapeAttachment(graft.Cutscene)));
        var gliderCandidateCount = project.PartGrafts.Count(graft => graft.IsGlider);
        if (cosmeticCandidateCount != 1 || gliderCandidateCount != 1)
        {
            detail = "The adapter certificate does not describe exactly one active cosmetic cape and one active glide cape.";
            return false;
        }

        // Take at most two so malformed projects with duplicate instance IDs fail closed instead
        // of letting SingleOrDefault throw while a base stage is being selected.
        var cosmeticMatches = project.PartGrafts.Where(graft =>
                string.Equals(
                    graft.InstanceId,
                    declared.CosmeticCapeGraftInstanceId,
                    StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        var gliderMatches = project.PartGrafts.Where(graft =>
                string.Equals(
                    graft.InstanceId,
                    declared.GlideCapeGraftInstanceId,
                    StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (cosmeticMatches.Count != 1 || gliderMatches.Count != 1)
        {
            detail = "The authored shell's bound cape grafts are missing, duplicated, or replaced.";
            return false;
        }

        cosmetic = cosmeticMatches[0];
        glider = gliderMatches[0];
        if (ReferenceEquals(cosmetic, glider) || cosmetic.IsGlider || !glider.IsGlider)
        {
            detail = "The adapter certificate does not bind distinct cosmetic-cape and glide-cape grafts.";
            return false;
        }
        if (cosmetic.PreferDonorComponentShell || glider.PreferDonorComponentShell)
        {
            detail = "The adapter still requests a synthetic donor-component append. Re-apply the cape pair so it uses the authored Blueprint shell.";
            return false;
        }
        if (!ValidateAdapterGrafts(cosmetic, glider, out detail) ||
            !TryResolveAuthoredShellSlots(
                cosmetic,
                glider,
                out cosmeticSlot,
                out gliderSlot,
                out detail))
        {
            return false;
        }
        if (!string.Equals(cosmetic.Slot, cosmeticSlot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(glider.Slot, gliderSlot, StringComparison.OrdinalIgnoreCase))
        {
            detail = "The adapter grafts no longer target the authored Cape and Torso component fields.";
            return false;
        }

        if (!SameRequiredPackage(adapter.CosmeticPlayableSourcePackage, cosmetic.Playable!.SourcePackagePath) ||
            !SameRequiredPackage(adapter.CosmeticCutsceneSourcePackage, cosmetic.Cutscene!.SourcePackagePath) ||
            !SameRequiredPackage(adapter.GliderPlayableSourcePackage, glider.Playable!.SourcePackagePath) ||
            !SameRequiredPackage(adapter.GliderCutsceneSourcePackage, glider.Cutscene!.SourcePackagePath) ||
            string.IsNullOrWhiteSpace(adapter.AuthoredShellPlayablePackage) ||
            string.IsNullOrWhiteSpace(adapter.AuthoredShellCutscenePackage) ||
            !SameRequiredIdentity(adapter.PairedAnimClassObjectName, glider.Playable.AnimClassObjectName) ||
            !SameRequiredIdentity(adapter.PairedAnimClassObjectName, glider.Cutscene.AnimClassObjectName))
        {
            detail = "The adapter certificate's saved source, shell, or AnimClass identities no longer match the active grafts.";
            return false;
        }
        if (!TryResolvePairedCapeGlideAnimSets(
                glider,
                out var expectedGlideAnimLas,
                out var expectedGlideAnimMas,
                out detail))
        {
            return false;
        }
        if (!SameRequiredPackage(adapter.GlideAnimLasPackage, expectedGlideAnimLas) ||
            !SameRequiredPackage(adapter.GlideAnimMasPackage, expectedGlideAnimMas) ||
            !SameRequiredPackage(project.GliderAnimLas, adapter.GlideAnimLasPackage) ||
            !SameRequiredPackage(project.GliderAnimMas, adapter.GlideAnimMasPackage))
        {
            detail =
                "The paired-cape glide animation certificate is missing, changed, or no longer matches the exact Cape + Torso donor. Rebuild the adapter.";
            return false;
        }
        if (!PairedCapeVisualOverlayService.ValidateDeclaration(
                project,
                null,
                adapter,
                out detail))
        {
            return false;
        }

        detail = "The paired-cape adapter certificate matches its exact authored Cape + Torso scaffold, visual-base overlay, and glide animation blocks.";
        return true;
    }

    private static bool TryResolveAuthoredShellSlots(
        SavedPartGraft cosmetic,
        SavedPartGraft glider,
        out string cosmeticSlot,
        out string gliderSlot,
        out string detail)
    {
        cosmeticSlot = cosmetic.Playable?.TemplateSlot?.Trim() ?? "";
        gliderSlot = glider.Playable?.TemplateSlot?.Trim() ?? "";
        var cutsceneCosmeticSlot = cosmetic.Cutscene?.TemplateSlot?.Trim() ?? "";
        var cutsceneGliderSlot = glider.Cutscene?.TemplateSlot?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(cosmeticSlot) || string.IsNullOrWhiteSpace(gliderSlot) ||
            !string.Equals(cosmeticSlot, cutsceneCosmeticSlot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(gliderSlot, cutsceneGliderSlot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cosmeticSlot, gliderSlot, StringComparison.OrdinalIgnoreCase))
        {
            detail = "The donor pair does not expose matching, distinct cosmetic-cape and glide-component fields in both authored Blueprints.";
            return false;
        }
        if (!string.Equals(cosmeticSlot, "Cape", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(gliderSlot, "Torso", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"The proven paired-cape shell requires cosmetic 'Cape' plus glide 'Torso'; this donor exposes '{cosmeticSlot}' plus '{gliderSlot}'.";
            return false;
        }

        detail = "The donor pair supplies an authored Cape + Torso shell.";
        return true;
    }

    /// <summary>
    /// Resolves the body-animation blocks from the exact Cape + Torso graft donor, independently
    /// of the compatible Blueprint scaffold used to host those fields. This distinction matters
    /// when a safer Batman scaffold is selected for a Batman Animated Series visual pair: the
    /// visual donor still owns the required Batman glide pose.
    /// </summary>
    private static bool TryResolvePairedCapeGlideAnimSets(
        SavedPartGraft glider,
        out string lasPackage,
        out string masPackage,
        out string detail)
    {
        lasPackage = "";
        masPackage = "";
        var playableFamily = CharacterFamilyFromNativeSource(glider.Playable?.SourcePackagePath);
        var cutsceneFamily = CharacterFamilyFromNativeSource(glider.Cutscene?.SourcePackagePath);
        if (string.IsNullOrWhiteSpace(playableFamily) ||
            string.IsNullOrWhiteSpace(cutsceneFamily) ||
            !playableFamily.Equals(cutsceneFamily, StringComparison.OrdinalIgnoreCase))
        {
            detail =
                "The Cape + Torso donor pair does not resolve to one native character animation family.";
            return false;
        }

        lasPackage = $"/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_{playableFamily}";
        masPackage = $"/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_{playableFamily}";
        detail = $"The paired-cape donor requires {UnrealPathUtil.AssetName(lasPackage)} + {UnrealPathUtil.AssetName(masPackage)}.";
        return true;
    }

    private static string CharacterFamilyFromNativeSource(string? packagePath)
    {
        const string prefix = "/Game/Characters/Minifig/";
        var normalized = UnrealPathUtil.NormalizePackagePath(packagePath ?? "");
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        var relative = normalized[prefix.Length..];
        var slash = relative.IndexOf('/');
        if (slash <= 0)
        {
            return "";
        }
        var family = relative[..slash];
        return family.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? family
            : "";
    }

    private static bool ValidateAdapterGrafts(
        SavedPartGraft cosmetic,
        SavedPartGraft glider,
        out string detail)
    {
        if (cosmetic.IsGlider || !glider.IsGlider)
        {
            detail = "The adapter graft roles are not cosmetic-cape plus glide-cape.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(cosmetic.InstanceId) || string.IsNullOrWhiteSpace(glider.InstanceId) ||
            string.Equals(cosmetic.InstanceId, glider.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            detail = "The adapter grafts need distinct persistent instance IDs.";
            return false;
        }
        if (cosmetic.Playable is null || cosmetic.Cutscene is null ||
            glider.Playable is null || glider.Cutscene is null)
        {
            detail = "Both adapter parts need playable and cutscene donor recipes.";
            return false;
        }
        if (!IsValidCosmeticAdapterDonor(cosmetic.Playable) ||
            !IsValidCosmeticAdapterDonor(cosmetic.Cutscene))
        {
            detail = "The regular cape must be a native skeletal Cape recipe with no Glider tag or AnimClass.";
            return false;
        }
        if (!IsValidGlideAdapterDonor(glider.Playable) ||
            !IsValidGlideAdapterDonor(glider.Cutscene))
        {
            detail = "The glide cape must be a native skeletal Glider recipe driven by a proven ABP_Cape_Glide class.";
            return false;
        }
        if (!SameNativeDonorPair(cosmetic.Playable, cosmetic.Cutscene) ||
            !SameNativeDonorPair(glider.Playable, glider.Cutscene) ||
            !SamePackage(cosmetic.Playable.SourcePackagePath, glider.Playable.SourcePackagePath) ||
            !SamePackage(cosmetic.Cutscene.SourcePackagePath, glider.Cutscene.SourcePackagePath))
        {
            detail = "The regular and glide cape recipes must come from the same native playable/cutscene donor pair.";
            return false;
        }
        if (!SameRequiredIdentity(
                glider.Playable.AnimClassObjectName,
                glider.Cutscene.AnimClassObjectName) ||
            !SameRequiredPackage(
                glider.Playable.AnimClassPackagePath,
                glider.Cutscene.AnimClassPackagePath) ||
            !SameRequiredIdentity(
                glider.Playable.AnimClassObjectPath,
                glider.Cutscene.AnimClassObjectPath))
        {
            detail = "The authored playable and cutscene glide fields must use the same exact paired-cape AnimClass.";
            return false;
        }

        detail = "The declarative cape pair is a complete native adapter candidate.";
        return true;
    }

    private static bool IsValidCosmeticAdapterDonor(SavedPartGraftDonor donor)
    {
        var componentTags = donor.ComponentTags ?? new List<string>();
        return string.Equals(donor.MeshKind, "SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
               IsCosmeticCapeAttachment(donor) &&
               componentTags.Any(tag =>
                   string.Equals(tag, "Cape", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "TtCharacterAsset.Cape", StringComparison.OrdinalIgnoreCase)) &&
               componentTags.All(tag => !string.Equals(tag, "Glider", StringComparison.OrdinalIgnoreCase)) &&
               string.IsNullOrWhiteSpace(donor.AnimClassObjectName) &&
               string.IsNullOrWhiteSpace(donor.AnimClassPackagePath) &&
               string.IsNullOrWhiteSpace(donor.AnimClassObjectPath) &&
               HasNativeComponentRecipe(donor);
    }

    private static bool IsValidGlideAdapterDonor(SavedPartGraftDonor donor)
    {
        var componentTags = donor.ComponentTags ?? new List<string>();
        return string.Equals(donor.MeshKind, "SkeletalMesh", StringComparison.OrdinalIgnoreCase) &&
               componentTags.Any(tag => string.Equals(tag, "Glider", StringComparison.OrdinalIgnoreCase)) &&
               !string.IsNullOrWhiteSpace(donor.AnimClassObjectName) &&
               !string.IsNullOrWhiteSpace(donor.AnimClassPackagePath) &&
               !string.IsNullOrWhiteSpace(donor.AnimClassObjectPath) &&
               ClassifyPairedCapeDriver(
                   donor.AnimClassObjectName,
                   donor.AnimClassPackagePath,
                   donor.AnimClassObjectPath) == PairedCapeVisibilityDriver.PairedCapable &&
               HasNativeComponentRecipe(donor);
    }

    private static bool HasNativeComponentRecipe(SavedPartGraftDonor donor) =>
        !string.IsNullOrWhiteSpace(donor.SourcePackagePath) &&
        !string.IsNullOrWhiteSpace(donor.TemplatePackagePath) &&
        SamePackage(donor.SourcePackagePath, donor.TemplatePackagePath) &&
        !string.IsNullOrWhiteSpace(donor.TemplateSlot) &&
        !string.IsNullOrWhiteSpace(donor.TemplateComponentClass) &&
        (donor.TemplateComponentClass.Contains("Skeletal", StringComparison.OrdinalIgnoreCase) ||
         donor.TemplateComponentClass.Contains("SkinnedMesh", StringComparison.OrdinalIgnoreCase)) &&
        !string.IsNullOrWhiteSpace(donor.ParentComponentOrVariableName) &&
        !string.IsNullOrWhiteSpace(donor.AttachSocket) &&
        donor.Materials is { Count: > 0 } &&
        donor.Materials.All(material =>
            !string.IsNullOrWhiteSpace(material.ObjectName) &&
            !string.IsNullOrWhiteSpace(material.PackagePath));

    private static bool SameNativeDonorPair(
        SavedPartGraftDonor playable,
        SavedPartGraftDonor cutscene) =>
        string.Equals(playable.Context, "playable", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(cutscene.Context, "cutscene", StringComparison.OrdinalIgnoreCase) &&
        !SamePackage(playable.SourcePackagePath, cutscene.SourcePackagePath) &&
        DonorPairKey(playable.SourcePackagePath).Equals(
            DonorPairKey(cutscene.SourcePackagePath),
            StringComparison.OrdinalIgnoreCase);

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

    private static string GameplayDonorPackage(NativeSuitProject project) =>
        !string.IsNullOrWhiteSpace(project.BaseProfile?.GameplayDonorPackage)
            ? UnrealPathUtil.NormalizePackagePath(project.BaseProfile!.GameplayDonorPackage)
            : UnrealPathUtil.NormalizePackagePath(project.PlayableTemplate?.PackagePath ?? "");

    private static bool SamePackage(string? left, string? right) =>
        UnrealPathUtil.NormalizePackagePath(left ?? "").Equals(
            UnrealPathUtil.NormalizePackagePath(right ?? ""),
            StringComparison.OrdinalIgnoreCase);

    private static bool SameRequiredPackage(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        SamePackage(left, right);

    private static bool SameRequiredIdentity(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static PairedCapeVisibilityDriver ClassifyPairedCapeDriver(params string?[] values)
    {
        var identities = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(IdentityLeaf)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var paired = identities.Any(identity =>
            identity.Equals("ABP_Cape_Glide", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_Cape_Glide_C", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_Cape_Glide_Batgirl_Party", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_Cape_Glide_Batgirl_Party_C", StringComparison.OrdinalIgnoreCase));
        var glideOnly = identities.Any(identity =>
            identity.Equals("ABP_Wingsuit", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_Wingsuit_C", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_TaliaGlider", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_TaliaGlider_C", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_GordonGlider", StringComparison.OrdinalIgnoreCase) ||
            identity.Equals("ABP_GordonGlider_C", StringComparison.OrdinalIgnoreCase));
        if (paired && !glideOnly)
        {
            return PairedCapeVisibilityDriver.PairedCapable;
        }
        if (glideOnly && !paired)
        {
            return PairedCapeVisibilityDriver.GlideOnly;
        }
        return PairedCapeVisibilityDriver.Unknown;
    }

    private static string IdentityLeaf(string? value)
    {
        var identity = value?.Trim().Trim('\'', '"') ?? "";
        var dot = identity.LastIndexOf('.');
        var slash = Math.Max(identity.LastIndexOf('/'), identity.LastIndexOf('\\'));
        var separator = Math.Max(dot, slash);
        if (separator >= 0 && separator + 1 < identity.Length)
        {
            identity = identity[(separator + 1)..];
        }
        return identity.Trim().Trim('\'', '"');
    }

    private static PairedCapeVisibilityDriver ClassifyLegacyPairedCapeDriver(params string?[] values)
    {
        var identity = string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (identity.Contains("Wingsuit", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Talia", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Gordon", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Catwoman", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("CatWoman", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Nightwing", StringComparison.OrdinalIgnoreCase))
        {
            return PairedCapeVisibilityDriver.GlideOnly;
        }
        if (identity.Contains("CAPE_Glide", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("glide cape", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Batgirl Party", StringComparison.OrdinalIgnoreCase))
        {
            return PairedCapeVisibilityDriver.PairedCapable;
        }
        return PairedCapeVisibilityDriver.Unknown;
    }

    public static IEnumerable<NativeSuitPartRecord> NativeGliderParts(NativeSuitPartIndex? partIndex, string search)
    {
        if (partIndex is null)
        {
            return Enumerable.Empty<NativeSuitPartRecord>();
        }

        var query = partIndex.Parts
            .Where(part =>
                part.HasMesh &&
                part.Context.Equals("playable", StringComparison.OrdinalIgnoreCase) &&
                IsNativeGliderPart(part));

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(part =>
                MatchesSearch(search,
                    GliderPresetLabel(part),
                    part.MeshObjectName,
                    part.MeshObjectPath,
                    part.MeshPackagePath,
                    part.AnimClassObjectName,
                    part.AnimClassObjectPath,
                    part.Slot,
                    part.CharacterFolder,
                    string.Join(" ", part.Materials.Select(material => $"{material.ObjectName} {material.ObjectPath}"))));
        }

        return query
            .GroupBy(GliderPresetKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(part => part.ComponentTags.Any(tag => tag.Equals("Glider", StringComparison.OrdinalIgnoreCase)))
                .ThenBy(part => GliderSlotRank(part.Slot))
                .ThenBy(part => part.CharacterFolder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(part => part.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(GliderSlotRankForPart)
            .ThenBy(GliderPresetLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(part => part.SourcePackagePath, StringComparer.OrdinalIgnoreCase);
    }

    public static NativeSuitPartRecord? FindWingsuitPartForMaterial(NativeSuitPartIndex? partIndex, string materialPath, string context)
    {
        var chr = WingsuitCharFromMaterial(materialPath);
        if (partIndex is null || chr is null)
        {
            return null;
        }

        var meshName = $"SK_GA_Wingsuit_{chr}";
        return partIndex.Parts.FirstOrDefault(part =>
            part.HasMesh &&
            part.Context.Equals(context, StringComparison.OrdinalIgnoreCase) &&
            part.MeshObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase));
    }

    public static string GliderPresetLabel(NativeSuitPartRecord part)
    {
        var character = HumanizeCharacter(part.CharacterFolder);
        var kind = KindForPart(part);
        var visual = kind switch
        {
            GliderVisualKind.GlideCape when part.MeshObjectName.Contains("Short", StringComparison.OrdinalIgnoreCase) => "short glide cape",
            GliderVisualKind.GlideCape => "glide cape",
            GliderVisualKind.Wingsuit => "wingsuit",
            _ => "glider"
        };

        var variant = part.MeshObjectName.EndsWith("_2", StringComparison.OrdinalIgnoreCase)
            ? " 2"
            : "";
        return $"{character} {visual}{variant}".Trim();
    }

    public static GliderVisualKind KindForPart(NativeSuitPartRecord part)
    {
        var name = $"{part.MeshObjectName} {part.MeshPackagePath} {part.AnimClassObjectName} {part.AnimClassPackagePath}";
        if (name.Contains("Wingsuit", StringComparison.OrdinalIgnoreCase))
        {
            return GliderVisualKind.Wingsuit;
        }

        if (HasGlideTag(part) ||
            name.Contains("CAPE_Glide", StringComparison.OrdinalIgnoreCase))
        {
            return GliderVisualKind.GlideCape;
        }

        return GliderVisualKind.CharacterGlider;
    }

    public static string KindLabel(NativeSuitPartRecord part) => KindForPart(part) switch
    {
        GliderVisualKind.GlideCape => "Glide cape",
        GliderVisualKind.Wingsuit => "Wingsuit",
        _ => "Character glider"
    };

    public static string RoleLabel(NativeSuitPartRecord part) => KindForPart(part) switch
    {
        GliderVisualKind.GlideCape => "glide-only cape visual",
        GliderVisualKind.Wingsuit => "glide-only wingsuit visual",
        _ => "glide-only character visual"
    };

    public static GliderMaterialCompatibilityResult CheckMaterialCompatibility(
        NativeSuitPartRecord? glideVisual,
        string materialPath)
    {
        if (glideVisual is null)
        {
            return new GliderMaterialCompatibilityResult
            {
                Kind = GliderMaterialCompatibility.Unknown,
                Title = "Glide visual not identified",
                Detail = "Batcomputer cannot compare this material until a native glide visual has been selected. Preview it before building."
            };
        }

        var candidate = NormalizeMaterialPackage(materialPath);
        var nativeMaterials = glideVisual.Materials
            .Select(material => NormalizeMaterialPackage(string.IsNullOrWhiteSpace(material.PackagePath) ? material.ObjectPath : material.PackagePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (nativeMaterials.Count == 0)
        {
            return new GliderMaterialCompatibilityResult
            {
                Kind = GliderMaterialCompatibility.Unknown,
                Title = "No native material record",
                Detail = "This glide component has no indexed override materials, so Batcomputer cannot check its UV family. Preview it before building."
            };
        }

        if (nativeMaterials.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            return new GliderMaterialCompatibilityResult
            {
                Kind = GliderMaterialCompatibility.NativeMatch,
                Title = "Native glide material",
                Detail = "This is one of the selected glide visual's original material overrides. Its UV layout is the expected match."
            };
        }

        if (candidate.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
        {
            return new GliderMaterialCompatibilityResult
            {
                Kind = GliderMaterialCompatibility.CustomMaterial,
                Title = "Custom glide material",
                Detail = "Custom materials can be correct, but their source UV family is not stored with the asset. Check the 3D preview and test in-game before release."
            };
        }

        return new GliderMaterialCompatibilityResult
        {
            Kind = GliderMaterialCompatibility.DifferentNativeMaterial,
            Title = "Different native material family",
            Detail = "This material is not one of this glide visual's native overrides. It may use a different UV layout and appear stretched, tiled, or misplaced."
        };
    }

    public static string MountLabel(NativeSuitPartRecord part)
    {
        var socket = part.AttachSocket?.Trim() ?? "";
        if (socket.Contains("Chest", StringComparison.OrdinalIgnoreCase))
        {
            return "Chest-mounted";
        }
        if (socket.Equals("Root", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(socket))
        {
            return "Root-mounted";
        }
        return $"{socket}-mounted";
    }

    public static string GliderPresetSubtitle(NativeSuitPartRecord part)
    {
        var materialCount = part.Materials.Count;
        var anim = string.IsNullOrWhiteSpace(part.AnimClassObjectName)
            ? "no anim"
            : part.AnimClassObjectName.Replace("_C", "", StringComparison.OrdinalIgnoreCase);
        return $"{RoleLabel(part)} | {MountLabel(part)} | {anim} | {materialCount} mat{(materialCount == 1 ? "" : "s")}";
    }

    public static NativeSuitPartRecord WithWingsuitDecalOverride(NativeSuitPartRecord part, string materialPath)
    {
        var clone = ClonePart(part);
        var materialPackage = PackagePathFromObjectPath(materialPath);
        var materialName = AssetName(materialPackage);
        var materialRef = new NativeSuitObjectRef
        {
            ObjectName = materialName,
            PackagePath = materialPackage,
            ObjectPath = $"{materialPackage}.{materialName}",
            ClassName = "MaterialInstanceConstant"
        };

        var slot = clone.Materials.FindIndex(material =>
            material.ObjectName.Contains("DECAL", StringComparison.OrdinalIgnoreCase) ||
            material.ObjectPath.Contains("DECAL", StringComparison.OrdinalIgnoreCase));
        if (slot < 0 && clone.Materials.Count > 0)
        {
            slot = 0;
        }

        if (slot >= 0)
        {
            clone.Materials[slot] = materialRef;
        }
        else
        {
            clone.Materials.Add(materialRef);
        }

        clone.Notes = string.IsNullOrWhiteSpace(clone.Notes)
            ? $"Wingsuit decal override: {materialName}"
            : $"{clone.Notes} | Wingsuit decal override: {materialName}";
        return clone;
    }

    /// <summary>The wingsuit character name embedded in a decal/mesh path (.../GA_Wingsuit_Char/...), or null.</summary>
    public static string? WingsuitCharFromMaterial(string gliderMaterialGamePath)
    {
        if (string.IsNullOrWhiteSpace(gliderMaterialGamePath)) return null;
        var norm = gliderMaterialGamePath.Contains('.') ? gliderMaterialGamePath[..gliderMaterialGamePath.IndexOf('.')] : gliderMaterialGamePath;
        const string marker = "/GA_Wingsuit_";
        var i = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var after = norm[(i + marker.Length)..];
        var chr = after.Contains('/') ? after[..after.IndexOf('/')] : after;
        return string.IsNullOrWhiteSpace(chr) ? null : chr;
    }

    /// <summary>
    /// The donor character's glide ANIMATION sets for a glider preset, injected as parent
    /// sets so the body plays that character's glide pose. A cross-type glider (wingsuit on
    /// a cape base) needs this or the membrane collapses (invisible). Returns ("","") when
    /// the character can't be resolved. Batman and Batgirl are included: a custom base
    /// does not necessarily inherit their traversal sets merely because the donor visual
    /// did. Paths follow the confirmed convention
    /// LAS_Traversal_&lt;Char&gt; + MAS_Glide_&lt;Char&gt; (findings doc §12).
    /// </summary>
    public static (string Las, string Mas) GliderAnimSetsForPart(NativeSuitPartRecord part)
    {
        var chr = GliderAnimCharacter(part);
        if (string.IsNullOrWhiteSpace(chr))
        {
            return ("", "");
        }
        return ($"/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_{chr}",
                $"/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_{chr}");
    }

    /// <summary>
    /// The character whose glide animation a preset needs. Uses the source character folder
    /// (the character who natively glides with this visual).
    /// </summary>
    private static string GliderAnimCharacter(NativeSuitPartRecord part)
    {
        var chr = (part.CharacterFolder ?? "").Trim();
        if (string.IsNullOrWhiteSpace(chr))
        {
            return "";
        }
        return chr;
    }

    private static string GliderPresetKey(NativeSuitPartRecord part)
    {
        var mesh = !string.IsNullOrWhiteSpace(part.MeshObjectName) ? part.MeshObjectName : part.MeshPackagePath;
        var anim = !string.IsNullOrWhiteSpace(part.AnimClassObjectName) ? part.AnimClassObjectName : part.AnimClassPackagePath;
        return $"{part.CharacterFolder}|{mesh}|{anim}";
    }

    private static int GliderSlotRankForPart(NativeSuitPartRecord part) => GliderSlotRank(part.Slot);

    private static int GliderSlotRank(string slot) => slot.ToLowerInvariant() switch
    {
        "cape" => 0,
        "torso" => 1,
        "torso2" => 2,
        _ => 10
    };

    private static bool HasGlideTag(NativeSuitPartRecord part) => part.ComponentTags.Any(tag =>
        tag.Equals("Glider", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("GlideCape", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeMaterialPackage(string path)
    {
        var trimmed = path?.Trim() ?? "";
        var dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    private static bool MatchesSearch(string search, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var haystack = string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return search
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static NativeSuitPartRecord ClonePart(NativeSuitPartRecord part) => new()
    {
        SourcePackagePath = part.SourcePackagePath,
        SourceUasset = part.SourceUasset,
        ContentRelativePath = part.ContentRelativePath,
        CharacterFolder = part.CharacterFolder,
        Stem = part.Stem,
        Context = part.Context,
        Slot = part.Slot,
        ComponentClass = part.ComponentClass,
        ComponentTemplateExport = part.ComponentTemplateExport,
        ComponentTemplateExportIndex = part.ComponentTemplateExportIndex,
        ScsNodeExport = part.ScsNodeExport,
        ScsNodeExportIndex = part.ScsNodeExportIndex,
        ParentComponentOrVariableName = part.ParentComponentOrVariableName,
        AttachSocket = part.AttachSocket,
        MeshKind = part.MeshKind,
        MeshObjectName = part.MeshObjectName,
        MeshPackagePath = part.MeshPackagePath,
        MeshObjectPath = part.MeshObjectPath,
        AnimClassObjectName = part.AnimClassObjectName,
        AnimClassPackagePath = part.AnimClassPackagePath,
        AnimClassObjectPath = part.AnimClassObjectPath,
        Materials = part.Materials.Select(material => new NativeSuitObjectRef
        {
            ObjectName = material.ObjectName,
            PackagePath = material.PackagePath,
            ObjectPath = material.ObjectPath,
            ClassName = material.ClassName
        }).ToList(),
        ComponentTags = part.ComponentTags.ToList(),
        HasClassChildProperty = part.HasClassChildProperty,
        IsKnownVisualSlot = part.IsKnownVisualSlot,
        IsLikelyGraftCandidate = part.IsLikelyGraftCandidate,
        SemanticKind = part.SemanticKind,
        TemplatePackagePath = part.TemplatePackagePath,
        TemplateUasset = part.TemplateUasset,
        TemplateSlot = part.TemplateSlot,
        TemplateComponentClass = part.TemplateComponentClass,
        IsSynthesized = part.IsSynthesized,
        RecipeKey = part.RecipeKey,
        Notes = part.Notes
    };

    private static string PackagePathFromObjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var trimmed = path.Trim();
        var dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    private static string AssetName(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return "";
        }

        var path = PackagePathFromObjectPath(packagePath);
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string HumanizeCharacter(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        return value.Replace('_', ' ');
    }
}
