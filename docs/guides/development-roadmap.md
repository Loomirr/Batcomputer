# What's next

The next release is mostly about testing and fixing bugs. Custom characters, extra suits, held items, and existing-rig body imports have worked in-game. The combinations people will actually use still need testing.

These are the main areas still getting attention.

## Characters and suits

The [character editor](custom-characters.md) creates a separate roster entry with its own suits. Multiple characters and mods together, saved selections after restarting, and co-op still need testing.

New voices, scripted story roles, unlock challenges, custom roster emblems, and default-vehicle editing aren't supported yet.

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

Possible next steps are better character presentation options, more supported equipment, and improvements to skinned-mesh imports. The crash-prone effects need their own tests before coming back.
