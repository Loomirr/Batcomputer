using System.Text.Json;

namespace Batcomputer;

public sealed class VehicleProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "V" + Guid.NewGuid().ToString("N")[..16];
    public string DisplayName { get; set; } = "New vehicle";
    public string OwnerTag { get; set; } = "Pawns.Playable.Batman";
    // Required for a custom owner; its default character is included with the vehicle.
    public string OwnerCharacterProjectPath { get; set; } = "";
    public string DonorId { get; set; } = "batmobile1995";
    public SkinnedMeshImport? Model { get; set; }
    public List<VehicleComponentTransform> Transforms { get; set; } = [];
    public List<VehiclePaletteColor> Palette { get; set; } = [];
    public List<VehicleMaterialOverride> MaterialOverrides { get; set; } = [];
    public List<string> DisabledParts { get; set; } = [];
    public List<VehicleLightSettings> Lights { get; set; } = [];
    public VehicleRgbColor? AccentColor { get; set; }
    public List<VehicleLightSurface> LightSurfaces { get; set; } = [];
    public VehicleProject Clone() => JsonSerializer.Deserialize<VehicleProject>(JsonSerializer.Serialize(this))!;
}

/// <summary>A rigid body material section routed through an existing native lamp controller.</summary>
public sealed class VehicleLightSurface
{
    public int Slot { get; set; }
    public string Role { get; set; } = "";
}

public sealed class VehicleRgbColor
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
}

/// <summary>Actual spotlight sources, not the decorative bulb/glow meshes. Colors are sRGB bytes.</summary>
public sealed class VehicleLightSettings
{
    public string Component { get; set; } = "";
    public int R { get; set; } = 255;
    public int G { get; set; } = 255;
    public int B { get; set; } = 255;
    public float Intensity { get; set; } = 128;
    public float Radius { get; set; } = 2400;
    public float OuterCone { get; set; } = 60;
}

public sealed class VehicleMaterialOverride
{
    public string Component { get; set; } = "body";
    public int Slot { get; set; }
    public string MaterialPath { get; set; } = "";
}

public sealed class ModVehicleEntry
{
    public string VehicleProjectPath { get; set; } = "";
    public string VehicleId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class VehicleComponentTransform
{
    public string Component { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public float Roll { get; set; }
    public float ScaleX { get; set; } = 1;
    public float ScaleY { get; set; } = 1;
    public float ScaleZ { get; set; } = 1;
}

/// <summary>Optional simple body colors, stored in linear space like native material parameters.</summary>
public sealed class VehiclePaletteColor
{
    public int Slot { get; set; }
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
}
