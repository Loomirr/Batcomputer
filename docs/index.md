# Batcomputer

Batcomputer is a Windows authoring tool for creating native playable suit mods for
*LEGO Batman: Legacy of the Dark Knight*. It uses assets extracted from your own game, builds the
metadata the game expects, and produces a player-ready release for Loomirr's LOTDK UE4SS.

![Batcomputer's mod workspace](assets/screenshots/home-mods.jpg){ .bc-doc-shot loading=lazy }

!!! info "Public beta"
    The current build is **0.9.0-beta.1**. The core suit workflow is working in-game, but this is the
    first broad author release. Keep source projects and report repeatable problems with diagnostics.

## Start here

<div class="grid cards" markdown>

-   <span class="bc-card-heading"><img src="assets/icons/Download.png" alt=""> <strong>Install Batcomputer</strong></span>

    ---

    Unpack the portable build and learn which author-side dependencies are required.

    [Installation guide](getting-started/install.md)

-   <span class="bc-card-heading"><img src="assets/icons/Settings.png" alt=""> <strong>Complete first-time setup</strong></span>

    ---

    Configure mappings, the game Paks folder, UE 5.6, and the first character extraction.

    [Setup guide](getting-started/setup.md)

-   <span class="bc-card-heading"><img src="assets/icons/Add.png" alt=""> <strong>Create a suit</strong></span>

    ---

    Start a mod, choose visual and gameplay donors, customize the character, and test it.

    [First-suit tutorial](guides/first-suit.md)

-   <span class="bc-card-heading"><img src="assets/icons/Warning.png" alt=""> <strong>Solve a problem</strong></span>

    ---

    Work through common setup, viewer, build, menu-discovery, and texture problems.

    [Troubleshooting](help/troubleshooting.md)

</div>

## What Batcomputer builds

- Playable and cutscene character Blueprints based on shipped donors.
- Grafted character parts, materials, textures, equipment, gliders, and supported animation data.
- Custom static-mesh attachments imported from OBJ files.
- Native PawnTag, DCMD, UIMD, StringTable, gameplay-tag configuration, and Asset Registry data.
- One or more suits grouped into a single distributable mod.
- A direct local test installation and a player-ready ZIP.

## What it does not ship

Batcomputer does not include game files, extracted assets, mappings, the proprietary Oodle runtime,
Unreal Engine, or Loomirr's LOTDK UE4SS. Authors provide those local dependencies from legitimate
installs; players only need Loomirr's LOTDK UE4SS and the finished mod.

## The normal workflow

```text
Install → Set up → Extract/index → Create mod → Add suit → Customize
        → Validate → Build/install → Cold-launch test → Create release ZIP
```

Continue with [Requirements](getting-started/requirements.md).
