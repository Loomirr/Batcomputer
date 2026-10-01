namespace Batcomputer;

/// <summary>Batch draft edits only; partner lines and unrelated assignments are never changed.</summary>
internal static class CharacterVoiceAssignmentService
{
    internal enum Edit { Recording, Silence, Original }

    internal static int Apply(CharacterVoiceLibraryService.Profile profile,
        IEnumerable<CharacterVoiceCatalogService.Line> selection, Edit edit, string clipId = "")
    {
        if (!Enum.IsDefined(edit) || (edit == Edit.Recording && !CharacterVoiceLibraryService.HashId(clipId)) ||
            (edit != Edit.Recording && clipId.Length != 0))
            throw new InvalidDataException("Choose one imported recording, silence, or original audio.");
        var lines = selection.Where(l => l.OwnSpeaker).DistinctBy(l => l.Id).ToArray();
        if (lines.Any(l => !CharacterVoiceLibraryService.HashId(l.Id)))
            throw new InvalidDataException("Invalid voice slot identity.");
        var draft = profile.Assignments.ToList();
        var changed = 0;
        foreach (var line in lines)
        {
            var index = draft.FindIndex(a => a.LineId == line.Id);
            if (edit == Edit.Original)
            {
                if (index >= 0) { draft.RemoveAt(index); changed++; }
                continue;
            }
            var replacement = new CharacterVoiceLibraryService.Assignment(line.Id, line.EventPackage,
                line.Media, edit == Edit.Recording ? clipId : "", Silent: edit == Edit.Silence);
            if (index >= 0 && draft[index] == replacement) continue;
            if (index >= 0) draft[index] = replacement; else draft.Add(replacement);
            changed++;
        }
        if (changed > 0) profile.Assignments = draft;
        return changed;
    }
}
