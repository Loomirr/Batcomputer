using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;

namespace Batcomputer;

/// <summary>
/// Turns a native-body animation draft into one cooked AnimSequence and registers it in the
/// workspace library. Unreal is launched in a disposable project; the game and suit are untouched.
/// </summary>
internal static class AnimationDraftCookService
{
    private const string NativeMesh = "/Game/Characters/LEGOfig/SK_LEGOfig_Minifig";
    private const string NativeSkeleton = "/Game/Characters/LEGOfig/SKEL_LEGOfig";
    private const string AnimationFolder = "/Game/Mods/BatcomputerAnimations/Animations";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal sealed record Result(AnimLibraryEntry Entry, string CookedUasset, string ReportDirectory, bool Reused);

    internal static async Task<Result> CookAndImportAsync(
        string projectRoot, string draftPath, string referenceFbx, Action<string> log,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) throw new InvalidDataException("Choose a Batcomputer workspace first.");
        if (!File.Exists(draftPath)) throw new FileNotFoundException("Animation draft not found.", draftPath);
        if (!File.Exists(referenceFbx)) throw new FileNotFoundException("Native-rig FBX not found.", referenceFbx);
        if (!Path.GetExtension(referenceFbx).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a rigged FBX reference, not a GLB or Blend file.");
        var engine = AppSettings.Current.EffectiveUnrealEngineRoot();
        var editor = Path.Combine(engine, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        var versionPath = Path.Combine(engine, "Engine", "Build", "Build.version");
        if (!File.Exists(editor) || !File.Exists(versionPath))
            throw new InvalidDataException("Configure Unreal Engine 5.6 in Settings before cooking an animation draft.");
        using (var version = JsonDocument.Parse(File.ReadAllText(versionPath)))
            if (version.RootElement.GetProperty("MajorVersion").GetInt32() != 5 ||
                version.RootElement.GetProperty("MinorVersion").GetInt32() != 6)
                throw new InvalidDataException("Animation drafts require Unreal Engine 5.6, matching the game.");

        log("Reading the game's native LEGOfig rig…");
        object[] donorBones;
        string[] boneNames;
        string rigSignature;
        using (var provider = SkinnedMeshCookService.OpenProvider())
        {
            var native = provider.LoadPackageObject<USkeletalMesh>(NativeMesh);
            var bones = SkinnedGlbExportService.Bones(native.ReferenceSkeleton);
            boneNames = bones.Select(bone => bone.Name).ToArray();
            rigSignature = string.Join('|', bones.Select(bone =>
                bone.Name + ":" + (bone.Parent >= 0 ? bones[bone.Parent].Name : "")));
            donorBones = bones.Select(bone => (object)new
            {
                name = bone.Name, parent = bone.Parent,
                translation = new[] { bone.Translation.X, bone.Translation.Y, bone.Translation.Z },
                rotation = new[] { bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W },
                scale = new[] { bone.Scale.X, bone.Scale.Y, bone.Scale.Z }
            }).ToArray();
        }
        var draftBytes = File.ReadAllBytes(draftPath);
        if (draftBytes.Length is 0 or > 2_000_000) throw new InvalidDataException("Animation draft must be smaller than 2 MB.");
        using var draft = JsonDocument.Parse(draftBytes);
        var name = ValidateDraft(draft.RootElement, rigSignature, boneNames);
        var hash = Convert.ToHexString(SHA256.HashData(draftBytes))[..12];
        var safeName = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()).Trim('_');
        if (safeName.Length == 0) safeName = "Motion";
        if (safeName.Length > 28) safeName = safeName[..28].TrimEnd('_');
        var assetName = $"A_{safeName}_{hash}";
        var packagePath = AnimationFolder + "/" + assetName;
        var libraryService = new AnimLibraryService(projectRoot, AppSettings.Current.EffectiveUsmapPath());
        var library = libraryService.Load();
        var previous = library.Entries.FirstOrDefault(entry =>
            entry.PackagePath.Equals(packagePath, StringComparison.OrdinalIgnoreCase) && entry.IsAvailable);
        if (previous is not null)
        {
            log("This exact draft is already cooked and ready in the animation library.");
            return new Result(previous, "", libraryService.LibraryRoot, true);
        }

        log("Checking the native-rig FBX and its skin weights…");
        var audit = SkinnedFbxValidator.Inspect(referenceFbx);
        if (audit.Bones < boneNames.Length - 2)
            throw new InvalidDataException("The FBX does not contain the complete native LEGOfig rig. Export the full weighted body reference.");
        var reportDirectory = Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "AnimationDraftCooks", assetName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(reportDirectory);
        using var workspace = new SkinnedCookWorkspace(packagePath);
        var cookRoot = workspace.Root;
        var configDirectory = Path.Combine(cookRoot, "Config");
        Directory.CreateDirectory(configDirectory);
        var project = Path.Combine(cookRoot, "AnimCook.uproject");
        File.WriteAllText(project, JsonSerializer.Serialize(new
        {
            FileVersion = 3, EngineAssociation = "5.6",
            Plugins = new[] { new { Name = "PythonScriptPlugin", Enabled = true },
                new { Name = "EditorScriptingUtilities", Enabled = true },
                new { Name = "MeshModelingToolset", Enabled = true } }
        }));
        var sourceCopy = Path.Combine(cookRoot, "source.fbx");
        File.Copy(referenceFbx, sourceCopy);
        var draftCopy = Path.Combine(cookRoot, "draft.json");
        File.WriteAllBytes(draftCopy, draftBytes);
        var temporaryMesh = "/Game/Mods/BatcomputerAnimations/Temp/SK_" + hash;
        File.WriteAllText(Path.Combine(cookRoot, "import.json"), JsonSerializer.Serialize(new
        {
            source = sourceCopy, scale = 1, package = temporaryMesh, donor_bones = donorBones,
            drafts = new[] { draftCopy }, asset_name = assetName, animation_folder = AnimationFolder
        }, JsonOptions));
        CopyResource("Batcomputer.Tools.SkinnedMesh.import_mesh.py", Path.Combine(cookRoot, "import_mesh.py"));
        var authorScript = Path.Combine(cookRoot, "author_animation_drafts.py");
        CopyResource("Batcomputer.Tools.Animations.author_animation_drafts.py", authorScript);
        var ddc = Path.Combine(Path.GetTempPath(), "BatcomputerSkinnedDDC");
        Directory.CreateDirectory(ddc);
        File.WriteAllText(Path.Combine(configDirectory, "DefaultEngine.ini"),
            "[/Script/Engine.RendererSettings]\nr.RayTracing=False\nr.AllowStaticLighting=False\n" +
            "[ConsoleVariables]\nInterchange.FeatureFlags.Import.FBX=False\n" +
            "[SkinnedLocalDDC]\nRoot=(Type=KeyLength, Length=120, Inner=AsyncPut)\n" +
            "AsyncPut=(Type=AsyncPut, Inner=Local)\n" +
            $"Local=(Type=FileSystem, ReadOnly=false, Clean=false, Flush=false, DeleteUnused=false, Path=\"{ddc.Replace('\\', '/')}\")\n");
        File.WriteAllText(Path.Combine(configDirectory, "DefaultGame.ini"),
            "[/Script/UnrealEd.ProjectPackagingSettings]\nbUseIoStore=False\nbUsePakFile=False\n" +
            "bUseZenStore=False\nbCookAll=False\nbSkipEditorContent=True\n");
        File.WriteAllText(Path.Combine(reportDirectory, "cook-workspace.txt"), cookRoot);

        async Task RunUnreal(string phase, params string[] args)
        {
            var logPath = Path.Combine(cookRoot, phase + ".log");
            log(phase == "author" ? "Authoring the AnimSequence in Unreal 5.6…" : "Cooking the animation for Windows…");
            try
            {
                await RunProcess(editor, [project, "-unattended", "-nop4", "-nosplash", "-NullRHI", "-NoSound",
                    "-NoCrashDialog", "-DDC=SkinnedLocalDDC", "-NoZenAutoLaunch", .. args, "-abslog=" + logPath],
                    cookRoot, cancellation);
            }
            finally
            {
                if (File.Exists(logPath)) File.Copy(logPath, Path.Combine(reportDirectory, phase + ".log"), true);
            }
        }
        await RunUnreal("author", "-run=pythonscript", "-script=" + authorScript);
        var authorReport = Path.Combine(cookRoot, "animations-result.json");
        if (!File.Exists(authorReport)) throw new InvalidDataException("Unreal did not produce an animation report. See " + reportDirectory);
        File.Copy(authorReport, Path.Combine(reportDirectory, "animations-result.json"));
        using (var report = JsonDocument.Parse(File.ReadAllText(authorReport)))
        {
            var item = report.RootElement.EnumerateArray().Single();
            if (item.GetProperty("name").GetString() != assetName ||
                item.GetProperty("skeleton").GetString() != temporaryMesh + "_Skeleton." + Path.GetFileName(temporaryMesh) + "_Skeleton")
                throw new InvalidDataException("Unreal authored a different animation or skeleton than requested.");
        }
        await RunUnreal("cook", "-run=Cook", "-TargetPlatform=Windows", "-CookDir=" + Path.Combine(cookRoot, "Content", "Mods"),
            "-NoDefaultMaps", "-NoAlwaysCookMaps", "-SkipEditorContent", "-SkipZenStore");
        var cookedFolder = Path.Combine(cookRoot, "Saved", "Cooked", "Windows", "AnimCook", "Content", "Mods", "BatcomputerAnimations", "Animations");
        var cookedUasset = Path.Combine(cookedFolder, assetName + ".uasset");
        if (!File.Exists(cookedUasset) || !File.Exists(Path.Combine(cookedFolder, assetName + ".uexp")))
            throw new InvalidDataException("The cooked animation pair is incomplete. See " + reportDirectory);
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            var source = Path.Combine(cookedFolder, assetName + extension);
            if (File.Exists(source)) File.Copy(source, Path.Combine(reportDirectory, assetName + extension));
        }
        var importedUasset = Path.Combine(reportDirectory, assetName + ".uasset");
        log("Connecting the cooked animation to the game's native LEGOfig skeleton…");
        if (Program.RepathNameMap(importedUasset, temporaryMesh + "_Skeleton", NativeSkeleton) != 0 ||
            Program.RepathNameMap(importedUasset, Path.GetFileName(temporaryMesh) + "_Skeleton", "SKEL_LEGOfig") != 0)
            throw new InvalidDataException("The cooked animation could not be bound to the native skeleton.");
        var entry = libraryService.ImportCookedFile(library, assetName, importedUasset, packagePath, "authored-draft", "Character");
        if (!entry.IsAvailable || !entry.Skeleton.Equals(NativeSkeleton, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cooked animation was quarantined: " + string.Join("; ", entry.HealthIssues));
        log("Ready in the animation library. Choose an individual slot in Edit character animations.");
        workspace.Complete = true;
        return new Result(entry, importedUasset, reportDirectory, false);
    }

    internal static string ValidateDraft(JsonElement draft, string rigSignature, IReadOnlyCollection<string> boneNames)
    {
        if (draft.ValueKind != JsonValueKind.Object ||
            !draft.TryGetProperty("schema", out var schema) || schema.GetString() != "batcomputer.animation-draft.v1" ||
            !draft.TryGetProperty("rigSignature", out var signature) || signature.GetString() != rigSignature ||
            !draft.TryGetProperty("fps", out var fps) || fps.GetInt32() != 30 ||
            !draft.TryGetProperty("durationFrames", out var duration) || duration.GetInt32() is < 3 or > 900 ||
            !draft.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            !draft.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() is < 1 or > 100)
            throw new InvalidDataException("This draft does not match the native LEGOfig rig or supported 30 fps timeline.");
        var name = nameValue.GetString()?.Trim() ?? "";
        if (name.Length is < 1 or > 64) throw new InvalidDataException("Give the animation a name between 1 and 64 characters.");
        if (draft.TryGetProperty("loop", out var loop) && loop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The draft has an invalid loop setting.");
        var knownBones = boneNames.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks.EnumerateArray())
        {
            var bone = track.GetProperty("bone").GetString() ?? "";
            if (!knownBones.Contains(bone) || !seen.Add(bone)) throw new InvalidDataException("The draft contains an unknown or repeated bone: " + bone);
            var keys = track.GetProperty("keys");
            if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is < 1 or > 901)
                throw new InvalidDataException("The draft contains an empty or oversized bone track: " + bone);
            var previous = -1;
            foreach (var key in keys.EnumerateArray())
            {
                var frame = key.GetProperty("frame").GetInt32();
                if (frame <= previous || frame > duration.GetInt32()) throw new InvalidDataException("The draft has unsorted or out-of-range keys.");
                previous = frame;
                ValidateVector(key.GetProperty("p"), 3, -5, 5);
                var rotation = key.GetProperty("q");
                ValidateVector(rotation, 4, -1.01, 1.01);
                var norm = rotation.EnumerateArray().Sum(value => Math.Pow(value.GetDouble(), 2));
                if (Math.Abs(norm - 1) > .02) throw new InvalidDataException("The draft has a non-unit bone rotation.");
                if (key.TryGetProperty("s", out var scale)) ValidateVector(scale, 3, .05, 5);
                if (key.TryGetProperty("interpolation", out var interpolation) &&
                    interpolation.GetString() is not ("linear" or "smooth" or "hold"))
                    throw new InvalidDataException("The draft has an unsupported key transition.");
            }
        }
        return name;
    }

    private static void ValidateVector(JsonElement vector, int length, double minimum, double maximum)
    {
        if (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() != length ||
            vector.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < minimum || number > maximum))
            throw new InvalidDataException("The draft contains an invalid bone transform.");
    }

    private static void CopyResource(string resourceName, string destination)
    {
        using var source = typeof(AnimationDraftCookService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("The bundled animation cook helper is missing: " + resourceName);
        using var output = File.Create(destination);
        source.CopyTo(output);
    }

    private static async Task RunProcess(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not launch Unreal Editor commandlet.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0)
        {
            var logPath = arguments.FirstOrDefault(arg => arg.StartsWith("-abslog=", StringComparison.OrdinalIgnoreCase))?[8..];
            var detail = logPath is not null && File.Exists(logPath) ? File.ReadAllText(logPath) : errors + "\n" + output;
            throw new InvalidDataException($"Unreal exited with code {process.ExitCode}.\n" + SkinnedMeshCookService.FailureSummary(detail));
        }
    }
}
