namespace Batcomputer;

/// <summary>Optional suit-local movement tuning. Null fields inherit the gameplay donor.</summary>
public sealed class CharacterMovementSettings
{
    public float? RunSpeedMultiplier { get; set; }
    // Unreal character speeds use centimetres per second.
    public float? SprintSpeed { get; set; }
    // Height scales with launch velocity squared under unchanged gravity.
    public float? JumpHeightMultiplier { get; set; }
    public float? GlideSpeedMultiplier { get; set; }
    public float? GlideDescentMultiplier { get; set; }
    public CharacterMovementSettings Clone() => (CharacterMovementSettings)MemberwiseClone();
}
