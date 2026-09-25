using System.Text.Json;

namespace Batcomputer;

/// <summary>Changes runtime identity without moving source assets or changing saved project IDs.</summary>
internal static class CharacterPawnIdentityService
{
    internal static bool NeedsUpdate(SuitProjectService service, NativeSuitProject project, string owner) =>
        project.CustomCharacter is { IsDefinition: true } identity &&
        (!CustomCharacterProjectService.PawnOwner(identity).Equals(owner, StringComparison.Ordinal) ||
        service.ListProjectFiles().Select(row => service.LoadProject(row.Path)).Any(p => p?.CustomCharacter is { } child &&
            child.DefinitionSlotId == identity.DefinitionSlotId && !CustomCharacterProjectService.PawnOwner(child).Equals(owner, StringComparison.Ordinal)));

    internal static NativeSuitProject SaveFamily(SuitProjectService service, NativeSuitProject edited, string owner, out string backup)
    {
        if (edited.CustomCharacter is not { IsDefinition: true } identity)
            throw new InvalidDataException("Change the pawn-tag family on the character definition, not an individual suit.");
        if (!CustomCharacterProjectService.IsIdentifier(owner) || CustomCharacterProjectService.IsNativeOwner(owner))
            throw new InvalidDataException("Choose a unique pawn-tag family containing letters and numbers. Native character families are reserved.");
        var projects = service.ListProjectFiles().Select(row => service.LoadProject(row.Path)
            ?? throw new InvalidDataException("Could not read saved project: " + row.Path)).ToList();
        var family = projects.Where(p => p.CustomCharacter?.DefinitionSlotId == identity.DefinitionSlotId).ToList();
        var newScope = "Pawns.Playable." + owner;
        if (projects.Except(family).Any(p => PawnTagConfigService.CharacterScopeForPawnTag(p.PawnTag).Equals(newScope, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("That pawn-tag family is already used by another saved character or suit.");
        if (!family.Any(p => p.SlotId == edited.SlotId)) throw new InvalidDataException("Save the character before changing its identity.");
        var originals = family.ToDictionary(p => service.ProjectPathForSlot(p.SlotId), p => File.ReadAllText(service.ProjectPathForSlot(p.SlotId)));
        var updated = family.Select(p => JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(p.SlotId == edited.SlotId ? edited : p))!).ToList();
        foreach (var project in updated)
        {
            project.CustomCharacter!.PawnTagOwner = owner;
            CustomCharacterProjectService.ApplyIdentity(project);
            var error = CustomCharacterProjectService.IdentityError(project);
            if (error is not null) throw new InvalidDataException(error);
        }
        backup = Path.Combine(service.ProjectRoot, "Generated", "IdentityBackups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (var pair in originals) File.WriteAllText(Path.Combine(backup, Path.GetFileName(pair.Key)), pair.Value);
        try { foreach (var project in updated) service.SaveProject(project); }
        catch
        {
            foreach (var pair in originals) AtomicFileUtil.WriteAllText(pair.Key, pair.Value);
            throw;
        }
        return updated.Single(p => p.SlotId == edited.SlotId);
    }
}
