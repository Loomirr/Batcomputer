# Import and export projects

Open **Home → Import/Export**. Select the mod you want under **Select mod to export**;
the heading shows which mod the export actions will use.

## Pick the right archive

| Archive | For | What to do with it |
| --- | --- | --- |
| Release ZIP | Players | Install the built mod into the game |
| Editable creator copy | Another creator or a project handoff | Import into Batcomputer |
| Batcomputer application ZIP | Updating the editor | Extract into the application folder, not the game or project importer |

A cooked mod ZIP cannot be turned back into a complete editable project. Ask the author for an
**editable copy** if you need their recipes and source assets.

## Export a player release

1. Save the mod's edits and check which suits, characters and vehicles are enabled.
2. Open **Home → Build mod**, run **Check mod**, then build successfully.
3. Return to **Import/Export** and select the same mod.
4. Choose **Create release ZIP**.
5. Test that exact archive after a full game restart, then share it with requirements and known limits.

If you see **Build before packaging**, the selected mod has no complete build to package.
If you changed anything since building, rebuild first: exporting a ZIP does not apply new edits
to the previous build automatically.

The archive includes game-relative registry/tag files as well as the pak/ucas/utoc trio.
Install the full bundle; copying only the pak is not enough. See [Build, test, and share](build-test-share.md).

## Export an editable copy

1. Save the projects, then select their mod on **Import/Export**.
2. Choose **Export editable copy** and save the creator archive.
3. If a source file is missing, restore it and export again. Do not send an incomplete archive.
4. Tell the recipient which game build, DLC and Batcomputer version you used.

The archive carries the mod/suit/vehicle recipes and supported project-owned source images, mesh
imports/caches and custom material dependencies. Native game content remains a reference; it does
not include the game, mappings, UE4SS or saves. Keep your original Blender/artwork files separately
if they were never imported into Batcomputer.

Imported custom **animation libraries are not transferred yet**. Projects depending on them are
blocked from editable export rather than sent with missing files. A creator archive is therefore
not a replacement for a [complete workspace backup](backups-and-moving.md).

## Import another creator's archive

1. Back up your workspace, or use a separate workspace for an unchanged handoff.
2. Choose **Home → Import/Export → Import editable mod** and select the creator archive.
3. Read any conflict report. Import does not overwrite existing projects with duplicate identities.
4. Open the imported mod, review its enabled content and native donors, and confirm your extraction is current.
5. Run **Check mod**, rebuild and test. The sender's successful build is not a substitute for your local dependencies.

Conflicting Mod IDs, suit/vehicle IDs, pawn tags or character package paths block import.
Identical shared materials can be reused; a different material at the same path is a conflict.
Do not rename files inside the ZIP to bypass those checks. Use a separate workspace or resolve the
project's identity in the editor before exporting again.
