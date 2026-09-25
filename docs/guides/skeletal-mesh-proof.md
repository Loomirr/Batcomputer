# Import a skinned character mesh

Use a weighted FBX when a custom body or part needs to move with the character's native skeleton.
Use [OBJ attachments](custom-meshes.md) for a rigid accessory attached to one socket instead.

!!! warning "Experimental native-rig workflow"
    Batcomputer does not auto-rig, auto-weight or retarget a model. Keep the selected donor's
    complete bone hierarchy, names and rest pose. New rigs, cloth and morph targets are not supported.
    Start on a backed-up test suit and check the result in-game before sharing it.

## 1. Choose the rig you actually need

Set the suit's visual base and gameplay donor first. Open its skinned-mesh workshop from **Parts**
and choose the native component in **Which native rig did you use?**

For a body replacement, select **CharacterMesh0** and the correct native body mesh. For a head,
hood or other attachment, select that attachment's native skeletal component. The list is drawn
from the suit's available components; it is not a browser of every skeleton in the game. If the
needed attachment is missing, add its native part to the suit before opening this workshop.

Choose **Export native reference + rig (GLB)…**. This writes the reference and the adjacent
`Batcomputer_PrepareBlenderRig.py` helper. Use a fresh export from 1.0 if you have an old reference
with the tiny-rig or oversized-skeleton problem.

## 2. Prepare the model in Blender

Follow [Prepare a native rig in Blender](blender-rig-preparation.md). In short:

1. Import the GLB without changing the donor rig's proportions or bone names.
2. Run the supplied preparation script once on that armature.
3. Fit your geometry to the reference, then rig and weight it to those native bones.
4. Export only the final joined mesh and the complete armature, with no extra leaf bones.

Do not use an assembled character-viewer GLB as a replacement for this donor-specific reference.
The assembled export can contain several independent rigs and preview placements.

## 3. Import and assign materials

1. Keep Batcomputer's import scale at **1.0000**. The current cooker rejects arbitrary import-scale changes.
2. Choose **Import / replace weighted FBX…** and select your export.
3. Wait for the import, cook and per-bone validation to finish.
4. Assign a compatible **cooked game material** to every FBX slot. Blender shaders do not become Unreal materials automatically.
5. Compare **Original**, **Custom** and **Skeleton** in the preview. Use **Frame** to fit the view.
6. Test individual bones with the pose-check controls, then return to **Rest pose**.
7. Hide only the original visual components your replacement actually covers, then choose **Use skinned mesh**.

The pose-check colors identify material slots. This is not the final game shader or full animation
playback. Hiding an overlapping original head may be appropriate for a full-body replacement;
hiding an unrelated cape or face just to conceal a bad rig is not a fix.

## 4. Save, rebuild and test

Run **Check mod**, build the complete mod and cold-launch the game. Check idle, walking/running,
attacks, traversal and cutscenes. A mesh can look fine in its rest pose and still stretch under animation.

Use **Import / replace weighted FBX…** for a newly exported Blender file. **Reimport saved FBX**
cooks the copy already stored by Batcomputer; it does not pick up a different file in Downloads.
Keep your `.blend` and source art too.

If changing the base or identity requires removing a saved skinned replacement, keep the source,
make the base change, then export the new donor reference and reimport against it.

## Fix an import error

| Error or symptom | What to check |
| --- | --- |
| Unexpected bone named after the model | The armature **object** should be named `Armature`. Do not rename a native bone to fix an object-name problem. |
| One extra bone or leaf bones | Export only the intended mesh and armature, disable Add Leaf Bones, and keep the complete native rig. |
| Root rotation 90°/180° | Start with a fresh reference and run its helper once. Do not rotate native bones by trial and error. |
| Root scale difference 99 or 0.99 | This is a measured mismatch, not a value to set to zero. Keep import scale 1.0000 and rebuild from the correctly prepared rig. |
| Whole rig tiny or giant relative to the body | Check the complete stick hierarchy, not only Root at the feet. If the whole rig disagrees, stop and re-export the native reference. |
| A few vertices stretch across the screen | Check their vertex groups and weights, the chosen donor and the per-bone report. Do not compensate by scaling the whole character. |
| Import succeeds but cook fails | Read `cook.log`; a path-length or missing dependency error is not necessarily a weighting error. |

When validation fails, keep `rig-comparison.json`, `import.log` and `cook.log` from the reported
import folder. Include the donor's package name and your Batcomputer version in a bug report.

## Attachment body clearance

Body clearance is a separate feature for [custom static attachments](custom-meshes.md).
It adjusts supported native body offsets to make room for accessories; it does not fix an FBX
bind-pose mismatch or authorize a differently scaled skeleton.

For other workflows, see [skeletal equipment](equipment-workshop.md#skeletal-equipment) and
[vehicle rigs](vehicles.md). Use the reference exported by that editor, not a minifig body rig.
