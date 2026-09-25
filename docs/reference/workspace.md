# Workspace and files

The default workspace is portable and lives beside `Batcomputer.exe`. Settings can move the large
workspace and extraction to another drive.

```text
Batcomputer/
  Batcomputer.exe
  app/
  Batcomputer.settings.json
  Data/
    Mappings/
    Cache/
  Generated/
    GameExtracts/
    NativeSuitModProjects/
    NativeSuitModBuilds/
    NativeSuitGuiProjects/
    VehicleProjects/
    NativeSuitMaterials/
    AnimationLibrary/
    Preview/
  Runtime/
  Tools/
```

## Files to back up

The portable editor contains no LOTDKExpanded or standalone Mode Access DLL. Those game-side
features come from the separately installed LOTDK UE4SS framework. Batcomputer retains only its
configuration writer and installed-runtime checks. Its local `Runtime/` state directory is unrelated
to the old helper source folder.

Back up:

- `Batcomputer.settings.json`
- `Generated/NativeSuitModProjects`
- `Generated/NativeSuitGuiProjects` (the project JSON files **and** their per-project subfolders)
- `Generated/VehicleProjects`
- `Generated/NativeSuitMaterials` for shared generated materials and their dependencies
- `Generated/AnimationLibrary` when you have imported custom animations
- Project-owned texture sources, OBJ/FBX imports and validated mesh caches
- Original Blender files and external artwork referenced by those projects
- Notes stored in the mod project

Back up before changing mappings, replacing the extracted Content folder, or rebasing several
suits. A new Batcomputer portable can reuse these project folders without copying old cache files.

Custom-character definitions and their child suits use the suit-project storage; don't back up only
the currently selected variant. When unsure about dependencies, copy the complete workspace first.
See [Back up and move a workspace](../guides/backups-and-moving.md) for a safe migration.

## Rebuildable data

Game extracts, indexes, preview folders, registry-writer caches, and staged builds can be recreated
from the current game, mappings, projects, and source files. They may still take time to
regenerate, so keep them when actively developing.

Imported source/cooked caches and generated material libraries are **not** disposable preview data.
Do not delete everything under Generated to clear a viewer cache. Application update transactions
live separately in `.batcomputer-updates`; they back up app files, not authoring projects.

If a base or part replay fails, Batcomputer normally restores the previous generated stage. When
Diagnostics reports that a recovery backup was retained, keep that exact folder until the project
opens and passes **Check mod** again.

## Game install layout

Generated test installations use the runtime namespace consumed by Loomirr's LOTDK UE4SS:

```text
LEGOBatmanLotDK/
  Config/Tags/<ModId>Tags.ini
  Content/Paks/~mods/Expanded/<ModId>_P.pak
  Content/Paks/~mods/Expanded/<ModId>_P.ucas
  Content/Paks/~mods/Expanded/<ModId>_P.utoc
  Binaries/Win64/ue4ss/LOTDKExpanded/
    Mods/<ModId>/mod.json
    RegistryPlugins/<ModId>Registry/...
```

Loomirr's LOTDK UE4SS supplies `LOTDKExpandedCoreRegistry`; suit mods must not replace it.

See [Update or repair a suit](../guides/update-repair-suit.md) before moving an older project to a
new game dump.
