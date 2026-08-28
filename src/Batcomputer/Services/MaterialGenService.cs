using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using PropertyData = UAssetAPI.PropertyTypes.Objects.PropertyData;

namespace Batcomputer;

/// <summary>
/// Clones an existing game Material Instance (MI) and retargets its texture
/// parameters to a modder's own texture object paths, writing the result into the
/// export Content root so it packages with the suit trio. It does NOT stage/copy
/// textures - modders ship their own texture paks; this only writes the MI that
/// references those texture paths.
/// </summary>
public sealed class MaterialGenService
{
    public string ProjectRoot { get; }

    public MaterialGenService(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    // ---- read (for the UI to show the base MI's texture params) --------------

    public sealed class TextureParam
    {
        public string Name { get; set; } = "";
        public string CurrentTexturePath { get; set; } = "";
        public string ObjectPath { get; set; } = "";
    }

    /// <summary>An authored Material Instance vector parameter, stored by UE as FLinearColor.</summary>
    public sealed class ColorParam
    {
        public string Name { get; set; } = "";
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
        public float A { get; set; } = 1f;
    }

    /// <summary>An authored Material Instance scalar parameter.</summary>
    public sealed class ScalarParam
    {
        public string Name { get; set; } = "";
        public float Value { get; set; }
    }

    public sealed class MaterialTemplateInfo
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string SourcePackagePath { get; set; } = "";
        public string SourceStem { get; set; } = "";
        public string ParentMaterialPath { get; set; } = "";
        public List<TextureParam> TextureParams { get; set; } = new();
        public List<ColorParam> ColorParams { get; set; } = new();
        public List<ScalarParam> ScalarParams { get; set; } = new();
    }

    public MaterialTemplateInfo ReadTemplate(string uassetPath)
    {
        var info = new MaterialTemplateInfo();
        try
        {
            if (!File.Exists(uassetPath))
            {
                info.Status = "missing";
                info.Error = $"Asset not found: {uassetPath}";
                return info;
            }

            var mappings = LoadMappings();
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);
            info.SourcePackagePath = asset.FolderName?.ToString() ?? "";
            info.SourceStem = Path.GetFileNameWithoutExtension(uassetPath);

            var (textureExport, textureArray) = FindParameterArray(asset, "TextureParameterValues");
            var (colorExport, colorArray) = FindParameterArray(asset, "VectorParameterValues");
            var (scalarExport, scalarArray) = FindParameterArray(asset, "ScalarParameterValues");
            if ((textureExport is null || textureArray is null) &&
                (colorExport is null || colorArray is null) &&
                (scalarExport is null || scalarArray is null))
            {
                info.Status = "no-material-params";
                info.Error = "No texture, vector, or scalar parameter array was found (is this a Material Instance?).";
                return info;
            }

            var materialExport = textureExport ?? colorExport ?? scalarExport;
            var parent = materialExport is null
                ? null
                : FindProperty<ObjectPropertyData>(materialExport.Data, "Parent");
            if (parent is not null)
            {
                info.ParentMaterialPath = DescribeObjectImportPath(asset, parent.Value);
            }

            foreach (var entry in textureArray?.Value?.OfType<StructPropertyData>() ?? Enumerable.Empty<StructPropertyData>())
            {
                var name = ReadParamName(entry);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var current = "";
                var valueProp = FindProperty<ObjectPropertyData>(entry.Value, "ParameterValue");
                if (valueProp is not null)
                {
                    current = DescribeObjectImport(asset, valueProp.Value);
                }

                info.TextureParams.Add(new TextureParam
                {
                    Name = name,
                    CurrentTexturePath = current,
                    ObjectPath = valueProp is null ? "" : DescribeObjectImportPath(asset, valueProp.Value),
                });
            }

            foreach (var entry in colorArray?.Value?.OfType<StructPropertyData>() ?? Enumerable.Empty<StructPropertyData>())
            {
                var name = ReadParamName(entry);
                var value = FindLinearColorValue(entry);
                if (string.IsNullOrWhiteSpace(name) || value is null)
                {
                    continue;
                }

                info.ColorParams.Add(new ColorParam
                {
                    Name = name,
                    R = value.Value.R,
                    G = value.Value.G,
                    B = value.Value.B,
                    A = value.Value.A,
                });
            }

            foreach (var entry in scalarArray?.Value?.OfType<StructPropertyData>() ?? Enumerable.Empty<StructPropertyData>())
            {
                var name = ReadParamName(entry);
                var value = FindScalarValue(entry);
                if (string.IsNullOrWhiteSpace(name) || value is null)
                {
                    continue;
                }

                info.ScalarParams.Add(new ScalarParam
                {
                    Name = name,
                    Value = value.Value,
                });
            }

            info.Status = "ok";
            return info;
        }
        catch (Exception ex)
        {
            info.Status = "error";
            info.Error = ex.ToString();
            return info;
        }
    }

    // ---- generate -----------------------------------------------------------

    public sealed class GenRequest
    {
        public string BaseUassetPath { get; set; } = "";
        public string OutputPackagePath { get; set; } = ""; // e.g. /Game/Mods/MyMod/Materials/MI_MySuit_Body
        // Parameter name -> texture object path (e.g. /Game/Mods/MyMod/Textures/T_MySuit_BC)
        public Dictionary<string, string> ParamToTexture { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // Parameters in this set retain their authored override entry but receive a null UObject
        // reference. This is deliberately separate from an absent map value, which means inherit
        // the texture already stored by the cloned base material instance.
        public HashSet<string> TextureParamsToClear { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // Parameter name -> linear RGBA colour. The base MI's parent graph and switches are retained.
        public Dictionary<string, ColorParam> ParamToColor { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // Parameter name -> scalar value. Face helper controls use the base material's native
        // visibility scalars rather than trying to remove every face texture independently.
        public Dictionary<string, float> ParamToScalar { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class GenResult
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string OutputPackagePath { get; set; } = "";
        public string OutputUasset { get; set; } = "";
        public List<string> Retargeted { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public GenResult Generate(GenRequest request)
    {
        var outputPackagePath = UnrealPathUtil.NormalizePackagePath(request.OutputPackagePath);
        var result = new GenResult { OutputPackagePath = outputPackagePath };
        try
        {
            if (!File.Exists(request.BaseUassetPath))
            {
                result.Status = "missing-base";
                result.Error = $"Base MI not found: {request.BaseUassetPath}";
                return result;
            }

            if (!outputPackagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "bad-output";
                result.Error = $"Output package path must start with /Game/. Got: {request.OutputPackagePath}";
                return result;
            }

            var exportContentRoot = AppSettings.Current.EffectiveExportContentRoot();
            var outputBase = PackagePathToBasePath(exportContentRoot, outputPackagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);

            var baseNoExt = Path.Combine(
                Path.GetDirectoryName(request.BaseUassetPath)!,
                Path.GetFileNameWithoutExtension(request.BaseUassetPath));

            CopyIfExists(baseNoExt + ".uasset", outputBase + ".uasset");
            CopyIfExists(baseNoExt + ".uexp", outputBase + ".uexp");
            CopyIfExists(baseNoExt + ".ubulk", outputBase + ".ubulk");

            var mappings = LoadMappings();
            var asset = new UAsset(outputBase + ".uasset", EngineVersion.VER_UE5_6, mappings, CustomSerializationFlags.SkipPreloadDependencyLoading);

            // Rename the package so it is a distinct asset at the new path and does
            // not collide with / shadow the original game MI.
            var sourcePackagePath = UnrealPathUtil.NormalizePackagePath(asset.FolderName?.ToString());
            var sourceStem = UnrealPathUtil.AssetName(sourcePackagePath);
            var targetStem = UnrealPathUtil.AssetName(outputPackagePath);
            asset.FolderName = new FString(outputPackagePath);

            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(sourcePackagePath))
            {
                replacements[sourcePackagePath] = outputPackagePath;
            }
            if (!string.IsNullOrWhiteSpace(sourceStem) && !string.Equals(sourceStem, targetStem, StringComparison.Ordinal))
            {
                replacements[sourceStem] = targetStem;
            }
            var ordered = replacements
                .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                .OrderByDescending(p => p.Key.Length)
                .ToList();

            var nameMap = asset.GetNameMapIndexList();
            for (var i = 0; i < nameMap.Count; i++)
            {
                var original = nameMap[i].ToString();
                var patched = original;
                foreach (var pair in ordered)
                {
                    patched = patched.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                }
                if (patched != original)
                {
                    asset.SetNameReference(i, new FString(patched));
                }
            }

            var (_, textureArray) = FindParameterArray(asset, "TextureParameterValues");
            var (_, colorArray) = FindParameterArray(asset, "VectorParameterValues");
            var (_, scalarArray) = FindParameterArray(asset, "ScalarParameterValues");
            if (textureArray is null && colorArray is null && scalarArray is null)
            {
                result.Status = "no-material-params";
                result.Error = "No texture, vector, or scalar parameter array exists in the base MI.";
                return result;
            }

            var textureNameReplacements = new Dictionary<string, string>(StringComparer.Ordinal);
            if ((request.ParamToTexture.Count > 0 || request.TextureParamsToClear.Count > 0) && textureArray is null)
            {
                result.Warnings.Add("The base MI has no texture parameters to override.");
            }
            foreach (var entry in textureArray?.Value?.OfType<StructPropertyData>() ?? Enumerable.Empty<StructPropertyData>())
            {
                var name = ReadParamName(entry);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var clearTexture = request.TextureParamsToClear.Contains(name);
                var hasTextureOverride = request.ParamToTexture.TryGetValue(name, out var texturePath) &&
                                         !string.IsNullOrWhiteSpace(texturePath);
                if (!clearTexture && !hasTextureOverride)
                {
                    continue;
                }

                var valueProp = FindProperty<ObjectPropertyData>(entry.Value, "ParameterValue");
                if (valueProp is null)
                {
                    result.Warnings.Add($"Param '{name}': no ParameterValue property to retarget.");
                    continue;
                }

                // TextureStreamingData keys are texture object names, not
                // material parameter names. Keep the old imported object name
                // so the streaming metadata can be retargeted after the MI
                // parameter itself is changed.
                var oldTextureName = DescribeObjectImport(asset, valueProp.Value);
                if (clearTexture)
                {
                    valueProp.Value = FPackageIndex.FromRawIndex(0);
                    result.Retargeted.Add($"{name} -> None (null Texture2D)");
                    continue;
                }

                var (texPackage, texObject) = SplitObjectPath(texturePath!);
                var import = EnsureObjectImport(asset, texPackage, texObject, "/Script/Engine", "Texture2D");
                if (import.IsNull())
                {
                    result.Warnings.Add($"Param '{name}': failed to import texture '{texturePath}'.");
                    continue;
                }

                valueProp.Value = import;
                result.Retargeted.Add($"{name} -> {UnrealPathUtil.ObjectPath(texturePath)}");
                if (!string.IsNullOrWhiteSpace(oldTextureName))
                {
                    textureNameReplacements[oldTextureName] = texObject;
                }
            }

            if (request.ParamToColor.Count > 0 && colorArray is null)
            {
                result.Warnings.Add("The base MI has no colour parameters to override.");
            }
            foreach (var entry in colorArray?.Value?.OfType<StructPropertyData>() ?? Enumerable.Empty<StructPropertyData>())
            {
                var name = ReadParamName(entry);
                if (string.IsNullOrWhiteSpace(name) || !request.ParamToColor.TryGetValue(name, out var colour))
                {
                    continue;
                }

                var value = FindLinearColorValue(entry);
                if (value is null)
                {
                    result.Warnings.Add($"Param '{name}': no FLinearColor ParameterValue to retarget.");
                    continue;
                }

                value.Value = new FLinearColor(colour.R, colour.G, colour.B, colour.A);
                result.Retargeted.Add($"{name} -> linear ({colour.R:0.#####}, {colour.G:0.#####}, {colour.B:0.#####}, {colour.A:0.#####})");
            }

            if (request.ParamToScalar.Count > 0 && scalarArray is null)
            {
                result.Warnings.Add("The base MI has no scalar parameters to override.");
            }
            foreach (var entry in scalarArray?.Value?.OfType<StructPropertyData>() ?? Enumerable.Empty<StructPropertyData>())
            {
                var name = ReadParamName(entry);
                if (string.IsNullOrWhiteSpace(name) || !request.ParamToScalar.TryGetValue(name, out var scalar))
                {
                    continue;
                }

                var value = FindScalarValue(entry);
                if (value is null)
                {
                    result.Warnings.Add($"Param '{name}': no float ParameterValue to retarget.");
                    continue;
                }

                value.Value = scalar;
                result.Retargeted.Add($"{name} -> {scalar.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            UpdateTextureStreamingData(asset, textureNameReplacements, result);

            UnrealPathUtil.RepairSplitPathNameMapEntries(
                asset,
                request.ParamToTexture.Values.Append(outputPackagePath),
                result.Retargeted);

            asset.Write(outputBase + ".uasset");
            result.OutputUasset = outputBase + ".uasset";
            result.Status = "created";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    // ---- MI navigation ------------------------------------------------------

    private static (NormalExport?, ArrayPropertyData?) FindParameterArray(UAsset asset, string propertyName)
    {
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            var array = FindProperty<ArrayPropertyData>(export.Data, propertyName);
            if (array is not null)
            {
                return (export, array);
            }
        }
        return (null, null);
    }

    private static LinearColorPropertyData? FindLinearColorValue(StructPropertyData entry)
    {
        var parameterValue = FindProperty<StructPropertyData>(entry.Value, "ParameterValue");
        return parameterValue?.Value.OfType<LinearColorPropertyData>().FirstOrDefault();
    }

    private static FloatPropertyData? FindScalarValue(StructPropertyData entry) =>
        FindProperty<FloatPropertyData>(entry.Value, "ParameterValue");

    private static string ReadParamName(StructPropertyData entry)
    {
        // FMaterialParameterInfo.Name lives inside the ParameterInfo struct.
        var info = FindProperty<StructPropertyData>(entry.Value, "ParameterInfo");
        if (info is not null)
        {
            var nameProp = FindProperty<NamePropertyData>(info.Value, "Name");
            if (nameProp is not null)
            {
                return nameProp.Value.ToString();
            }
        }

        // Fallback: some layouts expose Name/ParameterName directly on the entry.
        var direct = FindProperty<NamePropertyData>(entry.Value, "Name")
                     ?? FindProperty<NamePropertyData>(entry.Value, "ParameterName");
        return direct?.Value.ToString() ?? "";
    }

    private static string DescribeObjectImport(UAsset asset, FPackageIndex index)
    {
        if (index is null || index.IsNull() || !index.IsImport())
        {
            return "";
        }

        var importIndex = -index.Index - 1;
        if (importIndex < 0 || importIndex >= asset.Imports.Count)
        {
            return "";
        }

        return asset.Imports[importIndex].ObjectName.ToString();
    }

    private static string DescribeObjectImportPath(UAsset asset, FPackageIndex index)
    {
        if (index is null || index.IsNull() || !index.IsImport())
        {
            return "";
        }

        var importIndex = -index.Index - 1;
        if (importIndex < 0 || importIndex >= asset.Imports.Count)
        {
            return "";
        }

        var import = asset.Imports[importIndex];
        var objectName = import.ObjectName.ToString();
        if (import.OuterIndex.IsImport())
        {
            var outerIndex = -import.OuterIndex.Index - 1;
            if (outerIndex >= 0 && outerIndex < asset.Imports.Count)
            {
                var packageName = asset.Imports[outerIndex].ObjectName.ToString();
                if (ExtractedPackagePathService.IsContentPackagePath(packageName))
                {
                    return packageName + "." + objectName;
                }
            }
        }

        return ExtractedPackagePathService.IsContentPackagePath(objectName) ? objectName : "";
    }

    private static void UpdateTextureStreamingData(
        UAsset asset,
        IReadOnlyDictionary<string, string> textureNameReplacements,
        GenResult result)
    {
        if (textureNameReplacements.Count == 0)
        {
            return;
        }

        var updated = 0;
        foreach (var export in asset.Exports.OfType<NormalExport>())
        {
            var array = FindProperty<ArrayPropertyData>(export.Data, "TextureStreamingData");
            if (array is null)
            {
                continue;
            }

            foreach (var entry in array.Value?.OfType<StructPropertyData>() ?? Enumerable.Empty<StructPropertyData>())
            {
                var textureName = FindProperty<NamePropertyData>(entry.Value, "TextureName");
                if (textureName is null)
                {
                    continue;
                }

                var oldName = textureName.Value.ToString();
                if (!textureNameReplacements.TryGetValue(oldName, out var newName) ||
                    string.IsNullOrWhiteSpace(newName) ||
                    oldName.Equals(newName, StringComparison.Ordinal))
                {
                    continue;
                }

                textureName.Value = MakeName(asset, newName);
                updated++;
                result.Retargeted.Add($"TextureStreamingData {oldName} -> {newName}");
            }
        }

        if (updated == 0)
        {
            result.Warnings.Add("No matching TextureStreamingData entries were found for the retargeted texture parameters.");
        }
    }

    // ---- shared UAssetAPI helpers (replicated from the graft service) --------

    private static T? FindProperty<T>(List<PropertyData> properties, string name) where T : PropertyData
    {
        return properties
            .OfType<T>()
            .FirstOrDefault(p => p.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static FPackageIndex EnsureObjectImport(UAsset asset, string packagePath, string objectName, string classPackage, string className)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(objectName))
        {
            return FPackageIndex.FromRawIndex(0);
        }

        var packageImport = EnsurePackageImport(asset, packagePath);
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            if (import.ObjectName.ToString().Equals(objectName, StringComparison.Ordinal) &&
                import.OuterIndex.Index == packageImport.Index &&
                import.ClassPackage.ToString().Equals(classPackage, StringComparison.Ordinal) &&
                import.ClassName.ToString().Equals(className, StringComparison.Ordinal))
            {
                return FromImportNumber(i + 1);
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
                return FromImportNumber(i + 1);
            }
        }

        AddNames(asset, packagePath, "/Script/CoreUObject", "Package");
        return asset.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), packagePath, false, asset));
    }

    private static void AddNames(UAsset asset, params string?[] names)
    {
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && !asset.ContainsNameReference(new FString(name)))
            {
                asset.AddNameReference(new FString(name), false, false);
            }
        }
    }

    private static FName MakeName(UAsset asset, string value)
    {
        if (!asset.ContainsNameReference(new FString(value)))
        {
            asset.AddNameReference(new FString(value), false, false);
        }

        return new FName(asset, value, 0);
    }

    private static FPackageIndex FromImportNumber(int importNumber)
    {
        return importNumber <= 0 ? FPackageIndex.FromRawIndex(0) : FPackageIndex.FromImport(importNumber - 1);
    }

    // ---- path helpers -------------------------------------------------------

    private Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? MappingsCache.Load(configured) : null;
    }

    private static (string package, string obj) SplitObjectPath(string path)
    {
        var packagePath = UnrealPathUtil.NormalizePackagePath(path);
        return (packagePath, UnrealPathUtil.AssetName(packagePath));
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string PackagePathToBasePath(string contentRoot, string packagePath)
    {
        packagePath = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only /Game package paths are supported. Got: {packagePath}");
        }
        return Path.Combine(contentRoot, packagePath["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }
        // Editing an MI in place uses the same file as base and output - copying
        // a file onto itself throws "process cannot access the file". The bytes
        // are already where we need them, so skip the copy and edit in place.
        if (string.Equals(
                Path.GetFullPath(source),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}
