namespace Batcomputer;

internal static class ItemWorkshopRegressionChecks
{
    internal static IReadOnlyList<(bool Passed, string Description)> Run()
    {
        var results = new List<(bool, string)>();
        var recipe = new WeaponModelRecipe { SourceName = "claws.obj", ObjText = "fixture", Scale = 4, X = 20, Y = -.3f, Z = 3, Yaw = 90,
            Materials = [new() { Slot = 0, SourceMaterialName = "MB_5", StableSlotName = "MB_5", MaterialPath = "/Game/Models/Gadgets/GA_Claw_CatWoman/Mi_Claw_CatWoman" }] };
        var copy = recipe.Clone(); copy.Materials[0].MaterialPath = "/Game/Mods/Fixture/MI_Claws";
        results.Add((recipe.Materials[0].MaterialPath != copy.Materials[0].MaterialPath && copy.Scale == 4 && copy.X == 20 && copy.Yaw == 90,
            "item editing clones slot assignments without changing saved alignment or source material identities"));
        results.Add((HeldItemsForm.Friendly("/Game/Models/Gadgets/BP_Claw_Catwoman") == "Claw Catwoman",
            "held-item browser presents friendly labels while keeping package identity separate"));
        var source = System.Text.Encoding.UTF8.GetString(EmbeddedAssets.ReadBytes("preview/ItemWorkshopViewer.js") ?? []);
        results.Add((source.Contains("BC_SLOT_") && source.Contains("BatcomputerCharacterNormals") && source.Contains("BatcomputerCharacterSurface") &&
            source.Contains("THREE.LinearEncoding") && source.Contains("THREE.sRGBEncoding") && source.Contains("normalScale.set(1,-1)"),
            "item viewer uses stable OBJ slots, shared LEGO/micro/RAO shaders and correct color/data normal conventions"));
        results.Add((source.Contains("s.originalSlots?.[i]||slot") && source.Contains("scene.environment = environment.texture") && source.Contains("firstCustom"),
            "item preview restores unassigned native slots, supplies metallic reflections and frames the first imported model"));
        results.Add((source.Contains("THREE.TransformControls") && source.Contains("commitTransform()") && source.Contains("pending.revision") && source.Contains("controls.enabled = !dragging") && source.Contains("setTranslationSnap"),
            "item viewers share draggable move/rotate/uniform-size controls with snap, gesture commits and stale-update guards"));
        results.Add(((EmbeddedAssets.ReadBytes("preview/ItemTransformMath.js")?.Length ?? 0) > 0 && source.Contains("previewFailed(message)") && source.Contains("!h.name.startsWith('XYZ')"),
            "shared item transform math is embedded, sizing excludes nonuniform plane handles, and preview failures unlock editing"));
        var message = "{\"type\":\"item-workshop-transform\",\"target\":\"custom\",\"index\":-1,\"revision\":0,\"transform\":{\"scale\":2.5,\"offset\":[3,-4,5],\"rotation\":[20,35,10]}}";
        results.Add((ItemWorkshopTransform.TryParse(message, out var gesture) && !gesture.Effect && gesture.Scale == 2.5f && gesture.Offset[1] == -4,
            "gizmo messages retain Unreal centimeters, signed axes and all three rotation fields"));
        results.Add((!ItemWorkshopTransform.TryParse(message.Replace("2.5", "-2.5"), out _) &&
            !ItemWorkshopTransform.TryParse(message.Replace("[3,-4,5]", "[3,4]"), out _) &&
            !ItemWorkshopTransform.TryParse(message.Replace("35,10", "361,10"), out _) &&
            !ItemWorkshopTransform.TryParse(message.Replace("\"custom\"", "\"native\""), out _) &&
            !ItemWorkshopTransform.TryParse("{}", out _), "gizmo messages reject malformed, negative, unbounded and native-mesh edits"));
        results.Add((ItemWorkshopTransform.TryParse(message.Replace("\"custom\"", "\"effect\"").Replace("\"index\":-1", "\"index\":0"), out var effectGesture) && effectGesture.Effect &&
            !ItemWorkshopTransform.TryParse(message.Replace("\"custom\"", "\"effect\"").Replace("\"index\":-1", "\"index\":3"), out _),
            "effect gizmo messages identify a bounded effect index independently of custom OBJ alignment"));
        var scratch = Path.Combine(Path.GetTempPath(), "Batcomputer-item-import-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            var path = Path.Combine(scratch, "claws.obj");
            var obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nusemtl MB_5\nf 1 2 3\nusemtl Handle\nf 1 3 4\n";
            File.WriteAllText(path, obj);
            var imported = WeaponModelService.ImportObj(path, null, [(0, "Claws", "/Game/Native/MI_Claw"), (1, "Handle", "/Game/Native/MI_Handle")]);
            results.Add((imported.ObjText == obj && imported.SourceName == "claws.obj" && imported.Materials.Count == 2 &&
                imported.Materials[0].MaterialPath == "/Game/Native/MI_Claw" && imported.Materials[1].MaterialPath == "/Game/Native/MI_Handle",
                "direct OBJ import prepares geometry and per-slot native materials without opening a workshop"));
            var baseFile = WeaponModelService.WritePreviewGeometry(imported, scratch);
            var baseBytes = File.ReadAllBytes(Path.Combine(scratch, baseFile));
            imported.Scale = 4; imported.X = 20; imported.Yaw = 90; imported.Materials[0].MaterialPath = "/Game/Mods/Fixture/MI_Claws";
            results.Add((baseFile == WeaponModelService.WritePreviewGeometry(imported, scratch) && baseBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(scratch, baseFile))),
                "alignment and material-package edits reuse centered unit-scale geometry instead of exporting on every gesture"));
            var moved = imported.Clone(); moved.Scale = 4; moved.X = 20; moved.Y = -3; moved.Z = 7; moved.Pitch = 20; moved.Yaw = 35; moved.Roll = 10;
            var bakedPreview = Path.Combine(scratch, "baked.glb");
            StaticMeshObjProbeService.WritePreviewGlb(path, bakedPreview, moved.Scale, moved.X, moved.Y, moved.Z, moved.Pitch, moved.Yaw, moved.Roll, moved.Materials);
            var positions = Positions(baseBytes); var expected = Positions(File.ReadAllBytes(bakedPreview));
            const float rad = MathF.PI / 180;
            var q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, moved.Yaw * rad) *
                System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, moved.Pitch * rad) *
                System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, moved.Roll * rad);
            var matches = positions.Count == expected.Count;
            for (var i = 0; i < positions.Count && matches; i++)
            {
                var p = positions[i]; var ue = new System.Numerics.Vector3(p.X, -p.Z, p.Y) * moved.Scale;
                ue = System.Numerics.Vector3.Transform(ue, q) + new System.Numerics.Vector3(moved.X, moved.Y, moved.Z) / 100;
                matches &= System.Numerics.Vector3.Distance(new(ue.X, ue.Z, -ue.Y), expected[i]) < .00001f;
            }
            results.Add((matches, "unit preview geometry plus a scene transform exactly matches baked OBJ centering, rotation order and signed UE basis"));
            File.WriteAllText(path, obj.Replace("usemtl MB_5\nf 1 2 3\nusemtl Handle\nf 1 3 4", "usemtl Handle\nf 1 3 4\nusemtl MB_5\nf 1 2 3"));
            var replacement = WeaponModelService.ImportObj(path, imported, []);
            results.Add((replacement.Materials[1].MaterialPath == "/Game/Mods/Fixture/MI_Claws" && replacement.Scale == 4 && replacement.X == 20 && replacement.Yaw == 90 &&
                imported.ObjText == obj && imported.Materials[0].SourceMaterialName == "MB_5",
                "replacing an OBJ keeps alignment and material identities when slots reorder without changing the prior draft"));
            File.WriteAllText(path, "not an OBJ");
            var rejected = false; try { WeaponModelService.ImportObj(path, imported, []); } catch (InvalidOperationException) { rejected = true; }
            results.Add((rejected && imported.ObjText == obj, "invalid OBJ import leaves the existing draft untouched"));
        }
        catch (Exception ex) { results.Add((false, "direct OBJ import: " + ex.Message)); }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new ItemMaterialSlotsControl(true);
                control.SetSlots([(0, "Claws", "/Game/Native/MI_Claw"), (1, "Handle", "/Game/Native/MI_Handle")], [new() { Slot = 1, Package = "/Game/Mods/Fixture/MI_Handle" }]);
                results.Add((control.Overrides.Count == 1 && control.Overrides[0].Slot == 1,
                    "native material UI keeps inherited defaults separate from explicit per-slot overrides"));
                control.SetSlots([(0, "Claws", "/Game/Native/MI_Claw")], [new() { Slot = 3, Package = "/Game/Mods/Fixture/MI_Saved" }]);
                results.Add((control.Overrides.Count == 1 && control.Overrides[0].Slot == 3,
                    "material browser preserves saved unmatched slots for validation instead of silently deleting them"));
                var changed = 0; control.Changed += () => changed++;
                control.ApplyMaterial(3, "/Game/Mods/Fixture/MI_Own");
                results.Add((control.Overrides.Single().Package == "/Game/Mods/Fixture/MI_Own" && changed == 1 &&
                    Descendants(control).OfType<CheckBox>().Any(c => c.Name == "OwnMaterialsFilter"),
                    "material assignment has a direct own-material filter and updates the exact selected surface"));
                using var picker = new ItemMaterialPickerForm("/Game/Mods/Fixture/MI_Own", ownOnly: true);
                results.Add((Descendants(picker).OfType<ThemedDropDown>().Single().SelectedItem?.ToString() == "Your materials",
                    "own-material assignment filter opens the picker scoped to the user's library"));
                typeof(ItemMaterialPickerForm).GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(picker,
                    new List<(string Name, string Path, string Source)> { ("Own claws", "/Game/Mods/Fixture/MI_Own", "Your materials"), ("Native metal", "/Game/Native/MI_Metal", "Game materials") });
                typeof(ItemMaterialPickerForm).GetMethod("Filter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(picker, null);
                var pickerRows = Descendants(picker).OfType<ListView>().Single();
                results.Add((pickerRows.Items.Count == 1 && pickerRows.Items[0].Text == "Own claws",
                    "own-material filter excludes game entries while retaining exact user package identity"));
                Descendants(picker).OfType<ThemedDropDown>().Single().SelectedIndex = 0;
                results.Add((pickerRows.Items.Count == 2, "material picker can switch back to all materials without discarding either library"));
                using var weapon = new WeaponModelEditorForm("/Game/Models/Props/SM_Katana", recipe);
                results.Add((weapon.Result is null && recipe.Materials[0].MaterialPath.EndsWith("Mi_Claw_CatWoman"),
                    "opening a weapon workshop does not accept a recipe or mutate its caller"));
                results.Add((Descendants(weapon).OfType<CheckBox>().Single(c => c.Text == "Original model").Checked,
                    "saved custom models open with the original visible for comparison rather than silently hiding it"));
                weapon.ApplyViewerTransform(gesture);
                results.Add((weapon.ReadRecipe().Scale == 2.5f && weapon.ReadRecipe().Y == -4 && weapon.ReadRecipe().Pitch == 20 && recipe.Scale == 4,
                    "workshop gizmo commits flow into the validated bake recipe without mutating the caller"));
                weapon.ApplyViewerTransform(gesture with { Revision = 99, Scale = 7 });
                results.Add((weapon.ReadRecipe().Scale == 2.5f, "workshop rejects stale gizmo revisions without overwriting newer numeric alignment"));
                Descendants(weapon).OfType<ThemedDropDown>().Single(c => c.Name == "ImportUnits").SelectedIndex = 1;
                Click(Descendants(weapon).OfType<Button>().Single(b => b.Name == "ImportUnitsApply"));
                var scaledImport = weapon.ReadRecipe();
                results.Add((scaledImport.Scale == 100 && scaledImport.X == 3 && scaledImport.Y == -4 && scaledImport.Pitch == 20 && recipe.Scale == 4,
                    "explicit import-unit scale changes only the private size multiplier, not offsets, rotations or the saved source"));
                var identity = new NativeHeldItemEdit { ActorPackage = "/Game/Actors/BP_Claws", OriginalMeshPackage = "/Game/Meshes/SM_Claws" };
                var saved = identity.Clone(); saved.CustomModel = recipe.Clone(); saved.Transform = new() { X = 7 };
                var binding = new NativeHeldItemService.Binding(identity, "LAM.RightHand", "Native", true, "");
                using var native = new NativeHeldItemSettingsForm(binding, saved);
                var model = native.ReadCustomModel()!;
                results.Add((model.Scale == 4 && model.X == 20 && model.Yaw == 90 && model.Materials[0].MaterialPath.EndsWith("Mi_Claw_CatWoman") &&
                    native.Result.Transform!.X == 7 && !ReferenceEquals(model, saved.CustomModel),
                    "same-window held-item editing loads custom alignment and materials independently of native placement"));
                var alignment = (List<NumericUpDown>)typeof(NativeHeldItemSettingsForm).GetField("_alignment", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
                alignment[1].Value = 30;
                results.Add((native.ReadCustomModel()!.X == 30 && saved.CustomModel!.X == 20 && native.Result.CustomModel!.X == 20 && native.Result.Transform!.X == 7,
                    "inline OBJ alignment updates the preview recipe without accepting changes or moving native placement"));
                var materials = (ItemMaterialSlotsControl)typeof(NativeHeldItemSettingsForm).GetField("_customMaterials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
                materials.SetSlots([(0, "MB_5", "/Game/Native/MI_Claw")], [new() { Slot = 0, Package = "/Game/Mods/Fixture/MI_NewClaws" }]);
                results.Add((materials.Enabled && native.ReadCustomModel()!.Materials[0].MaterialPath == "/Game/Mods/Fixture/MI_NewClaws" && saved.CustomModel!.Materials[0].MaterialPath.EndsWith("Mi_Claw_CatWoman"),
                    "same-window material selection updates the custom recipe and keeps the saved caller unchanged"));
                native.ApplyViewerTransform(gesture);
                results.Add((native.ReadCustomModel()!.Scale == 2.5f && native.ReadCustomModel()!.Z == 5 && native.Result.Transform!.X == 7 && saved.CustomModel!.X == 20,
                    "inline held-item gizmos change only the private OBJ alignment, not native placement or the saved source"));
                var effectItem = new HeldItemSettings { Effects = [new() { X = 1 }] };
                using var effects = new HeldItemEffectsForm(effectItem);
                effects.ApplyViewerTransform(effectGesture);
                results.Add((effects.Result[0].Scale == 2.5f && effects.Result[0].Y == -4 && effectItem.Effects[0].X == 1,
                    "selected effect gizmos update private placement and sizing without moving the source weapon or saved effect"));
                using var equipment = new WeaponModelEditorForm("/Game/Models/Props/SM_Katana", recipe, true);
                var propSource = new HeldItemSettings { Name = "TestClaws", Hand = HeldItemHand.Both, TemplateId = "plant-spray", MeshPackage = "/Game/Models/Gadgets/GA_PlantSpray/SM_PlantSpray", CustomModel = recipe.Clone(), MaterialPackage = "/Game/Mods/Fixture/MI_Own", Effects = [new()] };
                using var prop = new HeldItemSettingsForm(propSource);
                results.Add((Descendants(prop).OfType<ModelPreviewControl>().Count() == 1 && prop.ReadProp(prop.ReadRecipe()).Hand == HeldItemHand.Both &&
                    prop.ReadProp(prop.ReadRecipe()).Name == "TestClaws" && prop.ReadProp(prop.ReadRecipe()).Effects.Count == 1 && prop.Result.CustomModel!.Scale == 4,
                    "extra props use one inline model workspace while preserving name, both hands, effects and saved alignment"));
                results.Add((prop.ReadRecipe().Materials.Single(m => m.Slot == 0).MaterialPath == "/Game/Mods/Fixture/MI_Own" && prop.ReadProp(prop.ReadRecipe()).MaterialPackage == "" &&
                    propSource.MaterialPackage == "/Game/Mods/Fixture/MI_Own" && propSource.CustomModel!.Materials[0].MaterialPath.EndsWith("Mi_Claw_CatWoman"),
                    "legacy prop slot-zero overrides edit their effective model surface privately without a hidden post-bake override"));
                var donorChoice = Descendants(prop).OfType<ThemedDropDown>().Single(d => d.Items.Contains("Sword / katana")); donorChoice.SelectedIndex = 0;
                var changedProp = prop.ReadProp(prop.ReadRecipe());
                results.Add((changedProp.TemplateId == "sword" && changedProp.MeshPackage == "/Game/Models/Props/SM_Katana" && changedProp.Name == "TestClaws" &&
                    changedProp.CustomModel!.Scale == 4 && propSource.TemplateId == "plant-spray" && propSource.MeshPackage.Contains("PlantSpray"),
                    "changing a prop donor retains its imported model/name and changes only the private actor/mesh choice"));
                var simpleSource = new HeldItemSettings();
                using var simpleProp = new HeldItemSettingsForm(simpleSource);
                var priorityToggle = Descendants(simpleProp).OfType<CheckBox>().Single(c => c.Text == "Keep prop over animation empty-hand requests");
                results.Add((!priorityToggle.Checked && !simpleProp.ReadProp(null).PreferOverAnimationEmptyHands,
                    "extra prop priority control defaults to native behavior for existing and new ordinary props"));
                priorityToggle.Checked = true;
                Descendants(simpleProp).OfType<ThemedDropDown>().Single(d => d.Items.Contains("Both hands")).SelectedIndex = 2;
                Click((Button)simpleProp.AcceptButton!);
                results.Add((simpleProp.DialogResult == DialogResult.OK && simpleProp.Result.Hand == HeldItemHand.Both && simpleSource.Hand == HeldItemHand.Right,
                    "inline extra-prop acceptance saves Both hands through the actual button without mutating the caller"));
                results.Add((simpleProp.Result.PreferOverAnimationEmptyHands && !simpleSource.PreferOverAnimationEmptyHands,
                    "actual prop acceptance retains the explicit claw-priority opt-in without mutating the saved caller"));
                using var reopened = new HeldItemSettingsForm(simpleProp.Result);
                results.Add((Descendants(reopened).OfType<ThemedDropDown>().Single(d => d.Items.Contains("Both hands")).SelectedItem?.ToString() == "Both hands",
                    "accepted inline prop retains its Both hands selection when reopened"));
                results.Add((Descendants(reopened).OfType<CheckBox>().Single(c => c.Text == "Keep prop over animation empty-hand requests").Checked,
                    "reopened claw props retain the explicit animation empty-hand priority setting"));
                using var browser = new HeldItemsForm([propSource]);
                foreach (var form in new Form[] { weapon, equipment, native, effects, prop, browser })
                {
                    form.MinimumSize = Size.Empty; form.ClientSize = new(1040, 650); LayoutTree(form);
                    results.Add((FooterFits(form), form.Text + " footer keeps status and action text within a compact client area"));
                    form.Scale(new SizeF(1.5f, 1.5f)); form.ClientSize = new(1560, 975); LayoutTree(form);
                    results.Add((FooterFits(form), form.Text + " footer grows with scaled controls instead of clipping button captions"));
                    var footer = ((TableLayoutPanel)form.Controls[0]).Controls.OfType<TableLayoutPanel>().Single(c => c.Name == "WorkshopFooter"); var height = footer.Height;
                    var status = Descendants(footer).OfType<Label>().Single(); status.Text = string.Join(" ", Enumerable.Repeat("Long preview diagnostic", 40)); LayoutTree(form);
                    results.Add((footer.Height == height && FooterFits(form), form.Text + " status does not expand the footer or steal space from the viewer"));
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) results.Add((false, "item workshop UI construction: " + error.Message));
        return results;
    }
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    // Exercise our own click handler without opening or automating a user window.
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);

    private static List<System.Numerics.Vector3> Positions(byte[] glb)
    {
        var length = BitConverter.ToInt32(glb, 12); using var d = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, length)); var root = d.RootElement;
        var a = root.GetProperty("accessors")[0]; var view = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        var start = 28 + length + (view.TryGetProperty("byteOffset", out var offset) ? offset.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out offset) ? offset.GetInt32() : 0);
        return Enumerable.Range(0, a.GetProperty("count").GetInt32()).Select(i => new System.Numerics.Vector3(
            BitConverter.ToSingle(glb, start + i * 12), BitConverter.ToSingle(glb, start + i * 12 + 4), BitConverter.ToSingle(glb, start + i * 12 + 8))).ToList();
    }
    private static void LayoutTree(Control control)
    {
        control.PerformLayout(); foreach (Control child in control.Controls) LayoutTree(child);
    }
    private static bool FooterFits(Form form)
    {
        var root = (TableLayoutPanel)form.Controls[0]; var footer = root.Controls.OfType<TableLayoutPanel>().Single(c => c.Name == "WorkshopFooter");
        if (footer.Bottom > root.ClientSize.Height - root.Padding.Bottom || footer.Height <= 0) return false;
        return footer.Controls.Cast<Control>().All(c => c.Bottom <= footer.ClientSize.Height - footer.Padding.Bottom && c.Right <= footer.ClientSize.Width &&
            c.Height >= (c is Button b ? b.GetPreferredSize(Size.Empty).Height : c.Font.Height));
    }
}
