using System.Text.Json;
using System.Text.RegularExpressions;

namespace Batcomputer;

public sealed class VehicleProjectService(string projectRoot)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Root => Path.Combine(AppSettings.GeneratedRootFor(projectRoot), "VehicleProjects");
    public sealed record Summary(string Id, string Name, string Owner, string Path, string Error = "");
    public string ProjectPath(string id) { RequireId(id); return Path.Combine(Root, id + ".vehicle-project.json"); }
    public string DirectoryFor(VehicleProject project) { RequireId(project.Id); return Path.Combine(Root, project.Id); }
    public string Resolve(ModVehicleEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.VehicleProjectPath)) throw new InvalidDataException("Missing vehicle project path.");
        return Path.GetFullPath(Path.IsPathRooted(entry.VehicleProjectPath) ? entry.VehicleProjectPath : Path.Combine(projectRoot, entry.VehicleProjectPath));
    }
    public ModVehicleEntry Entry(VehicleProject project) => new() { VehicleId = project.Id, VehicleProjectPath = Path.GetRelativePath(projectRoot, ProjectPath(project.Id)) };
    public IReadOnlyList<Summary> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "*.vehicle-project.json").Select(path => {
            try { var p = Load(path); return new Summary(p.Id, p.DisplayName, p.OwnerTag, path); }
            catch (Exception ex) { return new Summary("", Path.GetFileName(path), "Unreadable project", path, ex.Message); }
        }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public VehicleProject Load(string path)
    {
        var p = JsonSerializer.Deserialize<VehicleProject>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty vehicle project.");
        ValidateIdentity(p); return p;
    }
    public string Save(VehicleProject project)
    {
        ValidateIdentity(project);
        var file = ProjectPath(project.Id);
        Directory.CreateDirectory(Root);
        AtomicFileUtil.WriteAllText(file, JsonSerializer.Serialize(project, Json));
        return file;
    }
    public List<VehicleProject> Enabled(NativeSuitModProject mod)
    {
        var results = new List<VehicleProject>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in mod.Vehicles.Where(e => e.Enabled))
        {
            var p = Load(Resolve(entry));
            if (!string.Equals(entry.VehicleId, p.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Vehicle entry identity changed. Remove and re-add " + p.DisplayName + ".");
            if (!ids.Add(p.Id)) throw new InvalidDataException("Duplicate vehicle in this mod: " + p.DisplayName);
            results.Add(p);
        }
        return results;
    }
    public static void RequireId(string id)
    {
        if (!Regex.IsMatch(id ?? "", "^[A-Za-z][A-Za-z0-9]{0,63}$")) throw new InvalidDataException("Vehicle IDs use letters and numbers, start with a letter, and have at most 64 characters.");
    }
    public static void ValidateIdentity(VehicleProject p)
    {
        RequireId(p.Id);
        if (p.SchemaVersion != 1) throw new InvalidDataException("This vehicle project needs a different Batcomputer version.");
        if (string.IsNullOrWhiteSpace(p.DisplayName)) throw new InvalidDataException("Give the vehicle a name.");
        if (!Regex.IsMatch(p.OwnerTag ?? "", "^Pawns\\.Playable\\.[A-Za-z][A-Za-z0-9]*$")) throw new InvalidDataException("Choose a character owner, not an individual suit tag.");
        if (p.DonorId != VehicleAssetService.DonorId) throw new InvalidDataException("This first vehicle editor supports the Batman Forever rig only.");
        if (p.Transforms is null || p.Palette is null || p.Transforms.Any(t => t is null || t.Component is null) || p.Palette.Any(c => c is null)) throw new InvalidDataException("Incomplete vehicle settings.");
        VehicleCustomizationService.ValidateShape(p);
        if (p.Transforms.Select(t => t.Component).Distinct(StringComparer.Ordinal).Count() != p.Transforms.Count) throw new InvalidDataException("Duplicate vehicle component edits.");
        foreach (var t in p.Transforms)
            if (string.IsNullOrWhiteSpace(t.Component) || new[] { t.X, t.Y, t.Z, t.Pitch, t.Yaw, t.Roll, t.ScaleX, t.ScaleY, t.ScaleZ }.Any(v => !float.IsFinite(v)) ||
                new[] { t.X, t.Y, t.Z }.Any(v => Math.Abs(v) > 10000) || new[] { t.Pitch, t.Yaw, t.Roll }.Any(v => Math.Abs(v) > 360) ||
                new[] { t.ScaleX, t.ScaleY, t.ScaleZ }.Any(v => v < .01f || v > 100)) throw new InvalidDataException("Component transforms contain invalid values.");
        if (p.Palette.Select(c => c.Slot).Distinct().Count() != p.Palette.Count || p.Palette.Any(c => c.Slot < 0 || p.Model is null || c.Slot >= p.Model.Materials.Count || new[] { c.R, c.G, c.B }.Any(v => !float.IsFinite(v) || v < 0 || v > 1)))
            throw new InvalidDataException("Invalid vehicle color slots.");
    }
    internal static string ContentRoot(VehicleProject p) { RequireId(p.Id); return "/Game/Mods/Vehicle_" + p.Id; }
    internal static string PawnTag(VehicleProject p) => "Pawns.Vehicle.Batcomputer." + p.Id;
    internal static string ProgressTag(VehicleProject p) => "GameProgress.Definitions.Vehicles." + p.OwnerTag.Split('.').Last() + ".Batcomputer_" + p.Id;
    internal static string Mesh(VehicleProject p) => ContentRoot(p) + "/Vehicles/SK_" + p.Id;
    internal static string Metadata(VehicleProject p) => ContentRoot(p) + "/Vehicles/DA_Vehicle_" + p.Id;
    internal static string Blueprint(VehicleProject p) => ContentRoot(p) + "/Vehicles/BP_" + p.Id;
    internal static string Progress(VehicleProject p) => "/Game/GameProgress/Mods/Vehicle_" + p.Id + "/PROG_" + p.Id;

    internal static bool RuntimeSuitManifestRequired(string buildRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(buildRoot, "mod.json")));
        return json.RootElement.GetProperty("suits").GetArrayLength() > 0;
    }

    internal static void ValidateInstalledCollisions(NativeSuitModProject mod, IReadOnlyList<VehicleProject> vehicles, string? installedRoot, ModReleaseValidationService.Result result)
    {
        if (vehicles.Count == 0 || string.IsNullOrEmpty(installedRoot) || !Directory.Exists(installedRoot)) return;
        var ownIds = mod.PreviousModIds.Append(mod.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(installedRoot))
        foreach (var name in new[] { "mod.json", "vehicle-content.json" })
        {
            var file = Path.Combine(folder, name); if (!File.Exists(file)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file)); var root = document.RootElement;
                var id = root.TryGetProperty("mod_id", out var value) ? value.GetString() ?? "" : Path.GetFileName(folder);
                if (ownIds.Contains(id) || !root.TryGetProperty("vehicles", out var entries)) continue;
                foreach (var entry in entries.EnumerateArray())
                foreach (var vehicle in vehicles)
                    if ((entry.TryGetProperty("vehicle_id", out var installedId) && string.Equals(installedId.GetString(), vehicle.Id, StringComparison.OrdinalIgnoreCase)) ||
                        (entry.TryGetProperty("pawn_tag", out var tag) && string.Equals(tag.GetString(), PawnTag(vehicle), StringComparison.OrdinalIgnoreCase)) ||
                        (entry.TryGetProperty("metadata", out var metadata) && string.Equals(metadata.GetString(), Metadata(vehicle), StringComparison.OrdinalIgnoreCase)))
                        result.AddError("installed vehicle collision", $"'{vehicle.DisplayName}' is already installed by '{id}'. Remove that release before installing it through another mod.", vehicle.Id);
            }
            catch (Exception ex) { result.AddError("installed vehicle collision", "Could not inspect installed vehicle ownership: " + file + ". " + ex.Message); }
        }
    }
}
