# Troubleshooting

Start with **Diagnostics → Copy log**. Keep the relevant lines from the first error through the end
of the action.

## Setup keeps reopening

Batcomputer silently skips setup only when the required paths resolve and the extracted Content has
a real `Characters` folder. Check:

- Current `.usmap` exists.
- Game `Content\Paks` folder exists.
- Extracted Content points at the Content root, not an unrelated parent.
- The bundled `Tools\retoc-oodle\retoc.exe` is present.

## Extraction fails or indexes are empty

- Ensure roughly 18 GB is free at the extraction destination.
- Select the shipped game `Content\Paks` folder, not `~mods`.
- Close FModel or other tools reading the same output.
- Run the full character refresh again after a game update.
- Confirm the final log reports parsed assets with zero errors before trusting the indexes.

## The 3D viewer is blank

- Install or repair Microsoft Edge WebView2 Runtime.
- Confirm mappings and the game Paks path are current.
- Refresh extracted game assets.
- Try another shipped playable to distinguish a project problem from renderer startup.
- Copy diagnostics, including texture decode or GLB generation lines.

The viewer is an authoring approximation. A material can look somewhat different under the game's
lighting without being broken.

## A generated material does not appear

- Confirm a suit and mod are active.
- Reopen the target component and material slot.
- Check that the donor material parsed and the generated package path was logged.
- For faces, confirm the template supports the selected face mesh family.
- Reopen the suit if it was created before generated-material metadata was introduced.

## A custom mesh stays gray

- Apply the material to the custom mesh's own slot, not CharacterMesh0.
- Reopen the 3D viewer after assigning it.
- Confirm source textures resolve in diagnostics.
- Save the transform and choose **Bake to game** before packaging.

## A UI icon is corrupt or crashes on hover

- Recook it with the current verified native suit-icon profile.
- Do not reuse an old experimental DXT5/BC7 cooked output.
- Confirm the staged texture exists and FModel displays it.
- Validate the package before launching.

A texture showing `PF_Unknown`, zero dimensions, missing mips, or nonsensical mip sizes is not safe
to test in-game.

## The suit does not appear in the menu

1. Fully restart the game.
2. Confirm LOTDK Expanded and the mod's registry plugin loaded.
3. Confirm the generated tag INI is under the game's `Config\Tags` folder.
4. Check release preflight for duplicate PawnTag, suit ID, or DCMD package paths.
5. Confirm the intended character family is encoded in the PawnTag.
6. Rebuild from current post-update donors.

## Access denied while building or installing

Close the game, FModel, UAssetGUI, Explorer preview panes, and any previous Batcomputer instance that
may hold output files open. Then rebuild. Avoid authoring directly under `Program Files`.

## The game crashes

Restore the last known-good release, then test one change at a time. Collect:

- Batcomputer diagnostics.
- Relevant LOTDK Expanded/UE4SS log lines.
- Exact action that triggered the crash.
- Whether a clean cold launch crashes before opening the suit menu.

Do not repeatedly test a texture or cooked asset that already fails extractor validation.

Still stuck? [Report a problem](reporting-issues.md).
