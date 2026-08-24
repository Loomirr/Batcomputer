# Changelog

## 0.9.0-beta.7 — 2026-08-24

Recovery-safe base replay, exact donor recovery, and schema-safe dynamic cape-glide adapters.

- Makes base selection, reselection, and declarative part/material/removal replay transactional.
  Failed rebuilds restore the prior project and generated stages, retain a recovery backup when
  needed, and keep packaging blocked until the saved edits replay completely.
- Binds the native part-index cache to the active extracted Content root, reloads or rebuilds it for
  the current workspace, and adds **Refresh part index** to the main menu.
- Recovers complete legacy donor recipes only from an exact source-package, mesh, and role match
  before replay, so an index refresh cannot silently substitute a different component recipe.
- Migrates project-owned OBJ sources safely when a base change derives a new slot ID, using
  no-overwrite moves and targeted rollback so a failed base change cannot lose the custom mesh.
- Fixes the Batmite `_Quest` visual-base flow with a Robin playable donor instead of returning to
  the base picker, while retaining the selected quest-character appearance.
- Adds a schema-safe dynamic adapter for giving a Nightwing gameplay donor a native Batman
  regular-cape/glide-cape pair. It reapplies Nightwing's Head, Face, and body materials, keeps
  Nightwing's general behavior, and replaces only Nightwing's glide/traversal categories with the
  Batman LAS/MAS blocks required by the cape. Validation now rejects competing native and cape
  glide controllers. The corrected path passed in-game acceptance with Nightwing's appearance and
  normal playset intact and the Batman cape-glide animation active.

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
