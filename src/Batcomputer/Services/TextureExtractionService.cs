using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace Batcomputer;

/// <summary>Exports a referenced cooked game or workspace texture without changing its material.</summary>
internal static class TextureExtractionService
{
    private const string ZeroAes = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private const string GameContentPrefix = "LEGOBatmanLotDK/Content/";

    internal sealed record Result(
        bool Success,
        string Detail,
        string ObjectPath = "",
        int Width = 0,
        int Height = 0,
        string PixelFormat = "");

    public static Result ExportPng(
        string texturePath,
        string destinationPath,
        IEnumerable<string>? looseContentRoots = null)
    {
        var packagePath = UnrealPathUtil.NormalizePackagePath(texturePath);
        var objectPath = UnrealPathUtil.ObjectPath(packagePath);
        if (string.IsNullOrWhiteSpace(objectPath))
        {
            return new Result(false, "The selected texture does not have a usable Unreal object path.");
        }

        var settings = AppSettings.Current;
        var paksRoot = settings.EffectiveGamePaksRoot();
        var usmapPath = settings.EffectiveUsmapPath();
        if (!Directory.Exists(paksRoot))
        {
            return new Result(false, "The configured game Paks folder was not found. Check Settings and try again.", objectPath);
        }
        if (string.IsNullOrWhiteSpace(usmapPath) || !File.Exists(usmapPath))
        {
            return new Result(false, "The configured UE 5.6 mappings file was not found. Check Settings and try again.", objectPath);
        }

        var tempPath = destinationPath + ".extracting-" + Guid.NewGuid().ToString("N") + ".png";
        try
        {
            var paks = new DirectoryInfo(paksRoot);
            var dlcRoot = new DirectoryInfo(GameAssetRefreshService.DlcRootForPaksRoot(paks.FullName));
            var provider = new DefaultFileProvider(
                paks,
                dlcRoot.Exists ? [dlcRoot] : [],
                BaseGamePakSource.ShippedContainerSearchOption,
                versions: new VersionContainer(EGame.GAME_UE5_6),
                pathComparer: StringComparer.OrdinalIgnoreCase);
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey(ZeroAes));
            // Base-game and DLC references must resolve from their mounted containers. A loose
            // extraction can deserialize as a Texture2D while still lacking reconstructed cooked
            // platform data (PF_Unknown), and must never shadow the healthy container copy.
            // Batcomputer-owned textures live below /Game/Mods and genuinely need their cooked
            // workspace overlay because they do not exist in the shipped containers.
            if (packagePath.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                AddLooseContent(provider, looseContentRoots);
            }

            UTexture2D? texture = null;
            Exception? loadError = null;
            foreach (var candidate in LoadCandidates(packagePath, objectPath))
            {
                try
                {
                    texture = provider.LoadPackageObject(candidate) as UTexture2D;
                    if (texture is not null)
                    {
                        objectPath = candidate;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    loadError = ex;
                }
            }

            if (texture is null)
            {
                var reason = loadError?.Message.Split('\n')[0];
                return new Result(false,
                    "The referenced texture could not be loaded from the game or active workspace." +
                    (string.IsNullOrWhiteSpace(reason) ? "" : $"\n\n{reason}"), objectPath);
            }

            var decoded = TextureDecodeService.TryDecode(texture);
            if (decoded is null || !TextureDecodeService.TryExportPng(texture, tempPath, keepAlpha: true))
            {
                return new Result(false,
                    $"The texture uses an unsupported or unreadable pixel format ({texture.Format}).",
                    objectPath,
                    PixelFormat: texture.Format.ToString());
            }

            var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
            return new Result(true, "Texture extracted.", objectPath, decoded.Width, decoded.Height, texture.Format.ToString());
        }
        catch (Exception ex)
        {
            return new Result(false, "Texture extraction failed.\n\n" + ex.Message, objectPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // The completed export is unaffected if temporary-file cleanup is denied.
            }
        }
    }

    private static IEnumerable<string> LoadCandidates(string packagePath, string objectPath)
    {
        yield return objectPath;
        yield return packagePath;
        if (packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = packagePath["/Game/".Length..];
            yield return GameContentPrefix + relative + "." + UnrealPathUtil.AssetName(packagePath);
            yield return GameContentPrefix + relative;
        }
    }

    private static void AddLooseContent(DefaultFileProvider provider, IEnumerable<string>? contentRoots)
    {
        if (contentRoots is null)
        {
            return;
        }

        var roots = contentRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < roots.Length; index++)
        {
            using var loose = new DefaultFileProvider(
                roots[index],
                SearchOption.AllDirectories,
                new VersionContainer(EGame.GAME_UE5_6),
                StringComparer.OrdinalIgnoreCase);
            loose.Initialize();
            if (loose.LooseFileCount == 0)
            {
                continue;
            }

            var files = loose.Files.ToDictionary(
                pair => GameContentPrefix + pair.Key.TrimStart('/', '\\').Replace('\\', '/'),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            provider.Files.AddFiles(files, long.MaxValue - index);
        }
    }
}
