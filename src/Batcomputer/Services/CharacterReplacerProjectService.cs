using System.Text.Json.Nodes;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Output policy for the ordinary suit editor. Replacers never enter suit registration.</summary>
internal static class CharacterReplacerProjectService
{
    internal static bool IsReplacer(NativeSuitProject? project) => !string.IsNullOrWhiteSpace(project?.ReplacementTarget);
    internal static bool VisualCategory(string category) => category is "Home" or "Base" or "Materials" or "Faces" or "Textures" or "Parts" or "Build mod" or "Review";

    internal static void Validate(NativeSuitProject p)
    {
        if (!IsReplacer(p)) throw new InvalidDataException("This is not a replacement project.");
        NpcAppearanceTestService.Validate(new(p.ReplacementTarget, Changes: []));
        if (p.PlayableTemplate?.PackagePath != p.ReplacementTarget || p.CutsceneTemplate?.PackagePath != p.ReplacementTarget)
            throw new InvalidDataException("Replacement base must remain the exact original target. Create another replacer to change targets.");
        if (p.CustomCharacter is not null || p.AbilityLoadout is not null || CharacterMovementService.Enabled(p.MovementAttributes) || p.UseCustomArchetype || p.EquipmentSlots.Count != 0 ||
            p.AnimationOverrides.Count != 0 || p.LocomotionOverrides.Count != 0 || p.AnimationSlotOverrides.Count != 0 ||
            p.GameplayAnimationGraphs.Count != 0 || !string.IsNullOrWhiteSpace(p.FaceAnimationBlueprintPackage) ||
            !string.IsNullOrWhiteSpace(p.VoiceProfileId) || !string.IsNullOrWhiteSpace(p.GliderType) || p.PairedCapeAdapter is not null ||
            !string.IsNullOrWhiteSpace(p.MachineryDonorPlayable) || p.DcmdTemplate is not null || p.BaseProfile is not null)
            throw new InvalidDataException("Replacers support appearance changes only. Abilities, voices, equipment, animation overrides and gameplay donors cannot be packaged.");
        if (!p.TargetPackages.Playable.StartsWith("/Game/Mods/", StringComparison.Ordinal) ||
            !p.TargetPackages.Cutscene.StartsWith("/Game/Mods/", StringComparison.Ordinal) || p.TargetPackages.Playable == p.TargetPackages.Cutscene)
            throw new InvalidDataException("Replacement working copies must use distinct mod-local paths.");
        foreach (var rule in p.Requirements)
            if (rule.Kind != "remove-component" || GameplayShellComponentPolicy.IsRequired(rule.TargetComponent))
                throw new InvalidDataException("Non-visual replacement requirement: " + rule.Kind + " " + rule.TargetComponent);
    }

    internal static TemplateRecord Template(string package, string content, string role)
    {
        var path = Path.Combine(content, package[6..] + ".uasset");
        if (!File.Exists(path)) throw new FileNotFoundException("Extract the selected character before creating a replacer.", path);
        return new() { PackagePath = package, Uasset = path, Uexp = Path.ChangeExtension(path, ".uexp"),
            Stem = UnrealPathUtil.AssetName(package), Role = role, Character = BaseEligibilityService.CharacterStem(package),
            ContentRelative = package[6..], HasPair = File.Exists(Path.ChangeExtension(path, ".uexp")), HasSplitPair = File.Exists(Path.ChangeExtension(path, ".uexp")) };
    }

    internal static NativeSuitProject Create(string target, string name, string content, string id)
    {
        NpcAppearanceTestService.Validate(new(target, Changes: []));
        var root = "/Game/Mods/" + UnrealPathUtil.SanitizeIdentifier(id, "Replacement");
        var p = new NativeSuitProject { SlotId = id, DisplayName = name, ReplacementTarget = target, PackageBaseName = id + "_P",
            PlayableTemplate = Template(target, content, "playable"), CutsceneTemplate = Template(target, content, "cutscene"),
            TargetPackages = new() { Playable = root + "/Characters/BP_Replacement_Playable", Cutscene = root + "/Characters/BP_Replacement_Cutscene" } };
        Validate(p); return p;
    }

    // Called only on a disposable prepared stage. Export the edited working copy back at the
    // exact original identity and bundle only its reachable custom visual dependencies.
    internal static string Export(NativeSuitProject project, string preparedContent, string nativeContent, string mappings, string output)
    {
        Validate(project);
        if (Directory.Exists(output)) throw new IOException("Replacement export requires a new output directory.");
        var maps = new Usmap(mappings);
        UAsset Read(string path)
        {
            var asset = new UAsset(path, EngineVersion.VER_UE5_6, maps, CustomSerializationFlags.SkipPreloadDependencyLoading);
            NativeBlueprintSchemaService.EnsureParents(asset);
            return asset;
        }
        // Warm the real native inheritance chain before parsing either comparison operand.
        // Otherwise the first load can retain opaque CDO data while the second decodes it.
        var originalPath = Path.Combine(nativeContent, project.ReplacementTarget[6..] + ".uasset");
        _ = Read(originalPath);
        var working = project.TargetPackages.Playable;
        var edited = Read(Path.Combine(preparedContent, working[6..] + ".uasset"));
        var oldStem = UnrealPathUtil.AssetName(working); var newStem = UnrealPathUtil.AssetName(project.ReplacementTarget);
        var rename = new Dictionary<string, string> { [working] = project.ReplacementTarget, [oldStem] = newStem,
            [oldStem + "_C"] = newStem + "_C", ["Default__" + oldStem + "_C"] = "Default__" + newStem + "_C" };
        var names = edited.GetNameMapIndexList().Select(n => n.ToString()).ToArray();
        for (var i = 0; i < names.Length; i++) if (rename.TryGetValue(names[i], out var value)) edited.SetNameReference(i, new FString(value));
        // IoStore derives package identity from the summary, not the output filename.
        // Leaving the working-copy identity here builds a new mod package, not an override.
        var original = Read(originalPath);
        var nativeIdentity = original.FolderName?.ToString();
        if (!string.Equals(nativeIdentity, project.ReplacementTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The extracted source's package identity does not match the replacement target.");
        edited.FolderName = new FString(nativeIdentity!);
        VerifyVisualOnly(original, edited);
        Directory.CreateDirectory(output);
        var destination = Path.Combine(output, project.ReplacementTarget[6..] + ".uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); edited.Write(destination);
        VerifyVisualOnly(original, Read(destination));
        var dependencies = edited.Imports.Where(i => i.ClassName.ToString() == "Package")
            .Select(i => i.ObjectName.ToString()).Where(p => p.StartsWith("/Game/Mods/", StringComparison.Ordinal)).ToArray();
        CharacterReplacementService.CopyVisualDependencies(dependencies, nativeContent, [preparedContent], output, maps);
        return output;
    }

    private static readonly HashSet<string> MeshFields = new(StringComparer.Ordinal) { "SkeletalMesh", "StaticMesh", "OverrideMaterials", "RelativeLocation", "RelativeRotation", "RelativeScale3D", "AttachSocketName", "bVisible", "bHiddenInGame" };
    private static bool MeshClass(string kind) => kind is "SkeletalMeshComponent" or "SkeletalMeshComponentBudgeted" or "StaticMeshComponent";

    internal static void VerifyVisualOnly(UAsset original, UAsset edited)
    {
        VerifyPackageIdentity(original.FolderName?.ToString(), edited.FolderName?.ToString());
        string Ref(UAsset a, FPackageIndex index)
        {
            if (index.IsNull()) return "None";
            if (index.IsExport()) return "export:" + index.ToExport(a).ObjectName;
            var i = index.ToImport(a); return Ref(a, i.OuterIndex) + "/" + i.ClassName + ":" + i.ObjectName;
        }
        string Kind(UAsset a, Export e) => e.ClassIndex.IsImport() ? e.ClassIndex.ToImport(a).ObjectName.ToString() : "local";
        var before = JsonNode.Parse(original.SerializeJson(false))!["Exports"]!.AsArray();
        var after = JsonNode.Parse(edited.SerializeJson(false))!["Exports"]!.AsArray();
        if (edited.Imports.Count < original.Imports.Count) throw new InvalidDataException("Replacement removed native imports.");
        for (var n = 0; n < original.Imports.Count; n++)
            if (Ref(original, FPackageIndex.FromImport(n)) != Ref(edited, FPackageIndex.FromImport(n)))
                throw new InvalidDataException("Replacement redirected a native import. Visual edits must add their own references, not rewrite behavior dependencies.");
        // Fail closed on structural rewrites until their graph invariants are certified. Material,
        // body/mesh and placement edits must never change AI/collision/quest serialized fields.
        if (original.Exports.Count != edited.Exports.Count)
            throw new InvalidDataException("This part edit changes the Blueprint's component structure. It cannot yet be certified as a visual-only replacement. Existing mesh/material edits are supported.");
        for (var i = 0; i < original.Exports.Count; i++)
        {
            var a = original.Exports[i]; var b = edited.Exports[i];
            if (a.ObjectName.ToString() != b.ObjectName.ToString() || Ref(original, a.ClassIndex) != Ref(edited, b.ClassIndex) || Ref(original, a.SuperIndex) != Ref(edited, b.SuperIndex))
                throw new InvalidDataException("Replacement changed an original export's identity or behavior class: " + a.ObjectName);
            var allowed = MeshClass(Kind(original, a)) ? MeshFields : new HashSet<string>();
            JsonNode? Snapshot(JsonNode? export, UAsset asset)
            {
                var data = export?["Data"]?.DeepClone();
                if (data is JsonArray list)
                    for (var n = list.Count - 1; n >= 0; n--) if (allowed.Contains(list[n]?["Name"]?.GetValue<string>() ?? "")) list.RemoveAt(n);
                void Normalize(JsonNode? node)
                {
                    if (node is JsonObject obj)
                    {
                        if (obj["$type"]?.GetValue<string>().Contains("ObjectPropertyData", StringComparison.Ordinal) == true && obj["Value"] is JsonValue v && v.TryGetValue<int>(out var index))
                            obj["Value"] = Ref(asset, new FPackageIndex(index));
                        foreach (var child in obj.Select(p => p.Value).ToArray()) Normalize(child);
                    }
                    else if (node is JsonArray array) foreach (var child in array) Normalize(child);
                }
                Normalize(data); return data;
            }
            if (!JsonNode.DeepEquals(Snapshot(before[i], original), Snapshot(after[i], edited)))
                throw new InvalidDataException("Replacement changed a non-visual property on " + a.ObjectName + ". Build stopped; original behavior must remain unchanged.");
            JsonNode? Metadata(JsonNode? export)
            {
                var copy = export?.DeepClone()?.AsObject();
                // File offsets/sizes change when visual properties grow. Behavior bytecode,
                // flags, class information and export relationships must remain identical.
                foreach (var field in new[] { "Data", "SerialSize", "SerialOffset" }) copy?.Remove(field);
                return copy;
            }
            if (!JsonNode.DeepEquals(Metadata(before[i]), Metadata(after[i])))
                throw new InvalidDataException("Replacement changed export metadata or bytecode on " + a.ObjectName);
            if (a is RawExport ar && b is RawExport br && !ar.Data.SequenceEqual(br.Data))
                throw new InvalidDataException("Replacement changed opaque behavior data on " + a.ObjectName);
        }
    }

    internal static void VerifyPackageIdentity(string? original, string? edited)
    {
        if (string.IsNullOrWhiteSpace(original) || !original.Equals(edited, StringComparison.Ordinal))
            throw new InvalidDataException("Replacement package identity must match the original target, not its mod-local working copy.");
    }
}
