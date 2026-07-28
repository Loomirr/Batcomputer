using System.Text;
using System.Text.Json;

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
        ViewerLayoutPersistence();
        UnrealPathNormalization();
        ModReleaseConventions();
        RegistryPluginConventions();
        CrossKindHeadGraftRules();
        TextureCookProfilePersistence();
        ModernSelectorConstruction();
        GameplayTagLeaf();
        GlideVisualUnknownVsAbsent();
        VisualBaseAndGameplayDonorRules();

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
    /// Portable installs default beside the executable, while configured projects retain their
    /// historical Generated/_generated location.
    /// </summary>
    private static void GeneratedRootResolution()
    {
        Console.WriteLine("Generated root");
        var temp = Path.Combine(Path.GetTempPath(), "bc_selftest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            Equal("configured project defaults to Generated",
                Path.Combine(temp, "Generated"), AppSettings.GeneratedRootFor(temp));

            Directory.CreateDirectory(Path.Combine(temp, "_generated"));
            Equal("configured project preserves an existing _generated folder",
                Path.Combine(temp, "_generated"), AppSettings.GeneratedRootFor(temp));
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
    /// The native DLL reads schema-3 mod.json paths directly. Keep the derived mod
    /// root, StringTable, and UI metadata package paths in lockstep with that contract.
    /// </summary>
    private static void ModReleaseConventions()
    {
        Console.WriteLine("Mod release conventions");
        var mod = new NativeSuitModProject { ModId = "MyBatmanPack" };
        ModProjectService.ApplyDerivedFields(mod);

        Equal("mod package name is derived from ModId", "MyBatmanPack_P", mod.PackageBaseName);
        Equal("mod content root is owned by ModId", "/Game/Mods/MyBatmanPack", mod.ContentRoot);
        Equal("mod StringTable path is owned by ModId",
            "/Game/Mods/MyBatmanPack/Localization/ST_MyBatmanPack.ST_MyBatmanPack", mod.StringTablePackage);
        Equal("DCMD derives the matching UIMD package",
            "/Game/Mods/MyBatmanPack/UI/DA_UIMD_Batman_TestSuit",
            MainForm.DeriveUimdPackagePathForTest(
                "/Game/Mods/MyBatmanPack/Characters/DA_DCMD_Batman_TestSuit_Playable.DA_DCMD_Batman_TestSuit_Playable"));
    }

    /// <summary>The generated registry remains one plugin per mod and its writer contract stays exact.</summary>
    private static void RegistryPluginConventions()
    {
        Console.WriteLine("Asset Registry plugin");
        var layout = RegistryPluginService.CreateLayout(Path.Combine(Path.GetTempPath(), "bc_registry"), "MyBatmanPack");
        Check("registry plugin is owned by the mod ID",
            layout.PluginDirectory.EndsWith(Path.Combine("Engine", "Plugins", "Mods", "MyBatmanPackRegistry"), StringComparison.Ordinal));
        Equal("registry descriptor uses the mod-owned plugin name", "MyBatmanPackRegistry.uplugin", Path.GetFileName(layout.DescriptorPath));
        Equal("registry binary has the stock AssetRegistry filename", "AssetRegistry.bin", Path.GetFileName(layout.RegistryPath));
        Equal("registry config has the stock Game.ini filename", "Game.ini", Path.GetFileName(layout.GameIniPath));
        Check("registry config extends PawnMetaData scanning to /Game/Mods",
            RegistryPluginService.BuildGameIni().Contains("(Path=\"/Game/Mods\")", StringComparison.Ordinal));

        var rows = new[]
        {
            new RegistryPluginService.RegistryRow("/Game/Mods/MyBatmanPack/Characters/DA_DCMD_Batman_Alpha"),
            new RegistryPluginService.RegistryRow("/Game/Mods/MyBatmanPack/Characters/DA_DCMD_Batman_Beta.DA_DCMD_Batman_Beta"),
        };
        Check("clean mod DCMD rows pass registry validation", RegistryPluginService.ValidateRows(rows).Count == 0);
        Check("duplicate DCMD primary IDs block the registry",
            RegistryPluginService.ValidateRows(new[]
            {
                new RegistryPluginService.RegistryRow("/Game/Mods/A/Characters/DA_DCMD_Batman_Same"),
                new RegistryPluginService.RegistryRow("/Game/Mods/B/Characters/DA_DCMD_Batman_Same"),
            }).Any(error => error.Contains("collides", StringComparison.OrdinalIgnoreCase)));
        Check("writer verification requires every expected primary row",
            RegistryPluginService.VerificationMatches(
                "SUIT_SLOTS_REGISTRY_WRITER_RESULT cooked_header=yes expected_primary_rows=2 exact_primary_rows=2 exact_primary_ids=2 all_expected_rows=yes all_expected_primary_ids=yes sentinel_enabled=yes sentinel_exact_row=yes sentinel_exact_primary_id=yes",
                2));
    }

    /// <summary>A grafted hair component named Head_2 must replace a donor Head cowl.</summary>
    private static void CrossKindHeadGraftRules()
    {
        Console.WriteLine("Cross-kind head grafts");
        var hair = new NativeSuitProject
        {
            PartGrafts = new List<SavedPartGraft>
            {
                new() { Slot = "Head", ResolvedComponent = "Head_2" },
            },
        };
        Check("Head_2 hair graft hides the donor Head cowl",
            MainForm.CrossKindHeadGraftNeedsCowlRemovalForTest(hair));

        var direct = new NativeSuitProject
        {
            PartGrafts = new List<SavedPartGraft>
            {
                new() { Slot = "Head", ResolvedComponent = "Head" },
            },
        };
        Check("same-slot head replacement does not add a redundant removal",
            !MainForm.CrossKindHeadGraftNeedsCowlRemovalForTest(direct));
    }

    /// <summary>Compact cook choices must survive saving a suit project for later staging.</summary>
    private static void TextureCookProfilePersistence()
    {
        Console.WriteLine("Texture cook profile");
        var project = new NativeSuitProject
        {
            GeneratedTextures = new List<GeneratedTextureEntry>
            {
                new()
                {
                    DisplayName = "Mask",
                    CookProfile = "mask-1k-bc1",
                    CookWidth = 1024,
                    CookHeight = 1024,
                    CookPixelFormat = "PF_DXT1",
                }
            }
        };

        var restored = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project));
        var texture = restored?.GeneratedTextures.SingleOrDefault();
        Check("compact cook profile persists with the texture",
            texture is not null && texture.CookProfile == "mask-1k-bc1" &&
            texture.CookWidth == 1024 && texture.CookHeight == 1024 &&
            texture.CookPixelFormat == "PF_DXT1");
    }

    /// <summary>The owner-drawn selector and material forge must construct without a visible form.</summary>
    private static void ModernSelectorConstruction()
    {
        Console.WriteLine("Modern selector");
        using var selector = new ThemedDropDown();
        selector.Items.Add("First");
        selector.Items.Add("Second");
        selector.SelectedItem = "First";
        Check("themed selector accepts transparent parent rendering",
            selector.SelectedIndex == 0 && string.Equals(selector.SelectedItem?.ToString(), "First", StringComparison.Ordinal));
        using var selectorOptions = selector.CreatePopupOptionsForTest();
        Check("themed selector popup options construct for a ToolStrip host", selectorOptions.Controls.Count > 0);

        using var wizard = new MaterialWizard(Path.GetTempPath(), "SelfTest", "MI_SelfTest");
        Check("material forge constructs with the themed selector", wizard.Controls.Count > 0);

        using var materialPicker = new MaterialCatalogPicker();
        Check("catalog-backed material picker constructs without an OS file dialog", materialPicker.Controls.Count > 0);

        var homeHero = new VirtualTilePanel.HeroModel
        {
            Overline = "MOD WORKSPACE",
            Title = "SelfTest Mod",
            Subtitle = "Two suits grouped into one mod release.",
            Workflow = new[]
            {
                new VirtualTilePanel.HeroModel.WorkflowStep { Label = "1. MOD", Detail = "mod selected", Accent = Theme.Research, Complete = true },
                new VirtualTilePanel.HeroModel.WorkflowStep { Label = "2. SUITS", Detail = "add or edit", Accent = Theme.Base, Current = true },
                new VirtualTilePanel.HeroModel.WorkflowStep { Label = "3. BUILD", Detail = "release when ready", Accent = Theme.Gold },
            },
        };
        using var homeGrid = new VirtualTilePanel { Size = new Size(760, 320) };
        homeGrid.SetHero(homeHero);
        homeGrid.SetTiles(new[] { new VirtualTilePanel.Tile { Section = "1. MOD", Title = "SelfTest", Accent = Theme.Research } });
        using var homeBitmap = new Bitmap(760, 320);
        try
        {
            homeGrid.DrawToBitmap(homeBitmap, new Rectangle(Point.Empty, homeBitmap.Size));
            Check("mod workflow hero paints in the virtual tile grid", homeGrid.Hero?.Workflow.Count == 3);
        }
        catch (Exception ex)
        {
            Check("mod workflow hero paints in the virtual tile grid", false, ex.Message);
        }
    }

    /// <summary>
    /// The part mover must have a home outside a suit project: a stock character preview should be
    /// able to remember an alignment, and returning an axis to zero should clear that entry again.
    /// </summary>
    private static void ViewerLayoutPersistence()
    {
        Console.WriteLine("Viewer layout");
        var temp = Path.Combine(Path.GetTempPath(), "bc_viewer_layout_" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = ViewerLayoutService.CharacterKey(
                "LEGOBatmanLotDK\\Content\\Characters\\Minifig\\BruceWayne\\BP_Test");
            Check("character key normalizes path separators",
                key == "character:legobatmanlotdk/content/characters/minifig/brucewayne/bp_test");

            Check("viewer placement writes to sidecar",
                ViewerLayoutService.Save(temp, key, "Hip", 0.1f, -0.05f, 0f));
            var saved = ViewerLayoutService.Load(temp, key).SingleOrDefault();
            Check("viewer placement reloads without a project",
                saved is not null && saved.Component == "Hip" &&
                Math.Abs(saved.OffsetX - 0.1f) < 0.00001f &&
                Math.Abs(saved.OffsetY + 0.05f) < 0.00001f);

            ViewerLayoutService.Save(temp, key, "Hip", 0f, 0f, 0f, 2);
            saved = ViewerLayoutService.Load(temp, key).SingleOrDefault();
            Check("UV-only viewer override is persisted",
                saved is not null && saved.UvChannel == 2 &&
                Math.Abs(saved.OffsetX) < 0.00001f && Math.Abs(saved.OffsetY) < 0.00001f);

            ViewerLayoutService.Save(temp, key, "Hip", 0f, 0f, 0f, null);
            Check("reset clears both alignment and UV viewer overrides",
                ViewerLayoutService.Load(temp, key).Count == 0);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup */ }
        }
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

    /// <summary>
    /// A cutscene is a legitimate visual source, but it can never impersonate the
    /// runtime donor. This keeps the broad creative picker separate from the
    /// narrow gameplay safety gate.
    /// </summary>
    private static void VisualBaseAndGameplayDonorRules()
    {
        Console.WriteLine("Visual base and gameplay donor");
        const string visual = "/Game/Characters/Minifig/Joker/BP_Joker_Default_Cutscene";
        const string donor = "/Game/Characters/Minifig/Batman/BP_Batman_TheBatman2025_Playable";

        Check("a character cutscene is accepted as a visual base",
            BaseEligibilityService.IsVisualCharacterPackage(visual) &&
            BaseEligibilityService.IsCutsceneVisualPackage(visual));
        Check("a cutscene cannot be used as the gameplay donor",
            !BaseEligibilityService.IsGameplayDonorPackage(visual));
        Check("a real playable is accepted as the gameplay donor",
            BaseEligibilityService.IsGameplayDonorPackage(donor));

        var ready = BaseEligibilityService.Evaluate(visual, donor);
        Check("cutscene visual plus playable donor is base-ready", ready.IsReady);

        var blocked = BaseEligibilityService.Evaluate(visual, visual);
        Check("visual-only selection is blocked from staging", !blocked.IsReady &&
            blocked.Detail.Contains("_Playable", StringComparison.Ordinal));

        var project = new NativeSuitProject
        {
            BaseProfile = BaseEligibilityService.CreateProfile(visual, donor)
        };
        var restored = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project));
        Check("visual and gameplay base choices persist with the suit",
            restored?.BaseProfile?.VisualBasePackage == visual &&
            restored.BaseProfile.GameplayDonorPackage == donor &&
            restored.BaseProfile.Eligibility == "ready");
    }
}
