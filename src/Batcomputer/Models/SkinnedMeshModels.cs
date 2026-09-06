namespace Batcomputer;

/// <summary>A validated, project-owned existing-rig FBX. Geometry is cooked once; suit edits replay separately.</summary>
public sealed class SkinnedMeshImport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "Custom skinned mesh";
    public string SourceRelativePath { get; set; } = "";
    public string CacheRelativePath { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string DonorMeshPackage { get; set; } = "";
    public string SkeletonPackage { get; set; } = "";
    public string MeshPackage { get; set; } = "";
    public string Component { get; set; } = "CharacterMesh0";
    public float ImportScale { get; set; } = 1;
    public List<CustomStaticMeshMaterialSlot> Materials { get; set; } = [];
    // Explicit visual exclusions only. Never delete the authored component graph.
    public List<string> HiddenComponents { get; set; } = [];
    public SkinnedMeshImport Clone() => System.Text.Json.JsonSerializer.Deserialize<SkinnedMeshImport>(
        System.Text.Json.JsonSerializer.Serialize(this))!;
}
