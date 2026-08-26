namespace Batcomputer;

/// <summary>
/// Tested material templates for LOTDK. A template never invents a parent graph or
/// changes a static permutation: every output copies a cooked Material Instance whose
/// render context, mesh family, and switches already work in the game.
/// </summary>
internal sealed class MaterialTemplateCatalogService
{
    internal static class TargetKinds
    {
        public const string Body = "Body";
        public const string Face = "Face";
        public const string Cape = "Cape";
        public const string Accessory = "Accessory";
        public const string Unknown = "Unknown";
    }

    internal sealed record Target(
        string Component,
        string Label,
        int Slot,
        string MeshPackagePath)
    {
        public string Kind => ClassifyTarget(this);
        public string DisplayName => string.IsNullOrWhiteSpace(Label)
            ? $"{Component} · slot {Slot}"
            : Label;
    }

    internal sealed record Output(
        string Role,
        string NameSuffix,
        string DonorPackagePath,
        bool Primary = false);

    internal sealed class Recipe
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Category { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Guidance { get; init; } = "";
        public bool IsFace { get; init; }
        public bool Advanced { get; init; }
        public bool Enabled { get; init; } = true;
        // True only for donor families whose shipped MMR was verified to leave green unused.
        // Other specialized attachment materials can carry authored mask data in that channel.
        public bool ExpectsUnusedMmrGreen { get; init; }
        public string DisabledReason { get; init; } = "";
        public IReadOnlyList<string> AllowedTargetKinds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CompatibleMeshPackagePaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<Output> Outputs { get; init; } = Array.Empty<Output>();
        public IReadOnlyDictionary<string, string> DefaultTextureOverrides { get; init; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Output PrimaryOutput => Outputs.FirstOrDefault(output => output.Primary)
                                       ?? Outputs.First();
    }

    internal sealed record ResolvedOutput(Output Definition, string DiskPath);

    internal sealed record Compatibility(
        bool CanUse,
        bool Exact,
        string Status,
        string Detail,
        IReadOnlyList<ResolvedOutput> ResolvedOutputs);

    private const string StandardFaceMesh = "/Game/Characters/Attachments/LEGOface/SK_LEGOface";
    private const string SuperheroFaceMesh = "/Game/Characters/Attachments/LEGOface/SK_LEGOface_Superhero";
    private const string Joker89FaceMesh = "/Game/Characters/Attachments/LEGOface/SK_LEGOface_Joker89";
    private const string MrFreezeFaceMesh = "/Game/Characters/Attachments/LEGOface/SK_LEGOface_MrFreeze_BaR";
    private const string FaceTexMesh = "/Game/Characters/Attachments/FaceTex/SK_FaceTex_LEGOfig-NEW";

    private const string DummyRao = "/Game/Characters/Textures/Shared/EoM/T_Dummy_RAO.T_Dummy_RAO";
    private const string DummyCt = "/Game/Characters/Textures/Shared/EoM/T_Dummy_CTUV.T_Dummy_CTUV";
    private const string DummyNormal = "/Game/Characters/Textures/Shared/EoM/T_Dummy_Norm.T_Dummy_Norm";
    private const string DummyColourId = "/Game/Characters/Textures/Shared/EoM/T_Dummy_Black_BC.T_Dummy_Black_BC";

    private static readonly IReadOnlyList<Recipe> Catalog = BuildCatalog();

    public IReadOnlyList<Recipe> Recipes() => Catalog;

    public Compatibility Evaluate(Recipe recipe, Target? target)
    {
        if (!recipe.Enabled)
        {
            return new Compatibility(false, false, "Unavailable", recipe.DisabledReason, Array.Empty<ResolvedOutput>());
        }

        var resolved = new List<ResolvedOutput>();
        var missing = new List<string>();
        foreach (var output in recipe.Outputs)
        {
            var diskPath = MainForm.ResolveMiDiskPath(output.DonorPackagePath, preferExport: false);
            if (string.IsNullOrWhiteSpace(diskPath))
            {
                missing.Add($"{output.Role}: {output.DonorPackagePath}");
            }
            else
            {
                resolved.Add(new ResolvedOutput(output, diskPath));
            }
        }

        if (missing.Count > 0)
        {
            return new Compatibility(
                false,
                false,
                "Donor not extracted",
                "Refresh the full character asset index. Missing cooked donor(s):\n" + string.Join("\n", missing),
                resolved);
        }

        if (target is not null && recipe.AllowedTargetKinds.Count > 0 &&
            !recipe.AllowedTargetKinds.Contains(target.Kind, StringComparer.OrdinalIgnoreCase))
        {
            return new Compatibility(
                false,
                false,
                "Wrong target",
                $"This template is for {string.Join(" / ", recipe.AllowedTargetKinds)} targets; the selected {target.DisplayName} is classified as {target.Kind}.",
                resolved);
        }

        if (recipe.CompatibleMeshPackagePaths.Count > 0)
        {
            var targetMesh = Normalize(target?.MeshPackagePath);
            if (string.IsNullOrWhiteSpace(targetMesh))
            {
                return new Compatibility(
                    true,
                    false,
                    "Verify mesh family",
                    $"The selected target did not expose a mesh path. This template requires {DescribeMeshes(recipe.CompatibleMeshPackagePaths)}; Batcomputer will check it when the material is applied.",
                    resolved);
            }

            if (!recipe.CompatibleMeshPackagePaths.Any(mesh => MeshMatches(mesh, targetMesh)))
            {
                return new Compatibility(
                    false,
                    false,
                    "Different mesh family",
                    $"The selected target uses {UnrealPathUtil.AssetName(targetMesh)}. This template is made for {DescribeMeshes(recipe.CompatibleMeshPackagePaths)} and cannot be used with a different UV or expression layout.",
                    resolved);
            }
        }

        var contextNote = recipe.Outputs.Count switch
        {
            1 when recipe.Outputs[0].Role.Contains("gameplay", StringComparison.OrdinalIgnoreCase) =>
                "Gameplay-only donor. Test any cutscene use separately.",
            1 when recipe.Outputs[0].Role.Contains("cutscene", StringComparison.OrdinalIgnoreCase) =>
                "Cutscene-only donor. It is not a gameplay material.",
            2 => "Generates a synchronized gameplay/cutscene pair.",
            4 => "Generates the complete gameplay/cutscene LOD0/LOD1 set.",
            _ => "Creates one material from a game template.",
        };
        return new Compatibility(true, true, "Compatible", contextNote, resolved);
    }

    private static IReadOnlyList<Recipe> BuildCatalog()
    {
        static Output One(string donor, string role = "shared") => new(role, "", donor, true);
        static Output Gameplay(string donor) => new("gameplay", "_EoM", donor, true);
        static Output Cutscene(string donor) => new("cutscene", "_CUT", donor);
        static string[] Kinds(params string[] values) => values;
        static string[] Meshes(params string[] values) => values;

        return new List<Recipe>
        {
            new()
            {
                Id = "body.recolourable.absolute",
                DisplayName = "Recolourable character body",
                Category = "Character body",
                Summary = "Textured minifigure body with the game's working Colour Mask and Red Brick support.",
                Guidance = "Replace BC, MMR, DNRM, pristine maps, and ColourMask as needed. Both runtime contexts are generated from Batman Absolute donors.",
                ExpectsUnusedMmrGreen = true,
                AllowedTargetKinds = Kinds(TargetKinds.Body),
                Outputs = new[]
                {
                    Gameplay("/Game/Characters/Minifig/Batman/Material/MI_Batman_Absolute_EOM"),
                    Cutscene("/Game/Characters/Minifig/Batman/Material/MI_Batman_Absolute_CUT"),
                },
            },
            new()
            {
                Id = "body.fixed.bruce-suit",
                DisplayName = "Fixed-colour textured body",
                Category = "Character body",
                Summary = "Body artwork that should remain fixed instead of using a colour-mask recolour branch.",
                Guidance = "Replace the donor BC/MMR/DNRM artwork. This template intentionally makes no Red Brick recolour promise.",
                AllowedTargetKinds = Kinds(TargetKinds.Body),
                Outputs = new[]
                {
                    Gameplay("/Game/Characters/Minifig/BruceWayne/Material/MI_BruceWayne_SuitBlack_EoM"),
                    Cutscene("/Game/Characters/Minifig/BruceWayne/Material/MI_BruceWayne_SuitBlack_EoM_CUT"),
                },
            },
            new()
            {
                Id = "body.solid.unsupported",
                DisplayName = "Universal solid-colour body",
                Category = "Character body",
                Summary = "A generic body that needs no texture artwork.",
                Enabled = false,
                DisabledReason = "No tested gameplay/cutscene pair has the required solid-body setup. Use Fixed-colour textured body with a tiny flat BC texture instead.",
                AllowedTargetKinds = Kinds(TargetKinds.Body),
            },
            new()
            {
                Id = "accessory.solid.paired",
                DisplayName = "Solid-colour accessory (paired)",
                Category = "Accessories",
                Summary = "Simple plastic attachment with synchronized gameplay and cutscene outputs.",
                Guidance = "The Bowler material's geometry-specific RAO, CT, NRM, and colour-ID maps are replaced with built-in neutral maps.",
                AllowedTargetKinds = Kinds(TargetKinds.Accessory),
                Outputs = new[]
                {
                    Gameplay("/Game/Characters/Attachments/Hat/Bowler/MI_HAT_Bowler_Black"),
                    Cutscene("/Game/Characters/Attachments/Hat/Bowler/MI_HAT_Bowler_Black_CUT"),
                },
                DefaultTextureOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RAO"] = DummyRao,
                    ["CT"] = DummyCt,
                    ["NRM"] = DummyNormal,
                    ["SwapColourID"] = DummyColourId,
                },
            },
            new()
            {
                Id = "accessory.solid.gameplay-clean",
                DisplayName = "Solid-colour accessory (clean gameplay donor)",
                Category = "Accessories",
                Summary = "Minimal single-colour gameplay attachment donor without Bowler geometry maps.",
                Guidance = "This clean material has no tested cutscene match. Use the paired template when the part must appear in both gameplay and cutscenes.",
                Advanced = true,
                AllowedTargetKinds = Kinds(TargetKinds.Accessory),
                Outputs = new[] { One("/Game/Characters/Attachments/Hair/MI_Black", "gameplay only") },
            },
            new()
            {
                Id = "accessory.textured-cowl.native-plastic",
                DisplayName = "Textured cowl / custom mesh (paired)",
                Category = "Accessories",
                Summary = "Native plastic cowl material with synchronized gameplay and cutscene outputs.",
                Guidance = "Preferred for custom cowls. It retains the game's inherited micro-detail while avoiding the Mask of Tengu donor's metallic switch and extreme negative decal roughness. Donor mesh-specific RAO, CT, NRM, and ColourMask maps start neutral. For this template use R=metalness, G=unused, B=roughness in MMR.",
                ExpectsUnusedMmrGreen = true,
                AllowedTargetKinds = Kinds(TargetKinds.Accessory),
                Outputs = new[]
                {
                    Gameplay("/Game/Characters/Attachments/Hat/BatmanCowl_MoldedEyes/Materials/MI_HAT_BatmanBraveAndTheBold_EOM"),
                    Cutscene("/Game/Characters/Attachments/Hat/BatmanCowl_MoldedEyes/Materials/MI_HAT_BatmanBraveAndTheBold_CUT"),
                },
                DefaultTextureOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RAO"] = DummyRao,
                    ["CT"] = DummyCt,
                    ["NRM"] = DummyNormal,
                    ["ColourMask"] = DummyColourId,
                },
            },
            new()
            {
                Id = "accessory.textured.poison-ivy",
                DisplayName = "Textured / decal accessory",
                Category = "Accessories",
                Summary = "BC/MMR/DNRM attachment artwork with gameplay and cutscene donors.",
                Guidance = "Use for hair, hats, torso attachments, and similar meshes whose UVs match your replacement textures.",
                AllowedTargetKinds = Kinds(TargetKinds.Accessory),
                Outputs = new[]
                {
                    Gameplay("/Game/Characters/Attachments/Hair/PoisonIvy/Materials/MI_HAIR_PoisonIvy_EoM"),
                    Cutscene("/Game/Characters/Attachments/Hair/PoisonIvy/Materials/MI_HAIR_PoisonIvy_EoM_CUT"),
                },
            },
            new()
            {
                Id = "accessory.metallic.armoured",
                DisplayName = "Metallic plastic attachment",
                Category = "Accessories",
                Summary = "Simple attachment using the game's metallic material option.",
                Guidance = "Creates the Batman Armoured Suit gameplay/cutscene pair. Supply maps made for your mesh.",
                AllowedTargetKinds = Kinds(TargetKinds.Accessory),
                Outputs = new[]
                {
                    Gameplay("/Game/Characters/Attachments/Torso/Batman_ArmouredSuit/MI_TorsoA_BatmanArmouredSuit"),
                    Cutscene("/Game/Characters/Attachments/Torso/Batman_ArmouredSuit/MI_TorsoA_BatmanArmouredSuit_CUT"),
                },
            },
            new()
            {
                Id = "cape.cloth.spiked",
                DisplayName = "Cloth cape (complete LOD set)",
                Category = "Capes",
                Summary = "The four distinct materials required by a normal cloth cape.",
                Guidance = "LOTDK uses different controller families for LOD0 and LOD1, plus gameplay and cutscene variants. This template keeps all four together.",
                AllowedTargetKinds = Kinds(TargetKinds.Cape),
                Outputs = new[]
                {
                    new Output("gameplay LOD0", "_LOD0", "/Game/Characters/Attachments/Cape/Spiked/MI_CAPE_Spiked_Black17_LOD0", true),
                    new Output("cutscene LOD0", "_LOD0_CUT", "/Game/Characters/Attachments/Cape/Spiked/MI_CAPE_Spiked_Black17_LOD0_CUT"),
                    new Output("gameplay LOD1", "_LOD1", "/Game/Characters/Attachments/Cape/Spiked/MI_CAPE_Spiked_Black17_LOD1"),
                    new Output("cutscene LOD1", "_LOD1_CUT", "/Game/Characters/Attachments/Cape/Spiked/MI_CAPE_Spiked_Black17_LOD1_CUT"),
                },
            },
            new()
            {
                Id = "cape.rubber.one-hole",
                DisplayName = "Rubber cape / attachment",
                Category = "Capes",
                Summary = "A tested rubber material with the game's colour-tint behaviour.",
                Guidance = "Gameplay-only donor. Keep rubber separate from ordinary plastic and cloth.",
                Advanced = true,
                AllowedTargetKinds = Kinds(TargetKinds.Cape, TargetKinds.Accessory),
                Outputs = new[] { One("/Game/Characters/Attachments/Cape/OneHole_Rubber/MI_CAPE_Rubber_Black", "gameplay only") },
            },

            Face("face.standard.regular", "Regular expressive face", "Standard faces",
                "Full ordinary LEGO face topology with brows, eyes, print layers, and mouth support.",
                "/Game/Characters/Attachments/Face/FACE_BruceAdult/MI_FACE_BruceAdult", StandardFaceMesh),
            Face("face.standard.simple", "Simple regular face", "Standard faces",
                "A simpler regular face with fewer editable layers.",
                "/Game/Characters/Attachments/Face/FACE_GenericMale/MI_FACE_GenericMale", StandardFaceMesh),
            Face("face.standard.no-eyes", "No-eyes / cowl face", "Standard faces",
                "Batman face donor whose left and right eye zones are compiled off.",
                "/Game/Characters/Attachments/Face/FACE_Batman/MI_FACE_Batman_NoEyes", StandardFaceMesh,
                guidance: "Best base for cowls that supply their own eye shapes. You may replace the lower-face print without re-enabling eyes."),
            new()
            {
                Id = "face.standard.joker89-print-no-eyes",
                DisplayName = "Joker ’89 lower-face print — no eyes",
                Category = "Standard faces",
                Summary = "Joker ’89 lower-face artwork on Batman's standard face with the eye regions disabled.",
                Guidance = "For a cowl with no visible eyes, copy MI_FACE_Batman_NoEyes, then replace only HeadLowerUnder BC/NML with the Joker ’89 textures. Do not apply the Joker89 face material directly to SK_LEGOface.",
                IsFace = true,
                AllowedTargetKinds = Kinds(TargetKinds.Face),
                CompatibleMeshPackagePaths = Meshes(StandardFaceMesh),
                Outputs = new[] { One("/Game/Characters/Attachments/Face/FACE_Batman/MI_FACE_Batman_NoEyes") },
                DefaultTextureOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HeadLowerUnder BC"] = "/Game/Characters/Textures/Attachments/LEGOface/T_LOWER_UNDER_Joker_Batman89_DIST_BC.T_LOWER_UNDER_Joker_Batman89_DIST_BC",
                    ["HeadLowerUnder NML"] = "/Game/Characters/Textures/Attachments/LEGOface/T_LOWER_UNDER_Joker_Batman89_DIST_DNRM.T_LOWER_UNDER_Joker_Batman89_DIST_DNRM",
                },
            },
            Face("face.standard.lashes", "Face with lashes", "Standard faces",
                "Standard face with working eyelash layers.",
                "/Game/Characters/Attachments/Face/FACE_Batgirl/MI_FACE_Batgirl", StandardFaceMesh),
            Face("face.standard.recolour-lashes", "Recolourable lashes face", "Standard faces",
                "Standard face with lash and tint controls.",
                "/Game/Characters/Attachments/Face/FACE_Catwoman/MI_FACE_Catwoman", StandardFaceMesh),
            Face("face.standard.glasses", "Glasses face", "Standard faces",
                "Standard face material with a working glasses zone and its supporting layers.",
                "/Game/Characters/Attachments/Face/FACE_QueerEye90sGlasses/MI_FACE_QueerEye90sGlasses", StandardFaceMesh),
            Face("face.superhero.robin", "Superhero-mask face", "Special face rigs",
                "Face material for the alternate superhero face mesh.",
                "/Game/Characters/Attachments/Face/FACE_Robin/MI_FACE_Robin", SuperheroFaceMesh),
            Face("face.superhero.nightwing", "Superhero-mask face (Nightwing)", "Special face rigs",
                "Alternate superhero-mask layout with Colour Mask support.",
                "/Game/Characters/Attachments/Face/FACE_Nightwing/MI_FACE_Nightwing", SuperheroFaceMesh),
            Face("face.facetex.deathstroke", "FaceTex projected face", "Special face rigs",
                "Projected FaceTex material for the dedicated FaceTex mesh family.",
                "/Game/Characters/Attachments/FaceTex/FTEX_Goon_Deathstroke_Arkham/MI_FTEX_Deathstroke_Arkham", FaceTexMesh,
                advanced: true),
            Face("face.exact.joker89", "Exact Joker ’89 face", "Special face rigs",
                "Exact donor for SK_LEGOface_Joker89; its visible eye behavior is not represented by normal standard-face eye parameters.",
                "/Game/Characters/Attachments/Face/FACE_Joker_Batman89/MI_FACE_Joker_Batman89", Joker89FaceMesh,
                advanced: true,
                guidance: "Only use with SK_LEGOface_Joker89. For a Batman cowl or no-eyes face, use the Joker ’89 lower-face print — no eyes template instead."),
            Face("face.exact.joker-mime", "Exact Joker Mime face", "Special face rigs",
                "Joker89-family variant with eye-back textures.",
                "/Game/Characters/Attachments/Face/FACE_Joker_Mime/MI_FACE_Joker_Mime", Joker89FaceMesh,
                advanced: true),
            Face("face.exact.mr-freeze", "Exact Mr. Freeze face", "Special face rigs",
                "Material for the dedicated Batman & Robin Mr. Freeze face mesh.",
                "/Game/Characters/Attachments/Face/FACE_MrFreeze_BatmanAndRobin/MI_FACE_MrFreeze_BaR", MrFreezeFaceMesh,
                advanced: true),
            Face("face.blank.standard", "Blank standard face baseline", "Advanced face bases",
                "Built-in LEGOface defaults; use only when its compiled zones match the result you need.",
                "/Game/Characters/Materials/M_Masters/LEGOface/MI_LEGOface-Defaults", StandardFaceMesh,
                advanced: true,
                guidance: "Clone-only baseline. Batcomputer cannot enable an absent static face zone, so start from a feature-bearing donor for an expressive face."),
            Face("face.blank.superhero", "Blank superhero face baseline", "Advanced face bases",
                "Built-in defaults for the superhero face mesh.",
                "/Game/Characters/Materials/M_Masters/LEGOface/MI_LEGOface-Defaults_Superhero", SuperheroFaceMesh,
                advanced: true),

            new()
            {
                Id = "special.thin-translucent",
                DisplayName = "Thin translucent attachment",
                Category = "Special / advanced",
                Summary = "Special translucent branch used by thin attachment geometry.",
                Guidance = "Clone-only specialist donor. Confirm sorting, opacity, and geometry in-game.",
                Advanced = true,
                AllowedTargetKinds = Kinds(TargetKinds.Accessory),
                Outputs = new[] { One("/Game/Characters/Attachments/Torso/BrickBody/MI_BrickBody_Transcluency") },
            },
            new()
            {
                Id = "special.translucent-emissive-overlay",
                DisplayName = "Translucent emissive overlay",
                Category = "Special / advanced",
                Summary = "Mr. Freeze translucent overlay donor with its compiled emissive/translucent path.",
                Guidance = "Specialist template; use it only on an overlay mesh designed for translucent sorting.",
                Advanced = true,
                AllowedTargetKinds = Kinds(TargetKinds.Accessory),
                Outputs = new[] { One("/Game/Characters/Attachments/Hat/MrFreeze_BatmanAndRobin/MI_HAT_MrFreeze_TranslucentOverlay") },
            },
            new()
            {
                Id = "special.chrome-gold-cutscene",
                DisplayName = "Chrome / gold body (cutscene only)",
                Category = "Special / advanced",
                Summary = "Gold-specific body donor with metallic and emissive cheats.",
                Guidance = "This game material is cutscene-only and cannot be used as the gameplay half of a normal body pair.",
                Advanced = true,
                AllowedTargetKinds = Kinds(TargetKinds.Body),
                Outputs = new[] { One("/Game/Characters/Minifig/Batman/Material/MI_Batman_ChromeGold20Anniversary_CUT", "cutscene only") },
            },
            CowlEyes("cowl-eyes.hollow", "Hollow cowl eyes", "/Game/Characters/Materials/MI_Instances/EoM/Controller/MI_BatmanCowlEyes_Hollow"),
            CowlEyes("cowl-eyes.molded", "Molded cowl eyes", "/Game/Characters/Materials/MI_Instances/EoM/Controller/MI_BatmanCowlEyes_Molded"),
            CowlEyes(
                "cowl-eyes.black",
                "Black cowl eyes",
                "/Game/Characters/Materials/MI_Instances/EoM/Controller/MI_Cowl_BlackEyes",
                enabled: false,
                disabledReason: "The built-in leaf material has no texture, colour, or scalar values to edit. Use Hollow cowl eyes or Molded cowl eyes as an editable starting point."),
        };

        static Recipe Face(
            string id,
            string name,
            string category,
            string summary,
            string donor,
            string mesh,
            bool advanced = false,
            string guidance = "Hide existing layers with the face helpers. Adding a feature absent from this donor may require a different template because face zones are static permutations.") => new()
            {
                Id = id,
                DisplayName = name,
                Category = category,
                Summary = summary,
                Guidance = guidance,
                IsFace = true,
                Advanced = advanced,
                AllowedTargetKinds = Kinds(TargetKinds.Face),
                CompatibleMeshPackagePaths = Meshes(mesh),
                Outputs = new[] { One(donor) },
            };

        static Recipe CowlEyes(
            string id,
            string name,
            string donor,
            bool enabled = true,
            string disabledReason = "") => new()
        {
            Id = id,
            DisplayName = name,
            Category = "Cowl eyes",
            Summary = "Small cowl-eye material with an editable eye treatment.",
            Guidance = "Advanced clone-only option for a cowl eye material slot; this is not a printed LEGO face material.",
            Advanced = true,
            Enabled = enabled,
            DisabledReason = disabledReason,
            AllowedTargetKinds = Kinds(TargetKinds.Accessory),
            Outputs = new[] { One(donor, "gameplay only") },
        };
    }

    private static string ClassifyTarget(Target target)
    {
        var text = $"{target.Component} {target.Label} {target.MeshPackagePath}";
        if (text.Contains("LEGOface", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("FaceTex", StringComparison.OrdinalIgnoreCase) ||
            target.Component.Equals("Face", StringComparison.OrdinalIgnoreCase))
        {
            return TargetKinds.Face;
        }
        if (target.Component.Equals("CharacterMesh0", StringComparison.OrdinalIgnoreCase) ||
            target.Label.Equals("Body", StringComparison.OrdinalIgnoreCase))
        {
            return TargetKinds.Body;
        }
        if (text.Contains("cape", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("glid", StringComparison.OrdinalIgnoreCase))
        {
            return TargetKinds.Cape;
        }
        if (!string.IsNullOrWhiteSpace(target.Component))
        {
            return TargetKinds.Accessory;
        }
        return TargetKinds.Unknown;
    }

    private static string Normalize(string? path) => UnrealPathUtil.NormalizePackagePath(path);

    /// <summary>
    /// Inspector data sometimes contains the complete mesh package and sometimes only the
    /// exported asset name (for example <c>SK_LEGOface</c>). Asset-name equality is still an
    /// exact mesh-family match; importantly, it does not collapse SK_LEGOface, the Joker89
    /// mesh, and the Superhero mesh into one family.
    /// </summary>
    private static bool MeshMatches(string requiredMesh, string targetMesh)
    {
        var required = Normalize(requiredMesh);
        var target = Normalize(targetMesh);
        if (required.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var requiredName = UnrealPathUtil.AssetName(required);
        var targetName = UnrealPathUtil.AssetName(target);
        return !string.IsNullOrWhiteSpace(requiredName) &&
               requiredName.Equals(targetName, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeMeshes(IEnumerable<string> meshes) => string.Join(", ", meshes
        .Select(UnrealPathUtil.AssetName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase));
}
