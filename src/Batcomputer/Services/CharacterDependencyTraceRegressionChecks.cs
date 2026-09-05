namespace Batcomputer;

/// <summary>Portable contract checks for the extraction-derived character trace.</summary>
internal static class CharacterDependencyTraceRegressionChecks
{
    internal sealed record Result(bool Passed, string Description);

    public static IReadOnlyList<Result> Run()
    {
        var results = new List<Result>();
        void Check(bool condition, string description) => results.Add(new Result(condition, description));

        Check(
            CharacterDependencyTraceService.IsHumanoidPlayablePackageForTest(
                "/DLC_BeyondPack/Characters/Smallfig/Robin/BP_RobinDickGrayson_Beyond_Playable") &&
            CharacterDependencyTraceService.IsHumanoidPlayablePackageForTest(
                "/Game/Characters/Minifig/Batman/BP_Batman_Default_Playable") &&
            !CharacterDependencyTraceService.IsHumanoidPlayablePackageForTest(
                "/Game/Characters/BP_Master/BPs_Playable/BP_CAT_Playable") &&
            !CharacterDependencyTraceService.IsHumanoidPlayablePackageForTest(
                "/Game/Characters/Creatures/RemoteKitten/BP_RemoteKitten_Playable"),
            "character trace includes Minifig/Smallfig DLC playables while excluding framework and equipment pawns");

        Check(
            CharacterDependencyTraceService.IsPlayablePawnTagForTest(
                "Pawns.Playable.BruceWayneChild.Dressed") &&
            CharacterDependencyTraceService.IsPlayablePawnTagForTest(
                "Pawns.Playable.ThomasWayne.Default") &&
            !CharacterDependencyTraceService.IsPlayablePawnTagForTest("Pawns.NPC.Cluemaster") &&
            CharacterDependencyTraceService.IsNullSoftReferenceForTest("None", "None") &&
            !CharacterDependencyTraceService.IsNullSoftReferenceForTest(
                "/Game/Characters/Equipment/Batarang/DA_ETA_Batarang",
                "DA_ETA_Batarang"),
            "character trace derives playable DCMDs from PawnTag and preserves soft None entries as nulls");

        Check(
            CharacterDependencyTraceService.SelectNearestDependencyForTest(
                new IReadOnlyList<string>[]
                {
                    Array.Empty<string>(),
                    ["/Game/Characters/Minifig/Batman/DA_DPRD_TheBatmanCharacterData"],
                    ["/Game/Characters/Minifig/BruceWayne/DA_DPRD_BruceWayneCharacterData"],
                }).Equals(
                "/Game/Characters/Minifig/Batman/DA_DPRD_TheBatmanCharacterData",
                StringComparison.OrdinalIgnoreCase),
            "character trace inherits the nearest serialized gameplay dependency from the class chain");

        Check(
            string.IsNullOrWhiteSpace(CharacterDependencyTraceService.SelectNearestDependencyForTest(
                new IReadOnlyList<string>[]
                {
                    ["/Game/A", "/Game/B"],
                    ["/Game/C"],
                })),
            "character trace fails closed when one class level exposes ambiguous dependency imports");

        var inheritedDprd = "/Game/Characters/Minifig/Batman/DA_DPRD_TheBatmanCharacterData";
        var unresolvedLocalAnchor = CharacterDependencyTraceService.SelectNearestDependencyForTest(
            new (IReadOnlyList<string> Candidates, bool HasExplicitNull, bool HasUnresolvedValue)[]
            {
                (Array.Empty<string>(), false, true),
                ([inheritedDprd], false, false),
            });
        var explicitlyClearedLocalAnchor = CharacterDependencyTraceService.SelectNearestDependencyForTest(
            new (IReadOnlyList<string> Candidates, bool HasExplicitNull, bool HasUnresolvedValue)[]
            {
                (Array.Empty<string>(), true, false),
                ([inheritedDprd], false, false),
            });
        Check(
            string.IsNullOrWhiteSpace(unresolvedLocalAnchor) &&
            string.IsNullOrWhiteSpace(explicitlyClearedLocalAnchor),
            "character trace inherits absent CDO anchors but blocks raw, wrong-typed, unresolved, and explicitly null local anchors");

        const string cacheFingerprint = "CHARACTER-TRACE-FINGERPRINT";
        Check(
            CharacterDependencyTraceService.CurrentSchemaVersion >= 4 &&
            !CharacterDependencyTraceService.CacheIdentityMatchesForTest(
                3,
                cacheFingerprint,
                cacheFingerprint) &&
            CharacterDependencyTraceService.CacheIdentityMatchesForTest(
                CharacterDependencyTraceService.CurrentSchemaVersion,
                cacheFingerprint,
                cacheFingerprint) &&
            !CharacterDependencyTraceService.CacheIdentityMatchesForTest(
                CharacterDependencyTraceService.CurrentSchemaVersion,
                cacheFingerprint,
                cacheFingerprint + "-changed"),
            "character trace rejects pre-upgrade schema-3 caches and mismatched source fingerprints");

        var valid = CertificateFixture();
        var competingCombat = CertificateFixture();
        competingCombat.CombatAbilitySetPackages.Add("/Game/Characters/Abilities/MeleeAbilities/AS_Melee_Other");
        var unresolvedRoot = CertificateFixture();
        unresolvedRoot.LayerComposite.TargetExists = false;
        var competingGrapple = CertificateFixture();
        competingGrapple.GrappleDataSetPackages.Add("/Game/Characters/Abilities/CoreAbilities/Grappling/GameplayDataSets/AS_GrappleDataOther");
        var incompleteClosure = CertificateFixture();
        incompleteClosure.IsDependencyClosureComplete = false;
        Check(
            CharacterDependencyTraceService.ProfileCertificateForTest(valid) &&
            !CharacterDependencyTraceService.ProfileCertificateForTest(competingCombat) &&
            !CharacterDependencyTraceService.ProfileCertificateForTest(unresolvedRoot) &&
            !CharacterDependencyTraceService.ProfileCertificateForTest(competingGrapple) &&
            !CharacterDependencyTraceService.ProfileCertificateForTest(incompleteClosure),
            "character profile certificate requires complete targets, exact roots, one combat controller, and at most one grapple profile");

        var certifiedProfile = CertificateFixture();
        certifiedProfile.IsStructurallyCertified = true;
        var validVariant = CertifiedVariantFixture();
        var missingEvidenceVariant = CertifiedVariantFixture();
        missingEvidenceVariant.HasSerializedPlayableDcmdEvidence = false;
        var emptyDcmdVariant = CertifiedVariantFixture();
        emptyDcmdVariant.Dcmds.Clear();
        var invalidVariant = CertifiedVariantFixture();
        invalidVariant.Diagnostics.Add(new CharacterTraceDiagnostic
        {
            Severity = CharacterTraceDiagnosticSeverity.Error,
            Code = "ambiguous-dprd",
        });
        Check(
            CharacterDependencyTraceService.VariantCertificateForTest(certifiedProfile, validVariant) &&
            !CharacterDependencyTraceService.VariantCertificateForTest(certifiedProfile, missingEvidenceVariant) &&
            !CharacterDependencyTraceService.VariantCertificateForTest(certifiedProfile, emptyDcmdVariant) &&
            !CharacterDependencyTraceService.VariantCertificateForTest(certifiedProfile, invalidVariant),
            "character variant certification requires serialized playable-DCMD evidence, a nonempty DCMD set, and no class-chain errors");

        var cliCatalog = CliCatalogFixture();
        var cleanBoundedCatalogAccepted =
            cliCatalog.ClosureDepth == CharacterTraceClosureDepth.DirectSerializedReferences &&
            !cliCatalog.TransitiveBlueprintPackageGraphsTraced &&
            cliCatalog.Dcmds.Single().HasUntracedNestedPackageGraphs &&
            CharacterDependencyTraceService.IsCatalogUsableForCli(cliCatalog);
        var diagnosticOwners = new List<List<CharacterTraceDiagnostic>>
        {
            cliCatalog.Diagnostics,
            cliCatalog.PlayableVariants.Single().Diagnostics,
            cliCatalog.Dcmds.Single().Diagnostics,
            cliCatalog.PlayableDcmds.Single().Diagnostics,
            cliCatalog.GameplayProfiles.Single().Diagnostics,
            cliCatalog.AbilitySets.Single().Diagnostics,
            cliCatalog.EquipmentTypes.Single().Diagnostics,
            cliCatalog.EquipmentDefinitions.Single().Diagnostics,
            cliCatalog.Upgrades.Single().Diagnostics,
            cliCatalog.AnimationComposites.Single().Diagnostics,
        };
        var everyNestedErrorRejected = diagnosticOwners.All(diagnostics =>
        {
            diagnostics.Add(Error("cli-regression"));
            var rejected = !CharacterDependencyTraceService.IsCatalogUsableForCli(cliCatalog);
            diagnostics.Clear();
            return rejected;
        });
        var noCertifiedVariant = CliCatalogFixture();
        noCertifiedVariant.PlayableVariants.Single().IsStructurallyCertified = false;
        Check(
            cleanBoundedCatalogAccepted &&
            everyNestedErrorRejected &&
            !CharacterDependencyTraceService.IsCatalogUsableForCli(noCertifiedVariant),
            "character trace CLI accepts explicit bounded direct closure but rejects every nested error and zero certified playables");

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "Batcomputer-character-trace-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = Path.Combine(fixtureRoot, "LEGOBatmanLotDK", "Content");
            var pluginContent = Path.Combine(
                fixtureRoot,
                "LEGOBatmanLotDK",
                "Plugins",
                "GameFeatures",
                "DLC_TestPack",
                "Content");
            Directory.CreateDirectory(Path.Combine(content, "Characters"));
            Directory.CreateDirectory(Path.Combine(pluginContent, "Characters"));
            var mappings = Path.Combine(fixtureRoot, "test.usmap");
            var baseAsset = Path.Combine(content, "Characters", "BP_Base_Playable.uasset");
            var dlcAsset = Path.Combine(pluginContent, "Characters", "BP_Dlc_Playable.uasset");
            File.WriteAllText(mappings, "mapping");
            File.WriteAllText(baseAsset, "base");
            File.WriteAllText(dlcAsset, "dlc");
            var before = CharacterDependencyTraceService.ComputeSourceFingerprintForTest(content, mappings);
            File.AppendAllText(dlcAsset, "-changed");
            var after = CharacterDependencyTraceService.ComputeSourceFingerprintForTest(content, mappings);
            Check(
                !before.Equals(after, StringComparison.Ordinal),
                "character trace cache fingerprint changes when an installed DLC asset changes");
        }
        catch (Exception ex)
        {
            Check(false, "character trace cache fingerprint regression threw: " + ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the uniquely named regression fixture.
            }
        }

        return results;
    }

    private static CharacterGameplayProfileTrace CertificateFixture() => new()
    {
        Id = "/Game/Profile",
        RuntimeData = Existing("/Game/DA_DPRD_Profile"),
        MontageComposite = Existing("/Game/MAS_Char_Profile"),
        LayerComposite = Existing("/Game/LAS_Char_Profile"),
        OrderedAbilitySets =
        [
            Existing("/Game/AS_PlayableCoreAbilitySet"),
            Existing("/Game/AS_Melee_Profile"),
        ],
        HasPlayableCore = true,
        IsDependencyClosureComplete = true,
        CombatAbilitySetPackages = ["/Game/AS_Melee_Profile"],
        GrappleDataSetPackages = ["/Game/AS_GrappleDataGeneric"],
    };

    private static CharacterPlayableVariantTrace CertifiedVariantFixture() => new()
    {
        PackagePath = "/Game/BP_Profile_Playable",
        IsHumanoid = true,
        HasSerializedPlayableDcmdEvidence = true,
        Dcmds = [Existing("/Game/DA_DCMD_Profile_Playable")],
        IsDependencyClosureComplete = true,
    };

    private static CharacterDependencyTraceCatalog CliCatalogFixture()
    {
        var profile = CertificateFixture();
        profile.IsPlayerProfile = true;
        profile.IsStructurallyCertified = true;
        profile.HasUntracedNestedPackageGraphs = true;
        var variant = CertifiedVariantFixture();
        variant.IsStructurallyCertified = true;
        return new CharacterDependencyTraceCatalog
        {
            SchemaVersion = CharacterDependencyTraceService.CurrentSchemaVersion,
            SourceFingerprint = "CLI-REGRESSION",
            ClosureDepth = CharacterTraceClosureDepth.DirectSerializedReferences,
            TransitiveBlueprintPackageGraphsTraced = false,
            PlayableVariants = [variant],
            Dcmds =
            [
                new CharacterDcmdTrace
                {
                    PackagePath = "/Game/DA_DCMD_Profile_Playable",
                    IsReadable = true,
                    IsDependencyClosureComplete = true,
                    HasUntracedNestedPackageGraphs = true,
                },
            ],
            PlayableDcmds = [new CharacterDcmdTrace { PackagePath = "/Game/DA_DCMD_Profile_Playable" }],
            GameplayProfiles = [profile],
            AbilitySets = [new CharacterAbilitySetTrace { PackagePath = "/Game/AS_Profile", IsReadable = true }],
            EquipmentTypes = [new CharacterEquipmentTypeTrace { PackagePath = "/Game/DA_ETA_Profile", IsReadable = true }],
            EquipmentDefinitions = [new CharacterEquipmentDefinitionTrace { PackagePath = "/Game/DA_ED_Profile", IsReadable = true }],
            Upgrades = [new CharacterUpgradeTrace { PackagePath = "/Game/DA_UF_Profile", IsReadable = true }],
            AnimationComposites = [new CharacterAnimationCompositeTrace { PackagePath = "/Game/MAS_Profile", IsReadable = true }],
        };
    }

    private static CharacterTraceDiagnostic Error(string code) => new()
    {
        Severity = CharacterTraceDiagnosticSeverity.Error,
        Code = code,
    };

    private static CharacterTraceReference Existing(string packagePath) => new()
    {
        PackagePath = packagePath,
        TargetExists = true,
        Evidence = CharacterTraceEvidenceKind.SerializedProperty,
    };
}
