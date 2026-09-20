using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion;

namespace Batcomputer;

/// <summary>Shared existing-rig pipeline. Only verified cooked geometry enters a suit stage.</summary>
internal static class SkinnedMeshCookService
{
    internal const string Warning = "Your FBX must already be rigged and weighted to the selected game skeleton in Blender or another 3D editor. Batcomputer does not automatically rig or weight models. Export one joined mesh, preserve the native rest pose, and disable extra leaf bones. Cloth, morph targets and new rigs are not supported in this first pass.";
    internal sealed record CookManifest(string SourceHash, string Donor, string Skeleton, string Package,
        string TemporarySkeleton, string[] Slots, Dictionary<string, string> Files);

    internal static DefaultFileProvider OpenProvider(string? extraContainers = null)
    {
        var settings = AppSettings.Current;
        var roots = new List<DirectoryInfo>();
        var dlc = GameAssetRefreshService.DlcRootForPaksRoot(settings.GamePaksRoot!);
        if (Directory.Exists(dlc))
            roots.AddRange(Directory.EnumerateFiles(dlc, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file) is ".pak" or ".utoc")
                .Select(file => Path.GetDirectoryName(file)!).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new DirectoryInfo(path)));
        if (extraContainers is not null) roots.Add(new DirectoryInfo(extraContainers));
        var provider = new DefaultFileProvider(new DirectoryInfo(settings.GamePaksRoot!), roots.ToArray(),
            BaseGamePakSource.ShippedContainerSearchOption, new VersionContainer(EGame.GAME_UE5_6), StringComparer.OrdinalIgnoreCase);
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(settings.EffectiveUsmapPath()!);
        provider.Initialize(); provider.SubmitKey(new FGuid(), new FAesKey("0x" + new string('0', 64)));
        return provider;
    }

    internal static string ExportReference(string donor, string directory)
    {
        using var provider = OpenProvider();
        var mesh = provider.LoadPackageObject<USkeletalMesh>(donor);
        var exporter = new MeshExporter(mesh, new ExporterOptions
        { MeshFormat = EMeshFormat.Gltf2, LodFormat = ELodFormat.FirstLod, ExportMaterials = false, ExportMorphTargets = false });
        if (!exporter.TryWriteToDir(new DirectoryInfo(directory), out _, out var saved))
            throw new InvalidDataException("The native reference could not be exported.");
        SkinnedGlbExportService.CorrectFile(saved, mesh);
        return saved;
    }

    internal static async Task<SkinnedMeshImport> ImportAsync(string projectDirectory, string source, SkinnedMeshImport request,
        Action<string> log, CancellationToken cancellation = default)
    {
        var recipe = request.Clone();
        if (!float.IsFinite(recipe.ImportScale) || recipe.ImportScale is < 0.0001f or > 1000)
            throw new InvalidDataException("Import scale must be between 0.0001 and 1000.");
        RequireNativeDonor(recipe.DonorMeshPackage);
        if (!recipe.MeshPackage.StartsWith("/Game/Mods/", StringComparison.Ordinal) ||
            recipe.MeshPackage.Split('/').Skip(1).Any(s => !UnrealPathUtil.IsValidIdentifier(s)))
            throw new InvalidDataException("The custom mesh must use a valid suit-owned /Game/Mods package.");
        log("Checking source skin weights (no automatic repairs)…");
        var audit = SkinnedFbxValidator.Inspect(source);
        log($"Source: {audit.Vertices} vertices, {audit.Bones} bones. Checking UE 5.6 cooker…");
        var engine = AppSettings.Current.EffectiveUnrealEngineRoot();
        var editor = Path.Combine(engine, "Engine/Binaries/Win64/UnrealEditor-Cmd.exe");
        var versionPath = Path.Combine(engine, "Engine/Build/Build.version");
        if (!File.Exists(editor) || !File.Exists(versionPath))
            throw new InvalidDataException("Configure your Unreal Engine 5.6 installation in Settings before importing an FBX.");
        using (var version = JsonDocument.Parse(File.ReadAllText(versionPath)))
            if (version.RootElement.GetProperty("MajorVersion").GetInt32() != 5 || version.RootElement.GetProperty("MinorVersion").GetInt32() != 6)
                throw new InvalidDataException("This importer requires Unreal Engine 5.6, matching the game.");

        // New immutable revision: a failed/cancelled reimport cannot damage an existing working mesh.
        var revision = Path.Combine("ImportedSkinnedMeshes", Guid.NewGuid().ToString("N"));
        var root = SafePath(projectDirectory, revision); Directory.CreateDirectory(root);
        var sourceCopy = Path.Combine(root, "source.fbx"); File.Copy(source, sourceCopy);
        recipe.SourceRelativePath = Path.Combine(revision, "source.fbx");
        recipe.SourceSha256 = Hash(sourceCopy); recipe.CacheRelativePath = revision;
        // Unreal still checks cooked filenames against MAX_PATH. Keep its transient project
        // separate from the long, immutable suit/import cache path; logs and validated data stay there.
        using var workspace = new SkinnedCookWorkspace(recipe.MeshPackage);
        var cookRoot = workspace.Root; Directory.CreateDirectory(Path.Combine(cookRoot, "Config"));
        File.WriteAllText(Path.Combine(root, "cook-workspace.txt"), cookRoot);
        var project = Path.Combine(cookRoot, "SkinnedCook.uproject");
        File.WriteAllText(project, JsonSerializer.Serialize(new { FileVersion = 3, EngineAssociation = "5.6", Plugins = new[] {
            new { Name = "PythonScriptPlugin", Enabled = true }, new { Name = "EditorScriptingUtilities", Enabled = true } } }));
        var cookSource = Path.Combine(cookRoot, "source.fbx"); File.Copy(sourceCopy, cookSource);
        File.WriteAllText(Path.Combine(cookRoot, "import.json"), JsonSerializer.Serialize(new { source = cookSource, scale = recipe.ImportScale, package = recipe.MeshPackage }));
        var script = Path.Combine(cookRoot, "import_mesh.py");
        using (var resource = typeof(SkinnedMeshCookService).Assembly.GetManifestResourceStream("Batcomputer.Tools.SkinnedMesh.import_mesh.py")
            ?? throw new InvalidDataException("The bundled skeletal import script is missing."))
        using (var output = File.Create(script)) resource.CopyTo(output);
        var ddc = Path.Combine(Path.GetTempPath(), "BatcomputerSkinnedDDC"); Directory.CreateDirectory(ddc);
        File.WriteAllText(Path.Combine(cookRoot, "Config/DefaultEngine.ini"), """
[/Script/Engine.RendererSettings]
r.RayTracing=False
r.AllowStaticLighting=False
[ConsoleVariables]
Interchange.FeatureFlags.Import.FBX=False
[SkinnedLocalDDC]
Root=(Type=KeyLength, Length=120, Inner=AsyncPut)
AsyncPut=(Type=AsyncPut, Inner=Local)
""" + $"\nLocal=(Type=FileSystem, ReadOnly=false, Clean=false, Flush=false, DeleteUnused=false, Path=\"{ddc.Replace('\\', '/')}\")\n");
        File.WriteAllText(Path.Combine(cookRoot, "Config/DefaultGame.ini"), """
[/Script/UnrealEd.ProjectPackagingSettings]
bUseIoStore=False
bUsePakFile=False
bUseZenStore=False
bShareMaterialShaderCode=False
bCookAll=False
bSkipEditorContent=True
""");
        string[] common = [project, "-unattended", "-nop4", "-nosplash", "-NullRHI", "-NoSound", "-NoCrashDialog", "-DDC=SkinnedLocalDDC", "-NoZenAutoLaunch"];
        async Task RunUnreal(string phase, string[] args)
        {
            var logFile = Path.Combine(cookRoot, phase + ".log");
            try { await Run(editor, [.. common, .. args, "-abslog=" + logFile], root, cancellation); }
            finally { if (File.Exists(logFile)) File.Copy(logFile, Path.Combine(root, phase + ".log"), true); }
        }
        log("Importing FBX with Unreal (first cook can take several minutes)…");
        await RunUnreal("import", ["-run=pythonscript", "-script=" + script]);
        var reportPath = Path.Combine(cookRoot, "import-result.json");
        if (!File.Exists(reportPath)) throw new InvalidDataException("Unreal did not produce an import report. See " + Path.Combine(root, "import.log"));
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var slots = report.RootElement.GetProperty("slots").EnumerateArray().Select(s => s.GetString()!).ToArray();
        if (slots.Length == 0 || slots.Length > 64 || slots.Distinct(StringComparer.Ordinal).Count() != slots.Length)
            throw new InvalidDataException("Use between 1 and 64 uniquely named material slots.");
        log("Cooking skeletal render buffers…");
        await RunUnreal("cook", ["-run=Cook", "-TargetPlatform=Windows", "-CookDir=" + Path.Combine(cookRoot, "Content/Mods"),
            "-NoDefaultMaps", "-NoAlwaysCookMaps", "-SkipEditorContent", "-SkipZenStore"]);
        var cooked = Path.Combine(cookRoot, "Saved/Cooked/Windows/SkinnedCook/Content");
        var stage = Path.Combine(cookRoot, "AuditStage/LEGOBatmanLotDK/Content");
        var packageBase = recipe.MeshPackage[..recipe.MeshPackage.LastIndexOf('/')];
        var sourceFolder = SafePath(cooked, packageBase[6..]);
        var destFolder = SafePath(stage, packageBase[6..]); Directory.CreateDirectory(destFolder);
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.TopDirectoryOnly))
            if (new[] { ".uasset", ".uexp", ".ubulk" }.Contains(Path.GetExtension(file))) File.Copy(file, Path.Combine(destFolder, Path.GetFileName(file)));
        var container = Path.Combine(cookRoot, "AuditContainers"); Directory.CreateDirectory(container);
        await Run(AppSettings.Current.EffectiveRetocExePath(), ["to-zen", "--version", "UE5_6", Path.Combine(cookRoot, "AuditStage"), Path.Combine(container, "SkinnedAudit.utoc")], root, cancellation);
        await Run(AppSettings.Current.EffectiveRetocExePath(), ["verify", Path.Combine(container, "SkinnedAudit.utoc")], root, cancellation);
        log("Validating the cooked skeleton, rest pose and render buffers against the native donor…");
        using (var provider = OpenProvider(container))
        {
            var donor = provider.LoadPackageObject<USkeletalMesh>(recipe.DonorMeshPackage);
            var mesh = provider.LoadPackageObject<USkeletalMesh>(recipe.MeshPackage);
            ValidateRigWithReport(donor, mesh, Path.Combine(root, "rig-comparison.json"), log);
            recipe.SkeletonPackage = UnrealPathUtil.NormalizePackagePath(donor.Skeleton?.ResolvedObject?.GetPathName() ?? "");
            if (!ExtractedPackagePathService.IsContentPackagePath(recipe.SkeletonPackage)) throw new InvalidDataException("The donor has no usable native skeleton.");
        }
        var files = new Dictionary<string, string>();
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            var file = SafePath(cooked, recipe.MeshPackage[6..] + ext);
            if (!File.Exists(file)) continue;
            var name = "mesh" + ext; File.Copy(file, Path.Combine(root, name)); files.Add(name, Hash(Path.Combine(root, name)));
        }
        if (!files.ContainsKey("mesh.uasset") || !files.ContainsKey("mesh.uexp")) throw new InvalidDataException("Cooked mesh pair is incomplete.");
        var manifest = new CookManifest(recipe.SourceSha256, recipe.DonorMeshPackage, recipe.SkeletonPackage, recipe.MeshPackage,
            UnrealPathUtil.NormalizePackagePath(report.RootElement.GetProperty("skeleton").GetString()), slots, files);
        AtomicFileUtil.WriteAllText(Path.Combine(root, "validated.json"), JsonSerializer.Serialize(manifest));
        var previous = recipe.Materials.ToDictionary(m => m.SourceMaterialName, StringComparer.Ordinal);
        var vehicle = VehicleDonorService.All.Any(d => d.Mesh == recipe.DonorMeshPackage || d.SummonMesh == recipe.DonorMeshPackage);
        recipe.Materials = slots.Select((name, index) => new CustomStaticMeshMaterialSlot { Slot = index, SourceMaterialName = name,
            StableSlotName = name, MaterialPath = VehiclePaintService.ImportSlotMaterial(name, previous.GetValueOrDefault(name)?.MaterialPath, vehicle) }).ToList();
        log("Validated. Assign a game material to each slot, preview, then save to the suit.");
        workspace.Complete = true;
        return recipe;
    }

    internal static void ValidateRig(USkeletalMesh donor, USkeletalMesh mesh)
        => ValidateRigWithReport(donor, mesh, null, null);

    private static void ValidateRigWithReport(USkeletalMesh donor, USkeletalMesh mesh, string? reportPath, Action<string>? log)
    {
        var comparison = SkinnedRigComparisonService.Compare(SkinnedGlbExportService.Bones(donor.ReferenceSkeleton), SkinnedGlbExportService.Bones(mesh.ReferenceSkeleton));
        if (reportPath is not null)
        {
            SkinnedRigComparisonService.Write(reportPath, comparison);
            log?.Invoke($"Rig comparison: {(comparison.Passed ? "PASS" : "FAILED")} — {comparison.ActualBoneCount}/{comparison.ExpectedBoneCount} bones. Per-bone results: {reportPath}");
            foreach (var error in comparison.Errors) log?.Invoke(error);
        }
        if (!comparison.Passed) throw new InvalidDataException(comparison.Errors[0] + (reportPath is null ? "" : "\nPer-bone report: " + reportPath));
        if (!mesh.TryConvert(out var converted) || converted.LODs.Count == 0 || converted.LODs[0].NumVerts == 0)
            throw new InvalidDataException("Cooked skeletal render data is empty or unreadable.");
    }

    internal static void RequireNativeDonor(string package)
    {
        if (!ExtractedPackagePathService.IsContentPackagePath(package) || package.Contains("/Mods/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an existing native game skeletal mesh as the rig donor.");
    }
    internal static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Expected a project-relative mesh path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!FileSystemPathUtil.IsWithinDirectory(path, Path.GetFullPath(root))) throw new InvalidDataException("Mesh path escapes its project folder.");
        return path;
    }
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static async Task Run(string exe, IEnumerable<string> arguments, string directory, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch " + Path.GetFileName(exe));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation); var stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var output = await stdout; var errors = await stderr;
        if (process.ExitCode != 0)
        {
            var logPath = start.ArgumentList.FirstOrDefault(a => a.StartsWith("-abslog=", StringComparison.OrdinalIgnoreCase))?[8..];
            var details = logPath is not null && File.Exists(logPath) ? File.ReadAllText(logPath) : errors + "\n" + output;
            throw new InvalidDataException($"{Path.GetFileName(exe)} failed ({process.ExitCode}).\n" + FailureSummary(details) + $"\nFull logs: {directory}");
        }
    }

    internal static string FailureSummary(string output)
    {
        var lines = output.Split('\n');
        var relevant = lines.Select((line, index) => (line, index)).Where(x =>
            x.line.Contains(": Error:", StringComparison.OrdinalIgnoreCase) ||
            x.line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase) ||
            x.line.Contains("Traceback (", StringComparison.Ordinal)).Take(3)
            .SelectMany(x => lines.Skip(x.index).Take(4)).Distinct().ToArray();
        var text = string.Join("\n", relevant.Length > 0 ? relevant : lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(12));
        if (output.Contains("filename is too long", StringComparison.OrdinalIgnoreCase))
            text = "Unreal could not cook a file because its path is too long. The FBX does not need re-rigging for this error. Use a shorter temporary cook folder.\n" + text;
        return text[..Math.Min(2400, text.Length)];
    }
}
