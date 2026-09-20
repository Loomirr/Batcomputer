namespace Batcomputer;

/// <summary>One isolated Unreal cook. Failed jobs are retained for diagnosis.</summary>
internal sealed class SkinnedCookWorkspace : IDisposable
{
    internal const string LongestEngineOutput = "Saved/Cooked/Windows/Engine/Plugins/Animation/DeformerGraph/Content/DeformerFunctions/DG_Function_LinearBlendSkin_Morph_Cloth_PositionOnly.uasset";
    internal string Root { get; }
    internal bool Complete { get; set; }
    internal SkinnedCookWorkspace(string meshPackage)
    {
        Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BCS", Guid.NewGuid().ToString("N")[..16]));
        ValidateLength(Root, meshPackage);
        Directory.CreateDirectory(Root);
    }
    internal static void ValidateLength(string root, string meshPackage)
    {
        var outputs = new[] { LongestEngineOutput, "Saved/Cooked/Windows/SkinnedCook/Content/" + meshPackage[6..] + "_PhysicsAsset.uasset" };
        if (outputs.Any(p => Path.GetFullPath(Path.Combine(root, p)).Length >= 250))
            throw new InvalidDataException("The temporary cook path or mesh package name is too long for Unreal. Use a shorter Windows TEMP folder or shorten the model/package name. Cook folder: " + root);
    }
    public void Dispose()
    {
        if (!Complete) return;
        try
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BCS"));
            if (FileSystemPathUtil.IsWithinDirectory(Root, parent) && Path.GetFileName(Root).Length == 16)
                Directory.Delete(Root, true);
        }
        catch (IOException) { /* Unreal may briefly retain a log handle. */ }
        catch (UnauthorizedAccessException) { }
    }
}
