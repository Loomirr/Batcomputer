# Batcomputer

A Windows tool for building custom character suits for *LEGO Batman: Legacy of the Dark Knight*
(UE 5.6).

Pick a character to build on, swap parts and materials onto it, give it a name and a menu icon, and
package the result as an IoStore trio (`.pak` / `.ucas` / `.utoc`) that drops into the game's `~mods`
folder. A companion UE4SS runtime mod (distributed separately) is what actually registers the
finished suits in-game.

> This repository is the builder tool only. It contains no game files, no extracted assets, and not
> the runtime DLL. Its bundled catalog is reference metadata only, not game content.

## What it does

- Clones a shipped character - playable, cutscene and metadata - as a clean base for your suit.
- Grafts parts from any character in the game: hair, hats, capes, torso add-ons, accessories.
- Swaps materials per mesh slot, or builds new ones from your own textures.
- Adds gadgets, including ones native to other characters - their animation sets get grafted in too.
- Overrides animations (locomotion, or whole categories) from a compatibility catalog.
- Generates the DCMD/UIMD assets that give a suit its menu name, description and icon.
- Bundles several suits into a single mod.

## Requirements

The portable author release already includes the tools it is allowed to ship:

- **retoc-oodle** for normal asset extraction and mod packing.
- **The template indexer** and the source/config for the UE 5.6 Asset Registry writer.
- **The reference game-data catalog** used by the browser.

You still point the tool at things which must stay local to your machine:

- **A .usmap mappings file** for your game build - lets the tool read and write cooked assets.
- **Your installed game** and, for compact Oodle builds, an Oodle runtime from your local UE 5.6 install.
- **Unreal Engine 5.6** when building the static Asset Registry plugin. Players do not need it.

To build from source you also need the **.NET 10 SDK**. (A published release is self-contained and
needs no .NET install.)

Nothing is hardcoded to a particular machine; every path is set in Setup.

## Build & run

```powershell
dotnet build -c Release
dotnet run --project Batcomputer.csproj
```

**The `Assets/` folder is not in this repository.** It holds the UI icons and the minifig part
silhouettes, which are compiled into the exe as embedded resources - so a build expects
`Assets/*.png` and `Assets/Parts/*.png` to be there. Copy them in before building from a fresh clone.
Without them the tool still runs, it just degrades: the sidebar falls back to text glyphs and the
character panel falls back to the slot list.

Keep the tool somewhere you can write to, such as `C:\Tools\Batcomputer`. A default portable install
keeps its settings, `Data`, `Runtime`, and `Generated` workspace beside `Batcomputer.exe`. Choose a
different workspace in Settings only when you want the large extracted game dump on another drive;
no data silently goes to AppData or `%TEMP%`.

## First run

Setup walks through the paths above, then use **Refresh game assets** to unpack the game data the
tool reads from. That step is the one to plan for:

| Profile | Size |
|---|---|
| Refresh Batman donor assets | ~50 MB |
| Refresh all character assets | ~18 GB |
| Full refresh (adds equipment, UI, gameplay, GameFeatures) | ~19 GB |

The Batman donors are enough to build a Batman-based suit if you just want to try it. Later refreshes
replace the previous dump instead of stacking up, unless you turn that off in Settings.

## Regenerating the asset catalog

`gamedata/lotdk-*.json` catalogs character families, gadgets and animation sets. A prebuilt one is
included, so this is only needed when the game updates:

```powershell
dotnet run --project Batcomputer.csproj -- --build-gamedata "<extracted>\LEGOBatmanLotDK\Content" gamedata\lotdk.json --full
```

Keep exactly one catalog in `gamedata/`. The tool loads whichever is newest by timestamp, and zip
extraction can shuffle timestamps around.

## Legal

This tool ships no game content - no textures, meshes, or cooked assets. The bundled `gamedata/`
catalog is reference metadata only: asset package paths and class names. It reads assets you have
extracted from your own copy of the game, and produces mod packages for personal use. Don't
redistribute extracted or cooked game assets.

Not affiliated with TT Games, Warner Bros. Games or the LEGO Group.

## License

MIT - see [LICENSE](LICENSE).
