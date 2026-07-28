namespace Batcomputer;

public static class PatchPlanService
{
    public static NativeSuitProject CreateProjectFromRecommendedPlan(RecommendedDonorPlan plan)
    {
        var playable = plan.ThomasSource ?? plan.PlayableDonor;
        var cutscene = plan.ThomasCutsceneSource ?? plan.CutsceneDonor;
        var project = new NativeSuitProject
        {
            SlotId = plan.SlotId,
            DisplayName = "Thomas Wayne",
            Description = "Native suit prototype generated from template donors.",
            TargetPackages = plan.TargetPackages,
            PlayableTemplate = playable,
            CutsceneTemplate = cutscene,
            DcmdTemplate = plan.ThomasDcmdSource ?? plan.DcmdDonor,
            VisualSourceTemplate = plan.ThomasSource,
            VisualCutsceneSourceTemplate = plan.ThomasCutsceneSource,
            BaseProfile = BaseEligibilityService.CreateProfile(cutscene?.PackagePath, playable?.PackagePath),
            StaticMeshComponentShapeTemplate = plan.StaticMeshComponentShapeDonor
        };

        project.Requirements.Add(new NativeSuitRequirement
        {
            Id = "torso2",
            Kind = "component-template",
            SourcePackage = plan.PlayableDonor?.PackagePath ?? "",
            TargetComponent = "Torso2",
            Notes = "Transplant Batman Absolute's native Torso2 SkeletalMeshComponentBudgeted setup into the Thomas-based generated playable/cutscene."
        });
        project.Requirements.Add(new NativeSuitRequirement
        {
            Id = "slickback-hair",
            Kind = "static-mesh-component-template",
            SourcePackage = plan.ThomasSource?.PackagePath ?? "",
            TargetComponent = "Head",
            Notes = "Already present when ThomasWayne Casual is used as the generated playable/cutscene donor."
        });
        project.Requirements.Add(new NativeSuitRequirement
        {
            Id = "dcmd-soft-paths",
            Kind = "metadata",
            SourcePackage = plan.DcmdDonor?.PackagePath ?? "",
            TargetComponent = "DinnerCharacterMetaData",
            Notes = "Patch Pawn, MenuActor, CinematicsActor, PawnTag, ProgressTag, UIMetaData, DisplayName."
        });
        project.Requirements.Add(new NativeSuitRequirement
        {
            Id = "equipment",
            Kind = "metadata-or-component-data",
            SourcePackage = plan.ThomasSource?.PackagePath ?? "",
            TargetComponent = "equipment",
            Notes = "Later pass: identify native equipment container fields and patch Batarang/BatClaw replacements."
        });

        return project;
    }

    public static NativeSuitPatchPlan CreatePatchPlan(NativeSuitProject project)
    {
        var plan = new NativeSuitPatchPlan
        {
            Project = project
        };

        var order = 1;
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "copy",
            Source = project.PlayableTemplate?.PackagePath ?? "",
            Target = project.TargetPackages.Playable,
            Action = "Copy playable donor package pair to target package path.",
            Notes = "This creates the file shell; internal package/class/object names still need UAssetAPI rewriting."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "copy",
            Source = project.CutsceneTemplate?.PackagePath ?? "",
            Target = project.TargetPackages.Cutscene,
            Action = "Copy cutscene donor package pair to target package path.",
            Notes = "Use paired cutscene donor so cinematic actor has matching component layout."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "copy",
            Source = project.DcmdTemplate?.PackagePath ?? "",
            Target = project.TargetPackages.Dcmd,
            Action = "Copy DCMD donor package pair to target package path.",
            Notes = "Patch metadata soft class paths to point at generated playable/cutscene classes."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "uassetapi-name-map",
            Source = project.PlayableTemplate?.Stem ?? "",
            Target = "BP_Batman_Thomas_Playable / BP_Batman_Thomas_Playable_C",
            Action = "Rewrite playable package name, generated class name, SimpleConstructionScript owner paths, component template object names.",
            Notes = "This is the first UAssetAPI write milestone."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "uassetapi-name-map",
            Source = project.CutsceneTemplate?.Stem ?? "",
            Target = "BP_Batman_Thomas_Cutscene / BP_Batman_Thomas_Cutscene_C",
            Action = "Rewrite cutscene package name, generated class name, SCS owner paths, component template object names.",
            Notes = "Cutscene class must be a real generated class path that F8/F9 can resolve."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "uassetapi-metadata",
            Source = project.DcmdTemplate?.PackagePath ?? "",
            Target = project.TargetPackages.Dcmd,
            Action = "Patch DCMD Pawn/MenuActor/CinematicsActor soft paths and PawnTag/ProgressTag/DisplayName.",
            Notes = "Target release bridge remains TheBatman2025 later; command-only tests can keep ThomasWayne.Default bridge."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "component-data",
            Source = project.PlayableTemplate?.PackagePath ?? "",
            Target = "Torso2",
            Action = "Transplant Absolute Torso2 data into generated playable/cutscene.",
            Notes = "Thomas is now the base donor; Absolute should only provide the spiked torso component/template."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "component-data",
            Source = project.VisualSourceTemplate?.PackagePath ?? "",
            Target = "Head SlickBack hair",
            Action = "Preserve Thomas SlickBack static component fields.",
            Notes = "This should be present automatically when using ThomasWayne Casual as the playable/cutscene donor."
        });
        plan.Steps.Add(new PatchStep
        {
            Order = order++,
            Category = "packaging",
            Source = "patched stage",
            Target = "Io Store trio",
            Action = "Run retoc to-zen after UAssetAPI writes succeed.",
            Notes = "Then test with existing F8/F9 command-only bridge."
        });

        return plan;
    }
}
