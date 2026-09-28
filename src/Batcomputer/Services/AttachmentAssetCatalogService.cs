namespace Batcomputer;

/// <summary>
/// Catalog of character mesh assets and their observed Blueprint component usages. Browsing
/// can show each usage separately without changing the saved graft or build path.
/// </summary>
internal static class AttachmentAssetCatalogService
{
    private static readonly object CacheGate = new();
    private static NativeSuitPartIndex? _cachedIndex;
    private static string _cachedContentRoot = "";
    private static IReadOnlyList<Entry>? _cachedEntries;

    internal sealed record Entry(
        string MeshPackagePath,
        string MeshClass,
        IReadOnlyList<NativeSuitPartRecord> NativeRecipes,
        IReadOnlyList<NativeSuitPartRecord> InferredRecipes)
    {
        internal string Name => AttachmentCatalogService.AssetName(MeshPackagePath);
        internal string Status => NativeRecipes.Count > 0 ? "native recipe" :
            InferredRecipes.Count > 0 ? "inferred" : "preview only";
        internal IReadOnlyList<NativeSuitPartRecord> Usages => NativeRecipes.Count > 0 ? NativeRecipes : InferredRecipes;
        internal bool UsesFaceWorkflow => AttachmentAssetCatalogService.UsesFaceWorkflow(MeshPackagePath);
        internal IReadOnlyList<string> AttachmentPoints =>
            (NativeRecipes.Count > 0 ? NativeRecipes : InferredRecipes)
                .Select(recipe => recipe.Slot)
                .Where(slot => !string.IsNullOrWhiteSpace(slot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase)
                .DefaultIfEmpty("Unverified")
                .ToArray();
    }

    // Very common shared meshes (especially face meshes) can appear on hundreds of BPs.
    // Keep their distinct mount points visible, but put repeated donor usages in the detail view.
    internal const int FrequentUsageThreshold = 20;

    internal sealed record BrowseItem(Entry Asset, NativeSuitPartRecord? Recipe,
        IReadOnlyList<NativeSuitPartRecord>? GroupedRecipes = null)
    {
        internal IReadOnlyList<NativeSuitPartRecord> Recipes => GroupedRecipes ??
            (Recipe is null ? [] : [Recipe]);
        internal IReadOnlyList<string> AttachmentPoints => GroupedRecipes is { Count: > 0 }
            ? [Point(GroupedRecipes[0])]
            : Recipe is null ? Asset.AttachmentPoints : [Point(Recipe)];
    }

    private static string Point(NativeSuitPartRecord recipe) =>
        string.IsNullOrWhiteSpace(recipe.Slot) ? "Unverified" : recipe.Slot;

    internal static IReadOnlyList<BrowseItem> BrowseItems(IReadOnlyList<Entry> entries) =>
        entries.SelectMany(entry =>
        {
            if (entry.NativeRecipes.Count == 0) return new[] { new BrowseItem(entry, null) };
            if (entry.NativeRecipes.Count < FrequentUsageThreshold)
                return entry.NativeRecipes.Select(recipe => new BrowseItem(entry, recipe)).ToArray();
            return entry.NativeRecipes.GroupBy(Point, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Count() == 1
                    ? new BrowseItem(entry, group.First())
                    : new BrowseItem(entry, null, group.ToArray()))
                .ToArray();
        }).ToArray();

    internal static bool UsesFaceWorkflow(string path) =>
        path.Contains("/Attachments/FaceTex/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/Attachments/LEGOface/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/Attachments/Face/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Character Blueprint mesh components that belong in Parts, including meshes
    /// stored outside the Attachments folders. Root bodies and printed-face components keep
    /// their existing dedicated workflows.</summary>
    internal static bool IsCatalogPartUsage(NativeSuitPartRecord part)
    {
        var package = !string.IsNullOrWhiteSpace(part.MeshPackagePath) ? part.MeshPackagePath :
            part.MeshObjectPath.Split('.')[0];
        if (!part.HasMesh || string.IsNullOrWhiteSpace(package) ||
            package.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase) ||
            UsesFaceWorkflow(package)) return false;
        var slot = part.Slot;
        return !slot.StartsWith("Face", StringComparison.OrdinalIgnoreCase) &&
            !slot.Equals("Mesh", StringComparison.OrdinalIgnoreCase) &&
            !slot.Contains("CharacterMesh0", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryGetCached(NativeSuitPartIndex index, out IReadOnlyList<Entry> entries)
    {
        var root = AppSettings.Current.EffectiveExtractedContentRoot();
        lock (CacheGate)
        {
            if (ReferenceEquals(index, _cachedIndex) &&
                root.Equals(_cachedContentRoot, StringComparison.OrdinalIgnoreCase) &&
                _cachedEntries is not null)
            {
                entries = _cachedEntries;
                return true;
            }
        }
        entries = [];
        return false;
    }

    internal static IReadOnlyList<Entry> ForActiveGame(NativeSuitPartIndex? index)
    {
        if (index is not null && TryGetCached(index, out var cached)) return cached;
        var gd = GameDataService.Instance;
        var assets = gd.AssetsOfClass("StaticMesh").Concat(gd.AssetsOfClass("SkeletalMesh"));
        var inferred = AttachmentCatalogService.HairParts().Concat(AttachmentCatalogService.HatParts());
        var entries = Build(assets, index?.Parts ?? [], inferred);
        if (index is not null)
            lock (CacheGate)
            {
                _cachedIndex = index;
                _cachedContentRoot = AppSettings.Current.EffectiveExtractedContentRoot();
                _cachedEntries = entries;
            }
        return entries;
    }

    internal static IReadOnlyList<Entry> Build(
        IEnumerable<GameDataAsset> assets,
        IEnumerable<NativeSuitPartRecord> observed,
        IEnumerable<NativeSuitPartRecord> inferred)
    {
        static bool Attachment(string path) =>
            path.Contains("/Characters/Attachments/", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/Attachments/Face/", StringComparison.OrdinalIgnoreCase);
        static string Package(NativeSuitPartRecord part) =>
            !string.IsNullOrWhiteSpace(part.MeshPackagePath) ? part.MeshPackagePath :
            part.MeshObjectPath.Split('.')[0];

        var assetsByPath = assets.Where(a => Attachment(a.Path) &&
                a.Class is "StaticMesh" or "SkeletalMesh")
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Class, StringComparer.OrdinalIgnoreCase);
        var nativeByPath = observed.Where(p => p.HasMesh &&
                !string.IsNullOrWhiteSpace(Package(p)) &&
                !Package(p).StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Package, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NativeSuitPartRecord>)g
                .OrderBy(p => p.Context, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Slot, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var inferredByPath = inferred.Where(p => p.HasMesh && Attachment(Package(p)))
            .GroupBy(Package, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NativeSuitPartRecord>)g.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        // An observed mesh absent from the shipped metadata is still a real catalog asset.
        foreach (var (path, recipes) in nativeByPath)
            assetsByPath.TryAdd(path, recipes[0].MeshKind.Contains("Static", StringComparison.OrdinalIgnoreCase)
                ? "StaticMesh" : "SkeletalMesh");

        return assetsByPath.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new Entry(p.Key, p.Value,
                nativeByPath.GetValueOrDefault(p.Key) ?? [],
                inferredByPath.GetValueOrDefault(p.Key) ?? []))
            .ToArray();
    }

    internal static bool CanOneClickGraft(Entry entry, NativeSuitPartRecord recipe)
    {
        if (!entry.NativeRecipes.Contains(recipe)) return false;
        if (!IsCatalogPartUsage(recipe)) return false;
        if (recipe.Context is not ("playable" or "cutscene")) return false;
        // Capes and gliders need their paired workflow; a Satchel mounted as Cape is not a
        // valid satchel recipe. Keep these usages visible, but never silently choose them.
        if (recipe.Slot.StartsWith("Cape", StringComparison.OrdinalIgnoreCase) ||
            recipe.Slot.StartsWith("Face", StringComparison.OrdinalIgnoreCase) ||
            entry.MeshPackagePath.Contains("/Attachments/LEGOface/", StringComparison.OrdinalIgnoreCase) ||
            entry.MeshPackagePath.Contains("/Attachments/Weapon/", StringComparison.OrdinalIgnoreCase) ||
            entry.MeshPackagePath.Contains("/Attachments/Umbrella/", StringComparison.OrdinalIgnoreCase) ||
            entry.MeshPackagePath.Contains("/Attachments/Cape/", StringComparison.OrdinalIgnoreCase) ||
            entry.MeshPackagePath.Contains("/Attachments/Glider/", StringComparison.OrdinalIgnoreCase) ||
            entry.MeshPackagePath.Contains("Glide", StringComparison.OrdinalIgnoreCase) ||
            entry.MeshPackagePath.Contains("Wingsuit", StringComparison.OrdinalIgnoreCase) ||
            (entry.Name.Contains("Satchel", StringComparison.OrdinalIgnoreCase) &&
             recipe.ComponentTags.Any(t => t.Contains("Cape", StringComparison.OrdinalIgnoreCase))))
            return false;
        return PartRecipeService.Confidence(recipe).Level == PartRecipeService.RecipeConfidence.Native;
    }

    internal static NativeSuitPartRecord? UniqueCompatibleCounterpart(Entry entry, NativeSuitPartRecord selected)
    {
        var oppositeRole = selected.Context.Equals("playable", StringComparison.OrdinalIgnoreCase)
            ? "cutscene" : "playable";
        var matches = entry.NativeRecipes.Where(candidate =>
            candidate.Context.Equals(oppositeRole, StringComparison.OrdinalIgnoreCase) &&
            candidate.Slot.Equals(selected.Slot, StringComparison.OrdinalIgnoreCase) &&
            candidate.ComponentClass.Equals(selected.ComponentClass, StringComparison.OrdinalIgnoreCase) &&
            candidate.MeshKind.Equals(selected.MeshKind, StringComparison.OrdinalIgnoreCase) &&
            CanOneClickGraft(entry, candidate)).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1) return null; // conflicting native usages require an explicit pick
        if (selected.MeshKind.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) &&
            PartRecipeService.SemanticKind(selected) is "Hair" or "Hat")
            return PartRecipeService.Clone(selected); // existing static-only hair/hat fallback
        return null;
    }
}
