# Compatibility and beta limits

## Supported in the beta

- Native suit-menu discovery through LOTDK Expanded.
- Multiple suits in one mod and multiple independently installed suit mods.
- Batman and other character families when the suit uses an appropriate playable donor and unique
  identity.
- Shipped playable/cutscene visual bases and indexed character parts.
- Donor-backed material and face workflows.
- Character, mask, normal/packed, and verified UI texture cooking.
- Supported equipment, glider, and animation-data grafting.
- Custom OBJ static-mesh attachments.
- Direct install, validation, and player-ready release ZIPs.

## Not supported yet

- Custom Red Brick authoring. Only read-only previews of shipped palettes remain in the viewer.
- Custom skeletal-mesh cooking or skeleton transfer.
- Arbitrary new gameplay powers or code-driven character mechanics.
- Physical collectible placement in levels.
- Perfect shader/lighting parity between the 3D viewer and the game.

## Content that needs extra testing

- Equipment driven by controller actors, remote gadgets, or complex spawn/recall logic.
- Unusual body rigs or character scales.
- Cross-family face materials.
- Animation sets far from the chosen playable donor.
- Large or topologically unusual custom OBJ imports.

## Game updates

Cooked Blueprints and mappings can change after any game update. If a previously working project
starts crashing or disappearing:

1. Obtain a mappings file for the new build.
2. Run the full character extraction.
3. Rebuild indexes.
4. Re-select/rebase the playable and cutscene donors.
5. Validate and rebuild every affected mod.

Do not assume a package is compatible merely because FModel can list it.
