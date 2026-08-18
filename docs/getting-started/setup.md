# First-time setup

Batcomputer opens a guided setup on first launch. You can run it again later from **Settings →
General → Run first-time setup again**.

## 1. Workspace folder

Leave this blank to keep the workspace beside `Batcomputer.exe`, or select a writable folder on a
drive with enough space. The workspace contains your projects, extracts, indexes, previews, and
builds.

## 2. Mappings (`.usmap`)

Choose mappings made for the **currently installed game build**. Batcomputer copies the selected
file into its own `Data\Mappings` folder so the portable does not depend on a temporary download
location.

!!! warning "After a game update"
    Refresh the `.usmap`, then run a fresh character extraction before rebasing or packaging suits.
    Old cooked donors can parse successfully and still be incompatible with the updated game.

## 3. Game `Content\Paks` folder

Select:

```text
...\LEGOBatmanLotDK\Content\Paks
```

The extraction pipeline reads the shipped top-level containers. It ignores nested `~mods` content
so installed mods cannot contaminate the base-game donor index.

## 4. Extracted game Content

If you already have a current complete Content dump, select it. Otherwise leave the field blank and
accept the full extraction offered at the end of setup.

The normal extraction includes character, shared animation, localization, and supporting metadata.
It uses roughly 18 GB and can take several minutes.

## 5. Unreal Engine 5.6

Select the UE 5.6 installation root. Batcomputer prepares its bundled registry-writer project and
reuses the verified result until its source or the configured engine changes.

You can skip UE while browsing and assembling, but a complete native release build needs it.

## 6. Oodle runtime

For compact packages, select `oo2core_9_win64.dll` from your own UE 5.6 installation. Batcomputer
uses it locally and never copies it into generated mods or Batcomputer releases.

## Confirm the extraction

When setup finishes, choose **Extract assets**. Wait for all three milestones:

1. IoStore extraction completes without failed assets.
2. UAssetAPI validation completes without parse errors.
3. Template and part indexes rebuild successfully.

You are then ready to [create your first suit](../guides/first-suit.md).
