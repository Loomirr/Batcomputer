using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Batcomputer;

/// <summary>Explicit character-to-suit refresh. Does not import installed PAK overlays or mutate the parent.</summary>
internal static class CharacterRebaseService
{
    internal sealed record Section(string Id, string Label, string[] Fields);
    internal sealed record Row(Section Section, bool HasBaseline, bool SuitEdited, bool CharacterEdited)
    {
        internal bool TakeCharacterByDefault => HasBaseline && !SuitEdited;
    }
    private static readonly string[] IdentityFields =
    ["CustomCharacter", "SchemaVersion", "ToolVersion", "SlotId", "DisplayName", "Description", "PawnTag",
     "LockedDescription", "ProgressTag", "CoverImagePath", "PackageBaseName", "TargetPackages", "Changes"];

    internal static IReadOnlyList<Section> Sections()
    {
        var sections = new List<Section>
        {
            new("base", "Base, body and requirements", ["PlayableTemplate", "CutsceneTemplate", "DcmdTemplate", "VisualSourceTemplate", "VisualCutsceneSourceTemplate", "StaticMeshComponentShapeTemplate", "BaseProfile", "BodyProfile", "Requirements", "MachineryDonorPlayable"]),
            new("abilities", "Abilities, fighting style and held items", ["AbilityLoadout"]),
            new("equipment", "Equipment", ["EquipmentSlots"]),
            new("voice", "Voice profile", ["VoiceProfileId"]),
            new("animations", "Animations", ["UseCustomArchetype", "AnimationOverrides", "LocomotionOverrides", "AnimationSlotOverrides", "GameplayAnimationGraphs", "FaceAnimationBlueprintPackage"]),
            new("appearance", "Parts, models, materials and textures", ["MaterialAssignments", "PartGrafts", "CustomStaticMeshes", "SkinnedMeshes", "PreviewPartPlacements", "GeneratedTextures", "GeneratedMaterials"]),
            new("gliding", "Cape / glider setup", ["GliderType", "GliderMaterial", "GliderGrafted", "GliderAutoEnabledCustomArchetype", "GliderAnimLas", "GliderAnimMas", "PairedCapeAdapter"]),
            new("icons", "Suit icons", ["IconMenu", "IconSuit", "IconLeft", "IconRight"])
        };
        var known = IdentityFields.Concat(sections.SelectMany(s => s.Fields)).ToHashSet(StringComparer.Ordinal);
        var remaining = Node(new NativeSuitProject()).Select(p => p.Key).Where(k => !known.Contains(k)).ToArray();
        if (remaining.Length > 0) sections.Add(new("other", "Other saved character settings", remaining));
        return sections;
    }
    private static JsonObject Node(NativeSuitProject project) => JsonSerializer.SerializeToNode(project)!.AsObject();
    private static string Fingerprint(JsonObject node, Section section)
    {
        var fields = new JsonObject();
        foreach (var field in section.Fields) fields[field] = node[field]?.DeepClone();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fields.ToJsonString())));
    }
    internal static void RequireParent(NativeSuitProject owner, NativeSuitProject suit)
    {
        if (owner.CustomCharacter is not { IsDefinition: true } parent || suit.CustomCharacter is not { IsDefinition: false } child ||
            child.DefinitionSlotId != owner.SlotId || parent.DefinitionSlotId != owner.SlotId || parent.CharacterId != child.CharacterId ||
            CustomCharacterProjectService.PawnOwner(parent) != CustomCharacterProjectService.PawnOwner(child) || owner.SlotId == suit.SlotId)
            throw new InvalidDataException("This suit must reference its saved character definition. Rebase cannot change its character or roster identity.");
        if (child.Inheritance is { } state && (state.Version != 1 || state.DefinitionSlotId != owner.SlotId))
            throw new InvalidDataException("This suit's inheritance history is unsupported or belongs to another character.");
    }
    internal static IReadOnlyList<Row> Preview(NativeSuitProject owner, NativeSuitProject suit)
    {
        RequireParent(owner, suit);
        var inherited = suit.CustomCharacter!.Inheritance; var parentNode = Node(owner); var suitNode = Node(suit);
        return Sections().Select(s =>
        {
            string? previousSuit = null, previousParent = null;
            var known = inherited is not null && inherited.SuitFingerprints.TryGetValue(s.Id, out previousSuit) && inherited.CharacterFingerprints.TryGetValue(s.Id, out previousParent);
            return new Row(s, known, !known || inherited!.KeptSections.Contains(s.Id) || Fingerprint(suitNode, s) != previousSuit,
                !known || Fingerprint(parentNode, s) != previousParent);
        }).ToArray();
    }
    internal static void CaptureInitial(NativeSuitProject owner, NativeSuitProject suit)
    {
        RequireParent(owner, suit);
        Record(owner, suit, []);
    }
    internal static string? GameplayUpdateWarning(NativeSuitProject owner, NativeSuitProject suit)
    {
        // Suit recipes are independent snapshots. Never silently overwrite deliberate
        // overrides, but do not let a later parent fix go unnoticed at release time.
        var changed = Preview(owner, suit).Where(row => row.HasBaseline && row.CharacterEdited &&
            row.Section.Id is "base" or "abilities" or "equipment" or "voice" or "animations" or "gliding")
            .Select(row => row.Section.Label).ToArray();
        return changed.Length == 0 ? null :
            $"The saved character '{owner.DisplayName}' has newer settings than this suit's last inheritance review: {string.Join(", ", changed)}. " +
            "Open Rebase from character and review these sections before building. Keep Parts, models, materials and textures and Suit icons to preserve this suit's appearance. Local overrides will not be replaced automatically.";
    }
    private static void Record(NativeSuitProject owner, NativeSuitProject suit, IEnumerable<string> kept)
    {
        var parent = Node(owner); var child = Node(suit);
        suit.CustomCharacter!.Inheritance = new()
        {
            DefinitionSlotId = owner.SlotId,
            CharacterFingerprints = Sections().ToDictionary(s => s.Id, s => Fingerprint(parent, s)),
            SuitFingerprints = Sections().ToDictionary(s => s.Id, s => Fingerprint(child, s)),
            KeptSections = kept.ToList()
        };
    }
    internal static NativeSuitProject Compose(NativeSuitProject owner, NativeSuitProject suit, NativeSuitProject copiedOwner, IReadOnlySet<string> takeCharacter)
    {
        RequireParent(owner, suit);
        var sections = Sections();
        if (takeCharacter.Any(id => !sections.Any(s => s.Id == id))) throw new InvalidDataException("Unknown inheritance section.");
        var result = Node(suit); var incoming = Node(copiedOwner);
        foreach (var section in sections.Where(s => takeCharacter.Contains(s.Id)))
            foreach (var field in section.Fields) result[field] = incoming[field]?.DeepClone();
        var candidate = result.Deserialize<NativeSuitProject>()!;
        if (takeCharacter.Contains("appearance"))
        {
            // A visual reset changes assignments, not the user's imported-texture library.
            // Parent textures are shared, so the copied owner can legitimately have no
            // editable recipes. Replacing the list would orphan retained icons and imports.
            var imports = JsonSerializer.Deserialize<List<GeneratedTextureEntry>>(
                JsonSerializer.Serialize(suit.GeneratedTextures))!;
            var packages = imports.Select(t => t.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            imports.AddRange(candidate.GeneratedTextures.Where(t => packages.Add(t.PackagePath)));
            candidate.GeneratedTextures = imports;
        }
        if (!takeCharacter.Contains("appearance") && takeCharacter.Contains("abilities"))
            PreserveAbilityVisuals(suit, candidate);
        Record(owner, candidate, sections.Where(s => !takeCharacter.Contains(s.Id)).Select(s => s.Id));
        return candidate;
    }

    // Appearance lives inside several gameplay profiles too. Refresh their behavior without
    // replacing a receiving suit's existing mesh, material, placement or cosmetic effects.
    private static void PreserveAbilityVisuals(NativeSuitProject suit, NativeSuitProject candidate)
    {
        if (suit.AbilityLoadout is not { } old || candidate.AbilityLoadout is not { } updated) return;
        foreach (var item in updated.HeldItems ?? [])
        {
            var matches = (old.HeldItems ?? []).Where(i => i.Id == item.Id).ToArray();
            if (matches.Length != 1) continue; // Never guess from display names or list positions.
            var visual = matches[0];
            item.MeshPackage = visual.MeshPackage;
            item.MaterialPackage = visual.MaterialPackage;
            item.CustomModel = visual.CustomModel?.Clone();
            item.Effects = visual.Effects.Select(e => e.Clone()).ToList();
        }
        if (old.SwordCombat is { } sword && updated.SwordCombat is { } target && old.FightingStyleId == updated.FightingStyleId)
        {
            target.MeshPackage = sword.MeshPackage;
            target.MaterialPackage = sword.MaterialPackage;
            target.CustomModel = sword.CustomModel?.Clone();
        }
        // These are declarative visual overrides, not gameplay grants.
        updated.NativeHeldItems = JsonSerializer.Deserialize<List<NativeHeldItemEdit>>(JsonSerializer.Serialize(old.NativeHeldItems))!;
        updated.AnimationSpawnedItems = JsonSerializer.Deserialize<List<AnimationSpawnedItemEdit>>(JsonSerializer.Serialize(old.AnimationSpawnedItems))!;
    }

    internal static bool IsGameplayRefreshSection(string id) => id is "abilities" or "animations" or "voice";
    internal static NativeSuitProject Prepare(NativeSuitProject owner, NativeSuitProject suit, IReadOnlySet<string> takeCharacter, SuitProjectService service, Action<string> log)
    {
        RequireParent(owner, suit);
        if (takeCharacter.Any(id => !Sections().Any(s => s.Id == id))) throw new InvalidDataException("Unknown inheritance section.");
        if (takeCharacter.Count == 0) return Compose(owner, suit, suit, takeCharacter);
        // Native base and library-animation references carry no editable visual sources. A
        // movement-only refresh must not recook textures or require an unrelated missing OBJ.
        if (takeCharacter.All(id => id is "base" or "animations"))
        {
            var identity = suit.CustomCharacter!;
            var animationCopy = CustomCharacterProjectService.CreateRecipe(owner, suit.DisplayName, identity.CharacterId, identity.VariantId, owner.SlotId);
            animationCopy.TargetPackages = JsonSerializer.Deserialize<TargetPackages>(JsonSerializer.Serialize(suit.TargetPackages))!;
            CustomCharacterProjectService.PreserveAnimationReferences(owner, animationCopy);
            return Compose(owner, suit, animationCopy, takeCharacter);
        }
        var child = suit.CustomCharacter!;
        var copied = CustomCharacterProjectService.CreateRecipe(owner, suit.DisplayName, child.CharacterId, child.VariantId, owner.SlotId);
        // Repeated rebases must never overwrite a suit's editable meshes/materials. Every refresh
        // uses a fresh asset namespace and source folder, retained for recovery on failure.
        copied.SlotId = suit.SlotId;
        var revision = Guid.NewGuid().ToString("N");
        var root = copied.TargetPackages.Playable[..copied.TargetPackages.Playable.IndexOf("/Characters/", StringComparison.Ordinal)];
        var assetRoot = root + "/CharacterRebases/" + revision;
        var node = Node(copied);
        RewriteStrings(node, root + "/", assetRoot + "/");
        copied = node.Deserialize<NativeSuitProject>()!;
        CustomCharacterProjectService.PreserveAnimationReferences(owner, copied);
        copied.TargetPackages = JsonSerializer.Deserialize<TargetPackages>(JsonSerializer.Serialize(suit.TargetPackages))!;
        var prefix = "CharacterRebases/" + revision;
        CustomCharacterProjectService.CopyAuthoringSources(owner, copied, service, prefix, assetRoot);
        CustomCharacterProjectService.CopyVisualAssets(owner, copied, service, log, assetRoot, prefix);
        return Compose(owner, suit, copied, takeCharacter);
    }
    private static void RewriteStrings(JsonNode? node, string oldPrefix, string newPrefix)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj.ToArray())
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[key] = text.Replace(oldPrefix, newPrefix, StringComparison.Ordinal);
                else RewriteStrings(value, oldPrefix, newPrefix);
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[i] = text.Replace(oldPrefix, newPrefix, StringComparison.Ordinal);
                else RewriteStrings(array[i], oldPrefix, newPrefix);
    }
}
