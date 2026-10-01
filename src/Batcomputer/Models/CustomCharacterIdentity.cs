namespace Batcomputer;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CharacterModeAvailability { Normal, Mayhem, Both }

public sealed class CustomCharacterIdentity
{
    public bool IsDefinition { get; set; }
    public string CharacterId { get; set; } = "";
    // Stable project IDs and asset paths are independent from the runtime roster identity.
    // Empty preserves the historical Pawns.Playable.<CharacterId> family.
    public string PawnTagOwner { get; set; } = "";
    public string DefinitionSlotId { get; set; } = "";
    public string VariantId { get; set; } = "";
    // Advanced material override. An imported PNG takes precedence; empty retains the donor emblem.
    public string SymbolPackage { get; set; } = "";
    // Embedded source keeps symbols portable when a character recipe is shared or rebuilt.
    public string SymbolPngBase64 { get; set; } = "";
    // Empty retains the historically shipped 64px equipment-SDF interpretation.
    public string SymbolCookProfile { get; set; } = "";
    public float? SymbolBorderThickness { get; set; }
    // The definition owns this setting. Child suits inherit it at build time.
    public CharacterModeAvailability ModeAvailability { get; set; } = CharacterModeAvailability.Normal;
    // Empty retains the historical donor vehicle; "None" clears it; otherwise an exact vehicle pawn tag.
    public string DefaultVehicleTag { get; set; } = "";

    // Child-only provenance. Missing on older projects: do not guess which edits were inherited.
    public CharacterInheritanceState? Inheritance { get; set; }
}

public sealed class CharacterInheritanceState
{
    public int Version { get; set; } = 1;
    public string DefinitionSlotId { get; set; } = "";
    public Dictionary<string, string> CharacterFingerprints { get; set; } = new();
    public Dictionary<string, string> SuitFingerprints { get; set; } = new();
    public List<string> KeptSections { get; set; } = new();
}
