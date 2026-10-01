using System.Diagnostics;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Dialogue requests only: never edits combat, movement or compressed pose data.</summary>
internal static class AnimationVoiceCueService
{
    internal const string Revision = "voice-cues-v2";
    internal const string TemplatePackage = "/Game/Animation/LEGOfig/_Shared/Movement/A_Jump_Minifig";
    internal sealed record Cue(string Id, string Tag, int Frame, bool PlayOnEmptySwing = false);
    internal static readonly string[] Tags = ["VOX_Combat", "VOX_CombatSml", "VOX_CombatLrg", "VOX_Jump"];

    internal static Cue[] Read(JsonElement draft, int duration)
    {
        if (!draft.TryGetProperty("voiceCues", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != "batcomputer.voice-cues.v1" ||
            !value.TryGetProperty("cues", out var cues) || cues.ValueKind != JsonValueKind.Array || cues.GetArrayLength() > 16)
            throw new InvalidDataException("Invalid voice cue metadata.");
        var result = new List<Cue>();
        foreach (var item in cues.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("tag", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("frame", out var frame) || frame.ValueKind != JsonValueKind.Number || !frame.TryGetInt32(out var at))
                throw new InvalidDataException("Voice cues require an ID, category and integer frame.");
            var empty = false;
            if (item.TryGetProperty("playOnEmptySwing", out var play))
            {
                if (play.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid empty-swing voice option.");
                empty = play.GetBoolean();
            }
            result.Add(new Cue(id.GetString()!, tag.GetString()!, at, empty));
        }
        Validate(result, duration);
        return result.ToArray();
    }

    internal static void Validate(IReadOnlyList<Cue> cues, int duration)
    {
        if (duration < 1 || cues.Count > 16 || cues.Select(c => c.Id).Distinct().Count() != cues.Count ||
            cues.Select(c => c.Frame).Distinct().Count() != cues.Count || cues.Any(c =>
                string.IsNullOrEmpty(c.Id) || c.Id.Length > 64 || c.Id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')) ||
                !Tags.Contains(c.Tag, StringComparer.Ordinal) || (c.PlayOnEmptySwing && c.Tag == "VOX_Jump") || c.Frame < 0 || c.Frame >= duration))
            throw new InvalidDataException("Use unique voice cue IDs and frames, a supported category, and frames before the clip's end (maximum 16 cues).");
    }

    internal static async Task<string> ResolveTemplateAsync(string work, CancellationToken cancellation)
    {
        var file = ExtractedPackagePathService.ResolvePackageUasset(AppSettings.Current.EffectiveExtractedContentRoot(), TemplatePackage);
        if (File.Exists(file)) return file!;
        var native = Path.Combine(work, "VoiceNotifyTemplate");
        Directory.CreateDirectory(native);
        var info = new ProcessStartInfo(AppSettings.Current.EffectiveRetocExePath())
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "to-legacy", "--version", "UE5_6", "--no-shaders", "--no-script-objects", "-f", TemplatePackage[6..], AppSettings.Current.EffectiveGamePaksRoot(), native })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not extract the native dialogue notify template.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        await stdout; var errors = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("Native voice template extraction failed: " + errors);
        return ExtractedPackagePathService.ResolvePackageUasset(Path.Combine(native, "LEGOBatmanLotDK/Content"), TemplatePackage)
            ?? throw new InvalidDataException("Native dialogue notify template was not extracted.");
    }

    internal static void Apply(string file, string templateFile, IReadOnlyList<Cue> cues, int duration, int fps = 30)
    {
        Validate(cues, duration);
        if (fps != 30) throw new InvalidDataException("Voice cue frames require 30 fps.");
        if (cues.Count == 0) return;
        var maps = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        UAsset Load(string path) => new(path, EngineVersion.VER_UE5_6, maps, CustomSerializationFlags.SkipPreloadDependencyLoading);
        var target = Load(file); var donor = Load(templateFile);
        if (!target.FolderName.ToString().StartsWith("/Game/Mods/", StringComparison.Ordinal))
            throw new InvalidDataException("Voice cues may only be authored into private mod animations.");
        var animation = target.Exports.OfType<NormalExport>().Single(e => Class(e) == "AnimSequence");
        float seconds = animation.Data.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "SequenceLength").Value;
        if (!float.IsFinite(seconds) || Math.Abs(seconds - duration / (float)fps) > .002f)
            throw new InvalidDataException("Voice cue timeline does not match the cooked animation length.");
        // Never stack a second set on an already voiced clip. Author from a clean draft;
        // existing montage notifications must also be reviewed before assigning the result.
        if (target.Exports.Any(e => Class(e) == "WubDialogueAnimNotify"))
            throw new InvalidDataException("This animation already has dialogue notifies. Use a clean draft to avoid duplicate voices.");
        var source = donor.Exports.OfType<NormalExport>().Single(e => Class(e) == "WubDialogueAnimNotify");
        var sourceAnimation = donor.Exports.OfType<NormalExport>().Single(e => Class(e) == "AnimSequence");
        if (source.Extras.Length != 0 || source.Data.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "Tag").Value.ToString() != "VOX_Jump")
            throw new InvalidDataException("Unrecognized native dialogue template.");
        int sourceIndex = donor.Exports.IndexOf(source) + 1, sourceAnimationIndex = donor.Exports.IndexOf(sourceAnimation) + 1;
        var sourceEvent = sourceAnimation.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Notifies").Value
            .OfType<StructPropertyData>().Single(p => p.Value.OfType<ObjectPropertyData>().Any(o => o.Name.ToString() == "Notify" && o.Value.Index == sourceIndex));
        var events = animation.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "Notifies");
        if (events is null)
        {
            events = (ArrayPropertyData)PartGraftService.DeepClonePropertiesRebased(
                [sourceAnimation.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Notifies")], target).Single();
            events.Value = [];
            animation.Data.Add(events);
        }
        var oldExtras = animation.Extras.ToArray();
        foreach (var cue in cues.OrderBy(c => c.Frame))
        {
            int newIndex = target.Exports.Count + 1;
            FPackageIndex Rebase(FPackageIndex index)
            {
                if (index.IsNull()) return FPackageIndex.FromRawIndex(0);
                if (index.IsImport()) return AttachmentClearanceService.RebaseImport(target, donor, index);
                if (index.Index == sourceIndex) return FPackageIndex.FromRawIndex(newIndex);
                if (index.Index == sourceAnimationIndex) return FPackageIndex.FromRawIndex(target.Exports.IndexOf(animation) + 1);
                throw new InvalidDataException("Native voice notify references an unsupported export.");
            }
            var copy = (NormalExport)source.Clone(); copy.Asset = target;
            copy.ObjectName = new FName(target, "BCVoiceCue_" + cue.Id);
            if (target.Exports.Any(e => e.ObjectName.ToString() == copy.ObjectName.ToString())) throw new InvalidDataException("Voice cue export name collision.");
            copy.Data = PartGraftService.DeepClonePropertiesRebased(source.Data, target);
            copy.Data.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "Tag").Value = new FName(target, cue.Tag);
            copy.ClassIndex = Rebase(source.ClassIndex); copy.TemplateIndex = Rebase(source.TemplateIndex);
            copy.OuterIndex = Rebase(source.OuterIndex); copy.SuperIndex = Rebase(source.SuperIndex);
            copy.SerializationBeforeSerializationDependencies = source.SerializationBeforeSerializationDependencies.Select(Rebase).ToList();
            copy.CreateBeforeSerializationDependencies = source.CreateBeforeSerializationDependencies.Select(Rebase).ToList();
            copy.SerializationBeforeCreateDependencies = source.SerializationBeforeCreateDependencies.Select(Rebase).ToList();
            copy.CreateBeforeCreateDependencies = source.CreateBeforeCreateDependencies.Select(Rebase).ToList();
            foreach (var obj in Walk(copy.Data).OfType<ObjectPropertyData>()) obj.Value = Rebase(obj.Value);
            if (!target.HasUnversionedProperties) CompleteTypeTags(target, copy.Data);
            target.Exports.Add(copy);
            var added = (StructPropertyData)PartGraftService.DeepClonePropertiesRebased([sourceEvent], target).Single();
            foreach (var obj in Walk([added]).OfType<ObjectPropertyData>()) obj.Value = Rebase(obj.Value);
            added.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "SegmentLength").Value = seconds;
            added.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "NotifyTriggerChance").Value = 1;
            added.Value.OfType<BoolPropertyData>().Single(p => p.Name.ToString() == "bCanBeFilteredViaRequest").Value = !cue.PlayOnEmptySwing;
            // Instantaneous event, absolute seconds: do not inherit the donor jump's offsets.
            foreach (var value in Walk([added]).OfType<FloatPropertyData>())
            {
                if (value.Name.ToString() == "LinkValue") value.Value = cue.Frame / (float)fps;
                if (value.Name.ToString() is "TriggerTimeOffset" or "EndTriggerTimeOffset" or "Duration" or "duration") value.Value = 0;
            }
            events.Value = [..events.Value, added];
            if (!target.HasUnversionedProperties) CompleteTypeTags(target, [events]);
            animation.CreateBeforeSerializationDependencies.Add(FPackageIndex.FromRawIndex(newIndex));
        }
        if (!oldExtras.SequenceEqual(animation.Extras)) throw new InvalidDataException("Voice edit changed compressed motion.");
        target.Write(file);
        var saved = Load(file);
        var savedAnimation = saved.Exports.OfType<NormalExport>().Single(e => Class(e) == "AnimSequence");
        if (!oldExtras.SequenceEqual(savedAnimation.Extras) || saved.Exports.Count != target.Exports.Count)
            throw new InvalidDataException("Voice notification serialization failed verification.");
        var savedEvents = savedAnimation.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Notifies").Value.OfType<StructPropertyData>().ToArray();
        foreach (var cue in cues)
        {
            var notify = saved.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString() == "BCVoiceCue_" + cue.Id);
            var index = saved.Exports.IndexOf(notify) + 1;
            var linked = savedEvents.Single(e => e.Value.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "Notify" && p.Value.Index == index));
            if (Class(notify) != "WubDialogueAnimNotify" ||
                notify.Data.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "Tag").Value.ToString() != cue.Tag ||
                Math.Abs(linked.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "LinkValue").Value - cue.Frame / (float)fps) > .00001f ||
                linked.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "NotifyTriggerChance").Value != 1 ||
                linked.Value.OfType<BoolPropertyData>().Single(p => p.Name.ToString() == "bCanBeFilteredViaRequest").Value == cue.PlayOnEmptySwing)
                throw new InvalidDataException("A cooked voice cue did not retain its category, frame or trigger chance.");
        }
    }

    private static string Class(Export export) => export.GetExportClassType().ToString();
    private static void CompleteTypeTags(UAsset asset, IEnumerable<PropertyData> properties)
    {
        // Game templates omit type headers; fresh UE cooks use UE5 complete headers.
        FPropertyTypeName Type(params (string Name, int Children)[] nodes) => new(nodes.Select(n =>
            new FPropertyTypeNameNode { Name = new FName(asset, n.Name), InnerCount = n.Children }).ToList(), true);
        foreach (var p in Walk(properties))
        {
            if (p.PropertyTypeName is not null) continue;
            p.PropertyTypeName = p switch
            {
                ArrayPropertyData a when a.ArrayType.ToString() == "StructProperty" && a.Value.FirstOrDefault() is StructPropertyData s
                    => Type(("ArrayProperty", 1), ("StructProperty", 1), (s.StructType.ToString(), 1), ("/Script/Engine", 0)),
                StructPropertyData s => Type(("StructProperty", 1), (s.StructType.ToString(), 1), ("/Script/Engine", 0)),
                EnumPropertyData e => Type(("EnumProperty", 1), (e.EnumType.ToString(), 1), ("/Script/Engine", 0)),
                ArrayPropertyData => throw new InvalidDataException("Unsupported voice template array."),
                _ => Type((p.PropertyType.ToString(), 0))
            };
            if (p is EnumPropertyData enumeration && !enumeration.Value.ToString().Contains("::", StringComparison.Ordinal))
                enumeration.Value = new FName(asset, enumeration.EnumType + "::" + enumeration.Value);
        }
    }
    private static IEnumerable<PropertyData> Walk(IEnumerable<PropertyData> values)
    {
        foreach (var value in values)
        {
            yield return value;
            var children = value is StructPropertyData s ? s.Value : value is ArrayPropertyData a ? a.Value.AsEnumerable() : [];
            foreach (var child in Walk(children)) yield return child;
        }
    }
}
