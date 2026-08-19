using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Persists small manual placement corrections made in the 3D viewer. This is deliberately a
/// preview-only sidecar: it never changes a suit project, Blueprint, staged asset, or mod package.
/// </summary>
internal static class ViewerLayoutService
{
    private sealed class LayoutStore
    {
        public Dictionary<string, List<SavedPreviewPartPlacement>> Layouts { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string CharacterKey(string blueprintPath, string? bodyMeshPath = null)
    {
        var key = "character:" + Normalize(blueprintPath);
        return string.IsNullOrWhiteSpace(bodyMeshPath)
            ? key
            : key + "|body:" + Normalize(bodyMeshPath);
    }

    public static string SuitKey(NativeSuitProject project) =>
        "suit:" + Normalize(string.IsNullOrWhiteSpace(project.SlotId) ? project.DisplayName : project.SlotId);

    public static IReadOnlyList<SavedPreviewPartPlacement> Load(string projectRoot, string layoutKey)
    {
        if (string.IsNullOrWhiteSpace(layoutKey))
        {
            return Array.Empty<SavedPreviewPartPlacement>();
        }

        var store = Read(StorePath(projectRoot));
        return store.Layouts.TryGetValue(layoutKey, out var placements)
            ? Clone(placements)
            : Array.Empty<SavedPreviewPartPlacement>();
    }

    /// <summary>
    /// Imports the old suit-project placement field once, only when this viewer layout has no data.
    /// Future saves go exclusively to the preview sidecar.
    /// </summary>
    public static void ImportLegacyIfEmpty(
        string projectRoot,
        string layoutKey,
        IEnumerable<SavedPreviewPartPlacement>? legacyPlacements)
    {
        if (string.IsNullOrWhiteSpace(layoutKey) || legacyPlacements is null)
        {
            return;
        }

        var imported = NormalizePlacements(legacyPlacements).ToList();
        if (imported.Count == 0)
        {
            return;
        }

        var path = StorePath(projectRoot);
        var store = Read(path);
        if (store.Layouts.TryGetValue(layoutKey, out var current) && current.Count > 0)
        {
            return;
        }

        store.Layouts[layoutKey] = imported;
        Write(path, store);
    }

    public static bool Save(
        string projectRoot,
        string layoutKey,
        string component,
        float offsetX,
        float offsetY,
        float offsetZ,
        int? uvChannel = null)
    {
        if (string.IsNullOrWhiteSpace(layoutKey) || string.IsNullOrWhiteSpace(component) ||
            !float.IsFinite(offsetX) || !float.IsFinite(offsetY) || !float.IsFinite(offsetZ) ||
            (uvChannel is not null && (uvChannel < 0 || uvChannel > 7)))
        {
            return false;
        }

        var path = StorePath(projectRoot);
        var store = Read(path);
        if (!store.Layouts.TryGetValue(layoutKey, out var placements))
        {
            placements = new List<SavedPreviewPartPlacement>();
            store.Layouts[layoutKey] = placements;
        }

        component = component.Trim();
        placements.RemoveAll(placement => placement.Component.Equals(component, StringComparison.OrdinalIgnoreCase));
        var isZero = Math.Abs(offsetX) < 0.00001f &&
                     Math.Abs(offsetY) < 0.00001f &&
                     Math.Abs(offsetZ) < 0.00001f;
        if (!isZero || uvChannel is not null)
        {
            placements.Add(new SavedPreviewPartPlacement
            {
                Component = component,
                OffsetX = offsetX,
                OffsetY = offsetY,
                OffsetZ = offsetZ,
                UvChannel = uvChannel,
            });
        }

        if (placements.Count == 0)
        {
            store.Layouts.Remove(layoutKey);
        }
        Write(path, store);
        return true;
    }

    private static string StorePath(string projectRoot) =>
        Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "Preview", "viewer-layouts.json");

    private static LayoutStore Read(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<LayoutStore>(File.ReadAllText(path), Json) is { } store)
            {
                store.Layouts ??= new Dictionary<string, List<SavedPreviewPartPlacement>>();
                return store;
            }
        }
        catch
        {
            // A broken viewer sidecar must never make a character preview unusable.
        }
        return new LayoutStore();
    }

    private static void Write(string path, LayoutStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFileUtil.WriteAllText(path, JsonSerializer.Serialize(store, Json));
    }

    private static IReadOnlyList<SavedPreviewPartPlacement> Clone(IEnumerable<SavedPreviewPartPlacement> placements) =>
        NormalizePlacements(placements)
            .Select(placement => new SavedPreviewPartPlacement
            {
                Component = placement.Component,
                OffsetX = placement.OffsetX,
                OffsetY = placement.OffsetY,
                OffsetZ = placement.OffsetZ,
                UvChannel = placement.UvChannel,
            })
            .ToList();

    private static IEnumerable<SavedPreviewPartPlacement> NormalizePlacements(IEnumerable<SavedPreviewPartPlacement> placements) =>
        placements.Where(placement => !string.IsNullOrWhiteSpace(placement.Component) &&
                                      float.IsFinite(placement.OffsetX) &&
                                      float.IsFinite(placement.OffsetY) &&
                                      float.IsFinite(placement.OffsetZ) &&
                                      (placement.UvChannel is null || (placement.UvChannel >= 0 && placement.UvChannel <= 7)));

    private static string Normalize(string value) =>
        value.Trim().Replace('\\', '/').ToLowerInvariant();
}
