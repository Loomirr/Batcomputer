# Compatibility and beta limits

## Supported in the beta

- Suit-menu discovery through the plugin loading provided by Loomirr's LOTDK UE4SS.
- Multiple suits in one mod and multiple independently installed suit mods.
- Batman and other character families when the suit uses an appropriate playable donor and unique
  identity.
- Built-in playable and cutscene visual bases, plus indexed character parts.
- Game-material templates and face tools.
- Character, mask, normal/packed, and verified UI texture cooking.
- Supported equipment, glider, and animation-data grafting.
- A separate regular cape plus a replacement glide cape when the indexed animation controller uses
  the game's verified paired-cape visibility contract. Glide-only wingsuits and character gliders
  are supported when the regular `Cape` is removed.
- Custom OBJ static-mesh attachments.
- Direct installation, build checks, and installable ZIPs.

## Not supported yet

- Custom Red Brick creation. The viewer only previews the game's existing colour options.
- Custom skeletal-mesh cooking or skeleton transfer.
- Arbitrary new gameplay powers or code-driven character mechanics.
- Physical collectible placement in levels.
- Perfect shader/lighting parity between the 3D viewer and the game.
- Combining a separate regular cape with a wingsuit or other glide-only controller. Those
  controllers animate their glide visual but cannot hide and restore another cape.

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
