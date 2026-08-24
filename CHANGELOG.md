# Changelog

## In development

- Adds the exact shipped Minifig and Smallfig root-body profiles while preserving the selected
  gameplay donor's runtime setup and shared native skeleton.
- Keeps tool-created materials in a workspace library so another suit can find, import, and package
  them safely.
- Adds a modern read-only native-part inspector with a 3D preview, exact attachment recipe, and
  resolved mesh-default or component-override materials.
- Relocates saved base-template paths to the active extract by exact `/Game` package when an older
  cache folder has been replaced.
- Keeps Diagnostics in place while reading older lines instead of forcing the log back to the end.

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
