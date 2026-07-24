using System.Text;

namespace Batcomputer;

/// <summary>
/// A dependency-free assertion harness, run with <c>Batcomputer.exe --self-test</c>.
///
/// Deliberately not xUnit: this project ships as a single self-contained exe and takes no NuGet
/// packages, so the tests live in the app and run from the app. What it covers is the pure logic
/// that is easy to break and expensive to catch in-game - tag validation, path derivation, naming.
/// Byte-level asset surgery is not covered here; that still needs a real pak and a real launch.
/// </summary>
internal static class SelfTest
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    public static int Run()
    {
        Console.WriteLine("Batcomputer self-test");
        Console.WriteLine(new string('-', 60));

        PawnTagValidation();
        PawnTagSuggestion();
        GeneratedRootResolution();
        UnrealPathNormalization();
        GameplayTagLeaf();
        GlideVisualUnknownVsAbsent();

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"{_passed} passed, {Failures.Count} failed");
        foreach (var f in Failures)
        {
            Console.WriteLine("  FAIL " + f);
        }
        return Failures.Count == 0 ? 0 : 1;
    }

    // ---- assertions ---------------------------------------------------------

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ok   {name}");
        }
        else
        {
            Failures.Add(detail is null ? name : $"{name}: {detail}");
            Console.WriteLine($"  FAIL {name}");
        }
    }

    private static void Equal(string name, string? expected, string? actual) =>
        Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
            $"expected \"{expected}\", got \"{actual}\"");

    // ---- cases --------------------------------------------------------------

    /// <summary>
    /// A suit shipping on the shared donor tag is the bug that made custom-to-custom switching do
    /// nothing. The validator has to reject it, not just a blank tag.
    /// </summary>
    private static void PawnTagValidation()
    {
        Console.WriteLine("PawnTag validation");
        var svc = new StageValidationService("", null);

        static NativeSuitProject WithTag(string tag) => new() { PawnTag = tag, SlotId = "test_suit" };

        var blank = svc.Validate(WithTag(""));
        Check("blank tag is an ERROR",
            blank.Any(f => f.Severity == "ERROR" && f.Message.Contains("no PawnTag")));

        var donor = svc.Validate(WithTag("Pawns.Playable.Batman.TheBatman2025"));
        Check("donor tag is an ERROR",
            donor.Any(f => f.Severity == "ERROR" && f.Message.Contains("shared donor tag")));

        var donorCased = svc.Validate(WithTag("pawns.playable.batman.thebatman2025"));
        Check("donor tag match is case-insensitive",
            donorCased.Any(f => f.Severity == "ERROR" && f.Message.Contains("shared donor tag")));

        var odd = svc.Validate(WithTag("Custom.Thing.Whatever"));
        Check("tag outside Pawns.Playable.* warns but does not block",
            odd.Any(f => f.Severity == "WARN") &&
            !odd.Any(f => f.Severity == "ERROR" && f.Message.Contains("PawnTag")));

        var good = svc.Validate(WithTag("Pawns.Playable.Batman.Electric"));
        Check("a unique tag produces no PawnTag finding",
            !good.Any(f => f.Message.Contains("PawnTag") || f.Message.Contains("pawn tag")));
    }

    /// <summary>The suggestion has to be a legal tag, or it just moves the problem.</summary>
    private static void PawnTagSuggestion()
    {
        Console.WriteLine("PawnTag suggestion");
        Equal("spaces and punctuation collapse to PascalCase",
            "Pawns.Playable.Batman.ElectricSuit",
            MainForm.SuggestPawnTagForTest(new NativeSuitProject { DisplayName = "electric suit!" }));

        Equal("falls back to the slot id when unnamed",
            "Pawns.Playable.Batman.BatmanJoker",
            MainForm.SuggestPawnTagForTest(new NativeSuitProject { SlotId = "batman_joker" }));

        Equal("nothing to work with yields empty, not a bare prefix",
            "",
            MainForm.SuggestPawnTagForTest(new NativeSuitProject()));
    }

    /// <summary>
    /// The pre-rename output folder is still honoured. Getting this wrong orphans every suit a
    /// long-time user has already built.
    /// </summary>
    private static void GeneratedRootResolution()
    {
        Console.WriteLine("Generated root");
        var temp = Path.Combine(Path.GetTempPath(), "bc_selftest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            Equal("defaults to Generated when neither exists",
                Path.Combine(temp, "Generated"), AppSettings.GeneratedRootFor(temp));

            Directory.CreateDirectory(Path.Combine(temp, "_generated"));
            Equal("prefers an existing _generated",
                Path.Combine(temp, "_generated"), AppSettings.GeneratedRootFor(temp));

            Directory.CreateDirectory(Path.Combine(temp, "Generated"));
            Equal("prefers Generated once both exist",
                Path.Combine(temp, "Generated"), AppSettings.GeneratedRootFor(temp));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private static void UnrealPathNormalization()
    {
        Console.WriteLine("Unreal paths");
        Equal("object suffix is stripped to a package path",
            "/Game/Mods/X/Characters/BP_Y",
            UnrealPathUtil.NormalizePackagePath("/Game/Mods/X/Characters/BP_Y.BP_Y"));
        Equal("an already-clean path is unchanged",
            "/Game/Mods/X/Characters/BP_Y",
            UnrealPathUtil.NormalizePackagePath("/Game/Mods/X/Characters/BP_Y"));
        Equal("asset name is the leaf",
            "BP_Y", UnrealPathUtil.AssetName("/Game/Mods/X/Characters/BP_Y"));
    }

    /// <summary>
    /// Regression: an unreadable base used to return the same bare null as a base with no cape, so a
    /// suit whose asset dump had been pruned was told it had "no native glide visual" - a defect it
    /// did not have. Unknown must never be reported as Absent.
    /// </summary>
    private static void GlideVisualUnknownVsAbsent()
    {
        Console.WriteLine("Glide visual detection");
        var svc = new AnimArchetypeGraftService();

        var noTemplate = new NativeSuitProject { PlayableTemplate = null };
        Check("no base template recorded is Unknown, not Absent",
            svc.BaseGlideVisual(noTemplate, out _) == AnimArchetypeGraftService.GlideVisualStatus.Unknown);

        var gone = new NativeSuitProject
        {
            PlayableTemplate = new TemplateRecord
            {
                Uasset = Path.Combine(Path.GetTempPath(), "bc_no_such_base_" + Guid.NewGuid().ToString("N") + ".uasset"),
            },
        };
        Check("a base file that no longer exists is Unknown, not Absent",
            svc.BaseGlideVisual(gone, out _) == AnimArchetypeGraftService.GlideVisualStatus.Unknown);

        var garbage = Path.Combine(Path.GetTempPath(), "bc_bad_base_" + Guid.NewGuid().ToString("N") + ".uasset");
        try
        {
            File.WriteAllText(garbage, "this is not a uasset");
            var bad = new NativeSuitProject { PlayableTemplate = new TemplateRecord { Uasset = garbage } };
            Check("a base that will not parse is Unknown, not Absent",
                svc.BaseGlideVisual(bad, out _) == AnimArchetypeGraftService.GlideVisualStatus.Unknown);
        }
        finally
        {
            try { File.Delete(garbage); } catch { /* temp cleanup */ }
        }
    }

    private static void GameplayTagLeaf()
    {
        Console.WriteLine("Gameplay tag leaf");
        Equal("underscores become word breaks", "BatmanJoker", MainForm.ToGameplayTagLeafForTest("batman_joker"));
        Equal("existing PascalCase survives", "ElectricSuit", MainForm.ToGameplayTagLeafForTest("ElectricSuit"));
        Equal("digits are kept", "Batman2025", MainForm.ToGameplayTagLeafForTest("batman 2025"));
    }
}
