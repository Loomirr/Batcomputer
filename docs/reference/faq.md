# Frequently asked questions

## Using Batcomputer

### Do players need Batcomputer?

No. Players need Loomirr's LOTDK UE4SS 0.1.1 or newer and the finished mod. They do not need
Batcomputer, Unreal Engine, mappings, or your extracted workspace.

### Can one mod contain several suits?

Yes. One mod can contain multiple enabled suits. Its shared StringTable and registry files are
generated once.

### Can suit mods from different authors be installed together?

Yes, as long as every release has unique Mod IDs, suit IDs, PawnTags, and package paths. Individual
suit mods must not include or overwrite the shared `LOTDKExpandedCoreRegistry`.

### Does this only work for Batman?

No. Choose a PawnTag for the intended character family and an appropriate gameplay donor. The
visual base can come from a different playable, cutscene, or supported `_Quest` character.

### Why are the visual base and gameplay donor separate?

A character can have the look you want without having a complete playable setup. The visual base
supplies the appearance; the gameplay donor supplies movement, equipment, and other playable data.

### Can I resize the windows?

Yes. The main window, tools, and dialogs are resizable and capped to one usable monitor. If a button
is still clipped, lower Windows display scaling temporarily and include the resolution and scaling
percentage in a bug report.

## Dumps, indexes, and older projects

### Do I need to rebase every suit after updating Batcomputer?

No. If a suit opens, checks, builds, and works in-game, leave it alone. Rebase when the game dump
changed or a saved base points at an older extraction.

### What is the difference between refreshing game assets and refreshing the part index?

**Refresh game assets** extracts current cooked character files from the game. **Refresh part
index** rebuilds the searchable part recipes from the active extracted Content folder. Updating the
index cannot repair an old or incomplete extraction.

### What is the safe order for repairing an old suit?

Use current mappings, refresh the character assets, choose **Refresh part index**, rebase the suit to
the current dump, then open **Base** and choose **Use as base**. Finish with **Check mod**, rebuild,
and cold-launch the game. The full walkthrough is in [Update or repair a suit](../guides/update-repair-suit.md).

### Will rebasing erase my parts, materials, or custom meshes?

Rebasing changes the saved base source paths. **Use as base** then replays the saved parts,
removals, materials, and custom-mesh recipes. Beta 7 restores the previous project and generated
stages if that replay fails, but you should still keep a backup before a game update.

### Why does the inspector show zero components after selecting a base?

The selected base may be missing from the current dump, or its playable/cutscene stage may not have
finished. Refresh the assets and part index, rebase or select both donors again, then choose **Use as
base**. Do not package the suit while the base or saved-edit replay is incomplete.

### Why does a part or material say its donor cannot be resolved?

The saved project points at a donor that is absent from the active part index, or an older project
does not contain enough information to identify it safely. Refresh the current assets and part
index. If the exact donor still cannot be recovered, remove and reapply only the named part or
material.

### Can I use Batmite or another `_Quest` character as the visual base?

Supported extracted `_Quest` Blueprints appear as visual bases, including Smallfig characters such
as Batmite. They still need an explicit playable gameplay donor, such as a compatible Robin setup.

## Capes and gliders

### Can Nightwing keep his normal playstyle and use a regular cape?

Yes, when you use a supported native **Glide cape** preset and the matching regular cape from the
same character variant. Batcomputer keeps Nightwing's appearance and normal playset, then uses the
cape donor's matching animation while gliding.

### What order should I apply the cape and glider?

Apply the **Glide cape** preset first with **Use preset**. Then open Parts and apply the matching
regular `Cape` from that exact same native character variant. Finish with **Check mod**.

### The glider mesh belongs to Torso in the game. Should I add it as a Torso part?

No. Use the Glider preset. It keeps the authored component, pose, materials, and visibility setup,
including cases where the game stores the visual through the torso assembly.

### Why does Batcomputer block my cape/glider combination?

A regular cape cannot safely be mixed with an unrelated wingsuit or glider controller. Use a
supported **Glide cape** preset and its matching native cape, or remove the regular `Cape` and keep
the glide-only visual.

## Materials, models, and previews

### Can I make a custom face?

You can copy a compatible game face material and change its supported print layers. The face mesh
family still matters: standard LEGOface and SuperheroFace recipes are not interchangeable.

### Can I import a model?

The beta supports verified OBJ static-mesh attachments. It does not cook arbitrary skeletal meshes
or transfer skeletons.

### Why did my custom mesh move back after another edit?

Open it in the 3D viewer, save the scale/position/rotation, and choose **Bake to game**. Current
projects keep those baked values through later part-removal and base-replay rebuilds. If an older
project still resets, edit and bake that mesh once with the current version.

### Can I create Red Bricks?

No. The Red Brick selector is a read-only preview of the game's existing colour palettes for
compatible playable bodies. It does not create, unlock, register, or package Red Bricks.

## Building and sharing

### Why does building require Unreal Engine 5.6?

The game needs native Asset Registry data for custom assets. Batcomputer normally uses its bundled
writer module with UE 5.6, so it does not compile anything. It uses the included source fallback
only when the installed editor has a different compatible `BuildId`.

### Where is the installable ZIP action?

Build the mod first. On **Home** → **Build mod**, choose the **Zip _mod name_** tile. The archive is
already arranged above `LEGOBatmanLotDK` for extraction into the Steam `common` folder.

### Why should I fully restart the game after installing a build?

Unreal discovers gameplay tags, registry rows, and primary assets during startup. Returning to the
frontend is not always enough to load a newly installed build.
