namespace Batcomputer;

/// <summary>
/// Shared classification and compatibility rules for character attachment recipes.
/// A mesh path is not enough to graft a cooked component safely: the component class,
/// parent, socket, tags, and visual family all influence how the game treats it.
/// </summary>
public static class PartRecipeService
{
    public static string SemanticKind(NativeSuitPartRecord part) =>
        SemanticKind(part.Slot, part.MeshObjectPath, part.MeshObjectName, part.ComponentTags);

    public static string SemanticKind(
        string? slot,
        string? meshObjectPath,
        string? meshObjectName,
        IEnumerable<string>? componentTags = null)
    {
        var s = (slot ?? string.Empty).Trim();
        var probe = $"{s} {meshObjectPath} {meshObjectName}";
        var tags = componentTags ?? Array.Empty<string>();

        bool Has(string value) => probe.Contains(value, StringComparison.OrdinalIgnoreCase);
        bool Tag(string value) => tags.Any(t => t.Equals(value, StringComparison.OrdinalIgnoreCase));

        if (Has("SM_HAIR") || Has("SK_HAIR") ||
            Has("SlickBack") || Has("SweptBack") || Has("WidowsPeak") ||
            Has("CombOver") || Has("ShortCoiled") || Has("Balding") ||
            s.Equals("Hair", StringComparison.OrdinalIgnoreCase))
            return "Hair";
        if (Has("SM_HAT") || Has("SK_HAT") || s.Equals("Hat", StringComparison.OrdinalIgnoreCase))
            return "Hat";
        if (s.Equals("Torso2", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("TorsoA", StringComparison.OrdinalIgnoreCase) ||
            Tag("TtCharacterAsset.Torso2"))
            return "Torso2";
        if (s.Equals("Torso", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("Torso1", StringComparison.OrdinalIgnoreCase) ||
            Tag("Glider") || Tag("GlideCape") || Has("CAPE_Glide"))
            return "Torso";
        if (s.StartsWith("Cape", StringComparison.OrdinalIgnoreCase) ||
            Has("/CAPe/") || Tag("TtCharacterAsset.Cape") || Tag("Cape"))
            return "Cape";
        if (s.Equals("Face", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("Face1", StringComparison.OrdinalIgnoreCase) || Has("LEGOface"))
            return "Face";
        if (s.Equals("Head", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("CustomHead", StringComparison.OrdinalIgnoreCase) ||
            Has("Cowl") || Tag("Cowl") || Tag("TtCharacterAsset.Head"))
            return "Head";
        if (s.Equals("Hip", StringComparison.OrdinalIgnoreCase)) return "Hip";
        if (s.Equals("Collar", StringComparison.OrdinalIgnoreCase)) return "Collar";
        if (s.Equals("Spine", StringComparison.OrdinalIgnoreCase)) return "Spine";
        if (s.Equals("Costume", StringComparison.OrdinalIgnoreCase)) return "Costume";
        if (s.Equals("Arm", StringComparison.OrdinalIgnoreCase)) return "Arm";
        if (s.Equals("LWrist", StringComparison.OrdinalIgnoreCase)) return "LWrist";
        if (s.Equals("Batpack", StringComparison.OrdinalIgnoreCase)) return "Batpack";

        return string.IsNullOrWhiteSpace(s) ? "Other" : s;
    }

    /// <summary>How much of a part's native recipe we actually know.</summary>
    public enum RecipeConfidence
    {
        /// <summary>Observed straight off a cooked native component - class, mesh kind, and attachment are known.</summary>
        Native,
        /// <summary>Enough to graft, but some recipe detail was inferred rather than observed.</summary>
        Inferred,
        /// <summary>Grafting this is risky - the recipe is incomplete or self-contradictory.</summary>
        Unsafe,
    }

    /// <summary>
    /// Classifies how trustworthy a part's graft recipe is, with a human reason. This makes the
    /// scoring the graft services already rely on visible BEFORE a drop, instead of discovering a
    /// bad recipe as an in-game crash. Conservative by design: unknown &gt; optimistic.
    /// </summary>
    public static (RecipeConfidence Level, string Reason) Confidence(NativeSuitPartRecord part)
    {
        var hasClass = !string.IsNullOrWhiteSpace(part.ComponentClass);
        var hasMeshKind = !string.IsNullOrWhiteSpace(part.MeshKind);
        var isStaticClass = part.ComponentClass.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase);
        var isSkeletalClass = part.ComponentClass.Contains("Skeletal", StringComparison.OrdinalIgnoreCase) ||
                              part.ComponentClass.Contains("SkinnedMesh", StringComparison.OrdinalIgnoreCase);
        var isStaticMesh = part.MeshKind.Contains("Static", StringComparison.OrdinalIgnoreCase);
        var isSkeletalMesh = part.MeshKind.Contains("Skel", StringComparison.OrdinalIgnoreCase);

        // Hard contradictions - a class/mesh mismatch is the cooked-loader crash signature.
        if (hasClass && hasMeshKind && ((isStaticClass && isSkeletalMesh) || (isSkeletalClass && isStaticMesh)))
        {
            return (RecipeConfidence.Unsafe, $"component class ({part.ComponentClass}) disagrees with mesh kind ({part.MeshKind}) — grafting would need an unsafe class conversion.");
        }
        if (!part.HasMesh)
        {
            return (RecipeConfidence.Unsafe, "no mesh recorded for this component.");
        }
        if (!hasClass)
        {
            return (RecipeConfidence.Unsafe, "component class unknown — no safe native pattern to clone.");
        }

        // Synthesized entries were assembled by the tool, not observed on a cooked component.
        if (part.IsSynthesized)
        {
            return (RecipeConfidence.Inferred, "synthesized by the tool rather than observed on a native component.");
        }

        // Attachments need a real parent/socket; body-ish slots legitimately have neither.
        var kind = string.IsNullOrWhiteSpace(part.SemanticKind) ? SemanticKind(part) : part.SemanticKind;
        var needsAttachment = kind is "Hair" or "Hat" or "Torso2" or "Cape" or "Collar" or "Batpack";
        if (needsAttachment &&
            string.IsNullOrWhiteSpace(part.AttachSocket) &&
            string.IsNullOrWhiteSpace(part.ParentComponentOrVariableName))
        {
            return (RecipeConfidence.Inferred, $"{kind} attachment has no recorded socket or parent — attachment will be inferred.");
        }

        if (!part.IsKnownVisualSlot)
        {
            return (RecipeConfidence.Inferred, $"slot '{part.Slot}' isn't a known visual slot — semantic kind was inferred.");
        }

        return (RecipeConfidence.Native, $"observed native {kind} component ({part.ComponentClass}).");
    }

    public static string BuildRecipeKey(NativeSuitPartRecord part)
    {
        var values = new[]
        {
            part.SemanticKind,
            part.MeshKind,
            part.ComponentClass,
            part.AttachSocket,
            part.ParentComponentOrVariableName,
            string.Join(",", part.ComponentTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        };
        return string.Join("|", values.Select(value => (value ?? string.Empty).Trim()));
    }

    public static NativeSuitPartRecord Clone(NativeSuitPartRecord source) => new()
    {
        SourcePackagePath = source.SourcePackagePath,
        SourceUasset = source.SourceUasset,
        ContentRelativePath = source.ContentRelativePath,
        CharacterFolder = source.CharacterFolder,
        Stem = source.Stem,
        Context = source.Context,
        Slot = source.Slot,
        ComponentClass = source.ComponentClass,
        ComponentTemplateExport = source.ComponentTemplateExport,
        ComponentTemplateExportIndex = source.ComponentTemplateExportIndex,
        ScsNodeExport = source.ScsNodeExport,
        ScsNodeExportIndex = source.ScsNodeExportIndex,
        ParentComponentOrVariableName = source.ParentComponentOrVariableName,
        AttachSocket = source.AttachSocket,
        MeshKind = source.MeshKind,
        MeshObjectName = source.MeshObjectName,
        MeshPackagePath = source.MeshPackagePath,
        MeshObjectPath = source.MeshObjectPath,
        AnimClassObjectName = source.AnimClassObjectName,
        AnimClassPackagePath = source.AnimClassPackagePath,
        AnimClassObjectPath = source.AnimClassObjectPath,
        Materials = source.Materials.Select(m => new NativeSuitObjectRef
        {
            ObjectName = m.ObjectName,
            PackagePath = m.PackagePath,
            ObjectPath = m.ObjectPath,
            ClassName = m.ClassName
        }).ToList(),
        ComponentTags = source.ComponentTags.ToList(),
        HasClassChildProperty = source.HasClassChildProperty,
        IsKnownVisualSlot = source.IsKnownVisualSlot,
        IsLikelyGraftCandidate = source.IsLikelyGraftCandidate,
        SemanticKind = source.SemanticKind,
        TemplatePackagePath = source.TemplatePackagePath,
        TemplateUasset = source.TemplateUasset,
        TemplateSlot = source.TemplateSlot,
        TemplateComponentClass = source.TemplateComponentClass,
        IsSynthesized = source.IsSynthesized,
        RecipeKey = source.RecipeKey,
        Notes = source.Notes
    };
}
