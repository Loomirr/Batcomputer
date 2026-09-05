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
        var playable = ResolveTemplateUasset(
            project.PlayableTemplate,
            AppSettings.Current.EffectiveExtractedContentRoot());
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
            var playable = ResolveTemplateUasset(
                project.PlayableTemplate,
                AppSettings.Current.EffectiveExtractedContentRoot());
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
            var refreshed = ExtractedPackagePathService.ResolvePackageUasset(
                extractedContentRoot,
                template.PackagePath);
            if (!string.IsNullOrWhiteSpace(refreshed) && File.Exists(refreshed))
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
        // the stable Unreal package path (base /Game or an installed DLC mount) against the
        // active extract before looking at the generated stage.
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
            var donorRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var donorUasset = ExtractedPackagePathService.ResolvePackageUasset(
                donorRoot,
                project.MachineryDonorPlayable) ?? "";
            var md = DetectDonor(donorUasset, donorRoot, mappings);
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
            var lasFile = ExtractedPackagePathService.ResolvePackageUasset(extracted, g.LasDefaultPackage) ?? "";
            if (!File.Exists(lasFile)) return g;

            List<string> Imports(string pkg)
            {
                var f = ExtractedPackagePathService.ResolvePackageUasset(extracted, pkg) ?? "";
                if (!File.Exists(f)) return new List<string>();
                var a = new UAsset(f, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
                return a.Imports.Select(i => i.ObjectName.ToString())
                    .Where(n => IsExtractedPackagePath(extracted, n)).Distinct().ToList();
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

            var archUasset = ExtractedPackagePathService.ResolvePackageUasset(contentRoot, archPkg) ?? "";
            if (!File.Exists(archUasset))
            {
                // Archetype lives in the base game, not our stage - read from extracted content.
                archUasset = ExtractedPackagePathService.ResolvePackageUasset(
                    AppSettings.Current.EffectiveExtractedContentRoot(),
                    archPkg) ?? "";
            }
            if (File.Exists(archUasset))
            {
                var arch = new UAsset(archUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
                foreach (var n in arch.Imports.Select(i => i.ObjectName.ToString()))
                {
                    if (IsExtractedPackagePath(AppSettings.Current.EffectiveExtractedContentRoot(), n))
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

                var dprdUasset = ExtractedPackagePathService.ResolvePackageUasset(
                    AppSettings.Current.EffectiveExtractedContentRoot(),
                    info.DprdPackage) ?? "";
                if (!string.IsNullOrEmpty(info.DprdPackage) && File.Exists(dprdUasset))
                {
                    var dprd = new UAsset(dprdUasset, EngineVersion.VER_UE5_6, mappings, NameMapOnly);
                    info.AbilitySetPackage = dprd.Imports
                        .Select(i => i.ObjectName.ToString())
                        .FirstOrDefault(n => IsExtractedPackagePath(AppSettings.Current.EffectiveExtractedContentRoot(), n) &&
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
        var segments = package.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 ||
            !segments[1].Equals("Characters", StringComparison.OrdinalIgnoreCase) ||
            (!segments[2].Equals("Minifig", StringComparison.OrdinalIgnoreCase) &&
             !segments[2].Equals("Smallfig", StringComparison.OrdinalIgnoreCase)))
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
            var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var names = a.Imports.Select(i => i.ObjectName.ToString())
                .Where(n => IsExtractedPackagePath(extractedRoot, n))
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
            var extractedRoot = AppSettings.Current.EffectiveExtractedContentRoot();
            var mis = a.Imports.Select(i => i.ObjectName.ToString())
                .Where(n => IsExtractedPackagePath(extractedRoot, n) &&
                            UnrealPathUtil.AssetName(n).StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            var face = mis.FirstOrDefault(n => UnrealPathUtil.AssetName(n).StartsWith("MI_FACE_", StringComparison.OrdinalIgnoreCase)) ?? "";
            // Body: the character's own material (…/<rig>/<Char>/Materials/MI_<Char>…), not
            // face/hair/cape attachment materials.
            var body = mis.FirstOrDefault(n =>
                           IsCharacterOwnedMaterialPackage(n, characterFolder) &&
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

    internal static bool IsCharacterOwnedMaterialPackage(string packagePath, string characterFolder) =>
        new[] { "Minifig", "Smallfig" }.Any(rig =>
            packagePath.Contains(
                $"/Characters/{rig}/{characterFolder}/",
                StringComparison.OrdinalIgnoreCase));

    private static string FindCharacterBodyMaterialOnDisk(string characterBlueprint, string characterFolder)
    {
        try
        {
            var characterRoot = Path.GetDirectoryName(characterBlueprint);
            if (string.IsNullOrWhiteSpace(characterRoot) || !Directory.Exists(characterRoot))
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

            return ExtractedPackagePathService.PackagePathFromFile(
                       AppSettings.Current.EffectiveExtractedContentRoot(),
                       selected.Path)
                   ?? "";
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
        if (!RequiresCustomArchetype(project))
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
            var hasAuthoredCapeShell = GliderService.TryGetAuthoredPairedCapeShell(
                project,
                out var authoredShellPlayable,
                out _,
                out var authoredShellDetail);
            if (project.PairedCapeAdapter is not null && !hasAuthoredCapeShell)
            {
                result.Status = "error";
                result.Error =
                    "The declared paired-cape adapter failed certification before its authored shell could be packaged: " +
                    authoredShellDetail;
                return result;
            }

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
            PurgeGeneratedArchetypeDerivatives(
                contentRoot,
                custom,
                result);

            DonorInfo? authoredShellDonor = null;
            if (hasAuthoredCapeShell)
            {
                var authoredShellUasset = ExtractedPackagePathService.ResolvePackageUasset(
                    extractedRoot,
                    authoredShellPlayable) ?? "";
                authoredShellDonor = DetectDonor(authoredShellUasset, extractedRoot, mappings);
                if (authoredShellDonor is null || !authoredShellDonor.Valid)
                {
                    result.Status = "error";
                    result.Error = "Could not detect the authored paired-cape shell's parent archetype.";
                    return result;
                }

                // Always restore a pristine authored-shell clone. A prior package attempt may
                // have repointed this mod-local archetype to generated MAS/LAS/DPRD derivatives;
                // those derivatives are purged above. Reusing that stale archetype would leave
                // dangling imports after the user removes an equipment or animation override.
                CloneDonorAsset(
                    extractedRoot,
                    authoredShellDonor.ArchetypePackage,
                    authoredShellDonor.ArchetypeStem,
                    contentRoot,
                    custom,
                    customStem,
                    mappings,
                    result);

                // Keep the shell's exact superclass schema (and therefore its cooked child-CDO
                // contract), but bridge its behavior references to the selected gameplay donor.
                // Replacing the whole superclass with Nightwing is unsafe because the two cooked
                // generated classes own different inherited fields and SCS layouts.
                var behaviorRepoint = new Dictionary<string, string>();
                AddPackageAndStemReplacement(
                    behaviorRepoint,
                    authoredShellDonor.MasCharPackage,
                    donor.MasCharPackage);
                AddPackageAndStemReplacement(
                    behaviorRepoint,
                    authoredShellDonor.LasCharPackage,
                    donor.LasCharPackage);
                AddPackageAndStemReplacement(
                    behaviorRepoint,
                    authoredShellDonor.DprdPackage,
                    donor.DprdPackage);
                if (behaviorRepoint.Count == 0)
                {
                    result.Status = "error";
                    result.Error = "The authored shell or gameplay donor did not expose the MAS/LAS/DPRD references required for the gameplay bridge.";
                    return result;
                }
                var behaviorChanges = ApplyNameMapReplacements(
                    StageUasset(contentRoot, custom),
                    behaviorRepoint,
                    mappings);
                result.Log.Add(
                    $"paired-cape authored shell: kept {authoredShellDonor.ArchetypeStem} schema and repointed {behaviorChanges} MAS/LAS/DPRD name(s) to {donor.Family}");
            }
            else
            {
                // Normal custom-archetype suits clone the selected gameplay donor wholesale.
                CloneDonorAsset(extractedRoot, donor.ArchetypePackage, donor.ArchetypeStem,
                    contentRoot, custom, customStem, mappings, result);
            }

            // 2) Reparent the playable + cutscene in the packaged root (name-map only,
            //    so it composes cleanly with any part grafts already applied).
            var reparentDonor = authoredShellDonor ?? donor;
            var reparent = new Dictionary<string, string>
            {
                [reparentDonor.ArchetypePackage] = custom,
                ["Default__" + reparentDonor.ArchetypeStem + "_C"] = "Default__" + customStem + "_C",
                [reparentDonor.ArchetypeStem + "_C"] = customStem + "_C",
                [reparentDonor.ArchetypeStem] = customStem,
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
        if (!RequiresCustomArchetype(project))
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
        var foreignEffects = new List<string>();
        var foreignAbilitySets = new List<string>();
        var exactDonorEquipmentKnown = AbilityDependencyService.TryReadDonorRuntimeEquipmentSlots(
            project,
            gd.Db.Equipment,
            out var donorEquipmentSlots);
        foreach (var change in project.EquipmentSlots)
        {
            var eq = gd.FindEquipment(change.Gadget);
            if (eq is null)
            {
                continue;
            }
            var nativeAtSlot = exactDonorEquipmentKnown &&
                               donorEquipmentSlots.TryGetValue(change.Slot, out var donorItem) &&
                               donorItem.Equals(eq.Name, StringComparison.OrdinalIgnoreCase);
            if (!nativeAtSlot && !string.IsNullOrWhiteSpace(eq.EdPackage))
            {
                foreignEd.Add((change.Slot, eq.EdPackage));
            }
            var controllerSets = EquipmentDependencyService.Analyze(eq, donorFamily).AbilitySets;
            if (controllerSets.Count > 0)
            {
                result.Log.Add(
                    $"equipment dependency [{eq.Name}]: its ED owns AbilitySetsToGrant " +
                    string.Join(", ", controllerSets.Select(UnrealPathUtil.AssetName)) +
                    "; they are not duplicated in the character DPRD");
            }
        }

        // Ability, equipment, held-item, combat-effect, and animation dependencies are one
        // transaction. A melee AbilitySet by itself is not a complete fighting-style swap.
        var dependencyPlan = AbilityDependencyService.Build(project, donorFamily, gd.Db.Equipment);
        var dependencyErrors = dependencyPlan.Issues
            .Where(issue => issue.Severity == AbilityDependencySeverity.Error)
            .Select(issue => issue.Message)
            .ToList();
        if (dependencyErrors.Count > 0)
        {
            result.Status = "error";
            result.Error = "Ability/equipment dependency validation failed:\n- " +
                           string.Join("\n- ", dependencyErrors);
            return result;
        }
        foreach (var issue in dependencyPlan.Issues.Where(issue =>
                     issue.Severity != AbilityDependencySeverity.Error))
        {
            result.Log.Add($"ability dependency {issue.Severity.ToString().ToLowerInvariant()}: {issue.Message}");
        }
        foreach (var package in dependencyPlan.RequiredAbilitySets)
        {
            if (!foreignAbilitySets.Contains(package, StringComparer.OrdinalIgnoreCase)) foreignAbilitySets.Add(package);
        }
        foreach (var package in dependencyPlan.RequiredMontageAnimSets)
        {
            if (!foreignMas.Contains(package, StringComparer.OrdinalIgnoreCase)) foreignMas.Add(package);
        }
        foreach (var package in dependencyPlan.RequiredLayerAnimSets)
        {
            if (!foreignLas.Contains(package, StringComparer.OrdinalIgnoreCase)) foreignLas.Add(package);
        }
        foreach (var package in dependencyPlan.GameplayAbilitiesToBridge)
        {
            if (!foreignAbilities.Contains(package, StringComparer.OrdinalIgnoreCase)) foreignAbilities.Add(package);
        }
        if (dependencyPlan.FightingStyle is { } fightingStyle)
        {
            if (!string.IsNullOrWhiteSpace(fightingStyle.CombatTypeEffectPackage) &&
                !foreignEffects.Contains(fightingStyle.CombatTypeEffectPackage, StringComparer.OrdinalIgnoreCase))
            {
                foreignEffects.Add(fightingStyle.CombatTypeEffectPackage);
            }
            result.Log.Add($"fighting-style bundle: {fightingStyle.DisplayName}");
        }

        var usesPairedCapeAdapter = GliderService.IsDeclaredPairedCapeAdapterValid(
            project,
            BaseCapeGlideContract(project),
            requireResolvedComponents: true,
            out _);
        if (EnsureGliderAbilitySetDependency(
                project,
                usesPairedCapeAdapter,
                foreignAbilitySets))
        {
            result.Log.Add("glider dependency: adding native AS_Gliding ability set");
        }
        else if (usesPairedCapeAdapter)
        {
            result.Log.Add(
                "paired-cape adapter: preserving the gameplay donor's native gliding ability/loadout while replacing its glide-only animation categories with the certified cape donor's blocks");
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
                var gliderLas = ExtractedPackagePathService.ResolvePackageUasset(
                    gliderExtractedRoot,
                    project.GliderAnimLas);
                if (!string.IsNullOrWhiteSpace(gliderLas) && File.Exists(gliderLas))
                {
                    if (!usesPairedCapeAdapter)
                    {
                        foreignLas.Add(project.GliderAnimLas);
                    }
                }
                else
                {
                    if (usesPairedCapeAdapter)
                    {
                        result.Status = "error";
                        result.Error =
                            "The certified paired-cape LAS dependency is missing from the active extract: " +
                            project.GliderAnimLas;
                        return result;
                    }
                    result.Log.Add($"glider LAS not found on disk, skipped (glide pose won't change): {project.GliderAnimLas}");
                }
            }
            if (!string.IsNullOrWhiteSpace(project.GliderAnimMas) && !foreignMas.Contains(project.GliderAnimMas))
            {
                var gliderMas = ExtractedPackagePathService.ResolvePackageUasset(
                    gliderExtractedRoot,
                    project.GliderAnimMas);
                if (!string.IsNullOrWhiteSpace(gliderMas) && File.Exists(gliderMas))
                {
                    if (!usesPairedCapeAdapter)
                    {
                        foreignMas.Add(project.GliderAnimMas);
                    }
                }
                else
                {
                    if (usesPairedCapeAdapter)
                    {
                        result.Status = "error";
                        result.Error =
                            "The certified paired-cape MAS dependency is missing from the active extract: " +
                            project.GliderAnimMas;
                        return result;
                    }
                    result.Log.Add($"glider MAS not found on disk, skipped: {project.GliderAnimMas}");
                }
            }
        }

        var exactSlotOverrides = project.AnimationSlotOverrides ?? [];
        var hasAbilityCustomization = AbilityLoadoutService.HasCustomizations(project);
        if (foreignMas.Count == 0 && foreignLas.Count == 0 && foreignEd.Count == 0 &&
            foreignAbilitySets.Count == 0 &&
            foreignEffects.Count == 0 &&
            dependencyPlan.GameplayAbilitiesToRemove.Count == 0 &&
            dependencyPlan.RequiredLayerSlices.Count == 0 &&
            project.AnimationOverrides.Count == 0 && project.LocomotionOverrides.Count == 0 &&
            exactSlotOverrides.Count == 0 &&
            !hasAbilityCustomization &&
            !usesPairedCapeAdapter)
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
            var montageSlotOverrides = exactSlotOverrides
                .Where(o => o.Kind.Equals("Montage", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var layerSlotOverrides = exactSlotOverrides
                .Where(o => o.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // A locomotion edit builds one suit-owned LAS_Default graph. If another edit has
            // already replaced or cloned that same parent, the later legacy locomotion pass would
            // no longer find LAS_Default_<donor> and ReplaceParentSet would append a second default
            // controller. Two competing defaults are order-dependent and can crash. Fail before
            // cloning either composite until these edits can be merged into one graph.
            if (project.LocomotionOverrides.Count > 0)
            {
                var compositionConflict = LocomotionCompositionConflict(
                    donor.Family,
                    layerOverrides,
                    layerSlotOverrides);
                if (!string.IsNullOrWhiteSpace(compositionConflict))
                {
                    result.Status = "error";
                    result.Error = compositionConflict;
                    return result;
                }
            }

            // --- Animations: clone MAS/LAS, inject foreign blocks + apply overrides, repoint. ---
            var needMas = foreignMas.Count > 0 ||
                          dependencyPlan.MontageAnimSetsToRemove.Count > 0 ||
                          dependencyPlan.AnimationReplacements.Any(replacement =>
                              replacement.Kind.StartsWith("Montage", StringComparison.OrdinalIgnoreCase)) ||
                          montageOverrides.Count > 0 ||
                          montageSlotOverrides.Count > 0 || usesPairedCapeAdapter;
            var needLas = foreignLas.Count > 0 ||
                          dependencyPlan.LayerAnimSetsToRemove.Count > 0 ||
                          dependencyPlan.RequiredLayerSlices.Count > 0 ||
                          dependencyPlan.AnimationReplacements.Any(replacement =>
                              replacement.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase)) ||
                          layerOverrides.Count > 0 ||
                          layerSlotOverrides.Count > 0 || project.LocomotionOverrides.Count > 0 ||
                          usesPairedCapeAdapter;
            if (needMas || needLas)
            {
                if (needMas) CloneDonorAsset(extractedRoot, donor.MasCharPackage, donor.MasCharStem, patchedContentRoot, customMasPkg, masStem, mappings, result);
                if (needLas) CloneDonorAsset(extractedRoot, donor.LasCharPackage, donor.LasCharStem, patchedContentRoot, customLasPkg, lasStem, mappings, result);

                // A combat style may need a few context-gated held-item rows from another
                // character's LAS_Default. Never replace the donor's complete default layer:
                // clone the source into this suit, retain only the certified contexts, and add
                // that narrow layer beside the donor's unchanged locomotion controller.
                foreach (var slice in dependencyPlan.RequiredLayerSlices)
                {
                    var customSlicePackage = FightingStyleLayerSlicePackage(mod, slice);
                    var sourceStem = UnrealPathUtil.AssetName(slice.SourcePackage);
                    var customStem = UnrealPathUtil.AssetName(customSlicePackage);
                    CloneDonorAsset(
                        extractedRoot,
                        slice.SourcePackage,
                        sourceStem,
                        patchedContentRoot,
                        customSlicePackage,
                        customStem,
                        mappings,
                        result);
                    var customSliceUasset = StageUasset(patchedContentRoot, customSlicePackage);
                    var filtered = graft.KeepOnlyLayerEntriesMatchingContexts(
                        customSliceUasset,
                        slice.RequiredContextTags,
                        slice.AdditionalContextTags);
                    result.Log.Add(
                        $"fighting-style LAS context slice: {filtered.Status} {sourceStem} " +
                        $"contexts=[{string.Join(",", slice.RequiredContextTags)}] rows={filtered.Added.Count}{ErrSuffix(filtered.Error)}");
                    if (!filtered.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error =
                            "The fighting-style held-item animation layer could not be isolated without replacing donor locomotion: " +
                            (filtered.Error ?? filtered.Status);
                        return result;
                    }
                    if (!foreignLas.Contains(customSlicePackage, StringComparer.OrdinalIgnoreCase))
                    {
                        foreignLas.Add(customSlicePackage);
                    }
                }

                if (dependencyPlan.MontageAnimSetsToRemove.Count > 0)
                {
                    var r = graft.RemoveParentSets(
                        StageUasset(patchedContentRoot, customMasPkg),
                        dependencyPlan.MontageAnimSetsToRemove);
                    result.Log.Add($"MAS displaced-equipment cleanup: {r.Status} removed=[{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error = "Displaced equipment MAS dependencies could not be removed: " +
                                       (r.Error ?? r.Status);
                        return result;
                    }
                }
                foreach (var removal in dependencyPlan.AnimationReplacements.Where(replacement =>
                             replacement.Kind.Equals("MontageRemove", StringComparison.OrdinalIgnoreCase)))
                {
                    var r = graft.RemoveParentSetsByPrefix(
                        StageUasset(patchedContentRoot, customMasPkg),
                        removal.DonorSetPrefix);
                    result.Log.Add(
                        $"fighting-style MAS cleanup: {r.Status} removed=[{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error = "The prior fighting-style combat animation block could not be removed: " +
                                       (r.Error ?? r.Status);
                        return result;
                    }
                }
                foreach (var replacement in dependencyPlan.AnimationReplacements.Where(replacement =>
                             replacement.Kind.Equals("Montage", StringComparison.OrdinalIgnoreCase)))
                {
                    var r = graft.SetExclusiveParentSet(
                        StageUasset(patchedContentRoot, customMasPkg),
                        "TTAnimSet",
                        replacement.DonorSetPrefix,
                        replacement.ReplacementPackage,
                        requireExisting: false);
                    result.Log.Add(
                        $"fighting-style MAS replacement: {r.Status} {replacement.DonorSetPrefix}*→{UnrealPathUtil.AssetName(replacement.ReplacementPackage)}{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error = "The fighting-style combat animation block could not be installed: " +
                                       (r.Error ?? r.Status);
                        return result;
                    }
                }
                if (foreignMas.Count > 0)
                {
                    var r = graft.InjectParentSets(StageUasset(patchedContentRoot, customMasPkg), "TTAnimSet", foreignMas);
                    result.Log.Add($"MAS graft: {r.Status} added=[{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                    if ((usesPairedCapeAdapter || dependencyPlan.RequiredMontageAnimSets.Count > 0) &&
                        !r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error =
                            "A required equipment/style MAS block could not be injected: " +
                            (r.Error ?? r.Status);
                        return result;
                    }
                }
                if (dependencyPlan.LayerAnimSetsToRemove.Count > 0)
                {
                    var r = graft.RemoveParentSets(
                        StageUasset(patchedContentRoot, customLasPkg),
                        dependencyPlan.LayerAnimSetsToRemove);
                    result.Log.Add($"LAS displaced-equipment cleanup: {r.Status} removed=[{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error = "Displaced equipment LAS dependencies could not be removed: " +
                                       (r.Error ?? r.Status);
                        return result;
                    }
                }
                if (usesPairedCapeAdapter)
                {
                    var donorSet = $"MAS_Glide_{donor.Family}";
                    var r = graft.ReplaceParentSet(
                        StageUasset(patchedContentRoot, customMasPkg),
                        "TTAnimSet",
                        donorSet,
                        project.PairedCapeAdapter!.GlideAnimMasPackage,
                        requireExisting: true);
                    result.Log.Add($"paired-cape MAS category replacement: {r.Status} {donorSet}→{UnrealPathUtil.AssetName(project.PairedCapeAdapter.GlideAnimMasPackage)}{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error =
                            "The certified paired-cape MAS glide category could not replace the gameplay donor's native category: " +
                            (r.Error ?? r.Status);
                        return result;
                    }
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
                    if ((usesPairedCapeAdapter || dependencyPlan.RequiredLayerAnimSets.Count > 0) &&
                        !r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error =
                            "A required equipment/style LAS block could not be injected: " +
                            (r.Error ?? r.Status);
                        return result;
                    }
                }
                foreach (var replacement in dependencyPlan.AnimationReplacements.Where(replacement =>
                             replacement.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase)))
                {
                    var r = graft.SetExclusiveParentSet(
                        StageUasset(patchedContentRoot, customLasPkg),
                        "TTLayerSet",
                        replacement.DonorSetPrefix,
                        replacement.ReplacementPackage,
                        requireExisting: true);
                    result.Log.Add(
                        $"fighting-style LAS replacement: {r.Status} {replacement.DonorSetPrefix}*→{UnrealPathUtil.AssetName(replacement.ReplacementPackage)}{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error =
                            "The fighting-style default animation layer could not replace the donor layer: " +
                            (r.Error ?? r.Status);
                        return result;
                    }
                }
                if (usesPairedCapeAdapter)
                {
                    var donorSet = $"LAS_Traversal_{donor.Family}";
                    var r = graft.ReplaceParentSet(
                        StageUasset(patchedContentRoot, customLasPkg),
                        "TTLayerSet",
                        donorSet,
                        project.PairedCapeAdapter!.GlideAnimLasPackage,
                        requireExisting: true);
                    result.Log.Add($"paired-cape LAS category replacement: {r.Status} {donorSet}→{UnrealPathUtil.AssetName(project.PairedCapeAdapter.GlideAnimLasPackage)}{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error =
                            "The certified paired-cape LAS traversal category could not replace the gameplay donor's native category: " +
                            (r.Error ?? r.Status);
                        return result;
                    }
                }
                foreach (var o in layerOverrides)
                {
                    var donorSet = DonorSetForCategory(o.Category, donor.Family);
                    var r = graft.ReplaceParentSet(StageUasset(patchedContentRoot, customLasPkg), "TTLayerSet", donorSet, o.ReplacementPackage);
                    result.Log.Add($"LAS override [{o.Category}]: {r.Status} {donorSet}→{o.ReplacementSet}{ErrSuffix(r.Error)}");
                }

                // Exact action/layer overrides: clone only the affected parent set, patch its
                // semantic slot, then replace that one parent in the suit-owned character
                // composite. Context-specific rows remain independent even when they reuse the
                // same donor montage.
                if (!ApplyExactSlotOverrides(
                        montageSlotOverrides,
                        "TTAnimSet",
                        customMasPkg,
                        extractedRoot,
                        patchedContentRoot,
                        mod,
                        mappings,
                        graft,
                        result) ||
                    !ApplyExactSlotOverrides(
                        layerSlotOverrides,
                        "TTLayerSet",
                        customLasPkg,
                        extractedRoot,
                        patchedContentRoot,
                        mod,
                        mappings,
                        graft,
                        result))
                {
                    return result;
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

                    var rl = graft.ReplaceParentSet(
                        StageUasset(patchedContentRoot, customLasPkg),
                        "TTLayerSet",
                        UnrealPathUtil.AssetName(g.LasDefaultPackage),
                        custom[g.LasDefaultPackage],
                        requireExisting: true);
                    result.Log.Add($"LAS_Char → custom LAS_Default: {rl.Status} {string.Join(",", rl.Added)}{ErrSuffix(rl.Error)}");
                    if (!rl.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error = rl.Error ??
                                       $"The character layer composite no longer contains '{UnrealPathUtil.AssetName(g.LasDefaultPackage)}'.";
                        return result;
                    }
                }

                var repoint = new Dictionary<string, string>();
                if (needMas) { repoint[donor.MasCharPackage] = customMasPkg; repoint[donor.MasCharStem] = masStem; }
                if (needLas) { repoint[donor.LasCharPackage] = customLasPkg; repoint[donor.LasCharStem] = lasStem; }
                var applied = ApplyNameMapReplacements(archetypeUasset, repoint, mappings);
                result.Log.Add($"archetype repoint → custom anim sets: {applied} name(s)");
            }

            // --- Loadout: clone DPRD, swap the gadget's ED into Equipment, repoint archetype. ---
            if (RequiresGeneratedDprdFromResolvedDependencies(
                    foreignEd.Count > 0,
                    foreignAbilitySets.Count > 0 || foreignEffects.Count > 0 || hasAbilityCustomization))
            {
                var customDprdPkg = $"/Game/Mods/{mod}/Characters/DA_DPRD_{mod}";
                var dprdStem = UnrealPathUtil.AssetName(customDprdPkg);
                CloneDonorAsset(extractedRoot, donor.DprdPackage, donor.DprdStem, patchedContentRoot, customDprdPkg, dprdStem, mappings, result);
                var customDprdUasset = StageUasset(patchedContentRoot, customDprdPkg);
                var equipmentMutation = new AbilityAssetMutationService();
                var equipmentBefore = equipmentMutation.InspectDprdEquipment(customDprdUasset);
                if (!equipmentBefore.Success)
                {
                    result.Status = "error";
                    result.Error = equipmentBefore.Error ?? "The cloned donor DPRD Equipment array could not be inspected.";
                    return result;
                }
                var expectedEquipment = equipmentBefore.Equipment
                    .OrderBy(reference => reference.Index)
                    .Select(reference => reference.IsNull
                        ? ""
                        : UnrealPathUtil.NormalizePackagePath(reference.PackagePath))
                    .ToList();
                foreach (var (slot, edPkg) in foreignEd)
                {
                    // NOTE: the boss/NPC "equipment adapter" (clone ED + graft GetGadgetOutAbility via
                    // graft.SetGadgetDrawScaffolding / EquipmentNeedsDrawAdapter) is PARKED - the draw
                    // ability alone did not make FreezeGun usable in-game, so boss equipment needs more
                    // than the ED scaffolding (deferred research). Those helpers remain for when we
                    // resume; for now slot the gadget's base ED directly (proven path for hero gadgets).
                    var r = graft.SetEquipmentSlot(customDprdUasset, slot, edPkg);
                    result.Log.Add($"DPRD equipment slot {slot + 1}: {r.Status} [{string.Join(",", r.Added)}]{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error = $"Runtime equipment slot {slot + 1} could not be written exactly: " +
                                       (r.Error ?? r.Status);
                        return result;
                    }
                    if (slot < 0 || slot >= expectedEquipment.Count)
                    {
                        result.Status = "error";
                        result.Error = $"Runtime equipment slot {slot + 1} is outside the donor DPRD's serialized Equipment array.";
                        return result;
                    }
                    expectedEquipment[slot] = UnrealPathUtil.NormalizePackagePath(edPkg);
                }
                var equipmentAfter = equipmentMutation.InspectDprdEquipment(customDprdUasset);
                var actualEquipment = equipmentAfter.Equipment
                    .OrderBy(reference => reference.Index)
                    .Select(reference => reference.IsNull
                        ? ""
                        : UnrealPathUtil.NormalizePackagePath(reference.PackagePath))
                    .ToList();
                if (!equipmentAfter.Success ||
                    !actualEquipment.SequenceEqual(expectedEquipment, StringComparer.OrdinalIgnoreCase))
                {
                    result.Status = "error";
                    result.Error = equipmentAfter.Error ??
                                   "The staged DPRD did not reload with the exact ordered runtime Equipment slot sequence.";
                    return result;
                }
                var applied = ApplyNameMapReplacements(archetypeUasset, new Dictionary<string, string>
                {
                    [donor.DprdPackage] = customDprdPkg,
                    [donor.DprdStem] = dprdStem,
                }, mappings);
                result.Log.Add($"archetype repoint → custom DPRD: {applied} name(s)");

                var customizedSetPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (hasAbilityCustomization && project.AbilityLoadout is { } abilityProfile)
                {
                    var protectedCoreEdit = abilityProfile.AllowUnsafeCoreEdits
                        ? null
                        : abilityProfile.AbilitySets.FirstOrDefault(selection =>
                            AbilityLoadoutService.IsProtectedCoreSet(selection.PackagePath) &&
                            (selection.AddedGameplayAbilities.Count > 0 ||
                             selection.RemovedGameplayAbilities.Count > 0));
                    if (protectedCoreEdit is not null)
                    {
                        result.Status = "error";
                        result.Error =
                            $"{UnrealPathUtil.AssetName(protectedCoreEdit.PackagePath)} is a protected core AbilitySet. " +
                            "Open Abilities and explicitly unlock unsafe core edits before changing its grants.";
                        return result;
                    }

                    var abilityMutation = new AbilityAssetMutationService();
                    var inspection = abilityMutation.InspectDprdAbilitySets(customDprdUasset);
                    if (!inspection.Success)
                    {
                        result.Status = "error";
                        result.Error = inspection.Error ?? "The cloned DPRD AbilitySets array could not be inspected.";
                        return result;
                    }

                    var donorAbilitySets = inspection.AbilitySets.Select(reference => reference.PackagePath).ToList();
                    if (!AbilityLoadoutService.DonorMatches(
                            abilityProfile,
                            donor.DprdPackage,
                            donorAbilitySets))
                    {
                        result.Status = "error";
                        result.Error =
                            "This saved ability loadout belongs to a different gameplay donor or donor revision. " +
                            "Open Abilities, reset/remap the loadout, then build again.";
                        return result;
                    }

                    var resolvedAbilitySets = AbilityLoadoutService.Resolve(
                        donorAbilitySets,
                        abilityProfile,
                        foreignAbilitySets).ToList();
                    var enabledSelections = abilityProfile.AbilitySets
                        .Where(selection => selection.Enabled)
                        .OrderBy(selection => selection.Order)
                        .ToList();
                    var editedIndex = 0;
                    foreach (var selection in enabledSelections)
                    {
                        if (selection.AddedGameplayAbilities.Count == 0 &&
                            selection.RemovedGameplayAbilities.Count == 0)
                        {
                            continue;
                        }

                        var sourcePackage = UnrealPathUtil.NormalizePackagePath(selection.PackagePath);
                        var sourceUasset = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, sourcePackage) ?? "";
                        if (!File.Exists(sourceUasset))
                        {
                            result.Status = "error";
                            result.Error = $"The AbilitySet selected for suit-local editing is missing from the active extraction: {sourcePackage}";
                            return result;
                        }

                        var customSetPackage = $"/Game/Mods/{mod}/Characters/AS_{mod}_User_{editedIndex++:00}";
                        CloneDonorAsset(
                            extractedRoot,
                            sourcePackage,
                            UnrealPathUtil.AssetName(sourcePackage),
                            patchedContentRoot,
                            customSetPackage,
                            UnrealPathUtil.AssetName(customSetPackage),
                            mappings,
                            result);
                        var customSetUasset = StageUasset(patchedContentRoot, customSetPackage);
                        var grantEdits = selection.RemovedGameplayAbilities
                            .Where(package => !string.IsNullOrWhiteSpace(package))
                            .Select(package => new AbilityAssetMutationService.GameplayAbilityEdit
                            {
                                Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Remove,
                                TargetPackagePath = package,
                            })
                            .Concat(selection.AddedGameplayAbilities
                                .Where(grant => !string.IsNullOrWhiteSpace(grant.PackagePath))
                                .Select(grant => new AbilityAssetMutationService.GameplayAbilityEdit
                                {
                                    Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Add,
                                    TargetPackagePath = grant.PackagePath,
                                    AbilityLevelOverride = grant.AbilityLevel,
                                    InputTagOverride = grant.InputTag,
                                }))
                            .ToList();
                        PopulateFirstGrantTemplateIfNeeded(grantEdits, customSetUasset, extractedRoot, abilityMutation);
                        var editResult = abilityMutation.ApplyGameplayAbilityEdits(customSetUasset, grantEdits);
                        if (!editResult.Success)
                        {
                            result.Status = "error";
                            result.Error = editResult.Error ?? $"Could not edit {sourcePackage}.";
                            return result;
                        }
                        result.Log.Add(
                            $"AbilitySet clone {UnrealPathUtil.AssetName(sourcePackage)} → {UnrealPathUtil.AssetName(customSetPackage)}: " +
                            string.Join(", ", editResult.Changes));
                        customizedSetPackages[sourcePackage] = customSetPackage;
                        for (var index = 0; index < resolvedAbilitySets.Count; index++)
                        {
                            if (resolvedAbilitySets[index].Equals(sourcePackage, StringComparison.OrdinalIgnoreCase))
                            {
                                resolvedAbilitySets[index] = customSetPackage;
                            }
                        }
                    }

                    var setResult = abilityMutation.SetDprdAbilitySets(customDprdUasset, resolvedAbilitySets);
                    if (!setResult.Success)
                    {
                        result.Status = "error";
                        result.Error = setResult.Error ?? "The suit-local DPRD AbilitySets list could not be written.";
                        return result;
                    }
                    result.Log.Add($"DPRD ability loadout: {resolvedAbilitySets.Count} ordered set(s), {customizedSetPackages.Count} suit-local clone(s)");
                }

                foreach (var abilitySet in hasAbilityCustomization
                             ? Enumerable.Empty<string>()
                             : foreignAbilitySets)
                {
                    var r = graft.AddAbilitySet(
                        customDprdUasset,
                        abilitySet);
                    result.Log.Add(
                        $"DPRD controller set: {r.Status} added=[{string.Join(",", r.Added)}] " +
                        $"skipped=[{string.Join(",", r.Skipped)}]{ErrSuffix(r.Error)}");
                    if (!r.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = "error";
                        result.Error = "A required DPRD AbilitySet could not be installed: " +
                                       (r.Error ?? r.Status);
                        return result;
                    }
                }

                // Clone the donor ability set and add standard foreign gadget abilities.
                if ((foreignAbilities.Count > 0 || foreignEffects.Count > 0 || dependencyPlan.FightingStyle is not null ||
                     dependencyPlan.GameplayAbilitiesToRemove.Count > 0) &&
                    !string.IsNullOrEmpty(donor.AbilitySetPackage))
                {
                    string customAsPkg;
                    string customAsUasset;
                    if (customizedSetPackages.TryGetValue(donor.AbilitySetPackage, out var existingCustomSet))
                    {
                        customAsPkg = existingCustomSet;
                        customAsUasset = StageUasset(patchedContentRoot, customAsPkg);
                    }
                    else
                    {
                        customAsPkg = $"/Game/Mods/{mod}/Characters/AS_{mod}";
                        var asStem = UnrealPathUtil.AssetName(customAsPkg);
                        CloneDonorAsset(extractedRoot, donor.AbilitySetPackage, donor.AbilitySetStem, patchedContentRoot, customAsPkg, asStem, mappings, result);
                        customAsUasset = StageUasset(patchedContentRoot, customAsPkg);
                        var asApplied = ApplyNameMapReplacements(customDprdUasset, new Dictionary<string, string>
                        {
                            [donor.AbilitySetPackage] = customAsPkg,
                            [donor.AbilitySetStem] = asStem,
                        }, mappings);
                        if (asApplied == 0)
                        {
                            result.Status = "error";
                            result.Error =
                                "The donor character AbilitySet is not active in the resolved DPRD, so Batcomputer cannot replace it with the required suit-local equipment/fighting-style bridge.";
                            return result;
                        }
                        result.Log.Add($"DPRD repoint → custom ability set: {asApplied} name(s)");
                    }

                    var abilityMutation = new AbilityAssetMutationService();
                    var equipmentEdits = dependencyPlan.GameplayAbilitiesToRemove.Select(package =>
                        new AbilityAssetMutationService.GameplayAbilityEdit
                        {
                            Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Remove,
                            TargetPackagePath = package,
                        }).Concat(foreignAbilities.Select(package =>
                        new AbilityAssetMutationService.GameplayAbilityEdit
                        {
                            Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Add,
                            TargetPackagePath = package,
                        })).ToList();
                    PopulateFirstGrantTemplateIfNeeded(equipmentEdits, customAsUasset, extractedRoot, abilityMutation);
                    var grantResult = abilityMutation.ApplyGameplayAbilityEdits(customAsUasset, equipmentEdits);
                    if (!grantResult.Success)
                    {
                        result.Status = "error";
                        result.Error = grantResult.Error ?? "The foreign equipment gameplay abilities could not be granted.";
                        return result;
                    }
                    result.Log.Add($"ability-set grant: {grantResult.Status} [{string.Join(",", grantResult.Changes)}]");

                    if (dependencyPlan.FightingStyle is not null)
                    {
                        var hasCombatEffect = !string.IsNullOrWhiteSpace(dependencyPlan.FightingStyle.CombatTypeEffectPackage);
                        if (foreignEffects.Count != (hasCombatEffect ? 1 : 0))
                        {
                            result.Status = "error";
                            result.Error = "A fighting-style bridge must resolve to exactly one combat-type effect.";
                            return result;
                        }
                        var sourceSetPackage = dependencyPlan.FightingStyle?.CharacterAbilitySetPackage ?? "";
                        var sourceSetUasset = string.IsNullOrWhiteSpace(sourceSetPackage)
                            ? ""
                            : ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, sourceSetPackage) ?? "";
                        var effectResult = abilityMutation.SetExclusiveCombatTypeEffect(
                            customAsUasset,
                            hasCombatEffect ? new AbilityAssetMutationService.GameplayEffectAddition
                            {
                                PackagePath = foreignEffects[0],
                                SourceAbilitySetUassetPath = sourceSetUasset,
                                SourceEffectPackagePath = foreignEffects[0],
                            } : null);
                        if (!effectResult.Success)
                        {
                            result.Status = "error";
                            result.Error = effectResult.Error ?? "The fighting-style combat effect could not be granted.";
                            return result;
                        }
                        result.Log.Add($"combat-effect bridge: {effectResult.Status} [{string.Join(",", effectResult.Changes)}]");
                    }
                }
                else if (foreignAbilities.Count > 0 || foreignEffects.Count > 0 || dependencyPlan.FightingStyle is not null ||
                         dependencyPlan.GameplayAbilitiesToRemove.Count > 0)
                {
                    result.Status = "error";
                    result.Error =
                        "The selected donor DPRD has no character AbilitySet to clone, so required equipment/fighting-style grants cannot be applied safely.";
                    return result;
                }

                if (SwordCombatService.Enabled(project.AbilityLoadout))
                {
                    var meleeSource = customizedSetPackages.GetValueOrDefault(SwordCombatService.NativeMelee, SwordCombatService.NativeMelee);
                    SwordCombatService.Generate(project.AbilityLoadout!, extractedRoot, patchedContentRoot, mod,
                        customDprdUasset, meleeSource, mappings!, result.Log);
                }

                if (HeldItemService.Independent(project.AbilityLoadout))
                    HeldItemService.Generate(project.AbilityLoadout!, extractedRoot, patchedContentRoot, mod, customDprdUasset, mappings!, result.Log);

                if (!VerifyStagedDependencyCertificate(
                        project,
                        donor,
                        extractedRoot,
                        patchedContentRoot,
                        customDprdUasset,
                        mod,
                        foreignAbilitySets,
                        foreignAbilities,
                        foreignEffects,
                        dependencyPlan,
                        needMas ? StageUasset(patchedContentRoot, customMasPkg) : "",
                        needLas ? StageUasset(patchedContentRoot, customLasPkg) : "",
                        out var certificateError))
                {
                    result.Status = "error";
                    result.Error = "The staged ability/equipment dependency certificate failed: " + certificateError;
                    return result;
                }
                result.Log.Add("staged dependency certificate: exact DPRD/GA/GE/MAS/LAS state verified");
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

    internal static bool VerifyStagedDependencyCertificate(
        NativeSuitProject project,
        DonorInfo donor,
        string extractedRoot,
        string stagedRoot,
        string stagedDprdUasset,
        string mod,
        IReadOnlyCollection<string> requiredAbilitySets,
        IReadOnlyCollection<string> bridgeAbilities,
        IReadOnlyCollection<string> combatEffects,
        AbilityDependencyPlan plan,
        string stagedMasUasset,
        string stagedLasUasset,
        out string error)
    {
        error = "";
        var mutation = new AbilityAssetMutationService();
        var donorDprdUasset = ExtractedPackagePathService.ResolvePackageUasset(
            extractedRoot,
            donor.DprdPackage) ?? "";
        var donorInspection = mutation.InspectDprdAbilitySets(donorDprdUasset);
        if (!donorInspection.Success)
        {
            error = donorInspection.Error ?? "The donor DPRD AbilitySets array could not be read.";
            return false;
        }

        var donorSets = donorInspection.AbilitySets.Select(reference => reference.PackagePath).ToList();
        var expectedSets = AbilityLoadoutService.Resolve(
            donorSets,
            project.AbilityLoadout,
            requiredAbilitySets).ToList();
        var customizedSets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var editedIndex = 0;
        foreach (var selection in (project.AbilityLoadout?.AbilitySets ?? [])
                     .Where(selection => selection.Enabled)
                     .OrderBy(selection => selection.Order))
        {
            if (selection.AddedGameplayAbilities.Count == 0 &&
                selection.RemovedGameplayAbilities.Count == 0)
            {
                continue;
            }
            var source = UnrealPathUtil.NormalizePackagePath(selection.PackagePath);
            customizedSets[source] = $"/Game/Mods/{mod}/Characters/AS_{mod}_User_{editedIndex++:00}";
        }
        for (var index = 0; index < expectedSets.Count; index++)
        {
            if (customizedSets.TryGetValue(expectedSets[index], out var custom))
            {
                expectedSets[index] = custom;
            }
        }

        var needsBridge = bridgeAbilities.Count > 0 || combatEffects.Count > 0 || plan.FightingStyle is not null ||
                          plan.GameplayAbilitiesToRemove.Count > 0;
        var bridgePackage = "";
        if (needsBridge)
        {
            if (string.IsNullOrWhiteSpace(donor.AbilitySetPackage))
            {
                error = "The donor has no character AbilitySet for the required grant bridge.";
                return false;
            }
            bridgePackage = customizedSets.GetValueOrDefault(
                UnrealPathUtil.NormalizePackagePath(donor.AbilitySetPackage),
                $"/Game/Mods/{mod}/Characters/AS_{mod}");
            for (var index = 0; index < expectedSets.Count; index++)
            {
                if (expectedSets[index].Equals(donor.AbilitySetPackage, StringComparison.OrdinalIgnoreCase))
                {
                    expectedSets[index] = bridgePackage;
                }
            }
        }

        if (SwordCombatService.Enabled(project.AbilityLoadout))
        {
            var sourceMelee = customizedSets.GetValueOrDefault(SwordCombatService.NativeMelee, SwordCombatService.NativeMelee);
            var sourceIndex = expectedSets.FindIndex(p => p.Equals(sourceMelee, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0) { error = "The sword preset requires one player martial melee set."; return false; }
            expectedSets[sourceIndex] = SwordCombatService.MeleePackage(mod);
            if (!SwordCombatService.Verify(project.AbilityLoadout!, extractedRoot, stagedRoot, mod, LoadMappings()!, out error)) return false;
        }

        if (HeldItemService.Independent(project.AbilityLoadout))
        {
            expectedSets.AddRange(project.AbilityLoadout!.HeldItems!.Select(i => HeldItemService.SetPackage(mod, i)));
            if (!HeldItemService.Verify(project.AbilityLoadout, extractedRoot, stagedRoot, mod, LoadMappings()!, out error)) return false;
        }
        var actualDprd = mutation.InspectDprdAbilitySets(stagedDprdUasset);
        var actualSets = actualDprd.AbilitySets.Select(reference => reference.PackagePath).ToList();
        if (!actualDprd.Success ||
            !actualSets.SequenceEqual(expectedSets, StringComparer.OrdinalIgnoreCase))
        {
            error = actualDprd.Error ??
                    "The generated DPRD's serialized AbilitySets membership/order differs from the resolved loadout.";
            return false;
        }
        var duplicatedEquipmentSets = actualSets.Where(actual =>
                plan.EquipmentOwnedAbilitySets.Contains(actual, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (duplicatedEquipmentSets.Count > 0)
        {
            error = "Equipment-owned controller AbilitySets were duplicated into DPRD: " +
                    string.Join(", ", duplicatedEquipmentSets.Select(UnrealPathUtil.AssetName));
            return false;
        }

        foreach (var abilitySetPackage in actualSets)
        {
            var abilitySetUasset = ResolveDependencyUasset(extractedRoot, stagedRoot, abilitySetPackage);
            if (string.IsNullOrWhiteSpace(abilitySetUasset))
            {
                error = $"Final DPRD AbilitySet does not resolve to staged or extracted content: {abilitySetPackage}";
                return false;
            }
            var abilitySet = mutation.InspectAbilitySet(abilitySetUasset);
            if (!abilitySet.Success)
            {
                error = abilitySet.Error ?? $"Final AbilitySet could not be inspected: {abilitySetPackage}";
                return false;
            }
            foreach (var grantPackage in abilitySet.GameplayAbilities.Concat(abilitySet.GameplayEffects)
                         .Select(grant => grant.PackagePath)
                         .Where(ExtractedPackagePathService.IsContentPackagePath))
            {
                if (string.IsNullOrWhiteSpace(ResolveDependencyUasset(extractedRoot, stagedRoot, grantPackage)))
                {
                    error = $"{UnrealPathUtil.AssetName(abilitySetPackage)} grants an unresolved gameplay asset: {grantPackage}";
                    return false;
                }
            }
        }

        foreach (var package in plan.RequiredGameplayAbilities.Concat(plan.RequiredGameplayEffects))
        {
            if (string.IsNullOrWhiteSpace(ResolveDependencyUasset(extractedRoot, stagedRoot, package)))
            {
                error = $"A required gameplay dependency is missing from staged and extracted content: {package}";
                return false;
            }
        }

        if (needsBridge)
        {
            var bridgeUasset = ResolveDependencyUasset(extractedRoot, stagedRoot, bridgePackage);
            var bridge = mutation.InspectAbilitySet(bridgeUasset);
            if (!bridge.Success)
            {
                error = bridge.Error ?? "The suit-local character AbilitySet bridge could not be inspected.";
                return false;
            }
            var actualAbilities = bridge.GameplayAbilities.Select(grant => grant.PackagePath).ToList();
            var missingBridge = bridgeAbilities.Where(required =>
                !actualAbilities.Contains(required, StringComparer.OrdinalIgnoreCase)).ToList();
            var staleBridge = plan.GameplayAbilitiesToRemove.Where(removed =>
                actualAbilities.Contains(removed, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missingBridge.Count > 0 || staleBridge.Count > 0)
            {
                error = "The suit-local bridge has incorrect gameplay-ability grants. Missing: " +
                        string.Join(", ", missingBridge.Select(UnrealPathUtil.AssetName)) +
                        "; still present after removal: " +
                        string.Join(", ", staleBridge.Select(UnrealPathUtil.AssetName));
                return false;
            }
            if (plan.FightingStyle is { } style)
            {
                var actualCombat = bridge.GameplayEffects
                    .Where(effect => AbilityAssetMutationService.IsCombatTypeEffect(effect.PackagePath))
                    .Select(effect => effect.PackagePath)
                    .ToList();
                var expectedCombat = string.IsNullOrWhiteSpace(style.CombatTypeEffectPackage)
                    ? Array.Empty<string>() : new[] { style.CombatTypeEffectPackage };
                if (!actualCombat.SequenceEqual(expectedCombat, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"The suit-local bridge has incorrect combat effects for {style.DisplayName}. Expected: {string.Join(", ", expectedCombat)}.";
                    return false;
                }
            }
        }

        if (!VerifyAnimationDependencyState(
                "MAS",
                stagedMasUasset,
                extractedRoot,
                stagedRoot,
                plan.RequiredMontageAnimSets,
                plan.MontageAnimSetsToRemove,
                plan.AnimationReplacements.Where(replacement =>
                    replacement.Kind.StartsWith("Montage", StringComparison.OrdinalIgnoreCase)),
                out error) ||
             !VerifyAnimationDependencyState(
                "LAS",
                stagedLasUasset,
                extractedRoot,
                stagedRoot,
                plan.RequiredLayerAnimSets.Concat(plan.RequiredLayerSlices.Select(slice =>
                    FightingStyleLayerSlicePackage(mod, slice))),
                plan.LayerAnimSetsToRemove,
                plan.AnimationReplacements.Where(replacement =>
                    replacement.Kind.Equals("Layer", StringComparison.OrdinalIgnoreCase)),
                out error))
        {
            return false;
        }
        return true;
    }

    internal static string FightingStyleLayerSlicePackage(
        string mod,
        FightingStyleLayerSlice slice)
    {
        var sourceStem = UnrealPathUtil.AssetName(slice.SourcePackage);
        var contextStem = string.Join("_", slice.RequiredContextTags.Concat(slice.AdditionalContextTags ?? [])
            .Select(tag => tag.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Context")
            .Select(segment => new string(segment.Where(char.IsLetterOrDigit).ToArray()))
            .Where(segment => segment.Length > 0));
        if (string.IsNullOrWhiteSpace(contextStem)) contextStem = "Context";
        return $"/Game/Mods/{mod}/Characters/{sourceStem}_{contextStem}_{mod}";
    }

    private static bool VerifyAnimationDependencyState(
        string label,
        string stagedUasset,
        string extractedRoot,
        string stagedRoot,
        IEnumerable<string> requiredPackages,
        IEnumerable<string> removedPackages,
        IEnumerable<AbilityAnimationReplacement> replacements,
        out string error)
    {
        error = "";
        var required = requiredPackages.Select(UnrealPathUtil.NormalizePackagePath)
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .ToList();
        var removed = removedPackages.Select(UnrealPathUtil.NormalizePackagePath)
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .ToList();
        var replacementList = replacements.ToList();
        if (required.Count == 0 && removed.Count == 0 && replacementList.Count == 0)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(stagedUasset) || !File.Exists(stagedUasset))
        {
            error = $"The required generated {label} composite is missing.";
            return false;
        }
        var inspection = new AnimGraftService().InspectParentSets(stagedUasset);
        if (!inspection.Success)
        {
            error = inspection.Error ?? $"The generated {label} ParentSetsArray could not be inspected.";
            return false;
        }
        var parents = inspection.PackagePaths;
        var missing = required.Where(package =>
            !parents.Contains(package, StringComparer.OrdinalIgnoreCase)).ToList();
        var stale = removed.Where(package =>
            parents.Contains(package, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0 || stale.Count > 0)
        {
            error = $"The generated {label} has an incorrect exact parent set. Missing: " +
                    string.Join(", ", missing.Select(UnrealPathUtil.AssetName)) +
                    "; stale: " + string.Join(", ", stale.Select(UnrealPathUtil.AssetName));
            return false;
        }
        foreach (var replacement in replacementList)
        {
            var matching = parents.Where(package => UnrealPathUtil.AssetName(package).StartsWith(
                    replacement.DonorSetPrefix,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (replacement.Kind.Equals("MontageRemove", StringComparison.OrdinalIgnoreCase))
            {
                if (matching.Count > 0)
                {
                    error = $"The generated {label} still contains excluded {replacement.DonorSetPrefix}* parents.";
                    return false;
                }
            }
            else if (matching.Count != 1 ||
                     !matching[0].Equals(replacement.ReplacementPackage, StringComparison.OrdinalIgnoreCase))
            {
                error = $"The generated {label} must contain exactly one {replacement.DonorSetPrefix}* parent, '{replacement.ReplacementPackage}'.";
                return false;
            }
        }
        foreach (var parent in parents.Where(ExtractedPackagePathService.IsContentPackagePath))
        {
            if (string.IsNullOrWhiteSpace(ResolveDependencyUasset(extractedRoot, stagedRoot, parent)))
            {
                error = $"The generated {label} references an unresolved exact parent package: {parent}";
                return false;
            }
        }
        return true;
    }

    private static string ResolveDependencyUasset(
        string extractedRoot,
        string stagedRoot,
        string package)
    {
        var normalized = UnrealPathUtil.NormalizePackagePath(package);
        if (!ExtractedPackagePathService.IsContentPackagePath(normalized)) return "";
        var staged = StageUasset(stagedRoot, normalized);
        if (File.Exists(staged)) return staged;
        var extracted = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, normalized) ?? "";
        return File.Exists(extracted) ? extracted : "";
    }

    // Generated-derivative filename prefixes: everything the archetype graft emits
    // into /Game/Mods/<mod>/Characters/. NOT prefixes of the hand-staged files that
    // must survive (BP_<Family>_<Suit>_Playable/_Cutscene, DA_DCMD_*, MI_*, T_*).
    private static readonly string[] GeneratedDerivativePrefixes =
    {
        "BP_CAT_Archetype_", "AS_", "MAS_", "LAS_", "DA_DPRD_", "ABP_", "BS_"
    };

    /// <summary>
    /// Mirrors the loadout-generation gate: native donor-family gadgets already live in the
    /// gameplay DPRD, while a foreign equipment definition or controller ability set requires a
    /// generated mod-local DPRD. Shared with final validation so valid native-equipment adapters
    /// are not mistaken for foreign loadout grafts.
    /// </summary>
    internal static bool RequiresGeneratedDprd(
        NativeSuitProject project,
        string? donorFamily)
    {
        var gameData = GameDataService.Instance;
        var exactKnown = AbilityDependencyService.TryReadDonorRuntimeEquipmentSlots(
            project,
            gameData.Db.Equipment,
            out var donorSlots);
        foreach (var change in project.EquipmentSlots)
        {
            var equipment = gameData.FindEquipment(change.Gadget);
            if (equipment is null)
            {
                continue;
            }
            var nativeAtSlot = exactKnown &&
                               donorSlots.TryGetValue(change.Slot, out var donorItem) &&
                               donorItem.Equals(equipment.Name, StringComparison.OrdinalIgnoreCase);
            if (!nativeAtSlot && !string.IsNullOrWhiteSpace(equipment.EdPackage))
            {
                return true;
            }
        }
        return AbilityLoadoutService.HasCustomizations(project);
    }

    /// <summary>
    /// Final generation uses the already-resolved dependency lists, which also include the
    /// AS_Gliding set automatically added for ordinary non-paired replacement gliders. Keep this
    /// separate from the paired-adapter equipment projection above so a no-equipment wingsuit
    /// cannot lose its generated DPRD.
    /// </summary>
    internal static bool RequiresGeneratedDprdFromResolvedDependencies(
        bool hasForeignEquipmentDefinitions,
        bool hasForeignAbilitySets) =>
        hasForeignEquipmentDefinitions || hasForeignAbilitySets;

    public static bool RequiresCustomArchetype(NativeSuitProject project) =>
        project.UseCustomArchetype ||
        AbilityLoadoutService.HasCustomizations(project) ||
        HasExactEquipmentGraftDependency(project);

    private static bool HasExactEquipmentGraftDependency(NativeSuitProject project)
    {
        if (project.EquipmentSlots.Count == 0) return false;
        var equipment = GameDataService.Instance.Db.Equipment;
        var exactKnown = AbilityDependencyService.TryReadDonorRuntimeEquipmentSlots(
            project,
            equipment,
            out var donorSlots);
        foreach (var change in project.EquipmentSlots)
        {
            var item = equipment.FirstOrDefault(candidate =>
                candidate.Name.Equals(change.Gadget, StringComparison.OrdinalIgnoreCase));
            if (item is null) continue;
            if (!exactKnown ||
                !donorSlots.TryGetValue(change.Slot, out var donorItem) ||
                !donorItem.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void PopulateFirstGrantTemplateIfNeeded(
        IList<AbilityAssetMutationService.GameplayAbilityEdit> edits,
        string targetAbilitySetUasset,
        string extractedRoot,
        AbilityAssetMutationService mutation)
    {
        if (!edits.Any(edit => edit.Kind == AbilityAssetMutationService.GameplayAbilityEditKind.Add) ||
            mutation.InspectAbilitySet(targetAbilitySetUasset).GameplayAbilities.Count > 0)
        {
            return;
        }

        const string templatePackage = "/Game/Characters/Minifig/Batman/Abilities/AS_Batman";
        var templateUasset = ExtractedPackagePathService.ResolvePackageUasset(extractedRoot, templatePackage) ?? "";
        if (!File.Exists(templateUasset))
        {
            return;
        }
        var templateGrant = mutation.InspectAbilitySet(templateUasset).GameplayAbilities.FirstOrDefault();
        if (templateGrant is null)
        {
            return;
        }

        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            if (edit.Kind != AbilityAssetMutationService.GameplayAbilityEditKind.Add)
            {
                continue;
            }
            edits[index] = new AbilityAssetMutationService.GameplayAbilityEdit
            {
                Kind = edit.Kind,
                TargetPackagePath = edit.TargetPackagePath,
                ReplacementPackagePath = edit.ReplacementPackagePath,
                AbilityLevelOverride = edit.AbilityLevelOverride,
                InputTagOverride = edit.InputTagOverride,
                InsertIndex = edit.InsertIndex,
                SourceAbilitySetUassetPath = templateUasset,
                SourceAbilityPackagePath = templateGrant.PackagePath,
            };
        }
    }

    /// <summary>
    /// Adds the runtime gliding ability dependency for an ordinary replacement glider. The paired
    /// Nightwing cape adapter deliberately keeps its gameplay donor's native gliding loadout, but
    /// all other replacement gliders still need AS_Gliding in a generated DPRD even when the user
    /// selected no equipment. Kept as one production helper so the release regression exercises
    /// the same dependency mutation that feeds the generation gate.
    /// </summary>
    internal static bool EnsureGliderAbilitySetDependency(
        NativeSuitProject project,
        bool usesPairedCapeAdapter,
        ICollection<string> foreignAbilitySets)
    {
        if (!project.PartGrafts.Any(graft => graft.IsGlider) ||
            usesPairedCapeAdapter ||
            foreignAbilitySets.Contains(
                GliderService.GlidingAbilitySetPackage,
                StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        foreignAbilitySets.Add(GliderService.GlidingAbilitySetPackage);
        return true;
    }

    /// <summary>
    /// Deletes previously-generated archetype derivatives (ability sets, DPRD, anim
    /// composites, cloned locomotion graph, the custom archetype) from the packaged
    /// stage so a re-package emits exactly the CURRENT config with no orphans. Leaves
    /// the playable/cutscene/DCMD/materials in place - they are re-derived or reused.
    /// </summary>
    private static void PurgeGeneratedArchetypeDerivatives(
        string contentRoot,
        string customArchetypePackage,
        Result result)
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

    private static void AddPackageAndStemReplacement(
        IDictionary<string, string> replacements,
        string sourcePackage,
        string targetPackage)
    {
        sourcePackage = UnrealPathUtil.NormalizePackagePath(sourcePackage);
        targetPackage = UnrealPathUtil.NormalizePackagePath(targetPackage);
        if (string.IsNullOrWhiteSpace(sourcePackage) || string.IsNullOrWhiteSpace(targetPackage))
        {
            return;
        }
        replacements[sourcePackage] = targetPackage;
        replacements[UnrealPathUtil.AssetName(sourcePackage)] = UnrealPathUtil.AssetName(targetPackage);
    }

    private static bool ApplyExactSlotOverrides(
        IReadOnlyList<AnimationSlotOverride> overrides,
        string setClass,
        string customCompositePackage,
        string extractedRoot,
        string patchedRoot,
        string mod,
        Usmap? mappings,
        AnimGraftService graft,
        Result result)
    {
        foreach (var group in overrides
                     .Where(change => !string.IsNullOrWhiteSpace(change.OwnerSetPackage))
                     .GroupBy(
                         change => UnrealPathUtil.NormalizePackagePath(change.OwnerSetPackage),
                         StringComparer.OrdinalIgnoreCase))
        {
            var ownerPackage = group.Key;
            if (!ownerPackage.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "error";
                result.Error = $"Animation override owner is not a /Game package: {ownerPackage}";
                return false;
            }

            var ownerStem = UnrealPathUtil.AssetName(ownerPackage);
            var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(ownerPackage.ToUpperInvariant())))
                [..8]
                .ToLowerInvariant();
            var clonedStem = $"{ownerStem}_{mod}_{hash}";
            var clonedPackage = $"/Game/Mods/{mod}/Characters/AnimationSets/{clonedStem}";
            CloneDonorAsset(
                extractedRoot,
                ownerPackage,
                ownerStem,
                patchedRoot,
                clonedPackage,
                clonedStem,
                mappings,
                result);

            var clonedUasset = StageUasset(patchedRoot, clonedPackage);
            if (!File.Exists(clonedUasset))
            {
                result.Status = "error";
                result.Error = $"The animation set '{ownerPackage}' could not be cloned from the active extract.";
                return false;
            }

            foreach (var change in group)
            {
                var patched = graft.ReplaceAnimationSlot(clonedUasset, change);
                result.Log.Add(
                    $"animation slot [{change.ActionTag}]: {patched.Status} " +
                    $"{UnrealPathUtil.AssetName(change.DonorPackage)}→{UnrealPathUtil.AssetName(change.ReplacementPackage)}" +
                    ErrSuffix(patched.Error));
                if (!patched.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                {
                    result.Status = "error";
                    result.Error = patched.Error ??
                                   $"The animation slot '{change.ActionTag}' could not be patched.";
                    return false;
                }
            }

            var repointed = graft.ReplaceParentSet(
                StageUasset(patchedRoot, customCompositePackage),
                setClass,
                ownerStem,
                clonedPackage,
                requireExisting: true);
            result.Log.Add(
                $"animation parent clone: {repointed.Status} {ownerStem}→{clonedStem}" +
                ErrSuffix(repointed.Error));
            if (!repointed.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "error";
                result.Error = repointed.Error ??
                               $"The character animation composite no longer contains '{ownerStem}'.";
                return false;
            }
        }

        return true;
    }

    internal static string? LocomotionCompositionConflict(
        string donorFamily,
        IReadOnlyList<AnimSetOverride> layerOverrides,
        IReadOnlyList<AnimationSlotOverride> layerSlotOverrides)
    {
        if (string.IsNullOrWhiteSpace(donorFamily))
        {
            return null;
        }

        var defaultSet = "LAS_Default_" + donorFamily;
        var replacesDefaultSet = layerOverrides.Any(change =>
            DonorSetForCategory(change.Category, donorFamily)
                .Equals(defaultSet, StringComparison.OrdinalIgnoreCase));
        var clonesDefaultSet = layerSlotOverrides.Any(change =>
            UnrealPathUtil.AssetName(UnrealPathUtil.NormalizePackagePath(change.OwnerSetPackage))
                .Equals(defaultSet, StringComparison.OrdinalIgnoreCase));
        if (!replacesDefaultSet && !clonesDefaultSet)
        {
            return null;
        }

        var conflictingEdit = replacesDefaultSet
            ? "a whole Locomotion layer-set swap"
            : "an exact animation-layer replacement inside the donor's LAS_Default set";
        return
            $"This suit combines individual idle/walk/run overrides with {conflictingEdit}. " +
            $"Both edits need to own '{defaultSet}', which would create two competing locomotion controllers. " +
            "Reset either the individual locomotion overrides or the conflicting layer edit, then build again.";
    }

    private static void CloneDonorAsset(string extractedRoot, string donorPackage, string donorStem,
        string patchedRoot, string targetPackage, string targetStem, Usmap? mappings, Result result)
    {
        var donorBase = ExtractedPackagePathService.ResolvePackageBase(extractedRoot, donorPackage);
        if (string.IsNullOrWhiteSpace(donorBase))
        {
            result.Log.Add($"clone {donorStem}: invalid donor package '{donorPackage}' — skipped");
            return;
        }
        if (!File.Exists(donorBase + ".uasset"))
        {
            result.Log.Add($"clone {donorStem}: donor not extracted ({donorBase}.uasset) — skipped");
            return;
        }

        var targetBase = PackageToStageBase(patchedRoot, targetPackage);
        Directory.CreateDirectory(Path.GetDirectoryName(targetBase)!);
        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
        {
            if (File.Exists(donorBase + extension))
            {
                File.Copy(donorBase + extension, targetBase + extension, overwrite: true);
            }
        }

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
            var patched = ApplyReplacementMapNonCascading(original, ordered);
            if (patched != original)
            {
                asset.SetNameReference(i, new FString(patched));
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// Applies every match found in the original name without rescanning replacement text. A
    /// package replacement such as MAS_Char_Nightwing -> MAS_Char_MyMod may itself contain the
    /// shorter source stem; cascading the stem rule into that new text used to produce corrupt
    /// paths such as MAS_Char_MyModMyMod. Longest-match selection also lets a compound
    /// Package.Object name replace both original segments in one pass.
    /// </summary>
    private static string ApplyReplacementMapNonCascading(
        string original,
        IReadOnlyList<KeyValuePair<string, string>> ordered)
    {
        if (string.IsNullOrEmpty(original) || ordered.Count == 0)
        {
            return original;
        }

        System.Text.StringBuilder? patched = null;
        var copyFrom = 0;
        for (var position = 0; position < original.Length;)
        {
            KeyValuePair<string, string>? match = null;
            foreach (var pair in ordered)
            {
                if (pair.Key.Length <= original.Length - position &&
                    original.AsSpan(position, pair.Key.Length).SequenceEqual(pair.Key.AsSpan()))
                {
                    match = pair;
                    break;
                }
            }

            if (match is not { } replacement)
            {
                position++;
                continue;
            }

            patched ??= new System.Text.StringBuilder(original.Length + 32);
            patched.Append(original, copyFrom, position - copyFrom);
            patched.Append(replacement.Value);
            position += replacement.Key.Length;
            copyFrom = position;
        }

        if (patched is null)
        {
            return original;
        }
        patched.Append(original, copyFrom, original.Length - copyFrom);
        return patched.ToString();
    }

    internal static string ApplyNameMapReplacementsForTest(
        string original,
        IReadOnlyDictionary<string, string> replacements)
    {
        var ordered = replacements
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                           !string.IsNullOrWhiteSpace(pair.Value) &&
                           !pair.Key.Equals(pair.Value, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length)
            .ToList();
        return ApplyReplacementMapNonCascading(original, ordered);
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

    private static bool IsExtractedPackagePath(string contentRoot, string packagePath) =>
        ExtractedPackagePathService.ResolvePackageBase(contentRoot, packagePath) is not null;

    private static string PackageToStageBase(string contentRoot, string packagePath)
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

    private static string StageUasset(string contentRoot, string packagePath) => PackageToStageBase(contentRoot, packagePath) + ".uasset";

    private static string ErrSuffix(string? error) => string.IsNullOrWhiteSpace(error) ? "" : " ERROR=" + error.Split('\n')[0];

    private static Usmap? LoadMappings()
    {
        var configured = AppSettings.Current.EffectiveUsmapPath();
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? MappingsCache.Load(configured) : null;
    }
}
