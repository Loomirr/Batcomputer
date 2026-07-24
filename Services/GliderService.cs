namespace Batcomputer;

/// <summary>
/// Helpers for selecting and applying native glide visuals. The important rule:
/// gliders should come from real indexed character components whenever possible
/// (mesh + anim BP + all material slots + component tags), not from a synthetic
/// one-material record.
/// </summary>
public static class GliderService
{
    private const string WingsuitAbp = "/Game/Animation/LEGOfig/Nightwing/Traversal/ABP_Wingsuit";

    public static bool IsNativeGliderPart(NativeSuitPartRecord part)
    {
        if (part.ComponentTags.Any(tag => tag.Equals("Glider", StringComparison.OrdinalIgnoreCase)))
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
               haystack.Contains("Glide", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("Glider", StringComparison.OrdinalIgnoreCase);
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
        var name = !string.IsNullOrWhiteSpace(part.MeshObjectName)
            ? part.MeshObjectName
            : AssetName(part.MeshPackagePath);

        foreach (var prefix in new[]
        {
            "SK_GA_Wingsuit_",
            "SK_GA_Glider_",
            "SM_GA_Glider_",
            "SK_CAPE_",
            "SM_CAPE_",
            "SK_",
            "SM_"
        })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(name) ? part.CharacterFolder : name;
    }

    public static string GliderPresetSubtitle(NativeSuitPartRecord part)
    {
        var materialCount = part.Materials.Count;
        var anim = string.IsNullOrWhiteSpace(part.AnimClassObjectName)
            ? "no anim"
            : part.AnimClassObjectName.Replace("_C", "", StringComparison.OrdinalIgnoreCase);
        return $"{part.Slot} - {anim} - {materialCount} material{(materialCount == 1 ? "" : "s")}";
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
    /// Legacy fallback for old material-driven tests. New glider UI uses the
    /// indexed native glider records above so we preserve all slots/tags/anim data.
    /// </summary>
    public static NativeSuitPartRecord? BuildWingsuitPart(string gliderMaterialGamePath)
    {
        var chr = WingsuitCharFromMaterial(gliderMaterialGamePath);
        if (chr is null) return null;

        var meshPkg = $"/Game/Models/Gadgets/GA_Wingsuit_{chr}/SK_GA_Wingsuit_{chr}";
        var meshName = $"SK_GA_Wingsuit_{chr}";
        var matPkg = gliderMaterialGamePath.Contains('.') ? gliderMaterialGamePath[..gliderMaterialGamePath.IndexOf('.')] : gliderMaterialGamePath;
        var matName = matPkg[(matPkg.LastIndexOf('/') + 1)..];

        var part = new NativeSuitPartRecord
        {
            SourcePackagePath = meshPkg,
            SourceUasset = meshName + ".uasset",
            CharacterFolder = $"GA_Wingsuit_{chr}",
            Stem = meshName,
            Context = "playable",
            Slot = "Cape",
            ComponentClass = "SkeletalMeshComponentBudgeted",
            MeshKind = "SkeletalMesh",
            MeshObjectName = meshName,
            MeshPackagePath = meshPkg,
            MeshObjectPath = $"{meshPkg}.{meshName}",
            AnimClassObjectName = "ABP_Wingsuit_C",
            AnimClassPackagePath = WingsuitAbp,
            AnimClassObjectPath = $"{WingsuitAbp}.ABP_Wingsuit_C",
            Materials = new List<NativeSuitObjectRef>
            {
                new()
                {
                    ObjectName = matName,
                    PackagePath = matPkg,
                    ObjectPath = $"{matPkg}.{matName}",
                    ClassName = "MaterialInstanceConstant"
                }
            },
            ComponentTags = new List<string> { "TtCharacterAsset.Cape", "Glider" },
            IsKnownVisualSlot = true,
            IsLikelyGraftCandidate = true,
            SemanticKind = "Cape",
            IsSynthesized = true,
            Notes = $"Wingsuit glide visual ({chr}) - synthesized legacy glider graft."
        };
        part.RecipeKey = PartRecipeService.BuildRecipeKey(part);
        return part;
    }

    /// <summary>
    /// The donor character's glide ANIMATION sets for a glider preset, injected as parent
    /// sets so the body plays that character's glide pose. A cross-type glider (wingsuit on
    /// a cape base) needs this or the membrane collapses (invisible). Returns ("","") when
    /// the character can't be resolved or is Batman/Batgirl (the minifig-default cape glide
    /// - no injection needed). Paths follow the confirmed convention
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
    /// (the character who natively glides with this visual). Batman/Batgirl cape-glides are
    /// the minifig default - their body already poses for a cape, so no anim injection.
    /// </summary>
    private static string GliderAnimCharacter(NativeSuitPartRecord part)
    {
        var chr = (part.CharacterFolder ?? "").Trim();
        if (string.IsNullOrWhiteSpace(chr) ||
            chr.Equals("Batman", StringComparison.OrdinalIgnoreCase) ||
            chr.Equals("Batgirl", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        return chr;
    }

    private static string GliderPresetKey(NativeSuitPartRecord part)
    {
        var mesh = !string.IsNullOrWhiteSpace(part.MeshObjectName) ? part.MeshObjectName : part.MeshPackagePath;
        var anim = !string.IsNullOrWhiteSpace(part.AnimClassObjectName) ? part.AnimClassObjectName : part.AnimClassPackagePath;
        return $"{part.CharacterFolder}|{part.Slot}|{mesh}|{anim}";
    }

    private static int GliderSlotRankForPart(NativeSuitPartRecord part) => GliderSlotRank(part.Slot);

    private static int GliderSlotRank(string slot) => slot.ToLowerInvariant() switch
    {
        "cape" => 0,
        "torso" => 1,
        "torso2" => 2,
        _ => 10
    };

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
}
