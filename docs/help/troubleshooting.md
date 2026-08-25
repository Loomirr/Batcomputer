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
- Select the game's original `Content\Paks` folder, not `~mods`.
- Close FModel or other tools reading the same output.
- Run the full character refresh again after a game update.
- Confirm the final log reports parsed assets with zero errors before trusting the indexes.
- Confirm Diagnostics reports a nonzero `Character-supporting gadget assets=` count. Equipment and
  glider materials are stored under `Content/Models/Gadgets`, outside the main Characters folder.
- Check the **active extracted Content** path in Setup. An empty `ExtractedPakData` folder from an
  older portable layout is not used automatically.

### The part browser is empty or has the wrong characters

Open the main menu and choose **Refresh part index**. The index is tied to the active extracted
Content folder, so an index built from another dump is intentionally rejected.

If rebuilding still finds no parts, the extraction itself is incomplete. Run **Refresh game
assets** → **Refresh all character assets** first, then rebuild the index again.

## An older suit no longer loads its base

Use this order:

1. Confirm current mappings and the active extracted Content folder in Setup.
2. Refresh all character assets.
3. Choose **Refresh part index**.
4. Open the suit and choose **Rebase suit to current dump…** from the main menu.
5. Open **Base** and choose **Use as base** to re-stage and replay the saved edits.

If the rebase preview says a template is missing, stop and extract that character again or select a
new base manually. See [Update or repair a suit](../guides/update-repair-suit.md).

## The inspector shows no components after a base change

Do not rebuild the mod yet. Check Diagnostics for the first base-stage or donor error. Refresh the
current assets and part index, then rebase or select both the visual and gameplay donors again.

Beta 7 restores the previous project and generated stage when a replay fails. If Diagnostics lists a
recovery backup, keep that folder until the suit opens correctly.

## The 3D viewer is blank

- Install or repair Microsoft Edge WebView2 Runtime.
- Confirm mappings and the game Paks path are current.
- Refresh extracted game assets.
- Try another built-in playable to separate a project problem from a viewer problem.
- Copy diagnostics, including texture decode or GLB generation lines.

The viewer is an approximation. A material can look somewhat different under the game's
lighting without being broken.

## A generated material does not appear

- Confirm a suit and mod are active.
- Reopen the target component and material slot.
- Check that the donor material parsed and the generated package path was logged.
- For faces, confirm the template supports the selected face mesh family.
- Reopen the suit if it was created before generated-material metadata was introduced.

### Material apply says a saved part donor cannot be resolved

Material changes replay the suit's saved parts onto a clean base before the new assignment is
committed. Refresh the part index. If the exact named donor is still missing, remove and reapply
that part from the current index, then apply the material again. The previous saved project stays
active when this operation fails.

## A custom mesh stays gray

- Apply the material to the custom mesh's own slot, not CharacterMesh0.
- Reopen the 3D viewer after assigning it.
- Confirm source textures resolve in diagnostics.
- Save the transform and choose **Bake to game** before packaging.

### A custom mesh returns to its original size or position

Edit the mesh in the current version, save its transform, and choose **Bake to game**. The baked
recipe is replayed during later part removals and base rebuilds. Keep the project-owned OBJ source;
if it is missing, Batcomputer cannot rebuild that attachment.

## A UI icon is corrupt or crashes on hover

- Recook it with the current verified native suit-icon profile.
- Do not reuse an old experimental DXT5/BC7 cooked output.
- Confirm the staged texture exists and FModel displays it.
- Validate the package before launching.

A texture showing `PF_Unknown`, zero dimensions, missing mips, or nonsensical mip sizes is not safe
to test in-game.

## A suit texture is corrupt unless Texture Quality is Epic

Epic quality can hide a broken lower mip; it does not repair the texture. Rebuild the texture with
the current cook profile:

1. Keep or restore the original source PNG.
2. Open the texture and confirm its role is correct: color, mask/packed, normal, or UI.
3. Use **Change cook profile** if this is an older project with no saved profile.
4. Build again. Batcomputer will recook older output and require one verified file set before it can
   be staged.
5. Check the new asset in FModel. A 2K character texture should list every mip from 2048 through 1.

Do not tell users to leave Texture Quality on Epic as the workaround. If the current cook still
changes appearance between quality levels, include the source PNG, texture role, cook profile, and
Batcomputer diagnostics in the report.

## Metalness or roughness looks wrong

Check that the packed texture uses **Native 2K DXT1 MMR**, not a Character texture or the older
BGRA8 packed-map route. The native MMR layout reads red as metalness and blue as roughness; green is
unused. For an older saved texture named like `BodyMMR` or `T_Body_ORM`, right-click it and use
**Change cook profile**. The current build recognizes that suffix, offers the MMR profile, and only
updates the saved role after a successful recook.

If the channels are correct but the material still looks wrong, confirm that the material's MMR
parameter points at the newly cooked `/Game/Mods/...` texture rather than the old donor or a base
game texture.

## The suit does not appear in the menu

1. Fully restart the game.
2. Confirm Loomirr's LOTDK UE4SS and the mod's registry plugin loaded.
3. Confirm the generated tag INI is under the game's `Config\Tags` folder.
4. Check the build results for a duplicate PawnTag, suit ID, or DCMD package path.
5. Confirm the intended character family is encoded in the PawnTag.
6. Rebuild from current post-update donors.

## A `_Quest` visual returns to the base picker

Quest-only characters are visual bases, not gameplay donors. Select the `_Quest` character as the
visual base, then choose an explicit compatible playable donor when prompted. Refresh the full
character assets and part index if the `_Quest` Blueprint is not listed.

## A cape/glider combination is blocked

- Apply a supported native **Glide cape** preset first.
- Apply the regular `Cape` from that exact same character variant second.
- Do not add the glide visual manually as a Torso part.
- For a wingsuit or unrelated character glider, remove the regular `Cape`.

Batcomputer blocks mismatched controllers because they can leave both visuals active, use the wrong
glide pose, or crash the game.

## A window does not fit the display

Resize or maximize the window. Batcomputer's windows and dialogs now adapt to a usable single
monitor. If controls are still clipped, record the monitor resolution and Windows scaling
percentage, try a lower scaling value, and include both values with a screenshot in the report.

## Access denied while building or installing

Close the game, FModel, UAssetGUI, Explorer preview panes, and any previous Batcomputer instance that
may hold output files open. Then rebuild. Do not keep your Batcomputer workspace under `Program Files`.

## The game crashes

Restore the last known-good release, then test one change at a time. Collect:

- Batcomputer diagnostics.
- Relevant Loomirr's LOTDK UE4SS log lines.
- Exact action that triggered the crash.
- Whether a clean cold launch crashes before opening the suit menu.

Do not repeatedly test a texture or cooked asset that already fails extractor validation.

Also remove or move aside the last test mod before testing its replacement. Keeping two builds with
the same identities in `~mods` can make it look like the new build is still broken.

Still stuck? [Report a problem](reporting-issues.md).
