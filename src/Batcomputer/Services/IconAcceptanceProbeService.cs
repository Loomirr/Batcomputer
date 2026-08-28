namespace Batcomputer;

/// <summary>Builds a disposable suit with one native 256px selector icon and three native 512px character portraits.</summary>
public sealed class IconAcceptanceProbeService
{
    public sealed class Result
    {
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string SuitProjectPath { get; set; } = "";
        public string ModProjectPath { get; set; } = "";
    }

    public Result Create(string projectRoot, string sourceProjectPath, string modId)
    {
        var result = new Result();
        try
        {
            modId = ModProjectService.DeriveModId(modId);
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("A test mod id is required.");
            var suits = new SuitProjectService(projectRoot);
            var source = suits.LoadProject(sourceProjectPath) ?? throw new FileNotFoundException("The source suit could not be read.", sourceProjectPath);
            if (source.PlayableTemplate is null || source.CutsceneTemplate is null || source.DcmdTemplate is null)
                throw new InvalidOperationException("The source suit needs a complete playable, cutscene, and DCMD base.");
            var marker = "\\LEGOBatmanLotDK\\Content\\";
            var donorPath = source.PlayableTemplate.Uasset;
            var markerIndex = donorPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                AppSettings.Current.ExtractedContentRoot = donorPath[..(markerIndex + marker.Length - 1)];
            }
            var extractedCharacterIcon = Path.Combine(AppSettings.Current.EffectiveExtractedContentRoot(), "UI", "Icons", "Characters", "T_UI_IconChar_Batman_TheBatman2025_Menu_BCA.uasset");
            var localDonorRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "IconDonorExtract", "LEGOBatmanLotDK", "Content");
            if (!File.Exists(extractedCharacterIcon) && File.Exists(Path.Combine(localDonorRoot, "UI", "Icons", "Characters", "T_UI_IconChar_Batman_TheBatman2025_Menu_BCA.uasset")))
            {
                AppSettings.Current.ExtractedContentRoot = localDonorRoot;
            }
            if (!TextureCookTemplateService.NormalizeNativeSuitIconTemplate(projectRoot) ||
                !TextureCookTemplateService.NormalizeNativeCharacterIconTemplate(projectRoot))
                throw new InvalidOperationException("The native icon donors are missing. Refresh game assets and retry.");

            var slotId = "icon_acceptance_" + modId.ToLowerInvariant();
            var suitPath = suits.ProjectPathForSlot(slotId);
            var mods = new ModProjectService(projectRoot);
            var modPath = Path.Combine(mods.ModOutputRoot, modId + ".native-suit-mod-project.json");
            if (File.Exists(suitPath) || File.Exists(modPath))
                throw new InvalidOperationException($"Test id '{modId}' already exists; choose a new id instead of overwriting it.");

            var outputRoot = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "IconAcceptance", modId);
            var contentRoot = Path.Combine(outputRoot, "Cooked", "LEGOBatmanLotDK", "Content");
            var project = new NativeSuitProject
            {
                SlotId = slotId,
                DisplayName = "UI Icon Format Acceptance",
                Description = "Checks the 256px suit tile plus 512px menu, left, and right character portraits.",
                PawnTag = $"Pawns.Playable.Batman.{modId}",
                ProgressTag = source.ProgressTag,
                PackageBaseName = modId + "_P",
                TargetPackages = new TargetPackages
                {
                    Playable = $"/Game/Mods/{modId}/Characters/BP_{modId}_Playable",
                    Cutscene = $"/Game/Mods/{modId}/Characters/BP_{modId}_Cutscene",
                    Dcmd = $"/Game/Mods/{modId}/Characters/DA_DCMD_{modId}_Playable"
                },
                PlayableTemplate = source.PlayableTemplate,
                CutsceneTemplate = source.CutsceneTemplate,
                DcmdTemplate = source.DcmdTemplate,
                VisualSourceTemplate = source.VisualSourceTemplate ?? source.PlayableTemplate,
                VisualCutsceneSourceTemplate = source.VisualCutsceneSourceTemplate ?? source.CutsceneTemplate,
                BaseProfile = source.BaseProfile
            };
            var specs = new[]
            {
                ("Menu portrait", "Home.png", "Character icon", "ui-character-512-bc7", TextureCookTemplateService.NativeCharacterIconTemplateFolder, "T_Icon_Menu"),
                ("Suit selector", "Suits.png", "Suit selector icon", "ui-suit-256-bc7", TextureCookTemplateService.NativeSuitIconTemplateFolder, "T_Icon_Suit"),
                ("Left portrait", "Parts.png", "Character icon", "ui-character-512-bc7", TextureCookTemplateService.NativeCharacterIconTemplateFolder, "T_Icon_Left"),
                ("Right portrait", "Materials.png", "Character icon", "ui-character-512-bc7", TextureCookTemplateService.NativeCharacterIconTemplateFolder, "T_Icon_Right"),
            };
            foreach (var spec in specs)
            {
                var sourcePng = Path.Combine(projectRoot, "Assets", spec.Item2);
                if (!File.Exists(sourcePng)) throw new FileNotFoundException("Bundled test artwork is missing.", sourcePng);
                var package = $"/Game/Mods/{modId}/Textures/{spec.Item6}";
                var template = TextureCookTemplateService.TemplateJsonPath(projectRoot, spec.Item5);
                var cooked = new TextureCookService(projectRoot).Cook(new TextureCookService.Request
                {
                    SourceImagePath = sourcePng, TemplateJsonPath = template, OutputContentRoot = contentRoot,
                    OutputPackagePath = package, Bc7Quality = "high"
                });
                if (!cooked.Status.Equals("created", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Could not cook {spec.Item1}: {cooked.Error ?? cooked.Status}");
                project.GeneratedTextures.Add(new GeneratedTextureEntry
                {
                    DisplayName = spec.Item1, Kind = spec.Item3, CookProfile = spec.Item4,
                    CookWidth = cooked.Width, CookHeight = cooked.Height, CookPixelFormat = cooked.PixelFormat,
                    SourcePng = sourcePng, PackagePath = package, ObjectPath = package + "." + spec.Item6,
                    TemplateJson = template, OutputRoot = outputRoot, CreatedUtc = DateTime.UtcNow.ToString("O")
                });
            }
            project.IconMenu = project.GeneratedTextures[0].PackagePath;
            project.IconSuit = project.GeneratedTextures[1].PackagePath;
            project.IconLeft = project.GeneratedTextures[2].PackagePath;
            project.IconRight = project.GeneratedTextures[3].PackagePath;
            result.SuitProjectPath = suits.SaveProject(project);
            result.ModProjectPath = mods.SaveMod(new NativeSuitModProject
            {
                ModId = modId, DisplayName = project.DisplayName, Description = project.Description,
                Suits = [new ModSuitEntry { SuitProjectPath = mods.MakeRelativeSuitProjectPath(result.SuitProjectPath), SuitId = slotId, Enabled = true }]
            });
            result.Status = "created";
        }
        catch (Exception ex) { result.Status = "error"; result.Error = ex.Message; }
        return result;
    }
}
