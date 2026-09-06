namespace Batcomputer;

public sealed class CustomCharacterIdentity
{
    public bool IsDefinition { get; set; }
    public string CharacterId { get; set; } = "";
    public string DefinitionSlotId { get; set; } = "";
    public string VariantId { get; set; } = "";
    // Native roster emblem. Empty retains the reference Batman emblem for now.
    public string SymbolPackage { get; set; } = "";
}
