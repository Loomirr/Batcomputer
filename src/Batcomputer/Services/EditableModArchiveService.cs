using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Batcomputer;

/// <summary>
/// Moves authoring projects between Batcomputer workspaces. A release ZIP is for
/// players; this archive deliberately contains editable JSON plus the local source
/// files that the recipes point at. Native game packages remain references and are
/// never redistributed.
/// </summary>
public sealed class EditableModArchiveService
{
    private const string ManifestName = "batcomputer-editable-mod.json";
    private const int Schema = 2;
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Manifest
    {
        public int SchemaVersion { get; set; } = Schema;
        public string Format { get; set; } = "batcomputer-editable-mod";
        public string ModId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ExportedUtc { get; set; } = "";
        public List<string> SuitIds { get; set; } = [];
        public List<string> VehicleIds { get; set; } = [];
        // Original authoring path -> archive member. Only exact values are rebased.
        public Dictionary<string, string> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> SourceDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // Shared-material library package files are copied to their normal library
        // location on import; recipe package paths intentionally stay unchanged.
        public Dictionary<string, string> MaterialLibraryFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // Member -> path relative to Generated; only declared suit/vehicle caches are accepted.
        public Dictionary<string, string> ProjectFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record ExportResult(string ArchivePath, int Suits, int Vehicles, int SourceFiles, long SourceBytes);
    public sealed record ImportResult(string ModProjectPath, string ModId, int Suits, int Vehicles, int SourceFiles, string SourceFolder);

    private readonly string _projectRoot;
    private readonly ModProjectService _mods;
    private readonly SuitProjectService _suits;
    private readonly VehicleProjectService _vehicles;

    public EditableModArchiveService(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _mods = new ModProjectService(_projectRoot);
        _suits = new SuitProjectService(_projectRoot);
        _vehicles = new VehicleProjectService(_projectRoot);
    }

    public ExportResult Export(string modProjectPath, string archivePath)
    {
        var mod = _mods.LoadMod(modProjectPath) ?? throw new InvalidDataException("Could not load the selected mod project.");
        ModProjectService.ApplyDerivedFields(mod);
        if (string.IsNullOrWhiteSpace(mod.ModId)) throw new InvalidDataException("The mod has no Mod ID.");
        var destination = Path.GetFullPath(archivePath);
        if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) destination += ".zip";
        if (File.Exists(destination)) throw new IOException("Choose a new filename; Batcomputer will not overwrite an editable archive.");

        var suitFiles = new List<(ModSuitEntry Entry, string Path, NativeSuitProject Project)>();
        foreach (var entry in mod.Suits)
        {
            var path = _mods.ResolveSuitProjectPath(entry);
            var project = _suits.LoadProject(path) ?? throw new InvalidDataException($"Could not read suit '{entry.SuitId}'. Fix or remove it before exporting.");
            suitFiles.Add((entry, path, project));
        }
        var vehicleFiles = new List<(ModVehicleEntry Entry, string Path, VehicleProject Project)>();
        foreach (var entry in mod.Vehicles)
        {
            var path = _vehicles.Resolve(entry);
            vehicleFiles.Add((entry, path, _vehicles.Load(path)));
        }
        // Include character definitions required by child suits and custom vehicle owners.
        foreach (var vehicle in vehicleFiles.Where(v => !string.IsNullOrWhiteSpace(v.Project.OwnerCharacterProjectPath)))
        {
            var entry = new ModSuitEntry { SuitProjectPath = vehicle.Project.OwnerCharacterProjectPath, Enabled = vehicle.Entry.Enabled };
            var owner = _suits.LoadProject(_mods.ResolveSuitProjectPath(entry))
                ?? throw new InvalidDataException("Missing custom vehicle owner: " + vehicle.Project.DisplayName);
            entry.SuitId = owner.SlotId;
            if (!mod.Suits.Any(e => e.SuitId.Equals(entry.SuitId, StringComparison.OrdinalIgnoreCase))) mod.Suits.Add(entry);
        }
        var expanded = CustomCharacterProjectService.ExpandMembers(mod.Suits, _mods, _suits);
        foreach (var entry in expanded)
            if (!mod.Suits.Any(e => e.SuitId.Equals(entry.SuitId, StringComparison.OrdinalIgnoreCase))) mod.Suits.Add(entry);
        foreach (var suit in suitFiles.Where(s => s.Project.CustomCharacter is { IsDefinition: false }))
        {
            var identity = suit.Project.CustomCharacter!;
            if (mod.Suits.Any(e => e.SuitId.Equals(identity.DefinitionSlotId, StringComparison.OrdinalIgnoreCase))) continue;
            var definitionPath = _suits.ProjectPathForSlot(identity.DefinitionSlotId);
            var definition = _suits.LoadProject(definitionPath);
            if (definition?.CustomCharacter is not { IsDefinition: true } owner || owner.CharacterId != identity.CharacterId)
                throw new InvalidDataException("Missing character definition for " + suit.Project.DisplayName);
            mod.Suits.Add(new() { SuitId = definition.SlotId, SuitProjectPath = _mods.MakeRelativeSuitProjectPath(definitionPath), Enabled = suit.Entry.Enabled });
        }
        foreach (var entry in mod.Suits)
            if (!suitFiles.Any(s => s.Project.SlotId.Equals(entry.SuitId, StringComparison.OrdinalIgnoreCase)))
            {
                var path = _mods.ResolveSuitProjectPath(entry);
                suitFiles.Add((entry, path, _suits.LoadProject(path) ?? throw new InvalidDataException("Missing suit dependency: " + entry.SuitId)));
            }

        var manifest = new Manifest
        {
            ModId = mod.ModId,
            DisplayName = mod.DisplayName,
            ExportedUtc = DateTime.UtcNow.ToString("O"),
            SuitIds = suitFiles.Select(x => x.Project.SlotId).ToList(),
            VehicleIds = vehicleFiles.Select(x => x.Project.Id).ToList(),
        };
        var sourceCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectLocalSources(JsonSerializer.Serialize(mod, Json), sourceCandidates, directories);
        foreach (var suit in suitFiles)
        {
            var recipe = JsonSerializer.Serialize(suit.Project, Json);
            CollectLocalSources(recipe, sourceCandidates, directories);
            CollectProjectSources(recipe, _suits.ProjectOutputDirectory(suit.Project), projectSources);
        }
        foreach (var vehicle in vehicleFiles)
        {
            var recipe = JsonSerializer.Serialize(vehicle.Project, Json);
            CollectLocalSources(recipe, sourceCandidates, directories);
            CollectProjectSources(recipe, _vehicles.DirectoryFor(vehicle.Project), projectSources);
        }
        var materialRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in suitFiles.Select(s => JsonSerializer.Serialize(s.Project, Json)).Concat(vehicleFiles.Select(v => JsonSerializer.Serialize(v.Project, Json))))
            Walk(JsonNode.Parse(recipe), (name, value) =>
            {
                if (name == "replacementPackage" && value.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("This project uses an imported animation library asset. Editable sharing does not yet transfer animation libraries: " + value);
                if ((name.Contains("material", StringComparison.OrdinalIgnoreCase) || name == "miPackagePath") && value.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) materialRoots.Add(value);
            });
        foreach (var material in suitFiles.SelectMany(s => s.Project.GeneratedMaterials)) materialRoots.Add(material.PackagePath);
        var materialSources = CollectMaterialLibraryFiles(materialRoots);
        foreach (var source in materialSources.Keys) sourceCandidates.Remove(source);
        var sources = ExpandSources(sourceCandidates);
        long bytes = sources.Concat(materialSources.Keys).Concat(projectSources.Keys).Sum(path => new FileInfo(path).Length);
        if (bytes > MaxArchiveBytes) throw new InvalidDataException("Editable source files exceed the 2 GB sharing limit. Remove an unneeded source or export smaller projects separately.");
        var folders = sources.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var directory in directories)
            manifest.SourceDirectories[directory] = "sources/tree" + (manifest.SourceDirectories.Count + 1).ToString("D4");
        foreach (var directory in directories.OrderBy(p => p.Length))
        {
            var parent = manifest.SourceDirectories.Where(d => d.Key != directory && FileSystemPathUtil.IsWithinDirectory(directory, d.Key))
                .OrderBy(d => d.Key.Length).FirstOrDefault();
            if (parent.Key is not null) manifest.SourceDirectories[directory] = parent.Value + "/" + Path.GetRelativePath(parent.Key, directory).Replace('\\', '/');
        }
        var sourceMembers = sources.Select(source =>
            (Source: source, Member: "sources/" + (folders.IndexOf(Path.GetDirectoryName(source)) + 1).ToString("D4") + "/" + Path.GetFileName(source))).ToList();
        for (var i = 0; i < sourceMembers.Count; i++)
        {
            var source = sourceMembers[i].Source;
            var parent = manifest.SourceDirectories.OrderBy(d => d.Key.Length).FirstOrDefault(d => FileSystemPathUtil.IsWithinDirectory(source, d.Key));
            if (parent.Key is not null) sourceMembers[i] = (source, parent.Value + "/" + Path.GetRelativePath(parent.Key, source).Replace('\\', '/'));
        }
        foreach (var source in sourceMembers) manifest.Sources[source.Source] = source.Member;
        foreach (var source in materialSources) manifest.MaterialLibraryFiles[source.Key] = source.Value;
        foreach (var source in projectSources) manifest.ProjectFiles["cache/" + source.Value.Replace('\\', '/')] = source.Value.Replace('\\', '/');
        ValidateIds(manifest);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        AddText(archive, ManifestName, JsonSerializer.Serialize(manifest, Json));
        mod.PreviousModIds.Clear();
        AddText(archive, "projects/mod.native-suit-mod-project.json", JsonSerializer.Serialize(mod, Json));
        foreach (var suit in suitFiles)
            AddText(archive, "projects/suits/" + SafeName(suit.Project.SlotId) + ".native-suit-project.json", JsonSerializer.Serialize(suit.Project, Json));
        foreach (var vehicle in vehicleFiles)
            AddText(archive, "projects/vehicles/" + SafeName(vehicle.Project.Id) + ".vehicle-project.json", JsonSerializer.Serialize(vehicle.Project, Json));
        foreach (var source in sourceMembers)
        {
            AddFile(archive, source.Member, source.Source);
        }
        foreach (var material in materialSources) AddFile(archive, material.Value, material.Key);
        foreach (var source in projectSources) AddFile(archive, "cache/" + source.Value.Replace('\\', '/'), source.Key);
        }
        File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new ExportResult(destination, suitFiles.Count, vehicleFiles.Count, sources.Count + materialSources.Count + projectSources.Count, bytes);
    }

    public ImportResult Import(string archivePath)
    {
        var archiveFile = Path.GetFullPath(archivePath);
        if (!File.Exists(archiveFile)) throw new FileNotFoundException("The editable archive does not exist.", archiveFile);
        using var archive = ZipFile.OpenRead(archiveFile);
        if (archive.Entries.Count > 20000 || archive.Entries.Sum(e => e.Length) > MaxArchiveBytes)
            throw new InvalidDataException("Editable archive exceeds the sharing size or file-count limit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateEntry(entry);
            if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate archive member: " + entry.FullName);
        }
        var manifest = ReadJson<Manifest>(archive, ManifestName);
        if (manifest is null || manifest.SchemaVersion != Schema || manifest.Format != "batcomputer-editable-mod" || string.IsNullOrWhiteSpace(manifest.ModId))
            throw new InvalidDataException("This is not a supported Batcomputer editable-mod archive. Re-export older creator archives using the current Batcomputer version.");
        ValidateIds(manifest);
        if (_mods.ListMods().Any(m => m.ModId.Equals(manifest.ModId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"This workspace already has a mod with ID '{manifest.ModId}'. Import into a clean workspace, or change one mod's ID before sharing it.");
        if (_suits.ListProjectFiles().Any(p => manifest.SuitIds.Contains(p.SlotId, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("This workspace already has one of the archive's suit IDs. Import is blocked to avoid a silent identity/package collision.");
        if (_vehicles.List().Any(v => manifest.VehicleIds.Contains(v.Id, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("This workspace already has one of the archive's vehicle IDs. Import is blocked to avoid a silent identity/package collision.");
        var modDestination = MemberTarget(_mods.ModOutputRoot, ModProjectService.DeriveModId(manifest.ModId) + ".native-suit-mod-project.json", "");
        if (File.Exists(modDestination)) throw new InvalidDataException("The mod project filename already exists.");
        var pawnTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actorPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ReserveIdentity(NativeSuitProject project)
        {
            if (!string.IsNullOrWhiteSpace(project.PawnTag) && !pawnTags.Add(project.PawnTag)) throw new InvalidDataException("A suit already uses pawn tag " + project.PawnTag);
            foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene, project.TargetPackages.Dcmd }.Where(p => !string.IsNullOrWhiteSpace(p)))
                if (!actorPackages.Add(package)) throw new InvalidDataException("A suit already uses actor/metadata package " + package);
        }
        foreach (var existing in _suits.ListProjectFiles())
        {
            var project = _suits.LoadProject(existing.Path);
            if (project is null) continue;
            if (!string.IsNullOrWhiteSpace(project.PawnTag)) pawnTags.Add(project.PawnTag);
            foreach (var package in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene, project.TargetPackages.Dcmd }.Where(p => !string.IsNullOrWhiteSpace(p))) actorPackages.Add(package);
        }

        var sourceFolder = Path.Combine(AppSettings.GeneratedRootFor(_projectRoot), "ImportedModSources", SafeName(manifest.ModId), Guid.NewGuid().ToString("N"));
        var rebase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        try
        {
            foreach (var directory in manifest.SourceDirectories)
                rebase[directory.Key] = MemberTarget(sourceFolder, directory.Value, "sources/");
            foreach (var pair in manifest.Sources)
            {
                var entry = RequireEntry(archive, pair.Value);
                ValidateEntry(entry);
                var target = MemberTarget(sourceFolder, pair.Value, "sources/");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
                rebase[pair.Key] = target;
            }
            foreach (var pair in manifest.ProjectFiles)
            {
                if (pair.Key != "cache/" + pair.Value || !IsProjectCachePath(pair.Value, manifest))
                    throw new InvalidDataException("Invalid project cache member: " + pair.Key);
                var target = MemberTarget(AppSettings.GeneratedRootFor(_projectRoot), pair.Value, "");
                if (File.Exists(target)) throw new InvalidDataException("Project cache already exists: " + pair.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ExtractOwned(RequireEntry(archive, pair.Key), target, written);
            }
            foreach (var pair in manifest.MaterialLibraryFiles)
            {
                var entry = RequireEntry(archive, pair.Value);
                ValidateEntry(entry);
                var target = MemberTarget(new ToolMaterialLibraryService(_projectRoot).ContentRoot, pair.Value, "materials/Content/");
                if (!pair.Value.StartsWith("materials/Content/Mods/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only mod-local material packages may be imported.");
                if (File.Exists(target))
                {
                    using var existing = File.OpenRead(target);
                    using var incoming = entry.Open();
                    if (!System.Security.Cryptography.SHA256.HashData(existing).SequenceEqual(System.Security.Cryptography.SHA256.HashData(incoming)))
                        throw new InvalidDataException("A different material already uses this package: " + pair.Value);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ExtractOwned(entry, target, written);
            }
            var mod = ReadNode(archive, "projects/mod.native-suit-mod-project.json", rebase).Deserialize<NativeSuitModProject>(Json)
                ?? throw new InvalidDataException("Archive mod project is empty.");
            if (!mod.ModId.Equals(manifest.ModId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive manifest and mod project IDs do not match.");
            mod.PreviousModIds.Clear();
            var suitPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in manifest.SuitIds)
            {
                var project = ReadNode(archive, "projects/suits/" + SafeName(id) + ".native-suit-project.json", rebase).Deserialize<NativeSuitProject>(Json)
                    ?? throw new InvalidDataException("Archive suit is empty: " + id);
                if (!project.SlotId.Equals(id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive suit filename/identity mismatch: " + id);
                ReserveIdentity(project);
                var path = _suits.ProjectPathForSlot(project.SlotId);
                MemberTarget(Path.GetDirectoryName(path)!, Path.GetFileName(path), "");
                if (File.Exists(path)) throw new InvalidDataException("Import target already exists: " + Path.GetFileName(path));
                _suits.SaveProject(project); written.Add(path); suitPaths[id] = path;
            }
            var vehiclePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in manifest.VehicleIds)
            {
                var project = ReadNode(archive, "projects/vehicles/" + SafeName(id) + ".vehicle-project.json", rebase).Deserialize<VehicleProject>(Json)
                    ?? throw new InvalidDataException("Archive vehicle is empty: " + id);
                if (!project.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive vehicle filename/identity mismatch: " + id);
                if (!string.IsNullOrWhiteSpace(project.OwnerCharacterProjectPath))
                {
                    var owner = suitPaths.Values.Select(p => _suits.LoadProject(p)).SingleOrDefault(p => p?.CustomCharacter is { IsDefinition: true } identity && CustomCharacterProjectService.Scope(identity) == project.OwnerTag)
                        ?? throw new InvalidDataException("Archive is missing the vehicle's custom character owner.");
                    project.OwnerCharacterProjectPath = _mods.MakeRelativeSuitProjectPath(_suits.ProjectPathForSlot(owner.SlotId));
                }
                var path = _vehicles.ProjectPath(project.Id);
                MemberTarget(Path.GetDirectoryName(path)!, Path.GetFileName(path), "");
                if (File.Exists(path)) throw new InvalidDataException("Import target already exists: " + Path.GetFileName(path));
                _vehicles.Save(project); written.Add(path); vehiclePaths[id] = path;
            }
            foreach (var entry in mod.Suits)
            {
                if (!suitPaths.TryGetValue(entry.SuitId, out var path)) throw new InvalidDataException("Mod references a suit not included in this archive: " + entry.SuitId);
                entry.SuitProjectPath = _mods.MakeRelativeSuitProjectPath(path);
            }
            foreach (var entry in mod.Vehicles)
            {
                if (!vehiclePaths.TryGetValue(entry.VehicleId, out var path)) throw new InvalidDataException("Mod references a vehicle not included in this archive: " + entry.VehicleId);
                entry.VehicleProjectPath = Path.GetRelativePath(_projectRoot, path);
            }
            var modPath = _mods.SaveMod(mod); written.Add(modPath);
            return new ImportResult(modPath, mod.ModId, suitPaths.Count, vehiclePaths.Count, manifest.Sources.Count + manifest.MaterialLibraryFiles.Count + manifest.ProjectFiles.Count, sourceFolder);
        }
        catch
        {
            foreach (var path in written) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
            try { if (Directory.Exists(sourceFolder)) Directory.Delete(sourceFolder, recursive: true); } catch { }
            throw;
        }
    }

    private void CollectLocalSources(string json, ISet<string> candidates, ISet<string> directories)
    {
        var root = JsonNode.Parse(json);
        Walk(root, (name, value) =>
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (name is "coverImagePath" or "sourcePng" or "templateJson")
            {
                if (!File.Exists(value)) throw new InvalidDataException("Missing editable source: " + value);
                if (IsAuthoringFile(value)) candidates.Add(Path.GetFullPath(value));
            }
            else if (name is "outputRoot" or "ioStoreRoot" or "sourceRawRoot")
            {
                if (!Directory.Exists(value) || !IsAuthoringDirectory(name, value)) throw new InvalidDataException("Missing or external generated texture cache: " + value);
                directories.Add(Path.GetFullPath(value));
                foreach (var file in Directory.EnumerateFiles(value, "*", SearchOption.AllDirectories)) if (IsAuthoringFile(file)) candidates.Add(Path.GetFullPath(file));
            }
        });
    }

    private bool IsAuthoringDirectory(string name, string path) =>
        name is "outputRoot" or "ioStoreRoot" or "sourceRawRoot" && FileSystemPathUtil.IsWithinDirectory(path, AppSettings.GeneratedRootFor(_projectRoot));

    private bool IsAuthoringFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (new FileInfo(full).Length > MaxFileBytes) throw new InvalidDataException("An editable source is larger than 512 MB: " + full);
        var settings = AppSettings.Current;
        var excluded = new[] { settings.EffectiveExtractedContentRoot(), settings.EffectiveGamePaksRoot(), settings.UnrealEngineRoot };
        if (excluded.Any(root => !string.IsNullOrWhiteSpace(root) && FileSystemPathUtil.IsWithinDirectory(full, root))) return false;
        for (var current = new FileInfo(full) as FileSystemInfo; current is not null; current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Editable source passes through a file/directory link: " + full);
        return new[] { ".png", ".jpg", ".jpeg", ".tga", ".dds", ".bmp", ".obj", ".mtl", ".fbx", ".glb", ".gltf", ".bin", ".json", ".uasset", ".uexp", ".ubulk", ".uptnl", ".pak", ".utoc", ".ucas" }
            .Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ExpandSources(IEnumerable<string> candidates)
    {
        var files = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.ToArray())
        {
            var ext = Path.GetExtension(candidate);
            if (ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase))
                foreach (var sidecar in new[] { ".uexp", ".ubulk" }) if (File.Exists(Path.ChangeExtension(candidate, sidecar))) files.Add(Path.ChangeExtension(candidate, sidecar));
            if (ext.Equals(".obj", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(candidate, ".mtl"))) files.Add(Path.ChangeExtension(candidate, ".mtl"));
        }
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Dictionary<string, string> CollectMaterialLibraryFiles(IEnumerable<string> materials)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var library = new ToolMaterialLibraryService(_projectRoot);
        foreach (var material in materials)
        {
            var package = UnrealPathUtil.NormalizePackagePath(material);
            if (!package.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in library.EditablePackageFiles(package))
            {
                if (!IsAuthoringFile(file.Key)) throw new InvalidDataException("Invalid material source: " + file.Key);
                result[file.Key] = "materials/Content/" + file.Value;
            }
        }
        return result;
    }

    private static void Walk(JsonNode? node, Action<string, string> visitor)
    {
        if (node is JsonObject obj) foreach (var pair in obj) { if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) visitor(pair.Key, text); else Walk(pair.Value, visitor); }
        else if (node is JsonArray array) foreach (var item in array) Walk(item, visitor);
    }
    private static void Rebase(JsonNode? node, IReadOnlyDictionary<string, string> paths)
    {
        if (node is JsonObject obj) foreach (var pair in obj.ToList()) { if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && paths.TryGetValue(text, out var replacement)) obj[pair.Key] = replacement; else Rebase(pair.Value, paths); }
        else if (node is JsonArray array) for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && paths.TryGetValue(text, out var replacement)) array[i] = replacement;
            else Rebase(array[i], paths);
        }
    }
    private static JsonNode ReadNode(ZipArchive archive, string member, IReadOnlyDictionary<string, string> paths)
    {
        var node = JsonNode.Parse(ReadText(RequireEntry(archive, member))) ?? throw new InvalidDataException("Invalid JSON: " + member);
        Rebase(node, paths); return node;
    }
    private static T? ReadJson<T>(ZipArchive archive, string member) => JsonSerializer.Deserialize<T>(ReadText(RequireEntry(archive, member)), Json);
    private static string ReadText(ZipArchiveEntry entry) { if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Archive recipe is too large."); using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }
    private static void ExtractOwned(ZipArchiveEntry entry, string target, ICollection<string> written)
    {
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        written.Add(target);
        using var input = entry.Open();
        input.CopyTo(output);
    }
    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string member) => archive.GetEntry(member) ?? throw new InvalidDataException("Archive is missing: " + member);
    private static void AddText(ZipArchive archive, string member, string text) { using var writer = new StreamWriter(archive.CreateEntry(member, CompressionLevel.Optimal).Open()); writer.Write(text); }
    private static void AddFile(ZipArchive archive, string member, string path) => archive.CreateEntryFromFile(path, member, CompressionLevel.Optimal);
    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        ValidateRelative(entry.FullName);
        if (entry.Length > MaxFileBytes || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Unsafe or oversized archive member: " + entry.FullName);
    }
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || Path.IsPathRooted(path) ||
            path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Invalid archive path: " + path);
    }
    private static string MemberTarget(string root, string member, string prefix)
    {
        ValidateRelative(member);
        if (!member.StartsWith(prefix, StringComparison.Ordinal) || member.Length <= prefix.Length) throw new InvalidDataException("Unexpected archive member: " + member);
        var target = Path.GetFullPath(Path.Combine(root, member[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
        if (!FileSystemPathUtil.IsWithinDirectory(target, root)) throw new InvalidDataException("Archive path escaped its destination.");
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(target)!); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Import destination passes through a directory link: " + dir.FullName);
        return target;
    }
    private static void ValidateIds(Manifest manifest)
    {
        foreach (var ids in new[] { new[] { manifest.ModId }, manifest.SuitIds.ToArray(), manifest.VehicleIds.ToArray() })
        {
            if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length) throw new InvalidDataException("Duplicate project identities in archive.");
            foreach (var id in ids)
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || SafeName(id) != id) throw new InvalidDataException("Invalid archive identity: " + id);
        }
    }
    private static bool IsProjectCachePath(string path, Manifest manifest) =>
        manifest.SuitIds.Any(id => path.StartsWith("NativeSuitGuiProjects/" + id + "/", StringComparison.Ordinal)) ||
        manifest.VehicleIds.Any(id => path.StartsWith("VehicleProjects/" + id + "/", StringComparison.Ordinal));

    private void CollectProjectSources(string recipe, string directory, IDictionary<string, string> sources)
    {
        Walk(JsonNode.Parse(recipe), (name, value) =>
        {
            if (string.IsNullOrWhiteSpace(value) || name is not ("sourceRelativePath" or "sourceObjRelativePath" or "cacheRelativePath")) return;
            var relative = value.Replace('\\', '/');
            var path = MemberTarget(directory, relative, "");
            var files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : File.Exists(path) ? new[] { path } : throw new InvalidDataException("Missing editable mesh/icon cache: " + path);
            foreach (var file in ExpandSources(files))
                if (IsAuthoringFile(file)) sources[file] = Path.GetRelativePath(AppSettings.GeneratedRootFor(_projectRoot), file);
        });
    }
    private static string SafeName(string value) => string.Concat((value ?? "").Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}
