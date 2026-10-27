using System.Security.Cryptography;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Build-time, suit-local movement changes. No polling or global native asset patches.</summary>
internal static class CharacterMovementService
{
    internal const string SprintAbility = "/Game/Characters/Abilities/CoreAbilities/Sprinting/GA_Sprinting";
    internal const string SprintEffect = "/Game/Characters/Abilities/CoreAbilities/Sprinting/GE_Sprinting";
    internal const string GlideDefaults = "/Game/Characters/Abilities/CoreAbilities/Gliding/GE_GlidingCharacterDefaults";
    internal const string GlideDescent = "/Game/Characters/Abilities/CoreAbilities/Gliding/GE_GlideDescentRate";
    internal const string SetTemplate = "/Game/Characters/Abilities/CoreAbilities/Gliding/AS_Gliding";
    internal const string RunTemplate = "/Game/Characters/Abilities/GameplayEffects/Movemement/GE_IncreaseMovementSpeed";
    internal const string JumpTemplate = "/Game/Characters/Abilities/CoreAbilities/BalanceSplines/GE_BalanceJump_JumpHeight";

    internal static bool Enabled(CharacterMovementSettings? s) => s is not null &&
        (s.RunSpeedMultiplier.HasValue || s.SprintSpeed.HasValue || s.JumpHeightMultiplier.HasValue ||
         s.GlideSpeedMultiplier.HasValue || s.GlideDescentMultiplier.HasValue);

    internal static string? ValidationError(CharacterMovementSettings? s)
    {
        if (s is null) return null;
        foreach (var (name, value, min, max) in new (string, float?, float, float)[] {
            ("Run speed", s.RunSpeedMultiplier, .25f, 3), ("Sprint speed", s.SprintSpeed, 50, 7500),
            ("Jump height", s.JumpHeightMultiplier, .25f, 4), ("Glide speed", s.GlideSpeedMultiplier, .25f, 3),
            ("Glide descent", s.GlideDescentMultiplier, .1f, 3) })
            if (value is { } v && (!float.IsFinite(v) || v < min || v > max))
                return $"{name} must be between {min} and {max}.";
        return null;
    }

    internal static string? ProjectError(NativeSuitProject p)
    {
        if (!Enabled(p.MovementAttributes)) return null;
        if (ValidationError(p.MovementAttributes) is { } error) return error;
        if (!string.IsNullOrWhiteSpace(p.ReplacementTarget)) return "Character replacers support visual edits only; clear movement attributes.";
        if (p.MovementAttributes!.SprintSpeed.HasValue && p.GameplayAnimationGraphs.SelectMany(g => g.Abilities)
            .Any(binding => binding.OriginalPackage == SprintAbility))
            return "This character uses a custom Sprint controller. Keep its existing sprint tuning by clearing the Sprint speed override; the other movement controls remain independent.";
        return null;
    }

    internal static string Root(NativeSuitProject p)
    {
        var package = p.TargetPackages.Playable;
        var mod = package.Split('/').ElementAtOrDefault(3) ?? "";
        if (!package.StartsWith("/Game/Mods/", StringComparison.Ordinal) || mod.Length == 0 ||
            mod.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("Movement attributes need a character/suit package namespace.");
        return $"/Game/Mods/{mod}/MovementAttributes";
    }
    private static string Mod(NativeSuitProject p) => Root(p).Split('/')[3];
    internal static string SetPackage(NativeSuitProject p) => Root(p) + "/AS_MovementAttributes";
    private static string SetClone(NativeSuitProject p, string source) => Root(p) + "/AS_Movement_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    private static string Local(NativeSuitProject p, string native) => Root(p) + "/" + UnrealPathUtil.AssetName(native);
    private static NormalExport Defaults(UAsset a) => a.Exports.OfType<NormalExport>()
        .Single(e => e.ObjectName.ToString().StartsWith("Default__", StringComparison.Ordinal));
    private static StructPropertyData Struct(IEnumerable<PropertyData> properties, string name) =>
        properties.OfType<StructPropertyData>().Single(p => p.Name.ToString() == name);
    private static ArrayPropertyData Modifiers(UAsset a) => Defaults(a).Data.OfType<ArrayPropertyData>().Single(p => p.Name.ToString() == "Modifiers");
    private static string Attribute(StructPropertyData modifier) =>
        Struct(modifier.Value, "Attribute").Value.OfType<StrPropertyData>().Single(p => p.Name.ToString() == "AttributeName").Value?.ToString() ?? "";
    private static StructPropertyData Magnitude(StructPropertyData modifier) => Struct(modifier.Value, "ModifierMagnitude");
    private static StructPropertyData Scalable(StructPropertyData modifier) => Struct(Magnitude(modifier).Value, "ScalableFloatMagnitude");
    private static FloatPropertyData Number(StructPropertyData scalable) => scalable.Value.OfType<FloatPropertyData>().Single(p => p.Name.ToString() == "Value");
    private static void Constant(UAsset a, StructPropertyData modifier, float value, string operation)
    {
        var op = modifier.Value.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "ModifierOp");
        op.Value = new FName(a, operation);
        var magnitude = Magnitude(modifier);
        magnitude.Value.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "MagnitudeCalculationType").Value = new FName(a, "ScalableFloat");
        var scalable = Scalable(modifier);
        Number(scalable).Value = value;
        // A scalable float multiplies its curve value. Absolute values must clear both curve and registry lookup.
        var curve = Struct(scalable.Value, "Curve");
        curve.Value.OfType<ObjectPropertyData>().Single().Value = FPackageIndex.FromRawIndex(0);
        curve.Value.OfType<NamePropertyData>().Single().Value = new FName(a, "None");
        Struct(scalable.Value, "RegistryType").Value.OfType<NamePropertyData>().Single().Value = new FName(a, "None");
    }
    private static void Scale(UAsset a, IReadOnlyDictionary<string, float> factors)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modifier in Modifiers(a).Value.Cast<StructPropertyData>())
        {
            var attribute = Attribute(modifier);
            if (!factors.TryGetValue(attribute, out var factor)) continue;
            var type = Magnitude(modifier).Value.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "MagnitudeCalculationType");
            if (type.Value.ToString() != "ScalableFloat") throw new InvalidDataException("Movement tuning requires a scalable native modifier: " + attribute);
            var number = Number(Scalable(modifier));
            if (!float.IsFinite(number.Value) || number.Value <= 0) throw new InvalidDataException("Invalid native movement coefficient: " + attribute);
            number.Value *= factor;
            found.Add(attribute);
        }
        if (!found.SetEquals(factors.Keys)) throw new InvalidDataException("Missing native movement modifier(s): " + string.Join(", ", factors.Keys.Except(found)));
    }
    private static Dictionary<string, float> GlideFactors(float factor) => new(StringComparer.Ordinal) {
        ["GlideMinForwardSpeed"] = factor, ["GlideDefaultForwardSpeed"] = factor, ["GlideMaxForwardSpeed"] = factor };
    private static Dictionary<string, string> Redirects(NativeSuitProject p)
    {
        var s = p.MovementAttributes!;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (s.SprintSpeed.HasValue) map[SprintAbility] = Local(p, SprintAbility);
        if (s.GlideSpeedMultiplier.HasValue) map[GlideDefaults] = Local(p, GlideDefaults);
        if (s.GlideDescentMultiplier.HasValue) map[GlideDescent] = Local(p, GlideDescent);
        return map;
    }
    private static void RequireSuccess(AbilityAssetMutationService.MutationResult r)
    { if (!r.Success) throw new InvalidDataException(r.Error ?? r.Status); }
    private static bool References(AbilityAssetMutationService.AbilitySetInspection set, string package) =>
        set.GameplayAbilities.Concat(set.GameplayEffects).Any(g => g.PackagePath == package);

    internal static void Generate(NativeSuitProject p, string extracted, string staged, string dprd, Usmap mappings, Action<string> log)
    {
        if (!Enabled(p.MovementAttributes)) return;
        if (ProjectError(p) is { } error) throw new InvalidDataException(error);
        var s = p.MovementAttributes!;
        var mutation = new AbilityAssetMutationService();
        var inspection = mutation.InspectDprdAbilitySets(dprd);
        if (!inspection.Success) throw new InvalidDataException(inspection.Error);
        var sources = inspection.AbilitySets.Select(r => r.PackagePath).ToList();
        using var context = new SwordCombatService.Context(extracted, staged, Mod(p), mappings, Root(p));
        var redirects = Redirects(p);
        var sets = sources.Select(source => (Source: source, Asset: context.Read(source))).ToList();
        var setInfo = sets.Select(set => (set.Source, Info: mutation.InspectAbilitySet(set.Asset.FilePath))).ToList();
        foreach (var (_, info) in setInfo) if (!info.Success) throw new InvalidDataException(info.Error);
        foreach (var native in redirects.Keys)
            if (!setInfo.Any(set => References(set.Info, native)))
                throw new InvalidDataException($"Movement attributes: this loadout does not grant {UnrealPathUtil.AssetName(native)}. Keep that native ability/effect enabled, or clear its movement override. Custom movement controllers need their own tuning.");

        if (s.SprintSpeed is { } sprint)
        {
            var effect = context.Clone(SprintEffect, Local(p, SprintEffect));
            var modifier = Modifiers(effect).Value.Cast<StructPropertyData>().Single(m => Attribute(m) == "MaxWalkSpeed");
            Constant(effect, modifier, sprint, "Override"); context.Write(effect, Local(p, SprintEffect));
            context.Clone(SprintAbility, Local(p, SprintAbility), new() { [SprintEffect] = Local(p, SprintEffect) });
        }
        if (s.GlideSpeedMultiplier is { } glide)
        {
            var effect = context.Clone(GlideDefaults, Local(p, GlideDefaults));
            Scale(effect, GlideFactors(glide)); context.Write(effect, Local(p, GlideDefaults));
        }
        if (s.GlideDescentMultiplier is { } descent)
        {
            var effect = context.Clone(GlideDescent, Local(p, GlideDescent));
            Scale(effect, new Dictionary<string, float> { ["GlideDescentRateScale"] = descent });
            context.Write(effect, Local(p, GlideDescent));
        }
        var finalSets = new List<string>();
        foreach (var (source, info) in setInfo)
        {
            if (!redirects.Keys.Any(native => References(info, native))) { finalSets.Add(source); continue; }
            var target = SetClone(p, source);
            context.Clone(source, target, redirects);
            finalSets.Add(target);
        }
        if (s.RunSpeedMultiplier.HasValue || s.JumpHeightMultiplier.HasValue)
        {
            var generatedEffects = new List<string>();
            if (s.RunSpeedMultiplier is { } run)
            {
                var package = Root(p) + "/GE_RunSpeed";
                var effect = context.Clone(RunTemplate, package);
                var modifier = Modifiers(effect).Value.Cast<StructPropertyData>().Single();
                if (Attribute(modifier) != "MaxWalkSpeed") throw new InvalidDataException("The run-speed template no longer targets ground speed.");
                Constant(effect, modifier, run, "MultiplyCompound");
                // The source is an AI difficulty effect; remove its applicability restrictions on this private clone.
                Defaults(effect).Data.RemoveAll(property => property.Name.ToString() == "ApplicationTagRequirements");
                if (Defaults(effect).Data.OfType<ArrayPropertyData>().FirstOrDefault(property => property.Name.ToString() == "GEComponents") is { } components)
                    components.Value = [];
                context.Write(effect, package); generatedEffects.Add(package);
            }
            if (s.JumpHeightMultiplier is { } height)
            {
                var package = Root(p) + "/GE_JumpHeight";
                var effect = context.Clone(JumpTemplate, package);
                var modifier = Modifiers(effect).Value.Cast<StructPropertyData>().Single(m => Attribute(m) == "JumpZVelocity");
                // Balance jumping also changes falling braking. A height edit must not inherit that behavior.
                Modifiers(effect).Value = [modifier];
                modifier.Name = new FName(effect, "0");
                Constant(effect, modifier, MathF.Sqrt(height), "MultiplyCompound");
                context.Write(effect, package); generatedEffects.Add(package);
            }
            var set = context.Clone(SetTemplate, SetPackage(p));
            var setExport = set.Exports.OfType<NormalExport>().Single();
            var grants = setExport.Data.OfType<ArrayPropertyData>().Single(property => property.Name.ToString() == "GrantedGameplayEffects");
            grants.Value = [];
            setExport.Data.Clear(); setExport.Data.Add(grants);
            context.Write(set, SetPackage(p));
            RequireSuccess(mutation.AddGameplayEffects(context.PathFor(SetPackage(p)), generatedEffects.Select(package => new AbilityAssetMutationService.GameplayEffectAddition {
                PackagePath = package, EffectLevelOverride = 1,
                SourceAbilitySetUassetPath = ExtractedPackagePathService.ResolvePackageUasset(extracted, SetTemplate) ?? "",
                SourceEffectPackagePath = GlideDefaults }).ToArray()));
            finalSets.Add(SetPackage(p));
        }
        RequireSuccess(mutation.SetDprdAbilitySets(dprd, finalSets));
        log("Movement attributes: " + (sources.Count - sources.Intersect(finalSets).Count()) + " local ability-set clone(s); " + Summary(s));
    }

    internal static List<string> ExpectedSets(NativeSuitProject p, string extracted, string staged, Usmap mappings, IReadOnlyList<string> sources)
    {
        if (!Enabled(p.MovementAttributes)) return sources.ToList();
        var redirects = Redirects(p);
        var result = new List<string>();
        var mutation = new AbilityAssetMutationService();
        using var context = new SwordCombatService.Context(extracted, staged, Mod(p), mappings, Root(p));
        foreach (var source in sources)
        {
            var asset = context.Read(source);
            var info = mutation.InspectAbilitySet(asset.FilePath);
            if (!info.Success) throw new InvalidDataException(info.Error);
            var target = redirects.Keys.Any(native => References(info, native)) ? SetClone(p, source) : source;
            var actualTarget = p.ReleaseAssetAliases.GetValueOrDefault(target, target);
            if (target != source)
            {
                var actual = context.ReadStagedShared(actualTarget);
                var actualInfo = mutation.InspectAbilitySet(actual.FilePath);
                if (!actualInfo.Success || GrantSignature(info, redirects, p.ReleaseAssetAliases) != GrantSignature(actualInfo, new(), p.ReleaseAssetAliases))
                    throw new InvalidDataException("Movement tuning changed unrelated grants or metadata in " + source);
            }
            result.Add(actualTarget);
        }
        if (p.MovementAttributes is { RunSpeedMultiplier: not null } or { JumpHeightMultiplier: not null })
        {
            var target = p.ReleaseAssetAliases.GetValueOrDefault(SetPackage(p), SetPackage(p));
            var asset = context.ReadStagedShared(target);
            var info = mutation.InspectAbilitySet(asset.FilePath);
            var expectedEffects = new List<string>();
            if (p.MovementAttributes!.RunSpeedMultiplier.HasValue) expectedEffects.Add(Root(p) + "/GE_RunSpeed");
            if (p.MovementAttributes.JumpHeightMultiplier.HasValue) expectedEffects.Add(Root(p) + "/GE_JumpHeight");
            if (!info.Success || info.GameplayAbilities.Count + info.Attributes.Count + info.GameplayData.Count +
                info.ActorGameplayCues.Count + info.StaticGameplayCues.Count != 0 || info.AccessoryAnimGraphClass is not null ||
                !info.GameplayEffects.Select(effect => effect.PackagePath).SequenceEqual(expectedEffects.Select(package => p.ReleaseAssetAliases.GetValueOrDefault(package, package))) ||
                info.GameplayEffects.Any(effect => effect.EffectLevel != 1))
                throw new InvalidDataException("Movement AbilitySet contains missing, duplicated or unrelated grants.");
            result.Add(target);
        }
        return result;
    }

    private static string GrantSignature(AbilityAssetMutationService.AbilitySetInspection info,
        Dictionary<string, string> redirects, IReadOnlyDictionary<string, string> aliases)
    {
        string[] Rows(IEnumerable<AbilityAssetMutationService.AbilityGrantReference> grants) => grants.Select(grant =>
        {
            var package = redirects.GetValueOrDefault(grant.PackagePath, grant.PackagePath);
            package = aliases.GetValueOrDefault(package, package);
            return $"{package}|{grant.AbilityLevel}|{grant.EffectLevel}|{grant.InputTag}|{grant.IsNativeClass}";
        }).ToArray();
        return System.Text.Json.JsonSerializer.Serialize(new { Abilities = Rows(info.GameplayAbilities), Effects = Rows(info.GameplayEffects),
            Attributes = Rows(info.Attributes), Data = Rows(info.GameplayData), ActorCues = Rows(info.ActorGameplayCues), StaticCues = Rows(info.StaticGameplayCues),
            Accessory = info.AccessoryAnimGraphClass is { } graph ? Rows([graph]) : [] });
    }

    internal static void Verify(NativeSuitProject p, string extracted, string staged, Usmap mappings)
    {
        if (!Enabled(p.MovementAttributes)) return;
        if (ValidationError(p.MovementAttributes) is { } error) throw new InvalidDataException(error);
        using var context = new SwordCombatService.Context(extracted, staged, Mod(p), mappings, Root(p));
        UAsset Read(string package) => context.ReadStagedShared(p.ReleaseAssetAliases.GetValueOrDefault(package, package));
        void CheckConstant(string package, string attribute, float value, string operation)
        {
            var asset = Read(package); var modifier = Modifiers(asset).Value.Cast<StructPropertyData>().Single(m => Attribute(m) == attribute);
            var magnitude = Magnitude(modifier); var scalar = Scalable(modifier);
            if (Attribute(modifier) != attribute || !float.IsFinite(Number(scalar).Value) || Math.Abs(Number(scalar).Value - value) > .0001f ||
                magnitude.Value.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "MagnitudeCalculationType").Value.ToString() != "ScalableFloat" ||
                modifier.Value.OfType<EnumPropertyData>().Single(p => p.Name.ToString() == "ModifierOp").Value.ToString() != operation ||
                !Struct(scalar.Value, "Curve").Value.OfType<ObjectPropertyData>().Single().Value.IsNull() ||
                Struct(scalar.Value, "RegistryType").Value.OfType<NamePropertyData>().Single().Value.ToString() != "None")
                throw new InvalidDataException("Movement override did not rebuild: " + attribute);
        }
        void CheckScale(string native, Dictionary<string, float> factors)
        {
            var source = context.Read(native); var actual = Read(Local(p, native));
            var original = Modifiers(source).Value.Cast<StructPropertyData>().ToDictionary(Attribute);
            var edited = Modifiers(actual).Value.Cast<StructPropertyData>().ToDictionary(Attribute);
            if (!original.Keys.ToHashSet().SetEquals(edited.Keys)) throw new InvalidDataException("Glide modifier membership changed.");
            foreach (var (attribute, modifier) in original)
                if (!float.IsFinite(Number(Scalable(edited[attribute])).Value) || Math.Abs(Number(Scalable(edited[attribute])).Value - Number(Scalable(modifier)).Value * factors.GetValueOrDefault(attribute, 1)) > .0001f)
                    throw new InvalidDataException("Glide override did not rebuild: " + attribute);
        }
        var s = p.MovementAttributes!;
        if (s.RunSpeedMultiplier is { } run)
        {
            var package = Root(p) + "/GE_RunSpeed";
            CheckConstant(package, "MaxWalkSpeed", run, "MultiplyCompound");
            var effect = Read(package);
            if (Modifiers(effect).Value.Length != 1 ||
                Defaults(effect).Data.Any(p => p.Name.ToString() == "ApplicationTagRequirements") ||
                Defaults(effect).Data.OfType<ArrayPropertyData>().Any(p => p.Name.ToString() == "GEComponents" && p.Value.Length != 0))
                throw new InvalidDataException("The run-speed effect retained unrelated AI conditions or modifiers.");
        }
        if (s.JumpHeightMultiplier is { } height)
        {
            var package = Root(p) + "/GE_JumpHeight";
            CheckConstant(package, "JumpZVelocity", MathF.Sqrt(height), "MultiplyCompound");
            if (Modifiers(Read(package)).Value.Length != 1) throw new InvalidDataException("The jump-height effect changed unrelated falling attributes.");
        }
        if (s.SprintSpeed is { } sprint)
        {
            CheckConstant(Local(p, SprintEffect), "MaxWalkSpeed", sprint, "Override");
            var ability = Read(Local(p, SprintAbility));
            var effect = Defaults(ability).Data.OfType<ObjectPropertyData>().Single(p => p.Name.ToString() == "SprintMaxSpeedGameplayEffect");
            var expected = Local(p, SprintEffect);
            if (SwordCombatService.Package(ability, effect.Value) != p.ReleaseAssetAliases.GetValueOrDefault(expected, expected))
                throw new InvalidDataException("The sprint controller is not wired to its local speed effect.");
        }
        if (s.GlideSpeedMultiplier is { } glide) CheckScale(GlideDefaults, GlideFactors(glide));
        if (s.GlideDescentMultiplier is { } descent) CheckScale(GlideDescent, new() { ["GlideDescentRateScale"] = descent });
    }
    internal static string Summary(CharacterMovementSettings? s) => !Enabled(s) ? "Gameplay donor defaults" : string.Join(" · ", new[] {
        s!.RunSpeedMultiplier is { } run ? $"run {run * 100:0}%" : null,
        s.SprintSpeed is { } sprint ? $"sprint {sprint:0} cm/s" : null,
        s.JumpHeightMultiplier is { } jump ? $"jump {jump * 100:0}%" : null,
        s.GlideSpeedMultiplier is { } glide ? $"glide {glide * 100:0}%" : null,
        s.GlideDescentMultiplier is { } descent ? $"descent {descent * 100:0}%" : null }.OfType<string>());
}
