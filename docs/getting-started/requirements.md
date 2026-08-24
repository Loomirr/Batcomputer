# Requirements

## To make mods

| Requirement | Why it is needed |
| --- | --- |
| Windows x64 | Batcomputer is currently a Windows desktop application. |
| A local LOTDK installation | Batcomputer reads the original IoStore containers and installs test builds into the game. |
| A matching `.usmap` | Cooked Unreal assets cannot be interpreted safely with mappings from another game build. |
| Unreal Engine 5.6 | The bundled registry-writer project uses UE 5.6 to create native Asset Registry data. |
| About 18 GB free for extraction | The full extraction includes characters, animations, localization, and supporting files. |
| Loomirr's LOTDK UE4SS 0.1.1 or newer | Generated mods depend on its plugin loading and shared `/Game/Mods` discovery configuration. |

## Bundled with Batcomputer

- The self-contained .NET desktop runtime.
- The Oodle-capable `retoc` packaging helper.
- A verified UE 5.6 Asset Registry writer module, plus its source fallback.
- Game-data indexes and runtime calibration metadata that contain paths and measurements, not game
  textures or cooked assets.

## Not bundled

- Game files or extracted content.
- A `.usmap` file.
- Unreal Engine.
- `oo2core_9_win64.dll`.
- Loomirr's LOTDK UE4SS.

Batcomputer normally uses its bundled writer module without compiling anything. If Epic replaces
the UE 5.6 editor with a different build, the source fallback requires Visual Studio 2022 with the
**Game development with C++** workload, the **.NET Framework 4.8 SDK**, and the **.NET Framework
4.8 targeting pack**.

## To use finished mods

People installing a finished suit mod do **not** need Batcomputer, Unreal Engine, mappings, or your
extracted workspace. They need:

1. A compatible game build.
2. Loomirr's LOTDK UE4SS.
3. Your finished mod release.

Next: [Install Batcomputer](install.md).
