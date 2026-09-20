# What's next

The next release is mostly about testing and fixing bugs. Custom characters, extra suits, held items, and existing-rig body imports have worked in-game. The combinations people will actually use still need testing.

These are the main areas still getting attention.

## Current release status

1. Character viewer: custom-part gizmos are user-tested; keyboard-focus fixes for W/E/R and F have regression coverage. Native parts remain read-only. Character/suit labels and initial Home population are now covered by local checks.
2. Skinned import reliability: verify the short temporary cook workspace against user logs, retain useful errors and per-bone rig reports, then retest the affected users' FBXs. A successful cook does not make an incompatible rig safe.
3. Native Joker and Harley suits: the additional-suit tests now pass in-game after the retained Default donor gates were mapped to the current HTV/BTAS progression entries. Keep checking first-hover previews, switching, native gadgets/gliding and cutscene face/material behavior on future builds.
4. Editable sharing now passes independent export/import/edit/rebuild checks for a custom-material Joker suit and a rigged-mesh suit. Relative mesh caches and material closures are included, with source/identity checks. Vehicle-specific sharing and generated-texture recooking still need broader acceptance coverage. Imported custom animation libraries are explicitly blocked until their transfer is supported.
5. Face replacements: native component-preservation checks pass for Batman, Joker and Harley. The Batman selected-suit face-animation test also passed in-game.
6. Voice mismatch: the Nightwing paired-cape dialogue test passed in-game. Adapters preserve the gameplay donor's dialogue voice instead of the visual scaffold's voice. Rebuild affected suits. A native voice-family picker and individual-line overrides remain future work; scripted dialogue and combat audio have separate routing.
7. Release checks: the full configured regression suite, clean portable publish, published-executable checks and documentation build passed. Tested vehicle workflows passed except the deferred Retro/Talia build-up model. A clean install/upgrade on another user's machine remains a useful release-candidate check.

## Characters and suits

The [character editor](custom-characters.md) creates a separate roster entry with its own suits. Multiple characters and mods together, saved selections after restarting, and co-op still need testing.

Custom character symbols are supported and have been tested in-game. New voices, scripted story roles, unlock challenges, and default-vehicle editing aren't supported yet.

## Abilities and held items

Combat styles, sword/bat/baton attacks, custom held models, and left/right-hand placement are working. Combat-style changes should only replace combat animations, not a character's whole animation set.

The extra effects and status-effect experiments are on hold after crashes when hovering over suits. The working native baton trail doesn't mean every effect combination is safe. BigFig shield and hammer adapters aren't part of the current work.

## Custom equipment

The [equipment workshop](equipment-workshop.md) supports eligible playable equipment with static-mesh bodies. Held models and projectile models can be changed separately. NPC, boss, and mainly skeletal equipment stay view-only.

The white-and-green SDF import, shared HUD material and appearance controls have worked in-game. Batarang upgrade choices and corrected custom-instance bindings are now experimental; special modes, filtering and upgrade-state transitions need a targeted gameplay retest. Impact effects can still contain the native model.

## Skinned meshes

The [skinned-mesh importer](skeletal-mesh-proof.md) uses rigs already in the game. The model needs to be weighted in Blender or another 3D editor before importing.

Attachments, material assignments, save/reopen, failed imports, and deformation in-game still need testing. New rigs, cloth, facial rigs, and skeletal equipment need separate work.

## Fixes to keep checking

- Catwoman and Robin suits in cutscenes and after restarting.
- Texture recovery and rebuilding older projects.
- Hip and shoulder clearance without offsets stacking.
- Ordinary native-character suits alongside custom characters.
- A clean portable install, with no development files or test mods included.

The frontend stays Batman-only. That's intended.

## After this release

The September 20 acceptance round passed the Nightwing paired-cape dialogue test, selected-suit
face-animation test and the tested vehicles' driving/menu workflows. The Movie vehicle's custom
build-up also worked. One Retro/Talia-rig test had an invisible build-up model; that isolated test
remains unresolved and is not evidence that every donor/assembly combination works. Vehicle
summon models remain experimental and must be tested separately from the driving mesh.

Editable sharing has separate-workspace import/rebuild checks, including a rigged-mesh cache.
Unprovided third-party FBXs, all donor combinations and every multiplayer/story interaction are
not covered by these checks.

Possible next steps are better character presentation options, more supported equipment, and improvements to skinned-mesh imports. The crash-prone effects need their own tests before coming back.
