# Custom vehicles (experimental)

Make a separate selectable vehicle with a custom body, materials and editable attachment points. Choose the driving base before importing a model.

| Driving base | Workshop support |
| --- | --- |
| 1995 Batman Forever | Headlight, rear/accent and light-surface controls |
| 1997 Batman & Robin, Talia sports car | Headlight beam color and placement |
| 1989 Batmobile, 2005 Tumbler | Experimental; light controllers stay native |
| Batmobeast | Experimental; requires Party Pack DLC for the creator and players; light controllers stay native |
| 2022 Batbike | Experimental two-wheeler; uses a separate bike skeleton and front/rear wheel bones; light controllers stay native |

Start with the driving model. A LEGO build-up model is a separate asset with a different rig; see [summon and parked models](vehicle-summon-models.md) after the car drives correctly.

!!! warning "Experimental vehicle workflow"
    Vehicle authoring is included in 1.0. Native handling, collision and driving animations are retained. Moving the visible body or a seat does not rebuild the physics rig. Test each donor and model combination in-game.

## 1. Get the reference

Configure the game files, mappings and UE 5.6 in [Settings](../getting-started/setup.md). After the first full extraction, **Refresh game assets → Add vehicle driving bases (quick)** adds missing donor files to the active extract without rebuilding the character indexes.

1. Open **Vehicles → + New vehicle**.
2. In the workshop, open **Body & setup**. Set the name and owning character. Renaming keeps the vehicle's saved ID.
3. Open **Body & materials → Import / edit rigged FBX…**.
4. Choose **Export native reference + rig (GLB)…**. Import that GLB into Blender as the donor reference.
5. Save your own `.blend` copy. Keep the donor in a separate reference collection and do not export its geometry with the custom model.

Keep the donor's bone names, hierarchy, rest transforms and scale. Fit the mesh to the rig, not the rig to the mesh. Do not use automatic bone reorientation on the reference.

If an older exported GLB has displaced wheel bones or reversed bone rotations, export a fresh reference with the current tool. The corrected export keeps the native rest pose and mesh geometry; rotating bones by hand is not a substitute.

### Motion preview

Open **Motion** below the workshop viewport to play or scrub a canopy or launcher clip. Native clips are available for the Forever, 1989 and Tumbler bases. Wheel spin, steering and suspension tests appear when the rig has the matching bones. These are visual checks, not a simulation of game physics. Motion is never saved into the vehicle; **Rest** returns to the normal editing pose.

Check wheel pivots, tire clearance, canopy travel and parts attached to moving bones. The new driving bases still need in-game handling and animation tests with your model.

## 2. Fit the car in Blender

![Custom vehicle fitted to the driving rig](../assets/vehicles/driving-model.png){ .bc-doc-shot loading=lazy }

*Blender Workbench render of a prepared example. This is not an in-game material preview.*

Match these before adding details:

- **Wheel centers:** line up each tire and rim with its donor wheel pivot. The center of the visible wheel must be the center it rotates around.
- **Wheel size and ground contact:** stay close to the donor. A larger visible tire does not increase the physics wheel radius.
- **Body:** fit around those wheels. Check width, wheel arches and clearance from the ground.
- **Canopy and steering wheel:** align any moving pieces with their existing hinges/pivots.
- **Cockpit:** leave room for driver and passenger. The workshop can offset seats, but it cannot invent new entry animations or change the collision shape.

Use front, side and top views. Left/right always mean the vehicle's own left/right, not the side of the screen. Do not copy a scene-scale number from another Blender file: compare against the imported donor in the same scene.

## 3. Assign rigid weights

Most of the car is rigid. Give each complete LEGO piece **weight 1.0 to one bone**, with no weight on other bones. Automatic weights can make a panel bend or follow a nearby wheel.

For a new unweighted model, select the mesh and then the armature, and use **Parent → Armature Deform → With Empty Groups**. In mesh Edit Mode, select a complete piece, choose its bone-named vertex group, set Weight to `1.000`, and assign it. Remove conflicting bone-group weights. Existing correctly weighted models do not need parenting again. See [Blender's armature parenting guide](https://docs.blender.org/manual/en/5.0/animation/armatures/skinning/parenting.html).

![Driving-model weight regions: body, wheels and canopy](../assets/vehicles/driving-weights.png){ .bc-doc-shot loading=lazy }

*Illustration colors: blue-gray = Body, green = rotating wheels, cyan = Canopy. Interior steering and non-spinning wheel mounts also have their own assignments. These colors are not exported material requirements.*

### Batman Forever driving rig

| Bone | What belongs here |
| --- | --- |
| `Body` | Fixed body panels, bumpers, fixed windows, light lenses and fixed exhaust pieces. |
| `Wheel_FL`, `Wheel_FR` | Front-left and front-right rotating tires/rims. |
| `Wheel_BL`, `Wheel_BR` | Back-left and back-right rotating tires/rims. `B` means back, not brake. |
| `Canopy` | The opening canopy and anything that should open with it. |
| `SteeringWheel` | The interior steering wheel. |
| `ChassisAttach_FL`, `ChassisAttach_FR`, `ChassisAttach_BL`, `ChassisAttach_BR` | Non-spinning wheel-mount pieces where appropriate. Compare the donor; do not use these for the tires. |
| `Root`, `Chassis` | Preserve these hierarchy bones. Do not give them arbitrary weights just to fill every group. |

There are 13 native driving bones. Every exported vertex needs a valid assignment, but every bone does **not** need weighted geometry.

In Pose Mode, test one wheel, the steering wheel and the canopy separately. A wheel should spin around its center without pulling bodywork. A canopy should open without stretching. Clear the test pose before export; do not apply it as a new rest pose.

## 4. Split material and light regions

Give areas that need different materials separate material slots: body paint, glass, tires, metal, lenses and accents. Slots describe surfaces; vertex groups describe how those surfaces move. They do different jobs.

The exact slot names `LEGO_Solid`, `LEGO_Metallic` and `LEGO_Transparent` suggest the corresponding native shaders on a new import. These shaders use LEGO color data, not Blender's material color. Assign a color in **Body & materials** if a slot needs a particular hue.

For a lens that should inherit native light behavior, use a **lens-only material slot**, entirely weighted to `Body`. Helpful names include:

| Suggested slot name | Workshop role |
| --- | --- |
| `BC_Headlight_L`, `BC_Headlight_R` | Left/right headlights |
| `BC_BrakeLight_L`, `BC_BrakeLight_R` | Left/right brake lights |
| `BC_RearLight_L`, `BC_RearLight_R` | Left/right rear lights |
| `BC_Accent_01` through `BC_Accent_04` | Accent channels 1–4 |

These are **material slot names, not extra bones**. The names suggest roles; confirm them in the workshop. Each role and slot can be assigned once. Combine lenses that should share one role into the same slot. Moving-bone lenses and sections with 65,535 or more vertices are currently rejected.

Keep painted or permanently visible exhaust/flame geometry on its appropriate body bone. That alone does not make it appear only while boosting. The boost particle outlet is a separate attachment point.

## 5. Export and import the driving FBX

Export only the custom mesh and its complete donor armature as a **binary FBX**. Leave out reference meshes, lights, cameras and unrelated rigs. Disable extra leaf/end bones and animation baking. Keep all required native bones, including unweighted hierarchy bones. Preserve UVs and material slots.

Use unit and axis settings that preserve the donor rest pose. A GLB-to-Blender-to-FBX round trip can change bone axes or introduce an extra root; looking correct in the viewport is not enough. Import a small trial first. Batcomputer checks the cooked hierarchy and rest transforms against the actual game rig. If that fails, fix the export rather than renaming bones or moving the rig until it passes. Batcomputer's import scale must stay **1.0000**. Fit your geometry to the reference rather than compensating with 0.01 or 100.

When a cooked rig comparison fails, check `rig-comparison.json` beside the import logs. It lists the expected and actual parent and rest transform for each bone.

Back in the import window:

1. Choose **Import / replace weighted FBX…** and wait for validation.
2. Assign a valid cooked material to every slot. Blender materials do not become game shaders automatically.
3. Inspect the deformation preview, then choose **Use skinned mesh**.
4. Apply the setup and return to the vehicle workshop.

**Reimport saved FBX** cooks Batcomputer's saved copy. To bring in new Blender edits, choose **Import / replace weighted FBX…** and select the new export.

## 6. Finish in the 3D workshop

- **Materials:** click a surface, then **Change selected surface…**. Choose **Edit material** or **Create a copy**, set the color, and choose LEGO solid, metallic or transparent. **Keep current shader** preserves the current material; changing finish replaces its shader. **Choose from library…** opens Generated, Vehicle materials, Vehicle part materials and Base game. Base-game materials cannot be edited directly.
- **Body paint:** open **Body & setup → Body & materials**, select a slot and choose **Set slot color…**. New colors use **Solid** native LEGO paint. Choose **Metallic** or **Transparent** in **Paint finish** when appropriate. **Use slot material** removes the color override.
- **Light surfaces:** select the lens slot and its **Light behavior**, or use **Use named light slots**, then review the assignments. This reuses existing native lamp controllers, not a new lighting system.
- **Assembly:** position decorative parts and native light sources. Lens/glow geometry and the beam source are separate, so check both when moving a lamp. Preview hiding is not the same as removing a part from the build.
- **Seats:** enable **Seated figures** and position driver/passenger. The preview uses native seated poses for The Batman 2025 and default Catwoman. It does not simulate driving hand adjustments, entry/exit or cape movement.
- **Hardpoints:** position the native launcher, grapple and boost/exhaust markers. These move existing attachment points; they do not add weapon slots or change damage. The Forever boost marker uses the effect's local +Z direction. Independent boost-color authoring is not implemented.

The workshop uses vehicle axes: **X = forward/back, Y = left/right, Z = up/down**. Part axes rotate with the selected part. Materials and glow are approximate previews; check final appearance in-game.

Choose **Save vehicle** to keep the workshop edits.

### Native paint and older vehicles

Older simple-color recipes keep their existing appearance. **Upgrade simple colors…** converts those overrides to native solid LEGO paint without changing their saved RGB values. Review glass and metal slots afterward. Run **Full refresh** if the build reports a missing native paint template or palette.

New body imports get separate, editable copies of their assigned native materials, named for the car and slot—for example `MI_Slot0_70sBatmobile`. Existing custom assignments are kept. Reimporting does not overwrite edited copies. For an older car, use **Body & setup → Body & materials → Make editable copies** to convert its base assignments and color overrides.

Material saves update the generated library immediately; **Save vehicle** keeps the assignment. Editing an existing material affects every surface using it. Create a copy when only the selected slot should change. Generated paint copies include their own color swatches, so recoloring one does not recolor another.

To change a headlight, open **Lights**, select **Headlight beam 1** or **2**, and use **Beam color** and the position/rotation controls. Bulb/glow meshes are separate from the light cast onto the road. A marker labeled **reference** is only a socket, not an editable lamp controller; changing a decorative mesh's material does not create a functional headlight or brake light.

Native paint uses the game's LEGO shader families and a private color swatch. The solid finish retains the native **Supports RedBrick Tinting** permutation. This is experimental: check appearance and Red Brick effects in-game before sharing a release. The preview is approximate, and retaining a shader setting does not prove every runtime effect works. **Flat** remains available for the older simple-color shader.

### Vehicle menu icon

Open **Body & setup → Menu icon → Import PNG…**. Use a **512 × 340 PNG** with a transparent background and some padding around the car. This is a color thumbnail, not an equipment SDF. Batcomputer cooks all mip levels and assigns it to the vehicle's selection-menu entry.

**Use donor icon** restores the original thumbnail. Save the vehicle and rebuild its mod after changing the icon. This does not change a 3D parked or display model.

## 7. Add it to a mod

On **Vehicles**, create or select the target mod, choose its vehicles and save the selection. The tab separates vehicles enabled in that mod from the rest of the library. A saved vehicle that is not enabled will not be packaged. A saved custom-character owner is included automatically when needed.

**Build selected mod** packages the enabled vehicles. A vehicle-only mod does not need a suit entry or an open suit project.

Build through the normal [build and sharing flow](build-test-share.md). Install the complete release bundle, including its registry plugin and tag configuration—not just the `.pak` file.

Check the thumbnail, selection, steering, wheel rotation, boost, firing/grapple, both seats, canopy movement and returning to the Batcave. For native paint, compare Red Brick effects on and off against the original donor. Restart with the vehicle equipped. Also verify that the original donor still works. Keep source art and vehicle project files for later edits; avoid shipping extracted native reference files as authoring samples.

## Driving versus parked and summon appearance

The normal body import changes the driving model and vehicle menu model. The active car parked in the Batcave can use a separate summon mesh, so replacing the body does not cover every presentation actor. A shelf/display car or 2D thumbnail can be another asset again.

There is a separate **Body & setup → Assembly model** import. It requires the selected donor's summon rig, not its driving skeleton. Read [summon and parked models](vehicle-summon-models.md) for the preparation flow and current limits.

## Parts toybox and vehicle size

Open **+ Parts toybox** to search the installed vehicle and shared light-piece meshes. This lists static meshes; animated weapons and other skeletal parts need their own setup.

- **Add a piece:** creates a movable, non-colliding decorative attachment. Select its surfaces to choose, copy or recolor materials. Adding a light-shaped mesh does not add a brake/headlight controller.
- **Choose replacement shape:** changes a selected native decorative mesh while keeping its existing controller and position. This is the route for changing a working lamp's shape. Its material overrides reset; native light-data compatibility still matters.
- **Remove part from mod:** hides the piece without removing the donor's controller graph. Undo or restore brings it back.

**Body & setup → Vehicle → Size %** scales the body, wheel geometry and attached parts together. It also applies to the menu and parked model. This control is experimental: check tire contact, suspension, collision, character size, seat placement and summon animations in game. It does not retune the donor's handling or author new collision shapes. Keep the FBX rig at its original scale.
