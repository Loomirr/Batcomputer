using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>The group/progression/config registration proven by the Moon Knight gameplay test.
/// All writes are mod-owned; native templates are copied before UAssetAPI opens them.</summary>
public static class CustomCharacterRegistrationService
{
    public const string GroupDonor = "/Game/Characters/MetaData/Groups/DA_CharacterGroup_Batman";
    public const string ProgressDonor = "/Game/GameProgress/PROG_Characters";
    public static string GroupPackage(string mod, string id) => $"/Game/Characters/MetaData/Groups/Mods/{mod}/DA_CharacterGroup_{mod}_{id}";
    public static string ProgressPackage(string mod, string id) => $"/Game/GameProgress/Mods/{mod}/PROG_{mod}_{id}";
    public static string NameKey(string id) => $"Character.{id}.Name";

    internal static IReadOnlyList<string> MissingDonorFiles(string nativeContent) =>
        new[] { GroupDonor, ProgressDonor }
            .SelectMany(package => new[] { ".uasset", ".uexp" }.Select(ext => package + ext))
            .Where(file =>
            {
                var path = Path.Combine(nativeContent, file[6..].Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || new FileInfo(path).Length == 0;
            }).ToArray();

    internal static string MissingDonorMessage(string nativeContent, IReadOnlyList<string> missing) =>
        "The active extraction is incomplete for custom characters. Missing: " + string.Join(", ", missing) +
        ". Update Batcomputer and run Full character extraction in Settings (DeveloperResearch is also supported). " +
        "Your saved character is intact. Active Content root: " + nativeContent;
    public static IEnumerable<PawnTagConfigService.TagRow> AdditionalTags(NativeSuitProject project, string mod)
    {
        if (project.CustomCharacter is not { } identity) yield break;
        if (identity.IsDefinition) yield return new(CustomCharacterProjectService.Scope(identity), mod + ": custom character");
        yield return new(project.ProgressTag, mod + ": initially unlocked variant");
    }

    public static IReadOnlyList<RegistryPluginService.RegistryRow> Generate(string content, string nativeContent,
        string mod, IReadOnlyList<NativeSuitProject> projects, Usmap maps)
    {
        var rows = new List<RegistryPluginService.RegistryRow>();
        if (!UnrealPathUtil.IsValidIdentifier(mod)) throw new InvalidDataException("Registration requires a package-safe Mod ID.");
        if (projects.Any(project => project.CustomCharacter is not null))
        {
            var missing = MissingDonorFiles(nativeContent);
            if (missing.Count > 0) throw new FileNotFoundException(MissingDonorMessage(nativeContent, missing));
        }
        foreach (var family in projects.Where(project => project.CustomCharacter is not null)
                     .GroupBy(project => project.CustomCharacter!.CharacterId, StringComparer.OrdinalIgnoreCase))
        {
            var definition = family.SingleOrDefault(CustomCharacterProjectService.IsCharacter)
                ?? throw new InvalidDataException($"Character '{family.Key}' has no default definition in this release.");
            var identity = definition.CustomCharacter!;
            foreach (var project in family)
                Require(CustomCharacterProjectService.IdentityError(project) is null &&
                    project.CustomCharacter!.DefinitionSlotId == definition.SlotId, "Character identity/definition mismatch: " + project.DisplayName);
            var groupPackage = GroupPackage(mod, identity.CharacterId);
            var groupFile = CopyDonor(nativeContent, content, GroupDonor, groupPackage);
            var group = Read(groupFile, maps);
            Rename(group, GroupDonor, groupPackage);
            Require(NativeAssetTextPatch.SetGameplayTag(group, "BaseCharacterTag", CustomCharacterProjectService.Scope(identity)), "Group owner field is missing.");
            Require(NativeAssetTextPatch.SetGameplayTag(group, "DefaultCharacterVariant", definition.PawnTag), "Group default variant is missing.");
            Require(NativeAssetTextPatch.SetStringTableText(group, "DisplayName", StringTableGenService.ObjectPathFor(mod), NameKey(identity.CharacterId)), "Group display name is missing.");
            if (!string.IsNullOrWhiteSpace(identity.SymbolPackage))
                Require(NativeAssetTextPatch.SetSoftObject(group, "Symbol", identity.SymbolPackage), "The group emblem must be a supported soft asset reference.");
            group.Write(groupFile);
            var check = Read(groupFile, maps);
            Require(NativeAssetTextPatch.GetGameplayTag(check, "BaseCharacterTag") == CustomCharacterProjectService.Scope(identity) &&
                NativeAssetTextPatch.GetGameplayTag(check, "DefaultCharacterVariant") == definition.PawnTag, "Character group failed its written identity check.");

            var progressPackage = ProgressPackage(mod, identity.CharacterId);
            var progressFile = CopyDonor(nativeContent, content, ProgressDonor, progressPackage);
            var progress = Read(progressFile, maps);
            var raw = progress.Exports.OfType<RawExport>().Single();
            raw.Data = CreateUnlockedProgress(progress, raw.Data, family.Select(project => project.ProgressTag).ToArray());
            Rename(progress, ProgressDonor, progressPackage);
            progress.Write(progressFile);
            Require(Read(progressFile, maps).Exports.OfType<RawExport>().Single().Data.SequenceEqual(raw.Data), "Progress failed its written data check.");
            rows.Add(new(groupPackage, RegistryPluginService.CharacterGroupPrimaryAssetType, RegistryPluginService.CharacterGroupClass));
            rows.Add(new(progressPackage, RegistryPluginService.ProgressDefinitionPrimaryAssetType, RegistryPluginService.ProgressDefinitionClass));
        }
        return rows;
    }

    // FInstancedStruct is opaque in UAssetAPI. Reject every unexpected schema, type, length or
    // default instead of guessing offsets. Only the known native character progression record
    // is cloned; every generated variant starts Unlocked and retains native save behavior.
    internal static byte[] CreateUnlockedProgress(UAsset asset, byte[] source, IReadOnlyList<string> tags)
    {
        Require(tags.Count > 0 && tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Count &&
            tags.All(tag => System.Text.RegularExpressions.Regex.IsMatch(tag, "^GameProgress\\.Definitions\\.Characters\\.[A-Za-z][A-Za-z0-9]{0,63}\\.[A-Za-z][A-Za-z0-9]{0,63}$")), "Invalid or duplicate character progress tags.");
        using var reader = new BinaryReader(new MemoryStream(source));
        Require(reader.ReadUInt16() == 0x0301, "Unsupported PROG_Characters schema. Refresh extraction and mappings.");
        int count = reader.ReadInt32();
        Require(count > 0 && count < 1000, "Invalid progress entry count.");
        byte[]? donor = null; int typeIndex = 0;
        for (int i = 0; i < count; i++)
        {
            int type = reader.ReadInt32(), size = reader.ReadInt32();
            Require(size > 0 && size < 4096, "Invalid progress entry size.");
            var data = reader.ReadBytes(size);
            Require(data.Length == size, "Truncated progress entry.");
            Require(type < 0 && FPackageIndex.FromRawIndex(type).ToImport(asset).ObjectName.ToString() == "DinnerCharacterProgressDefinition", "Unsupported progress entry type.");
            if (size == 24 && BitConverter.ToUInt16(data) == 0x0480 && BitConverter.ToUInt16(data, 2) == 0x0701 &&
                asset.GetNameReference(BitConverter.ToInt32(data, 12)).ToString() == "GameProgress.Definitions.Characters.Batman.TheBatman2025")
            { Require(donor is null, "Ambiguous native progression donor."); donor = data; typeIndex = type; }
        }
        Require(reader.BaseStream.Length - reader.BaseStream.Position == 4 && reader.ReadInt32() == 0, "Unsupported progress trailing fields.");
        Require(donor is not null, "Known native progression donor not found. Refresh extraction and mappings.");
        Require(donor![4] == 1 && BitConverter.ToInt32(donor, 5) == 0 && donor[9] == 1 && BitConverter.ToUInt16(donor, 10) == 0x0300 &&
            BitConverter.ToInt32(donor, 16) == 0 && BitConverter.ToInt32(donor, 20) == 0, "Unsupported native progress property layout.");
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write((ushort)0x0301); writer.Write(tags.Count);
        foreach (var tag in tags)
        {
            var entry = (byte[])donor.Clone(); entry[0] = 0; entry[4] = 1;
            BitConverter.GetBytes(asset.AddNameReference(new FString(tag))).CopyTo(entry, 12);
            writer.Write(typeIndex); writer.Write(entry.Length); writer.Write(entry);
        }
        writer.Write(0);
        return bytes.ToArray();
    }

    public static string RosterConfig(IEnumerable<NativeSuitProject> projects)
    {
        var definitions = projects.Where(CustomCharacterProjectService.IsCharacter).ToArray();
        if (definitions.Any(project => CustomCharacterProjectService.IdentityError(project) is not null))
            throw new InvalidDataException("Cannot register an invalid custom character identity.");
        return "[/Script/DinnerCharacterSelect.DinnerCharacterSelectSystem]\n" + string.Concat(definitions
            .Where(CustomCharacterProjectService.IsCharacter)
            .Select(project => CustomCharacterProjectService.Scope(project.CustomCharacter!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Select(scope => $"+SortedCharacterTags=(TagName=\"{scope}\")\n"));
    }

    public static void WriteRosterConfig(string pluginDirectory, IReadOnlyList<NativeSuitProject> projects)
    {
        if (!projects.Any(CustomCharacterProjectService.IsCharacter)) return;
        var path = Path.Combine(pluginDirectory, "Config", "CharacterSelectSystem.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, RosterConfig(projects));
    }

    private static string CopyDonor(string nativeContent, string content, string sourcePackage, string targetPackage)
    {
        var source = Path.Combine(nativeContent, sourcePackage[6..].Replace('/', Path.DirectorySeparatorChar));
        var output = Path.Combine(content, targetPackage[6..].Replace('/', Path.DirectorySeparatorChar));
        foreach (var ext in new[] { ".uasset", ".uexp" })
        {
            if (!File.Exists(source + ext)) throw new FileNotFoundException("Custom character registration needs " + sourcePackage + ". Run a new Full character extraction in Settings before building.", source + ext);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(source + ext, output + ext, false);
        }
        return output + ".uasset";
    }
    private static UAsset Read(string file, Usmap maps) => new(file, EngineVersion.VER_UE5_6, maps, CustomSerializationFlags.SkipPreloadDependencyLoading);
    private static void Rename(UAsset asset, string from, string to)
    {
        var oldName = UnrealPathUtil.AssetName(from); var newName = UnrealPathUtil.AssetName(to);
        var names = asset.GetNameMapIndexList();
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i].ToString();
            if (name == from) asset.SetNameReference(i, new FString(to));
            else if (name == oldName) asset.SetNameReference(i, new FString(newName));
        }
        asset.FolderName = new FString(to);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
