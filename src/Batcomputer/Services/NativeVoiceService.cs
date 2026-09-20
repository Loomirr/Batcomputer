using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Keep a paired-cape visual scaffold from replacing the gameplay donor's dialogue voice.</summary>
internal static class NativeVoiceService
{
    internal static string ReadVoice(UAsset asset) => VoiceProperty(asset)?.Value.ToString() ?? "";

    private static NamePropertyData? VoiceProperty(UAsset asset) => asset.Exports
        .OfType<NormalExport>()
        .Where(e => e.ObjectName.ToString() == "WubDialogueVoiceActor_GEN_VARIABLE")
        .SelectMany(e => e.Data).OfType<NamePropertyData>()
        .SingleOrDefault(p => p.Name.ToString() == "VoiceActorName");

    internal static void RestoreAdapterVoice(NativeSuitProject project, string contentRoot)
    {
        if (project.PairedCapeAdapter is not { } adapter) return;
        var donorPackage = adapter.GameplayDonorPackage;
        var donorPath = ExtractedPackagePathService.ResolvePackageUasset(
            AppSettings.Current.EffectiveExtractedContentRoot(), donorPackage)
            ?? throw new InvalidDataException("The paired-cape voice donor is missing: " + donorPackage);
        var mappings = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()
            ?? throw new InvalidDataException("Configure the current mappings before rebuilding dialogue voices."));
        UAsset Read(string path) => new(path, EngineVersion.VER_UE5_6, mappings,
            CustomSerializationFlags.SkipPreloadDependencyLoading);
        var voice = ReadVoice(Read(donorPath));
        if (string.IsNullOrWhiteSpace(voice) || voice == "None")
            throw new InvalidDataException("The paired-cape gameplay donor has no explicit native dialogue voice: " + donorPackage);
        foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene })
        {
            var path = Path.Combine(contentRoot, package[6..].Replace('/', Path.DirectorySeparatorChar)) + ".uasset";
            var asset = Read(path);
            var property = VoiceProperty(asset);
            // Cinematic actors commonly use sequence-owned dialogue and have no voice component.
            if (property is null)
            {
                if (package == project.TargetPackages.Playable)
                    throw new InvalidDataException("The paired-cape playable scaffold has no dialogue voice component.");
                continue;
            }
            if (property.Value.ToString() == voice) continue;
            NativeBlueprintSchemaService.EnsureParents(asset);
            property.Value = new FName(asset, voice);
            asset.Write(path);
            if (ReadVoice(Read(path)) != voice)
                throw new InvalidDataException("The staged dialogue voice did not survive serialization: " + package);
        }
    }
}
