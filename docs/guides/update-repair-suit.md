# Update or repair a suit

You do not need to rebase a suit just because a new Batcomputer build came out. Rebase when the
game dump changed, the suit points at an older extraction, or Batcomputer can no longer resolve its
saved base or parts.

Good reasons to use this flow include:

- The game updated and you have new mappings.
- The base picker keeps reopening or a previously working base no longer loads.
- The inspector shows no components after a failed base change.
- A saved part says its playable or cutscene donor cannot be found.
- Diagnostics says the part index belongs to another extracted Content folder.
- Opening the suit reports that a saved base package is missing from the active extracted Content
  folder.

If the suit already opens, checks, builds, and works in-game, leave it alone.

When an older project contains a retired absolute extract path, Batcomputer first tries the exact
saved `/Game` package under the active Content folder and updates the cache path automatically. A
manual rebase is needed only when that exact package is not present or the game data itself changed.

## Before you start

1. Close the game and any asset tools using the same files.
2. Back up `Batcomputer.settings.json`, `Generated/NativeSuitProjects`, and
   `Generated/NativeSuitModProjects`.
3. Keep the source images and OBJ files used by the project.

## 1. Make the dump current

Open Setup and confirm that the `.usmap`, game `Content\Paks` folder, and active extracted Content
folder all belong to the current game build.

After a game update, use the main menu:

1. **Refresh game assets** → **Refresh all character assets**.
2. Wait for extraction and validation to finish.
3. Choose **Refresh part index**.

Refreshing the part index does not extract game files. It rebuilds Batcomputer's searchable part
recipes from the active Content folder. If the assets themselves are old or incomplete, refresh
them first.

## 2. Rebase the suit

1. Open the affected suit.
2. Open the main menu and choose **Rebase suit to current dump…**.
3. Read the preview before confirming.

If the preview says a template is missing, stop and refresh that character's assets or pick the base
again. Rebase changes the saved source paths; it does not change the suit's `/Game/Mods/...` output
identity.

## 3. Re-stage the base

After the rebase finishes, open **Base** and choose **Use as base**. This rebuilds the playable and
cutscene stages from the current dump and replays the saved parts, removals, materials, and custom
mesh recipes.

Beta 7 keeps the previous project and generated stages if replay fails. Read the first useful error
in Diagnostics instead of repeatedly pressing **Use as base**.

## 4. Fix anything that cannot replay

Most older projects recover automatically when the exact native donor still exists. If one item
does not:

- Refresh the part index once more and retry.
- Remove and reapply only the named part from the current index.
- For a missing material donor, choose the current native material again.
- If the error names a missing **workspace material source**, open **Materials** → **Your
  materials** and choose **Repair materials**. This recovers the existing material closure and
  reapplies the saved assignments without changing their authored values.
- To refresh every generated texture from its saved PNG in one pass, open **Textures** and choose
  **Reimport all**. The batch is backed up and rolled back together if one cook fails.
- For a custom mesh, confirm its project-owned OBJ source still exists.
- If both base templates are missing, return to the base picker and select the visual and gameplay
  donors again.

Do not replace an unresolved donor with a similarly named character at random. Playable and
cutscene recipes can look alike while containing different component links.

## 5. Check, build, and test

1. Run **Check mod** and resolve every error.
2. Choose **Build mod**.
3. Fully restart the game.
4. Test menu hover, selection, normal gameplay, equipment, gliding, and one return to the frontend.

For a mod with several suits, repair and test one suit first. Once that works, repeat the same flow
for the rest.

Still stuck? Work through [Troubleshooting](../help/troubleshooting.md) or
[report the problem](../help/reporting-issues.md) with the relevant Diagnostics lines.
