using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>
/// The donor character the suit is actually built from (Batman, ThomasWayne, …),
/// detected from the suit's own playable/archetype rather than assumed. All the
/// clone sources (archetype, char anim sets, DPRD, ability set) are family-relative.
/// </summary>
public sealed class DonorInfo
{
    public string Family { get; set; } = "";
    public string ArchetypePackage { get; set; } = "";
    public string ArchetypeStem { get; set; } = "";
    public string MasCharPackage { get; set; } = "";
    public string LasCharPackage { get; set; } = "";
    public string DprdPackage { get; set; } = "";
    public string AbilitySetPackage { get; set; } = "";

    public string ArchetypeStemName => ArchetypeStem;
    public string MasCharStem => UnrealPathUtil.AssetName(MasCharPackage);
    public string LasCharStem => UnrealPathUtil.AssetName(LasCharPackage);
    public string DprdStem => UnrealPathUtil.AssetName(DprdPackage);
    public string AbilitySetStem => UnrealPathUtil.AssetName(AbilitySetPackage);
    public bool Valid => !string.IsNullOrEmpty(ArchetypePackage);
}

/// <summary>
/// Post-generation step for custom-archetype suits: when a foreign gadget was
/// added, clone the donor's MAS_Char/LAS_Char into the mod, graft the gadget's
/// equipment anim blocks into them, and repoint the (already-cloned) custom
/// archetype at the new sets. Only runs when <see cref="NativeSuitProject.UseCustomArchetype"/>
/// is on and there is at least one foreign gadget with resolvable anim blocks.
/// Runs against the patched content root, so it packages into the same pak.
/// </summary>
public sealed class AnimArchetypeGraftService
{
    private const CustomSerializationFlags NameMapOnly =
        CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading;

    /// <summary>
    /// Detects the donor family + its clone-source assets from the suit's playable:
    /// playable → parent BP_CAT_Archetype_&lt;Family&gt; → its MAS_Char/LAS_Char/DPRD;
    /// DPRD → the family AS_&lt;Family&gt; ability set. Reads import tables only.
    /// </summary>
    /// <summary>
    /// Detects the donor from the suit's PRISTINE base playable template (never
    /// modified by our pipeline), so it's stable across repeated packages. Falls
    /// back to the staged playable only if the template file is unavailable.
    /// </summary>
    /// <summary>
    /// Whether the suit's base can carry gadgets - i.e. its playable has the gadget
    /// machinery (EquipmentContainer/EquipmentManager components). If present we can
    /// drive gadgets even when the DPRD lacks an Equipment array (the graft creates
    /// one). Only truly non-combat bases without those components can't equip.
    /// Best-effort: unknown → assume supported (don't block).
    /// </summary>
    public bool BaseSupportsEquipment(NativeSuitProject project, out string family)
    {
        family = "";
        try
        {
            var mappings = LoadMappings();
            var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var donor = DetectDonorForProject(project, extractedRoot, mappings);
            family = donor?.Family ?? "";

            var playable = project.PlayableTemplate?.Uasset;
            if (string.IsNullOrWhiteSpace(playable) || !File.Exists(playable))
            {
                return true; // couldn't determine — don't block
            }
            // Component template names are plain FNames in the asset's name table.
            var text = File.ReadAllText(playable, System.Text.Encoding.Latin1);
            return text.Contains("EquipmentManager", StringComparison.Ordinal)
                   || text.Contains("EquipmentContainer", StringComparison.Ordinal);
        }
        catch
        {
            return true; // never block on a detection error
        }
    }

    /// <summary>
    /// Whether the suit's BASE character natively has a glide visual - a skeletal
    /// mesh component tagged "Glider" (e.g. Batman's cape, Catwoman/Nightwing's
    /// wingsuit). Only those carry the proven GE_ShowGlider → Visible.Glider ABPTag
    /// visibility wiring, so a wingsuit can be applied by repointing that component.
    /// Returns the component's variable name (e.g. "Cape") or null if none.
    /// Reads the PRISTINE base template, not the (possibly graft-modified) stage.
    /// </summary>
    /// <summary>
    /// Whether the base was inspected, and what was found. <see cref="Unknown"/> exists because the
    /// answer used to be a bare null for both "this base has no cape" and "I couldn't read the base",
    /// which made a pruned asset dump look like a civilian base and produced a confidently wrong
    /// warning on suits that glide fine.
    /// </summary>
    public enum GlideVisualStatus { Unknown, Present, Absent }

    /// <summary>
    /// Describes the two-part cape contract used by characters whose normal cape is hidden while
    /// their separate glide visual is shown. A character with only a Glider component cannot safely
    /// accept an additional cosmetic cape: the gameplay Blueprint has no proven visibility wiring
    /// for that newly-added cape, so both meshes can remain visible during flight.
    /// </summary>
    public enum CapeGlideContractStatus { Unknown, Paired, GlideOnly, CapeOnly, Neither }

    public CapeGlideContractStatus BaseCapeGlideContract(NativeSuitProject project)
    {
        var playable = ResolveTemplateUasset(
            project.PlayableTemplate,
            AppSettings.Current.EffectiveExtractedContentRoot());
        if (string.IsNullOrWhiteSpace(playable) || !File.Exists(playable))
        {
            return CapeGlideContractStatus.Unknown;
        }

        try
        {
            var asset = new UAsset(
                playable,
                EngineVersion.VER_UE5_6,
                LoadMappings(),
                CustomSerializationFlags.SkipPreloadDependencyLoading);
            var hasCosmeticCape = false;
            var hasGlider = false;
            foreach (var exp in asset.Exports.OfType<NormalExport>())
            {
                var tags = exp.Data.OfType<UAssetAPI.PropertyTypes.Objects.ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString() == "ComponentTags")?
                    .Value?.OfType<UAssetAPI.PropertyTypes.Objects.NamePropertyData>()
                    .Select(tag => tag.Value.ToString())
                    .ToList() ?? [];
                var isGlider = tags.Any(tag =>
                    tag.Equals("Glider", StringComparison.OrdinalIgnoreCase));
                var isCape = tags.Any(tag =>
                    tag.Equals("Cape", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("TtCharacterAsset.Cape", StringComparison.OrdinalIgnoreCase));

                hasGlider |= isGlider;
                hasCosmeticCape |= isCape && !isGlider;
            }

            return (hasCosmeticCape, hasGlider) switch
            {
                (true, true) => CapeGlideContractStatus.Paired,
                (false, true) => CapeGlideContractStatus.GlideOnly,
                (true, false) => CapeGlideContractStatus.CapeOnly,
                _ => CapeGlideContractStatus.Neither
            };
        }
        catch
        {
            return CapeGlideContractStatus.Unknown;
        }
    }

    /// <summary>
    /// Looks for a glide visual on the base. Returns <see cref="GlideVisualStatus.Unknown"/> when the
    /// base asset can't be read at all - callers must not report that as "no glide visual".
    /// </summary>
    public GlideVisualStatus BaseGlideVisual(NativeSuitProject project, out string? component)
    {
        component = null;
        var playable = project.PlayableTemplate?.Uasset;
        if (string.IsNullOrWhiteSpace(playable) || !File.Exists(playable))
        {
            return GlideVisualStatus.Unknown;
        }
        try
        {
            component = ScanForGlideComponent(playable);
            return component is null ? GlideVisualStatus.Absent : GlideVisualStatus.Present;
        }
        catch
        {
            // An unparseable base tells us nothing about its cape.
            return GlideVisualStatus.Unknown;
        }
    }

    public string? BaseGlideVisualComponent(NativeSuitProject project)
    {
        try
        {
            var playable = project.PlayableTemplate?.Uasset;
            if (string.IsNullOrWhiteSpace(playable) || !File.Exists(playable))
            {
                return null;
            }
            return ScanForGlideComponent(playable);
        }
        catch
        {
            return null;
        }
    }

    private string? ScanForGlideComponent(string playable)
    {
        {
            var asset = new UAsset(playable, EngineVersion.VER_UE5_6, LoadMappings(), CustomSerializationFlags.SkipPreloadDependencyLoading);

            // The glide visual is a component tagged "Glider" - a glide-ONLY skeletal
            // mesh (e.g. Catwoman "Cape" = SK_GA_Wingsuit; Batman "Torso" = SK_CAPE_Glide
            // + ABP_Cape_Glide), separate from the body (CharacterMesh0, never Glider-
            // tagged), hidden until glide. Repointing preserves this component identity, but a
            // replacement is compatible with a separate regular Cape only when its AnimBlueprint
            // implements the paired visibility contract; GliderService enforces that separately.
            // Prefer a "Cape"-named one; otherwise take the first Glider-tagged one.
            string? fallback = null;
            foreach (var exp in asset.Exports.OfType<NormalExport>())
            {
                var varName = exp.ObjectName.ToString().Replace("_GEN_VARIABLE", "");
                var tags = exp.Data.OfType<UAssetAPI.PropertyTypes.Objects.ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name.ToString() == "ComponentTags");
                var hasGlider = tags?.Value?.OfType<UAssetAPI.PropertyTypes.Objects.NamePropertyData>()
                    .Any(t => t.Value.ToString().Equals("Glider", StringComparison.OrdinalIgnoreCase)) == true;
                if (!hasGlider)
                {
                    continue;
                }
                if (varName.Equals("Cape", StringComparison.OrdinalIgnoreCase))
                {
                    return varName;
                }
                fallback ??= varName;
            }
            return fallback;
        }
    }

    /// <summary>
    /// Resolves a saved template to a live .uasset. A package path survives an Extract
    /// Game Assets refresh while the absolute path recorded in an older suit project does not.
    /// </summary>
    private static string? ResolveTemplateUasset(TemplateRecord? template, string extractedContentRoot)
    {
        if (template is null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(template.Uasset) && File.Exists(template.Uasset))
        {
            return template.Uasset;
        }
        if (!string.IsNullOrWhiteSpace(template.PackagePath))
        {
            var refreshed = PackageToBase(extractedContentRoot, template.PackagePath) + ".uasset";
            if (File.Exists(refreshed))
            {
                return refreshed;
            }
        }
        return null;
    }

    public static DonorInfo? DetectDonorForProject(NativeSuitProject project, string contentRoot, Usmap? mappings)
    {
        // 1) The base character's OWN machinery (normal heroes have a BP_CAT_Archetype).
        // Saved projects retain absolute donor paths after an extract refresh, so resolve
        // the stable /Game package path against the active extract before looking at the
        // generated stage.
        var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
        var basePlayable = ResolveTemplateUasset(project.PlayableTemplate, extractedRoot);
        if (!string.IsNullOrWhiteSpace(basePlayable))
        {
            var d = DetectDonor(basePlayable, extractedRoot, mappings);
            if (d is not null && d.Valid)
            {
                return d;
            }
        }
        var staged = DetectDonor(StageUasset(contentRoot, project.TargetPackages.Playable), contentRoot, mappings);
        if (staged is not null && staged.Valid)
        {
            return staged;
        }

        // 2) The base has no machinery of its own (villain/NPC with no archetype). Inherit
        // from the machinery donor the user chose - a hero playable in extracted content.
        if (!string.IsNullOrWhiteSpace(project.MachineryDonorPlayable))
        {
            var donorUasset = PackageToBase(AppSettings.Current.EffectiveExtractedContentRoot(), project.MachineryDonorPlayable) + ".uasset";
            var md = DetectDonor(donorUasset, AppSettings.Current.EffectiveExtractedContentRoot(), mappings);
            if (md is not null && md.Valid)
            {
                return md;
            }
        }
        return staged; // may be null/invalid — the archetype pipeline reports a clear error
    }

    /// <summary>
    /// The full locomotion asset graph for a family: LAS_Default → its ABPs →
    /// their BlendSpaces, and the AnimSequence each asset plays. Lets us override
    /// idle (ABP_Core), walk/run (ABP_Movement's BlendSpaces) and sprint alike.
    /// </summary>
    public sealed class LocomotionGraph
    {
        public string Family { get; set; } = "";
        public string LasDefaultPackage { get; set; } = "";
        public List<string> AbpPackages { get; } = new();                          // ABPs referenced by LAS_Default
        public Dictionary<string, List<string>> AbpBlendSpaces { get; } = new();     // abp → BS packages it references
        // owner package (ABP or BS) → the A_* sequences it directly references.
        public Dictionary<string, List<(string Name, string Package)>> Sequences { get; } = new();

        public IEnumerable<(string Name, string Package)> AllSequences =>
            Sequences.Values.SelectMany(v => v).GroupBy(s => s.Package).Select(g => g.First());
    }

    public static LocomotionGraph DetectLocomotionGraph(string family, Usmap? mappings)
    {
        var g = new LocomotionGraph { Family = family };
        var extracted = AppSettings.Current.EffectiveExtractedContentRoot();
        try
        {
            g.LasDefaultPackage = $"/Game/Animation/LayerAnimSets/Default/LAS_Default_{family}";
            var lasFile = PackageToBase(extracted, g.LasDefaultPackage) + ".uasset";
            if (!File.Exists(lasFile)) return g;

            List<string> Imports(string pkg)
            {
                var f = PackageToBase(extracted, pkg) + ".uasset";
                if (!File.Exists(f)) return new List<string>();
                var a = new UAsset(f, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
                return a.Imports.Select(i => i.ObjectName.ToString())
                    .Where(n => n.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            }

            bool IsSeq(string n) => n.Contains("/A_", StringComparison.OrdinalIgnoreCase) && UnrealPathUtil.AssetName(n).StartsWith("A_", StringComparison.OrdinalIgnoreCase);
            bool IsBs(string n) => UnrealPathUtil.AssetName(n).StartsWith("BS_", StringComparison.OrdinalIgnoreCase);
            bool IsAbp(string n) => UnrealPathUtil.AssetName(n).StartsWith("ABP_", StringComparison.OrdinalIgnoreCase) && n.Contains($"_{family}", StringComparison.OrdinalIgnoreCase);

            void AddSeq(string owner, string n)
            {
                if (!g.Sequences.TryGetValue(owner, out var l)) { l = new(); g.Sequences[owner] = l; }
                if (l.All(s => s.Package != n)) l.Add((UnrealPathUtil.AssetName(n), n));
            }

            foreach (var abp in Imports(g.LasDefaultPackage).Where(IsAbp))
            {
                if (g.AbpPackages.Contains(abp)) continue;
                g.AbpPackages.Add(abp);
                g.AbpBlendSpaces[abp] = new();
                foreach (var r in Imports(abp))
                {
                    if (IsSeq(r)) AddSeq(abp, r);
                    else if (IsBs(r))
                    {
                        g.AbpBlendSpaces[abp].Add(r);
                        foreach (var s in Imports(r).Where(IsSeq)) AddSeq(r, s);
                    }
                }
            }
        }
        catch { /* best effort */ }
        return g;
    }

    /// <summary>Flat list of overridable locomotion sequences (idle/walk/run/sprint) for the UI.</summary>
    public static List<(string Name, string Package)> DetectLocomotionSequences(string family, Usmap? mappings) =>
        DetectLocomotionGraph(family, mappings).AllSequences
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static DonorInfo? DetectDonor(string playableUasset, string contentRoot, Usmap? mappings)
    {
        try
        {
            if (!File.Exists(playableUasset)) return null;
            var playable = new UAsset(playableUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
            var archPkg = playable.Imports
                .Select(i => i.ObjectName.ToString())
                .FirstOrDefault(IsCharacterArchetypePackage);
            if (archPkg is null) return null;

            var info = new DonorInfo
            {
                ArchetypePackage = archPkg,
                ArchetypeStem = UnrealPathUtil.AssetName(archPkg),
            };

            var archUasset = PackageToBase(contentRoot, archPkg) + ".uasset";
            if (!File.Exists(archUasset))
            {
                // Archetype lives in the base game, not our stage - read from extracted content.
                archUasset = PackageToBase(AppSettings.Current.EffectiveExtractedContentRoot(), archPkg) + ".uasset";
            }
            if (File.Exists(archUasset))
            {
                var arch = new UAsset(archUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
                foreach (var n in arch.Imports.Select(i => i.ObjectName.ToString()))
                {
                    if (n.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n.Contains("/MAS_Char_", StringComparison.OrdinalIgnoreCase)) info.MasCharPackage = n;
                        else if (n.Contains("/LAS_Char_", StringComparison.OrdinalIgnoreCase)) info.LasCharPackage = n;
                        else if (n.Contains("/DA_DPRD_", StringComparison.OrdinalIgnoreCase)) info.DprdPackage = n;
                    }
                }

                // Family from the anim-set name (e.g. MAS_Char_ThomasWayne → ThomasWayne)
                // - robust even if the playable was already reparented to a mod archetype.
                if (info.MasCharStem.StartsWith("MAS_Char_", StringComparison.OrdinalIgnoreCase))
                {
                    info.Family = info.MasCharStem["MAS_Char_".Length..];
                }
                else if (info.LasCharStem.StartsWith("LAS_Char_", StringComparison.OrdinalIgnoreCase))
                {
                    info.Family = info.LasCharStem["LAS_Char_".Length..];
                }

                var dprdUasset = PackageToBase(AppSettings.Current.EffectiveExtractedContentRoot(), info.DprdPackage) + ".uasset";
                if (!string.IsNullOrEmpty(info.DprdPackage) && File.Exists(dprdUasset))
                {
                    var dprd = new UAsset(dprdUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
                    info.AbilitySetPackage = dprd.Imports
                        .Select(i => i.ObjectName.ToString())
                        .FirstOrDefault(n => n.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) &&
                                             n.Contains($"/AS_{info.Family}", StringComparison.OrdinalIgnoreCase)) ?? "";
                }
            }
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Playable families normally inherit from BP_CAT_Archetype_*, but Catwoman's
    /// native parent is the differently named BP_Catwoman_Archetype. Keep that
    /// exception explicit so boss/NPC assets such as BP_Firefly_Boss_Archetype do
    /// not become eligible gameplay donors merely because their names contain
    /// "Archetype".
    /// </summary>
    internal static bool IsCharacterArchetypePackage(string? packagePath)
    {
        var package = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!package.StartsWith("/Game/Characters/Minifig/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = UnrealPathUtil.AssetName(package);
        return name.StartsWith("BP_CAT_Archetype_", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("BP_Catwoman_Archetype", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The base playable's actual parent class package (a /BP_Master/ base class like
    /// BP_NPC_Quest for villains, or a BP_CAT_Archetype for heroes), read from its imports.
    /// Used to reparent a villain/NPC base onto a donor's playable archetype.</summary>
    public static string? DetectBaseParentPackage(string playableUasset, Usmap? mappings)
    {
        try
        {
            if (!File.Exists(playableUasset)) return null;
            var a = new UAsset(playableUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
            // Prefer a /BP_Master/ base class (the real parent); fall back to any archetype.
            var names = a.Imports.Select(i => i.ObjectName.ToString())
                .Where(n => n.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return names.FirstOrDefault(n => n.Contains("/BP_Master/", StringComparison.OrdinalIgnoreCase))
                   ?? names.FirstOrDefault(n => n.Contains("/BP_CAT_Archetype_", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts a character's identity materials from its BP imports so a villain/NPC's
    /// LOOK can be reskinned onto a working donor playable (NPCs can't be made playable by
    /// reparenting - their body/movement setup lives in their NPC parent class). Returns the
    /// body material (MI_&lt;Char&gt;… under the character's Materials folder) and the face
    /// material (MI_FACE_…). Either may be empty if not found.</summary>
    public static (string BodyMi, string FaceMi) ExtractCharacterMaterials(string playableUasset, string characterFolder, Usmap? mappings)
    {
        try
        {
            if (!File.Exists(playableUasset)) return ("", "");
            var a = new UAsset(playableUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
            var mis = a.Imports.Select(i => i.ObjectName.ToString())
                .Where(n => n.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) &&
                            UnrealPathUtil.AssetName(n).StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            var face = mis.FirstOrDefault(n => UnrealPathUtil.AssetName(n).StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase)) ?? "";
            // Body: the character's own material (…/Minifig/<Char>/Materials/MI_<Char>…), not
            // face/hair/cape attachment materials.
            var body = mis.FirstOrDefault(n =>
                           n.Contains($"/Minifig/{characterFolder}/", StringComparison.OrdinalIgnoreCase) &&
                           IsCharacterMaterialFolder(n) &&
                           !UnrealPathUtil.AssetName(n).StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase) &&
                           !UnrealPathUtil.AssetName(n).StartsWith("MI_HAIR_", StringComparison.OrdinalIgnoreCase))
                       ?? "";
            body = string.IsNullOrWhiteSpace(body)
                ? FindCharacterBodyMaterialOnDisk(playableUasset, characterFolder)
                : body;
            return (body, face);
        }
        catch
        {
            return ("", "");
        }
    }

    private static bool IsCharacterMaterialFolder(string packagePath) =>
        packagePath.Contains("/Material/", StringComparison.OrdinalIgnoreCase) ||
        packagePath.Contains("/Materials/", StringComparison.OrdinalIgnoreCase);

    private static string FindCharacterBodyMaterialOnDisk(string characterBlueprint, string characterFolder)
    {
        try
        {
            var characterRoot = Path.GetDirectoryName(characterBlueprint);
            if (string.IsNullOrWhiteSpace(characterRoot) || !Directory.Exists(characterRoot))
            {
                return "";
            }

            var contentRoot = new DirectoryInfo(characterRoot);
            while (contentRoot is not null &&
                   !contentRoot.Name.Equals("Content", StringComparison.OrdinalIgnoreCase))
            {
                contentRoot = contentRoot.Parent;
            }
            if (contentRoot is null)
            {
                return "";
            }

            var prefix = $"MI_{characterFolder}_";
            var candidates = Directory.EnumerateFiles(characterRoot, "MI_*.uasset", SearchOption.AllDirectories)
                .Select(path => new
                {
                    Path = path,
                    Name = Path.GetFileNameWithoutExtension(path),
                })
                .Where(candidate =>
                    !candidate.Name.StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.Name.StartsWith("MI_HAIR_", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.Name.StartsWith("MI_CAPE_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Name.Equals($"MI_{characterFolder}_EOM", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(candidate => candidate.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = candidates.FirstOrDefault();
            if (selected is null)
            {
                return "";
            }

            var relative = Path.GetRelativePath(contentRoot.FullName, selected.Path);
            return "/Game/" + Path.ChangeExtension(relative, null)!.Replace('\\', '/');
        }
        catch
        {
            return "";
        }
    }

    public sealed class Result
    {
        public string Status { get; set; } = "skipped";
        public string? Error { get; set; }
        public List<string> Log { get; } = new();
    }

    /// <summary>
    /// Applies the FULL custom-archetype pipeline to a final packaged content root
    /// (which may be the grafted-parts stage, not the name-map stage): clones the
    /// donor archetype into it, reparents the playable + cutscene there, then runs
    /// the anim/equipment/visual graft. Idempotent - name-map repoints no-op if the
    /// root was already processed. This is what makes archetype suits with grafted
    /// parts actually package with their animations/equipment.
    /// </summary>
    public Result ApplyToPackagedRoot(NativeSuitProject project, string contentRoot)
    {
        var result = new Result();
        if (!project.UseCustomArchetype)
        {
            return result; // skipped
        }
        var custom = UAssetPatchService.CustomArchetypePackage(project);
        if (custom is null)
        {
            return result;
        }

        try
        {
            var mappings = LoadMappings();
            var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var customStem = UnrealPathUtil.AssetName(custom);

            var donor = DetectDonorForProject(project, contentRoot, mappings);
            if (donor is null || !donor.Valid)
            {
                result.Status = "error";
                result.Error = "Could not detect the suit's donor archetype from its playable.";
                return result;
            }
            result.Log.Add($"donor family = {donor.Family} (archetype {donor.ArchetypeStem})");

            // 0) Purge generated derivatives from any PRIOR package run so a config
            //    change (e.g. dropping the whip and its GA_Item ability) can't leave
            //    stale AS_/DPRD_/anim-set assets behind in the persisted stage. Each
            //    is regenerated below only if the current config still needs it.
            PurgeGeneratedArchetypeDerivatives(contentRoot, custom, result);

            // 1) Clone the donor archetype into the packaged root (if not present).
            CloneDonorAsset(extractedRoot, donor.ArchetypePackage, donor.ArchetypeStem,
                contentRoot, custom, customStem, mappings, result);

            // 2) Reparent the playable + cutscene in the packaged root (name-map only,
            //    so it composes cleanly with any part grafts already applied).
            var reparent = new Dictionary<string, string>
            {
                [donor.ArchetypePackage] = custom,
                ["Default__" + donor.ArchetypeStem + "_C"] = "Default__" + customStem + "_C",
                [donor.ArchetypeStem + "_C"] = customStem + "_C",
                [donor.ArchetypeStem] = customStem,
            };

            // Machinery-donor (villain/NPC base): the base's OWN parent is a /BP_Master/ base
            // class (e.g. BP_NPC_Quest), NOT the donor archetype - so the rename above finds
            // nothing and the base stays a non-playable NPC. Additionally swap its real parent
            // to the cloned playable archetype so it becomes a proper playable that inherits the
            // donor's family. Gated on a machinery donor so hero suits are untouched.
            if (!string.IsNullOrWhiteSpace(project.MachineryDonorPlayable))
            {
                var basePlayableStaged = StageUasset(contentRoot, project.TargetPackages.Playable);
                var baseParent = DetectBaseParentPackage(basePlayableStaged, mappings);
                var baseParentStem = UnrealPathUtil.AssetName(baseParent ?? "");
                // Only reparent a genuinely NON-playable NPC base class (e.g. BP_NPC_Quest). NEVER
                // reparent a playable MASTER (/BP_Master/BPs_Playable/BP_Playable): a villain reskin
                // builds on the Batman DONOR, so the staged base is ALREADY a proper playable whose
                // grandparent is BP_Playable. Renaming BP_Playable → the archetype globally rewrites
                // every inherited-component owner ref (Box/HitBox/Camera/Sphere/etc. all become
                // archetype-owned instead of BP_Playable-owned) → the pawn's component templates fail
                // to resolve → INVISIBLE + UNCONTROLLABLE. This was the real root cause of the broken
                // Joker suit (2026-07-11): a stale MachineryDonorPlayable enabled this block, and the
                // detected parent was BP_Playable, not an NPC class. Skip any playable-master parent.
                if (!string.IsNullOrWhiteSpace(baseParent) &&
                    !baseParent.Contains("/BP_CAT_Archetype_", StringComparison.OrdinalIgnoreCase) &&
                    !baseParent.Contains("/BPs_Playable/", StringComparison.OrdinalIgnoreCase) &&
                    !baseParentStem.Contains("Playable", StringComparison.OrdinalIgnoreCase) &&
                    !reparent.ContainsKey(baseParent))
                {
                    var parentStem = UnrealPathUtil.AssetName(baseParent);
                    reparent[baseParent] = custom;
                    reparent["Default__" + parentStem + "_C"] = "Default__" + customStem + "_C";
                    reparent[parentStem + "_C"] = customStem + "_C";
                    result.Log.Add($"machinery donor: reparenting villain/NPC base parent {parentStem} → {customStem} (so it becomes a playable)");
                }
            }

            foreach (var pkg in new[] { project.TargetPackages.Playable, project.TargetPackages.Cutscene })
            {
                var uasset = StageUasset(contentRoot, pkg);
                if (File.Exists(uasset))
                {
                    var n = ApplyNameMapReplacements(uasset, reparent, mappings);
                    result.Log.Add($"reparent {UnrealPathUtil.AssetName(pkg)}: {n} name(s)");
                }
                else
                {
                    result.Log.Add($"reparent {UnrealPathUtil.AssetName(pkg)}: <missing in packaged root>");
                }
            }
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }

        // 3) MAS/LAS/DPRD/AS graft + archetype repoint (operates on the same root).
        var g = Graft(project, contentRoot);
        result.Log.AddRange(g.Log);
        result.Status = g.Status == "skipped" ? "ok" : g.Status;
        if (!string.IsNullOrWhiteSpace(g.Error))
        {
            result.Error = g.Error;
        }
        return result;
    }

    public Result Graft(NativeSuitProject project, string patchedContentRoot)
    {
        var result = new Result();
        if (!project.UseCustomArchetype)
        {
            return result; // skipped: reparent off
        }

        var customArchetypePkg = UAssetPatchService.CustomArchetypePackage(project);
        if (customArchetypePkg is null)
        {
            return result;
        }

        var mod = ModOf(project.TargetPackages.Playable);
        var gd = GameDataService.Instance;
        var donorFamily = project.BaseProfile?.GameplayFamily;
        if (string.IsNullOrWhiteSpace(donorFamily))
        {
            donorFamily = gd.FamilyForBasePath(project.PlayableTemplate?.PackagePath ?? "")?.Name ?? "";
        }

        // Foreign gadgets (not native to the selected gameplay donor). Each needs its
        // equipment definition in the DPRD loadout (to actually equip) and its
        // anim blocks in MAS/LAS (to animate).
        var foreignMas = new List<string>();
        var foreignLas = new List<string>();
        var foreignEd = new List<(int Slot, string EdPackage)>();
        var foreignAbilities = new List<string>();
        var foreignAbilitySets = new List<string>();
        foreach (var change in project.EquipmentSlots)
        {
            var eq = gd.FindEquipment(change.Gadget);
            if (eq is null ||
                (!string.IsNullOrWhiteSpace(donorFamily) &&
                 eq.NativeFamilies.Contains(donorFamily, StringComparer.OrdinalIgnoreCase)))
            {
                continue; // native to donor — already in the loadout/animated
            }
            if (!string.IsNullOrWhiteSpace(eq.MontageAnimSet) && !foreignMas.Contains(eq.MontageAnimSet))
            {
                foreignMas.Add(eq.MontageAnimSet);
            }
            if (!string.IsNullOrWhiteSpace(eq.LayerAnimSet) && !foreignLas.Contains(eq.LayerAnimSet))
            {
                foreignLas.Add(eq.LayerAnimSet);
            }
            if (!string.IsNullOrWhiteSpace(eq.EdPackage))
            {
                foreignEd.Add((change.Slot, eq.EdPackage));
            }
            var controllerSets = EquipmentDependencyService.RequiredAbilitySets(eq, donorFamily);
            if (controllerSets.Count == 0)
            {
                foreach (var ability in eq.VisualAbilities)
                {
                    if (!foreignAbilities.Contains(ability))
                    {
                        foreignAbilities.Add(ability);
                    }
                }
            }
            foreach (var abilitySet in controllerSets)
            {
                if (!foreignAbilitySets.Contains(abilitySet))
                {
                    foreignAbilitySets.Add(abilitySet);
                }
            }
            if (controllerSets.Count > 0)
            {
                result.Log.Add(
                    $"equipment dependency [{eq.Name}]: adding native controller set " +
                    string.Join(", ", controllerSets.Select(UnrealPathUtil.AssetName)));
            }
        }

        if (project.PartGrafts.Any(graft => graft.IsGlider) &&
            !foreignAbilitySets.Contains(GliderService.GlidingAbilitySetPackage))
        {
            foreignAbilitySets.Add(GliderService.GlidingAbilitySetPackage);
            result.Log.Add("glider dependency: adding native AS_Gliding ability set");
        }

        // Cross-type glider: inject the donor character's glide anim sets
        // (LAS_Traversal_<Char> + MAS_Glide_<Char>) as parent sets so the body plays that
        // glide pose. The wingsuit membrane is driven by CopyPoseFromMesh - without the
        // matching arms-spread pose it collapses and is invisible (findings §12). Only
        // inject sets that exist on disk, else a dangling parent-set ref breaks LAS_Char.
        {
            var gliderExtractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            if (!string.IsNullOrWhiteSpace(project.GliderAnimLas) && !foreignLas.Contains(project.GliderAnimLas))
            {
                if (File.Exists(PackageToBase(gliderExtractedRoot, project.GliderAnimLas) + ".uasset"))
                {
                    foreignLas.Add(project.GliderAnimLas);
                }
                else
                {
                    result.Log.Add($"glider LAS not found on disk, skipped (glide pose won't change): {project.GliderAnimLas}");
                }
            }
            if (!string.IsNullOrWhiteSpace(project.GliderAnimMas) && !foreignMas.Contains(project.GliderAnimMas))
            {
                if (File.Exists(PackageToBase(gliderExtractedRoot, project.GliderAnimMas) + ".uasset"))
                {
                    foreignMas.Add(project.GliderAnimMas);
                }
                else
                {
                    result.Log.Add($"glider MAS not found on disk, skipped: {project.GliderAnimMas}");
                }
            }
        }

        if (foreignMas.Count == 0 && foreignLas.Count == 0 && foreignEd.Count == 0 &&
            foreignAbilitySets.Count == 0 &&
            project.AnimationOverrides.Count == 0 && project.LocomotionOverrides.Count == 0)
        {
            result.Log.Add("no foreign gadgets or animation overrides — archetype left on donor sets");
            return result;
        }

        try
        {
            var mappings = LoadMappings();
            var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var archetypeUasset = StageUasset(patchedContentRoot, customArchetypePkg);
            var graft = new AnimGraftService();

            var donor = DetectDonorForProject(project, patchedContentRoot, mappings);
            if (donor is null || !donor.Valid || string.IsNullOrEmpty(donor.MasCharPackage) || string.IsNullOrEmpty(donor.LasCharPackage))
            {
                result.Status = "error";
                result.Error = "Could not detect donor anim sets (MAS_Char/LAS_Char) from the suit's archetype.";
                return result;
            }

            var customMasPkg = $"/Game/Mods/{mod}/Characters/MAS_Char_{mod}";
            var customLasPkg = $"/Game/Mods/{mod}/Characters/LAS_Char_{mod}";
            var masStem = UnrealPathUtil.AssetName(customMasPkg);
            var lasStem = UnrealPathUtil.AssetName(customLasPkg);

            // Animation-set overrides (whole building-block swaps, e.g. locomotion
            // LAS_Default_Batman → LAS_Default_Catwoman).
            var montageOverrides = project.AnimationOverrides.Where(o => o.Kind.Equals("Montage", StringComparison.OrdinalIgnoreCase)).ToList();
            var layerOverrides = project.AnimationOverrides.Where(o => o.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase)).ToList();

            // --- Animations: clone MAS/LAS, inject foreign blocks + apply overrides, repoint. ---
            var needMas = foreignMas.Count > 0 || montageOverrides.Count > 0;
            var needLas = foreignLas.Count > 0 || layerOverrides.Count > 0 || project.LocomotionOverrides.Count > 0;
            if (needMas || needLas)
            {
                if (needMas) CloneDonorAsset(extractedRoot, donor.MasCharPackage, donor.MasCharStem, patchedContentRoot, customMasPkg, masStem, mappings, result);
                if (needLas) CloneDonorAsset(extractedRoot, donor.LasCharPackage, donor.LasCharStem, patchedContentRoot, customLasPkg, lasStem, mappings, result);

                if (foreignMas.Count > 0)
                {
                    var r = graft.InjectParentSets(StageUasset(patchedContentRoot, customMasPkg), "TTAnimSet", foreignMas);
                    result.Log.Add($"MAS graft: {r.Status} added=[{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                }
                foreach (var o in montageOverrides)
                {
                    var donorSet = DonorSetForCategory(o.Category, donor.Family);
                    var r = graft.ReplaceParentSet(StageUasset(patchedContentRoot, customMasPkg), "TTAnimSet", donorSet, o.ReplacementPackage);
                    result.Log.Add($"MAS override [{o.Category}]: {r.Status} {donorSet}→{o.ReplacementSet}{ErrSuffix(r.Error)}");
                }
                if (foreignLas.Count > 0)
                {
                    var r = graft.InjectParentSets(StageUasset(patchedContentRoot, customLasPkg), "TTLayerSet", foreignLas);
                    result.Log.Add($"LAS graft: {r.Status} added=[{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                }
                foreach (var o in layerOverrides)
                {
                    var donorSet = DonorSetForCategory(o.Category, donor.Family);
                    var r = graft.ReplaceParentSet(StageUasset(patchedContentRoot, customLasPkg), "TTLayerSet", donorSet, o.ReplacementPackage);
                    result.Log.Add($"LAS override [{o.Category}]: {r.Status} {donorSet}→{o.ReplacementSet}{ErrSuffix(r.Error)}");
                }

                // --- Per-animation locomotion: clone the suit's OWN locomotion graph
                //     (LAS_Default → ABPs → BlendSpaces), repoint the overridden idle/
                //     walk/run/sprint AnimSequences, and rewire the clones. Same graph =
                //     no crash; only pose assets change. ---
                if (project.LocomotionOverrides.Count > 0)
                {
                    var g = DetectLocomotionGraph(donor.Family, mappings);
                    var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // donor pkg → custom pkg
                    string CustomPkg(string donorPkg)
                    {
                        var stem = UnrealPathUtil.AssetName(donorPkg);
                        var cstem = stem.EndsWith($"_{donor.Family}", StringComparison.OrdinalIgnoreCase)
                            ? stem[..^donor.Family.Length] + mod
                            : stem + "_" + mod;
                        return $"/Game/Mods/{mod}/Characters/{cstem}";
                    }

                    // Clone LAS_Default + every ABP + every BlendSpace into the mod.
                    var toClone = new List<string> { g.LasDefaultPackage };
                    toClone.AddRange(g.AbpPackages);
                    toClone.AddRange(g.AbpBlendSpaces.Values.SelectMany(v => v));
                    foreach (var donorPkg in toClone.Distinct())
                    {
                        var cpkg = CustomPkg(donorPkg);
                        custom[donorPkg] = cpkg;
                        CloneDonorAsset(extractedRoot, donorPkg, UnrealPathUtil.AssetName(donorPkg), patchedContentRoot, cpkg, UnrealPathUtil.AssetName(cpkg), mappings, result);
                    }

                    // Repoint overridden sequences in whichever cloned asset owns them.
                    var seqReplace = new Dictionary<string, string>();
                    foreach (var o in project.LocomotionOverrides)
                    {
                        var replPkg = UnrealPathUtil.NormalizePackagePath(o.ReplacementPackage);
                        seqReplace[UnrealPathUtil.NormalizePackagePath(o.DonorSequencePackage)] = replPkg;
                        seqReplace[o.DonorSequence] = UnrealPathUtil.AssetName(replPkg);
                    }
                    var seqCount = 0;
                    foreach (var owner in g.Sequences.Keys)
                    {
                        if (custom.TryGetValue(owner, out var cowner))
                            seqCount += ApplyNameMapExact(StageUasset(patchedContentRoot, cowner), seqReplace, mappings);
                    }
                    result.Log.Add($"locomotion sequence repoint: {seqCount} name(s) for {project.LocomotionOverrides.Count} override(s)");

                    // Rewire clones: each ABP clone → its BlendSpace clones; LAS_Default clone → ABP clones.
                    foreach (var abp in g.AbpPackages)
                    {
                        var abpClone = custom[abp];
                        var bsRepoint = new Dictionary<string, string>();
                        foreach (var bs in g.AbpBlendSpaces[abp])
                        {
                            bsRepoint[bs] = custom[bs];
                            bsRepoint[UnrealPathUtil.AssetName(bs)] = UnrealPathUtil.AssetName(custom[bs]);
                        }
                        if (bsRepoint.Count > 0) ApplyNameMapReplacements(StageUasset(patchedContentRoot, abpClone), bsRepoint, mappings);
                    }
                    var lasRepoint = new Dictionary<string, string>();
                    foreach (var abp in g.AbpPackages)
                    {
                        lasRepoint[abp] = custom[abp];
                        lasRepoint[UnrealPathUtil.AssetName(abp)] = UnrealPathUtil.AssetName(custom[abp]);
                    }
                    var lasApplied = ApplyNameMapReplacements(StageUasset(patchedContentRoot, custom[g.LasDefaultPackage]), lasRepoint, mappings);
                    result.Log.Add($"LAS_Default → custom ABPs: {lasApplied} name(s)");

                    var rl = graft.ReplaceParentSet(StageUasset(patchedContentRoot, customLasPkg), "TTLayerSet", UnrealPathUtil.AssetName(g.LasDefaultPackage), custom[g.LasDefaultPackage]);
                    result.Log.Add($"LAS_Char → custom LAS_Default: {rl.Status} {string.Join(",", rl.Added)}{ErrSuffix(rl.Error)}");
                }

                var repoint = new Dictionary<string, string>();
                if (needMas) { repoint[donor.MasCharPackage] = customMasPkg; repoint[donor.MasCharStem] = masStem; }
                if (needLas) { repoint[donor.LasCharPackage] = customLasPkg; repoint[donor.LasCharStem] = lasStem; }
                var applied = ApplyNameMapReplacements(archetypeUasset, repoint, mappings);
                result.Log.Add($"archetype repoint → custom anim sets: {applied} name(s)");
            }

            // --- Loadout: clone DPRD, swap the gadget's ED into Equipment, repoint archetype. ---
            if (foreignEd.Count > 0 || foreignAbilitySets.Count > 0)
            {
                var customDprdPkg = $"/Game/Mods/{mod}/Characters/DA_DPRD_{mod}";
                var dprdStem = UnrealPathUtil.AssetName(customDprdPkg);
                CloneDonorAsset(extractedRoot, donor.DprdPackage, donor.DprdStem, patchedContentRoot, customDprdPkg, dprdStem, mappings, result);
                foreach (var (slot, edPkg) in foreignEd)
                {
                    // NOTE: the boss/NPC "equipment adapter" (clone ED + graft GetGadgetOutAbility via
                    // graft.SetGadgetDrawScaffolding / EquipmentNeedsDrawAdapter) is PARKED - the draw
                    // ability alone did not make FreezeGun usable in-game, so boss equipment needs more
                    // than the ED scaffolding (deferred research). Those helpers remain for when we
                    // resume; for now slot the gadget's base ED directly (proven path for hero gadgets).
                    var r = graft.SetEquipmentSlot(StageUasset(patchedContentRoot, customDprdPkg), slot, edPkg);
                    result.Log.Add($"DPRD equipment slot {slot + 1}: {r.Status} [{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                }
                var applied = ApplyNameMapReplacements(archetypeUasset, new Dictionary<string, string>
                {
                    [donor.DprdPackage] = customDprdPkg,
                    [donor.DprdStem] = dprdStem,
                }, mappings);
                result.Log.Add($"archetype repoint → custom DPRD: {applied} name(s)");

                foreach (var abilitySet in foreignAbilitySets)
                {
                    var r = graft.AddAbilitySet(
                        StageUasset(patchedContentRoot, customDprdPkg),
                        abilitySet);
                    result.Log.Add(
                        $"DPRD controller set: {r.Status} added=[{string.Join(",", r.Added)}] " +
                        $"skipped=[{string.Join(",", r.Skipped)}]{ErrSuffix(r.Error)}");
                }

                // Clone the donor ability set and add standard foreign gadget abilities.
                if (foreignAbilities.Count > 0 && !string.IsNullOrEmpty(donor.AbilitySetPackage))
                {
                    var customAsPkg = $"/Game/Mods/{mod}/Characters/AS_{mod}";
                    var asStem = UnrealPathUtil.AssetName(customAsPkg);
                    CloneDonorAsset(extractedRoot, donor.AbilitySetPackage, donor.AbilitySetStem, patchedContentRoot, customAsPkg, asStem, mappings, result);
                    var r = graft.AddGrantedAbilities(StageUasset(patchedContentRoot, customAsPkg), foreignAbilities);
                    result.Log.Add($"ability-set grant: {r.Status} added=[{string.Join(",", r.Added)}] skipped=[{string.Join(",", r.Skipped)}]{ErrSuffix(r.Error)}");

                    var asApplied = ApplyNameMapReplacements(StageUasset(patchedContentRoot, customDprdPkg), new Dictionary<string, string>
                    {
                        [donor.AbilitySetPackage] = customAsPkg,
                        [donor.AbilitySetStem] = asStem,
                    }, mappings);
                    result.Log.Add($"DPRD repoint → custom ability set: {asApplied} name(s)");
                }
                else if (foreignAbilities.Count > 0)
                {
                    result.Log.Add("ability-set grant skipped: donor DPRD has no character ability set to clone");
                }
            }

            result.Status = "ok";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.Error = ex.ToString();
            return result;
        }
    }

    // Generated-derivative filename prefixes: everything the archetype graft emits
    // into /Game/Mods/<mod>/Characters/. NOT prefixes of the hand-staged files that
    // must survive (BP_<Family>_<Suit>_Playable/_Cutscene, DA_DCMD_*, MI_*, T_*).
    private static readonly string[] GeneratedDerivativePrefixes =
    {
        "BP_CAT_Archetype_", "AS_", "MAS_", "LAS_", "DA_DPRD_", "ABP_", "BS_"
    };

    /// <summary>
    /// Deletes previously-generated archetype derivatives (ability sets, DPRD, anim
    /// composites, cloned locomotion graph, the custom archetype) from the packaged
    /// stage so a re-package emits exactly the CURRENT config with no orphans. Leaves
    /// the playable/cutscene/DCMD/materials in place - they are re-derived or reused.
    /// </summary>
    private static void PurgeGeneratedArchetypeDerivatives(string contentRoot, string customArchetypePackage, Result result)
    {
        // customArchetypePackage = /Game/Mods/<mod>/Characters/BP_CAT_Archetype_<mod>
        const string marker = "/Mods/";
        var idx = customArchetypePackage.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;
        var rest = customArchetypePackage[(idx + marker.Length)..];
        var mod = rest.Contains('/') ? rest[..rest.IndexOf('/')] : rest;
        if (string.IsNullOrWhiteSpace(mod)) return;

        var charsDir = Path.Combine(contentRoot, "Mods", mod, "Characters");
        if (!Directory.Exists(charsDir)) return;

        var purged = 0;
        foreach (var uasset in Directory.EnumerateFiles(charsDir, "*.uasset"))
        {
            var stem = Path.GetFileNameWithoutExtension(uasset);
            if (!GeneratedDerivativePrefixes.Any(p => stem.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
            {
                var f = Path.ChangeExtension(uasset, ext);
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
            }
            purged++;
        }
        if (purged > 0) result.Log.Add($"purged {purged} stale generated derivative(s) from Mods/{mod}/Characters before regenerating");
    }

    private static void CloneDonorAsset(string extractedRoot, string donorPackage, string donorStem,
        string patchedRoot, string targetPackage, string targetStem, Usmap? mappings, Result result)
    {
        if (string.IsNullOrWhiteSpace(donorPackage) || !donorPackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            result.Log.Add($"clone {donorStem}: invalid donor package '{donorPackage}' — skipped");
            return;
        }
        var donorBase = Path.Combine(extractedRoot, donorPackage["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(donorBase + ".uasset"))
        {
            result.Log.Add($"clone {donorStem}: donor not extracted ({donorBase}.uasset) — skipped");
            return;
        }

        var targetBase = PackageToBase(patchedRoot, targetPackage);
        Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
        File.Copy(donorBase + ".uasset", targetBase + ".uasset", overwrite: true);
        if (File.Exists(donorBase + ".uexp")) File.Copy(donorBase + ".uexp", targetBase + ".uexp", overwrite: true);

        var asset = new UAsset(targetBase + ".uasset", EngineVersion.VER_UE5_6, mappings, NameMapOnly);
        asset.FolderName = new FString(targetPackage);
        var replacements = new Dictionary<string, string>
        {
            [donorPackage] = targetPackage,
            [donorStem] = targetStem,
        };
        ApplyNameMapOnLoadedAsset(asset, replacements);
        asset.Write(targetBase + ".uasset");
        result.Log.Add($"cloned {donorStem} → {targetStem}");
    }

    /// <summary>
    /// Repoints name-map entries only when they EXACTLY equal a key (not substring).
    /// Required for AnimSequence refs - a substring "A_Idle_Batman" would also
    /// corrupt "A_Idle_Batman_HAT" etc.
    /// </summary>
    private static int ApplyNameMapExact(string uassetPath, Dictionary<string, string> exact, Usmap? mappings)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
        var nameMap = asset.GetNameMapIndexList();
        var changed = 0;
        for (var i = 0; i < nameMap.Count; i++)
        {
            if (exact.TryGetValue(nameMap[i].ToString(), out var rep))
            {
                asset.SetNameReference(i, new FString(rep));
                changed++;
            }
        }
        if (changed > 0)
        {
            asset.Write(uassetPath);
        }
        return changed;
    }

    private static int ApplyNameMapReplacements(string uassetPath, Dictionary<string, string> replacements, Usmap? mappings)
    {
        var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
        var count = ApplyNameMapOnLoadedAsset(asset, replacements);
        if (count > 0)
        {
            asset.Write(uassetPath);
        }
        return count;
    }

    private static int ApplyNameMapOnLoadedAsset(UAsset asset, Dictionary<string, string> replacements)
    {
        var ordered = replacements
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value) && p.Key != p.Value)
            .OrderByDescending(p => p.Key.Length)
            .ToList();

        var nameMap = asset.GetNameMapIndexList();
        var changed = 0;
        for (var i = 0; i < nameMap.Count; i++)
        {
            var original = nameMap[i].ToString();
            var patched = original;
            foreach (var pair in ordered)
            {
                patched = patched.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
            }
            if (patched != original)
            {
                asset.SetNameReference(i, new FString(patched));
                changed++;
            }
        }
        return changed;
    }

    /// <summary>The donor set replaced for a category, relative to the actual donor family (e.g. LAS_Default_ThomasWayne).</summary>
    private static string DonorSetForCategory(string category, string family)
    {
        var map = GameDataService.AnimCategoryMap.FirstOrDefault(m => m.Category == category);
        return map.Category is null ? "" : map.SetPrefix + family;
    }

    private static string ModOf(string playablePackage)
    {
        const string prefix = "/Game/Mods/";
        var pkg = UnrealPathUtil.NormalizePackagePath(playablePackage);
        if (!pkg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
        var rest = pkg[prefix.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : rest;
    }

    private static string PackageToBase(string contentRoot, string packagePath)
    {
        var pkg = UnrealPathUtil.NormalizePackagePath(packagePath);
        if (!pkg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            // Empty or non-/Game path (e.g. missing donor asset) - return a path
            // that won't exist so File.Exists checks fail gracefully instead of throwing.
            return Path.Combine(contentRoot, "__invalid__");
        }
        return Path.Combine(contentRoot, pkg["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static string StageUasset(string contentRoot, string packagePath) => PackageToBase(contentRoot, packagePath) + ".uasset";

    private static string ErrSuffix(string? error) => string.IsNullOrWhiteSpace(error) ? "" : " ERROR=" + error.Split('\n')[0];

    private static Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? MappingsCache.Load(configured) : null;
    }
}
