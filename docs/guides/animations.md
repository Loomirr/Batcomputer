# Character animations

Batcomputer can replace one exact animation used by the current suit while leaving the gameplay
donor and every other character alone. The change is saved in that suit project and applied to its
own generated animation assets when you package it. It never rewrites the base-game animation.

## Explore a character's animation setup

For a first test, change one action on a backed-up suit, rebuild and trigger that exact action in-game.
Keep its movement, attacks and equipment unchanged until the replacement is working. Then test
the transitions into and out of the action, not just its first pose.

Open **Animations** and choose **Edit character animations**. The Animation Explorer follows the
selected gameplay donor and groups its:

- Actions and context variants.
- Montage slots.
- Animation Blueprint layers.
- Locomotion sequences.
- Current suit overrides.

Select the exact row you want to change, then choose **Replace animation**. The picker keeps
base-game and imported sources separate and enforces the target asset class. **Reset to donor**
removes that one saved override and returns the row to the gameplay donor.

![Animation Explorer](../assets/screenshots/animation-explorer.jpg){ .bc-doc-shot loading=lazy }

Animation Blueprint layers are more tightly coupled to a character graph than ordinary sequences
or montages. Batcomputer labels cross-character layer swaps as experimental; test one in-game
before building the rest of a character around it.

An individual idle/walk/run override and a whole Locomotion layer swap cannot both own the donor's
`LAS_Default` controller. An exact layer edit inside that same default set has the same conflict.
Batcomputer stops the build and asks you to reset one side instead of packaging two competing
controllers.

## Pose and keyframe a draft in the 3D viewer

Open a character's **3D viewer**, then choose **Create** in the inspector. This is a local animation
studio for the native LEGOfig body rig. It opens in rest pose and does not modify the suit, the game,
or an imported animation. The parts list folds away to give the model and timeline more room; use
**Parts** if you need to inspect it while editing.

1. Name the clip, describe its intended use, and set its length (30 frames per second, up to 30 seconds).
2. Choose a bone from the searchable hierarchy. **Show joints** makes its joint markers clickable in
   the 3D view, and **Focus joint** centers the camera on the selected bone.
3. At the desired frame, use **Move**, **Rotate**, or **Scale** on the joint gizmo. The numeric fields
   show the selected bone's offset from its rest pose; they are not world-space coordinates. Scale
   is a multiplier of the bone's rest size (1 means unchanged), limited to 0.05–5 per axis. Choose
   local or world axes and turn on snapping when you need precise increments.
4. Use **Set key**, **Key rest pose**, or the transform controls to make keys. Drag a diamond in the
   timeline to retime it; copy/paste a key to another bone or frame; use **Undo/Redo** to correct an
   edit. A key's **Transition** can be linear, smooth, or held until the next key.
5. Scrub the timeline or use frame stepping, playback speed, looping, the frame number, and timeline
   zoom to inspect the motion. Save the draft JSON and reopen it later on the same native rig. Use
   **New draft** for another clip; it warns before discarding unsaved work.

Drag the small grip on the timeline's top edge to make the track area taller or shorter. Its height
is remembered. With the grip focused, Up/Down changes the height by keyboard.

Useful shortcuts while the 3D viewport has focus: `W` moves, `R` rotates, `E` scales, `K` adds a key,
Space starts or pauses, Left/Right steps one frame (Shift steps five), and Ctrl+Z/Ctrl+Y undo/redo.

The editor saves a draft JSON first. To make it playable in a mod:

1. Save the draft from **Create** in the 3D viewer. Saving does not change the suit or game.
2. In Batcomputer's **Animations** page, choose **Cook animation draft** and select that JSON.
   Select a prepared, weighted native LEGOfig body FBX when asked. Configure Unreal Engine 5.6
   in Settings. Batcomputer validates the draft against the installed game's rig, cooks an
   `AnimSequence` in an isolated Unreal project, connects it to the native skeleton, and adds it
   to this workspace's animation library. You do not need to run Unreal commands yourself.
3. In **Edit character animations**, select the exact suit animation slot and choose the cooked
   library entry. Build the suit, then test that action in-game. No other slot is changed.

For the one-time FBX reference, export **native reference + rig (GLB)** from the skinned-mesh
workshop using the standard Minifig body. Follow the [Blender rig preparation guide](blender-rig-preparation.md)
and export the weighted reference geometry with its complete armature as one binary FBX. Keep
that reference locally; do not include the game's extracted model in a shared mod. A mismatched
rig is rejected before library import. The FBX is a temporary cook scaffold, not a replacement
mesh shipped with the animation.

The cooked sequence is stored in the workspace library and staged into a suit's pak only after
that suit references it. A cook does not install a mod automatically. If you change a draft and
cook it again, its content-hashed package name is new, so the previously assigned version stays
intact until you deliberately choose the new one.

One character can have several idle slots, and the game may not play them all in a short session.
Seeing one idle work does not prove that the other slots were selected or that every authored clip
has played. For a reliable in-game check, temporarily map each candidate to a known primary idle
slot on a disposable test suit and observe it separately.

## Import a cooked animation pack

Choose **Import animation pack**, then select the `.utoc`, `.ucas`, or `.pak` from the cooked
container. Batcomputer finds the matching files, verifies the container, rejects package paths that
would overwrite the installed game or DLC, and imports supported `AnimSequence` and `AnimMontage`
assets with their required cooked dependencies.

The imported library belongs to the whole Batcomputer workspace. An animation imported while one
suit is open is available when editing every other suit in that same workspace. The **Imported**
filter shows the complete library; a row that cannot satisfy the selected target remains visible
but disabled and explains its class, health, ownership, or missing-cache problem. A complete asset
on an unverified rig stays available only behind an explicit experimental warning, since a wrong
bone layout can crash when that pose starts.

![Animation replacement picker](../assets/screenshots/animation-replacement-picker.jpg){ .bc-doc-shot loading=lazy }

Imported package files are not copied into every build. Batcomputer stages an imported animation
and its required support packages only when the current suit references it.

## Safe test flow

1. Start with one sequence or montage on a duplicate test suit.
2. Confirm the target row, required class, source rig, and replacement in Animation Explorer.
3. Run **Check mod**, then build and install.
4. Fully restart the game and test the exact action plus nearby transitions.
5. Return to Animation Explorer and use **Reset to donor** if the source graph is not compatible.

Changing one row does not replace that animation globally. Another suit receives the same change
only if you explicitly assign it there too.
