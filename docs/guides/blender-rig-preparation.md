# Prepare a native rig in Blender

This walkthrough is for the **character skinned-mesh importer**, using a fresh Batcomputer native
reference. It is not a conversion guide for arbitrary internet rigs. A model that already has
different bones still needs to be weighted to the native rig.

## Before editing

1. Export **native reference + rig (GLB)** from the skinned-mesh workshop for the component you intend to replace.
2. Keep the GLB and its adjacent `Batcomputer_PrepareBlenderRig.py` together. A copy of the helper also ships under `Tools/SkinnedMesh/prepare_blender_rig.py`.
3. In Blender, import the GLB using the glTF importer, then save a working `.blend` copy.
4. Keep the reference geometry available for size and placement comparisons. Do not scale its rig to fit your custom model.

Use a new reference if you previously exported with a build that produced a rig much smaller than
its mesh. Repeatedly applying 0.01 or 100 does not repair inconsistent bind transforms.

## Run the preparation helper once

1. Select the imported **armature object** in Object Mode.
2. Open Blender's **Scripting** workspace and use the Text Editor's **Open** action to load the supplied Python file.
3. Read the script, then choose **Run Script**. Only run the helper shipped with your Batcomputer download or reference export.
4. The armature object should now be named **Armature**. The bones keep their native names, including **Root**.

The helper prepares the rig's FBX rest-space basis and enables an in-front stick display with bone
names. It does not create weights or add/remove game bones. It refuses to run twice on the same
prepared armature. If you need to start again, reimport the untouched reference rather than clearing
that safeguard and running it again.

!!! note "Object name versus bone name"
    Rename the armature **object**, not the Root bone or every bone inside it.
    An object named after your model can be imported as an unwanted extra root.
    A tiny Root marker at the feet is normal; the **whole hierarchy** should still fit the body.

## Fit and weight the custom mesh

Fit the custom geometry to the reference in **mesh Edit Mode**. Keep the native armature's rest
pose and scale unchanged. If your mesh has object-level transforms, resolve those on the mesh
before binding/exporting; do not blindly apply transforms to the whole scene or pose the rig to
make the model fit.

Bind the mesh to the prepared armature and assign native bone-named vertex groups. Every exported
vertex needs suitable weights. A rigid LEGO piece can follow one bone with weight 1.0; regions
that need to bend require appropriate weighting. Automatic weights are only a starting point and
can attach nearby disconnected pieces to the wrong bone.

Test in Pose Mode: move one arm, leg or attachment bone at a time. Watch for pieces following the
wrong limb and isolated vertices stretching away. Clear the test pose afterward. Do not apply a
test pose as the new rest pose, and do not delete unweighted native hierarchy bones.

## Export checklist

Join the intended replacement geometry into one mesh while retaining its UVs, material slots and
weights. Select only that mesh and its complete armature; leave the reference geometry out.

The supplied helper calls for these FBX settings:

| Setting | Value |
| --- | --- |
| Export selection | Only the replacement mesh and its armature |
| Format | Binary FBX |
| Armature object name | `Armature` |
| Only Deform Bones | On; ensure this does not omit required native bones |
| Add Leaf Bones | Off |
| Armature FBXNode Type | Null |
| Bake Animation | Off |
| Apply Modifiers | Off |

Prepare any required non-armature geometry modifiers deliberately in your source file first.
Do not apply the armature deformation as a static mesh. Export with settings that preserve the
reference's scale and basis; don't add a manual 0.01/100 compensation. Make a small trial import
before spending time on detailed geometry.

## Validate in Batcomputer

Return to the same native donor and import with scale **1.0000**. Passing the bone-name/count
check alone is not enough: the cooker also compares hierarchy and rest transforms. For a failed
comparison, read the named bone in `rig-comparison.json` rather than changing random export values.

Continue with [materials, pose checks and the in-game test](skeletal-mesh-proof.md#3-import-and-assign-materials).
Keep the original `.blend`; the exported FBX is not a substitute for your authoring file.
