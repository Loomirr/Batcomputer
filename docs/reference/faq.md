# Frequently asked questions

## Using Batcomputer

### Will installing 1.0 over an older version keep my projects?

Yes, if you close the app and merge the **whole** release ZIP into the same folder without deleting
your settings or workspace. Back up first. Don't copy just the EXE, and don't delete the old folder
before extracting. See [Install/update](../getting-started/install.md#updating-an-existing-installation).

### Does the updater update my mods or UE4SS too?

No. It updates Batcomputer's application files. Your projects, game files and installed mods stay
where they are. The game-side framework is a separate installation. See [Updates](../guides/app-updates.md).

### Can I change a custom character's pawn tag without changing its name?

Yes. Edit **Pawn-tag family** on the default character's **Character identity & modes** screen.
Its project ID stays fixed and child suits inherit the new family. Display names such as Poison Ivy
are fine; native NPC tag families are reserved. [Follow the identity walkthrough](../guides/character-identity.md).

### Why can't I import a player's release ZIP as an editable mod?

That ZIP contains cooked installation files, not the complete authoring recipes and sources.
Ask for **Export editable copy** instead, then use **Home → Import/Export → Import editable mod**.
See [Import/Export](../guides/import-export.md) for the differences and conflict checks.

### Can a custom character have its own symbol?

Yes. Load the character's default definition and open **Characters → Character symbol**.
Import a square PNG (64–2048 pixels, at most 4 MB) with a transparent background and clear
margins. Batcomputer uses its silhouette, not its painted colors, and cooks a 64px distance-field
icon with the native UI emblem material. No painted outline is needed.

The symbol is shared by that character's suits; it does not replace suit portraits or equipment
icons. The source PNG is embedded in the saved recipe. **Save symbol**, then rebuild the mod.
**Use default symbol** restores the registration donor's emblem. Test the character-menu symbol
in-game after installing the complete rebuilt release.

### How do I inspect a part in the character viewer?

The **Character library** has saved-library, Playable and Cutscene sources. The saved library includes both character definitions and suits. Search by suit
name, narrow native suits with the character filter, then double-click a row, press Enter or
choose **Open in workshop**. Filtering keeps your selection when it remains in the results.

Click a part or choose it in the **Character workshop** list. **Focus** (or F) frames it;
**Isolate** temporarily hides the other pieces. **Whole character** restores their previous
visibility and frames the assembly. Search the **Assembly** list, or click a surface to select its
material in the inspector. The inspector groups **Placement**, **Surfaces**, **Face**, and **Scene**
controls. Camera buttons frame front/back/side/top or three-quarter views; **Clean view** hides
both sidebars. **Diagnostics** expands the technical log only when needed.

**Scene** provides lighting presets, exposure, a floor grid and supported Red Brick previews.
Selection, camera, lighting, UV selection, material-map toggles and visibility do not alter your
saved character. **Surfaces → Part UV set** changes the selected part's preview UV channel.
Native parts cannot be moved; legacy preview-alignment nudges are ignored. **Bake to game** on a
custom static mesh updates its authored placement and requires a mod rebuild before testing in-game.

### How do I position an imported custom part with arrows?

Open a saved suit in the workshop, select its imported **custom static mesh**, and use **Placement**.
**Move (W)**, **Rotate (E)**, and **Scale (R)** control the on-model gizmo. Scale is uniform;
this does not edit skeletal weights or individual bones. Choose **Local axes** to follow the part,
or **World axes** to move along the scene axes. In world space, green is up, red is X, and blue
is depth; the numeric offsets always use the attachment socket's Unreal XYZ in centimeters.
Movement, rotation and scale have independent snap options. Exact numeric fields remain available.
Shortcuts work after selecting a part or clicking a tool button. While typing in a field or using
a dropdown, those controls keep their normal keys; click the 3D viewport to return to shortcuts.

Use **Undo/Redo** (Ctrl+Z / Ctrl+Y outside a text field) or **Reset to loaded**. Reset restores the
values from when this preview opened, not the original import. **Ghost surrounding parts** and
**Show attachment origin** help with fitting; neither changes the mod or exported materials.
Native parts remain read-only, and **Surfaces → Part UV set** and hide/show still work.

Placement drafts auto-save to the project, including undo/reset changes. **Bake to game** waits
for pending drafts, rebuilds the game mesh and reloads the preview. If a draft fails, use
**Retry draft save** before baking. Rebuild the mod afterward to test placement in-game.
History is local to this open preview; it is cleared on reload. Drafts are not an installed mod.

### Which meshes and material effects does the viewer include?

The assembly includes mesh components from the character's visual Blueprint, including gliders.
Gliders are displayed upright beside the character for inspection, not in their gameplay position.
Native hidden components start hidden; select one and use **Show part**, or **Show every part**.
These are visual components, not an exhaustive simulation of dynamically spawned ability actors.
Native permanent attachment-manager bone offsets are applied to matching preview bones and rigid
attachments; animation-driven offsets, cloth and runtime-only procedural poses are not simulated.
The Red Brick preview applies to supported masks on attachment materials as well as the body.

Printed/decal normals and structural LEGO normals use their separate UV channels. Where the
material enables it, the viewer also reads the inherited micro-noise texture and intensity.
This is an approximation of the game shader; lighting and fine detail will not be identical
to Unreal. Reopen the character after updating Batcomputer to regenerate its preview.

### Can a custom chest part make room at the neck?

Yes. Edit the custom static mesh, choose **Chest**, **Second chest**, **Collar**, or **Back attachment / cape**,
turn off **Use default clearance**, and set **Body clearance**. These regions default to zero so
existing imports keep their shape. Shoulder/neck retain their 3.0 default; hip/belt retain 4.3.

Clearance uses native body-bone offsets in gameplay and cutscenes, separate from moving the
accessory mesh. The largest clearance wins; values do not stack. Zero adds no clearance and
does not remove a native donor's clearance. Rebuild and check in-game: the viewer does not yet
simulate runtime animations. Its matching rig now previews the staged permanent offsets.
Arbitrary custom-bone offsets are not exposed by this control.

### Can I export the assembled character to Blender?

Choose **Export GLB…** in the character workshop, then import that file as glTF in Blender.
It includes the loaded parts at their preview placements, available skinning/rigs and approximate
PBR materials. Isolation does not remove other parts from the export. Hidden material layers stay
hidden. Temporary hide/show choices do not change the export. Textures are embedded at up to
2048px; let the preview finish loading first. Unreal vertex-color masks that are disabled in the
viewer are omitted from GLB, so Blender does not multiply them into the surface colours.

This is a reference assembly, not a merged rig or a game-ready FBX. Unreal shaders (including
face effects and the separate LEGO/micro-normal shader layer), animation blueprints, cloth
physics and gameplay logic are not exported.

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

### Why do takedowns stop after switching Minifig and Smallfig?

The shared skeleton does not make every synchronized animation interchangeable. Native body
meshes also carry Minifig/Smallfig compatibility tags, while the gameplay donor retains its
original takedown sequences.

In **Ability workshop → Takedowns**, you can explicitly test **Minifig · Batman takedowns** or
**Smallfig · Robin takedowns**. Save the loadout and rebuild the mod. These experimental presets
replace only the selected melee set's main and existing end-of-encounter takedown grants—not
the fighting style, counters, grabs, movement or equipment. Cross-character behavior and alignment
still require in-game testing. They do not adapt arbitrary Blender-scaled or custom-rig meshes.

**Restore current melee set's takedowns** reverses these grant edits. Applying a fighting-style
bundle afterward can replace the takedown selection, so choose the style first.

### Can I resize the windows?

Yes. The main window, tools, and dialogs are resizable and capped to one usable monitor. If a button
is still clipped, lower Windows display scaling temporarily and include the resolution and scaling
percentage in a bug report.

### Can I change Batcomputer's theme?

Yes. Open **Settings** → **Visual** and choose **Classic**, **Alternate**, or **Mayhem Mode**. Each
theme selects its own header and accent palette. Classic uses gold, Alternate uses blue, and Mayhem
Mode uses its matching window icon with purple and lime highlights. Save the setting to apply it to
the current window and every tool you open afterward. The dark layout, category colors, and
warning/error colors intentionally stay the same.

## Animations

### Does replacing an animation change it for every character?

No. Batcomputer records an exact action/context override in the current suit and patches that
suit's generated animation composition during packaging. The base-game donor, other characters,
and other suits remain unchanged. Assign the same replacement separately if another suit should
use it too.

### Are imported animations available to my other suits?

Yes. The imported animation library is shared across the current Batcomputer workspace. Choose
**Animations** → **Imported animation library**, or use the **Imported** filter in the replacement
picker, to see everything you have imported.

### Why is an imported animation visible but disabled?

The Imported filter intentionally shows the complete library. A disabled row cannot satisfy the
selected target's required asset class, is quarantined/incomplete, or is not held in the managed
cache. Select it to read the reason, then choose a usable sequence or montage. A complete animation
on an unverified rig is not silently blocked, but Batcomputer shows a critical experimental warning
before it can be assigned.

## Dumps, indexes, and older projects

### Do I need to rebase every suit after updating Batcomputer?

No. If a suit opens, checks, builds, and works in-game, leave it alone. Rebase when the game dump
changed or a saved base points at an older extraction.

### Why did an older suit point at a deleted extract folder?

Older projects saved absolute cache paths alongside their Unreal package identities. Batcomputer
now relocates those template records to the active extraction when the exact `/Game` or installed
DLC Game Feature package is present. If the exact package is missing, refresh character assets and
the part index, then re-select the visual base and gameplay donor. It will not guess a similarly
named package.

### What is the difference between refreshing game assets and refreshing the part index?

**Refresh game assets** extracts current cooked character files from the game. **Refresh part
index** rebuilds the searchable part recipes from the active extracted Content folder. Updating the
index cannot repair an old or incomplete extraction.

### Why are DLC characters or parts missing from the picker?

Run **Refresh game assets** → **Refresh all character assets**, then let Batcomputer rebuild the
part index. When `Content\DLC` is installed, the refresh mounts it with the base game containers.
Batcave display assets usually live under `/Game/AdditionalContent`, while the real playable and
cutscene Blueprints use separate mounts such as `/DLC_BeyondPack`. Batcomputer reads both layouts,
including their parts, metadata, animation donors, and materials.

Owning a pack is not enough if its container has not downloaded yet. Batcomputer only lists DLC
that is actually present in the game's `Content\DLC` folder; a future or uninstalled pack cannot be
indexed. After Steam installs a pack, run the full refresh again.

The workspace may be on a different drive from the game. Batcomputer automatically creates the
temporary link folder on the game drive when hard links would otherwise cross volumes, then removes
only that owned folder after extraction. If link creation still fails, confirm Batcomputer can
create temporary files beside the game `Content` folder. The mount does not copy, move, or change
the game's container files.

### Why did selecting a saved suit say its staged Blueprint was in use?

Older builds could let the Inspector or 3D preview read `GraftedPartStage` while the same Batcomputer
window was restoring that stage. The current build waits for the restore before refreshing those
views and retries short Windows/OneDrive sharing locks for several seconds. Close truly external
asset viewers if the message persists.

### What is the safe order for repairing an old suit?

Use current mappings, refresh the character assets, choose **Refresh part index**, rebase the suit to
the current dump, then open **Base** and choose **Use as base**. Finish with **Check mod**, rebuild,
and cold-launch the game. The full walkthrough is in [Update or repair a suit](../guides/update-repair-suit.md).

### Will rebasing erase my parts, materials, or custom meshes?

Rebasing changes the saved base source paths. **Use as base** then replays the saved parts,
removals, materials, and custom-mesh recipes. Batcomputer restores the previous project and generated
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

In the 3D viewer, select a `Face` entry in **Material editor** to see which texture feeds each face
region. Use **Solo layer** to identify it on the model and **Restore face** when finished. This does
not alter the suit.

### Why are some suit icons 256px and others 512px?

The suit-selector tile under `UI/Icons/Suits` is 256px. The UIMD menu, left, and right character
portraits under `UI/Icons/Characters` are 512px. Import the tile as **Suit selector icon** and the
other three as **Character icon**; the icon window labels each field with the expected size.

### Why did transparent pixels in my imported texture turn black?

Recook the texture with the current build. RGBA profiles now preserve straight-alpha RGB through
resize and mip generation, including colour under fully transparent pixels. DXT1 has no alpha and
BC5 stores only two normal channels, so choose BGRA8, DXT5, or BC7 when the material needs alpha.

### Can I import a model?

Batcomputer 1.0 supports OBJ static-mesh attachments and an
experimental [existing-rig FBX workshop](../guides/skeletal-mesh-proof.md) for bodies and compatible
parts. Rigging and weights must be prepared externally first; arbitrary skeleton transfer, facial
rigs and cloth are not supported. Native-rig skeletal equipment has its own
[equipment workshop](../guides/equipment-workshop.md#skeletal-equipment) flow.

### Why did my custom mesh move back after another edit?

Open it in the 3D viewer, save the scale/position/rotation, and choose **Bake to game**. Current
projects keep those baked values through later part-removal and base-replay rebuilds. If an older
project still resets, edit and bake that mesh once with the current version.

### Can I create Red Bricks?

No. The Red Brick selector is a read-only preview of the game's existing colour palettes for
compatible playable bodies. It does not create, unlock, register, or package Red Bricks.

## Building and sharing

### Why does a hip material error mention a torso, or a build say its stage is incomplete?

Material changes replay the whole saved suit, so a previously failed part can block an unrelated
material edit. The current build preserves native animation/component defaults and fixes
premature part/glider saves that could leave failed edits behind. Build Mod waits for saved-suit
restoration and retries an incomplete stage once automatically, without rewriting the saved recipe.

If recovery still fails, the error names the part or operation. Open that suit, reapply/remove the
named part or repair the named material/model, then choose **Build Mod** again. Close any viewer
holding the generated files when the error specifically reports a file lock. Waiting alone cannot
fix a missing donor or invalid recipe; partial playable/cutscene stages stay blocked from packaging.

### Why does building require Unreal Engine 5.6?

The game needs native Asset Registry data for custom assets. Batcomputer normally uses its bundled
writer module with UE 5.6, so it does not compile anything. It uses the included source fallback
only when the installed editor has a different compatible `BuildId`.

### Where is the installable ZIP action?

Build the mod first. On **Home** → **Import/Export**, select the mod and choose **Create release ZIP**.
Extract into the game's installation directory so the archive's `LEGOBatmanLotDK` folder merges with
that existing folder. Check the paths before confirming; don't nest a second copy inside it.
For editable handoffs, use **Export editable copy** instead. See [Import/Export](../guides/import-export.md).

### Why should I fully restart the game after installing a build?

Unreal discovers gameplay tags, registry rows, and primary assets during startup. Returning to the
frontend is not always enough to load a newly installed build.
