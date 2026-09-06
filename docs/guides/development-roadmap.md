# Development and release order

## Current priority: release stabilization

The independent-character proof, both variants and the normal editor build have passed user testing. Existing-rig body import also has a successful in-game proof. These are implemented beta workflows, not a reason to skip compatibility testing.

Work through the [next-release acceptance checklist](release-test-checklist.md) before adding features or assembling the release archive. Prioritize characters and child suits, save/restart behavior, material/stage recovery, skinned bodies/parts and unchanged native-character suits. A successful build is not proof of runtime behavior.

## Abilities and held items

Supported combat bundles, sword/bat/baton adapters, independent held items, fourteen native examples, custom models and left/right-hand placement are implemented. User tests confirmed the normal bat/baton/prop flow and the separate native baton trail. Regression coverage checks dependencies and combat-only animation changes.

Configurable VFX/status combinations were subsequently reported to crash during suit hover. They remain parked, not accepted for release. The original working baton trail does not certify the expanded editor. Before release, isolate those experimental controls behind a clear opt-in or leave them out of the public build. Shield/hammer BigFig adapters remain out of scope.

## Recovery and native-suit fixes

Stage transactions, generated-texture recovery, attachment offsets and character-aware UI fixes have regression coverage. Catwoman/Robin cutscene and restart behavior still belong in every release's in-game smoke test with the separately supplied runtime. The frontend remains Batman-only by design.

## Custom equipment

The independent banana/Batarang proof was confirmed visible and usable, with native upgrades. The workshop supports eligible playable static-mesh equipment, visual references and projectile meshes. Validate the normal editor flow on more supported donors; NPC/boss, unsupported and mainly skeletal equipment remain view-only. Independent held props are distinct from selectable equipment.

## Experimental existing-rig skinned meshes

The weighted FBX body proof loaded in-game. The [workshop](skeletal-mesh-proof.md) imports bodies and compatible skeletal parts, validates the existing rig and cooks project-owned revisions. Expand acceptance to attachments, materials, failed imports, save/reopen and in-game deformation. New skeletons, facial rigs, cloth and skeletal equipment remain outside this pass.

## Independent characters

The [character editor](custom-characters.md) separates roster identity from gameplay donor, creates default and child suits, and generates additive group/progression/roster data without runtime DLL changes. The proof and normal build work in-game. Complete the multi-character, multi-mod, save/restart and co-op matrix before release. Scripted story casting, new voices, unlock challenges, custom roster emblems and default vehicles are not implemented by this pass.

## After release acceptance

Choose the next bounded feature from character roster presentation, broader supported equipment coverage or existing-rig import polish. Revisit unsafe effects separately with isolated tests. Do not expand into new rigs or story-character replacement until their own research and proof are authorized.
