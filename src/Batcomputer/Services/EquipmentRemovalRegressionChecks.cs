using System.Text.Json;

namespace Batcomputer;

internal static class EquipmentRemovalRegressionChecks
{
    internal sealed record Result(bool Passed, string Description);
    internal static List<Result> Run()
    {
        var checks = new List<Result>();
        var old = JsonSerializer.Deserialize<EquipmentSlotChange>("{\"Slot\":0,\"Gadget\":\"Whip\"}")!;
        var project = new NativeSuitProject { EquipmentSlots = [new() { Slot = 0, Gadget = "Whip", Remove = true }] };
        var copy = JsonSerializer.Deserialize<NativeSuitProject>(JsonSerializer.Serialize(project))!;
        checks.Add(new(!old.Remove && copy.EquipmentSlots[0].Remove && copy.EquipmentSlots[0].Slot == 0,
            "equipment removal is explicit and survives serialization; legacy replacements still inherit their old behavior"));
        checks.Add(new(EquipmentDependencyService.SavedChangeResolutionError(copy.EquipmentSlots[0], null) is null &&
            EquipmentDependencyService.SavedChangeResolutionError(new() { Remove = true, Custom = new() }, null) is not null,
            "removed equipment does not require a replacement gadget and rejects contradictory custom recipes"));
        checks.Add(new(AnimArchetypeGraftService.RequiresCustomArchetype(project) &&
            AnimArchetypeGraftService.RequiresGeneratedDprd(project, "Catwoman"),
            "equipment removal alone requires a suit-local archetype and runtime loadout clone"));
        var unknown = AbilityDependencyService.Build(project, "Catwoman", []);
        checks.Add(new(unknown.HasErrors && unknown.Issues.Any(i => i.Message.Contains("could not be read and mapped exactly")),
            "equipment removal fails closed when the original donor loadout is unknown"));
        var visuals = new AbilityLoadoutProfile { NativeHeldItems = [
            new() { AbilityPackage = "/Game/Characters/Abilities/GA_Whip" },
            new() { AbilityPackage = "/Game/Characters/Abilities/GA_Claws" }] };
        var active = NativeHeldItemService.ForEquipmentClosure(visuals, ["/Game/Characters/Abilities/GA_Whip"]);
        active.NativeHeldItems[0].Hide = true;
        checks.Add(new(active.NativeHeldItems.Count == 1 && active.NativeHeldItems[0].AbilityPackage.EndsWith("GA_Claws") &&
            visuals.NativeHeldItems.Count == 2 && !visuals.NativeHeldItems[1].Hide,
            "removed gadget visual edits are dormant without deleting saved edits or changing active claw bindings"));
        return checks;
    }
}
