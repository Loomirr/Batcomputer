namespace Batcomputer;

/// <summary>
/// Keeps the visual-base and gameplay-donor rules in one place. A cutscene can
/// always be selected for its look; only the playable donor is constrained by
/// runtime requirements.
/// </summary>
public static class BaseEligibilityService
{
    public sealed record Result(bool IsReady, string Detail, string VisualPackage, string GameplayDonorPackage);

    public static bool IsVisualCharacterPackage(string? packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath ?? "");
        if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ||
            !package.Contains("/Characters/", StringComparison.OrdinalIgnoreCase) ||
            package.Contains("/BP_Master/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = UnrealPathUtil.AssetName(package);
        if (!name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !new[]
        {
            "Archetype", "_ED", "_Inst", "HoverData", "Projectile", "Weapon",
            "_Data", "Upgrades", "_Ability", "Effect"
        }.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCutsceneVisualPackage(string? packagePath) =>
        IsVisualCharacterPackage(packagePath) &&
        UnrealPathUtil.AssetName(UnrealPathUtil.NormalizePackagePath(packagePath ?? ""))
            .Contains("_Cutscene", StringComparison.OrdinalIgnoreCase);

    public static bool IsGameplayDonorPackage(string? packagePath) =>
        IsVisualCharacterPackage(packagePath) &&
        UnrealPathUtil.AssetName(UnrealPathUtil.NormalizePackagePath(packagePath ?? ""))
            .EndsWith("_Playable", StringComparison.OrdinalIgnoreCase);

    public static string CharacterStem(string? packagePath)
    {
        var stem = UnrealPathUtil.AssetName(UnrealPathUtil.NormalizePackagePath(packagePath ?? ""));
        if (stem.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[3..];
        }

        foreach (var suffix in new[] { "_Default_Cutscene", "_Cutscene", "_Playable" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^suffix.Length];
            }
        }

        return stem;
    }

    public static SuitBaseProfile CreateProfile(string? visualBasePackage, string? gameplayDonorPackage)
    {
        var visual = UnrealPathUtil.NormalizePackagePath(visualBasePackage ?? "");
        var gameplay = UnrealPathUtil.NormalizePackagePath(gameplayDonorPackage ?? "");
        var visualOk = IsVisualCharacterPackage(visual);
        var gameplayOk = IsGameplayDonorPackage(gameplay);
        var catalog = GameDataService.Instance;

        return new SuitBaseProfile
        {
            VisualBasePackage = visual,
            VisualBaseKind = IsCutsceneVisualPackage(visual) ? "cutscene" :
                IsGameplayDonorPackage(visual) ? "playable" : "character",
            VisualFamily = catalog.FamilyForBasePath(visual)?.Name ?? "",
            GameplayDonorPackage = gameplay,
            GameplayFamily = catalog.FamilyForBasePath(gameplay)?.Name ?? "",
            Eligibility = !visualOk ? "missing-visual" : !gameplayOk ? "missing-gameplay-donor" : "ready",
            EligibilityDetail = !visualOk
                ? "Choose a character Blueprint or cutscene as the visual base."
                : !gameplayOk
                    ? "Choose a real _Playable Blueprint as the gameplay donor."
                    : "Visual base and gameplay donor are ready."
        };
    }

    public static Result Evaluate(string? visualBasePackage, string? gameplayDonorPackage)
    {
        var profile = CreateProfile(visualBasePackage, gameplayDonorPackage);
        return new Result(
            profile.Eligibility.Equals("ready", StringComparison.OrdinalIgnoreCase),
            profile.EligibilityDetail,
            profile.VisualBasePackage,
            profile.GameplayDonorPackage);
    }

    public static Result Evaluate(NativeSuitProject? project)
    {
        if (project is null)
        {
            return new Result(false, "Create or open a suit before selecting a base.", "", "");
        }

        var visual = project.BaseProfile?.VisualBasePackage;
        if (string.IsNullOrWhiteSpace(visual))
        {
            visual = project.VisualCutsceneSourceTemplate?.PackagePath ??
                     project.VisualSourceTemplate?.PackagePath ??
                     project.CutsceneTemplate?.PackagePath;
        }

        var gameplay = project.BaseProfile?.GameplayDonorPackage;
        if (string.IsNullOrWhiteSpace(gameplay))
        {
            gameplay = project.PlayableTemplate?.PackagePath;
        }

        return Evaluate(visual, gameplay);
    }
}
