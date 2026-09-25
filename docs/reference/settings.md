# Settings and preview performance

Open **Settings** from the main menu. Use **Paths**, **General**, **Visual**, and **Preview**
for their respective options, then choose **Save**. The displayed version links to Updates.

## Path indicators

Hover over a status dot for the resolved path and the reason behind it.

| Indicator | Meaning |
| --- | --- |
| Green | The configured or automatically resolved path passed its basic checks |
| Grey | An output/staging location is created on demand; it is not a missing required input |
| Red | A required file/folder is missing, invalid or incompatible |

A blank field can mean **automatic**, not broken. The hover text tells you which location will
actually be used. Green does not certify that every asset has extracted, that a DLL will load,
that a folder is writable, or that a mod will work in-game.

- **Mappings:** an existing non-empty `.usmap`; it still must match your installed game build.
- **Unreal Engine:** a UE 5.6 root with the command-line editor, not a random Engine subfolder.
- **Game Paks:** the game's container folder with `global.utoc` and pakchunk containers, not `~mods`.
- **Extracted Content:** the Content root containing `Characters`, not an unrelated parent folder.
- **Oodle runtime:** your local DLL; the runtime is not bundled or downloaded for you.
- **Workspace / extraction output:** writable destinations for projects and generated data.

See [Setup](../getting-started/setup.md) before changing these paths.

## General

**Run first-time setup again** reopens the guided path/extraction workflow.

**Keep previous asset extracts** retains the old dump when refreshing. Turning it off allows the
replaced dump to be deleted, saving substantial space. Back up authoring projects first and do not
store your own source files inside an extraction folder.

**Clean generated 3D previews automatically** removes older generated preview folders before the
next preview. Turn it off only if you need those models/textures for inspection; it is not a project backup setting.

Review options control the detailed table, category grouping and timestamps. See
[Review and remove edits](../guides/review-changes.md). Developer research tools are optional and
not required for ordinary mod creation.

## Visual

Choose **Classic**, **Alternate** or **Mayhem Mode** for the header/accent theme. This does not
change a character's game mode. Disable **Enable animations** if you prefer less UI motion.
The minifig character-panel option in General changes the editor's presentation, not the game model.

## Preview

| Setting | What it changes |
| --- | --- |
| Preview quality | Vehicle renderer resolution: Memory saver, Balanced or High detail |
| Viewer frame-rate cap | Rendering cap: 30, 60, 120 FPS or unlimited |
| Detailed attachment budget | Number of detailed vehicle attachments kept loaded; older selections become markers |
| Vehicle geometry cache | Size limit for the rebuildable vehicle GLB cache |
| Protect against oversized custom bodies | Uses the donor shell for an oversized body's offline preview |
| Custom-body safe limit | Cooked-file threshold used by that protection, not a RAM limit |

For a slow vehicle workshop, start with **Memory saver**, **30 FPS** and a smaller detailed-part
budget. Leave oversized-body protection on unless you understand the memory cost. Reopen the
workshop after changing these settings. Seeing the donor shell under this protection does **not**
mean your custom body was removed from the build.

Preview settings do not reduce your exported mesh or change final in-game materials. They also do
not fix invalid FBX weights or rest-pose mismatches.
