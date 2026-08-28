# Changelog

## Unreleased

- No changes yet.

## 0.9.0-beta.9 — 2026-08-27 (corrected 2026-08-28)

- Paired cape/glider cleanup now lets ordinary `Head`, `Face`, hair, and cowl visuals be removed in
  both character roles. Batcomputer preserves the invisible authored construction node needed for
  runtime safety, while the actual `Cape` and `Torso` glide visual remain one atomic pair.
- Inspector removal checks playable and cutscene eligibility before offering the action. Custom-mesh
  removal keeps its project OBJ and prior recipe until both roles are rebuilt, saved, and certified.
- Material-slot menus now say when they remove the entire part and use one component-level removal
  instead of making a secondary material slot look independently removable.
- Adds separate verified 512px character-portrait and 256px suit-selector icon cooks, and labels the
  four UIMD fields with the size they actually use.
- Adds native compact and larger **Face detail** / **Face detail normal** profiles plus dedicated CT
  and RAO cooks.
- Recognizes common `_BC`, `_MMR`, `_NRM`/`_DNRM`, `_ColorMask`/`_ColourMask`, `_CT`, and `_RAO`
  filename endings when choosing a texture use.
- First-time and full character refreshes include installed `Content\DLC` containers and index their
  extracted `/Game/AdditionalContent` characters, parts, materials, and textures with the base game.
- Bumps the portable build and documentation to beta 9.

## 0.9.0-beta.8 — 2026-08-25

- Material searches now merge the active extracted Content tree with the bundled fallback catalog,
  including the shared `Characters/Materials` instances previously missing from the Toybox and
  material forge.
- First-time extraction and **Full refresh** validate the complete recursive shared-material tree
  before switching dumps, while **Refresh part index** also invalidates and reloads live materials.
- Builds face compatibility once per view instead of reopening the workspace material library and
  scanning the full part index for every face tile.

- Adds **Reimport all** for safely recooking every saved texture recipe in one suit, with a complete
  rollback if any source or cook fails.
- Adds **Repair materials** for recovering a suit's saved material closure and reapplying its
  assignments without treating abandoned import-table names as required dependencies.
- Keeps material repair, the shared material library, the saved suit, and generated stages in one
  transaction so a failed repair restores the complete prior state.
- Writes multi-material OBJ sections, shared bounds, and area-weighted samplers in Unreal's native
  UE 5.6 order, fixing the startup crash found during the two-material cowl test.
- Extends package validation to reject missing or misaligned section samplers and buffer summaries
  before a multi-material custom mesh can reach the game.
- Adds the exact shipped Minifig and Smallfig root-body profiles while preserving the selected
  gameplay donor's runtime setup and shared native skeleton.
- Keeps tool-created materials in a workspace library so another suit can find, import, and package
  them safely, including materials recovered from older assignment-only suit projects.
- Adds a modern read-only native-part inspector with a 3D preview, exact attachment recipe, and
  resolved mesh-default or component-override materials.
- Relocates saved base-template paths to the active extract by exact `/Game` package when an older
  cache folder has been replaced.
- Keeps Diagnostics in place while reading older lines instead of forcing the log back to the end.
- Rebuilds generated textures with their complete streamed and inline mip chain, so lower texture
  quality settings no longer reveal untouched donor pixels.
- Adds a proven, in-game-verified native 2K DXT1 MMR profile with linear sampling, the game's packed
  red-metalness and blue-roughness channels, complete mips at every texture-quality setting, and
  safe recovery for older MMRs saved as Character textures.
- Treats each cooked texture package as one verified file set and blocks stale, partial, or changed
  output from being staged under an old success report.
- Packages assignment-only tool materials from the shared workspace library, fixing older suits
  that lost a generated material such as a custom cowl during a fresh build.
- Explains UnrealBuildTool's unavailable-Win64 failure with the exact Visual Studio C++, MSVC, and
  Windows SDK repair steps instead of leaving users with a generic writer exit code.
- Treats the bundled Registry writer prebuilt as part of the required portable layout and records
  why it was rejected before attempting a source build.
- Preserves the exact cooked Blueprint schema when adding an OBJ component, preventing the known
  load, hover, and startup crashes caused by that schema corruption.
- Turns each distinct OBJ `usemtl` section into its own stable material slot, keeps assignments
  attached to the material name after a re-import, and previews and packages every slot.
- Validates each custom mesh's saved OBJ, cooked package trio, live construction-script node,
  component template, socket, cooked section ranges, and role-specific materials before a suit can
  be packaged.
- Adds a paired custom-cowl material template with native LEGO surface detail and neutral
  mesh-specific maps, plus practical MMR and normal-map warnings for overly glossy materials.
- Keeps gameplay and cutscene material pairs matched, carries shared materials' mod-local textures
  and parents into another suit, and blocks stale files or package-path collisions.
- Finds a tool material's texture dependencies in the owning suit's current cooked texture output,
  so a successful texture recook no longer remains invisible to the shared material packager.
- Makes custom-material rename and deletion, shared-library staging, and 3D-viewer placement saves
  transactional so a failed rebuild restores the last working project and stage.

## 0.9.0-beta.7 — 2026-08-24

Safer project recovery, current part indexes, and working cape/glider swaps.

- A failed base selection, reselection, or saved-edit replay now restores the previous project and
  generated files instead of leaving the suit empty. Packaging stays blocked until every saved edit
  has been rebuilt successfully.
- The part index follows the active extracted Content folder and has a dedicated **Refresh part
  index** command in the main menu.
- Older saved parts recover their full native donor only from an exact package, mesh, and
  playable/cutscene match. A refresh cannot silently swap in a similarly named part.
- Base changes preserve project-owned OBJ sources, including when a rebuild cannot finish.
- Batmite `_Quest` visuals can use a Robin gameplay donor without returning to the base picker or
  losing the selected appearance.
- Glide-only gameplay donors such as Nightwing can use a supported native regular-cape/glide-cape
  pair while keeping their appearance and normal playset. The matching cape donor supplies the
  glide animation, and competing glide controllers are blocked before packaging.

## 0.9.0-beta.6 — 2026-08-23

Responsive windows, quest-character support, and safer native component grafts.

- Makes every top-level Batcomputer window and dialog resizable, keeps oversized windows inside one
  monitor, and adapts dense layouts for small or high-DPI displays.
- Discovers extracted `_Quest` Blueprints as visual bases, including characters under
  `Characters/Smallfig`, while still requiring an explicit playable gameplay donor.
- Indexes both Minifig and Smallfig part recipes, invalidates older Minifig-only caches so they are
  rebuilt when needed, and recognizes Smallfig-owned character materials.
- Includes the shared `M_Cape_Transparent` parent in every extraction profile so native cape
  materials have their required dependency.
- Keeps playable and cutscene extra-part grafts on their exact role-specific native recipes instead
  of reusing one context for both, including Mr. Freeze boss components.
- Adds the generated-class property and complete construction-script dependency links required for
  newly appended components to instantiate in-game, then reopens and validates each written asset.
- Clears an unrelated cloned skeletal `AnimClass` when the selected donor explicitly has none.
- Blocks synthetic regular-cape and glide-visual layouts on bases without native paired-cape wiring;
  verified paired bases retain their standing, gliding, and landing visibility behavior.
- Removes third-party build-machine debug paths from the portable single-file executable.

## 0.9.0-beta.5 — 2026-08-22

Donor selection, declarative staging, cape/glider safety, and registry reliability fixes.

- Always asks for an explicit gameplay donor after choosing a cutscene visual, recommends the
  matching playable without silently committing it, and recognizes Catwoman's nonstandard native
  archetype instead of falling back to Batman.
- Adds Edit and Remove actions for project-owned OBJ meshes and replays custom meshes, native
  grafts, removals, and materials after a base change.
- Keeps 3D-viewer custom-mesh bakes on the editor's live suit recipe, so removing an unrelated part
  no longer restores the mesh's old scale, position, or rotation during the clean rebuild.
- Retries transient generated-file sharing violations, requires complete playable/cutscene custom
  mesh grafts, and blocks packaging when a declarative stage did not finish rebuilding.
- Fixes the first declarative rebuild of a fresh suit when its generated graft-stage folder does not
  exist yet.
- Quotes the UE 5.6 writer correctly under paths such as `C:\Program Files`, validates structured
  writer counts, and keeps display punctuation out of generated Unreal identifiers.
- Blocks remote-controller gadgets on incompatible gameplay families and verifies a replacement
  glider's own visibility controller before pairing it with a separate regular cape.
- Supports the proven Batman and Batgirl glide-cape controllers; wingsuits and other glide-only
  visuals require the regular `Cape` to be removed. Older saved projects receive the same check.
- Makes Batman glide capes explicit in the Gliders browser and preserves the selected glider
  donor's controller identity and traversal animation sets.
- Builds package and mod outputs in disposable attempts, publishes only complete nonempty IoStore
  trios, and installs the certified trio transactionally with rollback on failure.

## 0.9.0-beta.4 — 2026-08-20

Display, extraction, registry-writer, and cape/glider fixes.

- Keeps Batcomputer on one monitor at mixed or unusually high display scaling and restores a
  compact, collapsed Diagnostics drawer at startup.
- Expands the standard character refresh to include `Content/Models/Gadgets`, where wingsuit,
  equipment, and related materials are stored, and keeps the previous extraction active if the
  refresh is incomplete.
- Retries the UE 5.6 writer with private build-only .NET Framework SDK metadata when an installed
  editor lacks the optional legacy SDK registration, and surfaces the first useful compiler or
  Rules error for other failures.
- Keeps cosmetic capes separate from runtime glide visuals, preserves role-specific component
  recipes, and carries the glider donor's traversal animation sets into custom suits.
- Adds release regression checks plus a 41-surface UI capture audit used to verify the portable
  build before publishing.

## 0.9.0-beta.3 — 2026-08-19

Shared registry dependency diagnostics.

- Reports the exact missing `LOTDKExpandedCoreRegistry` file paths when a mod cannot be installed.
- Explains that an already completed suit build does not need to be rebuilt after updating Loomirr's LOTDK UE4SS.
- Aligns shared registry ownership with the corrected Loomirr's LOTDK UE4SS 0.1.1 package.
- Ships a verified UE 5.6 writer module so normal mod builds no longer need Visual Studio or the
  legacy .NET Framework SDK.
- Falls back to the included source only when the configured editor has a different `BuildId`, with
  a specific dependency error instead of a generic UnrealBuildTool exit code.

## 0.9.0-beta.2 — 2026-08-18

Asset Registry writer hotfix.

- Builds the bundled UE 5.6 writer from a short per-user cache path, preventing UnrealBuildTool
  exit code 6 when Batcomputer is run from a deeply nested folder or source checkout.
- Keeps Unreal-generated writer output out of Batcomputer's portable folder and repository.
- Verifies the cached writer against its bundled source and configured UE 5.6 build before reuse.

## 0.9.0-beta.1 — 2026-08-18

First public beta.

- Build one suit or combine several suits into one mod for Loomirr's LOTDK UE4SS.
- Playable and cutscene visual donors with gameplay-donor separation.
- Part editing, game-material templates, face tools, texture cooking, equipment, gliders, and
  compatible animation data.
- Custom static-mesh OBJ import with socket-aware preview and baking.
- PawnTag, DCMD, UIMD, StringTable, gameplay-tag configuration, and Asset Registry output.
- Build checks, direct test installation, and installable ZIP creation.
- Built-in 3D viewer, base-game Red Brick colour previews for compatible characters, and notes saved
  with each mod.
- Guided first-run setup, one-click character extraction, responsive dialogs, and copyable
  diagnostics.

### Beta limits

- Windows x64 and the current supported game build only.
- Loomirr's LOTDK UE4SS is required to run generated suit mods.
- A matching `.usmap` and a local Unreal Engine 5.6 installation are required to build a mod.
- Red Brick creation is not part of this beta; the viewer only previews built-in colour options.
- Custom skeletal-mesh cooking and skeleton transfer are not supported.
- Complex equipment and controller behaviour must be tested in-game.
