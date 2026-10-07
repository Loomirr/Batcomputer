using System.Text.Json;
using System.Text.Json.Nodes;

namespace Batcomputer;

/// <summary>Textures stay owned by the included character definition, not its cosmetic variants.</summary>
internal static class CharacterTextureReferenceService
{
    internal static bool CanReuse(NativeSuitProject source, NativeSuitProject target) =>
        source.CustomCharacter is { IsDefinition: true } owner &&
        target.CustomCharacter is { IsDefinition: false } child &&
        child.DefinitionSlotId == source.SlotId && owner.CharacterId == child.CharacterId &&
        CustomCharacterProjectService.PawnOwner(owner) == CustomCharacterProjectService.PawnOwner(child);

    internal static IReadOnlySet<string> SharedPackages(NativeSuitProject source, NativeSuitProject target) =>
        CanReuse(source, target) ? source.GeneratedTextures.Select(t => t.PackagePath)
            .Where(ExtractedPackagePathService.IsContentPackagePath).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

    internal static NativeSuitProject Preserve(NativeSuitProject source, NativeSuitProject target, string oldRoot, string newRoot)
    {
        if (!CanReuse(source, target)) return target;
        var aliases = SharedPackages(source, target).Where(p => p.StartsWith(oldRoot + "/", StringComparison.Ordinal))
            .ToDictionary(p => newRoot + p[oldRoot.Length..], p => p, StringComparer.Ordinal);
        var node = JsonSerializer.SerializeToNode(target)!;
        Rewrite(node, aliases);
        var result = node.Deserialize<NativeSuitProject>()!;
        // No second editable recipe/cache: changing a suit requires importing a new local texture.
        result.GeneratedTextures.Clear();
        return result;
    }

    internal static IReadOnlyList<GeneratedTextureEntry> BaseTextures(NativeSuitProject? suit, SuitProjectService service)
    {
        if (suit?.CustomCharacter is not { IsDefinition: false } child) return [];
        var owner = service.LoadProject(service.ProjectPathForSlot(child.DefinitionSlotId));
        if (owner is null) return [];
        CharacterRebaseService.RequireParent(owner, suit);
        // Browsers receive detached metadata and must never edit the owning character's recipe.
        return JsonSerializer.Deserialize<List<GeneratedTextureEntry>>(JsonSerializer.Serialize(owner.GeneratedTextures))!;
    }

    private static void Rewrite(JsonNode node, IReadOnlyDictionary<string, string> aliases)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj.ToArray())
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[key] = CharacterAssetReuseService.Redirect(text, aliases);
                else if (value is not null) Rewrite(value, aliases);
        if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[i] = CharacterAssetReuseService.Redirect(text, aliases);
                else if (array[i] is { } value) Rewrite(value, aliases);
    }
}
