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
        Record(owner, candidate, sections.Where(s => !takeCharacter.Contains(s.Id)).Select(s => s.Id));
        return candidate;
    }
    internal static NativeSuitProject Prepare(NativeSuitProject owner, NativeSuitProject suit, IReadOnlySet<string> takeCharacter, SuitProjectService service, Action<string> log)
    {
        RequireParent(owner, suit);
        if (takeCharacter.Any(id => !Sections().Any(s => s.Id == id))) throw new InvalidDataException("Unknown inheritance section.");
        if (takeCharacter.Count == 0) return Compose(owner, suit, suit, takeCharacter);
        // Native base and library-animation references carry no editable visual sources. A
        // movement-only refresh must not recook textures or require an unrelated missing OBJ.
        if (takeCharacter.All(id => id is "base" or "animations")) return Compose(owner, suit, owner, takeCharacter);
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
