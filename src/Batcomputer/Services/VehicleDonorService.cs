namespace Batcomputer;

internal sealed record VehicleAnimation(string Label, string Package);

/// <summary>Audited asset sets. Donor-specific graph edits remain opt-in.</summary>
internal static class VehicleDonorService
{
    internal sealed record Donor(string Id, string Label, string Stem, string Plinth, bool FullWorkshop = false)
    {
        public string Root => "/Game/Models/Vehicles/VEH_" + Stem;
        public string Mesh { get; init; } = "/Game/Models/Vehicles/VEH_" + Stem + "/SK_VEH_" + Stem;
        public string Skeleton { get; init; } = "/Game/Models/Vehicles/VEH_" + Stem + "/SKEL_VEH_" + Stem;
        public string Physics { get; init; } = "/Game/Models/Vehicles/VEH_" + Stem + "/PHYS_VEH_" + Stem;
        public string Blueprint { get; init; } = "/Game/Models/Vehicles/VEH_" + Stem + "/BP_VEH_" + Stem;
        public string Metadata { get; init; } = "/Game/Vehicles/DA_Vehicle_" + Stem;
        public string Ui { get; init; } = "/Game/Vehicles/DA_UI_" + Stem;
        public string Menu { get; init; } = "/Game/Vehicles/MenuActors/BP_MenuActor_" + (Id == "sportscarTalia" ? "NinjaCar" : Stem);
        public string SummonMesh { get; init; } = "/Game/Models/Vehicles/Summon/SK_VEH_" + Stem + "_Summon";
        public string SummonSkeleton { get; init; } = "/Game/Models/Vehicles/Summon/SKEL_VEH_" + Stem + "_Summon";
        public bool HeadlightEditing { get; init; } = true;
        public string? RequiredDlc { get; init; }
        public string Notes { get; init; } = "Check fit, seats and driving in game.";
        public IReadOnlyList<VehicleAnimation> Animations { get; init; } = [];
        public IEnumerable<string> Required => [Mesh, Skeleton, Physics, Blueprint, Metadata, Ui, Menu, Plinth, VehicleAssetService.NativeProgress];
        public string[] MissingPackages(string content) => Required.Where(p => !PackageComplete(content, p)).ToArray();
        public bool IsAvailable(string content) => MissingPackages(content).Length == 0;
        public string UnavailableMessage => "The " + Label + " files are missing. " + (RequiredDlc is null ? "" : "This base requires " + RequiredDlc + ". ") + "Use Refresh game assets → Add vehicle driving bases (quick).";
        public override string ToString() => Label;
    }
    private const string PlinthRoot = "/Game/LEGOGameplay/Mechanics/Batcave/VehiclePurchase/VehiclePlinthActors/";
    internal static readonly Donor[] All = [
        new("batmobile1995", "1995 Batman Forever Batmobile", "Batmobile1995_BatmanForever", PlinthRoot + "Batman/BP_VehiclePlinth_Batmobile_BatmanForever", true)
        { Animations = [new("Canopy open", "/Game/Animation/Vehicles/Batmobile1995/Mechanics/A_Canopy_Open_Batmobile1995"), new("Canopy close", "/Game/Animation/Vehicles/Batmobile1995/Mechanics/A_Canopy_Close_Batmobile1995"), new("Canopy idle", "/Game/Animation/Vehicles/Batmobile1995/Mechanics/A_Canopy_Idle_Batmobile1995")] },
        new("batmobile1997", "1997 Batman & Robin Batmobile", "Batmobile1997_BatmanAndRobin", PlinthRoot + "Batman/BP_VehiclePlinth_Batmobile1997_BatmanAndRobin"),
        new("sportscarTalia", "Talia sports car", "SportsCar_Talia", PlinthRoot + "Talia/BP_VehiclePlinth_SportsCar1"),
        new("batmobile1989", "1989 Batmobile (experimental)", "Batmobile1989_Batman_Tt", PlinthRoot + "Batman/BP_VehiclePlinth_Batmobile1989")
        {
            Mesh = "/Game/Models/Vehicles/VEH_Batmobile1989_Batman_Tt/SK_VEH_Batmobile1989_Batman_TT", Skeleton = "/Game/Models/Vehicles/VEH_Batmobile1989_Batman_Tt/SKEL_VEH_Batmobile1989_Batman_TT", Physics = "/Game/Models/Vehicles/VEH_Batmobile1989_Batman_Tt/PHYS_VEH_Batmobile1989_Batman_TT",
            Metadata = "/Game/Vehicles/DA_Vehicle_Batmobile1989", Ui = "/Game/Vehicles/DA_UI_Batmobile1989", Menu = "/Game/Vehicles/MenuActors/BP_MenuActor_Batmobile1989",
            SummonMesh = "/Game/Models/Vehicles/Summon/SK_VEH_Batmobile1989_BatmanBegins_TT_Summon", SummonSkeleton = "/Game/Models/Vehicles/Summon/SKEL_VEH_Batmobile1989_BatmanBegins_TT_Summon",
            HeadlightEditing = false, Notes = "Experimental. Hinged canopy and pop-up guns; no launcher sockets. Light controllers stay native.",
            Animations = [new("Canopy open", "/Game/Animation/Vehicles/Batmobile1989/A_Canopy_Open_Batmobile1989"), new("Canopy close", "/Game/Animation/Vehicles/Batmobile1989/A_Canopy_Close_Batmobile1989"), new("Canopy idle", "/Game/Animation/Vehicles/Batmobile1989/A_Canopy_Idle_Batmobile1989"), new("Launcher covers respawn", "/Game/Animation/Vehicles/Batmobile1989/A_RespawnLauncherCovers_Batmobile_1989"), new("Rocket launcher out", "/Game/Animation/Vehicles/Batmobile1989/A_RocketLauncher_Out_Batmobile_1989"), new("Rocket launcher shoot", "/Game/Animation/Vehicles/Batmobile1989/A_RocketLauncher_Shoot_Batmobile_1989"), new("Rocket launcher in", "/Game/Animation/Vehicles/Batmobile1989/A_RocketLauncher_In_Batmobile_1989")]
        },
        new("batmobile2005", "2005 Tumbler (experimental)", "Batmobile2005_BatmanBegins_Tt", PlinthRoot + "Batman/BP_VehiclePlinth_Tumbler")
        {
            Mesh = "/Game/Models/Vehicles/VEH_Batmobile2005_BatmanBegins_Tt/SK_VEH_Batmobile2005_BatmanBegins_TT", Skeleton = "/Game/Models/Vehicles/VEH_Batmobile2005_BatmanBegins_Tt/SKEL_VEH_Batmobile2005_BatmanBegins_TT", Physics = "/Game/Models/Vehicles/VEH_Batmobile2005_BatmanBegins_Tt/PHYS_VEH_Batmobile2005_BatmanBegins_TT",
            SummonMesh = "/Game/Models/Vehicles/Summon/SK_VEH_Batmobile2005_BatmanBegins_TT_Summon", SummonSkeleton = "/Game/Models/Vehicles/Summon/SKEL_VEH_Batmobile2005_BatmanBegins_TT_Summon",
            Metadata = "/Game/Vehicles/DA_Vehicle_Batmobile2005", Ui = "/Game/Vehicles/DA_UI_Batmobile2005", Menu = "/Game/Vehicles/MenuActors/BP_MenuActor_Batmobile2005",
            HeadlightEditing = false, Notes = "Experimental. Canopy, wings, flaps and wipers. Light controllers stay native.",
            Animations = [new("Canopy open", "/Game/Animation/Vehicles/Batmobile2005/Mechanics/A_Canopy_Open"), new("Canopy close", "/Game/Animation/Vehicles/Batmobile2005/Mechanics/A_Canopy_Close"), new("Canopy idle", "/Game/Animation/Vehicles/Batmobile2005/Mechanics/A_Canopy_Idle")]
        },
        new("batmobilemonstertruck", "Batmobeast (Party Pack DLC, experimental)", "Batmobile_MonsterTruck", MonsterRoot + "/BP_VehiclePlinth_MonsterTruck")
        {
            Mesh = MonsterRoot + "/SK_VEH_Batmobile_MonsterTruck", Skeleton = MonsterRoot + "/SKEL_VEH_Batmobile_MonsterTruck", Physics = MonsterRoot + "/PHYS_VEH_Batmobile_MonsterTruck", Blueprint = MonsterRoot + "/BP_VEH_Batmobile_MonsterTruck",
            Metadata = "/DLC_PartyPack/Vehicles/DA_Vehicle_Batmobile_MonsterTruck", Ui = "/Game/Vehicles/DA_UI_Batmobile_monsterTruck",
            Menu = "/Game/AdditionalContent/DLC_Shared/Vehicles/BP_MenuActor_Batmobile_MonsterTruck",
            SummonMesh = "/Game/AdditionalContent/DLC_Shared/Vehicles/Summon/SK_VEH_Batmobile_MonsterTruck_Summon", SummonSkeleton = "/Game/AdditionalContent/DLC_Shared/Vehicles/Summon/SKEL_VEH_Batmobile_MonsterTruck_Summon",
            RequiredDlc = "Party Pack DLC", HeadlightEditing = false, Notes = "Experimental articulated suspension. Creator and players need Party Pack DLC. Light controllers stay native."
        },
        new("batbike2022", "2022 Batbike (experimental)", "Batbike2022_theBatman", PlinthRoot + "Batman/BP_VehiclePlinth_Batbike2022")
        {
            Mesh = "/Game/Models/Vehicles/VEH_Batbike2022_theBatman/SK_VEH_Batbike2022_theBatman",
            Skeleton = "/Game/Models/Vehicles/VEH_Batbike2022_theBatman/SKEL_VEH_Batbike2022_theBatman",
            Physics = "/Game/Models/Vehicles/VEH_Batbike2022_theBatman/PHYS_VEH_Batbike2022_theBatman",
            Blueprint = "/Game/Models/Vehicles/VEH_Batbike2022_theBatman/BP_VEH-Batbike2022_theBatman",
            Metadata = "/Game/Vehicles/DA_Vehicle_Batbike2022",
            Ui = "/Game/Vehicles/DA_UI_Batbike2022",
            Menu = "/Game/Vehicles/MenuActors/BP_MenuActor_Batbike2022",
            SummonMesh = "/Game/Models/Vehicles/Summon/SK_VEH_Batbike2022_theBatman_Summon",
            SummonSkeleton = "/Game/Models/Vehicles/Summon/SKEL_VEH_Batbike2022_theBatman_Summon",
            HeadlightEditing = false,
            Notes = "Experimental two-wheeler. Uses Wheel_F and Wheel_B rather than four car-wheel bones; fit and test both rider seats, steering and ground clearance in game. Light controllers stay native."
        }
    ];
    internal static Donor Get(VehicleProject project) => All.SingleOrDefault(d => d.Id == project.DonorId)
        ?? throw new InvalidDataException("Unknown vehicle driving base. Open this project with a version that supports its donor.");
    private const string MonsterRoot = "/Game/AdditionalContent/DLC_Shared/Vehicles/VEH_Batmobile_MonsterTruck";
    internal static IEnumerable<string> Packages => All.SelectMany(d => d.Required.Concat([d.SummonMesh, d.SummonSkeleton])).Distinct(StringComparer.OrdinalIgnoreCase);
    internal static IEnumerable<string> ExtractionFilters => Packages.Select(ExtractionFilter);
    internal static string ExtractionFilter(string package)
    {
        if (!ExtractedPackagePathService.IsContentPackagePath(package)) throw new InvalidDataException("Invalid vehicle package path.");
        if (package.StartsWith("/Game/", StringComparison.Ordinal)) return "Content/" + package[6..];
        var slash = package.IndexOf('/', 1);
        return "Plugins/GameFeatures/" + package[1..slash] + "/Content/" + package[(slash + 1)..];
    }
    internal static bool PackageComplete(string content, string package)
    {
        var file = ExtractedPackagePathService.ResolvePackageUasset(content, package);
        return file is not null && new[] { file, Path.ChangeExtension(file, ".uexp") }.All(f => File.Exists(f) && new FileInfo(f).Length > 0);
    }
}
