using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Batcomputer;

/// <summary>Private workspace authoring sources. Independent of saved-suit/build formats.</summary>
internal sealed class AnimationDraftLibraryService(string projectRoot)
{
    internal sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string RigSignature { get; set; } = "";
        public string CharacterId { get; set; } = "";
        public string CharacterName { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public int DurationFrames { get; set; }
        public List<CookedVersion> Cooked { get; set; } = [];
    }
    internal sealed record CookedVersion(string Package, string ContentHash);
    internal sealed record View(string Id, string Name, string Character, string RigSignature,
        int DurationFrames, string Status, string[] UsedBy, string[] Packages, string File, bool Available);
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal string Root => Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "AnimationLibrary", "Drafts");
    internal string IndexPath => Path.Combine(Root, "drafts.json");
    internal List<Entry> Load()
    {
        if (!File.Exists(IndexPath)) return [];
        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(IndexPath), JsonOptions)
            ?? throw new InvalidDataException("The animation draft library index is invalid.");
        if (entries.Any(entry => !ValidId(entry.Id)) || entries.Select(entry => entry.Id).Distinct().Count() != entries.Count)
            throw new InvalidDataException("The animation draft library contains invalid or duplicate identities.");
        return entries;
    }
    private static bool ValidId(string? id) => id is { Length:32 } && id.All(char.IsAsciiHexDigit);
    internal string DraftPath(Entry entry)
    {
        if (!ValidId(entry.Id)) throw new InvalidDataException("Invalid animation draft identity.");
        return Path.Combine(Root, entry.Id + ".json");
    }
    internal Entry Save(string json, NativeSuitProject? character = null, string? existingId = null, string? cookedPackage = null)
    {
        if (Encoding.UTF8.GetByteCount(json) is 0 or > 2_000_000) throw new InvalidDataException("Draft must be smaller than 2 MB.");
        using var document = JsonDocument.Parse(json);
        var draft = document.RootElement;
        var signature = draft.GetProperty("rigSignature").GetString() ?? "";
        var bones = signature.Split('|').Select(item => item.Split(':')[0]).ToArray();
        if (signature.Length > 20_000 || bones.Length is < 1 or > 100 || bones.Any(string.IsNullOrWhiteSpace) || bones.Distinct().Count() != bones.Length)
            throw new InvalidDataException("Invalid authoring rig signature.");
        var name = AnimationDraftCookService.ValidateDraft(draft, signature, bones);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        lock (Gate)
        {
            var entries = Load();
            var entry = ValidId(existingId) ? entries.FirstOrDefault(item => item.Id == existingId) : null;
            entry ??= entries.FirstOrDefault(item => item.ContentHash == hash);
            if (entry is not null && entry.RigSignature != signature)
                throw new InvalidDataException("Cannot replace an animation draft with a different rig.");
            entry ??= new Entry { Id = Guid.NewGuid().ToString("N"), CharacterId = character?.SlotId ?? "", CharacterName = character?.DisplayName ?? "" };
            var isNew = !entries.Contains(entry);
            var path = DraftPath(entry);
            var previous = File.Exists(path) ? File.ReadAllText(path) : null;
            entry.Name = name; entry.RigSignature = signature; entry.ContentHash = hash;
            entry.DurationFrames = draft.GetProperty("durationFrames").GetInt32();
            entry.UpdatedUtc = DateTime.UtcNow.ToString("O");
            if (!string.IsNullOrWhiteSpace(cookedPackage))
            {
                var package = UnrealPathUtil.NormalizePackagePath(cookedPackage);
                entry.Cooked.RemoveAll(item => item.Package.Equals(package, StringComparison.OrdinalIgnoreCase));
                entry.Cooked.Add(new CookedVersion(package, hash));
            }
            if (isNew) entries.Add(entry);
            Directory.CreateDirectory(Root);
            AtomicFileUtil.WriteAllText(path, json);
            try { AtomicFileUtil.WriteAllText(IndexPath, JsonSerializer.Serialize(entries, JsonOptions)); }
            catch
            {
                // A failed index commit must leave the previous editable draft intact.
                if (previous is not null) AtomicFileUtil.WriteAllText(path, previous);
                // A new orphan is harmless and recoverable; never delete user authoring data.
                throw;
            }
            return entry;
        }
    }
    internal Entry Import(string path, NativeSuitProject? character = null, string? package = null)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Choose an animation draft JSON under 2 MB.");
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.TryGetProperty("libraryDraftId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return Save(json, character, id, package);
    }
    internal static Dictionary<string, string[]> Usage(NativeSuitProject? character)
    {
        var usages = new List<(string Package, string Slot)>();
        if (character is not null)
        {
            usages.AddRange(character.LocomotionOverrides.Select(item => (item.ReplacementPackage, item.DonorSequence)));
            usages.AddRange(character.AnimationSlotOverrides.Select(item => (item.ReplacementPackage, item.ActionTag + (item.ContextTags.Count > 0 ? " · " + string.Join(", ", item.ContextTags) : ""))));
            usages.AddRange(character.AnimationOverrides.Select(item => (item.ReplacementPackage, item.Category + " · whole set")));
        }
        return usages.Where(item => !string.IsNullOrWhiteSpace(item.Package))
            .GroupBy(item => UnrealPathUtil.NormalizePackagePath(item.Package), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Slot).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    internal View[] Views(NativeSuitProject? character)
    {
        var usage = Usage(character);
        var library = new AnimLibraryService(projectRoot).Load();
        var available = library.Entries.Where(item => item.IsAvailable && item.CachedFiles.Count > 0)
            .Select(item => item.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Load().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(entry =>
        {
            var packages = entry.Cooked.Select(item => item.Package).ToArray();
            var used = packages.Where(usage.ContainsKey).SelectMany(package => usage[package]).Distinct().ToArray();
            var path = DraftPath(entry);
            var file = File.Exists(path) && new FileInfo(path).Length is > 0 and <= 2_000_000;
            var hash = file ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
            var ready = entry.Cooked.Any(item => item.ContentHash == hash && available.Contains(item.Package));
            return new View(entry.Id, entry.Name, entry.CharacterName, entry.RigSignature, entry.DurationFrames,
                !file ? "Source missing / invalid size" : ready ? "Cooked" : entry.Cooked.Count > 0 ? "Draft edited / cook again" : "Draft only",
                used, packages, "drafts/" + entry.Id + ".json", file);
        }).ToArray();
    }
    internal void WritePreview(string folder, NativeSuitProject character)
    {
        var views = Views(character);
        var draftFolder = Path.Combine(folder, "drafts"); Directory.CreateDirectory(draftFolder);
        foreach (var view in views.Where(item => item.Available))
            File.Copy(Path.Combine(Root, view.Id + ".json"), Path.Combine(draftFolder, view.Id + ".json"), true);
        File.AppendAllText(Path.Combine(folder, "models.js"), "\nwindow.PREVIEW_USER_ANIMATIONS=" + JsonSerializer.Serialize(views,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + ";");
    }
}
