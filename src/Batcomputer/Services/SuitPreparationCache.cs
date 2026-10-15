using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Batcomputer;

/// <summary>Disposable, bounded preparation snapshots. Never an authoring source or validation bypass.</summary>
internal static class SuitPreparationCache
{
    private static readonly object Gate = new();
    internal sealed record Receipt(string Key, NativeSuitProject Project, CharacterVoiceBuildService.Result? Voice,
        Dictionary<string, string> Files);
    internal sealed record Hit(string Root, Receipt Receipt);
    internal static string Root(string workspace) => Path.Combine(AppSettings.GeneratedRootFor(workspace), "BuildPreparationCache-v1");
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FileHash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Entry(string workspace, string slot) => Path.Combine(Root(workspace), Digest(slot) + ".zip");
    private static bool Owned(string path) => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^[A-F0-9]{64}\\.zip$");
    private sealed class CacheLock : IDisposable
    {
        private readonly Mutex mutex;
        internal CacheLock(string workspace)
        {
            mutex = new Mutex(false, "Local\\BatcomputerPreparation-" + Digest(Path.GetFullPath(Root(workspace)).ToUpperInvariant()));
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) { mutex.Dispose(); throw new IOException("Another build is using the preparation cache."); } }
            catch (AbandonedMutexException) { /* The previous process exited; this thread owns the mutex. */ }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    private static void RemoveInterruptedWrites(string workspace)
    {
        if (!Directory.Exists(Root(workspace))) return;
        foreach (var file in Directory.GetFiles(Root(workspace), "*.tmp-*"))
            if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^[A-F0-9]{64}\\.zip\\.tmp-[a-f0-9]{32}$")) File.Delete(file);
    }
    // Evict against compressed bytes as they are written. Reserving the entire raw
    // preparation would evict useful small archives and cause multi-suit cache thrashing.
    private sealed class BudgetStream(Stream output, Action<long> reserve) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => output.Length;
        public override long Position { get => output.Position; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { reserve(output.Position + buffer.Length); output.Write(buffer); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) output.Dispose(); base.Dispose(disposing); }
    }
    internal static void Clear(string workspace)
    {
        lock (Gate)
        {
            using var lease = new CacheLock(workspace);
            RemoveInterruptedWrites(workspace);
            if (!Directory.Exists(Root(workspace))) return;
            foreach (var file in Directory.GetFiles(Root(workspace), "*.zip").Where(Owned)) File.Delete(file);
        }
    }

    internal static void Trim(string workspace, int limitMb)
    {
        lock (Gate)
        {
            using var lease = new CacheLock(workspace);
            RemoveInterruptedWrites(workspace);
            if (!Directory.Exists(Root(workspace))) return;
            var files = Directory.GetFiles(Root(workspace), "*.zip").Where(Owned).Select(f => new FileInfo(f)).OrderBy(f => f.LastWriteTimeUtc).ToArray();
            long size = files.Sum(f => f.Length), limit = Math.Clamp(limitMb, 16, 2048) * 1024L * 1024L;
            foreach (var file in files) { if (size <= limit) break; size -= file.Length; file.Delete(); }
        }
    }

    internal static string Fingerprint(NativeSuitProject project, string workspace, string content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s) => hash.AppendData(Encoding.UTF8.GetBytes(s + "\n"));
        Add("SuitPreparationCache-v1"); Add(typeof(SuitPreparationCache).Module.ModuleVersionId.ToString());
        Add(JsonSerializer.Serialize(project)); Add(JsonSerializer.Serialize(AppSettings.Current));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0; int count = 0;
        void FileInput(string path, bool full)
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path + (full ? "|hash" : "|stat"))) return;
            var f = new FileInfo(path); Add(path);
            if (!f.Exists) { Add("missing"); return; }
            if (++count > 250000) throw new IOException("Too many cache input files; use a normal build.");
            Add(full ? f.Length.ToString() : f.Length + ":" + f.LastWriteTimeUtc.Ticks);
            if (full) { bytes += f.Length; if (bytes > 2L * 1024 * 1024 * 1024) throw new IOException("Cache input scan limit reached."); Add(FileHash(path)); }
        }
        void Tree(string path, bool full)
        {
            Add(path);
            if (!Directory.Exists(path)) { Add("missing directory"); return; }
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Order(StringComparer.OrdinalIgnoreCase)) FileInput(f, full);
        }
        // The certified source snapshot and explicit authoring sources are content-hashed.
        // Disposable preparation folders have random names. Hash their relative file names
        // and bytes, not their absolute paths or copy timestamps.
        foreach (var f in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            bytes += new FileInfo(f).Length;
            if (++count > 250000 || bytes > 2L * 1024 * 1024 * 1024) throw new IOException("Cache input scan limit reached.");
            Add(Path.GetRelativePath(content, f)); Add(FileHash(f));
        }
        var output = new SuitProjectService(workspace).ProjectOutputDirectory(project);
        void Walk(JsonNode? node)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length < 1024 && !s.StartsWith('/'))
            {
                if (Path.IsPathFullyQualified(s))
                {
                    if (Directory.Exists(s)) Tree(s, true); else FileInput(s, true);
                }
                else if (File.Exists(Path.Combine(output, s))) FileInput(Path.Combine(output, s), true);
            }
            else if (node is JsonObject obj) foreach (var p in obj) Walk(p.Value);
            else if (node is JsonArray a) foreach (var p in a) Walk(p);
        }
        Walk(JsonSerializer.SerializeToNode(project));
        if (project.CustomCharacter is { IsDefinition: false } child)
        {
            var service = new SuitProjectService(workspace);
            var parentPath = service.ProjectPathForSlot(child.DefinitionSlotId);
            FileInput(parentPath, true);
            var parent = service.LoadProject(parentPath) ?? throw new InvalidDataException("Missing character definition.");
            Walk(JsonSerializer.SerializeToNode(parent));
        }
        var settings = AppSettings.Current;
        if (settings.EffectiveUsmapPath() is { } mappings) FileInput(mappings, true);
        // Native/extraction inventories invalidate on changes without rereading multi-GB PAKs.
        // Native containers only: installing this mod must not invalidate its own cache.
        var paks = settings.EffectiveGamePaksRoot();
        Add(JsonSerializer.Serialize(GameAssetCompatibilityService.Capture(paks)));
        if (Directory.Exists(paks))
            foreach (var file in Directory.EnumerateFiles(paks).Order(StringComparer.OrdinalIgnoreCase))
                FileInput(file, Path.GetExtension(file).Equals(".utoc", StringComparison.OrdinalIgnoreCase));
        Tree(settings.EffectiveExtractedContentRoot(), false);
        Tree(settings.EffectiveExportContentRoot(), false);
        foreach (var dir in new[] { "AnimationLibrary", "GameplayAnimationGraphs", "VoiceWorkshop", "NativeSuitMaterials" })
            Tree(Path.Combine(AppSettings.GeneratedRootFor(workspace), dir), false);
        foreach (var graph in project.GameplayAnimationGraphs)
        {
            GameplayAnimationGraphService.Load(workspace, graph); // verify the immutable closure even on a cache hit
            Tree(Path.Combine(GameplayAnimationGraphService.CacheRoot(workspace), graph.Id), true);
        }
        if (!string.IsNullOrWhiteSpace(project.VoiceProfileId))
        {
            var selected = CharacterVoiceBuildService.Selected(project, workspace);
            var library = new CharacterVoiceLibraryService(workspace);
            foreach (var clip in selected.Assignments.Where(a => !a.Silent).Select(a => a.ClipId).Distinct()) FileInput(library.ClipPath(clip), true);
            var profile = JsonSerializer.SerializeToNode(selected);
            Add(profile?.ToJsonString() ?? "null"); Walk(profile);
            foreach (var file in new[] { settings.VoiceEncoderExePath, settings.EffectiveVgmstreamExePath() }) if (!string.IsNullOrEmpty(file)) FileInput(file, true);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Invalid cache path.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!FileSystemPathUtil.IsWithinDirectory(path, root)) throw new InvalidDataException("Cache path escaped its stage.");
        return path;
    }
    internal static Hit? Restore(string workspace, string slot, string key, string destination)
    {
        lock (Gate)
        {
            using var lease = new CacheLock(workspace);
            var file = Entry(workspace, slot); if (!File.Exists(file)) return null;
            using var zip = ZipFile.OpenRead(file);
            var manifest = zip.GetEntry("receipt.json") ?? throw new InvalidDataException("Missing cache receipt.");
            if (manifest.Length > 16 * 1024 * 1024) throw new InvalidDataException("Oversized cache receipt.");
            using var reader = new StreamReader(manifest.Open());
            var receipt = JsonSerializer.Deserialize<Receipt>(reader.ReadToEnd()) ?? throw new InvalidDataException("Invalid cache receipt.");
            if (receipt.Key != key) return null;
            if (Directory.Exists(destination)) throw new IOException("Cache restore requires a fresh directory.");
            if (receipt.Project.SlotId != slot || zip.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024 ||
                zip.Entries.Count != receipt.Files.Count + 1 ||
                zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
                throw new InvalidDataException("Invalid cache receipt or size.");
            foreach (var (relative, expected) in receipt.Files)
            {
                var entry = zip.GetEntry(relative) ?? throw new InvalidDataException("Missing cached file.");
                var target = SafePath(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
                if (FileHash(target) != expected) throw new InvalidDataException("Corrupt cached file.");
            }
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            var voice = receipt.Voice;
            if (voice is not null) voice = voice with { MediaFiles = voice.MediaFiles.Select(f => SafePath(destination, f)).ToArray(), ReportPath = SafePath(destination, voice.ReportPath) };
            return new(destination, receipt with { Voice = voice });
        }
    }
    internal static bool Store(string workspace, string key, NativeSuitProject project, string stage, CharacterVoiceBuildService.Result? voice, int limitMb)
    {
        lock (Gate)
        {
            using var lease = new CacheLock(workspace);
            RemoveInterruptedWrites(workspace);
            var files = Directory.GetFiles(Path.Combine(stage, "LEGOBatmanLotDK", "Content"), "*", SearchOption.AllDirectories).ToList();
            if (voice is not null) { files.AddRange(voice.MediaFiles); files.Add(voice.ReportPath); }
            files = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var limit = Math.Clamp(limitMb, 16, 2048) * 1024L * 1024L;
            var payloadSize = files.Sum(f => new FileInfo(f).Length);
            if (payloadSize > limit) return false;
            string Relative(string f) { var r = Path.GetRelativePath(stage, f).Replace('\\', '/'); SafePath(stage, r); return r; }
            var hashes = files.ToDictionary(Relative, FileHash, StringComparer.Ordinal);
            var relativeVoice = voice is null ? null : voice with { MediaFiles = voice.MediaFiles.Select(Relative).ToArray(), ReportPath = Relative(voice.ReportPath) };
            var json = JsonSerializer.Serialize(new Receipt(key, project, relativeVoice, hashes));
            var reserve = payloadSize + Encoding.UTF8.GetByteCount(json) + files.Count * 512L + 65536;
            var root = Root(workspace); Directory.CreateDirectory(root);
            if (reserve > limit) return false;
            var target = Entry(workspace, project.SlotId);
            // Drop an obsolete same-suit entry even if the replacement is too large.
            if (File.Exists(target)) File.Delete(target);
            var entries = Directory.GetFiles(root, "*.zip").Where(Owned).Select(f => new FileInfo(f)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long size = entries.Sum(f => f.Length);
            var eviction = new Queue<FileInfo>(entries);
            void ReserveCompressed(long length)
            {
                if (length > limit) throw new IOException("Compressed preparation exceeded the cache budget.");
                while (size + length > limit && eviction.TryDequeue(out var old)) { size -= old.Length; old.Delete(); }
            }
            var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var zip = new ZipArchive(new BudgetStream(File.Create(temp), ReserveCompressed), ZipArchiveMode.Create))
                {
                    foreach (var f in files) zip.CreateEntryFromFile(f, Relative(f), CompressionLevel.Fastest);
                    using var writer = new StreamWriter(zip.CreateEntry("receipt.json").Open()); writer.Write(json);
                }
                if (new FileInfo(temp).Length + size > limit) return false;
                File.Move(temp, target); return true;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
