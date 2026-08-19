# Changelog

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
