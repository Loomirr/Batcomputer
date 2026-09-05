using System.Collections.Concurrent;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// Catalogues the exact native LEGOfig body variants shipped by the game and transactionally
/// rewrites CharacterMesh0 in a disposable/generated Content root. Deliberately does not invent
/// reduced-body 08 variants that do not exist in the game files.
/// </summary>
public sealed class NativeBodyProfileService
{
    public const string IntegratedHeadPolicy = "integrated";
    public const string IntentionallyAbsentHeadPolicy = "intentionally-absent";
    public const string SharedSkeleton = "/Game/Characters/LEGOfig/SKEL_LEGOfig";

    public sealed record FileResult(
        string Role,
        string Path,
        bool Success,
        string Detail,
        bool TransientFileLock = false);

    public sealed class Result
    {
        public List<FileResult> Files { get; } = new();
        public bool Success => Files.Count == 2 && Files.All(file => file.Success);
        public bool TransientFileLock => Files.Any(file => file.TransientFileLock);
    }

    private sealed record Definition(
        string Id,
        string DisplayName,
        string MeshPackagePath,
        string GeometryFamily,
        string HeadPolicy,
        string EvidenceTier,
        string[] MissingRegions,
        string[] Warnings);

    private sealed record ProtectedGameplayContract(
        string ParentClass,
        string ParentPackage,
        string RequiredScsComponents);

    private static readonly Definition[] Definitions =
    [
        new("minifig-standard", "Minifig", "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig",
            "Minifig", IntegratedHeadPolicy, "shipped-standard", [], []),
        new("minifig-08", "Minifig 08", "/Game/Characters/LEGOfig/SK_Legofig_Minifig_08",
            "Minifig08", IntegratedHeadPolicy, "shipped-standard", [],
            ["Use materials authored for the 08 body layout."]),
        new("minifig-headless", "Minifig — headless", "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig_Body",
            "Minifig", IntentionallyAbsentHeadPolicy, "native-character", ["Head"],
            ["Head, face, hair, and hat attachments can float unless you add a compatible native replacement."]),
        new("minifig-armless", "Minifig — armless", "/Game/Characters/LEGOfig/SK_LEGOFig_Minifig_Armless",
            "Minifig", IntegratedHeadPolicy, "native-character", ["Left arm", "Right arm", "Left hand", "Right hand"],
            ["Hand equipment and wrist attachments can float. Add the character's native wing/arm recipe when needed."]),
        new("minifig-no-left-hand", "Minifig — no left hand", "/Game/Characters/LEGOfig/SK_LEGOFig_Minifig_NoLeftHand",
            "Minifig", IntegratedHeadPolicy, "native-character", ["Left hand"],
            ["Left-hand equipment can float. Add a compatible hook or replacement part at the left wrist when needed."]),
        new("minifig-no-upper-body", "Minifig — no upper body", "/Game/Characters/LEGOfig/SK_LEGOFig_Minifig_NoUpperBody",
            "Minifig", IntegratedHeadPolicy, "native-playable", ["Torso", "Left arm", "Right arm", "Left hand", "Right hand"],
            ["Upper-body equipment and attachments can float. Add the visual character's native BrickBody/upper-body recipe when needed."]),
        new("smallfig-standard", "Smallfig", "/Game/Characters/LEGOfig/SK_LEGOfig_Smallfig",
            "Smallfig", IntegratedHeadPolicy, "shipped-standard", [], []),
        new("smallfig-08", "Smallfig 08", "/Game/Characters/LEGOfig/SK_LEGOFig_Smallfig_08",
            "Smallfig08", IntegratedHeadPolicy, "shipped-standard", [],
            ["Use materials authored for the Smallfig 08 body layout."]),
        new("smallfig-armless", "Smallfig — armless", "/Game/Characters/LEGOfig/SK_LEGOfig_Smallfig_Armless",
            "Smallfig", IntegratedHeadPolicy, "native-character", ["Left arm", "Right arm", "Left hand", "Right hand"],
            ["Smallfig hand equipment can float. This is the only shipped reduced Smallfig body profile."]),
    ];

    public static IReadOnlyList<NativeBodyProfile> Catalog() =>
        Definitions.Select(definition => Create(definition, "")).ToList();

    public static NativeBodyProfile? Find(string? id) =>
        Definitions.FirstOrDefault(definition => definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } match
            ? Create(match, "")
            : null;

    public static NativeBodyProfile? MatchMesh(string? meshPath, string? sourceVisualPackage = null)
    {
        var package = UnrealPathUtil.NormalizePackagePath(meshPath);
        var match = Definitions.FirstOrDefault(definition =>
            UnrealPathUtil.NormalizePackagePath(definition.MeshPackagePath)
                .Equals(package, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : Create(match, sourceVisualPackage ?? "");
    }

    internal static NativeBodyProfile? SelectAfterBaseChange(
        NativeBodyProfile? previous,
        NativeBodyProfile? resolvedBaseBody,
        bool baseIdentityChanged) =>
        baseIdentityChanged ? resolvedBaseBody : previous ?? resolvedBaseBody;

    public static NativeBodyProfile? TryResolveFromBlueprint(
        string? uassetPath,
        string? sourceVisualPackage,
        Usmap? mappings)
    {
        if (string.IsNullOrWhiteSpace(uassetPath) || !File.Exists(uassetPath))
        {
            return null;
        }

        try
        {
            var asset = new UAsset(
                uassetPath,
                EngineVersion.VER_UE5_6,
                mappings,
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            return MatchMesh(TryReadBodyMeshPackage(asset), sourceVisualPackage);
        }
        catch
        {
            return null;
        }
    }

    public Result ApplyToContentRoot(string contentRoot, NativeSuitProject project, Usmap? mappings)
    {
        var result = new Result();
        var profile = project.BodyProfile;
        if (profile is null)
        {
            return result;
        }

        var canonical = Find(profile.Id) ?? MatchMesh(profile.MeshPackagePath);
        if (canonical is null)
        {
            throw new InvalidOperationException(
                $"Unknown native body profile '{profile.Id}' ({profile.MeshPackagePath}). Re-select the body profile before rebuilding.");
        }

        // Persist canonical mesh facts while retaining provenance from the selected visual source.
        canonical.SourceVisualPackage = profile.SourceVisualPackage;
        project.BodyProfile = canonical;

        foreach (var (role, packagePath) in new[]
                 {
                     ("playable", project.TargetPackages.Playable),
                     ("cutscene", project.TargetPackages.Cutscene),
                 })
        {
            var basePath = PackagePathToBasePath(contentRoot, packagePath);
            var uassetPath = basePath + ".uasset";
            if (!File.Exists(uassetPath))
            {
                result.Files.Add(new FileResult(role, uassetPath, false, "target package is missing"));
                continue;
            }

            try
            {
                var asset = new UAsset(
                    uassetPath,
                    EngineVersion.VER_UE5_6,
                    mappings,
                    CustomSerializationFlags.SkipPreloadDependencyLoading);
                var gameplayBefore = CaptureProtectedGameplayContract(asset, packagePath);
                if (role.Equals("cutscene", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureMinimalSchema(
                        asset,
                        "BP_CutsceneMinifigCharacter_C",
                        "/Game/Characters/BP_Master/BP_CutsceneMinifigCharacter");
                }

                var component = MaterialReplaceService.FindComponentExport(asset, "CharacterMesh0")
                    ?? throw new InvalidDataException(
                        "CharacterMesh0 (or its native Mesh alias) was not found in the generated Blueprint.");
                var meshImport = EnsureObjectImport(
                    asset,
                    canonical.MeshPackagePath,
                    UnrealPathUtil.AssetName(canonical.MeshPackagePath),
                    "/Script/Engine",
                    "SkeletalMesh");
                SetObjectProperty(component, asset, "SkeletalMesh", meshImport);
                SetObjectProperty(component, asset, "SkinnedAsset", meshImport);
                asset.Write(uassetPath);

                var written = new UAsset(
                    uassetPath,
                    EngineVersion.VER_UE5_6,
                    mappings,
                    CustomSerializationFlags.SkipPreloadDependencyLoading);
                var actual = TryReadBodyMeshPackage(written);
                var gameplayAfter = CaptureProtectedGameplayContract(written, packagePath);
                var bodyMatches = UnrealPathUtil.NormalizePackagePath(actual)
                    .Equals(canonical.MeshPackagePath, StringComparison.OrdinalIgnoreCase);
                var gameplayPreserved = gameplayBefore == gameplayAfter;
                var success = bodyMatches && gameplayPreserved;
                result.Files.Add(new FileResult(
                    role,
                    uassetPath,
                    success,
                    success
                        ? canonical.MeshPackagePath
                        : !bodyMatches
                            ? $"wrote '{canonical.MeshPackagePath}', read back '{actual}'"
                            : "the body mesh changed correctly, but the donor parent or protected gameplay SCS nodes changed"));
            }
            catch (Exception ex)
            {
                result.Files.Add(new FileResult(
                    role,
                    uassetPath,
                    false,
                    ex.Message,
                    FileLockUtil.IsTransient(ex)));
            }
        }

        return result;
    }

    private static ProtectedGameplayContract CaptureProtectedGameplayContract(
        UAsset asset,
        string generatedPackage)
    {
        var parentClass = "";
        var parentPackage = "";
        var generatedClassName = UnrealPathUtil.AssetName(generatedPackage) + "_C";
        var generatedClass = asset.Exports.FirstOrDefault(export =>
            export.ObjectName.ToString().Equals(generatedClassName, StringComparison.OrdinalIgnoreCase));
        if (generatedClass?.SuperIndex.IsImport() == true)
        {
            var parent = generatedClass.SuperIndex.ToImport(asset);
            parentClass = parent.ObjectName.ToString();
            if (parent.OuterIndex.IsImport())
            {
                parentPackage = UnrealPathUtil.NormalizePackagePath(
                    parent.OuterIndex.ToImport(asset).ObjectName.ToString());
            }
        }

        var requiredNodes = StageValidationService.LiveScsComponentNames(asset)
            .Select(GameplayShellComponentPolicy.ComponentName)
            .Where(GameplayShellComponentPolicy.IsRequired)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(component => component, StringComparer.OrdinalIgnoreCase);
        return new ProtectedGameplayContract(
            parentClass,
            parentPackage,
            string.Join("\n", requiredNodes));
    }

    internal static bool ProtectedGameplayContractMatchesForTest(
        string beforeParentClass,
        string beforeParentPackage,
        IEnumerable<string> beforeComponents,
        string afterParentClass,
        string afterParentPackage,
        IEnumerable<string> afterComponents)
    {
        static string ComponentSignature(IEnumerable<string> components) => string.Join(
            "\n",
            components
                .Select(GameplayShellComponentPolicy.ComponentName)
                .Where(GameplayShellComponentPolicy.IsRequired)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(component => component, StringComparer.OrdinalIgnoreCase));

        return beforeParentClass.Equals(afterParentClass, StringComparison.OrdinalIgnoreCase) &&
               UnrealPathUtil.NormalizePackagePath(beforeParentPackage).Equals(
                   UnrealPathUtil.NormalizePackagePath(afterParentPackage),
                   StringComparison.OrdinalIgnoreCase) &&
               ComponentSignature(beforeComponents).Equals(
                   ComponentSignature(afterComponents),
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static string? TryReadBodyMeshPackage(UAsset asset)
    {
        var component = MaterialReplaceService.FindComponentExport(asset, "CharacterMesh0");
        if (component is null)
        {
            return null;
        }

        foreach (var propertyName in new[] { "SkinnedAsset", "SkeletalMesh" })
        {
            var property = component.Data.OfType<ObjectPropertyData>().FirstOrDefault(candidate =>
                candidate.Name.ToString().Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            var package = ResolveImportPackage(asset, property?.Value ?? FPackageIndex.FromRawIndex(0));
            if (!string.IsNullOrWhiteSpace(package))
            {
                return UnrealPathUtil.NormalizePackagePath(package);
            }
        }

        return null;
    }

    private static NativeBodyProfile Create(Definition definition, string sourceVisualPackage) => new()
    {
        Id = definition.Id,
        DisplayName = definition.DisplayName,
        MeshPackagePath = definition.MeshPackagePath,
        MeshObjectPath = $"{definition.MeshPackagePath}.{UnrealPathUtil.AssetName(definition.MeshPackagePath)}",
        SkeletonPackagePath = SharedSkeleton,
        GeometryFamily = definition.GeometryFamily,
        MissingRegions = definition.MissingRegions.ToList(),
        HeadPolicy = definition.HeadPolicy,
        EvidenceTier = definition.EvidenceTier,
        SourceVisualPackage = UnrealPathUtil.NormalizePackagePath(sourceVisualPackage),
        Warnings = definition.Warnings.ToList(),
    };

    private static string? ResolveImportPackage(UAsset asset, FPackageIndex index)
    {
        if (!index.IsImport())
        {
            return null;
        }

        var currentIndex = -index.Index - 1;
        for (var depth = 0; depth <= asset.Imports.Count; depth++)
        {
            if (currentIndex < 0 || currentIndex >= asset.Imports.Count)
            {
                return null;
            }
            var current = asset.Imports[currentIndex];
            var name = current.ObjectName.ToString();
            if (ExtractedPackagePathService.IsContentPackagePath(name))
            {
                return name;
            }
            if (!current.OuterIndex.IsImport())
            {
                return null;
            }
            currentIndex = -current.OuterIndex.Index - 1;
        }
        return null;
    }

    private static void SetObjectProperty(
        NormalExport component,
        UAsset asset,
        string propertyName,
        FPackageIndex value)
    {
        var property = component.Data.OfType<ObjectPropertyData>().FirstOrDefault(candidate =>
            candidate.Name.ToString().Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        if (property is not null)
        {
            property.Value = value;
            return;
        }
        component.Data.Add(new ObjectPropertyData(MakeName(asset, propertyName)) { Value = value });
    }

    private static FPackageIndex EnsureObjectImport(
        UAsset asset,
        string packagePath,
        string objectName,
        string classPackage,
        string className)
    {
        var packageImport = EnsurePackageImport(asset, packagePath);
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString().Equals(objectName, StringComparison.Ordinal) &&
                import.OuterIndex.Index == packageImport.Index &&
                import.ClassPackage.ToString().Equals(classPackage, StringComparison.Ordinal) &&
                import.ClassName.ToString().Equals(className, StringComparison.Ordinal))
            {
                return FPackageIndex.FromImport(i);
            }
        }

        AddNames(asset, objectName, classPackage, className);
        return asset.AddImport(new Import(classPackage, className, packageImport, objectName, false, asset));
    }

    private static FPackageIndex EnsurePackageImport(UAsset asset, string packagePath)
    {
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString().Equals(packagePath, StringComparison.Ordinal) &&
                import.OuterIndex.IsNull() &&
                import.ClassName.ToString().Equals("Package", StringComparison.Ordinal))
            {
                return FPackageIndex.FromImport(i);
            }
        }

        AddNames(asset, packagePath, "/Script/CoreUObject", "Package");
        return asset.AddImport(new Import(
            "/Script/CoreUObject",
            "Package",
            FPackageIndex.FromRawIndex(0),
            packagePath,
            false,
            asset));
    }

    private static FName MakeName(UAsset asset, string value)
    {
        AddNames(asset, value);
        return new FName(asset, value);
    }

    private static void AddNames(UAsset asset, params string[] names)
    {
        foreach (var name in names)
        {
            if (!asset.ContainsNameReference(new FString(name)))
            {
                asset.AddNameReference(new FString(name), false, false);
            }
        }
    }

    private static void EnsureMinimalSchema(UAsset asset, string schemaName, string modulePath)
    {
        var mappings = asset.Mappings;
        if (mappings is null || mappings.Schemas.ContainsKey(schemaName))
        {
            return;
        }

        mappings.Schemas[schemaName] = new UsmapSchema(
            name: schemaName,
            superType: "",
            propCount: 0,
            props: new ConcurrentDictionary<int, UsmapProperty>(),
            isCaseInsensitive: mappings.AreFNamesCaseInsensitive,
            superTypeModulePath: "",
            fromAsset: true)
        {
            ModulePath = modulePath,
        };
    }

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Generated body target must be a /Game package: '{packagePath}'.");
        }
        return Path.Combine(
            contentRoot,
            package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}
