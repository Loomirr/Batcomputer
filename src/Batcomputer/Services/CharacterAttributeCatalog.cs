namespace Batcomputer;

/// <summary>Explicit native field bindings and bounded editor values; never arbitrary user paths.</summary>
internal static class CharacterAttributeCatalog
{
    internal enum ValueKind { Multiplier, Points, Seconds, FocusUnits }
    internal sealed record Definition(string Id, string Label, string Group, string ScriptPackage, string OwnerClass,
        ValueKind Kind, float Minimum, float Maximum, float Initial, string Detail)
    {
        internal string Operation => Kind == ValueKind.Multiplier ? "MultiplyCompound" : Kind == ValueKind.Points ? "Additive" : "Override";
        internal string Units => Kind switch { ValueKind.Multiplier => "%", ValueKind.Points => "points", ValueKind.Seconds => "seconds", _ => "Focus units" };
        internal float DisplayScale => Kind == ValueKind.Multiplier ? 100 : 1;
    }
    private const string Character = "/Script/TtGameplayAbilities";
    private static Definition Percent(string id, string label, string group, string script, string owner, string detail, float min = .25f, float max = 3) =>
        new(id, label, group, script, owner, ValueKind.Multiplier, min, max, 1, detail);
    private static Definition Points(string id, string label, string group, string script, string owner, string detail) =>
        new(id, label, group, script, owner, ValueKind.Points, 0, 200, 25, detail);
    internal static IReadOnlyList<Definition> All { get; } = new Definition[] {
        Percent("MaxAcceleration", "Ground acceleration", "Ground", Character, "CharacterAttributeSet", "How quickly ground movement reaches its target speed."),
        Percent("BrakingDecelerationWalking", "Ground braking", "Ground", Character, "CharacterAttributeSet", "How quickly the character slows after movement input stops."),
        Percent("MaxWalkSpeedCrouched", "Crouch speed", "Ground", Character, "CharacterAttributeSet", "Speed while the native crouched movement mode is active."),
        Percent("GroundFriction", "Ground friction", "Ground", Character, "CharacterAttributeSet", "Turning and slowing friction on the ground. Lower values feel more slippery."),
        Percent("AirControl", "Air control", "Air", Character, "CharacterAttributeSet", "Steering while falling; native movement can clamp the resulting value.", max: 4),
        Percent("GravityScale", "Gravity", "Air", Character, "CharacterAttributeSet", "Normal airborne gravity. This also changes jump height and airtime.", max: 2),
        Percent("GravityScaleDescending", "Descending gravity", "Air", Character, "CharacterAttributeSet", "The separate gravity value used during descent when supported by the movement mode.", max: 2),
        Percent("TerminalVelocity", "Maximum fall speed", "Air", Character, "CharacterAttributeSet", "Terminal falling speed, subject to the game's fall recovery and collision rules."),
        Percent("GrappleMovementSpeed", "Grapple travel speed", "Grapple", "/Script/Grappling", "GrappleAttributeSet", "Travel speed during native grappling; does not change target range or grant the gadget."),
        Percent("GrappleMovementMaxAcceleration", "Grapple acceleration", "Grapple", "/Script/Grappling", "GrappleAttributeSet", "Acceleration during grapple travel."),
        Percent("GrappleMovementDamping", "Grapple damping", "Grapple", "/Script/Grappling", "GrappleAttributeSet", "Damping of grapple movement. Test transitions and arrival at the ledge."),
        Percent("GrappleMovementDragK1", "Grapple linear drag", "Grapple", "/Script/Grappling", "GrappleAttributeSet", "Linear resistance during grapple travel."),
        Percent("GrappleMovementDragK2", "Grapple quadratic drag", "Grapple", "/Script/Grappling", "GrappleAttributeSet", "Speed-dependent grapple resistance."),
        Percent("GlideAcceleration", "Glide acceleration", "Glide handling", "/Script/GlidingRuntime", "GlidingAttributeSet", "How quickly the native glide accelerates."),
        Percent("GlideBrakingDeceleration", "Glide braking", "Glide handling", "/Script/GlidingRuntime", "GlidingAttributeSet", "How quickly the native glide decelerates."),
        Percent("GlideMaxStrafeVelocity", "Glide sideways speed", "Glide handling", "/Script/GlidingRuntime", "GlidingAttributeSet", "Maximum sideways glide movement."),
        Percent("GlideStrafeVelocityInterp", "Glide sideways response", "Glide handling", "/Script/GlidingRuntime", "GlidingAttributeSet", "Response rate for sideways glide movement."),
        Percent("GlideMaxSteeringSpeedAtLowSpeed", "Glide slow-speed steering", "Glide handling", "/Script/GlidingRuntime", "GlidingAttributeSet", "Steering speed while gliding slowly."),
        Percent("GlideMaxSteeringSpeedAtHighSpeed", "Glide high-speed steering", "Glide handling", "/Script/GlidingRuntime", "GlidingAttributeSet", "Steering speed while gliding quickly."),
        Percent("MaxHealth", "Maximum health", "Health & combat", Character, "HealthAttributeSet", "Maximum health capacity. This does not instantly heal or alter the current health resource.", min: .5f),
        Points("DefensePower", "Additional defense power", "Health & combat", Character, "HealthAttributeSet", "Adds native defense points; the game's damage calculation determines the reduction."),
        Percent("BaseDamage", "Base damage", "Health & combat", Character, "CombatAttributeSet", "Scales base damage used by compatible attacks. Authored attack and difficulty rules still apply."),
        Points("AttackPower", "Additional attack power", "Health & combat", Character, "CombatAttributeSet", "Adds native attack-power points; it is not an attack animation speed control."),
        Points("CritDamagePower", "Additional critical power", "Health & combat", Character, "CombatAttributeSet", "Adds critical-damage power. Does not grant critical hits or change their chance."),
        new("TakeHitStunDuration", "Incoming hit stun", "Health & combat", Character, "CombatAttributeSet", ValueKind.Seconds, .1f, 5, .5f,
            "This character's stun duration after a compatible incoming hit. Enemy attack-specific reactions can differ."),
        new("TakedownCost", "Takedown Focus cost", "Health & combat", Character, "CombatAttributeSet", ValueKind.FocusUnits, .1f, 10, 1,
            "Sets the native takedown-cost attribute. Unlocks, activation requirements and scripted takedowns remain native."),
        Percent("FocusCapacity", "Focus capacity", "Focus", "/Script/Focus", "FocusAttributeSet", "Scales Focus capacity. Does not fill the meter or grant upgrades.", min: .5f),
        Points("FocusPowerCombat", "Additional combat Focus power", "Focus", "/Script/Focus", "FocusAttributeSet", "Adds power used by native combat Focus rewards; reward conversion remains game-controlled."),
        Points("FocusPowerTraversal", "Additional traversal Focus power", "Focus", "/Script/Focus", "FocusAttributeSet", "Adds power used by native traversal Focus rewards such as supported parkour actions."),
    };
    private static readonly IReadOnlyDictionary<string, Definition> Index = All.ToDictionary(d => d.Id, StringComparer.Ordinal);
    internal static Definition? Find(string id) => Index.GetValueOrDefault(id);
    internal static IEnumerable<(Definition Definition, float Value)> Selected(CharacterMovementSettings? settings) =>
        All.Where(d => settings?.AttributeOverrides?.ContainsKey(d.Id) == true).Select(d => (d, settings!.AttributeOverrides![d.Id]));
    internal static string? ValidationError(CharacterMovementSettings? settings)
    {
        foreach (var (id, value) in settings?.AttributeOverrides ?? [])
        {
            if (Find(id) is not { } definition) return "Unsupported character attribute: " + id;
            if (!float.IsFinite(value) || value < definition.Minimum || value > definition.Maximum)
                return $"{definition.Label} must be between {definition.Minimum * definition.DisplayScale:0.##} and {definition.Maximum * definition.DisplayScale:0.##} {definition.Units}.";
        }
        return null;
    }
}
