# Back up and move a workspace

Projects are more than their JSON recipes. Imported images, model caches, shared materials and
animation libraries can live beside them. Keep those together when making a backup.

## Before a big edit or update

1. Save your work, close Batcomputer and note the paths in **Settings → Paths**.
2. Copy **Batcomputer.settings.json** and the workspace's **Generated** folder to a separate backup location.
3. Keep your mappings and any external source art, FBXs and Blender files too.
4. If you use shared imported animations or generated materials, ensure their library folders are in the backup.

Backing up all of Generated is the simplest approach, but includes potentially large extracts,
previews and builds. For a smaller backup, use the [workspace inventory](../reference/workspace.md)
and preserve all authoring dependencies—not just the project JSON.

An **editable creator copy** is useful for supported mod handoffs. It is not a full workspace backup
and currently cannot transfer imported animation libraries.

## Move only the application

If projects already live in an external workspace, extract Batcomputer into the new application
folder, copy its settings, then verify the paths before opening a project. Do not delete the
workspace; the new application can keep using it.

If you used the default workspace beside the EXE, copy its project data into the new location as
well. Check for explicitly saved paths still pointing to the old folder. Keeping the old portable
until a project successfully opens and rebuilds makes recovery much easier.

## Move the workspace to another drive

1. Close Batcomputer and **copy** the workspace to the new writable location. Keep the old one for now.
2. Open Batcomputer and update **Settings → Paths → Workspace folder**.
3. Check the extracted Content, extraction-output, mappings and export-staging paths separately. Changing the workspace field does not guarantee every explicit path changed.
4. Open a copied project and inspect materials, source textures and custom meshes.
5. Run **Check mod** and rebuild. Confirm any recook uses the copied source files.
6. Keep both copies until you have tested the rebuilt mod in-game.

Batcomputer can relocate some known project-owned paths, but arbitrary external source locations
are not guaranteed to move automatically. Restore a missing source or use that editor's replace/
reimport action; do not patch random paths inside a cooked asset.

## What can be regenerated?

Preview output and indexes can be rebuilt. Native extracts can be recreated from the matching game
and mappings. Do not treat imported animation libraries, custom material libraries, texture source
folders or skinned-mesh caches as disposable just because they are below Generated.

Update-center backups restore **application files only**. They do not restore deleted projects or
earlier versions of artwork. Keep your authoring backup separately from the app's update transactions.
