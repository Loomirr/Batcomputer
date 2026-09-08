# What to test before the next release

Use the normal Batcomputer menus for these tests, even if the prebuilt test paks worked. We're checking that you can create, save, reopen, and build the same thing yourself—not just load a finished mod.

## Before starting

- Back up the game save, workspace projects and working mod installs. Use disposable project copies for deliberate failure tests.
- Record the tool commit/build, game build, mappings, runtime version and enabled mods with every result.
- Remove duplicate old proofs with the same character/pawn IDs; preserve unrelated regular mods. Fully restart the game after installation changes.
- Begin with one simple mod. Keep configurable VFX/on-hit statuses disabled: expanded combinations were reported to crash on suit hover and remain parked. The original working baton trail is a separate case.

## 1. Characters and child suits — highest priority

- Create a character from a native base through **Characters → New character**. With an ID such as `Ragman`, the default variant should belong to Ragman, not Batman.
- Copy a complex saved suit into a character. Check materials, textures, geometry, hidden parts, abilities and equipment. The source project must remain unchanged; intentionally shared library assets remain shared.
- Set the name and every icon view, save, close Batcomputer and reopen. Verify identity and appearance persist.
- Create visibly different child suits using both **Add a suit** and the custom-character base picker. Confirm the same owner with distinct pawn/progress tags.
- Edit only a child: remove a headpiece, change a material and change an ability. The default character and original source suit must not change.
- Try duplicate/case-only duplicate IDs, invalid punctuation and an existing native owner. Expect a clear rejection without a half-created project.
- Build a child-only mod: its default character should be included automatically. Explicitly disabling the parent must block the build with an explanation.
- Try deleting a character with child suits. Dependency protection should prevent orphaned projects.
- Build two characters in one mod, then two independent character mods together. Check separate names, icons, suit lists and identities; native characters must remain available.

## 2. Save, build and recovery — release blockers

- Open an older suit and immediately build while its saved stage restores. It should wait safely or explain a retry, not require an unexplained several-minute delay.
- Add a torso and utility belt, then apply a belt material. The torso should replay correctly and both character roles should remain valid.
- Save/reopen and build the same edited project twice. Parts, offsets, materials, IDs and registry entries must not duplicate or drift.
- On a disposable copy, remove only one texture's generated cooked output, retaining its PNG/template. Build should automatically recook before material validation.
- Move a disposable PNG source away. Expect a named recovery error and no partial installed mod. Restore it or use **Replace image**, then rebuild.
- Cancel an edit or submit an invalid mesh. The last working recipe/stage must survive. Reopening should not reveal a half-applied change.
- Attempt a build during a pending edit or switch projects during preparation. Expect the correct saved snapshot or an explicit retry, never stale edits overwriting newer ones.
- Build one mod containing ordinary native-character suits, a new character and a child. Check membership/counts and preservation of unrelated projects.

## 3. Textures, materials and faces

- Edit an imported PNG externally, then **Reimport image**. Verify the existing source is reread, recooked and visibly updated in-game.
- **Replace image** with a different file, save/reopen, then reimport again. It should now use the replacement source.
- Use RGBA with meaningful RGB under transparent pixels. Check alpha/mask behavior and color preservation; a preview alone does not prove cooked channels.
- Assign different materials to body, hip, shoulder, cape and glider slots. Unrelated components should not change or cause stale-stage errors.
- Extract several native body/face textures and one generated texture through the right-click menu. Inspect the PNGs and test harmless cancellation.
- Edit compatible eye/brow/mouth layers. Same-rig materials should work; genuinely incompatible rigs should stay guarded.
- Set suit, menu, left and right icons; verify all four views in-game instead of accepting inherited base icons.
- Test playable/cutscene-specific material overrides, and copied-character materials. Distinguish isolated project edits from deliberate shared-library changes.

## 4. Attachments and skinned meshes

- Add native/custom Hip and Shoulder parts. Check slot labels, placement and separate body clearance in both roles.
- Reapply/rebuild repeatedly. Clearance must not stack; zero and custom values must behave as configured.
- Import a known-good, externally weighted FBX on the exported native rig: body first, then a compatible skeletal attachment.
- Check multi-material slots, hidden original parts, scale/orientation and bone-pose preview. The preview is not native animation playback or final shading.
- Save/reopen, reimport, then replace the FBX. Confirm persistence and that the intended cook revision is packaged.
- Reject wrong rigs, changed hierarchies, missing weights and unsupported imports clearly; failed/cancelled cooks must preserve the working mesh.
- Use asymmetric geometry/UVs to check static-model mirroring and alignment.
- Confirm facial rigs, cloth, new skeletons and skeletal-equipment authoring remain gated.

## 5. Abilities, animations and held items

- Add/remove a supported ability; inspect **Current loadout**, save/reopen and build. Required equipment/glider dependencies must stay consistent.
- Switch between supported playable fighting styles. Keep exactly one combat style while preserving unrelated locomotion/traversal.
- Test sword, bat and baton in empty space and near enemies. Verify attack variation and timing, not just one working attack.
- Test independent items in both hands, a small prop and a custom model, placement/scale and every offered visibility mode.
- Use gadgets, glide, take damage and switch suits with an item. Watch for stuck visibility or duplicate actors.
- Import an animation pack once, switch projects and check the tool-wide **Imported** library. Replace and reset a compatible exact slot.
- Verify only the selected suit/character uses the animation replacement; native donors and other suits must remain unchanged.
- Keep expanded VFX/status recipes out of these builds. Before publishing, separately resolve their warning/opt-in or exclusion policy; they are not accepted stable features.

## 6. Independent equipment

- Build banana/Batarang equipment through the normal workshop with custom model, icon and name. Native Batarang assets must remain unchanged.
- Equip another supported gadget alongside it. Check HUD, selection switching, aiming, use/throw, recall and upgrades.
- On a supported static-mesh donor, customize held and projectile models separately. Check materials, placement and in-game visibility of both.
- Reopen the editor, reset one part to native and rebuild. Other customizations should remain saved.
- Confirm NPC/boss, unverified and mainly skeletal donors are view-only with a reason.
- Check native characters still use their original equipment while the custom mod is installed.

## 7. UI and extraction

- Visit Home, Characters, Suits, viewer and Mod release. Check separate libraries, character-aware Save wording/counts and bounded inspector width.
- Resize and, where practical, test Windows scaling at 100%, 125%, 150% and 200%. Check top-bar actions, tile text, dialogs, scrolling and long errors.
- Switch Classic/Mayhem, restart and check persistence. Fresh settings should default to Classic.
- In a separate acceptance workspace, run **Full character extraction**, then build a character. Repeat with **DeveloperResearch** if enabled; both must include complete `PROG_Characters` prerequisites.
- Cancel/fail an extraction in a disposable setup. The previous active dump must remain usable; an incomplete refresh must not become active.

## 8. Gameplay and packaging — release blockers

- After a cold start, verify new default/child suits are visible, initially unlocked and independently selectable.
- Select a variant, save/quit fully, restart and revisit it. Repeat after switching to a native character.
- With new characters installed, test normal Batman plus visibly modified Catwoman/Robin suits in cold cutscene loads and after restart. New characters do not replace scripted story actors; the frontend remains Batman-only.
- Exercise movement, combat, gadgets, glide, damage/death/respawn and suit menus. With body-profile changes, also check speech, stealth/focus takedowns and level suit selection where supported by the native donor.
- Test co-op join/drop, player switching and level/hub transitions. One player's choice must not overwrite the other's.
- Inspect weighted models during combat/traversal, gadget use and death: deformation, socket attachments and collision-related behavior need runtime checks.
- Build/install normally, then test a mod's exported install layout separately. Keep trio, tags, manifest and registry plugin together; the trio alone is insufficient.
- Update/remove a test mod using the intended flow. Check for stale/duplicate entries and preserve unrelated mods.
- After acceptance, validate a clean portable build with no developer paths, extracted assets, source models, temporary output or unsafe test paks. Check version, docs, archive contents and hashes before publishing.

## Exit criteria and reporting

Block release on crashes, disappearing characters/suits, broken saved selection, stale/partial builds, donor overwrites or lost edits. Fix or isolate known crash-prone experiments before publishing. Unsupported features are explicit boundaries, not waived failures in supported flows.

For a failure, record exact steps, project/character ID, donor, enabled mods, expected/actual result, screenshot and relevant tool/game logs. State whether it persists after a full restart. Retest the original reproduction and a neighboring working case after each fix.
