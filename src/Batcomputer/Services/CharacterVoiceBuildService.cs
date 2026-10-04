using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

/// <summary>Builds explicit, suit-local voice profiles into disposable package preparation stages.</summary>
internal static class CharacterVoiceBuildService
{
    // oggenc2 uses legacy Windows file APIs: .NET can create a WAV that it cannot
    // open under a deeply nested project stage. Only codec scratch files use this
    // short workspace; profile data and final media stay in the selected project.
    internal sealed class CodecWorkspace : IDisposable
    {
        internal string Folder { get; }
        internal string Input => Path.Combine(Folder, "input.wav");
        internal string Encoded => Path.Combine(Folder, "output.wem");
        internal string Decoded => Path.Combine(Folder, "decoded.wav");
        private readonly string parent;
        internal CodecWorkspace(string runtimeRoot, string temporaryRoot)
        {
            var name = "v-" + Guid.NewGuid().ToString("N");
            var roots = new[] { Path.Combine(runtimeRoot, "VoiceCodec"), temporaryRoot };
            parent = roots.Select(Path.GetFullPath).FirstOrDefault(root =>
                Path.Combine(root, name, "decoded.wav").Length < 240)
                ?? throw new InvalidDataException("The voice encoder needs a short scratch path. Move Batcomputer to a shorter folder or choose a shorter Windows temporary folder.");
            Folder = Path.GetFullPath(Path.Combine(parent, name));
            if (!FileSystemPathUtil.IsWithinDirectory(Folder, parent)) throw new InvalidDataException("Voice codec scratch path escaped its root.");
            Directory.CreateDirectory(Folder);
        }
        public void Dispose()
        {
            // This exact GUID folder contains only our disposable codec files.
            if (!FileSystemPathUtil.IsWithinDirectory(Folder, parent)) return;
            try { Directory.Delete(Folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    internal sealed record Result(string Actor, string[] Packages, string[] MediaFiles, string ReportPath, string[]? MediaMountPaths = null);
    internal static string[] MountPaths(Result result)
    {
        if (result.MediaMountPaths is { } paths)
        {
            if (paths.Length != result.MediaFiles.Length || paths.Any(p => !Regex.IsMatch(p,
                @"^LEGOBatmanLotDK/(?:Content|Plugins/Wub_Loc_[a-z]{2}[A-Z]{2}/Content)/Wub/Platforms/Windows/Media/BCV_[a-f0-9]{64}\.wem$")))
                throw new InvalidDataException("Invalid private voice media mount paths.");
            return paths;
        }
        // Existing build receipts stored only shared media paths.
        return result.MediaFiles.Select(f => "LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Media/" + Path.GetFileName(f)).ToArray();
    }
    internal static string[] LocalizedMediaMounts(IEnumerable<string> nativePaths, string originalMedia, string privateMedia)
    {
        if (!Regex.IsMatch(privateMedia, @"^BCV_[a-f0-9]{64}$")) throw new InvalidDataException("Invalid private media identity.");
        return nativePaths.Where(p => p.EndsWith('/' + originalMedia + ".wem", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(p, @"^LEGOBatmanLotDK/Plugins/Wub_Loc_[a-z]{2}[A-Z]{2}/Content/Wub/Platforms/Windows/Media/[^/]+\.wem$"))
        .Select(p => p[..(p.LastIndexOf('/') + 1)] + privateMedia + ".wem").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    }
    // Per-suit preparation folders are disposed after merging their Content. Retain
    // media and audit reports in the aggregate build before that cleanup happens.
    internal static Result PreserveForAggregate(Result source, string directory)
    {
        Directory.CreateDirectory(directory);
        var mounts = MountPaths(source);
        var files = source.MediaFiles.Select((file,i) => {
            // UnrealPak deduplicates response rows sharing one source filename.
            // Keep physically distinct files for different localization mounts.
            var target = Path.GetFullPath(Path.Combine(directory, "Media", mounts[i].Replace('/', Path.DirectorySeparatorChar)));
            if (!FileSystemPathUtil.IsWithinDirectory(target, directory)) throw new InvalidDataException("Voice media escaped aggregate staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(file)))
                throw new InvalidDataException("Conflicting character voice media in aggregate build.");
            if (!File.Exists(target)) File.Copy(file, target);
            return target;
        }).ToArray();
        var report = Path.Combine(directory, "voice-result.json");
        var result = source with { MediaFiles = files, ReportPath = report };
        File.Copy(source.ReportPath, report, overwrite: false);
        AtomicFileUtil.WriteAllText(Path.Combine(directory, "aggregate-media.json"), JsonSerializer.Serialize(result));
        return result;
    }
    internal static Result? Combine(IReadOnlyList<Result> results) => results.Count == 0 ? null : new("",
        results.SelectMany(r => r.Packages).Distinct(StringComparer.Ordinal).ToArray(),
        results.SelectMany(r => r.MediaFiles).ToArray(), results[0].ReportPath,
        results.Any(r => r.MediaMountPaths is not null) ? results.SelectMany(MountPaths).ToArray() : null);
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static bool OwnsProfile(NativeSuitProject project, CharacterVoiceLibraryService.Profile profile) =>
        profile.CharacterId == project.SlotId || (project.CustomCharacter is { IsDefinition: false } child && profile.CharacterId == child.DefinitionSlotId);
    internal static (string Owner, string Root) BuildIdentity(NativeSuitProject project, CharacterVoiceLibraryService.Profile profile, NativeSuitProject? definition)
    {
        if (!OwnsProfile(project, profile)) throw new InvalidDataException("Voice profile belongs to another character.");
        var owner = project;
        if (profile.CharacterId != project.SlotId)
        {
            if (definition?.CustomCharacter is not { IsDefinition: true } identity || definition.SlotId != profile.CharacterId ||
                identity.CharacterId != project.CustomCharacter?.CharacterId)
                throw new InvalidDataException("The inherited voice profile needs its saved character definition.");
            owner = definition;
        }
        var playable = owner.TargetPackages.Playable;
        if (!playable.StartsWith("/Game/Mods/", StringComparison.Ordinal) || playable.Split('/').Length < 6 || playable.Contains(".."))
            throw new InvalidDataException("Invalid private voice owner.");
        return (owner.SlotId, string.Join('/', playable.Split('/').Take(4)));
    }
    internal static CharacterVoiceLibraryService.Profile Selected(NativeSuitProject project, string workspace)
    {
        if (!Regex.IsMatch(project.VoiceProfileId, "^[a-f0-9]{32}$")) throw new InvalidDataException("Invalid selected voice profile.");
        var library = new CharacterVoiceLibraryService(workspace);
        var profiles = library.Profiles(project.SlotId);
        if (project.CustomCharacter is { IsDefinition: false } child) profiles.AddRange(library.Profiles(child.DefinitionSlotId));
        var profile = profiles.DistinctBy(p => p.Id).SingleOrDefault(p => p.Id == project.VoiceProfileId)
            ?? throw new InvalidDataException("The selected voice profile is not in this character's local library. Open Character voices to choose it again.");
        if (!OwnsProfile(project, profile)) throw new InvalidDataException("Voice profile belongs to another character.");
        return profile;
    }

    internal static async Task<Result?> StageAsync(NativeSuitProject project, string workspace, string contentRoot, Action<string> log, CancellationToken cancellation,
        Dictionary<string, byte[]>? sharedMedia = null)
    {
        if (string.IsNullOrEmpty(project.VoiceProfileId)) return null;
        var profile = Selected(project, workspace);
        CharacterVoiceLevelService.Validate(profile.GainDb);
        if (profile.Schema is not (1 or 2) || profile.Assignments.Count == 0 || profile.Assignments.Count > 10000 || profile.Assignments.Select(a => a.LineId).Distinct().Count() != profile.Assignments.Count)
            throw new InvalidDataException("The selected voice profile is empty, invalid or has duplicate targets.");
        var snapshot = CharacterVoiceCatalogService.Inspect(project, cancellation);
        if (profile.Donor != snapshot.Donor || profile.NativeVoice != snapshot.VoiceActor || snapshot.Warnings.Length > 0)
            throw new InvalidDataException("Voice donor changed or native discovery is incomplete. Refresh the profile before building. " + string.Join("; ", snapshot.Warnings));
        foreach (var assignment in profile.Assignments)
        {
            if (!CharacterVoiceLibraryService.HashId(assignment.LineId) || (assignment.Silent ? assignment.ClipId != "" : !CharacterVoiceLibraryService.HashId(assignment.ClipId)))
                throw new InvalidDataException("Invalid voice assignment or silence choice.");
            if (!snapshot.Lines.Any(l => l.Id == assignment.LineId && l.EventPackage == assignment.Event && l.Media == assignment.Media && l.OwnSpeaker))
                throw new InvalidDataException("Stale or partner voice target: " + assignment.Media);
        }
        var encoder = AppSettings.Current.VoiceEncoderExePath;
        var decoder = AppSettings.Current.EffectiveVgmstreamExePath();
        if (!File.Exists(encoder) || !File.Exists(decoder)) throw new InvalidDataException("Configure wav2wem and vgmstream in Character voices before building a voice profile.");
        var unrealPak = Path.Combine(AppSettings.Current.UnrealEngineRoot ?? "", "Engine/Binaries/Win64/UnrealPak.exe");
        if (!File.Exists(unrealPak)) throw new InvalidDataException("Voice packaging requires UnrealPak from the configured Unreal Engine installation.");
        var playable = project.TargetPackages.Playable;
        if (!playable.StartsWith("/Game/Mods/", StringComparison.Ordinal) || playable.Split('/').Length < 6 || playable.Contains(".."))
            throw new InvalidDataException("Voices can only be built for a private /Game/Mods character package.");
        var definition = profile.CharacterId == project.SlotId ? null : new SuitProjectService(workspace).LoadProject(
            new SuitProjectService(workspace).ProjectPathForSlot(profile.CharacterId));
        var identity = BuildIdentity(project, profile, definition);
        var prefix = identity.Root + "/Voice/" + profile.Id;
        var actor = "BCVoice_" + Digest(identity.Owner + "|" + profile.Id)[..24];
        var work = Path.Combine(Path.GetDirectoryName(contentRoot)!, "VoiceBuild");Directory.CreateDirectory(work);
        var native = Path.Combine(work, "Native");
        var maps = MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        using var stock = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
        stock.ReadScriptData = true;
        JArray Exports(string package) => CharacterVoiceCatalogService.Exports(stock, package);
        string[] References(JContainer data, string marker) => data.DescendantsAndSelf().OfType<JProperty>().Where(p => p.Name == "ObjectPath")
            .Select(p => CharacterVoiceCatalogService.Package(p.Value.ToString())).Where(p => p.Contains(marker, StringComparison.Ordinal)).Distinct().ToArray();
        var tags = References(Exports(snapshot.Donor), "/Audio/Tagging/DataEntries/");
        var cards = tags.SelectMany(t => References(Exports(t), "/DataCards/Dialogue/")).Distinct().ToArray();
        var events = cards.SelectMany(c => References(Exports(c), "/Wub/DialogueEvents/")).Distinct().ToArray();
        if (tags.Length == 0 || cards.Length == 0 || events.Length == 0) throw new InvalidDataException("No complete native voice routing graph found.");
        var packages = tags.Concat(cards).Concat(events).Distinct().ToArray();
        var paths = packages.ToDictionary(p => p, p => prefix + "/V_" + Digest(p)[..24]);
        var redirects = new Dictionary<string, string>(paths);
        foreach (var pair in paths)
        {
            var leaf = Path.GetFileName(pair.Key);
            if (redirects.TryGetValue(leaf, out var other) && other != Path.GetFileName(pair.Value)) throw new InvalidDataException("Ambiguous native voice asset names.");
            redirects[leaf] = Path.GetFileName(pair.Value);
        }
        async Task<string> NativeFile(string package)
        {
            var extracted = ExtractedPackagePathService.ResolvePackageUasset(AppSettings.Current.EffectiveExtractedContentRoot(), package);
            if (File.Exists(extracted)) return extracted!;
            var cached = ExtractedPackagePathService.ResolvePackageUasset(Path.Combine(native, "LEGOBatmanLotDK/Content"), package);
            if (File.Exists(cached)) return cached!;
            // Each missing package is extracted from the configured game; never use imported character assets as templates.
            await Run(AppSettings.Current.EffectiveRetocExePath(), ["to-legacy", "--version", "UE5_6", "--no-shaders", "--no-script-objects", "-f", package[6..], AppSettings.Current.EffectiveGamePaksRoot(), native], work, cancellation);
            return ExtractedPackagePathService.ResolvePackageUasset(Path.Combine(native, "LEGOBatmanLotDK/Content"), package)
                ?? throw new InvalidDataException("Could not extract native voice asset: " + package);
        }
        UAsset Read(string file) => new(file, EngineVersion.VER_UE5_6, maps, CustomSerializationFlags.SkipPreloadDependencyLoading);
        string Output(string package)
        {
            if (!package.StartsWith(prefix + "/", StringComparison.Ordinal) && package != project.TargetPackages.Playable && package != project.TargetPackages.Cutscene)
                throw new InvalidDataException("Voice output is not owned by this suit.");
            if (stock.Files.ContainsKey("LEGOBatmanLotDK/Content/" + package[6..] + ".uasset")) throw new InvalidDataException("Voice build would replace a native asset.");
            var file = Path.GetFullPath(Path.Combine(contentRoot, package[6..] + ".uasset"));
            if (!FileSystemPathUtil.IsWithinDirectory(file, contentRoot)) throw new InvalidDataException("Voice path escaped staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);return file;
        }
        var library = new CharacterVoiceLibraryService(workspace);
        var encoded = new Dictionary<string, string>();var mediaFiles = new List<string>();var mediaMounts = new List<string>();
        var mountedMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void RegisterMedia(CharacterVoiceLibraryService.Assignment assignment, string name)
        {
            var shared = Path.Combine(contentRoot, "Wub/Platforms/Windows/Media", name + ".wem");
            var sharedMount = "LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Media/" + name + ".wem";
            if (mountedMedia.Add(sharedMount)) { mediaFiles.Add(shared);mediaMounts.Add(sharedMount); }
            // Imported recordings are explicit profile choices, not translated audio.
            // Preserve every observed native language lookup route for that target.
            // The filenames are suit-private; no native localization media is replaced.
            foreach (var mount in LocalizedMediaMounts(stock.Files.Keys, assignment.Media, name))
            {
                if (!mountedMedia.Add(mount)) continue;
                if (stock.Files.ContainsKey(mount)) throw new InvalidDataException("Native localized media collision.");
                var file = Path.Combine(work, "LocalizedMedia", mount.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.Copy(shared, file, overwrite: false);
                mediaFiles.Add(file);mediaMounts.Add(mount);
            }
        }
        async Task<string> Media(CharacterVoiceLibraryService.Assignment assignment)
        {
            var key = assignment.Silent ? "silence" : assignment.ClipId;
            if (encoded.TryGetValue(key, out var existing)) { RegisterMedia(assignment, existing);return existing; }
            var levelIdentity = profile.GainDb == 0 ? "" : "|gain-v3:" + profile.GainDb.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var name = "BCV_" + Digest(identity.Owner + "|" + profile.Id + "|" + key + levelIdentity);
            if (stock.Files.Keys.Any(f => f.EndsWith('/' + name + ".wem", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Native media collision.");
            var output = Path.Combine(contentRoot, "Wub/Platforms/Windows/Media", name + ".wem");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (sharedMedia?.TryGetValue(name, out var reused) == true)
            {
                using var target = File.Open(output, FileMode.CreateNew, FileAccess.Write); target.Write(reused);
                target.Dispose(); encoded[key] = name; RegisterMedia(assignment, name); return name;
            }
            using var codec = new CodecWorkspace(AppSettings.RuntimeRoot, Path.GetTempPath());
            var input = codec.Input;
            if (assignment.Silent)
            {
                if (assignment.ClipId != "") throw new InvalidDataException("Silenced lines cannot contain recordings.");
                using var writer = new BinaryWriter(File.Create(input));const int samples = 4800;
                writer.Write("RIFF"u8);writer.Write(36 + samples * 2);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(48000);writer.Write(96000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(samples * 2);writer.Write(new byte[samples * 2]);
            }
            else
            {
                var bytes = File.ReadAllBytes(library.ClipPath(key));
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(key, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Voice recording hash changed: " + key);
                File.WriteAllBytes(input, CharacterVoiceLevelService.Apply(bytes, profile.GainDb));
            }
            await Run(encoder!, ["-resample", "48000", "-q", "4", "-o", codec.Encoded, input], codec.Folder, cancellation, work);
            var decoded = codec.Decoded;await Run(decoder!, ["-o", decoded, codec.Encoded], codec.Folder, cancellation, work);
            // Lossy reconstruction may overshoot a sample-limited WAV. Check the actual
            // game encoding, then add only the headroom needed by that recording.
            if(profile.GainDb>0&&!assignment.Silent)
            {
                int headroom=0;
                while(CharacterVoiceLevelService.HasClippedSamples(File.ReadAllBytes(decoded)))
                {
                    if(++headroom>5)throw new InvalidDataException("Boosted voice still clips after encoding: "+key);
                    File.WriteAllBytes(input,CharacterVoiceLevelService.Apply(File.ReadAllBytes(input),-1));
                    await Run(encoder!, ["-resample", "48000", "-q", "4", "-o", codec.Encoded, input], codec.Folder, cancellation, work);
                    await Run(decoder!, ["-o", decoded, codec.Encoded], codec.Folder, cancellation, work);
                }
                if(headroom>0)log($"Voice: added {headroom} dB codec headroom to {key[..8]}.");
            }
            var before = CharacterVoiceLibraryService.InspectWav(File.ReadAllBytes(input));var after = CharacterVoiceLibraryService.InspectWav(File.ReadAllBytes(decoded));
            if (Math.Abs(before.Seconds - after.Seconds) > .03 || new FileInfo(codec.Encoded).Length < 44) throw new InvalidDataException("Encoded voice duration/format verification failed.");
            cancellation.ThrowIfCancellationRequested();
            // Normalize only freshly encoded private output, never donor media.
            using(var target=File.Open(output,FileMode.CreateNew,FileAccess.Write))
                target.Write(PrivateWemSetupService.Normalize(File.ReadAllBytes(codec.Encoded)));
            if (sharedMedia is not null) sharedMedia[name] = File.ReadAllBytes(output);
            encoded[key] = name;RegisterMedia(assignment, name);return name;
        }
        var expected = new Dictionary<string, JArray>();
        foreach (var package in packages)
        {
            cancellation.ThrowIfCancellationRequested();log("Voice: cloning " + Path.GetFileName(package));
            var data = Exports(package);var names = new Dictionary<string, string>(redirects);
            if (events.Contains(package))
            {
                var lines = CharacterVoiceCatalogService.ParseLines(data, package, "", snapshot.VoiceActor);
                foreach (var group in lines.GroupBy(l => l.Media))
                {
                    var changes = group.Select(l => profile.Assignments.SingleOrDefault(a => a.LineId == l.Id)).ToArray();
                    if (changes.All(a => a is null)) continue;
                    if (group.Any(l => !l.OwnSpeaker) || changes.Any(a => a is null) || changes.Select(a => (a!.ClipId, a.Silent)).Distinct().Count() != 1)
                        throw new InvalidDataException("Shared recording branches need the same choice on all linked lines: " + group.Key);
                    names[group.Key] = await Media(changes[0]!);
                }
                // Explicit speaker and self-associated actor references follow the private identity.
                // Default speakers stay contextual; partner actors and unrelated condition fields stay intact.
                if (lines.Any(l => l.Speaker == snapshot.VoiceActor)) names[snapshot.VoiceActor] = actor;
            }
            var asset = Read(await NativeFile(package));
            var extras = asset.Exports.Select(e => e.Extras.ToArray()).ToArray();
            Rename(asset, names);asset.FolderName = new FString(paths[package]);asset.Write(Output(paths[package]));
            var reloaded = Read(Output(paths[package]));
            if (!reloaded.Exports.Select((e, i) => e.Extras.SequenceEqual(extras[i])).All(x => x)) throw new InvalidDataException("Voice binary payload changed unexpectedly.");
            var wanted = (JArray)data.DeepClone();
            foreach (var value in wanted.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String))
            {
                var text = value.Value<string>()!;
                if (events.Contains(package) && value.Parent is JProperty { Name: "Character" or "AssociatedActor" } && text == snapshot.VoiceActor && names.ContainsKey(text)) value.Value = actor;
                else if (value.Parent is JProperty { Name: "WemName" } && names.TryGetValue(text, out var media)) value.Value = media;
                else value.Value = RedirectText(text, redirects);
            }
            expected[paths[package]] = wanted;
        }
        var ownedBlueprints = new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene }.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
        using var authoring = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!, [contentRoot]);
        foreach (var package in ownedBlueprints)
        {
            var file = Output(package);if (!File.Exists(file)) throw new InvalidDataException("Build the suit's current character stage before adding voices: " + package);
            var asset = Read(file);NativeBlueprintSchemaService.EnsureParents(asset);
            var voice = asset.Exports.OfType<NormalExport>().Where(e => e.ObjectName.ToString() == "WubDialogueVoiceActor_GEN_VARIABLE")
                .SelectMany(e => e.Data).OfType<NamePropertyData>().SingleOrDefault(p => p.Name.ToString() == "VoiceActorName");
            if (voice is null)
            {
                if (package == playable) throw new InvalidDataException("This character has no supported explicit voice component: " + package);
                log("Voice: cutscene scaffold has no gameplay voice component; leaving it unchanged.");continue;
            }
            if (!tags.Any(t => asset.GetNameMapIndexList().Any(n => n.ToString() == t))) throw new InvalidDataException("Character voice routing does not match the selected donor.");
            var wanted = CharacterVoiceCatalogService.Exports(authoring, package);
            foreach (var value in wanted.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String))
                value.Value = value.Parent is JProperty { Name: "VoiceActorName" } ? actor : RedirectText(value.Value<string>()!, redirects);
            expected[package] = wanted;
            Rename(asset, redirects);voice.Value = new FName(asset, actor);asset.Write(file);
        }
        using var verify = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!, [contentRoot]);verify.ReadScriptData = true;
        foreach (var pair in expected)
        {
            var actual = CharacterVoiceCatalogService.Exports(verify, pair.Key);
            if (!JToken.DeepEquals(CanonicalPaths(pair.Value), CanonicalPaths(actual)))
            {
                File.WriteAllText(Path.Combine(work, "expected.json"), pair.Value.ToString());File.WriteAllText(Path.Combine(work, "actual.json"), actual.ToString());
                throw new InvalidDataException("Private voice clone changed unsupported fields: " + pair.Key + ". No mod was packaged.");
            }
        }
        // Shared media is read back through the loose Content provider; localized
        // copies are outside Content and are verified again through the packed pak.
        for (int i = 0; i < mediaFiles.Count; i++)
            if (mediaMounts[i].StartsWith("LEGOBatmanLotDK/Content/", StringComparison.Ordinal) &&
                !verify.SaveAsset(mediaMounts[i]).SequenceEqual(File.ReadAllBytes(mediaFiles[i]))) throw new InvalidDataException("Staged media cannot be resolved.");
        var report = Path.Combine(work, "voice-build.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { profile.Id, profile.Name, profile.GainDb, project.SlotId, Actor = actor, Assignments = profile.Assignments.Count, Silent = profile.Assignments.Count(a => a.Silent), PrivatePackages = paths, Media = mediaFiles.Select((f,i) => new { Path = mediaMounts[i], Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) }), NativeReplacements = 0, RuntimeTested = false }, CharacterVoiceLibraryService.Json));
        log($"Voice: {profile.Assignments.Count} assignments, {encoded.Count} private recordings, {mediaFiles.Count} shared/localized media paths; no native replacements.");
        return new(actor, [..paths.Values, ..ownedBlueprints], mediaFiles.ToArray(), report, mediaMounts.ToArray());
    }
    internal static JArray CanonicalPaths(JArray exports)
    {
        var result = (JArray)exports.DeepClone();
        foreach (var value in result.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String))
        {
            var text = value.Value<string>()!;
            const string contentPrefix = "LEGOBatmanLotDK/Content/";
            if (text.StartsWith(contentPrefix, StringComparison.Ordinal)) value.Value = "/Game/" + text[contentPrefix.Length..];
        }
        return result;
    }
    internal static string RedirectText(string text, Dictionary<string, string> names)
    {
        if (names.TryGetValue(text, out var exact)) return exact;
        foreach (var pair in names.OrderByDescending(p => p.Key.Length))
            text = text.Replace(pair.Key + ".", pair.Value + ".", StringComparison.Ordinal).Replace("'" + pair.Key + "'", "'" + pair.Value + "'", StringComparison.Ordinal);
        // ObjectPath commonly includes the package followed by its exported object name.
        foreach (var pair in names.Where(p => !p.Key.Contains('/'))) if (text.EndsWith('.' + pair.Key, StringComparison.Ordinal)) text = text[..^pair.Key.Length] + pair.Value;
        return text;
    }
    private static void Rename(UAsset asset, Dictionary<string, string> names)
    {
        for (int i = 0; i < asset.GetNameMapIndexList().Count; i++) if (names.TryGetValue(asset.GetNameReference(i).ToString(), out var name)) asset.SetNameReference(i, new FString(name));
    }
    internal static async Task PackageMediaAsync(Result? result, string pak, CancellationToken cancellation)
    {
        if (result is null || result.MediaFiles.Length == 0) return;
        var work = Path.Combine(Path.GetDirectoryName(result.ReportPath)!, "Pak-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        var exe = Path.Combine(AppSettings.Current.UnrealEngineRoot ?? "", "Engine/Binaries/Win64/UnrealPak.exe");
        var raw = Path.Combine(work, "Existing");Directory.CreateDirectory(raw);
        var original = Path.Combine(work, "Original");Directory.CreateDirectory(original);File.Copy(pak, Path.Combine(original, "original.pak"));
        var originalHash = SHA256.HashData(File.ReadAllBytes(pak));
        DefaultFileProvider ReadPak(string folder)
        {
            var provider = new DefaultFileProvider(folder, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE5_6), StringComparer.OrdinalIgnoreCase);
            provider.Initialize();provider.SubmitKey(new FGuid(), new FAesKey(new byte[32]));return provider;
        }
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var existing = ReadPak(original))
        foreach (var key in existing.Files.Keys)
        {
            var file = Path.GetFullPath(Path.Combine(raw, key));
            if (!FileSystemPathUtil.IsWithinDirectory(file, raw) || key.Contains('"')) throw new InvalidDataException("Invalid existing pak path.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file, existing.SaveAsset(key));inputs.Add(key, file);
        }
        var mounts = MountPaths(result);
        for (int i = 0; i < result.MediaFiles.Length; i++)
        {
            var file = result.MediaFiles[i];var key = mounts[i];
            if (inputs.TryGetValue(key, out var previous) && !File.ReadAllBytes(previous).SequenceEqual(File.ReadAllBytes(file))) throw new InvalidDataException("Voice media conflicts with an existing pak entry.");
            inputs[key] = file;
        }
        var entries = inputs.Select(p => $"\"{p.Value}\" \"../../../{p.Key}\"").ToArray();
        var response = Path.Combine(work, "response.txt");File.WriteAllLines(response, entries);
        var nextFolder = Path.Combine(work, "New");Directory.CreateDirectory(nextFolder);
        var next = Path.Combine(nextFolder, "with-voice.pak");await Run(exe, [next, "-Create=" + response], work, cancellation);
        using (var readback = ReadPak(nextFolder))
        {
            if (readback.Files.Count != inputs.Count) throw new InvalidDataException("Unexpected voice pak footprint.");
            foreach (var pair in inputs)
                if (!readback.SaveAsset(pair.Key).SequenceEqual(File.ReadAllBytes(pair.Value))) throw new InvalidDataException("Voice pak media or existing entry readback failed.");
        }
        if (!SHA256.HashData(File.ReadAllBytes(pak)).SequenceEqual(originalHash)) throw new InvalidDataException("The original pak changed during voice packaging.");
        File.Copy(next, pak, overwrite: true);
    }
    private static async Task Run(string exe, IEnumerable<string> arguments, string work, CancellationToken cancellation, string? logDirectory = null)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };RetocRuntime.Configure(info);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start " + exe);
        var stdout = process.StandardOutput.ReadToEndAsync();var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellation); } catch { if (!process.HasExited) process.Kill(true);throw; }
        var output = await stdout + "\n" + await stderr;
        File.AppendAllText(Path.Combine(logDirectory ?? work, "tools.log"), Path.GetFileName(exe) + " " + string.Join(" ", arguments) + "\n" + output + "\n");
        if (process.ExitCode != 0) throw new InvalidDataException(Path.GetFileName(exe) + " failed: " + output[^Math.Min(output.Length, 1800)..]);
    }
}
