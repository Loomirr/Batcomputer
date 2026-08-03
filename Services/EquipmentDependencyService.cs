namespace Batcomputer;

public enum EquipmentSupportKind
{
    Native,
    CrossFamily,
    Controller,
    FamilyOnly,
    Experimental
}

public sealed class EquipmentDependencyProfile
{
    public EquipmentSupportKind Support { get; init; }
    public string Architecture { get; init; } = "Standard gadget";
    public string Summary { get; init; } = "";
    public string? RequiredGameplayFamily { get; init; }
    public IReadOnlyList<string> AbilitySets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExtraGrantedAbilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DefinitionAbilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RuntimeActors { get; init; } = Array.Empty<string>();

    public string SupportLabel => Support switch
    {
        EquipmentSupportKind.Native => "Native",
        EquipmentSupportKind.CrossFamily => "Cross-family",
        EquipmentSupportKind.Controller => "Controller graft",
        EquipmentSupportKind.FamilyOnly => "Family-only",
        _ => "Experimental"
    };
}

public static class EquipmentDependencyService
{
    private sealed record ControllerDependencies(
        string RequiredGameplayFamily,
        string AbilitySet,
        string[] GrantedAbilities,
        string[] DefinitionAbilities,
        string[] RuntimeActors);

    private static readonly Dictionary<string, ControllerDependencies> Controllers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Drone"] = new(
                "Batgirl",
                "/Game/Characters/Equipment/Drone/Abilities/AS_DroneUser",
                new[]
                {
                    "/Game/Characters/Equipment/Drone/Abilities/GA_Item_Drone",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_MindControlHelmet_Active",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_PerchPoint_Active",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_RecallDuringTransform",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_Scan",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_Turret_Active",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_UsingDrone"
                },
                new[]
                {
                    "/Game/Characters/Equipment/Drone/Abilities/GA_GetDroneOut",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_DeployGuardian",
                    "/Game/Characters/Equipment/Drone/Abilities/GA_DroneUser_DeployRc"
                },
                new[] { "BP_Drone_Inst" }),
            ["RemoteKitten"] = new(
                "Catwoman",
                "/Game/Characters/Equipment/RemoteKitten/AS_RemoteKittenUser",
                new[]
                {
                    "/Game/Characters/Equipment/RemoteKitten/Abilites/GA_RemoteKitten_HandOver",
                    "/Game/Characters/Equipment/RemoteKitten/Abilites/GA_RemoteKitten_Pilot"
                },
                new[]
                {
                    "/Game/Characters/Equipment/RemoteKitten/Abilites/GA_RemoteKitten_DeploySpawn",
                    "/Game/Characters/Equipment/RemoteKitten/Abilites/GA_RemoteKitten_QuickAttack"
                },
                new[] { "BP_RemoteKitten_Inst", "BP_LaserPointer_Weapon" })
        };

    public static EquipmentDependencyProfile Analyze(GameDataEquipment equipment, string? donorFamily)
    {
        var native = !string.IsNullOrWhiteSpace(donorFamily) &&
                     equipment.NativeFamilies.Contains(donorFamily, StringComparer.OrdinalIgnoreCase);
        Controllers.TryGetValue(equipment.Name, out var controller);

        if (native)
        {
            return Build(
                EquipmentSupportKind.Native,
                controller,
                controller is null
                    ? $"This gadget is native to {donorFamily}; its normal loadout and character machinery already supply the dependency chain."
                    : $"This controller gadget is native to {donorFamily}; its full remote-control chain already comes from the gameplay donor.");
        }

        if (controller is not null)
        {
            var currentFamily = string.IsNullOrWhiteSpace(donorFamily) ? "no gameplay donor" : donorFamily;
            return Build(
                EquipmentSupportKind.Controller,
                controller,
                $"This is a remote controller, not a normal held gadget. It is confirmed to work only with a {controller.RequiredGameplayFamily} gameplay donor; the current donor is {currentFamily}. Packaging can stage its files, but the remote pawn will not operate on another family.");
        }

        if (equipment.NativeFamilies.Count == 0)
        {
            return Build(
                EquipmentSupportKind.Experimental,
                null,
                "This boss or NPC item has no known playable-family setup. The tool can stage its loadout data, but player controls, draw behavior, or animations may be missing.");
        }

        var hasGenericDependencies =
            equipment.VisualAbilities.Count > 0 ||
            !string.IsNullOrWhiteSpace(equipment.LayerAnimSet) ||
            !string.IsNullOrWhiteSpace(equipment.MontageAnimSet);
        if (!hasGenericDependencies)
        {
            return Build(
                EquipmentSupportKind.FamilyOnly,
                null,
                $"This item has no separate player animation or ability records to graft. Use a {string.Join("/", equipment.NativeFamilies)} gameplay donor for the reliable path.");
        }

        return Build(
            EquipmentSupportKind.CrossFamily,
            null,
            "The tool can graft this hero gadget's loadout, listed abilities, and available equipment animation sets into a foreign gameplay donor.");
    }

    public static IReadOnlyList<string> RequiredAbilitySets(GameDataEquipment equipment, string? donorFamily)
    {
        var profile = Analyze(equipment, donorFamily);
        return profile.Support == EquipmentSupportKind.Controller
            ? profile.AbilitySets
            : Array.Empty<string>();
    }

    private static EquipmentDependencyProfile Build(
        EquipmentSupportKind support,
        ControllerDependencies? controller,
        string summary) => new()
        {
            Support = support,
            Architecture = controller is null ? "Standard gadget" : "Remote controller",
            Summary = summary,
            RequiredGameplayFamily = controller?.RequiredGameplayFamily,
            AbilitySets = controller is null ? Array.Empty<string>() : new[] { controller.AbilitySet },
            ExtraGrantedAbilities = controller?.GrantedAbilities ?? Array.Empty<string>(),
            DefinitionAbilities = controller?.DefinitionAbilities ?? Array.Empty<string>(),
            RuntimeActors = controller?.RuntimeActors ?? Array.Empty<string>()
        };

    private static string AssetName(string packagePath)
    {
        var slash = packagePath.LastIndexOf('/');
        return slash >= 0 ? packagePath[(slash + 1)..] : packagePath;
    }
}
