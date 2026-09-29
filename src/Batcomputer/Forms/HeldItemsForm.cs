namespace Batcomputer;

/// <summary>The extra-prop settings now share the model workspace instead of opening another editor.</summary>
public sealed class HeldItemSettingsForm : WeaponModelEditorForm
{
    public new HeldItemSettings Result => PropResult!;
    public HeldItemSettingsForm(HeldItemSettings settings)
        : base(settings.MeshPackage, settings.CustomModel, extraProp: settings) { }
}
