using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Workspace-only voice authoring drafts. Not a runtime voice-registration or cooking service.</summary>
internal sealed class CharacterVoiceLibraryService(string workspace)
{
    internal sealed record Clip(string Id, string Name, string Category, string Transcript, double Seconds, int SampleRate);
    internal sealed record Assignment(string LineId, string Event, string Media, string ClipId, bool Silent = false);
    internal sealed class Profile
    {
        public int Schema { get; set; } = 1;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string CharacterId { get; set; } = "";
        public string Name { get; set; } = "New voice";
        public string Donor { get; set; } = "";
        public string NativeVoice { get; set; } = "";
        public double GainDb { get; set; }
        public string ProposedVoiceActor => "BCVoice_" + Id;
        public List<Assignment> Assignments { get; set; } = [];
    }
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();
    internal string Root => Path.Combine(AppSettings.GeneratedRootFor(workspace), "VoiceWorkshop");
    private string Index => Path.Combine(Root, "clips.json");
    internal static bool HashId(string value) => Regex.IsMatch(value ?? "", "^[a-f0-9]{64}$");
    internal string ClipPath(string id) => HashId(id) ? Path.Combine(Root, "Clips", id + ".wav") : throw new InvalidDataException("Invalid voice clip identity.");
    internal List<Clip> Clips() => File.Exists(Index) ? JsonSerializer.Deserialize<List<Clip>>(File.ReadAllText(Index), Json) ?? [] : [];
    internal List<Profile> Profiles(string character) => Directory.Exists(Path.Combine(Root, "Profiles"))
        ? Directory.EnumerateFiles(Path.Combine(Root, "Profiles"), "*.json").Select(f => JsonSerializer.Deserialize<Profile>(File.ReadAllText(f), Json)!)
            .Where(p => p is not null && p.CharacterId == character).ToList() : [];

    internal void Save(Profile profile)
    {
        CharacterVoiceLevelService.Validate(profile.GainDb);
        if (profile.Schema is not (1 or 2) || !Regex.IsMatch(profile.Id, "^[a-f0-9]{32}$") || string.IsNullOrWhiteSpace(profile.CharacterId) ||
            string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Assignments.Count > 10000 ||
            profile.Assignments.Select(a => a.LineId).Distinct().Count() != profile.Assignments.Count)
            throw new InvalidDataException("Invalid voice profile or duplicate replacement targets.");
        foreach (var assignment in profile.Assignments)
            if (!HashId(assignment.LineId) || (assignment.Silent ? assignment.ClipId != "" : !HashId(assignment.ClipId) || !File.Exists(ClipPath(assignment.ClipId))))
                throw new InvalidDataException("A replacement recording is missing or invalid.");
        // An explicit silent override is different from removing an assignment (inherit original).
        if (profile.Assignments.Any(a => a.Silent)) profile.Schema = 2;
        lock (Gate) { Directory.CreateDirectory(Path.Combine(Root, "Profiles")); AtomicFileUtil.WriteAllText(Path.Combine(Root, "Profiles", profile.Id + ".json"), JsonSerializer.Serialize(profile, Json)); }
    }

    internal Clip Import(string path, string category = "Imported", string transcript = "", string? name = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Choose a PCM WAV under 64 MB.");
        var data = File.ReadAllBytes(path); var format = InspectWav(data);
        var id = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var clip = new Clip(id, name ?? Path.GetFileNameWithoutExtension(path), category, transcript, format.Seconds, format.Rate);
        lock (Gate)
        {
            var clips = Clips(); var existing = clips.FirstOrDefault(c => c.Id == id);
            if (existing is not null)
            {
                // A later catalog import can enrich a plain WAV import without replacing user metadata.
                var enriched = existing with {
                    Category = existing.Category == "Imported" && category != "Imported" ? category : existing.Category,
                    Transcript = string.IsNullOrWhiteSpace(existing.Transcript) ? transcript : existing.Transcript
                };
                if (enriched != existing) { clips[clips.IndexOf(existing)] = enriched; AtomicFileUtil.WriteAllText(Index, JsonSerializer.Serialize(clips, Json)); }
                return enriched;
            }
            Directory.CreateDirectory(Path.Combine(Root, "Clips"));
            var destination = ClipPath(id);
            if (File.Exists(destination)) { if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(data)) throw new InvalidDataException("Voice cache identity collision."); }
            else File.WriteAllBytes(destination, data);
            clips.Add(clip); AtomicFileUtil.WriteAllText(Index, JsonSerializer.Serialize(clips, Json));
        }
        return clip;
    }

    internal (int Imported, string[] Errors) ImportFolder(string folder, CancellationToken cancellation)
    {
        var root = Path.GetFullPath(folder); var errors = new List<string>(); int imported = 0;
        var catalog = Path.Combine(root, "catalog.json");
        var metadata = new Dictionary<string, (string Name, string Category, string Text)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(catalog))
        {
            if (new FileInfo(catalog).Length > 8 * 1024 * 1024) throw new InvalidDataException("Voice catalog is too large.");
            using var doc = JsonDocument.Parse(File.ReadAllText(catalog));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Voice catalog must be an array of clips.");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string Text(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                var relative = Text("file"); if (relative.Length == 0) continue;
                var path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Voice catalog references a file outside the selected folder.");
                metadata[path] = (Text("id"), Text("category"), Text("text"));
            }
        }
        var files = Directory.EnumerateFiles(root, "*.wav", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Take(5001).ToArray();
        if (files.Length > 5000) throw new InvalidDataException("Import at most 5,000 recordings per folder.");
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            try { metadata.TryGetValue(file, out var m); Import(file, string.IsNullOrWhiteSpace(m.Category) ? "Imported" : m.Category, m.Text ?? "", string.IsNullOrWhiteSpace(m.Name) ? null : m.Name); imported++; }
            catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
        }
        return (imported, errors.ToArray());
    }

    internal static (double Seconds, int Rate) InspectWav(byte[] bytes)
    {
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8) || BitConverter.ToUInt32(bytes, 4) != bytes.Length - 8)
            throw new InvalidDataException("This is not a complete RIFF WAV recording.");
        ushort channels = 0, align = 0; int rate = 0, length = 0; bool format = false;
        for (long pos = 12; pos + 8 <= bytes.Length;)
        {
            int at = (int)pos; uint size = BitConverter.ToUInt32(bytes, at + 4); long end = pos + 8 + size;
            if (end > bytes.Length) throw new InvalidDataException("Truncated WAV chunk.");
            if (bytes.AsSpan(at, 4).SequenceEqual("fmt "u8))
            {
                if (format || size < 16 || BitConverter.ToUInt16(bytes, at + 8) != 1 || BitConverter.ToUInt16(bytes, at + 22) != 16)
                    throw new InvalidDataException("Voice preview requires uncompressed 16-bit PCM WAV.");
                format = true; channels = BitConverter.ToUInt16(bytes, at + 10); rate = BitConverter.ToInt32(bytes, at + 12); align = BitConverter.ToUInt16(bytes, at + 20);
                if (channels is < 1 or > 2 || rate is < 8000 or > 192000 || align != channels * 2 || BitConverter.ToUInt32(bytes, at + 16) != rate * align)
                    throw new InvalidDataException("Unsupported or inconsistent WAV format.");
            }
            if (bytes.AsSpan(at, 4).SequenceEqual("data"u8)) { if (length != 0) throw new InvalidDataException("Multiple WAV data chunks are not supported."); length = checked((int)size); }
            pos = end + (size & 1);
        }
        if (!format || length <= 0 || length % align != 0) throw new InvalidDataException("WAV has no complete audio frames.");
        return ((double)length / (rate * align), rate);
    }
}
