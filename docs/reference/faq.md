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

### When should I choose a Native body profile?

Set the visual base and gameplay donor first. Then open **Parts** → **Native body profiles** and
leave the detected body alone unless the visual character uses another exact shipped body. Choose
the body before adding replacement arms, hands, heads, wings, hooks, or brick-body parts. Reduced
bodies intentionally leave their named regions empty until you add a compatible native part.

### Do I need to choose or transfer a skeleton for Minifig and Smallfig bodies?

No. All nine supported body profiles use the game's shared `SKEL_LEGOfig` skeleton. Selecting a
body changes its root mesh while keeping the gameplay donor's animation class and runtime setup.
Custom skeleton transfer is not supported.

### Can I resize the windows?

Yes. The main window, tools, and dialogs are resizable and capped to one usable monitor. If a button
is still clipped, lower Windows display scaling temporarily and include the resolution and scaling
percentage in a bug report.

## Dumps, indexes, and older projects

### Do I need to rebase every suit after updating Batcomputer?

No. If a suit opens, checks, builds, and works in-game, leave it alone. Rebase when the game dump
changed or a saved base points at an older extraction.

### Why did an older suit point at a deleted extract folder?

Older projects saved absolute cache paths alongside their `/Game` package identities. Batcomputer
now relocates those template records to the active extracted Content folder when the exact package
is present. If the exact package is missing, refresh character assets and the part index, then
re-select the visual base and gameplay donor. It will not guess a similarly named package.

### What is the difference between refreshing game assets and refreshing the part index?

**Refresh game assets** extracts current cooked character files from the game. **Refresh part
index** rebuilds the searchable part recipes from the active extracted Content folder. Updating the
index cannot repair an old or incomplete extraction.

### Why are DLC characters or parts missing from the picker?

Run **Refresh game assets** → **Refresh all character assets**, then let Batcomputer rebuild the
part index. When `Content\DLC` is installed, the refresh mounts it with the base game containers and
adds the extracted `Content/AdditionalContent` packages to the same active dump. The base picker,
part inspector, material browsers, and Research then use those DLC assets automatically.

If the refresh says it could not create the temporary base/DLC mount, either keep the workspace on
the same drive as the game or enable Windows Developer Mode. The mount contains links only; it does
not copy, move, or change the game's container files.

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

### Why did an older build say Head belonged to the cape shell?

The adapter uses a complete authored Blueprint shell, and older builds protected every component
inside it as though it belonged to the cape. Current builds protect only the actual `Cape` and
`Torso` glide pair. Right-clicking an ordinary `Head`, hair, cowl, or `Face` now hides its visual in
both character roles while leaving the safe construction node in place.

## Materials, models, and previews

### Can I reuse a material I created in another suit?

Yes. Open **Materials** and choose **All tool materials**. Tool-created materials are kept in the
workspace library and can be imported into another suit without copying them by hand. **Your
materials** shows the current suit's own set. Rename or deletion is blocked when another saved suit
still references the material. Older suits that saved only the material assignment are added to the
library automatically when their cooked material files are still present.

### Can I inspect a base-game part before applying it?

Yes. Right-click an indexed native part and choose **Inspect part in 3D**. The inspector shows the
mesh and source recipe, attachment socket, material slots, and whether each preview material came
from a component override or the mesh default. Its map switches are viewer-only.

### Can I make a custom face?

You can copy a compatible game face material and change its supported print layers. The face mesh
family still matters: standard LEGOface and SuperheroFace recipes are not interchangeable.

For imported face maps, choose **Face detail** or **Face detail normal** instead of Character
texture. Batcomputer offers the shipped compact and larger face-map sizes so the new texture can
follow the native map it replaces.

### Why are some suit icons 256px and others 512px?

The suit-selector tile under `UI/Icons/Suits` is 256px. The UIMD menu, left, and right character
portraits under `UI/Icons/Characters` are 512px. Import the tile as **Suit selector icon** and the
other three as **Character icon**; the icon window labels each field with the expected size.

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
