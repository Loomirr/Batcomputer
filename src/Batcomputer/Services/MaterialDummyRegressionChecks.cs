using System.Reflection;

namespace Batcomputer;

internal static class MaterialDummyRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var checks = new List<(bool, string)>();
        var eom = new MaterialGenService.MaterialTemplateInfo { ParentMaterialPath = "/Game/Materials/M_Char_EoM_Master" };
        string? Leaf(string p, MaterialGenService.MaterialTemplateInfo? m) => MaterialDummyTextureService.Resolve(p, m) is { } c ? UnrealPathUtil.AssetName(c.ObjectPath) : null;
        checks.Add((Leaf("CT", eom) == "T_Dummy_CTUV" && Leaf("ctuv", eom) == "T_Dummy_CTUV" && Leaf("RAO", eom) == "T_Dummy_RAO" &&
            Leaf("NRM", eom) == "T_Dummy_Norm" && Leaf("DNRM", eom) == "T_Dummy_Norm" && Leaf("MicroNoise", eom) == "T_Dummy_Norm" &&
            Leaf("MMR", eom) == "T_Dummy_MMR" && Leaf("MMR_Pristine", eom) == "T_Dummy_MMR", "dummy selection keeps CT, RAO, normals and packed MMR separate and uses shipped EoM placeholders"));
        checks.Add((Leaf("ColourMask", eom) == "T_Dummy_Black_BC" && Leaf("SwapColourID", eom) == "T_Dummy_Black_BC" &&
            Leaf("BC", eom) == "T_Dummy_White_BC" && Leaf("PM_Emissive", eom) == "T_Dummy_E", "colour-ID, base-colour and emissive dummy inputs retain their distinct neutral roles"));
        var face = new MaterialGenService.MaterialTemplateInfo { ParentMaterialPath = "/Game/Materials/M_LEGOface" };
        checks.Add((Leaf("BrowL NML", face) == "T_Dummy_NML" && Leaf("LashL NML", face) == "T_Dummy_NML" &&
            Leaf("LashL BC", face) is null && Leaf("Eye L BC", face) is null,
            "LEGOface normals use the face dummy while shared eye/lash artwork is left to the face visibility helpers"));
        checks.Add((Leaf("ORM", eom) is null && Leaf("Alpha", eom) is null && Leaf("CT", null) is null &&
            Leaf("RAO", new() { ParentMaterialPath = "/Game/M_LEGO08_Cape" }) is null,
            "dummy selection refuses unknown masks, packing and legacy shader families instead of guessing"));
        face.TextureParams.Add(new() { Name = "EyelidUpperL BC", ObjectPath = "/Game/Characters/Textures/Shared/EoM/T_Dummy_Alpha_Off.T_Dummy_Alpha_Off" });
        checks.Add((Leaf("EyelidUpperL BC", face) == "T_Dummy_Alpha_Off", "an existing typed native dummy is reused exactly rather than being replaced with another packing"));
        var noClears = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flat = new Dictionary<string, string> { ["NRM"] = "/Game/Characters/Textures/Shared/EoM/T_Dummy_Norm.T_Dummy_Norm", ["DNRM"] = "/Game/Characters/Textures/Shared/EoM/T_Dummy_Norm.T_Dummy_Norm" };
        checks.Add((MaterialWizard.FindDuplicatedEffectiveNormalParameters([], flat, noClears).Count == 0 &&
            !MaterialDummyTextureService.IsFlatNormal("/Game/Mods/Fixture/T_Dummy_Norm"),
            "shared native flat normals do not trigger a false doubled-detail warning; same-named mod textures are not trusted as dummies"));
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MaterialWizard(Path.GetTempPath(), "Fixture", "MI_DummyCheck");
                typeof(MaterialWizard).GetField("_lastTemplateInfo", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, eom);
                var grid = (DataGridView)typeof(MaterialWizard).GetField("_grid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
                grid.Rows.Add("Texture", "CT", "Donor CT", "");
                grid.Rows.Add("Texture", "RAO", "Donor RAO", "/Game/Mods/Fixture/T_Rao");
                grid.Rows.Add("Scalar", "CT", "0", "0.25");
                grid.Rows.Add("Texture", "ORM", "Unsupported packing", "/Game/Mods/Fixture/T_Orm");
                grid.CurrentCell = grid.Rows[0].Cells["Override"];
                checks.Add((form.SetSelectedTextureDummy() && grid.Rows[0].Cells["Override"].Value?.ToString()?.EndsWith("T_Dummy_CTUV.T_Dummy_CTUV") == true &&
                    grid.Rows[1].Cells["Override"].Value?.ToString() == "/Game/Mods/Fixture/T_Rao" && form.ResultMiPackagePath is null,
                    "Set dummy edits only the selected override as a real texture reference and does not generate or mutate another input"));
                typeof(MaterialWizard).GetMethod("ClearSelectedTextureParam", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
                checks.Add((grid.Rows[0].Cells["Override"].Value?.ToString() == "(None - clear texture)", "Set None remains a separate explicit-null action after Set dummy"));
                grid.CurrentCell = grid.Rows[2].Cells["Override"];
                checks.Add((!form.SetSelectedTextureDummy() && grid.Rows[2].Cells["Override"].Value?.ToString() == "0.25", "Set dummy cannot overwrite a scalar row"));
                grid.CurrentCell = grid.Rows[3].Cells["Override"];
                checks.Add((!form.SetSelectedTextureDummy() && grid.Rows[3].Cells["Override"].Value?.ToString() == "/Game/Mods/Fixture/T_Orm", "an unsupported dummy action preserves the existing authored texture override"));
                foreach (var size in new[] { new Size(720, 560), new Size(1080, 840) })
                {
                    if (size.Width == 1080) form.Scale(new SizeF(1.5f, 1.5f));
                    form.ClientSize = size; Layout(form);
                    var tools = Descendants(form).OfType<TableLayoutPanel>().Single(c => c.Name == "TextureAssignmentTools");
                    checks.Add((tools.Controls.Cast<Control>().All(c => c.Bottom <= tools.ClientSize.Height && c.Right <= tools.ClientSize.Width && c.Width > 20) &&
                        tools.Controls.OfType<Button>().Single(b => b.Name == "SetDummyTexture").Height >= tools.Font.Height,
                        $"material texture action row fits at {size.Width}px without overlapping or clipping the new dummy button"));
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) checks.Add((false, "material dummy UI regression: " + error.Message));
        return checks;
    }
    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));
    private static void Layout(Control c) { c.PerformLayout(); foreach (Control child in c.Controls) Layout(child); }
}
