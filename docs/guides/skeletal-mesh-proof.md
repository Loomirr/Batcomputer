# Import a skinned mesh

You can import a custom body or compatible attachment using a skeleton already in the game. The first weighted body test loaded in-game without crashing. How well your model moves still depends on the rig and weights you give it.

## Experimental tool flow

In **Parts → Import skinned mesh**, choose an existing skeletal component present in both the playable and cutscene actors. For an attachment, add the compatible native part first. The workshop replaces its geometry without converting component classes or changing the native animation/gameplay graph.

1. Export the native mesh and rig as GLB. Import that reference into Blender or another 3D editor.
2. Fit the model, bind it to that rig, and author/check its weights externally. Automatic weights are only a starting point. Preserve bone names, hierarchy and rest pose; remove exporter leaf/helper bones. Export **one joined mesh**, with named material slots, as binary FBX 7.4–7.7.
3. Import the FBX in the workshop. Set the unit correction if needed; it is not a body-size control. Batcomputer validates weights, cooks with the configured **Unreal Engine 5.6 installation**, and compares the cooked rig and rest pose to the native donor. The first cook can take several minutes.
4. Assign cooked material package paths from the Materials/Textures toyboxes to each FBX slot. Optionally hide other visible components, such as the original cowl and face on a full-body model.
5. Inspect the deformation preview: original/custom toggles, skeleton display, and individual bone rotation. This is a pose check with slot colors, **not native animation playback or final game shading**.
6. Use skinned mesh, then Build Mod normally. Both character roles receive the replacement. The normal inspector can still apply role-specific materials. Reopening the workshop without changing a slot preserves those overrides.

Saved source FBXs and validated mesh buffers live under the suit's `ImportedSkinnedMeshes` directory. Reimport uses the saved copy; Import / replace selects a different external FBX. Each cook creates a new revision, so failed/cancelled cooks do not replace the working mesh. Source/buffer hashes are checked again when staging. Keep this directory with the suit project.

Not supported yet: no new rigs, automatic weighting/retargeting, cloth, facial rigs/morphs, static-to-skeletal component conversion, or skeletal equipment authoring. Equipment restrictions stay in place pending dedicated tests. Remove skinned replacements before changing the suit's base/identity, then reimport against the new donor; cached cooks are not silently migrated. Standalone rigid attachments still use the existing static-mesh workflow.

This importer does not author physics assets or transfer mesh-local socket definitions. Native skeleton references and the actor's existing gameplay machinery remain, but collision/ragdoll/death behavior and socket-dependent parts need explicit in-game testing.

## Attachment body clearance

Body clearance is separate from accessory XYZ placement. Native LBM Batman raises `Spine_01` by **4.3** for the hip/belt; native Armoured Batman raises `Neck` by **3.0** in gameplay and cutscenes. These are serialized in the attachment manager's `PermanentOffsetData`, not in the static mesh.

Custom Hip and Shoulder attachments now use the matching slot tag instead of inheriting `TtCharacterAsset.Head` from their static donor shell. The import/edit dialog offers the native clearance default or an explicit nonnegative value; zero disables that attachment's contribution. Native part grafts inherit their donor's relevant clearance. Shared contributions use the maximum, not repeated addition, and existing native offsets are retained. Rebuild to update older suits.

The verified minifigure recipe updates both roles and can create the native inherited cutscene manager override when needed. Unsupported manager layouts stop with an explanation rather than discarding unrelated properties. The current 3D preview does **not** simulate these gameplay bone-clearance offsets; inspect the result in-game.

## Notes from the first body test

The proof uses the existing `/Game/Characters/LEGOfig/SKEL_LEGOfig` skeleton and native Batman animation/gameplay setup. A separate UE 5.6 headless project imports and cooks the custom body. Its temporary editor skeleton reference is replaced by the existing game skeleton after checking compatibility; the temporary skeleton and editor materials are not shipped.

The supplied source contains 53 native bones plus 68 exported socket/helper/end bones. Its test-only adapter merges helper weights into the native ancestor, limits and normalizes influences, and repairs three unweighted vertices from their nearest weighted neighbors. Those repairs were specific to that test model. The normal importer doesn't automatically fix arbitrary rigs or missing weights. Source files remain unchanged.

The body retains four authored material sections and all 2,304 triangles. Four supplied textures use native gameplay/cutscene material-instance parents with neutral detail maps. Separate character components have their visible meshes cleared; their construction graph and gameplay machinery remain intact.

## Verification and next in-game checks

The original proof passed independent cooked-container reading, native hierarchy/rest-pose comparison, texture/material loading and character metadata/loading-bundle verification before being tested in-game. Maximum rest-position discrepancy was below 0.0001 game units. The integrated importer also cooked and validated the corrected test source. Automated structural checks do not replace testing the new tool workflow in-game.

Automated checks cover material-slot names, attachment-manager layouts, cutscene references, invalid weights, and damaged cached files. Independent packed-container checks confirm the integrated mesh's native skeleton, four material references and both actor bindings. Offset checks pass after applying the same declarations twice, without stacking. The workshop preview exports its GLB and required viewer files; the preview and offset behavior still need checking in-game.

Test selection, idle/run/jump, combat, gadgets, cutscene appearance, restart persistence, and switching back to an unchanged native suit. Inspect shoulders, elbows, knees, hands and head for weight problems. The embedded face is not a new animated face rig. This workflow doesn't require runtime DLL changes.

For attachments, test a belt and shoulder piece separately and together in gameplay and cutscenes; rebuild twice to check that clearance does not stack, then remove the additions and verify the native baseline returns. Unrelated rigs, cloth, and morph targets aren't supported. To create a separate roster entry, use the [character editor](custom-characters.md).
