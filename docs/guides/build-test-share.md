# Build, test, and share

## Validate first

Open the mod and choose **Validate release**. Errors block packaging; warnings identify content that
needs review but may still be intentional.

Typical blockers include:

- Duplicate suit IDs, PawnTags, or DCMD package paths.
- Missing cooked texture payloads.
- Invalid or stale donor assets.
- Missing UIMD/DCMD/StringTable output.
- An unavailable Asset Registry writer.
- A third-party mod build with no installed LOTDK Expanded core.

## Build and install

Close the game before building. **Build mod**:

1. Stages every enabled suit and shared mod asset.
2. Validates the staged cooked data.
3. Writes gameplay tags and native Asset Registry data.
4. Builds the pak/ucas/utoc trio.
5. Installs that exact successful release into the configured game.

Restart the game after each new installation. Unreal discovers tags, registry rows, and primary
assets during startup.

## Test matrix

Test at least:

| Area | Check |
| --- | --- |
| Menu discovery | Correct character submenu, tile count, name, description, and icon. |
| Hover | Stable preview with the expected materials and parts. |
| Selection | Native swap animation and correct playable pawn. |
| Gameplay | Movement, equipment, glider, abilities, and animation behavior. |
| Persistence | Back out to frontend, reload gameplay, then fully restart the game. |
| Compatibility | Test beside at least one other LOTDK Expanded suit mod. |

## Create the player ZIP

Use Batcomputer's release ZIP action after a successful build. The archive uses game-relative paths
and starts above `LEGOBatmanLotDK`, so users can extract it into their Steam `common` directory or
install it with a compatible mod manager.

Third-party suit releases require LOTDK Expanded. They must not include or overwrite the shared
`LOTDKExpandedCoreRegistry`; only the official core mod owns it.

## Before publishing

- Keep your authoring project and source textures/OBJ files backed up.
- Confirm the Mod ID is final.
- Include LOTDK Expanded and the compatible game build in requirements.
- State that the release contains no Batcomputer, UE, mappings, or extracted game files.
- Provide a short list of included suits and known limitations.
- Cold-test the exact ZIP you intend to upload.
