using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Traced minifigure sequence adapters using the in-game-proven player attack shell.</summary>
internal static class PlayerMeleeAdapterService
{
    internal const string Bat = "player-baseball-bat";
    internal const string Baton = "player-stun-baton";
    internal const string Shell = "/Game/Animation/Enemies/RedHoodGang_Blade/Combat/AM_D0_AttackFwd_Chain_1_RHGBlade";
    internal sealed record Attack(string Sequence, float Start, float End, float Impact);
    private static readonly Attack[] BatAttacks = [
        new("/Game/Animation/Enemies/RedHoodGang_Blunt/Combat/A_Attack1_RHGBlunt", 0, 1.7666667f, 1f),
        new("/Game/Animation/Enemies/RedHoodGang_Blunt/Combat/A_RetaliationAttack_Attack_RHGBlunt", 0, 1.6666666f, .5962905f)
    ];
    private static readonly Attack[] BatonAttacks = [
        new("/Game/Animation/Enemies/RedHoodGang_StunBaton/Combat/A_BatonSlam_RHGStunBaton", .55f, 2.6666667f, 1.372765f)
    ];
    internal static bool IsSequenceAdapter(string? id) => id is Bat or Baton;
    internal static bool Enabled(string? id) => id == SwordCombatService.StyleId || IsSequenceAdapter(id);
    internal static string Label(string? id) => id switch { Bat => "Baseball bat", Baton => "Baton", _ => "Sword" };
    internal static IReadOnlyList<Attack> Attacks(string? id) => id switch { Bat => BatAttacks, Baton => BatonAttacks, _ => Array.Empty<Attack>() };
    internal static SwordCombatSettings Defaults(string? id) => IsSequenceAdapter(id)
        ? new() { AttackMontages = [Shell, Shell, Shell, Shell] } : new();
    internal static IReadOnlyList<string> RequiredPackages { get; } = BatAttacks.Concat(BatonAttacks).Select(a => a.Sequence).Append(Shell).ToArray();

    internal static IReadOnlyList<string> Validate(AbilityLoadoutProfile profile)
    {
        var settings = profile.SwordCombat ?? Defaults(profile.FightingStyleId);
        var errors = SwordCombatService.ValidateSettings(settings, !HeldItemService.Independent(profile)).ToList();
        if (IsSequenceAdapter(profile.FightingStyleId)) {
            if (!HeldItemService.Independent(profile)) errors.Add("Bat/baton combat requires separately configured Held items.");
            if (settings.AttackMontages is not { Count: 4 } || settings.AttackMontages.Any(p => p != Shell))
                errors.Add("Bat/baton attacks require the verified player adaptation. Restore defaults in Combat settings; raw enemy montages cannot substitute for this preset.");
        }
        return errors;
    }

    internal static void Adapt(UAsset asset, string style, int state, SwordCombatService.Context context)
    {
        if (!IsSequenceAdapter(style)) return;
        var attacks = Attacks(style); var attack = attacks[state % attacks.Count];
        var source = context.Read(attack.Sequence);
        if (!source.Exports.Any(e => e.GetExportClassType()?.ToString() == "AnimSequence") ||
            !source.Imports.Any(i => i.ObjectName.ToString() == "SKEL_LEGOfig"))
            throw new InvalidDataException("Player weapon attacks require SKEL_LEGOfig: " + attack.Sequence);
        var sourceLength = source.Exports.OfType<NormalExport>().SelectMany(e => e.Data.OfType<FloatPropertyData>())
            .Single(p => p.Name.ToString() == "SequenceLength").Value;
        Near(sourceLength, attack.End);
        var anim = Animation(asset);
        var segment = Segment(anim);
        var reference = segment.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "AnimReference");
        reference.Value = SwordCombatService.Obj(asset, attack.Sequence, UnrealPathUtil.AssetName(attack.Sequence), "/Script/Engine", "AnimSequence");
        anim.CreateBeforeSerializationDependencies.Add(reference.Value);
        Set(segment.Value, "StartPos", 0); Set(segment.Value, "AnimStartTime", attack.Start); Set(segment.Value, "AnimEndTime", attack.End);
        Set(segment.Value, "AnimPlayRate", 1);
        var length = attack.End - attack.Start; var impact = attack.Impact - attack.Start;
        Set(anim.Data, "SequenceLength", length);
        var notifies = anim.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Notifies");
        var hit = notifies.Value.OfType<StructPropertyData>().Single(e => EventName(e).Contains("DefaultHitFrame"));
        var oldImpact = Float(hit.Value, "LinkValue");
        if (!float.IsFinite(oldImpact) || oldImpact <= 0) throw new InvalidDataException("Invalid player attack-shell contact time.");
        float Retime(float t) => t <= oldImpact ? t * impact / oldImpact : impact + (t - oldImpact);
        // Keep the tested hitbox/warp/chain events. Never copy enemy AoE, self-status or AI telegraphs.
        foreach (var evt in notifies.Value.OfType<StructPropertyData>()) {
            var time = Float(evt.Value, "LinkValue"); var duration = Float(evt.Value, "Duration");
            var next = Retime(time); var end = Math.Min(length - .01f, Retime(time + duration));
            if (EventName(evt) == "BP_BreakoutIntoNextAttack_Notify_C") next = impact + .18f;
            if (EventName(evt) == "BP_Breakout_Notify_C") next = impact + .18f + (length - impact - .18f) * .65f;
            Set(evt.Value, "LinkValue", next);
            if (duration > 0) {
                Set(evt.Value, "Duration", Math.Max(.001f, end - next));
                var endLink = evt.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "EndLink");
                if (endLink is not null) Set(endLink.Value, "LinkValue", end);
            }
        }
        notifies.Value = notifies.Value.OrderBy(e => Float(((StructPropertyData)e).Value, "LinkValue")).ToArray();
        Verify(asset, style, state);
    }

    internal static void Verify(UAsset asset, string style, int state)
    {
        if (!IsSequenceAdapter(style)) return;
        var attacks = Attacks(style); var attack = attacks[state % attacks.Count];
        var anim = Animation(asset); var segment = Segment(anim);
        var reference = segment.Value.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "AnimReference");
        if (SwordCombatService.Package(asset, reference.Value) != attack.Sequence) throw new InvalidDataException("Incorrect player-adapter attack sequence.");
        var impact = attack.Impact - attack.Start; var length = attack.End - attack.Start;
        Near(Float(anim.Data, "SequenceLength"), length); Near(Float(segment.Value, "AnimStartTime"), attack.Start);
        Near(Float(segment.Value, "AnimEndTime"), attack.End); Near(Float(segment.Value, "StartPos"), 0); Near(Float(segment.Value, "AnimPlayRate"), 1);
        var events = anim.Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Notifies").Value.OfType<StructPropertyData>().ToArray();
        Near(Float(events.Single(e => EventName(e).Contains("DefaultHitFrame")).Value, "LinkValue"), impact);
        var hitEnd = events.Where(e => EventName(e) == "MeleeHitBox").Max(e => Float(e.Value, "LinkValue") + Float(e.Value, "Duration"));
        var chain = Float(events.Single(e => EventName(e) == "BP_BreakoutIntoNextAttack_Notify_C").Value, "LinkValue");
        var recovery = Float(events.Single(e => EventName(e) == "BP_Breakout_Notify_C").Value, "LinkValue");
        if (chain <= hitEnd || recovery <= chain || recovery >= length) throw new InvalidDataException("Invalid player-adapter combo timing.");
        foreach (var evt in events) {
            var time = Float(evt.Value, "LinkValue"); var duration = Float(evt.Value, "Duration");
            if (!float.IsFinite(time) || !float.IsFinite(duration) || time < 0 || duration < 0 || time + duration > length ||
                EventName(evt).Contains("Counterable") || EventName(evt).Contains("AreaOfEffect") || EventName(evt).Contains("AddGameplayTag"))
                throw new InvalidDataException("Unsafe or out-of-range player-adapter notify.");
        }
    }
    private static NormalExport Animation(UAsset asset) => asset.Exports.OfType<NormalExport>().Single(e => e.GetExportClassType()?.ToString() == "AnimMontage");
    private static StructPropertyData Segment(NormalExport anim) => Walk(anim.Data).OfType<StructPropertyData>().Single(p => p.Value.Any(v => v.Name.ToString() == "AnimReference"));
    private static IEnumerable<PropertyData> Walk(IEnumerable<PropertyData> properties) {
        foreach (var p in properties) { yield return p; var children = p is StructPropertyData s ? s.Value : p is ArrayPropertyData a ? a.Value.AsEnumerable() : [];
            foreach (var child in Walk(children)) yield return child; }
    }
    private static float Float(IEnumerable<PropertyData> properties, string name) => properties.OfType<FloatPropertyData>().Single(p => p.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static void Set(IEnumerable<PropertyData> properties, string name, float value) => properties.OfType<FloatPropertyData>().Single(p => p.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)).Value = value;
    private static string EventName(StructPropertyData evt) => evt.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "NotifyName").Value.ToString();
    private static void Near(float actual, float expected) { if (!float.IsFinite(actual) || Math.Abs(actual - expected) > .0001f) throw new InvalidDataException($"Player adapter donor/timing changed: expected {expected}, found {actual}."); }
}
