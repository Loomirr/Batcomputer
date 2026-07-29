using System.Text.Json;

namespace Batcomputer;

internal static class TextureCookTemplateService
{
    private sealed record Definition(
        string Folder,
        string JsonFile,
        string ContentRelativePath,
        string PackagePath,
        int Width,
        int Height,
        string PixelFormat,
        int BytesPerPixel,
        int MipCount);

    private sealed record TemplateDocument(
        string Name,
        string Package,
        int SizeX,
        int SizeY,
        string PixelFormat,
        IReadOnlyList<TemplateMip> Mips);

    private sealed record TemplateMip(int SizeX, int SizeY, TemplateBulkData BulkData);

    private sealed record TemplateBulkData(
        int ElementCount,
        int SizeOnDisk,
        int OffsetInFile,
        string BulkDataFlags);

    internal sealed class Result
    {
        public int Prepared { get; set; }
        public List<string> Logs { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private static readonly Definition[] Definitions =
    {
        new(
            "TextureStandaloneTemplate_DroneControlBGRA8",
            "T_GA_DroneControl_BatGirl_AO.json",
            "Models/Gadgets/GA_DroneControl_BatGirl/T_GA_DroneControl_BatGirl_AO",
            "/Game/Models/Gadgets/GA_DroneControl_BatGirl/T_GA_DroneControl_BatGirl_AO",
            2048, 2048, "PF_B8G8R8A8", 4, 5),
        new(
            "TextureStandaloneTemplate_BatarangBC5",
            "T_Batarang_N.json",
            "Models/Gadgets/GA_Batarang/T_Batarang_N",
            "/Game/Models/Gadgets/GA_Batarang/T_Batarang_N",
            2048, 2048, "PF_BC5", 1, 5),
        new(
            "TextureStandaloneTemplate_BatclawLogo_DXT5",
            "T_DECAL_BatclawLogo.json",
            "Models/Gadgets/GA_Batclaw/T_DECAL_BatclawLogo",
            "/Game/Models/Gadgets/GA_Batclaw/T_DECAL_BatclawLogo",
            2048, 2048, "PF_DXT5", 1, 5),
    };

    public static IReadOnlyList<string> RetocFilters { get; } = Definitions
        .Select(definition => "Content/" + definition.ContentRelativePath)
        .ToArray();

    public static bool HasCoreTemplates(string projectRoot) => Definitions
        .Take(2)
        .All(definition => IsTemplateReady(TemplateJsonPath(projectRoot, definition.Folder)));

    public static string TemplateJsonPath(string projectRoot, string folder) =>
        Path.Combine(
            AppSettings.GeneratedRootFor(projectRoot),
            folder,
            Definitions.First(definition => definition.Folder.Equals(folder, StringComparison.Ordinal)).JsonFile);

    public static bool IsTemplateReady(string templateJsonPath)
    {
        if (!File.Exists(templateJsonPath))
        {
            return false;
        }

        var assetBase = Path.Combine(
            Path.GetDirectoryName(templateJsonPath)!,
            Path.GetFileNameWithoutExtension(templateJsonPath));
        return File.Exists(assetBase + ".uasset") &&
            File.Exists(assetBase + ".uexp") &&
            File.Exists(assetBase + ".ubulk");
    }

    public static Result PrepareFromContentRoot(string projectRoot, string contentRoot)
    {
        var result = new Result();
        foreach (var definition in Definitions)
        {
            var sourceBase = Path.Combine(
                contentRoot,
                definition.ContentRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var required = new[] { ".uasset", ".uexp", ".ubulk" };
            var missing = required
                .Select(extension => sourceBase + extension)
                .Where(path => !File.Exists(path))
                .ToList();
            if (missing.Count > 0)
            {
                result.Warnings.Add($"Texture template donor is missing: {definition.ContentRelativePath} ({string.Join(", ", missing.Select(Path.GetFileName))})");
                continue;
            }

            var destination = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), definition.Folder);
            Directory.CreateDirectory(destination);
            foreach (var extension in required)
            {
                File.Copy(sourceBase + extension, Path.Combine(destination, Path.GetFileName(sourceBase) + extension), overwrite: true);
            }

            var json = new TemplateDocument(
                Path.GetFileName(definition.ContentRelativePath),
                definition.PackagePath,
                definition.Width,
                definition.Height,
                definition.PixelFormat,
                BuildMips(definition));
            File.WriteAllText(
                Path.Combine(destination, definition.JsonFile),
                JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            result.Prepared++;
            result.Logs.Add($"Texture template ready: {definition.PixelFormat} {definition.Width}x{definition.Height}");
        }

        return result;
    }

    private static IReadOnlyList<TemplateMip> BuildMips(Definition definition)
    {
        var mips = new List<TemplateMip>(definition.MipCount);
        var width = definition.Width;
        var height = definition.Height;
        var offset = 0;
        for (var i = 0; i < definition.MipCount; i++)
        {
            var size = checked(width * height * definition.BytesPerPixel);
            mips.Add(new TemplateMip(
                width,
                height,
                new TemplateBulkData(size, size, offset, "PayloadInSeperateFile")));
            offset = checked(offset + size);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return mips;
    }
}
