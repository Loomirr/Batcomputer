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

        var hasGliderTag = donor.ComponentTags.Any(tag =>
            tag.Equals("Glider", StringComparison.OrdinalIgnoreCase));
        if (hasGliderTag)
        {
            return false;
        }

        return donor.ComponentTags.Any(tag =>
                   tag.Equals("Cape", StringComparison.OrdinalIgnoreCase) ||
                   tag.Equals("TtCharacterAsset.Cape", StringComparison.OrdinalIgnoreCase)) ||
               donor.MeshObjectPath.Contains("Cape", StringComparison.OrdinalIgnoreCase) ||
               donor.Stem.Contains("Cape", StringComparison.OrdinalIgnoreCase);
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
