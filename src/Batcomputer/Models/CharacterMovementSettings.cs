namespace Batcomputer;

/// <summary>Optional suit-local character attributes. Null fields inherit the gameplay donor.</summary>
public sealed class CharacterMovementSettings
{
    public float? RunSpeedMultiplier { get; set; }
    // Unreal character speeds use centimetres per second.
    public float? SprintSpeed { get; set; }
    // Height scales with launch velocity squared under unchanged gravity.
    public float? JumpHeightMultiplier { get; set; }
    public float? GlideSpeedMultiplier { get; set; }
    public float? GlideDescentMultiplier { get; set; }
    // Catalog keys name supported native attributes; null retains the original recipe format.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, float>? AttributeOverrides { get; set; }
    public CharacterMovementSettings Clone()
    {
        var copy = (CharacterMovementSettings)MemberwiseClone();
        copy.AttributeOverrides = AttributeOverrides is null ? null : new(AttributeOverrides, StringComparer.Ordinal);
        return copy;
    }
}
