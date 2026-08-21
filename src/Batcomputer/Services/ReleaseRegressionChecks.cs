namespace Batcomputer;

/// <summary>
/// Fast, dependency-free checks for release bugs that previously reached users.
/// Kept as a CLI command so the portable executable can verify its own behavior
/// without requiring a separate test SDK or a local copy of the game.
/// </summary>
internal static class ReleaseRegressionChecks
{
    public static int Run(TextWriter output)
    {
        var failures = new List<string>();

        Check(
            GameAssetRefreshService.AllCharacterFilters.Contains(
                GameAssetRefreshService.CharacterGadgetFilter,
                StringComparer.OrdinalIgnoreCase),
            "normal refresh extracts Content/Models/Gadgets",
            failures,
            output);
        Check(
            GameAssetRefreshService.DeveloperResearchFilters.Contains(
                GameAssetRefreshService.CharacterGadgetFilter,
                StringComparer.OrdinalIgnoreCase),
            "developer refresh extracts Content/Models/Gadgets",
            failures,
            output);

        Check(
            PartGraftService.CanRepointExistingComponentForTest(false, false, false, false),
            "matching skeletal cosmetic components can be repointed",
            failures,
            output);
        Check(
            !PartGraftService.CanRepointExistingComponentForTest(false, false, true, false),
            "a runtime glider shell cannot be reused as a cosmetic cape",
            failures,
            output);
        Check(
            !PartGraftService.CanRepointExistingComponentForTest(false, false, false, true),
            "a cosmetic cape shell cannot be reused as a runtime glider",
            failures,
            output);
        Check(
            !PartGraftService.CanRepointExistingComponentForTest(true, false, false, false),
            "static and skeletal component shells are not mixed",
            failures,
            output);

        var cosmeticCape = new NativeSuitPartRecord
        {
            CharacterFolder = "Batman",
            Slot = "Cape",
            MeshKind = "SkeletalMesh",
            MeshObjectName = "SK_Cape_Spiked",
            MeshPackagePath = "/Game/Characters/Attachments/Cape/SK_Cape_Spiked",
            ComponentTags = new List<string> { "Cape" },
        };
        var wingsuit = new NativeSuitPartRecord
        {
            CharacterFolder = "Nightwing",
            Slot = "Cape",
            MeshKind = "SkeletalMesh",
            MeshObjectName = "SK_GA_Wingsuit_Nightwing",
            MeshPackagePath = "/Game/Models/Gadgets/GA_Wingsuit_Nightwing/SK_GA_Wingsuit_Nightwing",
            ComponentTags = new List<string> { "Glider" },
        };
        Check(
            GliderService.IsCosmeticCapeAttachment(cosmeticCape) && !GliderService.IsNativeGliderPart(cosmeticCape),
            "a normal cape remains a visible cosmetic attachment",
            failures,
            output);
        Check(
            GliderService.IsNativeGliderPart(wingsuit) && !GliderService.IsCosmeticCapeAttachment(wingsuit),
            "a wingsuit remains a runtime glider visual",
            failures,
            output);
        var (las, mas) = GliderService.GliderAnimSetsForPart(wingsuit);
        Check(
            las == "/Game/Animation/LayerAnimSets/Traversal/LAS_Traversal_Nightwing" &&
            mas == "/Game/Animation/MontageAnimSets/Traversal/MAS_Glide_Nightwing",
            "glider donor traversal sets are preserved",
            failures,
            output);

        var writerMessage = RegistryPluginService.DescribeWriterBuildFailureForTest(
            1,
            "Building...\nC:/Project/Source/Test.Build.cs(17): error CS1002: ; expected\nResult: Failed");
        Check(
            writerMessage.Contains("CS1002", StringComparison.Ordinal) &&
            writerMessage.Contains("First build error", StringComparison.Ordinal),
            "registry writer reports the first useful compiler error",
            failures,
            output);
        var netFxMessage = RegistryPluginService.DescribeWriterBuildFailureForTest(
            8,
            "Unable to instantiate module 'SwarmInterface': Could not find NetFxSDK install dir");
        Check(
            netFxMessage.Contains(".NET Framework 4.8 SDK", StringComparison.Ordinal),
            "registry writer gives an actionable NETFXSDK fallback message",
            failures,
            output);

        var singleMonitor = MainForm.ConstrainWindowBoundsForTest(
            new Rectangle(-1000, -400, 5200, 2600),
            new Rectangle(0, 0, 1920, 1080),
            new Size(1440, 960),
            recenter: true,
            edgeGap: 12);
        Check(
            new Rectangle(0, 0, 1920, 1080).Contains(singleMonitor) &&
            singleMonitor.Width <= 1800 && singleMonitor.Height <= 1000,
            "oversized startup bounds fit one monitor",
            failures,
            output);
        var spannedDesktop = MainForm.ConstrainWindowBoundsForTest(
            new Rectangle(0, 0, 5000, 1800),
            new Rectangle(0, 0, 3840, 1080),
            new Size(1440, 960),
            recenter: true,
            edgeGap: 12);
        Check(
            spannedDesktop.Width <= 1800 && spannedDesktop.Height <= 1000,
            "combined-monitor work areas cannot create a two-screen window",
            failures,
            output);

        output.WriteLine(failures.Count == 0
            ? "release regressions: PASS"
            : $"release regressions: FAIL ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Check(
        bool condition,
        string description,
        ICollection<string> failures,
        TextWriter output)
    {
        output.WriteLine($"{(condition ? "PASS" : "FAIL")}: {description}");
        if (!condition)
        {
            failures.Add(description);
        }
    }
}
