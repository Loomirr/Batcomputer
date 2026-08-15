<img width="1791" height="314" alt="Header2" src="https://github.com/user-attachments/assets/41091604-e8f9-4e2f-b83e-d2abc5724b62" />

Batcomputer is a Windows suit-building tool for *LEGO Batman: Legacy of the Dark Knight*.
It assembles custom playable suits from shipped character assets, then builds and installs a native
mod release for the game.

This repository contains the authoring tool only. It does not contain game files, extracted assets,
Oodle, or the LOTDK Expanded runtime.

## What It Does

- Starts a suit from a playable donor and any cutscene or playable visual character.
- Grafts hair, hats, capes, torsos, accessories, equipment, and supported animation data.
- Clones real in-game material instances and applies generated textures per mesh slot.
- Creates native PawnTag, DCMD, UIMD, StringTable, and Asset Registry data for menu discovery.
- Builds one or more suits into a single mod release.
- Installs the pak trio, PawnTags configuration, runtime manifest, and registry plugin to the correct
  game folders.
- Creates a player-ready ZIP with the same game-relative folder layout.
- Provides a 3D assembly preview with saved per-part viewer placement and UV adjustments.

## Requirements

### Author machine

- A local installation of *LEGO Batman: Legacy of the Dark Knight*.
- A matching `.usmap` file for the current game build.
- Unreal Engine 5.6 to build the small Asset Registry writer used by native releases.
- The separate LOTDK Expanded runtime installed in the game.

### Optional compact packages

Batcomputer includes an Oodle-capable `retoc` helper. Compact Oodle packages additionally require a
user-selected `oo2core_9_win64.dll` from a local UE 5.6 installation. The runtime DLL is never
copied into a mod, release ZIP, or this repository.

### Players

Players only need the finished mod and the LOTDK Expanded runtime. They do not need Batcomputer, .NET,
Unreal Engine, mappings, or extracted game assets.

## Portable Layout

Extract a portable release somewhere writable, such as `C:\Tools\Batcomputer`.

```text
Batcomputer/
  Batcomputer.exe
  Generated/       build output, suit projects, previews, and extracts
  Data/            reusable indexes and the writer cache
  Runtime/         local runtime state
  Tools/           retoc and the Asset Registry writer source
```

The default workspace stays beside `Batcomputer.exe`. Settings can move the workspace or the large
extracted game dump to another drive.

## First Run

Setup asks for the game Paks folder, mappings, and other local paths. When UE 5.6 is configured,
setup builds and verifies the Asset Registry writer once. Later mod builds reuse that local writer
until its source or the configured UE build changes.

Setup can then run the full character extraction. It reads only the shipped, top-level Paks
containers and ignores nested `~mods` folders. The standard all-character extraction includes
character, animation, and localisation assets and needs about 18 GB of free space.

## LOTDK Expanded Runtime

The native runtime is installed separately. Batcomputer installs each mod's `mod.json` under
`ue4ss\LOTDKExpanded\Mods` and its registry plugin under
`ue4ss\LOTDKExpanded\RegistryPlugins`. The official project whose Mod ID is exactly
`LOTDKExpanded` owns the one shared `LOTDKExpandedCoreRegistry` plugin that keeps the Asset
Manager scanning `/Game/Mods`. Third-party archives contain only their own registry rows,
gameplay tags, manifest, and cooked assets; they require LOTDK Expanded and never overwrite
its shared core. Batcomputer does not modify the runtime DLL or `mods.txt`.

## Building a Suit

1. Create or open a mod.
2. Add a suit and choose a visual base plus a playable donor.
3. Use the part, material, texture, equipment, and animation tools to assemble the suit.
4. Set the native identity and review the donor-based menu icons.
5. Use the 3D viewer to inspect the assembled character. Placement and UV saves affect the viewer
   only; they do not alter the in-game character transform.
6. Validate the release, then select **Build Mod**.

Every export is a mod, including a mod containing one suit. **Build Mod** creates the release and
installs it into the configured game folders. Restart the game before testing a newly built mod.

## Visual Base and Playable Donor

The visual base controls the character's appearance. Any cutscene character can be used for this
purpose. The playable donor supplies the gameplay-facing data that a cutscene character may not
have, such as equipment and movement support.

This separation makes it possible to build visually from a wide range of characters without
guessing gameplay metadata.

## 3D Viewer

The viewer lists shipped playable and cutscene characters plus suit projects in the current
workspace. It deliberately ignores installed game mods, so content under the game's `~mods` folder
cannot change the base-game catalog or preview material resolution.

Generated 3D preview files are cleaned automatically by default. The cleanup setting can be changed
in Settings when a generated model or texture needs to be inspected.

For shipped **Playable** entries and modded suit projects, the viewer can also offer read-only
previews of the base game's Red Brick colour palettes. The selector is omitted unless the assembled
body material has a usable Colour Mask, so a mask found only on an accessory cannot enable it. It is
never shown for Cutscene entries, and it does not create, package, register, unlock, or edit Red
Bricks. The normal character refresh extracts only the one native metadata payload needed to label
these built-in colour presets.

## Building From Source

Install the .NET 10 SDK, then run:

```powershell
dotnet build -c Release
dotnet run --project Batcomputer.csproj
```

The `Assets/` directory is intentionally not tracked. Release builds embed the artwork. A source
checkout without local artwork still runs with text and fallback glyphs.

## Legal

Batcomputer ships no game content. The bundled `gamedata` catalog contains reference metadata such
as package paths and class names, not textures, meshes, or cooked assets. Use assets from a locally
owned game installation and do not redistribute extracted game content.

Batcomputer is not affiliated with TT Games, Warner Bros. Games, or the LEGO Group.

## License

[MIT](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for bundled dependency notices.
