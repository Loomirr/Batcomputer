namespace Batcomputer;

/// <summary>
/// Builds the read-only hierarchy shown by <see cref="AnimationExplorerForm"/>. Keeping the
/// hierarchy independent from WinForms makes search and the safety rules around applying an
/// imported sequence deterministic and regression-testable.
/// </summary>
internal static class AnimationExplorerSnapshotBuilder
{
    internal static AnimationExplorerSnapshot Build(
        NativeSuitProject? project,
        AnimLibrary library,
        string? search = null) =>
        Build(project, library, characterGraph: null, search);

    internal static AnimationExplorerSnapshot Build(
        NativeSuitProject? project,
        AnimLibrary library,
        CharacterAnimationSnapshot? characterGraph,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(library);

        project ??= new NativeSuitProject
        {
            DisplayName = "No suit open",
            SlotId = "",
        };

        var roots = new List<AnimationExplorerNode>
        {
            characterGraph is null ? BuildCurrentSuit(project) : BuildCurrentCharacter(characterGraph),
            BuildImportedAnimations(library),
        };

        var query = search?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            roots = roots
                .Select(root => Filter(root, query))
                .Where(root => root is not null)
                .Cast<AnimationExplorerNode>()
                .ToList();
        }

        var imported = library.Entries.Count;
        var healthy = library.Entries.Count(CanApply);
        return new AnimationExplorerSnapshot(roots, imported, healthy);
    }

    private static AnimationExplorerNode BuildCurrentCharacter(CharacterAnimationSnapshot graph)
    {
        var montageSets = graph.Sets
            .Where(set => set.Kind == CharacterAnimationSetKind.Montage)
            .OrderBy(set => set.ParentIndex)
            .Select(BuildCharacterSet)
            .ToList();
        var layerSets = graph.Sets
            .Where(set => set.Kind == CharacterAnimationSetKind.Layer)
            .OrderBy(set => set.ParentIndex)
            .Select(BuildCharacterSet)
            .ToList();
        var locomotion = graph.LocomotionSequences
            .GroupBy(target => target.OwnerPackage, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => PackageLeaf(group.Key), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AnimationExplorerNode(
                AnimationExplorerNodeKind.Group,
                FriendlyAssetName(PackageLeaf(group.Key)),
                $"{group.Count()} sequence{Plural(group.Count())}",
                ChildNodes: group
                    .OrderBy(target => FriendlyAssetName(target.EffectiveObjectName), StringComparer.OrdinalIgnoreCase)
                    .Select(target => BuildCharacterTarget(target, slot: null))
                    .ToList()))
            .ToList();
        var diagnostics = graph.Diagnostics
            .Select(item => new AnimationExplorerNode(
                item.Severity == CharacterAnimationDiagnosticSeverity.Error
                    ? AnimationExplorerNodeKind.Warning
                    : AnimationExplorerNodeKind.Diagnostic,
                FriendlyDiagnostic(item),
                ValueOr(item.PackagePath, item.Code),
                Diagnostic: item))
            .ToList();

        var changed = graph.Sets.SelectMany(set => set.Slots).SelectMany(slot => slot.Targets)
                          .Count(target => target.IsOverridden) +
                      graph.LocomotionSequences.Count(target => target.IsOverridden);
        var total = graph.Sets.Sum(set => set.Slots.Sum(slot => slot.Targets.Count)) +
                    graph.LocomotionSequences.Count;
        var children = new List<AnimationExplorerNode>
        {
            new(
                AnimationExplorerNodeKind.Group,
                "Actions & montages",
                montageSets.Count == 0
                    ? "No readable montage sets"
                    : $"{montageSets.Count} sets • {montageSets.Sum(CountTargets)} replaceable targets",
                ChildNodes: montageSets),
            new(
                AnimationExplorerNodeKind.Group,
                "Layers & animation blueprints",
                layerSets.Count == 0
                    ? "No readable layer sets"
                    : $"{layerSets.Count} sets • {layerSets.Sum(CountTargets)} replaceable targets",
                ChildNodes: layerSets),
            new(
                AnimationExplorerNodeKind.Group,
                "Locomotion sequences",
                locomotion.Count == 0
                    ? "No readable locomotion sequences"
                    : $"{graph.LocomotionSequences.Count} sequences",
                ChildNodes: locomotion),
        };
        if (diagnostics.Count > 0)
        {
            children.Add(new AnimationExplorerNode(
                AnimationExplorerNodeKind.Group,
                "Scan notes",
                $"{diagnostics.Count} note{Plural(diagnostics.Count)}",
                ChildNodes: diagnostics));
        }

        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.Section,
            "Current character",
            $"{ValueOr(graph.GameplayFamily, "Unknown family")} • {total} targets" +
            (changed > 0 ? $" • {changed} changed" : ""),
            ChildNodes: children);
    }

    private static AnimationExplorerNode BuildCharacterSet(CharacterAnimationSetSnapshot set)
    {
        var groups = set.Slots
            .GroupBy(slot => slot.ActionTag, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => FriendlyAction(group.Key), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AnimationExplorerNode(
                AnimationExplorerNodeKind.Group,
                FriendlyAction(group.Key),
                $"{group.Sum(slot => slot.Targets.Count)} target{Plural(group.Sum(slot => slot.Targets.Count))}",
                ChildNodes: group
                    .OrderBy(slot => slot.EntryIndex)
                    .Select(BuildCharacterSlot)
                    .ToList()))
            .ToList();
        var title = FriendlyAssetName(PackageLeaf(set.EffectivePackage));
        var value = ValueOr(set.Category, set.Kind.ToString());
        if (set.IsOverridden)
        {
            value += " • family/set override";
        }
        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.CharacterSet,
            title,
            value,
            ChildNodes: groups);
    }

    private static AnimationExplorerNode BuildCharacterSlot(CharacterAnimationSlotSnapshot slot)
    {
        var context = slot.ContextTags.Count == 0
            ? "Default context"
            : string.Join(" + ", slot.ContextTags.Select(FriendlyContext));
        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.AnimationSlot,
            context,
            $"row {slot.EntryIndex + 1} • {slot.Targets.Count} target{Plural(slot.Targets.Count)}",
            CharacterSlot: slot,
            ChildNodes: slot.Targets
                .OrderBy(target => target.WeightIndex)
                .ThenBy(target => target.LayerIndex)
                .Select(target => BuildCharacterTarget(target, slot))
                .ToList());
    }

    private static AnimationExplorerNode BuildCharacterTarget(
        CharacterAnimationTargetSnapshot target,
        CharacterAnimationSlotSnapshot? slot)
    {
        var current = ValueOr(target.EffectiveObjectName, PackageLeaf(target.EffectivePackage), "Unresolved asset");
        var title = FriendlyAssetName(current);
        var kind = target.ReferenceKind switch
        {
            CharacterAnimationReferenceKind.AnimFile => "Action animation",
            CharacterAnimationReferenceKind.LayerAnimation => "Animation blueprint layer",
            CharacterAnimationReferenceKind.LocomotionSequence => "Locomotion sequence",
            _ => "Animation",
        };
        var value = $"{kind} • {ValueOr(target.EffectiveAssetClass, target.AssetClass, "unknown class")}";
        if (target.IsOverridden)
        {
            value += " • changed";
        }
        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.AnimationTarget,
            target.IsOverridden ? "✓ " + title : title,
            value,
            CanApply: CanReplaceTarget(target),
            CharacterTarget: target,
            CharacterSlot: slot);
    }

    internal static bool CanReplaceTarget(CharacterAnimationTargetSnapshot target) =>
        !string.IsNullOrWhiteSpace(target.OriginalPackage) &&
        !string.IsNullOrWhiteSpace(AnimationReplacementCatalogService.AcceptedClass(target));

    private static int CountTargets(AnimationExplorerNode node) =>
        (node.Kind == AnimationExplorerNodeKind.AnimationTarget ? 1 : 0) +
        node.Children.Sum(CountTargets);

    private static string FriendlyAction(string value)
    {
        var leaf = PackageLeaf(value.Replace('.', '/'));
        return FriendlyAssetName(ValueOr(leaf, "Unlabelled action"));
    }

    private static string FriendlyContext(string value)
    {
        var leaf = PackageLeaf(value.Replace('.', '/'));
        return FriendlyAssetName(ValueOr(leaf, value));
    }

    private static string FriendlyAssetName(string value)
    {
        var clean = value;
        foreach (var prefix in new[] { "ABP_", "AM_", "A_", "MAS_", "LAS_" })
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[prefix.Length..];
                break;
            }
        }
        return clean.Replace('_', ' ').Trim();
    }

    private static string FriendlyDiagnostic(CharacterAnimationDiagnostic item) =>
        item.Severity switch
        {
            CharacterAnimationDiagnosticSeverity.Error => "Could not read part of the graph",
            CharacterAnimationDiagnosticSeverity.Warning => "Some animation data was skipped",
            _ => "Animation scan note",
        } + ": " + item.Message;

    internal static bool CanApply(AnimLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var assetClass = entry.AssetClass?.Trim() ?? "";
        var split = Math.Max(assetClass.LastIndexOf('/'), assetClass.LastIndexOf('.'));
        if (split >= 0 && split + 1 < assetClass.Length)
        {
            assetClass = assetClass[(split + 1)..];
        }
        var isAnimation = assetClass.Equals("AnimSequence", StringComparison.OrdinalIgnoreCase) ||
                          assetClass.Equals("AnimMontage", StringComparison.OrdinalIgnoreCase);
        var isManaged = entry.CachedFiles.Count > 0 &&
                        !entry.SourceMode.Equals("external", StringComparison.OrdinalIgnoreCase) &&
                        !entry.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase);
        var isBaseGame = entry.SourceMode.Equals("base-game", StringComparison.OrdinalIgnoreCase);
        return entry.IsAvailable &&
               !entry.HealthStatus.Equals("quarantined", StringComparison.OrdinalIgnoreCase) &&
               isAnimation &&
               !string.IsNullOrWhiteSpace(entry.PackagePath) &&
               (isManaged || isBaseGame);
    }

    private static AnimationExplorerNode BuildCurrentSuit(NativeSuitProject project)
    {
        var familyChildren = project.AnimationOverrides
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ReplacementSet, StringComparer.OrdinalIgnoreCase)
            .Select(BuildSetOverride)
            .ToList();

        var sequenceChildren = project.LocomotionOverrides
            .OrderBy(item => SequenceOrder(item.DonorSequence))
            .ThenBy(item => item.DonorSequence, StringComparer.OrdinalIgnoreCase)
            .Select(BuildSequenceOverride)
            .ToList();

        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.Section,
            "Current suit",
            ValueOr(project.DisplayName, project.SlotId, "Unsaved suit"),
            ChildNodes:
            [
                new AnimationExplorerNode(
                    AnimationExplorerNodeKind.Group,
                    "Animation families",
                    familyChildren.Count == 0
                        ? "No family or animation-set swaps"
                        : $"{familyChildren.Count} override{Plural(familyChildren.Count)}",
                    ChildNodes: familyChildren),
                new AnimationExplorerNode(
                    AnimationExplorerNodeKind.Group,
                    "Idle, walk and run",
                    sequenceChildren.Count == 0
                        ? "Using the gameplay donor"
                        : $"{sequenceChildren.Count} sequence override{Plural(sequenceChildren.Count)}",
                    ChildNodes: sequenceChildren),
            ]);
    }

    private static AnimationExplorerNode BuildSetOverride(AnimSetOverride item)
    {
        var title = ValueOr(item.Category, item.Kind, "Animation set");
        var children = new List<AnimationExplorerNode>();
        AddValue(children, "Kind", item.Kind);
        AddValue(children, "Replaces", item.DonorSet);
        AddValue(children, "Uses", item.ReplacementSet);
        AddValue(children, "Package", item.ReplacementPackage, AnimationExplorerNodeKind.Package);

        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.Override,
            title,
            ValueOr(item.ReplacementSet, item.ReplacementPackage, "Configured"),
            ChildNodes: children);
    }

    private static AnimationExplorerNode BuildSequenceOverride(AnimSequenceOverride item)
    {
        var title = FriendlySequenceRole(item.DonorSequence);
        var children = new List<AnimationExplorerNode>();
        AddValue(children, "Donor sequence", item.DonorSequence);
        AddValue(children, "Donor package", item.DonorSequencePackage, AnimationExplorerNodeKind.Package);
        AddValue(children, "Replacement", item.ReplacementSequence);
        AddValue(children, "Package", item.ReplacementPackage, AnimationExplorerNodeKind.Package);

        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.Override,
            title,
            ValueOr(item.ReplacementSequence, item.ReplacementPackage, "Configured"),
            ChildNodes: children);
    }

    private static AnimationExplorerNode BuildImportedAnimations(AnimLibrary library)
    {
        var entries = library.Entries
            .OrderByDescending(CanApply)
            .ThenBy(entry => ValueOr(entry.Name, entry.PackagePath, entry.Id), StringComparer.OrdinalIgnoreCase)
            .Select(BuildImportedEntry)
            .ToList();

        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.Section,
            "Imported animations",
            entries.Count == 0
                ? "No cooked animations imported yet"
                : $"{entries.Count} animation{Plural(entries.Count)}",
            ChildNodes: entries);
    }

    private static AnimationExplorerNode BuildImportedEntry(AnimLibraryEntry entry)
    {
        var canApply = CanApply(entry);
        var health = FriendlyHealth(entry);
        var children = new List<AnimationExplorerNode>
        {
            new(
                AnimationExplorerNodeKind.Health,
                "Health",
                health,
                EntryId: entry.Id,
                CanApply: canApply),
        };

        AddEntryValue(children, entry, "Package", entry.PackagePath, AnimationExplorerNodeKind.Package, canApply);
        AddEntryValue(children, entry, "Rig / skeleton", entry.Skeleton, AnimationExplorerNodeKind.Rig, canApply);
        AddEntryValue(children, entry, "Asset class", entry.AssetClass, AnimationExplorerNodeKind.Property, canApply);
        AddEntryValue(children, entry, "Delivery", entry.SourceMode, AnimationExplorerNodeKind.Property, canApply);
        AddEntryValue(children, entry, "Category", entry.Category, AnimationExplorerNodeKind.Property, canApply);
        AddEntryValue(children, entry, "Additive mode", entry.AdditiveMode, AnimationExplorerNodeKind.Property, canApply);

        children.Add(new AnimationExplorerNode(
            AnimationExplorerNodeKind.Property,
            "Root motion",
            entry.RootMotion ? "Enabled" : "Disabled",
            EntryId: entry.Id,
            CanApply: canApply));

        if (entry.SupportPackages.Count > 0)
        {
            children.Add(new AnimationExplorerNode(
                AnimationExplorerNodeKind.Group,
                "Support packages",
                $"{entry.SupportPackages.Count} package{Plural(entry.SupportPackages.Count)}",
                EntryId: entry.Id,
                CanApply: canApply,
                ChildNodes: entry.SupportPackages
                    .OrderBy(package => package.AssetClass, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(package => package.PackagePath, StringComparer.OrdinalIgnoreCase)
                    .Select(package => BuildSupportPackage(entry, package, canApply))
                    .ToList()));
        }

        AddStringGroup(children, entry, "Dependencies", entry.Dependencies,
            AnimationExplorerNodeKind.Dependency, canApply);
        AddStringGroup(children, entry, "Unresolved imports", entry.UnresolvedImports,
            AnimationExplorerNodeKind.Warning, canApply);
        AddStringGroup(children, entry, "Health notes", entry.HealthIssues,
            AnimationExplorerNodeKind.Warning, canApply);

        if (!string.IsNullOrWhiteSpace(entry.Notes))
        {
            AddEntryValue(children, entry, "Notes", entry.Notes, AnimationExplorerNodeKind.Property, canApply);
        }

        var displayName = ValueOr(entry.Name, PackageLeaf(entry.PackagePath), entry.Id, "Imported animation");
        var subtitle = canApply ? $"Ready • {ValueOr(entry.Skeleton, "rig inspected")}" : health;
        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.ImportedAnimation,
            displayName,
            subtitle,
            EntryId: entry.Id,
            CanApply: canApply,
            ChildNodes: children);
    }

    private static AnimationExplorerNode BuildSupportPackage(
        AnimLibraryEntry owner,
        AnimLibraryCachedPackage package,
        bool canApply)
    {
        var children = new List<AnimationExplorerNode>();
        AddChildValue(children, owner, "Package", package.PackagePath,
            AnimationExplorerNodeKind.Package, canApply);
        AddStringGroup(children, owner, "Dependencies", package.Dependencies,
            AnimationExplorerNodeKind.Dependency, canApply);
        AddStringGroup(children, owner, "Unresolved imports", package.UnresolvedImports,
            AnimationExplorerNodeKind.Warning, canApply);

        return new AnimationExplorerNode(
            AnimationExplorerNodeKind.SupportPackage,
            ValueOr(package.AssetClass, "Support package"),
            ValueOr(package.PackagePath, "No package path"),
            EntryId: owner.Id,
            CanApply: canApply,
            ChildNodes: children);
    }

    private static void AddStringGroup(
        ICollection<AnimationExplorerNode> destination,
        AnimLibraryEntry owner,
        string title,
        IEnumerable<string> values,
        AnimationExplorerNodeKind childKind,
        bool canApply)
    {
        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        destination.Add(new AnimationExplorerNode(
            AnimationExplorerNodeKind.Group,
            title,
            $"{normalized.Count} item{Plural(normalized.Count)}",
            EntryId: owner.Id,
            CanApply: canApply,
            ChildNodes: normalized
                .Select(value => new AnimationExplorerNode(
                    childKind,
                    PackageLeaf(value),
                    value,
                    EntryId: owner.Id,
                    CanApply: canApply))
                .ToList()));
    }

    private static void AddValue(
        ICollection<AnimationExplorerNode> destination,
        string title,
        string value,
        AnimationExplorerNodeKind kind = AnimationExplorerNodeKind.Property)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        destination.Add(new AnimationExplorerNode(kind, title, value));
    }

    private static void AddEntryValue(
        ICollection<AnimationExplorerNode> destination,
        AnimLibraryEntry owner,
        string title,
        string value,
        AnimationExplorerNodeKind kind,
        bool canApply)
    {
        AddChildValue(destination, owner, title, value, kind, canApply);
    }

    private static void AddChildValue(
        ICollection<AnimationExplorerNode> destination,
        AnimLibraryEntry owner,
        string title,
        string value,
        AnimationExplorerNodeKind kind,
        bool canApply)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        destination.Add(new AnimationExplorerNode(
            kind,
            title,
            value,
            EntryId: owner.Id,
            CanApply: canApply));
    }

    private static AnimationExplorerNode? Filter(AnimationExplorerNode node, string query)
    {
        var children = node.Children
            .Select(child => Filter(child, query))
            .Where(child => child is not null)
            .Cast<AnimationExplorerNode>()
            .ToList();

        var matchesSelf = node.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          node.Value.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (!matchesSelf && children.Count == 0)
        {
            return null;
        }

        // A matching group or imported entry keeps its full subtree; a matching leaf keeps the
        // ancestor path that explains where the result lives.
        return matchesSelf ? node : node with { Children = children };
    }

    private static string FriendlyHealth(AnimLibraryEntry entry)
    {
        if (!entry.IsAvailable || entry.HealthStatus.Equals("quarantined", StringComparison.OrdinalIgnoreCase))
        {
            return "Needs attention";
        }

        return entry.HealthStatus.ToLowerInvariant() switch
        {
            "healthy" => "Ready",
            "legacy" => "Legacy import",
            "external" => "External reference",
            "" when !entry.Inspected => "Not inspected",
            "" => "Available",
            _ => entry.HealthStatus,
        };
    }

    private static string FriendlySequenceRole(string donorSequence)
    {
        if (donorSequence.Contains("idle", StringComparison.OrdinalIgnoreCase)) return "Idle";
        if (donorSequence.Contains("walk", StringComparison.OrdinalIgnoreCase)) return "Walk";
        if (donorSequence.Contains("run", StringComparison.OrdinalIgnoreCase)) return "Run";
        return ValueOr(donorSequence, "Sequence override");
    }

    private static int SequenceOrder(string donorSequence)
    {
        if (donorSequence.Contains("idle", StringComparison.OrdinalIgnoreCase)) return 0;
        if (donorSequence.Contains("walk", StringComparison.OrdinalIgnoreCase)) return 1;
        if (donorSequence.Contains("run", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static string PackageLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Item";
        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    private static string ValueOr(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Plural(int count) => count == 1 ? "" : "s";
}

internal sealed record AnimationExplorerSnapshot(
    IReadOnlyList<AnimationExplorerNode> Roots,
    int ImportedCount,
    int HealthyImportedCount);

internal sealed record AnimationExplorerNode(
    AnimationExplorerNodeKind Kind,
    string Title,
    string Value = "",
    string? EntryId = null,
    bool CanApply = false,
    IReadOnlyList<AnimationExplorerNode>? ChildNodes = null,
    CharacterAnimationTargetSnapshot? CharacterTarget = null,
    CharacterAnimationSlotSnapshot? CharacterSlot = null,
    CharacterAnimationDiagnostic? Diagnostic = null)
{
    internal IReadOnlyList<AnimationExplorerNode> Children { get; init; } =
        ChildNodes ?? Array.Empty<AnimationExplorerNode>();
}

internal enum AnimationExplorerNodeKind
{
    Section,
    Group,
    CharacterSet,
    AnimationSlot,
    AnimationTarget,
    Override,
    ImportedAnimation,
    Health,
    Rig,
    SupportPackage,
    Package,
    Dependency,
    Warning,
    Diagnostic,
    Property,
}
