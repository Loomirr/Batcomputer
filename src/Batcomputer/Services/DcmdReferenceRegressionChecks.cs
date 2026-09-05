namespace Batcomputer;

/// <summary>Dependency-free guards for native actor/cutscene identity decisions.</summary>
internal static class DcmdReferenceRegressionChecks
{
    public static void Run(ICollection<string> failures, TextWriter output)
    {
        Check(NativeMetadataDonorService.CanonicalProgressTag("GameProgress.Definitions.Characters.Batman.Batman") ==
                  "GameProgress.Definitions.Characters.Batman.TheBatman2025" &&
              NativeMetadataDonorService.CanonicalProgressTag("GameProgress.Definitions.Characters.Batman.1989") ==
                  "GameProgress.Definitions.Characters.Batman.1989" &&
              NativeMetadataDonorService.CanonicalProgressTag("MyMod.Unlock.Batman") == "MyMod.Unlock.Batman" &&
              NativeMetadataDonorService.CanonicalProgressTag("") == "",
            "retired Batman donor resolves to the game's defined TheBatman2025 unlock; other and custom tags stay unchanged",
            failures, output);
        const string serializedRobinCutscene =
            "/Game/Characters/Minifig/Robin_DickGrayson/BP_Robin_1966_Default_Cutscene";
        const string guessedRobinCutscene =
            "/Game/Characters/Minifig/Robin_DickGrayson/BP_RobinDickGrayson_1966_Cutscene";

        Check(
            NativeMetadataDonorService.PreferSerializedActorPackage(
                    serializedRobinCutscene,
                    guessedRobinCutscene)
                .Equals(serializedRobinCutscene, StringComparison.OrdinalIgnoreCase),
            "DCMD-authored cinematic actor wins over a filename-guessed Robin cutscene",
            failures,
            output);

        Check(
            PawnTagConfigService.CharacterScopeForPawnTag(
                    "Pawns.Playable.CatWoman.CustomPurple")
                .Equals("Pawns.Playable.CatWoman", StringComparison.Ordinal) &&
            PawnTagConfigService.CharacterScopeForPawnTag(
                    "Pawns.Playable.RobinDickGrayson.Custom")
                .Equals("Pawns.Playable.RobinDickGrayson", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(
                PawnTagConfigService.CharacterScopeForPawnTag("Equipment.Batarang")),
            "native manifests declare a stable per-character scope for non-Batman suits",
            failures,
            output);

        Check(
            PawnTagConfigService.CanonicalizeCharacterOwner(
                    "Pawns.Playable.Catwoman.CustomPurple",
                    "Pawns.Playable.CatWoman.Default_Mask")
                .Equals("Pawns.Playable.CatWoman.CustomPurple", StringComparison.Ordinal) &&
            PawnTagConfigService.CanonicalizeCharacterOwner(
                    "Pawns.Playable.Custom.CustomPurple",
                    "Pawns.Playable.CatWoman.Default_Mask")
                .Equals("Pawns.Playable.Custom.CustomPurple", StringComparison.Ordinal),
            "donor PawnTag evidence repairs owner casing without changing family or suit leaf",
            failures,
            output);

        Check(
            PawnTagConfigService.CharacterOwnerMismatchError(
                    "Pawns.Playable.Catwoman.CustomPurple",
                    "Pawns.Playable.CatWoman.Default_Mask") is null &&
            PawnTagConfigService.CharacterOwnerMismatchError(
                    "Pawns.Playable.RobinDickGrayson.CustomPurple",
                    "Pawns.Playable.CatWoman.Default_Mask") is { Length: > 0 },
            "packaging accepts donor owner casing differences but rejects cross-character PawnTags",
            failures,
            output);

        Check(
            MainForm.SuggestPawnTagForTest(
                    new NativeSuitProject
                    {
                        DisplayName = "Cold Cutscene",
                        BaseProfile = new SuitBaseProfile { GameplayFamily = "Catwoman" },
                    },
                    "Pawns.Playable.CatWoman.Default_Mask")
                .Equals("Pawns.Playable.CatWoman.ColdCutscene", StringComparison.Ordinal),
            "new PawnTag suggestions preserve the exact donor character-owner casing",
            failures,
            output);

        Check(
            NativeMetadataDonorService.PreferSerializedActorPackage(
                    "",
                    guessedRobinCutscene)
                .Equals(guessedRobinCutscene, StringComparison.OrdinalIgnoreCase),
            "legacy metadata without a serialized cinematic actor keeps its selected cutscene template",
            failures,
            output);
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
