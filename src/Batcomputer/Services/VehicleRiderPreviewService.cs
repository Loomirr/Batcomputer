using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse_Conversion.Animations;

namespace Batcomputer;

/// <summary>Native idle poses for placement checks, not a simulation of the driving AnimBlueprint.</summary>
internal static class VehicleRiderPreviewService
{
    internal const string Batman = "/Game/Characters/Minifig/Batman/BP_Batman_TheBatman2025_Playable";
    internal const string Catwoman = "/Game/Characters/Minifig/Catwoman/BP_CatWoman_Default_Mask_Playable";
    internal const string DriverPose = "/Game/Animation/LEGOfig/_Shared/Vehicles/Drive_SteeringWheel_Setups/A_Drive_SteeringWheel_Idle_Jumper_R_03_Minifig";
    internal const string PassengerPose = "/Game/Animation/LEGOfig/_Shared/Vehicles/Batmobile1966/A_Ride_Batmobile1966_Passenger";
    internal sealed record Bone(string Name, int Parent, float[] Reference, float[] Animated);
    internal sealed record Rider(string Seat, string Name, string Folder, string Animation, IReadOnlyList<Bone> Bones);
    internal static IReadOnlyList<Rider> Create(string folder, CancellationToken cancellation, Action<string>? progress = null)
    {
        var settings = AppSettings.Current;
        var riders = new List<Rider>();
        // Sequential: the existing character texture/face exporter has per-preview state.
        foreach (var (id, seat, name, bp, animation) in new[] {
            ("Batman", "seat:SeatDriver", "Batman · The Batman 2025", Batman, DriverPose),
            ("Catwoman", "seat:SeatPassenger", "Catwoman · default", Catwoman, PassengerPose) })
        {
            cancellation.ThrowIfCancellationRequested(); progress?.Invoke("Preparing seated " + name + "…");
            var target = Path.Combine(folder, id);
            if (!File.Exists(Path.Combine(target, "models.json")))
                ModelPreviewService.BuildPreviewCharacter(settings.EffectiveGamePaksRoot(), settings.EffectiveUsmapPath()!, bp,
                    previewOptions: new() { AllowPartMover = false, OutputDirectory = target, IgnoreSavedLayout = true, HiddenComponents = ["Face", "FaceMesh", "LEGOFace", "Cape"] });
            cancellation.ThrowIfCancellationRequested();
            using var provider = ModelPreviewService.MakeProvider(settings.EffectiveGamePaksRoot(), settings.EffectiveUsmapPath()!);
            var sequence = provider.LoadPackageObject<UAnimSequence>(animation);
            var skeleton = sequence.Skeleton?.Load<USkeleton>() ?? throw new InvalidDataException("The seated animation has no skeleton.");
            var converted = AnimConverter.ConvertAnims(skeleton, sequence).Sequences.First();
            var bones = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
            VehicleAssetService.Require(converted.Tracks.Count == bones.Length, "The seated animation track map changed.");
            var pose = new List<Bone>();
            static float[] Transform(CUE4Parse.UE4.Objects.Core.Math.FVector p, CUE4Parse.UE4.Objects.Core.Math.FQuat q, CUE4Parse.UE4.Objects.Core.Math.FVector scale)
                => [p.X / 100, p.Z / 100, p.Y / 100, -q.X, -q.Z, -q.Y, q.W, scale.X, scale.Z, scale.Y];
            for (int i = 0; i < bones.Length; i++)
            {
                var reference = skeleton.ReferenceSkeleton.FinalRefBonePose[i];
                var q = reference.Rotation; var p = reference.Translation; var scale = reference.Scale3D;
                converted.Tracks[i].GetBoneTransform(0, converted.NumFrames, ref q, ref p, ref scale);
                // CPU skinning uses native reference transforms, not the CUE glTF exporter’s
                // inverse-bind matrices. Reflect Y/Z for the exported geometry's handedness.
                pose.Add(new(bones[i].Name.Text, bones[i].ParentIndex, Transform(reference.Translation, reference.Rotation, reference.Scale3D), Transform(p, q, scale)));
            }
            riders.Add(new(seat, name, "Riders/" + id + "/", animation, pose));
        }
        File.WriteAllText(Path.Combine(folder, "riders.json"), JsonSerializer.Serialize(riders, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return riders;
    }
}
