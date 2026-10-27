[![Batcomputer](docs/assets/brand/Header.png)](https://loomirr.github.io/Batcomputer/)

Create suits, playable characters, visual character replacers, equipment and vehicles for **LEGO Batman: Legacy of the Dark Knight** using assets from your own game.

**[Stable 1.1](https://github.com/Loomirr/Batcomputer/releases/tag/v1.1.0)** · **[Beta 5](https://github.com/Loomirr/Batcomputer/releases/tag/v1.1.1-beta.5)** · **[Documentation](https://loomirr.github.io/Batcomputer/)** · **[Report a bug](https://github.com/Loomirr/Batcomputer/issues)**

> [!WARNING]
> **AI disclosure:** Batcomputer combines hands-on development with AI-assisted coding, debugging, and documentation. I actively build, review, and test the project myself.


## Get started

1. Choose a stable or beta release, download **Batcomputer-update-win-x64.zip**, and extract the entire ZIP to a writable folder such as `C:\Tools\Batcomputer`. Version-specific `update-from-…` ZIPs are updater patches, not standalone installations.
2. Keep `Batcomputer.exe` and its `app/` folder together. The Windows x64 release includes .NET; no separate .NET installation is needed.
3. Follow [first-time setup](https://loomirr.github.io/Batcomputer/getting-started/setup/), then [build your first suit](https://loomirr.github.io/Batcomputer/guides/first-suit/).

Already using Batcomputer? Open **Updates → Check for updates**. Choose **Beta** under **Settings & recovery** to include prereleases; **Stable** excludes them. Back up editable projects before upgrading.

[1.1.1 Beta 5](https://loomirr.github.io/Batcomputer/releases/1.1.1-beta.5/) adds character attribute controls and a modernized tuning editor. It includes the earlier animation compatibility, texture queue, cape, replacer and build improvements.

## Make it your own

- **Characters and suits:** create separate playable roster entries or additional suits, combine parts and custom visuals, and rebase suits while preserving their own edits.
- **Visual replacers:** edit a supported base-game playable, NPC or quest/cutscene variant through the suit tools while retaining its original identity and gameplay.
- **Animation and voice:** browse and author clips, import animations for compatible rigs, and build character-local voice profiles.
- **Icons:** generate individual icons or all four character slots, tune cameras and lighting, and share JSON presets.
- **Abilities and equipment:** edit supported loadouts, body-matched takedown/dodge sets and held items. Beta 5 also offers 34 optional movement, grapple/glide, health, combat and Focus controls.
- **Vehicles:** import models fitted to supported native rigs, place parts and seats, edit materials and move multiple parts together. Native driving physics remain in place.
- **Build and share:** inspect build findings, follow detailed progress and export installable ZIPs with readable output paths.

Replacers, vehicle authoring and some attribute combinations remain experimental. See the [guides](https://loomirr.github.io/Batcomputer/) for supported targets and limits, and test finished mods in-game.

## What you need

For authoring: **Windows x64**, **WebView2**, a local game installation and matching `.usmap` mappings. Allow roughly **18 GB or more** for the full extraction. **Unreal Engine 5.6** is needed for Asset Registry generation when building new roster entries.

New character, suit and vehicle mods also need the game-build-compatible **[Loomirr's LOTDK UE4SS](https://github.com/Loomirr/LOTDK-UE4SS)** framework installed separately. Players need the finished mod and its listed requirements, not Batcomputer or Unreal Engine. Reading or writing Oodle-compressed data uses your own compatible Oodle runtime; it is not bundled.

**[Setup](https://loomirr.github.io/Batcomputer/getting-started/setup/)** · **[Updating and recovery](https://loomirr.github.io/Batcomputer/guides/app-updates/)** · **[Troubleshooting](https://loomirr.github.io/Batcomputer/help/troubleshooting/)**

## Source repository

| Folder | Contents |
| --- | --- |
| `src/Batcomputer/` | Desktop app, services, artwork, embedded web UI and native support |
| `Tools/` | Build, import, documentation and release helpers |
| `docs/` | User guides, release notes and reference documentation |
| `gamedata/` | Versioned reference catalogs |
| `licenses/` | Dependency notices and native build provenance |
| `.usmap/` | Published mappings endpoint; path kept stable for clients |

With the **.NET 10 SDK** installed:

```powershell
dotnet build Batcomputer.csproj
dotnet run --project Batcomputer.csproj
```

See [Tools/README.md](Tools/README.md) for native helpers and documentation builds. Keep authored projects, extracted game content and build outputs in your workspace.

## License and game content

Batcomputer is [MIT licensed](LICENSE); bundled dependencies have their own [notices](THIRD_PARTY_NOTICES.md). This repository does not include extracted game textures, meshes, cooked assets, or proprietary game runtimes. Use assets from your own game and do not redistribute extracted game content.

Not affiliated with TT Games, Warner Bros. Games or the LEGO Group.
