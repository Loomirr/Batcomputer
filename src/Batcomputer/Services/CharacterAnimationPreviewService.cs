using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Animations;
using System.Collections.Concurrent;

namespace Batcomputer;

/// <summary>Catalogs a character family's sequences and samples body-compatible clips on demand.</summary>
internal static class CharacterAnimationPreviewService
{
    internal sealed record RigBone(string Name, int Parent, float[] Reference);
    internal sealed record Track(string Bone, float[][] Frames);
    internal sealed record Clip(string Label, string Package, string TargetPart, float FramesPerSecond,
        int FrameCount, IReadOnlyList<RigBone> Rig, IReadOnlyList<Track> Tracks, string? SampledSequence = null,
        IReadOnlyList<Dictionary<string, float>>? MaterialCurves = null);
    internal sealed record Asset(string Name, string Group, string Package, string Kind);
    internal sealed record Preview(IReadOnlyList<RigBone> Rig, IReadOnlyList<Clip> Clips, IReadOnlyList<Asset> Catalog);
    internal sealed record Bundle(Clip Primary, IReadOnlyList<Clip> Companions, IReadOnlyList<string> Warnings);
    private sealed record Source(string PaksDir, string UsmapPath,
        string[] LooseContentRoots, string[] AdditionalPakDirectories, HashSet<string> Packages,
        (string Part, string MeshPath)[] Parts, Asset[] Catalog);
    private static readonly ConcurrentDictionary<string, Source> Sources = new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(string folder, string paksDir, string usmapPath,
        IEnumerable<string>? looseContentRoots, IEnumerable<string>? additionalPakDirectories, Preview preview,
        IEnumerable<(string Part, string MeshPath)> parts)
    {
        Sources[Path.GetFullPath(folder)] = new Source(paksDir, usmapPath,
            looseContentRoots?.ToArray() ?? [], additionalPakDirectories?.ToArray() ?? [],
            preview.Catalog.Select(asset => asset.Package).ToHashSet(StringComparer.OrdinalIgnoreCase),
            parts.ToArray(), preview.Catalog.ToArray());
        // A preview owns only paths, not a live pak provider. Bound stale preview registrations.
        if (Sources.Count > 32)
            foreach (var stale in Sources.Keys.Where(key => !Directory.Exists(key)).ToArray()) Sources.TryRemove(stale, out _);
    }

    internal static Clip Load(string folder, string package)
    {
        if (!Sources.TryGetValue(Path.GetFullPath(folder), out var source) || !source.Packages.Contains(package))
            throw new InvalidDataException("This animation is not in the active character family's catalog.");
        using var provider = ModelPreviewService.MakeProvider(source.PaksDir, source.UsmapPath,
            source.LooseContentRoots, source.AdditionalPakDirectories);
        return LoadFrom(provider, source, package);
    }

    internal static Bundle LoadBundle(string folder, string package)
    {
        if (!Sources.TryGetValue(Path.GetFullPath(folder), out var source) || !source.Packages.Contains(package))
            throw new InvalidDataException("This animation is not in the active character family's catalog.");
        using var provider = ModelPreviewService.MakeProvider(source.PaksDir, source.UsmapPath,
            source.LooseContentRoots, source.AdditionalPakDirectories);
        var primary = LoadFrom(provider, source, package);
        var companions = new List<Clip>();
        var warnings = new List<string>();
        var usedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary.TargetPart };
        foreach (var asset in CompanionAssets(source.Catalog, primary))
        {
            try
            {
                var companion = LoadFrom(provider, source, asset.Package);
                if (usedParts.Add(companion.TargetPart)) companions.Add(companion);
            }
            catch (Exception ex) { warnings.Add(asset.Name + ": " + ex.Message.Split('\n')[0]); }
        }
        return new(primary, companions, warnings);
    }

    private static IEnumerable<Asset> CompanionAssets(IReadOnlyList<Asset> catalog, Clip primary)
    {
        var source = primary.SampledSequence ?? primary.Package;
        var name = UnrealPathUtil.AssetName(source);
        foreach (var suffix in new[] { "_LEGOface", "_HAT", "_Cape" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        var folder = source[..source.LastIndexOf('/')];
        var names = new[] { name, name + "_HAT", name + "_LEGOface", name + "_Cape" };
        foreach (var expected in names)
        {
            var match = catalog.Where(asset => asset.Kind == "Sequence" &&
                    asset.Name.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                    !asset.Package.Equals(primary.Package, StringComparison.OrdinalIgnoreCase))
                .OrderBy(asset => asset.Package.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(asset => asset.Package.Length)
                .FirstOrDefault();
            if (match is not null) yield return match;
        }
    }

    private static Clip LoadFrom(DefaultFileProvider provider, Source source, string package)
    {
        var asset = provider.LoadPackageObject(package)
            ?? throw new InvalidDataException("This animation asset could not be read.");
        var anim = ResolveSequence(asset)
            ?? throw new InvalidDataException("This montage has no readable animation sequence segment.");
        var skeleton = anim.Skeleton?.Load<USkeleton>()
            ?? throw new InvalidDataException("This animation has no skeleton.");
        var skeletonPath = skeleton.GetPathName();
        var matches = new List<string>();
        foreach (var part in source.Parts)
        {
            try
            {
                if (provider.LoadPackageObject(part.MeshPath) is USkeletalMesh mesh &&
                    string.Equals(mesh.Skeleton?.Load<USkeleton>()?.GetPathName(), skeletonPath, StringComparison.OrdinalIgnoreCase))
                    matches.Add(part.Part);
            }
            catch { /* A missing optional attachment cannot prevent body animation preview. */ }
        }
        var target = PreferredPart(package, matches);
        if (target is null)
            throw new InvalidDataException("This animation targets a rig not present in the current character assembly (often a prop or alternate cape).");
        return Sample(provider, skeleton, package, target, anim);
    }

    internal static Preview Read(DefaultFileProvider provider, USkeletalMesh body, string? character)
    {
        var skeleton = body.Skeleton?.Load<USkeleton>();
        if (skeleton is null || string.IsNullOrWhiteSpace(character)) return new([], [], []);
        var info = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        var reference = skeleton.ReferenceSkeleton.FinalRefBonePose;
        var rig = Enumerable.Range(0, info.Length)
            .Select(i => new RigBone(info[i].Name.Text, info[i].ParentIndex,
                VehicleAnimationPreviewService.Transform(reference[i].Translation, reference[i].Rotation, reference[i].Scale3D)))
            .ToArray();

        var family = "/Animation/LEGOfig/" + character + "/";
        var catalog = provider.Files
            .Select(pair => pair.Key.Replace('\\', '/'))
            .Where(path => path.Contains("/Content/", StringComparison.OrdinalIgnoreCase) &&
                path.Contains(family, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Select(path => "/Game/" + path[(path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + 9)..^7])
            .Where(path => path.StartsWith("/Game/Animation/LEGOfig/", StringComparison.OrdinalIgnoreCase))
            .Select(path => new Asset(UnrealPathUtil.AssetName(path), Group(path, character), path,
                UnrealPathUtil.AssetName(path).StartsWith("AM_", StringComparison.OrdinalIgnoreCase) ? "Montage" : "Sequence"))
            .Where(item => item.Name.StartsWith("A_", StringComparison.OrdinalIgnoreCase) ||
                item.Name.StartsWith("AM_", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Package, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = catalog
            .Select(item => (item.Package, item.Name, Rank: Rank(item.Name)))
            .Where(item => item.Rank < 3)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Name.Contains("N_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name.Length).ThenBy(item => item.Package, StringComparer.OrdinalIgnoreCase)
            .Take(24);

        var clips = new List<Clip>();
        var usedRanks = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            if (usedRanks.Contains(candidate.Rank)) continue;
            try
            {
                var sampled = Sample(provider, skeleton, candidate.Package, "CharacterMesh0");
                clips.Add(sampled with { Label = candidate.Rank switch { 0 => "Idle", 1 => "Walk", _ => "Run" } });
                usedRanks.Add(candidate.Rank);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Character animation preview skipped " + candidate.Package + ": " + ex.Message);
            }
            if (clips.Count == 3) break;
        }
        return new(rig, clips, catalog);
    }

    private static string? PreferredPart(string package, IReadOnlyList<string> matches)
    {
        if (matches.Count == 0) return null;
        var name = UnrealPathUtil.AssetName(package);
        string? prefer = name.Contains("LEGOface", StringComparison.OrdinalIgnoreCase) ? "Face" :
            name.Contains("_Cape", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Cape", StringComparison.OrdinalIgnoreCase) ? "Cape" :
            name.Contains("_HAT", StringComparison.OrdinalIgnoreCase) ? "Head" : null;
        return prefer is not null && matches.Contains(prefer, StringComparer.OrdinalIgnoreCase)
            ? prefer : matches[0];
    }

    private static UAnimSequence? ResolveSequence(CUE4Parse.UE4.Assets.Exports.UObject asset)
    {
        if (asset is UAnimSequence direct) return direct;
        static FStructFallback? StructValue(object? value) => value as FStructFallback
            ?? (value as FScriptStruct)?.StructType as FStructFallback;
        var tracks = asset.GetOrDefault<UScriptArray>("SlotAnimTracks");
        foreach (var trackEntry in tracks?.Properties ?? [])
        {
            var track = StructValue(trackEntry.GenericValue);
            var animTrack = StructValue(track?.Properties.FirstOrDefault(p =>
                p.Name.Text.Equals("AnimTrack", StringComparison.OrdinalIgnoreCase))?.Tag?.GenericValue);
            var segments = animTrack?.GetOrDefault<UScriptArray>("AnimSegments");
            foreach (var segmentEntry in segments?.Properties ?? [])
            {
                var sequence = StructValue(segmentEntry.GenericValue)?.GetOrDefault<FPackageIndex>("AnimReference")
                    ?.ResolvedObject?.Load() as UAnimSequence;
                if (sequence is not null) return sequence;
            }
        }
        return null;
    }

    private static Clip Sample(DefaultFileProvider provider, USkeleton skeleton, string package, string targetPart,
        UAnimSequence? resolved = null)
    {
        if ((resolved ?? provider.LoadPackageObject(package)) is not UAnimSequence anim)
            throw new InvalidDataException("This asset is not a readable animation sequence.");
        if (!string.Equals(anim.Skeleton?.Load<USkeleton>()?.GetPathName(), skeleton.GetPathName(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This sequence targets a different rig.");
        var converted = skeleton.ConvertAnims(anim).Sequences.FirstOrDefault()
            ?? throw new InvalidDataException("The sequence has no decodable body tracks.");
        var info = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        var reference = skeleton.ReferenceSkeleton.FinalRefBonePose;
        var rig = Enumerable.Range(0, info.Length)
            .Select(i => new RigBone(info[i].Name.Text, info[i].ParentIndex,
                VehicleAnimationPreviewService.Transform(reference[i].Translation, reference[i].Rotation, reference[i].Scale3D)))
            .ToArray();
        if (converted.Tracks.Count != info.Length || converted.NumFrames < 1)
            throw new InvalidDataException("The sequence's track map does not match this body rig.");
        const int limit = 120;
        var count = Math.Clamp(converted.NumFrames, 2, limit);
        var tracks = new List<Track>();
        for (var bone = 1; bone < info.Length; bone++) // Root motion stays locked for an in-place preview.
        {
            var frames = new float[count][];
            var moved = false;
            var rest = VehicleAnimationPreviewService.Transform(reference[bone].Translation, reference[bone].Rotation, reference[bone].Scale3D);
            for (var frame = 0; frame < count; frame++)
            {
                var key = frame * (converted.NumFrames - 1f) / (count - 1);
                var q = reference[bone].Rotation; var p = reference[bone].Translation; var s = reference[bone].Scale3D;
                converted.Tracks[bone].GetBoneTransform(key, converted.NumFrames, ref q, ref p, ref s);
                frames[frame] = VehicleAnimationPreviewService.Transform(p, q, s);
                moved |= Enumerable.Range(0, 10).Any(axis => Math.Abs(frames[frame][axis] - rest[axis]) > .0001f);
            }
            if (moved) tracks.Add(new Track(info[bone].Name.Text, frames));
        }
        var duration = anim.SequenceLength > 0 ? anim.SequenceLength : converted.NumFrames / Math.Max(1, converted.FramesPerSecond);
        duration = Math.Max(1f / 30, duration);
        var sampledSequence = resolved is null ? null : anim.GetPathName().Split('.')[0];
        IReadOnlyList<Dictionary<string, float>>? materialCurves = null;
        if (targetPart.Equals("Face", StringComparison.OrdinalIgnoreCase))
        {
            var nativeFrames = Enumerable.Range(0, count)
                .Select(frame => (int)MathF.Round(frame * (converted.NumFrames - 1f) / (count - 1)))
                .ToArray();
            var decoded = ModelPreviewService.LoadFaceMaterialCurves(anim, nativeFrames, converted.NumFrames);
            if (decoded is not null)
                materialCurves = nativeFrames.Select(frame => decoded.GetValueOrDefault(frame) ??
                    new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        return new Clip(UnrealPathUtil.AssetName(package), package, targetPart, (count - 1) / duration, count,
            rig, tracks, string.Equals(sampledSequence, package, StringComparison.OrdinalIgnoreCase) ? null : sampledSequence,
            materialCurves);
    }

    private static int Rank(string name) =>
        name.StartsWith("A_Idle_", StringComparison.OrdinalIgnoreCase) ? 0 :
        name.StartsWith("A_Walk", StringComparison.OrdinalIgnoreCase) && !name.Contains("Pose", StringComparison.OrdinalIgnoreCase) ? 1 :
        (name.StartsWith("A_Run", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("A_Sprint", StringComparison.OrdinalIgnoreCase)) &&
        !name.Contains("Pose", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Stop", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    private static string Group(string path, string character)
    {
        var start = ("/Game/Animation/LEGOfig/" + character + "/").Length;
        var end = path.LastIndexOf('/');
        return end > start ? path[start..end] : "General";
    }
}
