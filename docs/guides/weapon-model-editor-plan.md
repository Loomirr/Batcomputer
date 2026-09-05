# Weapon model editor — next implementation pass

Status: the independent held-item editor, custom models, left-hand items and native examples have passed user testing. Empty-space attacks, timing and attack-only visibility also passed. New VFX placements still need in-game acceptance.

Open **Abilities → Held items → Edit item → Open model editor**. Import an OBJ, use numeric position/rotation/scale controls, toggle either model, assign cooked materials per OBJ slot, then choose **Validate bake & use model**. Save both parent editors and rebuild the suit. The original OBJ text is embedded in the saved recipe, so moving the source file does not break later builds. Removing the custom model restores the selected original mesh on rebuild. **Edit effects / placement** opens the native VFX preset editor with approximate previews and mesh-local placement markers; these are cosmetic effects, separate from combat status settings.

Current limits: static held items; OBJ up to 8 MB; uniform scale 0.001–1000; centered OBJ geometry; numeric alignment rather than drag gizmos. The origin axes mark mesh-local zero, not a calibrated hand grip. The custom preview uses geometry and material-slot colors, not final game shaders. Native actor hitboxes and behavior are retained. Baking validates a scratch mesh; suit rebuilding creates the final suit-local asset.

## User workflow

1. Open a weapon from the fighting-style settings. Display the native mesh at its actual weapon-local origin. A calibrated grip marker and hand reference remain follow-ups.
2. Import a custom model alongside the original. Move, rotate and scale it; toggle or ghost the original independently. Preserve material slots.
3. Preview the custom model alone, with the exact transform used by the baker. Save/cancel must operate on a private editing copy.
4. Bake a separate suit-local mesh and assign it to the local weapon actor. Never overwrite the game's weapon. Preserve the source model and transform for later edits.

## Existing pieces to reuse

- `ModelPreviewControl`: embedded WebView2 renderer and placement-save messaging.
- `ModelPreviewService`: mesh/material preview generation and existing placement machinery.
- `StaticMeshObjProbeService`: existing OBJ inspection and cooked static-mesh tooling.
- `SwordCombatService`: isolated weapon actor, mesh and material assignment.

`WeaponModelService` bypasses `CustomStaticMeshImportService.Stage`, which builds a character attachment. It reuses `StaticMeshObjProbeService` directly for both preview and weapon-local baking. The existing orientation regression now includes nonzero offsets and all three rotations. A calibrated grip and full material rendering remain follow-ups.

## Separate controls and acceptance checks

Visual mesh changes do not automatically change collision, hitboxes, damage, effects or animation timing. Initially preserve the working native weapon behavior and clearly label those inherited settings; expose supported behavior settings separately.

Before release: verify source persistence after reopen, original visibility toggle, cancellation, multiple material slots, failed-bake rollback, suit-local dependency closure, packaged roundtrip, and in-game alignment using an asymmetric test mesh. No Blender runtime dependency or computer control is required.
