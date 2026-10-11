# LOTDK mappings

Download the Unreal property mappings used to read cooked assets from **LEGO Batman: Legacy of the Dark Knight**. Batcomputer needs a `.usmap` that matches your installed game build.

[Download latest.usmap](https://raw.githubusercontent.com/Loomirr/Batcomputer/main/.usmap/latest.usmap)

## Current mapping

- Game build: `5.6.1-1353284+++Dinner+mainline`
- Original filename: `Dinner-5.6.1-1353284+++Dinner+mainline-b872ad11.usmap`
- File size: `3636601` bytes
- SHA-256: `218b9e3458902a804e3970c0512119d088b66a91d36ede7d0dac5c5e64cf3257`

Use mappings that match your installed game build. A newer mapping is not necessarily compatible with older game assets.

## Use it in Batcomputer

1. Use **Download latest.usmap** above and save the file. Download the file itself, not this README or a GitHub HTML page.
2. In Batcomputer's first-time setup or **Settings → Paths**, select that file as your mappings.
3. After a game update, refresh all character assets and rebuild the part index before rebuilding affected mods. Updating the mapping alone does not refresh an old asset extraction.

Batcomputer keeps a local copy of your selected mapping. Replacing the download here does not silently update your installation.

## Does this match my game?

Compare the game-build details above with the build you are using. **Latest** means the newest mapping published here, not a guarantee that it fits every past or future game update. If your game updated beyond the listed build, wait for a verified matching mapping rather than forcing builds with an old one.

If extraction or validation fails, include your game build, mapping filename and the first relevant error when reporting it. Do not upload extracted game assets. See the [setup guide](https://loomirr.github.io/Batcomputer/getting-started/setup/) and [update/repair guide](https://loomirr.github.io/Batcomputer/guides/update-repair-suit/).

This folder provides mappings only, not game files, mods or the LOTDK UE4SS framework.
