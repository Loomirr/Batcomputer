# Held-item model editor

You can give a held item your own model without changing its attacks. Custom models, left-hand placement, attack timing, and attack-only visibility have been tested in-game.

## Replace a model

1. Open **Abilities → Held items → Edit item → Open model editor**.
2. Import an OBJ and line it up with the original using position, rotation, and scale.
3. Toggle the original model to compare the fit. Assign a cooked material to each OBJ material slot.
4. Choose **Validate bake & use model**.
5. Save both parent editors and rebuild the suit.

The source OBJ is saved with the recipe, so moving the original file won't break later builds. Removing the custom model restores the original mesh when you rebuild. Cancel leaves the saved model alone.

## What the preview shows

The preview shows geometry, material-slot colors, and the transform used for baking. It isn't a preview of the game's final shaders. The axes mark the mesh origin, not a calibrated hand grip.

Current limits are static held items, OBJ files up to 8 MB, uniform scale from 0.001 to 1000, and numeric alignment rather than drag controls.

## What stays the same

Changing the model doesn't change hitboxes, damage, animations, or attack timing. Those settings are separate from the item's appearance. The custom mesh is built under the suit's own asset path; it doesn't replace the original game mesh.

Extra VFX and status-effect controls are experimental and on hold after crash reports. Don't treat their placement previews as proof that an effect is safe in-game.

## Before sharing

Check the item in both hands, while moving, and during combat. Save and reopen the project, then rebuild it. An asymmetric test model makes flipped placement easier to spot.

## Implementation notes

The editor uses `ModelPreviewControl` and `ModelPreviewService` for the preview, and `StaticMeshObjProbeService` for weapon-local baking. It does not use the character-attachment staging path. Calibrated grip markers and full material rendering are not implemented.
