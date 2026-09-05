namespace Batcomputer;

/// <summary>
/// Identifies Blueprint construction-script components that belong to the playable gameplay shell,
/// not to the character's swappable appearance. These nodes can sit beside Head/Cape/etc. in the
/// SCS graph, but removing them as though they were cosmetic parts disables runtime behavior.
/// </summary>
internal static class GameplayShellComponentPolicy
{
    private static readonly HashSet<string> RequiredComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        // Resolves and manages the authored character-asset presentation used by playable flows,
        // including in-level character/suit presentation.
        "TtCharacterAssetMinion",

        // Owns the playable's Wwise dialogue voice identity. Removing its SCS node makes an
        // otherwise working suit silent.
        "WubDialogueVoiceActor",
    };

    internal static IReadOnlyCollection<string> RequiredComponentNames => RequiredComponents;

    internal static bool IsRequired(string? componentOrRemovalKey)
    {
        var component = ComponentName(componentOrRemovalKey);
        return RequiredComponents.Contains(component);
    }

    internal static string ComponentName(string? componentOrRemovalKey)
    {
        var component = (componentOrRemovalKey ?? "").Trim();
        var colon = component.LastIndexOf(':');
        if (colon > 0)
        {
            component = component[..colon];
        }

        const string generatedSuffix = "_GEN_VARIABLE";
        if (component.EndsWith(generatedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            component = component[..^generatedSuffix.Length];
        }

        // Treat generated duplicate names as the same gameplay node (for example,
        // WubDialogueVoiceActor_2). A cosmetic component cannot safely borrow these identities.
        var underscore = component.LastIndexOf('_');
        if (underscore > 0 &&
            underscore < component.Length - 1 &&
            component[(underscore + 1)..].All(char.IsDigit))
        {
            component = component[..underscore];
        }

        return component;
    }

    internal static bool IsLegacyAutomaticRemoval(NativeSuitRequirement? requirement)
    {
        if (requirement is null ||
            !requirement.Kind.Equals("remove-component", StringComparison.OrdinalIgnoreCase) ||
            !IsRequired(requirement.TargetComponent))
        {
            return false;
        }

        // These exact declarations were produced by the old visual-base cleanup paths. Do not
        // erase an intentionally hand-authored declaration silently; validation rejects that one
        // with an actionable error instead.
        return requirement.Notes.StartsWith("Auto-hidden on base select:", StringComparison.OrdinalIgnoreCase) ||
               requirement.Notes.StartsWith("Auto-hidden on visual-base select:", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> RemoveLegacyAutomaticRemovals(NativeSuitProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var removed = project.Requirements
            .Where(IsLegacyAutomaticRemoval)
            .Select(requirement => ComponentName(requirement.TargetComponent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(component => component, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (removed.Count > 0)
        {
            project.Requirements.RemoveAll(requirement => IsLegacyAutomaticRemoval(requirement));
        }
        return removed;
    }
}
