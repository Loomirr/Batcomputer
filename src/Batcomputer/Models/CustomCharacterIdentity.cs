namespace Batcomputer;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CharacterModeAvailability { Normal, Mayhem, Both }

public sealed class CustomCharacterIdentity
{
    public bool IsDefinition { get; set; }
    public string CharacterId { get; set; } = "";
    public string DefinitionSlotId { get; set; } = "";
    public string VariantId { get; set; } = "";
    // Advanced material override. An imported PNG takes precedence; empty retains the donor emblem.
    public string SymbolPackage { get; set; } = "";
    // Embedded source keeps symbols portable when a character recipe is shared or rebuilt.
    public string SymbolPngBase64 { get; set; } = "";
    // The definition owns this setting. Child suits inherit it at build time.
    public CharacterModeAvailability ModeAvailability { get; set; } = CharacterModeAvailability.Normal;
}
