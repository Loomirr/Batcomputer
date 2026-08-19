namespace Batcomputer;

/// <summary>
/// Surfaces the character-attachment library (faces, hair, hats) straight from
/// the shipped catalog (gamedata/*.json) so users never have to extract or build
/// a part index for them. Everything here is derived from cataloged asset paths:
/// <list type="bullet">
/// <item>Faces are just <c>MI_FACE_*</c> materials on the shared SK_LEGOface mesh
/// - swapping a face = assigning that material to the Face slot.</item>
/// <item>Hair/hats are <c>SM_HAIR_*</c>/<c>SM_HAT_*</c> static meshes with sibling
/// <c>MI_*</c> materials - grafting one = the normal part graft, which only needs
/// the mesh + material package refs (no donor uasset), so we can synthesize the
/// <see cref="NativeSuitPartRecord"/> from catalog paths alone.</item>
/// </list>
/// </summary>
public static class AttachmentCatalogService
{
    private const string FaceFolder = "/Attachments/Face/";
    private const string HairFolder = "/Attachments/HAIR/";
    private const string HatFolder = "/Attachments/HAT/";

    /// <summary>Face materials (MI_FACE_*), optionally limited to one character folder.</summary>
    public static IEnumerable<GameDataAsset> FaceMaterials(string? characterFolder = null)
    {
        var gd = GameDataService.Instance;
        return gd.AssetsOfClass("MaterialInstanceConstant")
            .Where(a => a.Path.Contains(FaceFolder, StringComparison.OrdinalIgnoreCase) &&
                        AssetName(a.Path).StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase))
            .Where(a => string.IsNullOrWhiteSpace(characterFolder) ||
                        FaceCharacterFolder(a.Path).Equals(characterFolder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Distinct character sub-folders under Attachments/Face (for the type dropdown).</summary>
    public static IReadOnlyList<string> FaceCharacterFolders() =>
        FaceMaterials()
            .Select(a => FaceCharacterFolder(a.Path))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Synthesized hair parts. Two flavors, each grafted onto a MATCHING-KIND host so
    /// no component-class conversion is ever needed (conversion corrupts cooked BPs):
    /// <list type="bullet">
    /// <item>SKELETAL hair (SK_HAIR_* + ABP_HAIR_*) → a "Hair" SkeletalMeshComponent
    /// (Catwoman-style, animated). Needs a skeletal donor node to clone.</item>
    /// <item>STATIC hair (SM_HAIR_*) maps to the native "Head" visual slot at
    /// HeadStud_Attach_Socket (ThomasWayne / base-game civilian style). Native static
    /// hair/hats are tagged TtCharacterAsset.Head; "NeckPeg" is a socket/helper concept,
    /// not a character-asset slot.</item>
    /// </list>
    /// The graft path (<see cref="PartGraftService"/>) picks a clone donor whose class
    /// matches MeshKind and fails cleanly if no same-kind donor exists.
    /// </summary>
    public static IEnumerable<NativeSuitPartRecord> HairParts() =>
        AttachmentParts(HairFolder, "SK_HAIR_", "ABP_HAIR_", "MI_HAIR_", "Hair", skeletal: true)
            .Concat(AttachmentParts(HairFolder, "SM_HAIR_", null, "MI_HAIR_", "Head", skeletal: false));

    /// <summary>Synthesized hat parts: skeletal SK_HAT_* → "Hat", static SM_HAT_* → "Head".</summary>
    public static IEnumerable<NativeSuitPartRecord> HatParts() =>
        AttachmentParts(HatFolder, "SK_HAT_", "ABP_HAT_", "MI_HAT_", "Hat", skeletal: true)
            .Concat(AttachmentParts(HatFolder, "SM_HAT_", null, "MI_HAT_", "Head", skeletal: false));

    private static IEnumerable<NativeSuitPartRecord> AttachmentParts(
        string folder, string meshPrefix, string? animPrefix, string materialPrefix, string slot, bool skeletal)
    {
        var gd = GameDataService.Instance;
        var meshClass = skeletal ? "SkeletalMesh" : "StaticMesh";

        // Pre-index sibling materials + anim BPs by folder (each style folder holds
        // SK_/SM_/ABP_/MI_ for that style).
        List<GameDataAsset> InFolder(string cls, string prefix) =>
            gd.AssetsOfClass(cls)
                .Where(a => a.Path.Contains(folder, StringComparison.OrdinalIgnoreCase) &&
                            AssetName(a.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var materialsByFolder = InFolder("MaterialInstanceConstant", materialPrefix)
            .GroupBy(a => PackageFolder(a.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var animByFolder = skeletal && animPrefix is not null
            ? InFolder("AnimBlueprintGeneratedClass", animPrefix)
                .GroupBy(a => PackageFolder(a.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, GameDataAsset>(StringComparer.OrdinalIgnoreCase);

        foreach (var mesh in InFolder(meshClass, meshPrefix).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
        {
            var meshName = AssetName(mesh.Path);
            var meshFolder = PackageFolder(mesh.Path);
            materialsByFolder.TryGetValue(meshFolder, out var mats);
            animByFolder.TryGetValue(meshFolder, out var anim);

            var record = new NativeSuitPartRecord
            {
                SourcePackagePath = mesh.Path,
                SourceUasset = meshName + ".uasset",
                CharacterFolder = PackageLeafFolder(mesh.Path),
                Stem = meshName,
                Context = "playable",
                Slot = slot,
                ComponentClass = skeletal ? "SkeletalMeshComponentBudgeted" : "StaticMeshComponent",
                MeshKind = meshClass,
                MeshObjectName = meshName,
                MeshPackagePath = mesh.Path,
                MeshObjectPath = $"{mesh.Path}.{meshName}",
                Materials = (mats ?? new List<GameDataAsset>())
                    .OrderBy(m => VariantRank(AssetName(m.Path)))
                    .Take(1)
                    .Select(m => new NativeSuitObjectRef
                    {
                        ObjectName = AssetName(m.Path),
                        PackagePath = m.Path,
                        ObjectPath = $"{m.Path}.{AssetName(m.Path)}",
                        ClassName = "MaterialInstanceConstant"
                    })
                    .ToList(),
                ComponentTags = new List<string> { $"TtCharacterAsset.{slot}" },
                IsKnownVisualSlot = true,
                IsLikelyGraftCandidate = true,
                SemanticKind = PartRecipeService.SemanticKind(slot, mesh.Path, meshName,
                    new[] { $"TtCharacterAsset.{slot}" }),
                IsSynthesized = true,
                Notes = $"Game attachment ({slot}) — built from the extracted registry."
            };
            if (anim is not null)
            {
                record.AnimClassObjectName = AssetName(anim.Path) + "_C";
                record.AnimClassPackagePath = anim.Path;
                record.AnimClassObjectPath = $"{anim.Path}.{AssetName(anim.Path)}_C";
            }
            record.RecipeKey = PartRecipeService.BuildRecipeKey(record);
            yield return record;
        }
    }

    // Lower rank = preferred material variant (plain over CUT/NPC/EOM cutscene variants).
    private static int VariantRank(string name)
    {
        if (name.EndsWith("_CUT", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.EndsWith("_NPC", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.EndsWith("_EOM", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    public static string AssetName(string packagePath) =>
        packagePath.Contains('/') ? packagePath[(packagePath.LastIndexOf('/') + 1)..] : packagePath;

    private static string PackageFolder(string packagePath) =>
        packagePath.Contains('/') ? packagePath[..packagePath.LastIndexOf('/')] : packagePath;

    private static string PackageLeafFolder(string packagePath)
    {
        var folder = PackageFolder(packagePath);
        return folder.Contains('/') ? folder[(folder.LastIndexOf('/') + 1)..] : folder;
    }

    private static string FaceCharacterFolder(string packagePath) => PackageLeafFolder(packagePath);
}
