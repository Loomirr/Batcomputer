# Workspace and files

The default workspace is portable and lives beside `Batcomputer.exe`. Settings can move the large
workspace and extraction to another drive.

```text
Batcomputer/
  Batcomputer.exe
  Batcomputer.settings.json
  Data/
    Mappings/
    Cache/
  Generated/
    GameExtracts/
    NativeSuitModProjects/
    NativeSuitModBuilds/
    NativeSuitProjects/
    Preview/
  Runtime/
  Tools/
```

## Persistent author data

Back up:

- `Batcomputer.settings.json`
- `Generated/NativeSuitModProjects`
- `Generated/NativeSuitProjects`
- Source images and OBJ files referenced by those projects
- Any mod-scoped notebook text stored in the mod project

## Rebuildable data

Game extracts, indexes, preview folders, registry-writer binaries, and staged builds can be recreated
from the current game, mappings, author projects, and source files. They may still be expensive to
regenerate, so keep them when actively developing.

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

The shared runtime additionally owns `LOTDKExpandedCoreRegistry`; third-party suit mods must not
replace it.
